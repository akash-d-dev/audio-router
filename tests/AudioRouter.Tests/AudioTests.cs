using System.Runtime.InteropServices;
using AudioRouter.Audio;
using AudioRouter.Core;
using Xunit;

namespace AudioRouter.Tests;

public sealed class AudioTests
{
    private static byte[] Bytes(params float[] data) => MemoryMarshal.AsBytes(data.AsSpan()).ToArray();
    private static float[] Render(AudioMixer mixer, int frames = 480)
    {
        byte[] output = new byte[frames * 8];
        Assert.Equal(output.Length, mixer.Read(output));
        return MemoryMarshal.Cast<byte, float>(output).ToArray();
    }
    private static byte[] Signal(float sample, int frames = 480) => Bytes(Enumerable.Repeat(sample, frames * 2).ToArray());

    [Fact]
    public void RingWrapPreservesOrderAndPadsUnderflow()
    {
        var ring = new PcmRingBuffer(4);
        ring.Write([1, 1, 2, 2, 3, 3]);
        var first = new float[4];
        ring.Read(first);
        Assert.Equal(new float[] { 1, 1, 2, 2 }, first);
        ring.Write([4, 4, 5, 5]);
        var rest = new float[8];
        Assert.Equal(6, ring.Read(rest));
        Assert.Equal(new float[] { 3, 3, 4, 4, 5, 5, 0, 0 }, rest);
    }

    [Fact]
    public void RingOverflowKeepsLatestCompleteFrames()
    {
        var ring = new PcmRingBuffer(2);
        ring.Write([1, 1, 2, 2, 3, 3]);
        var output = new float[4];
        ring.Read(output);
        Assert.Equal(new float[] { 2, 2, 3, 3 }, output);
        Assert.Equal(1, ring.DroppedFrames);
        Assert.Throws<ArgumentException>(() => ring.Write([1]));
    }

    [Fact]
    public void ClockBacklogIsBoundedInsteadOfGrowingDuringLongSessions()
    {
        var ring = new PcmRingBuffer();
        var output = new float[960];
        for (int i = 0; i < 5000; i++)
        {
            ring.Write(new float[962]); // capture clock runs slightly faster than render clock
            ring.Read(output);
            Assert.InRange(ring.BufferedFrames, 0, 2880);
        }
        Assert.True(ring.DroppedFrames > 0);
    }

    [Fact]
    public void AppSilenceNeverStopsMicrophoneOrRenderClock()
    {
        var mixer = new AudioMixer();
        mixer.AddApplicationSource();
        mixer.WriteMicrophone(Signal(.2f));
        Assert.All(Render(mixer), value => Assert.Equal(.2f, value, 5));
        Assert.All(Render(mixer), value => Assert.Equal(0, value));
    }

    [Fact]
    public void IndependentGainsSumEachApplicationTreeOnce()
    {
        var mixer = new AudioMixer { MicrophoneGain = .5f, ApplicationGain = .25f };
        var first = mixer.AddApplicationSource();
        var second = mixer.AddApplicationSource();
        Render(mixer); // allow the gain ramp to settle
        mixer.WriteMicrophone(Signal(.2f));
        mixer.WriteApplication(first, Signal(.4f));
        mixer.WriteApplication(second, Signal(.4f));
        Assert.All(Render(mixer), value => Assert.Equal(.3f, value, 5));
        var levels = mixer.TakeLevels();
        Assert.Equal(.1f, levels.Microphone, 5);
        Assert.Equal(.2f, levels.Application, 5);
        Assert.Equal(.3f, levels.Output, 5);
        Assert.Equal(default, mixer.TakeLevels());
    }

    [Fact]
    public void PausingDiscardsQueuedAndIncomingMusicButKeepsVoice()
    {
        var mixer = new AudioMixer();
        var source = mixer.AddApplicationSource();
        mixer.WriteApplication(source, Signal(.8f));
        mixer.SharingEnabled = false;
        mixer.WriteApplication(source, Signal(.8f));
        mixer.WriteMicrophone(Signal(.2f));
        Assert.All(Render(mixer), value => Assert.Equal(.2f, value, 5));
        mixer.SharingEnabled = true;
        Assert.All(Render(mixer), value => Assert.Equal(0, value));
    }

