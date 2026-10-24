using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioRouter.Audio;
using AudioRouter.Core;

namespace AudioRouter.App;

public partial class MainWindow : Window
{
    private readonly RoutingEngine router = new();
    private readonly Applications discovery = new();
    private readonly ObservableCollection<ApplicationChoice> applications = [];
    private readonly DispatcherTimer meterTimer = new() { Interval = TimeSpan.FromMilliseconds(125) };
    private readonly TrayIcon tray;
    private readonly RouterSettings saved = SettingsStore.Load();
    private bool ready, busy, refreshing, refreshInFlight, closing, cleanupComplete, initialRefresh = true;
    private bool exitRequested, loaded;
    private int discoveryRefreshCount;
    private readonly HashSet<string> preferredApplications;
    private int noticeVersion;
    public string? SmokeTestOutput { get; set; }
    public bool RunDeviceSwitchSmokeTest { get; set; }
    private Task deviceSwitchTask = Task.CompletedTask;
    private Task applicationSelectionTask = Task.CompletedTask;

    public MainWindow()
    {
        InitializeComponent();
        SourceList.ItemsSource = applications;
        CollectionViewSource.GetDefaultView(applications).Filter = MatchesSearch;
        MicrophoneSlider.Value = saved.MicrophoneGain * 100;
        MusicSlider.Value = saved.ApplicationGain * 100;
        preferredApplications = new(saved.Validated().ApplicationPaths!, StringComparer.OrdinalIgnoreCase);
        router.MicrophoneGain = saved.MicrophoneGain;
        router.ApplicationGain = saved.ApplicationGain;
        LiveMetersCheck.IsChecked = saved.ShowLiveMeters;
        tray = new TrayIcon(RestoreFromTray, RequestExit);
        router.Notice += (_, notice) => Dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            noticeVersion++;
            SetStatus(notice.Message, notice.IsError);
            if (notice.RemovedApplicationPath is { } removedPath)
            {
                bool previous = refreshing;
                refreshing = true;
                var choice = applications.FirstOrDefault(app => SamePath(app.ExecutablePath, removedPath));
                if (choice is not null) choice.IsSelected = false;
                refreshing = previous;
                RememberSelectedApplications();
                SaveSettings();
                UpdateApplicationCount();
            }
            UpdateControls();
        });
        meterTimer.Tick += (_, _) => UpdateMeters();
        ready = true;
        UpdateGainLabels();
        UpdateMeterActivity();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        await RefreshAsync();
        if (closing) return;
        UpdateMeterActivity();
        if (SmokeTestOutput is { } output)
        {
            try
            {
                // The regular smoke check does not open audio streams or persist settings.
                SearchBox.Text = "Brave";
                bool searchWorks = applications.Where(MatchesSearch).All(a => a.Name.Contains("Brave", StringComparison.OrdinalIgnoreCase)
                    || a.ExecutablePath.Contains("Brave", StringComparison.OrdinalIgnoreCase));
                SearchBox.Text = "";
                float previousMusic = router.ApplicationGain;
                MusicSlider.Value = 42;
                bool gainWorks = Math.Abs(router.ApplicationGain - .42f) < .001 && MusicGainLabel.Text == "42%";
                MusicSlider.Value = 200;
                bool musicBoostWorks = Math.Abs(router.ApplicationGain - 2f) < .001 && MusicGainLabel.Text == "200%";
                MusicSlider.Value = previousMusic * 100;
                ShareCheck.IsChecked = false;
                bool pauseWorks = !router.SharingEnabled;
                ShareCheck.IsChecked = true;
                MuteMicCheck.IsChecked = true;
                bool muteWorks = router.MicrophoneMuted;
                MuteMicCheck.IsChecked = false;
                if (!searchWorks || !gainWorks || !musicBoostWorks || !pauseWorks || !muteWorks) throw new InvalidOperationException("UI control smoke check failed.");
                object lightweightChecks = await VerifyLightweightUiAsync();
                object multipleSelectionChecks = await VerifyMultipleSelectionUiAsync();
                object? deviceSwitchChecks = RunDeviceSwitchSmokeTest ? await VerifyDeviceSwitchingAsync() : null;
                await Task.Delay(400);
                UpdateLayout();
                var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                image.Render(this);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using (var stream = File.Create(output)) encoder.Save(stream);
                var setup = CreateSetupWindow();
                setup.Show();
                setup.UpdateLayout();
                bool guideShowsSelectedCable = setup.TargetInputName.Text == (CableBox.SelectedItem as AudioDevice)?.RecordingEndpointName
                    || (CableBox.SelectedItem is null && setup.TargetInputName.Text == "CABLE Output (VB-Audio Virtual Cable)");
                bool volumeMixerShortcutAvailable = setup.VolumeMixerButton.IsEnabled;
                if (!guideShowsSelectedCable || !volumeMixerShortcutAvailable) throw new InvalidOperationException("Generic setup guide smoke check failed.");
                File.WriteAllText(Path.ChangeExtension(output, ".json"), JsonSerializer.Serialize(new
                {
                    Applications = applications.Select(a => a.Name), Microphones = MicrophoneBox.Items.Count,
                    Cables = CableBox.Items.Count, CanStart = StartButton.IsEnabled,
                    CableGuidanceVisible = CableMissingPanel.Visibility == Visibility.Visible,
                    State = StateLabel.Text, Status = StatusText.Text,
                    SearchWorks = searchWorks, GainWorks = gainWorks, MusicBoostWorks = musicBoostWorks, PauseWorks = pauseWorks, MuteWorks = muteWorks,
                    DeviceSwitchChecks = deviceSwitchChecks, LightweightChecks = lightweightChecks, MultipleSelectionChecks = multipleSelectionChecks,
                    GuideShowsSelectedCable = guideShowsSelectedCable, VolumeMixerShortcutAvailable = volumeMixerShortcutAvailable
                }, new JsonSerializerOptions { WriteIndented = true }));
                var helpImage = new RenderTargetBitmap((int)setup.ActualWidth, (int)setup.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                helpImage.Render(setup);
                var helpEncoder = new PngBitmapEncoder();
                helpEncoder.Frames.Add(BitmapFrame.Create(helpImage));
                using (var stream = File.Create(Path.ChangeExtension(output, ".setup.png"))) helpEncoder.Save(stream);
                setup.GuideScroll.ScrollToVerticalOffset(600);
                setup.UpdateLayout();
                var routingImage = new RenderTargetBitmap((int)setup.ActualWidth, (int)setup.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                routingImage.Render(setup);
                var routingEncoder = new PngBitmapEncoder();
                routingEncoder.Frames.Add(BitmapFrame.Create(routingImage));
                using (var stream = File.Create(Path.ChangeExtension(output, ".setup-routing.png"))) routingEncoder.Save(stream);
                setup.Close();
            }
            catch (Exception ex) { App.WriteError(ex); Environment.ExitCode = 1; }
            RequestExit();
        }
    }

    private async Task RefreshAsync()
    {
        if (refreshInFlight || closing || busy) return;
        discoveryRefreshCount++;
        refreshInFlight = true;
        UpdateControls();
        try
        {
            var appsTask = Task.Run(discovery.Discover);
            var devicesTask = Task.Run(Devices.GetInventory);
            await Task.WhenAll(appsTask, devicesTask);
            if (closing) return;
            if (MicrophoneBox.IsDropDownOpen || CableBox.IsDropDownOpen) return;
            refreshing = true;
            var inventory = devicesTask.Result;
            bool previouslyMissingCable = CableBox.Items.Count == 0;
            string? microphoneId = (MicrophoneBox.SelectedItem as AudioDevice)?.Id ?? saved.MicrophoneId;
            string? cableId = (CableBox.SelectedItem as AudioDevice)?.Id ?? saved.CableId;
            // Preserve open selections during an explicit refresh.
            if (!SameDevices(MicrophoneBox, inventory.Microphones))
            {
                MicrophoneBox.ItemsSource = inventory.Microphones;
                MicrophoneBox.SelectedItem = inventory.Microphones.FirstOrDefault(x => x.Id == microphoneId)
                    ?? inventory.Microphones.FirstOrDefault();
            }
            if (!SameDevices(CableBox, inventory.CableInputs))
            {
                CableBox.ItemsSource = inventory.CableInputs;
                CableBox.SelectedItem = Devices.PickCableInput(inventory.CableInputs, cableId);
            }
            var found = appsTask.Result;
            for (int i = applications.Count - 1; i >= 0; i--)
                if (!found.Any(a => SamePath(a.ExecutablePath, applications[i].ExecutablePath)))
                {
                    applications[i].PropertyChanged -= SourceChoice_Changed;
                    applications.RemoveAt(i);
                }
            foreach (var app in found.OrderBy(a => a.Name))
            {
                var existing = applications.FirstOrDefault(a => SamePath(a.ExecutablePath, app.ExecutablePath));
                if (existing is null)
                {
                    var choice = new ApplicationChoice(app, initialRefresh && preferredApplications.Contains(app.ExecutablePath));
                    choice.PropertyChanged += SourceChoice_Changed;
                    applications.Add(choice);
                }
                else existing.UpdateSource(app);
            }
            RememberSelectedApplications();
            UpdateApplicationCount();
            if (router.IsRunning) await router.SelectApplicationsAsync(CheckedApplications());
            if (initialRefresh)
            {
                if (inventory.CableInputs.Count == 0) SetStatus("Install VB-CABLE once, then refresh. Your current audio setup stays in place.");
                else if (inventory.Microphones.Count == 0) SetStatus("Connect a microphone, then refresh.", true);
                else SetStatus("Ready. Check one or more apps and click Start routing. You can also route just your microphone.");
            }
            else if (previouslyMissingCable && inventory.CableInputs.Count > 0 && !router.IsRunning)
                SetStatus("VB-CABLE is ready. Choose the matching cable output as your destination app’s microphone, then start routing.");
            initialRefresh = false;
        }
        catch (Exception ex) { App.WriteError(ex); SetStatus("Could not refresh audio devices or applications. " + RoutingEngine.FriendlyError(ex), true); }
        finally { refreshing = false; refreshInFlight = false; RefreshButton.IsEnabled = !busy; UpdateControls(); }
    }

    private static bool SameDevices(ComboBox box, IReadOnlyList<AudioDevice> devices) =>
        box.Items.Cast<AudioDevice>().SequenceEqual(devices);
    private static bool SamePath(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private ApplicationSource[] CheckedApplications() => applications.Where(app => app.IsSelected).Select(app => app.Source).ToArray();

    private void RememberSelectedApplications()
    {
        preferredApplications.Clear();
        preferredApplications.UnionWith(applications.Where(app => app.IsSelected).Select(app => app.ExecutablePath));
    }

    private async void SourceChoice_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ApplicationChoice.IsSelected) || !ready || refreshing || closing || busy) return;
        RememberSelectedApplications();
        UpdateApplicationCount();
        SaveSettings();
        if (!router.IsRunning) return;
        applicationSelectionTask = ApplySelectedApplicationsAsync();
        await applicationSelectionTask;
    }

    private async Task ApplySelectedApplicationsAsync()
    {
        busy = true;
        int previousNotice = noticeVersion;
        UpdateControls();
        try
        {
            await router.SelectApplicationsAsync(CheckedApplications());
            if (previousNotice == noticeVersion) SetStatus(RoutingSummary());
            SaveSettings();
        }
        catch (Exception ex)
        {
            App.WriteError(ex);
            SetStatus("Could not update app selection. " + RoutingEngine.FriendlyError(ex), true);
        }
        finally { busy = false; UpdateControls(); }
    }

    private string RoutingSummary()
    {
        var current = router.CurrentApplications;
        if (current.Count == 0) return "Microphone routing is active. Check applications to add their audio.";
        if (!router.SharingEnabled) return $"Application sharing is paused. {current.Count} apps selected; the microphone route stays active.";
        string names = current.Count <= 2 ? string.Join(" + ", current.Select(app => app.Name)) : $"{current.Count} applications";
        return $"Sharing {names} and your microphone.";
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await ToggleRoutingAsync();

    private async Task ToggleRoutingAsync()
    {
        if (busy || closing) return;
        busy = true;
        int previousNotice = noticeVersion;
        UpdateControls();
        try
        {
            if (router.IsRunning)
            {
                await router.StopAsync();
                SetStatus("Routing stopped. The virtual microphone is silent. Select your real microphone in the destination app to talk without Audio Router.");
            }
            else
            {
                if (MicrophoneBox.SelectedItem is not AudioDevice mic || CableBox.SelectedItem is not AudioDevice cable) return;
                await router.StartAsync(mic.Id, cable.Id, CheckedApplications());
                if (previousNotice == noticeVersion)
                    SetStatus(RoutingSummary());
                SaveSettings();
            }
        }
        catch (Exception ex) { App.WriteError(ex); SetStatus(RoutingEngine.FriendlyError(ex), true); }
        finally { busy = false; UpdateControls(); }
    }

    private void UpdateControls()
    {
        if (!ready) return;
        bool active = router.IsRunning;
        MicrophoneBox.IsEnabled = CableBox.IsEnabled = !busy && !refreshInFlight && !closing;
        MicrophoneBox.ToolTip = "Choose your microphone. Changing it while live briefly restarts routing.";
        CableBox.ToolTip = "Choose the virtual cable. Changing it while live briefly restarts routing.";
        if (CableBox.SelectedItem is AudioDevice cable)
            TargetMicrophoneHint.Text = $"Set that app’s microphone / input to:\n{cable.RecordingEndpointName}";
        SourceList.IsEnabled = !busy && !refreshInFlight && !closing;
        StartButton.IsEnabled = !busy && !refreshInFlight && !refreshing && !closing && (active || (MicrophoneBox.SelectedItem is AudioDevice && CableBox.SelectedItem is AudioDevice));
        StartButton.Content = busy ? "Please wait…" : active ? "Stop routing" : "Start routing";
        StateLabel.Text = active ? "ROUTING LIVE" : "STOPPED";
        StateLabel.Foreground = Brush(active ? "#54E1BD" : "#A8B7C6");
        CableMissingPanel.Visibility = CableBox.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TargetInstructions.Visibility = CableBox.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = !busy && !refreshInFlight && !closing;
        StartButton.ToolTip = CableBox.Items.Count == 0 ? "Install VB-CABLE, then refresh devices." : active ? "Stop music and microphone routing." : "Start the virtual microphone.";
        tray.SetRoutingState(active);
        UpdateMeterActivity();
    }

    private void Gain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        router.MicrophoneGain = (float)MicrophoneSlider.Value / 100;
        router.ApplicationGain = (float)MusicSlider.Value / 100;
        UpdateGainLabels();
    }
    private void UpdateGainLabels()
    {
        MicGainLabel.Text = $"{MicrophoneSlider.Value:0}%";
        MusicGainLabel.Text = $"{MusicSlider.Value:0}%";
    }
    private void Mute_Changed(object sender, RoutedEventArgs e) { if (ready) router.MicrophoneMuted = MuteMicCheck.IsChecked == true; }
    private void Share_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        router.SharingEnabled = ShareCheck.IsChecked == true;
        if (router.IsRunning) SetStatus(router.SharingEnabled
            ? "Application audio sharing is on. Your microphone continues."
            : "Application audio sharing is paused. Your microphone continues.");
    }
    private async void Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || refreshing || closing || busy) return;
        if (!router.IsRunning)
        {
            UpdateControls();
            SaveSettings();
            return;
        }
        if (MicrophoneBox.SelectedItem is not AudioDevice mic || CableBox.SelectedItem is not AudioDevice cable) return;
        if (router.CurrentDevices is { } current && current.MicrophoneId == mic.Id && current.CableId == cable.Id) return;
        deviceSwitchTask = SwitchDevicesAsync(mic, cable);
        await deviceSwitchTask;
    }

    private async Task SwitchDevicesAsync(AudioDevice microphone, AudioDevice cable)
    {
        // Preserve every captured source even when search hides its checkbox.
        var sources = router.CurrentApplications.ToArray();
        int previousNotice = noticeVersion;
        busy = true;
        SetStatus("Switching audio devices… routing will resume automatically.");
        UpdateControls();
        try
        {
            await router.StopAsync();
            if (closing) return;
            await router.StartAsync(microphone.Id, cable.Id, sources);
            if (!closing && previousNotice == noticeVersion)
                SetStatus("Devices updated. " + RoutingSummary());
            SaveSettings();
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                App.WriteError(ex);
                SetStatus("Could not switch devices. Routing stopped. Choose an available device and click Start routing. " + RoutingEngine.FriendlyError(ex), true);
            }
        }
        finally { busy = false; UpdateControls(); }
    }

    private async Task<object> VerifyDeviceSwitchingAsync()
    {
        var microphones = MicrophoneBox.Items.Cast<AudioDevice>().ToArray();
        var cables = CableBox.Items.Cast<AudioDevice>().ToArray();
        if (microphones.Length < 2 || cables.Length < 2) throw new InvalidOperationException("The live device check needs two microphones and two cable inputs.");
        // Open real streams with microphone muted and music paused; never save test preferences.
        MuteMicCheck.IsChecked = true;
        ShareCheck.IsChecked = false;
        MicrophoneSlider.Value = 70;
        MusicSlider.Value = 200;
        MicrophoneBox.SelectedItem = microphones[0];
        CableBox.SelectedItem = Devices.PickCableInput(cables, null);
        foreach (var choice in applications.Where(app => !string.Equals(Path.GetFileName(app.ExecutablePath), "AudioRouter.exe", StringComparison.OrdinalIgnoreCase)).Take(2)) choice.IsSelected = true;
        await ToggleRoutingAsync();
        if (!router.IsRunning || !MicrophoneBox.IsEnabled || !CableBox.IsEnabled)
            throw new InvalidOperationException("Device selectors are disabled while live.");
        var chosen = applications.Where(app => app.IsSelected).Take(2).ToArray();
        if (chosen.Length < 2 || router.CurrentApplications.Count < 2) throw new InvalidOperationException("Two checked apps were not captured.");
        chosen[0].IsSelected = false;
        await applicationSelectionTask;
        bool removedOneWhileLive = router.IsRunning && router.CurrentApplications.All(app => !SamePath(app.ExecutablePath, chosen[0].ExecutablePath))
            && router.CurrentApplications.Any(app => SamePath(app.ExecutablePath, chosen[1].ExecutablePath));
        chosen[0].IsSelected = true;
        await applicationSelectionTask;
        bool addedOneWhileLive = router.IsRunning && chosen.All(choice => router.CurrentApplications.Any(app => SamePath(app.ExecutablePath, choice.ExecutablePath)));
        if (!removedOneWhileLive || !addedOneWhileLive) throw new InvalidOperationException("Changing checkboxes interrupted another live source.");
        var routeBeforeHide = router.CurrentDevices;
        LiveMetersCheck.IsChecked = true;
        if (!meterTimer.IsEnabled || !router.MeteringEnabled) throw new InvalidOperationException("Enabled live meters did not start.");
        Close();
        await Task.Delay(200);
        bool trayKeptRouting = !IsVisible && tray.IsVisible && router.IsRunning && router.CurrentDevices == routeBeforeHide
            && !meterTimer.IsEnabled && !router.MeteringEnabled;
        RestoreFromTray();
        if (!trayKeptRouting || !meterTimer.IsEnabled) throw new InvalidOperationException("Tray hiding interrupted the route or kept metering active.");
        LiveMetersCheck.IsChecked = false;
        MicrophoneBox.IsDropDownOpen = true;
        bool microphoneDropdownOpened = MicrophoneBox.IsDropDownOpen;
        MicrophoneBox.IsDropDownOpen = false;
        CableBox.IsDropDownOpen = true;
        bool cableDropdownOpened = CableBox.IsDropDownOpen;
        CableBox.IsDropDownOpen = false;
        if (!microphoneDropdownOpened || !cableDropdownOpened)
            throw new InvalidOperationException("Device dropdowns cannot open while live.");
        var originalSources = router.CurrentApplications.Select(app => app.ExecutablePath).Order().ToArray();
        var otherCable = cables.First(x => x.Id != router.CurrentDevices!.CableId);
        CableBox.SelectedItem = otherCable;
        await deviceSwitchTask;
        bool cableSwitched = router.IsRunning && router.CurrentDevices?.CableId == otherCable.Id;
        if (!cableSwitched) throw new InvalidOperationException("Could not switch the live output: " + StatusText.Text);
        var otherMic = microphones.First(x => x.Id != router.CurrentDevices!.MicrophoneId);
        MicrophoneBox.SelectedItem = otherMic;
        await deviceSwitchTask;
        bool microphoneSwitched = router.IsRunning && router.CurrentDevices?.MicrophoneId == otherMic.Id;
        bool statePreserved = !router.SharingEnabled && router.MicrophoneMuted
            && Math.Abs(router.MicrophoneGain - .7f) < .001 && Math.Abs(router.ApplicationGain - 2f) < .001
            && router.CurrentApplications.Select(app => app.ExecutablePath).Order().SequenceEqual(originalSources);
        if (!cableSwitched || !microphoneSwitched || !statePreserved) throw new InvalidOperationException("Live device switching did not preserve routing state.");

        refreshing = true;
        var missing = new AudioDevice("missing-microphone-for-smoke-test", "Disconnected microphone", false, false);
        MicrophoneBox.ItemsSource = microphones.Append(missing).ToArray();
        MicrophoneBox.SelectedItem = otherMic;
        refreshing = false;
        MicrophoneBox.SelectedItem = missing;
        await deviceSwitchTask;
        bool failedSafely = !router.IsRunning && MicrophoneBox.IsEnabled && CableBox.IsEnabled;
        MicrophoneBox.SelectedItem = microphones[0];
        await ToggleRoutingAsync();
        bool recovered = router.IsRunning;
        if (!failedSafely || !recovered) throw new InvalidOperationException("Failed device switch did not stop safely or allow recovery.");
        return new { CableSwitched = cableSwitched, MicrophoneSwitched = microphoneSwitched,
            StatePreserved = statePreserved, FailedSwitchStoppedSafely = failedSafely, Recovered = recovered,
            MicrophoneDropdownOpened = microphoneDropdownOpened, CableDropdownOpened = cableDropdownOpened,
            SelectorsEnabledWhileLive = true, RemovedOneWhileLive = removedOneWhileLive, AddedOneWhileLive = addedOneWhileLive,
            TrayKeptRouting = trayKeptRouting, ActiveBeforeExplicitExit = router.IsRunning };
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready) return;
        CollectionViewSource.GetDefaultView(applications).Refresh();
        UpdateApplicationCount();
    }
    private bool MatchesSearch(object item) => item is ApplicationChoice source &&
        (source.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase)
         || Path.GetFileName(source.ExecutablePath).Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));
    private void UpdateApplicationCount()
    {
        int shown = applications.Count(a => MatchesSearch(a));
        ApplicationCount.Text = $"{shown} apps · {applications.Count(app => app.IsSelected)} checked · manual refresh";
        EmptyListLabel.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateMeters()
    {
        var levels = router.TakeLevels();
        MicMeter.Value = Meter(levels.Microphone);
        MusicMeter.Value = Meter(levels.Application);
        OutputMeter.Value = Meter(levels.Output);
        OutputLevelLabel.Text = levels.Output < .00001 ? "Silent" : $"{20 * Math.Log10(levels.Output):0} dB";
    }
    private void UpdateMeterActivity()
    {
        if (!ready) return;
        bool show = LiveMetersCheck.IsChecked == true;
        MicMeter.Visibility = MusicMeter.Visibility = OutputMetersPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        bool run = show && IsVisible && WindowState != WindowState.Minimized && router.IsRunning && !closing;
        router.MeteringEnabled = run;
        if (run) meterTimer.Start();
        else
        {
            meterTimer.Stop();
            MicMeter.Value = MusicMeter.Value = OutputMeter.Value = 0;
            OutputLevelLabel.Text = "Silent";
        }
    }
    private void LiveMeters_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        UpdateMeterActivity();
        SaveSettings();
    }
    private void Window_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateMeterActivity();
    private void Window_StateChanged(object? sender, EventArgs e) => UpdateMeterActivity();

    private async Task<object> VerifyLightweightUiAsync()
    {
        LiveMetersCheck.IsChecked = false;
        bool metersDisabled = !meterTimer.IsEnabled && !router.MeteringEnabled
            && MusicMeter.Visibility == Visibility.Collapsed && MicMeter.Visibility == Visibility.Collapsed
            && OutputMetersPanel.Visibility == Visibility.Collapsed;
        LiveMetersCheck.IsChecked = true;
        bool metersShown = MusicMeter.Visibility == Visibility.Visible && OutputMetersPanel.Visibility == Visibility.Visible;
        Close();
        bool hiddenToTray = !IsVisible && tray.IsVisible && !closing && !meterTimer.IsEnabled;
        RestoreFromTray();
        bool restored = IsVisible;
        LiveMetersCheck.IsChecked = saved.ShowLiveMeters;
        int before = discoveryRefreshCount;
        await Task.Delay(3200);
        bool noAutomaticRefresh = discoveryRefreshCount == before;
        await RefreshAsync();
        bool manualRefreshWorks = discoveryRefreshCount == before + 1;
        UpdateLayout();
        double badgeCenter = StateBadge.TranslatePoint(new Point(0, StateBadge.ActualHeight / 2), this).Y;
        double buttonCenter = SetupGuideButton.TranslatePoint(new Point(0, SetupGuideButton.ActualHeight / 2), this).Y;
        bool indicatorCentered = Math.Abs(badgeCenter - buttonCenter) < 1;
        if (!metersDisabled || !metersShown || !hiddenToTray || !restored || !noAutomaticRefresh || !manualRefreshWorks || !indicatorCentered)
            throw new InvalidOperationException("Tray, meters, manual refresh, or indicator alignment check failed.");
        return new { MetersDisabled = metersDisabled, MetersShown = metersShown, HiddenToTray = hiddenToTray,
            Restored = restored, NoAutomaticRefresh = noAutomaticRefresh, ManualRefreshWorks = manualRefreshWorks,
            IndicatorCentered = indicatorCentered };
    }

    private async Task<object> VerifyMultipleSelectionUiAsync()
    {
        var choices = applications.Take(2).ToArray();
        if (choices.Length < 2) throw new InvalidOperationException("The checkbox UI check needs two running applications.");
        var previous = applications.Where(app => app.IsSelected).ToArray();
        foreach (var app in applications) app.IsSelected = false;
        foreach (var app in choices) app.IsSelected = true;
        bool multipleChecked = CheckedApplications().Length == 2;
        SearchBox.Text = "does-not-match-any-app";
        bool searchPreservedSelection = CheckedApplications().Length == 2;
        SearchBox.Text = "";
        await RefreshAsync();
        bool refreshPreservedSelection = choices.All(choice => applications.Any(app => SamePath(app.ExecutablePath, choice.ExecutablePath) && app.IsSelected));
        choices[0].IsSelected = false;
        bool independentUncheck = CheckedApplications().Length == 1 && choices[1].IsSelected;
        foreach (var app in applications) app.IsSelected = false;
        foreach (var app in previous) app.IsSelected = true;
        if (!multipleChecked || !searchPreservedSelection || !refreshPreservedSelection || !independentUncheck)
            throw new InvalidOperationException("Multiple application checkbox check failed.");
        return new { MultipleChecked = multipleChecked, SearchPreservedSelection = searchPreservedSelection,
            RefreshPreservedSelection = refreshPreservedSelection, IndependentUncheck = independentUncheck };
    }

    public void RestoreFromTray()
    {
        if (closing) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        UpdateMeterActivity();
    }
    public void RequestExit()
    {
        exitRequested = true;
        Close();
    }
    internal void DisposeTrayIcon() => tray.Dispose();
    private static double Meter(float value) => value <= .001 ? 0 : Math.Clamp((20 * Math.Log10(value) + 60) / 60 * 100, 0, 100);
    private void SetStatus(string text, bool error = false) { StatusText.Text = text; StatusText.Foreground = Brush(error ? "#FFCF8A" : "#9CAEBF"); }
    private static SolidColorBrush Brush(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    private SetupWindow CreateSetupWindow() => new((CableBox.SelectedItem as AudioDevice)?.RecordingEndpointName
        ?? "CABLE Output (VB-Audio Virtual Cable)") { Owner = this };
    private void Setup_Click(object sender, RoutedEventArgs e) => CreateSetupWindow().ShowDialog();
    private void DownloadCable_Click(object sender, RoutedEventArgs e) => OpenLink("https://vb-audio.com/Cable/");
    internal static void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { App.WriteError(ex); MessageBox.Show("Open this address in your browser:\n" + url, "Audio Router"); }
    }

    private void SaveSettings()
    {
        if (SmokeTestOutput is not null) return;
        try
        {
            SettingsStore.Save(new()
            {
                MicrophoneId = (MicrophoneBox.SelectedItem as AudioDevice)?.Id ?? saved.MicrophoneId,
                CableId = (CableBox.SelectedItem as AudioDevice)?.Id ?? saved.CableId,
                ApplicationPaths = preferredApplications.ToArray(), MicrophoneGain = (float)MicrophoneSlider.Value / 100,
                ApplicationGain = (float)MusicSlider.Value / 100, ShowLiveMeters = LiveMetersCheck.IsChecked == true
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.WriteError(ex); }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (cleanupComplete) return;
        e.Cancel = true;
        if (!exitRequested)
        {
            SaveSettings();
            Hide();
            UpdateMeterActivity();
            return;
        }
        if (closing) return;
        closing = true;
        meterTimer.Stop();
        SaveSettings();
        UpdateControls();
        try { await router.DisposeAsync(); }
        catch (Exception ex) { App.WriteError(ex); }
        tray.Dispose();
        cleanupComplete = true;
        // Disposal can complete synchronously when stopped. Close only after this Closing event unwinds.
        _ = Dispatcher.BeginInvoke(() => Application.Current.Shutdown(Environment.ExitCode));
    }
}
