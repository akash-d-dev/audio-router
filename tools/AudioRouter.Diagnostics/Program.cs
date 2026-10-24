using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using AudioRouter.Audio;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
try
{
    if (args.Length == 0 || args[0] == "--inventory")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { Devices = Devices.GetInventory(), Applications = new Applications().Discover() }, jsonOptions));
        return 0;
    }
    if (args[0] == "--tone")
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(args[1]);
        await using var player = new WasapiPlayerBuilder().WithDevice(device).WithLatency(30).Build();
        player.Init(new QuietTone(double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)));
        player.Play();
        File.WriteAllText(args[3] + ".ready", "ready");
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(args[3] + ".stop") && timeout.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(30);
        return 0;
    }
    if (args[0] == "--verify-multiple-sources")
    {
        using var enumerator = new MMDeviceEnumerator();
        using var speakers = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var devices = Devices.GetInventory();
        var microphone = devices.Microphones.FirstOrDefault() ?? throw new InvalidOperationException("Connect a microphone for the routing check.");
        var cable = Devices.PickCableInput(devices.CableInputs, null) ?? throw new InvalidOperationException("Install VB-CABLE for the routing check.");
        MMDevice? cableRecording = null;
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            if (string.Equals(device.FriendlyName, cable.RecordingEndpointName, StringComparison.OrdinalIgnoreCase)) cableRecording = device;
            else device.Dispose();
        }
        using var recordingDevice = cableRecording ?? throw new InvalidOperationException("The matching cable recording endpoint is missing.");
        string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        string appHost = Path.Combine(assemblyFolder, "AudioRouter.Diagnostics.exe");
        if (!File.Exists(appHost)) throw new InvalidOperationException("Build the diagnostics executable before running this check.");
        string prefix = Path.Combine(Path.GetTempPath(), "AudioRouter-multi-" + Guid.NewGuid().ToString("N"));
        var children = new List<(Process Child, string Prefix, string Executable)>();
        try
        {
            foreach (int frequency in new[] { 997, 1733 })
            {
                string executable = Path.Combine(assemblyFolder, "AudioRouter-test-" + frequency + "-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(appHost, executable);
                string childPrefix = prefix + "-" + frequency;
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    info.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(Environment.ProcessPath);
                foreach (string arg in new[] { "--tone", speakers.ID, frequency.ToString(), childPrefix }) info.ArgumentList.Add(arg);
                var child = Process.Start(info) ?? throw new InvalidOperationException("Could not start the independent test source.");
                children.Add((child, childPrefix, executable));
                for (int i = 0; i < 100 && !File.Exists(childPrefix + ".ready") && !child.HasExited; i++) await Task.Delay(50);
                if (!File.Exists(childPrefix + ".ready")) throw new InvalidOperationException("The independent test source did not start.");
            }
            ApplicationSource Source(int index) => new("Test source " + index, children[index].Executable,
                [new(children[index].Child.Id, children[index].Child.StartTime.ToUniversalTime().ToFileTimeUtc())], true);
            var first = Source(0); var second = Source(1);
            var stats = new SignalStats();
            await using var recorder = new WasapiRecorderBuilder().WithDevice(recordingDevice)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).WithBufferLength(20).Build();
            Exception? captureError = null;
            recorder.DataAvailable += (data, _, _, _) => stats.Add(data);
            recorder.RecordingStopped += (_, stopped) => captureError = stopped.Exception;
            recorder.StartRecording();
            await using var router = new RoutingEngine { MicrophoneMuted = true, MicrophoneGain = 0, ApplicationGain = 1, MeteringEnabled = false };
            await router.StartAsync(microphone.Id, cable.Id, [first, first, second]);
            async Task<SignalResult> Measure()
            {
                await Task.Delay(400);
                stats.Reset(); await Task.Delay(700);
                if (captureError is not null) throw captureError;
                return stats.Result();
            }
            var both = await Measure();
            bool combined = router.CurrentApplications.Count == 2 && both.TargetAmplitude is > .0005 and < .0015 && both.OtherAmplitude is > .0005 and < .0015;
            await router.SelectApplicationsAsync([second]);
            var onlySecond = await Measure();
            bool independentRemoval = router.IsRunning && router.CurrentApplications.Count == 1
                && onlySecond.TargetAmplitude < .0001 && onlySecond.OtherAmplitude > .0005;
            await router.SelectApplicationsAsync([first, second]);
            router.SharingEnabled = false;
            var paused = await Measure();
            bool pauseAll = router.IsRunning && paused.Peak < .0001;
            router.SharingEnabled = true;
            var resumed = await Measure();
            bool resumedBoth = resumed.TargetAmplitude > .0005 && resumed.OtherAmplitude > .0005;
            File.WriteAllText(children[0].Prefix + ".stop", "stop");
            for (int i = 0; i < 30 && router.CurrentApplications.Count != 1; i++) await Task.Delay(100);
            var afterClose = await Measure();
            bool closureIsolated = router.IsRunning && router.CurrentApplications.Count == 1
                && router.CurrentApplications[0].ExecutablePath == second.ExecutablePath
                && afterClose.TargetAmplitude < .0001 && afterClose.OtherAmplitude > .0005;
            await router.SelectApplicationsAsync([second, new("Unavailable", "missing-test-application", [new(-1, 0)], false)]);
            var afterFailure = await Measure();
            bool failureIsolated = router.IsRunning && router.CurrentApplications.Count == 1 && afterFailure.OtherAmplitude > .0005;
            await router.StopAsync();
            Console.WriteLine(JsonSerializer.Serialize(new { CombinedAudioPassed = combined, DuplicateSelectionNotAmplified = combined,
                IndependentRemovalPassed = independentRemoval, PauseAllPassed = pauseAll, ResumeBothPassed = resumedBoth,
                SourceClosureIsolated = closureIsolated, SourceFailureIsolated = failureIsolated,
                Both = both, OnlySecond = onlySecond, AfterClose = afterClose }, jsonOptions));
            return combined && independentRemoval && pauseAll && resumedBoth && closureIsolated && failureIsolated ? 0 : 1;
        }
        finally
        {
            foreach (var (child, childPrefix, executable) in children)
            {
                File.WriteAllText(childPrefix + ".stop", "stop");
                if (!child.HasExited && !child.WaitForExit(3000)) child.Kill();
                child.WaitForExit(); child.Dispose();
                File.Delete(childPrefix + ".ready"); File.Delete(childPrefix + ".stop"); File.Delete(executable);
            }
        }
    }
    if (args[0] is "--verify-loopback" or "--verify-volume")
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var volume = endpoint.AudioEndpointVolume;
        float originalVolume = volume.MasterVolumeLevelScalar;
        bool originalMute = volume.Mute;
        bool changeVolume = args[0] == "--verify-volume";
        string prefix = Path.Combine(Path.GetTempPath(), "AudioRouter-probe-" + Guid.NewGuid().ToString("N"));
        var children = new List<(Process Child, string Prefix)>();
        try
        {
            foreach (int frequency in new[] { 997, 1733 })
            {
                string childPrefix = prefix + "-" + frequency;
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                foreach (string arg in new[] { "--tone", endpoint.ID, frequency.ToString(), childPrefix }) info.ArgumentList.Add(arg);
                var child = Process.Start(info) ?? throw new InvalidOperationException("Could not start test tone.");
                children.Add((child, childPrefix));
                for (int i = 0; i < 100 && !File.Exists(childPrefix + ".ready") && !child.HasExited; i++) await Task.Delay(50);
                if (!File.Exists(childPrefix + ".ready")) throw new InvalidOperationException("Test tone did not start.");
            }
            var stats = new SignalStats();
            await using var recorder = await new WasapiRecorderBuilder().WithProcessLoopback((uint)children[0].Child.Id)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).WithBufferLength(20).BuildAsync();
            Exception? captureError = null;
            recorder.DataAvailable += (data, _, _, _) => stats.Add(data);
            recorder.RecordingStopped += (_, stopped) => captureError = stopped.Exception;
            recorder.StartRecording();
            await Task.Delay(300);
            async Task<SignalResult> Measure()
            {
                stats.Reset(); await Task.Delay(700);
                if (captureError is not null) throw captureError;
                return stats.Result();
            }
            var baseline = await Measure();
            SignalResult? reduced = null, muted = null;
            if (changeVolume)
            {
                volume.MasterVolumeLevelScalar = originalVolume * .5f;
                await Task.Delay(100);
                reduced = await Measure();
                volume.Mute = true;
                await Task.Delay(100);
                muted = await Measure();
            }
            bool isolation = baseline.Frames > 10000 && baseline.TargetAmplitude > .0005 && baseline.OtherAmplitude < baseline.TargetAmplitude * .03;
            bool independent = !changeVolume || (reduced!.TargetAmplitude / baseline.TargetAmplitude is > .90 and < 1.10
                && muted!.TargetAmplitude / baseline.TargetAmplitude is > .90 and < 1.10);
            volume.MasterVolumeLevelScalar = originalVolume;
            volume.Mute = originalMute;
            bool restored = Math.Abs(volume.MasterVolumeLevelScalar - originalVolume) < .0001 && volume.Mute == originalMute;
            Console.WriteLine(JsonSerializer.Serialize(new { IsolationPassed = isolation, VolumeIndependencePassed = independent,
                SpeakerSettingsRestored = restored, Baseline = baseline, ReducedSpeakerVolume = reduced, SpeakersMuted = muted }, jsonOptions));
            return isolation && independent && restored ? 0 : 1;
        }
        finally
        {
            if (changeVolume) { volume.MasterVolumeLevelScalar = originalVolume; volume.Mute = originalMute; }
            foreach (var (child, childPrefix) in children)
            {
                File.WriteAllText(childPrefix + ".stop", "stop");
                if (!child.HasExited && !child.WaitForExit(3000)) child.Kill();
                child.Dispose();
                File.Delete(childPrefix + ".ready"); File.Delete(childPrefix + ".stop");
            }
        }
    }
    if (args[0] == "--probe-app" && args.Length >= 2)
    {
        var stats = new SignalStats();
        await using var recorder = await new WasapiRecorderBuilder().WithProcessLoopback(uint.Parse(args[1]))
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).BuildAsync();
        recorder.DataAvailable += (data, _, _, _) => stats.Add(data);
        recorder.StartRecording();
        await Task.Delay(TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[2]) : 3));
        Console.WriteLine(JsonSerializer.Serialize(stats.Result(), jsonOptions));
        return 0;
    }
    if (args[0] == "--probe-microphone")
    {
        using var enumerator = new MMDeviceEnumerator();
        using var mic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        var stats = new SignalStats();
        await using var recorder = new WasapiRecorderBuilder().WithDevice(mic)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).WithBufferLength(20).Build();
        Exception? captureError = null;
        recorder.DataAvailable += (data, _, _, _) => stats.Add(data);
        recorder.RecordingStopped += (_, stopped) => captureError = stopped.Exception;
        recorder.StartRecording();
        await Task.Delay(2000);
        if (captureError is not null) throw captureError;
        var result = stats.Result();
        Console.WriteLine(JsonSerializer.Serialize(new { Microphone = mic.FriendlyName, SamplesCaptured = result.Frames,
            Peak = result.Peak, AudioSaved = false, Passed = result.Frames > 48000 }, jsonOptions));
        return result.Frames > 48000 ? 0 : 1;
    }
    Console.Error.WriteLine("Usage: --inventory | --verify-loopback | --verify-volume | --verify-multiple-sources | --probe-app PID [seconds] | --probe-microphone");
    return 2;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

