using NAudio.Wave;

namespace Blurt.Audio;

/// <summary>
/// Carries microphone audio from the capture thread to the render thread of the send device.
/// </summary>
/// <remarks>
/// The microphone and the send device run on separate clocks, so over minutes the buffer slowly
/// fills or drains. Reads keep it near the target: when it runs long, the oldest audio is dropped
/// back to the target, and after it runs dry the output stays silent until the target is buffered
/// again. Each correction is a short glitch every few minutes at typical clock drift; continuously
/// adjusting a resampling ratio would remove it.
/// </remarks>
public sealed class MicInput : ISampleProvider
{
    readonly object gate = new();
    readonly float[] ring;
    readonly int targetSamples;
    readonly int highSamples;
    int readIndex;
    int count;
    bool primed;
    volatile float peak;

    public MicInput(WaveFormat format, int targetMs = 30, int slackMs = 40, int capacityMs = 1000)
    {
        WaveFormat = format;
        targetSamples = SamplesFor(targetMs);
        highSamples = SamplesFor(targetMs + slackMs);
        ring = new float[SamplesFor(capacityMs)];
    }

    public WaveFormat WaveFormat { get; }

    public float Volume { get; set; } = 1f;

    public bool Muted { get; set; }

    /// <summary>Peak level of the latest captured packet, before <see cref="Volume"/>.</summary>
    public float Peak => peak;

    public int BufferedSamples
    {
        get { lock (gate) return count; }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        float max = 0;
        foreach (var sample in samples) max = Math.Max(max, Math.Abs(sample));
        peak = max;
        lock (gate) Append(samples.Length, samples);
    }

    public void WriteSilence(int sampleCount)
    {
        peak = 0;
        lock (gate) Append(sampleCount, []);
    }

    public void Reset()
    {
        peak = 0;
        lock (gate)
        {
            readIndex = 0;
            count = 0;
            primed = false;
        }
    }

    public int Read(Span<float> buffer)
    {
        int copied = 0;
        lock (gate)
        {
            if (!primed && count >= targetSamples) primed = true;
            if (primed && count > highSamples) Discard(count - targetSamples);
            if (primed)
            {
                copied = Math.Min(buffer.Length, count);
                int first = Math.Min(copied, ring.Length - readIndex);
                ring.AsSpan(readIndex, first).CopyTo(buffer);
                ring.AsSpan(0, copied - first).CopyTo(buffer[first..]);
                Discard(copied);
                if (copied < buffer.Length) primed = false;
            }
        }

        if (Muted)
        {
            buffer.Clear();
            return buffer.Length;
        }
        buffer[copied..].Clear();
        float volume = Volume;
        if (volume != 1f)
        {
            foreach (ref var sample in buffer[..copied]) sample *= volume;
        }
        return buffer.Length;
    }

    int SamplesFor(int milliseconds) => WaveFormat.SampleRate * milliseconds / 1000 * WaveFormat.Channels;

    // An empty source appends silence.
    void Append(int length, ReadOnlySpan<float> source)
    {
        if (length > ring.Length)
        {
            if (!source.IsEmpty) source = source[^ring.Length..];
            length = ring.Length;
        }
        int overflow = count + length - ring.Length;
        if (overflow > 0) Discard(overflow);

        int write = (readIndex + count) % ring.Length;
        int first = Math.Min(length, ring.Length - write);
        if (source.IsEmpty)
        {
            ring.AsSpan(write, first).Clear();
            ring.AsSpan(0, length - first).Clear();
        }
        else
        {
            source[..first].CopyTo(ring.AsSpan(write));
            source[first..].CopyTo(ring);
        }
        count += length;
    }

    void Discard(int samples)
    {
        readIndex = (readIndex + samples) % ring.Length;
        count -= samples;
    }
}
