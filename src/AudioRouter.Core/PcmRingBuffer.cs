namespace AudioRouter.Core;

/// <summary>A bounded, frame-aligned queue. Late audio is discarded rather than replayed.</summary>
public sealed class PcmRingBuffer
{
    private readonly object gate = new();
    private readonly float[] samples;
    private readonly int channels;
    private int head;
    private int count;
    private long droppedSamples;

    public PcmRingBuffer(int capacityFrames = 4800, int channels = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        this.channels = channels;
        samples = new float[checked(capacityFrames * channels)];
    }

    public long DroppedFrames { get { lock (gate) return droppedSamples / channels; } }
    public int BufferedFrames { get { lock (gate) return count / channels; } }

    public void Write(ReadOnlySpan<float> input)
    {
        if (input.Length % channels != 0) throw new ArgumentException("Incomplete audio frame.", nameof(input));
        lock (gate)
        {
            if (input.Length >= samples.Length)
            {
                droppedSamples += count + input.Length - samples.Length;
                input = input[^samples.Length..];
                head = count = 0;
            }
            int excess = Math.Max(0, count + input.Length - samples.Length);
            head = (head + excess) % samples.Length;
            count -= excess;
            droppedSamples += excess;
            int tail = (head + count) % samples.Length;
            int first = Math.Min(input.Length, samples.Length - tail);
            input[..first].CopyTo(samples.AsSpan(tail));
            input[first..].CopyTo(samples);
            count += input.Length;
        }
    }

    public int Read(Span<float> output)
    {
        if (output.Length % channels != 0) throw new ArgumentException("Incomplete audio frame.", nameof(output));
        lock (gate)
        {
            // Bound long-running capture/render clock drift to 60 ms at 48 kHz.
            int maximumBacklog = Math.Min(samples.Length, 2880 * channels);
            if (count > maximumBacklog)
            {
                int discard = count - maximumBacklog;
                head = (head + discard) % samples.Length;
                count -= discard;
                droppedSamples += discard;
            }
            int take = Math.Min(count, output.Length);
            int first = Math.Min(take, samples.Length - head);
            samples.AsSpan(head, first).CopyTo(output);
            samples.AsSpan(0, take - first).CopyTo(output[first..]);
            output[take..].Clear();
            head = (head + take) % samples.Length;
            count -= take;
            return take;
        }
    }

    public void Clear() { lock (gate) head = count = 0; }
}
