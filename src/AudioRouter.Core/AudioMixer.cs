using System.Runtime.InteropServices;
using NAudio.Wave;

namespace AudioRouter.Core;

public readonly record struct AudioLevels(float Microphone, float Application, float Output);

/// <summary>Render-clock driven 48 kHz stereo float mix, independent of speaker endpoint volume.</summary>
public sealed class AudioMixer : IWaveProvider
{
    private readonly object applicationGate = new();
    private PcmRingBuffer[] applicationBuffers = [];
    private readonly PcmRingBuffer microphone = new();
    private float[] scratch = new float[19200];
    private float microphoneGain = 1f;
    private float applicationGain = .25f;
    private bool sharingEnabled = true;
    private bool microphoneMuted;
    private bool meteringEnabled = true;
    private float microphonePeak, applicationPeak, outputPeak;
    private float currentMicrophoneGain = 1f, currentApplicationGain = .25f;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public float MicrophoneGain { get => Volatile.Read(ref microphoneGain); set => Volatile.Write(ref microphoneGain, ValidGain(value)); }
    public float ApplicationGain { get => Volatile.Read(ref applicationGain); set => Volatile.Write(ref applicationGain, ValidGain(value)); }
    public bool MicrophoneMuted { get => Volatile.Read(ref microphoneMuted); set => Volatile.Write(ref microphoneMuted, value); }
    public bool MeteringEnabled
    {
        get => Volatile.Read(ref meteringEnabled);
        set
        {
            Volatile.Write(ref meteringEnabled, value);
            TakeLevels();
        }
    }
    public bool SharingEnabled
    {
        get => Volatile.Read(ref sharingEnabled);
        set
        {
            lock (applicationGate)
            {
                foreach (var buffer in applicationBuffers) buffer.Clear();
                Volatile.Write(ref sharingEnabled, value);
            }
        }
    }

    private static float ValidGain(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 2) : 0;

    public PcmRingBuffer AddApplicationSource()
    {
        lock (applicationGate)
        {
            var buffer = new PcmRingBuffer();
            Volatile.Write(ref applicationBuffers, [.. applicationBuffers, buffer]);
            return buffer;
        }
    }

    public void RemoveApplicationSources()
    {
        lock (applicationGate)
        {
            foreach (var buffer in applicationBuffers) buffer.Clear();
            Volatile.Write(ref applicationBuffers, []);
        }
    }

    public void WriteMicrophone(ReadOnlySpan<byte> data) => microphone.Write(MemoryMarshal.Cast<byte, float>(data));

    public void WriteApplication(PcmRingBuffer source, ReadOnlySpan<byte> data)
    {
        lock (applicationGate)
        {
            if (sharingEnabled) source.Write(MemoryMarshal.Cast<byte, float>(data));
        }
    }

    public int Read(Span<byte> buffer)
    {
        int bytes = buffer.Length - buffer.Length % WaveFormat.BlockAlign;
        var output = MemoryMarshal.Cast<byte, float>(buffer[..bytes]);
        if (scratch.Length < output.Length) scratch = new float[output.Length];
        var temp = scratch.AsSpan(0, output.Length);
        microphone.Read(output);
        float micTarget = MicrophoneMuted ? 0 : MicrophoneGain;
        float appTarget = SharingEnabled ? ApplicationGain : 0;
        bool measure = MeteringEnabled;
        float micPeak = 0, appPeak = 0, mixedPeak = 0;
        float micStep = (micTarget - currentMicrophoneGain) / Math.Max(1, output.Length / 2);
        float appStep = (appTarget - currentApplicationGain) / Math.Max(1, output.Length / 2);
        for (int i = 0; i < output.Length; i += 2)
        {
            currentMicrophoneGain += micStep;
            output[i] = SafeSample(output[i]) * currentMicrophoneGain;
            output[i + 1] = SafeSample(output[i + 1]) * currentMicrophoneGain;
            if (measure) micPeak = Math.Max(micPeak, Math.Max(Math.Abs(output[i]), Math.Abs(output[i + 1])));
        }
        temp.Clear();
        // A separate accumulator avoids gain being applied twice to multiple process trees.
        float[]? rented = null;
        try
        {
            var sources = Volatile.Read(ref applicationBuffers);
            if (sources.Length > 0)
            {
                rented = System.Buffers.ArrayPool<float>.Shared.Rent(output.Length);
                var input = rented.AsSpan(0, output.Length);
                foreach (var source in sources)
                {
                    source.Read(input);
                    for (int i = 0; i < temp.Length; i++) temp[i] += SafeSample(input[i]);
                }
            }
            bool share = SharingEnabled;
            for (int i = 0; i < output.Length; i += 2)
            {
                currentApplicationGain += appStep;
                for (int channel = 0; channel < 2; channel++)
                {
                    int index = i + channel;
                    float app = share ? temp[index] * currentApplicationGain : 0;
                    if (measure) appPeak = Math.Max(appPeak, Math.Abs(app));
                    output[index] = Limit(output[index] + app);
                    if (measure) mixedPeak = Math.Max(mixedPeak, Math.Abs(output[index]));
                }
            }
        }
        finally { if (rented is not null) System.Buffers.ArrayPool<float>.Shared.Return(rented); }
        currentMicrophoneGain = micTarget;
        currentApplicationGain = appTarget;
        if (measure)
        {
            Accumulate(ref microphonePeak, micPeak);
            Accumulate(ref applicationPeak, appPeak);
            Accumulate(ref outputPeak, mixedPeak);
        }
        buffer[bytes..].Clear();
        return bytes;
    }

    private static float SafeSample(float sample) => float.IsFinite(sample) ? sample : 0;
    private static float Limit(float value)
    {
        float magnitude = Math.Abs(value);
        return magnitude <= .9f ? value : MathF.CopySign(.9f + .08f * MathF.Tanh((magnitude - .9f) / .08f), value);
    }

    private static void Accumulate(ref float field, float value)
    {
        float previous;
        do { previous = Volatile.Read(ref field); if (value <= previous) return; }
        while (Interlocked.CompareExchange(ref field, value, previous) != previous);
    }

    public AudioLevels TakeLevels() => new(Interlocked.Exchange(ref microphonePeak, 0),
        Interlocked.Exchange(ref applicationPeak, 0), Interlocked.Exchange(ref outputPeak, 0));
}