    [Fact]
    public void SourceSwitchCannotReplayPreviouslyQueuedAppAudio()
    {
        var mixer = new AudioMixer();
        var old = mixer.AddApplicationSource();
        mixer.WriteApplication(old, Signal(.8f));
        mixer.RemoveApplicationSources();
        mixer.AddApplicationSource();
        mixer.WriteApplication(old, Signal(.8f)); // a callback already in flight
        Assert.All(Render(mixer), value => Assert.Equal(0, value));
    }

    [Fact]
    public void RemovingOneApplicationPreservesOtherApplicationAndMicrophone()
    {
        var mixer = new AudioMixer { ApplicationGain = 1 };
        var first = mixer.AddApplicationSource();
        var second = mixer.AddApplicationSource();
        Render(mixer);
        mixer.WriteMicrophone(Signal(.1f));
        mixer.WriteApplication(first, Signal(.2f));
        mixer.WriteApplication(second, Signal(.3f));
        Assert.All(Render(mixer), value => Assert.Equal(.6f, value, 5));
        mixer.RemoveApplicationSource(first);
        mixer.WriteApplication(first, Signal(.8f)); // an in-flight callback from the removed app
        mixer.WriteMicrophone(Signal(.1f));
        mixer.WriteApplication(second, Signal(.3f));
        Assert.All(Render(mixer), value => Assert.Equal(.4f, value, 5));
        mixer.RemoveApplicationSource(second);
        mixer.WriteMicrophone(Signal(.1f));
        Assert.All(Render(mixer), value => Assert.Equal(.1f, value, 5));
    }

    [Fact]
    public void SelectionNormalizationMergesDuplicateAppsWithoutDuplicatingProcessCapture()
    {
        var normalized = ApplicationSelection.Normalize([
            new("Browser", "C:\\Apps\\Browser.exe", [new(1, 100)], false),
            new("Browser", "c:\\apps\\browser.exe", [new(1, 100), new(2, 200)], true),
            new("Music", "C:\\Apps\\Music.exe", [new(3, 300)], true)]);
        Assert.Equal(2, normalized.Length);
        Assert.Equal(2, normalized[0].Processes.Count);
        Assert.True(normalized[0].HasAudio);
        Assert.Single(normalized[1].Processes);
    }

