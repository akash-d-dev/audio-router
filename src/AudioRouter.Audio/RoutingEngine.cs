using NAudio.CoreAudioApi;
using NAudio.Wave;
using AudioRouter.Core;

namespace AudioRouter.Audio;

public sealed record RouterNotice(string Message, bool IsError = false, string? RemovedApplicationPath = null);
public sealed record RoutingDevices(string MicrophoneId, string CableId);

/// <summary>Owns only private capture/render streams; never changes system device defaults or volume.</summary>
public sealed class RoutingEngine : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private sealed class CaptureGroup(ApplicationSource source)
    {
        public ApplicationSource Source { get; } = source;
        public List<WasapiRecorder> Recorders { get; } = [];
        public List<PcmRingBuffer> Buffers { get; } = [];
    }
    private readonly Dictionary<string, CaptureGroup> applicationGroups = new(StringComparer.OrdinalIgnoreCase);
    private WasapiRecorder? microphone;
    private WasapiPlayer? player;
    private MMDevice? microphoneDevice, cableDevice;
    private CancellationTokenSource? monitorCancellation;
    private Task? monitorTask;
    private readonly List<Task> retiredMonitors = [];
    private ApplicationSource[] selected = [];
    private AudioMixer? mixer;
    private RoutingDevices? activeDevices;
    private bool running, stopping, disposed;
    private int sessionGeneration;
    private float micGain = 1, musicGain = .25f;
    private bool sharing = true, muted;
    private bool meteringEnabled = true;

    public bool IsRunning => Volatile.Read(ref running);
    public IReadOnlyList<ApplicationSource> CurrentApplications => Volatile.Read(ref selected);
    public RoutingDevices? CurrentDevices => Volatile.Read(ref activeDevices);
    public event EventHandler<RouterNotice>? Notice;
    public float MicrophoneGain { get => micGain; set { micGain = value; if (mixer is { } m) m.MicrophoneGain = value; } }
    public float ApplicationGain { get => musicGain; set { musicGain = value; if (mixer is { } m) m.ApplicationGain = value; } }
    public bool SharingEnabled { get => sharing; set { sharing = value; if (mixer is { } m) m.SharingEnabled = value; } }
    public bool MicrophoneMuted { get => muted; set { muted = value; if (mixer is { } m) m.MicrophoneMuted = value; } }
    public bool MeteringEnabled { get => meteringEnabled; set { meteringEnabled = value; if (mixer is { } m) m.MeteringEnabled = value; } }
    public AudioLevels TakeLevels() => mixer?.TakeLevels() ?? default;

    public Task StartAsync(string microphoneId, string cableId, IReadOnlyList<ApplicationSource>? sources = null) => Task.Run(async () =>
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running) return;
            int session = ++sessionGeneration;
            using var enumerator = new MMDeviceEnumerator();
            microphoneDevice = enumerator.GetDevice(microphoneId);
            cableDevice = enumerator.GetDevice(cableId);
            if (microphoneDevice.DataFlow != DataFlow.Capture || Devices.IsCableName(microphoneDevice.FriendlyName))
                throw new InvalidOperationException("Choose your real microphone, not CABLE Output.");
            if (cableDevice.DataFlow != DataFlow.Render || !Devices.IsCableName(cableDevice.FriendlyName))
                throw new InvalidOperationException("Choose CABLE Input as the routing output.");

            var mix = new AudioMixer { MicrophoneGain = micGain, ApplicationGain = musicGain, SharingEnabled = sharing, MicrophoneMuted = muted, MeteringEnabled = meteringEnabled };
            Volatile.Write(ref mixer, mix);
            microphone = new WasapiRecorderBuilder().WithDevice(microphoneDevice).WithFormat(mix.WaveFormat)
                .WithBufferLength(20).WithMmcssThreadPriority().Build();
            microphone.DataAvailable += (data, _, _, _) => mix.WriteMicrophone(data);
            var activeMic = microphone;
            microphone.RecordingStopped += (_, args) =>
            {
                if (args.Exception is { } error && ReferenceEquals(microphone, activeMic))
                    QueueFatal(session, "Microphone disconnected or unavailable", error);
            };
            player = new WasapiPlayerBuilder().WithDevice(cableDevice).WithLatency(30).WithMmcssThreadPriority().Build();
            var activePlayer = player;
            player.PlaybackStopped += (_, args) =>
            {
                if (args.Exception is { } error && ReferenceEquals(player, activePlayer))
                    QueueFatal(session, "Virtual cable disconnected or unavailable", error);
            };
            player.Init(mix);
            Volatile.Write(ref running, true);
            microphone.StartRecording();
            player.Play();
            Volatile.Write(ref activeDevices, new(microphoneId, cableId));
            await ChangeApplicationsCoreAsync(sources ?? []).ConfigureAwait(false);
            monitorCancellation = new CancellationTokenSource();
            monitorTask = MonitorAsync(monitorCancellation.Token, session);
        }
        catch (Exception startupError)
        {
            try { await StopCoreAsync().ConfigureAwait(false); }
            catch (Exception cleanupError) { throw new AggregateException(startupError, cleanupError); }
            throw;
        }
        finally { gate.Release(); }
    });

    public Task SelectApplicationsAsync(IReadOnlyList<ApplicationSource> sources) => Task.Run(async () =>
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (running) await ChangeApplicationsCoreAsync(sources).ConfigureAwait(false); }
        finally { gate.Release(); }
    });

    private void PublishApplications() => Volatile.Write(ref selected, applicationGroups.Values.Select(group => group.Source).ToArray());

    private async Task ChangeApplicationsCoreAsync(IReadOnlyList<ApplicationSource> sources)
    {
        var requested = ApplicationSelection.Normalize(sources);
        foreach (var group in applicationGroups.Values.ToArray())
        {
            var replacement = requested.FirstOrDefault(source => string.Equals(source.ExecutablePath, group.Source.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            if (replacement is not null && replacement.Processes.SequenceEqual(group.Source.Processes)) continue;
            try { await RemoveApplicationCoreAsync(group).ConfigureAwait(false); }
            catch (Exception ex) { Notice?.Invoke(this, new($"{group.Source.Name} stopped with a cleanup error. Other sources continue. {FriendlyError(ex)}", true)); }
        }
        foreach (var source in requested)
        {
            if (applicationGroups.ContainsKey(source.ExecutablePath)) continue;
            try { await AddApplicationCoreAsync(source).ConfigureAwait(false); }
            catch (Exception ex) { Notice?.Invoke(this, new($"Could not capture {source.Name}. Your microphone and other sources continue. {FriendlyError(ex)}", true, source.ExecutablePath)); }
        }
        PublishApplications();
    }

    private async Task AddApplicationCoreAsync(ApplicationSource source)
    {
        if (mixer is not { } mix) return;
        var targets = source.Processes.Where(process => Applications.IsSameProcess(process, source.ExecutablePath)).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("That application has closed. Refresh and check it again after reopening it.");
        var group = new CaptureGroup(source);
        applicationGroups.Add(source.ExecutablePath, group);
        try
        {
            foreach (var target in targets)
            {
                var recorder = await new WasapiRecorderBuilder().WithProcessLoopback((uint)target.Id)
                    .WithFormat(mix.WaveFormat).WithBufferLength(20).WithMmcssThreadPriority().BuildAsync().ConfigureAwait(false);
                group.Recorders.Add(recorder);
                var buffer = mix.AddApplicationSource();
                group.Buffers.Add(buffer);
                recorder.DataAvailable += (data, _, _, _) => mix.WriteApplication(buffer, data);
                recorder.RecordingStopped += (_, args) =>
                {
                    if (args.Exception is { } error) QueueApplicationFailure(group, error);
                };
                recorder.StartRecording();
            }
            PublishApplications();
        }
        catch { await RemoveApplicationCoreAsync(group).ConfigureAwait(false); throw; }
    }

    private async Task RemoveApplicationCoreAsync(CaptureGroup group)
    {
        if (!applicationGroups.TryGetValue(group.Source.ExecutablePath, out var current) || !ReferenceEquals(current, group)) return;
        applicationGroups.Remove(group.Source.ExecutablePath);
        foreach (var buffer in group.Buffers) mixer?.RemoveApplicationSource(buffer);
        PublishApplications();
        var errors = new List<Exception>();
        foreach (var recorder in group.Recorders)
        {
            try { await recorder.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException("An application stream could not be released cleanly.", errors);
    }

    private async Task ClearApplicationsCoreAsync()
    {
        var errors = new List<Exception>();
        foreach (var group in applicationGroups.Values.ToArray())
        {
            try { await RemoveApplicationCoreAsync(group).ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0) throw new AggregateException("Application streams reported cleanup errors.", errors);
    }

    private async Task MonitorAsync(CancellationToken token, int session)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    foreach (var group in applicationGroups.Values.ToArray())
                    {
                        var source = group.Source;
                        if (source.Processes.Any(process => Applications.IsSameProcess(process, source.ExecutablePath))) continue;
                        try { await RemoveApplicationCoreAsync(group).ConfigureAwait(false); }
                        catch (Exception ex) { Notice?.Invoke(this, new($"Cleanup error for {source.Name}: {FriendlyError(ex)}", true)); }
                        Notice?.Invoke(this, new($"{source.Name} closed. Your microphone and other selected apps continue. Refresh and check it again after reopening it.", RemovedApplicationPath: source.ExecutablePath));
                    }
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { QueueFatal(session, "Audio monitoring failed", ex); }
    }

    private void QueueApplicationFailure(CaptureGroup group, Exception error) => _ = Task.Run(async () =>
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!running || !applicationGroups.TryGetValue(group.Source.ExecutablePath, out var current) || !ReferenceEquals(current, group)) return;
            try { await RemoveApplicationCoreAsync(group).ConfigureAwait(false); }
            catch (Exception cleanup) { error = new AggregateException(error, cleanup); }
            Notice?.Invoke(this, new($"{group.Source.Name} capture stopped. Your microphone and other apps continue. {FriendlyError(error)}", true, group.Source.ExecutablePath));
        }
        finally { gate.Release(); }
    });

    private void QueueFatal(int session, string message, Exception error) => _ = Task.Run(async () =>
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!running || session != sessionGeneration) return;
            try { await StopCoreAsync().ConfigureAwait(false); }
            catch (Exception cleanupError) { error = new AggregateException(error, cleanupError); }
            Notice?.Invoke(this, new($"{message}. Routing stopped. {FriendlyError(error)}", true));
        }
        finally { gate.Release(); }
    });

    public Task StopAsync() => Task.Run(async () =>
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    });

    private async Task StopCoreAsync()
    {
        if (stopping) return;
        stopping = true;
        try
        {
            sessionGeneration++;
            Volatile.Write(ref running, false);
            Volatile.Write(ref activeDevices, null);
            monitorCancellation?.Cancel();
            if (monitorCancellation is { } cancelled)
            {
                retiredMonitors.RemoveAll(task => task.IsCompleted);
                retiredMonitors.Add(ReapMonitorAsync(monitorTask, cancelled));
                monitorCancellation = null; monitorTask = null;
            }
            var errors = new List<Exception>();
            async Task ReleaseAsync(Func<ValueTask> release)
            {
                try { await release().ConfigureAwait(false); }
                catch (Exception ex) { errors.Add(ex); }
            }
            try { await ClearApplicationsCoreAsync().ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex); }
            var oldPlayer = player;
            player = null;
            if (oldPlayer is not null) await ReleaseAsync(oldPlayer.DisposeAsync).ConfigureAwait(false);
            var oldMic = microphone;
            microphone = null;
            if (oldMic is not null) await ReleaseAsync(oldMic.DisposeAsync).ConfigureAwait(false);
            try { microphoneDevice?.Dispose(); } catch (Exception ex) { errors.Add(ex); } finally { microphoneDevice = null; }
            try { cableDevice?.Dispose(); } catch (Exception ex) { errors.Add(ex); } finally { cableDevice = null; }
            Volatile.Write(ref mixer, null);
            if (errors.Count > 0) throw new AggregateException("Some audio streams reported errors while stopping.", errors);
        }
        finally { stopping = false; }
    }

    public static string FriendlyError(Exception ex) => ex.HResult switch
    {
        unchecked((int)0x80070005) => "Allow desktop apps to access your microphone in Windows Privacy settings.",
        unchecked((int)0x88890004) => "An audio device is no longer connected. Refresh devices and try again.",
        unchecked((int)0x8889000A) => "The device is in exclusive use. Close the application holding it and try again.",
        _ => ex.Message
    };

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        Task[] monitors;
        try
        {
            disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { monitors = retiredMonitors.ToArray(); gate.Release(); }
        await Task.WhenAll(monitors).ConfigureAwait(false);
        // Pending fault handlers can still finish through the gate; don't dispose it underneath them.
    }

    private static async Task ReapMonitorAsync(Task? monitor, CancellationTokenSource cancellation)
    {
        try { if (monitor is not null) await monitor.ConfigureAwait(false); }
        finally { cancellation.Dispose(); }
    }
}