sealed class QuietTone(double frequency) : IWaveProvider
{
    private long frame;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public int Read(Span<byte> buffer)
    {
        var output = MemoryMarshal.Cast<byte, float>(buffer);
        for (int i = 0; i < output.Length; i += 2)
        {
            float sample = .001f * (float)Math.Sin(2 * Math.PI * frequency * frame++ / 48000);
            output[i] = output[i + 1] = sample;
        }
        return buffer.Length;
    }
}
sealed record SignalResult(long Frames, double TargetAmplitude, double OtherAmplitude, double Peak);
sealed class SignalStats
{
    private readonly object gate = new();
    private long count;
    private double targetSin, targetCos, otherSin, otherCos, peak;
    public void Add(ReadOnlySpan<byte> data)
    {
        var samples = MemoryMarshal.Cast<byte, float>(data);
        lock (gate)
        {
            for (int i = 0; i < samples.Length; i += 2)
            {
                double sample = samples[i];
                double phase = 2 * Math.PI * count++ / 48000;
                targetSin += sample * Math.Sin(997 * phase); targetCos += sample * Math.Cos(997 * phase);
                otherSin += sample * Math.Sin(1733 * phase); otherCos += sample * Math.Cos(1733 * phase);
                peak = Math.Max(peak, Math.Abs(sample));
            }
        }
    }
    public void Reset() { lock (gate) { count = 0; targetSin = targetCos = otherSin = otherCos = peak = 0; } }
    public SignalResult Result()
    {
        lock (gate) return new(count, count == 0 ? 0 : 2 * Math.Sqrt(targetSin * targetSin + targetCos * targetCos) / count,
            count == 0 ? 0 : 2 * Math.Sqrt(otherSin * otherSin + otherCos * otherCos) / count, peak);
    }
}