    [Fact]
    public void MultipleSelectionsPersistAndLegacySingleSelectionMigrates()
    {
        string path = Path.Combine(Path.GetTempPath(), $"AudioRouter-multiple-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"ApplicationPath\":\"C:\\\\Apps\\\\Browser.exe\"}");
            Assert.Equal(new[] { "C:\\Apps\\Browser.exe" }, SettingsStore.Load(path).ApplicationPaths);
            SettingsStore.Save(new() { ApplicationPaths = [" C:\\Apps\\Browser.exe ", "c:\\apps\\browser.exe", "C:\\Apps\\Music.exe", ""] }, path);
            var restored = SettingsStore.Load(path);
            Assert.Equal(new[] { "C:\\Apps\\Browser.exe", "C:\\Apps\\Music.exe" }, restored.ApplicationPaths);
            Assert.Equal("C:\\Apps\\Browser.exe", restored.ApplicationPath);
            SettingsStore.Save(restored with { ApplicationPaths = [] }, path);
            Assert.Empty(SettingsStore.Load(path).ApplicationPaths!);
            Assert.Null(SettingsStore.Load(path).ApplicationPath);
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [Fact]
    public void LimiterContainsClippingAndNonFiniteInput()
    {
        var mixer = new AudioMixer { MicrophoneGain = 2, ApplicationGain = 2 };
        var source = mixer.AddApplicationSource();
        mixer.WriteMicrophone(Signal(5));
        mixer.WriteApplication(source, Signal(5));
        Assert.All(Render(mixer), value => Assert.InRange(value, -.981f, .981f));
        mixer.WriteMicrophone(Signal(float.NaN));
        mixer.WriteApplication(source, Signal(float.PositiveInfinity));
        Assert.All(Render(mixer), value => Assert.Equal(0, value));
    }

    [Fact]
    public void MutingMicrophoneDoesNotMuteApplication()
    {
        var mixer = new AudioMixer { MicrophoneMuted = true, ApplicationGain = 1 };
        var source = mixer.AddApplicationSource();
        Render(mixer);
        mixer.WriteMicrophone(Signal(.5f));
        mixer.WriteApplication(source, Signal(.2f));
        Assert.All(Render(mixer), value => Assert.Equal(.2f, value, 5));
    }

    [Fact]
    public void TwoHundredPercentBoostDoublesApplicationWhileVoiceLevelStaysUnchanged()
    {
        var mixer = new AudioMixer { ApplicationGain = 1 };
        var source = mixer.AddApplicationSource();
        Render(mixer); // settle the initial gain ramp
        mixer.WriteMicrophone(Signal(.1f));
        mixer.WriteApplication(source, Signal(.2f));
        Assert.All(Render(mixer), value => Assert.Equal(.3f, value, 5));

        mixer.ApplicationGain = 2;
        Render(mixer); // settle the live gain change
        mixer.WriteMicrophone(Signal(.1f));
        mixer.WriteApplication(source, Signal(.2f));
        Assert.All(Render(mixer), value => Assert.Equal(.5f, value, 5));
        Assert.Equal(1f, mixer.MicrophoneGain);
    }

    [Fact]
    public void OutgoingBoostIsRestoredAfterSettingsReloadAndCappedAtTwoHundredPercent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"AudioRouter-boost-{Guid.NewGuid():N}.json");
        try
        {
            SettingsStore.Save(new() { ApplicationGain = 2 }, path);
            Assert.Equal(2f, SettingsStore.Load(path).ApplicationGain);
            SettingsStore.Save(new() { ApplicationGain = 99 }, path);
            Assert.Equal(2f, SettingsStore.Load(path).ApplicationGain);
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [Fact]
    public void DisablingMetersKeepsAudioIdenticalAndDoesNotAccumulateLevels()
    {
        var measured = new AudioMixer { ApplicationGain = 1 };
        var quietUi = new AudioMixer { ApplicationGain = 1, MeteringEnabled = false };
        var measuredSource = measured.AddApplicationSource();
        var quietSource = quietUi.AddApplicationSource();
        Render(measured); Render(quietUi);
        measured.WriteMicrophone(Signal(.1f)); quietUi.WriteMicrophone(Signal(.1f));
        measured.WriteApplication(measuredSource, Signal(.2f)); quietUi.WriteApplication(quietSource, Signal(.2f));
        Assert.Equal(Render(measured), Render(quietUi));
        Assert.True(measured.TakeLevels().Output > 0);
        Assert.Equal(default, quietUi.TakeLevels());
        quietUi.MeteringEnabled = true;
        quietUi.WriteMicrophone(Signal(.1f));
        Render(quietUi);
        Assert.True(quietUi.TakeLevels().Microphone > 0);
        quietUi.MeteringEnabled = false;
        Assert.Equal(default, quietUi.TakeLevels());
    }

    [Fact]
    public void RenameLoadsLegacyPreferencesOnlyUntilNewPreferencesExist()
    {
        string current = Path.Combine(Path.GetTempPath(), $"AudioRouter-current-{Guid.NewGuid():N}.json");
        string legacy = current + ".legacy";
        try
        {
            File.WriteAllText(legacy, "{\"MicrophoneId\":\"mic\",\"CableId\":\"cable\",\"ApplicationGain\":2}");
            var migrated = SettingsStore.Load(current, legacy);
            Assert.Equal("mic", migrated.MicrophoneId);
            Assert.Equal("cable", migrated.CableId);
            Assert.Equal(2f, migrated.ApplicationGain);
            Assert.False(migrated.ShowLiveMeters);
            Assert.False(File.Exists(current));
            SettingsStore.Save(migrated with { ShowLiveMeters = true, ApplicationGain = 1.5f }, current);
            var restored = SettingsStore.Load(current, legacy);
            Assert.True(restored.ShowLiveMeters);
            Assert.Equal(1.5f, restored.ApplicationGain);
        }
        finally { File.Delete(current); File.Delete(current + ".tmp"); File.Delete(legacy); }
    }

    [Fact]
    public void RootResolutionStopsBeforeExplorerAndHandlesPidReuse()
    {
        var parents = new Dictionary<int, int> { [3] = 2, [2] = 1 };
        string? Path(int id) => id == 1 ? "explorer.exe" : "brave.exe";
        Assert.Equal(2, ProcessTree.FindApplicationRoot(3, parents, Path, id => id));
        Assert.Equal(3, ProcessTree.FindApplicationRoot(3, parents, Path, id => id == 2 ? 100 : id));
    }

    [Fact]
    public void CyclicProcessSnapshotCannotLoopForever()
    {
        var parents = new Dictionary<int, int> { [1] = 2, [2] = 1 };
        Assert.Equal(2, ProcessTree.FindApplicationRoot(1, parents, _ => "app.exe", _ => 1));
    }

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", true)]
    [InlineData("CABLE-A Input (VB-Audio Cable A)", true)]
    [InlineData("Microphone (USB Audio Device)", false)]
    [InlineData("Speakers (Realtek Audio)", false)]
    [InlineData("CABLE Input (another product)", false)]
    public void OnlyKnownCableEndpointsCanBeRoutingOutputs(string name, bool expected) => Assert.Equal(expected, Devices.IsCableName(name));

    [Fact]
    public void SettingsRecoverFromCorruptionAndRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"AudioRouter-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "broken json");
            Assert.Equal(.25f, SettingsStore.Load(path).ApplicationGain);
            SettingsStore.Save(new() { MicrophoneId = "mic", ApplicationPath = "C:\\Apps\\Brave.exe", MicrophoneGain = 99, ApplicationGain = -1 }, path);
            var loaded = SettingsStore.Load(path);
            Assert.Equal("mic", loaded.MicrophoneId);
            Assert.Equal("C:\\Apps\\Brave.exe", loaded.ApplicationPath);
            Assert.Equal(2, loaded.MicrophoneGain);
            Assert.Equal(0, loaded.ApplicationGain);
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [Fact]
    public async Task FailedStartRollsBackAndStopRemainsIdempotent()
    {
        await using var router = new RoutingEngine();
        await Assert.ThrowsAnyAsync<Exception>(() => router.StartAsync("missing-device", "missing-cable", null));
        Assert.False(router.IsRunning);
        Assert.Null(router.CurrentDevices);
        Assert.Empty(router.CurrentApplications);
        await router.StopAsync();
        await router.StopAsync();
    }

    [Fact]
    public async Task DisposedRouterCannotReopenAudioStreams()
    {
        var router = new RoutingEngine();
        await router.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => router.StartAsync("mic", "cable", null));
        Assert.False(router.IsRunning);
        await router.StopAsync();
    }

    [Fact]
    public void CableChoicePrefersRegularPairAndPreservesExplicitChoice()
    {
        var multichannel = new AudioDevice("16ch", "CABLE In 16ch (VB-Audio Virtual Cable)", false, true);
        var regular = new AudioDevice("regular", "CABLE Input (VB-Audio Virtual Cable)", false, true);
        AudioDevice[] inputs = [multichannel, regular]; // device enumeration sorts 16ch first
        Assert.Equal(regular, Devices.PickCableInput(inputs, null));
        Assert.Equal(regular, Devices.PickCableInput(inputs, "removed-device"));
        Assert.Equal(multichannel, Devices.PickCableInput(inputs, "16ch"));
        Assert.Equal(multichannel, Devices.PickCableInput([multichannel], null));
        Assert.Null(Devices.PickCableInput([], null));
    }

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", "CABLE Output (VB-Audio Virtual Cable)")]
    [InlineData("CABLE In 16ch (VB-Audio Virtual Cable)", "CABLE Out 16ch (VB-Audio Virtual Cable)")]
    [InlineData("CABLE-A Input (VB-Audio Cable A)", "CABLE-A Output (VB-Audio Cable A)")]
    public void DestinationAppHintMatchesSelectedCablePair(string input, string output) =>
        Assert.Equal(output, new AudioDevice("cable", input, false, true).RecordingEndpointName);
}
