using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Blurt.Audio;

/// <summary>
/// A sound decoded once into memory at the engine format, so playback never reads the disk.
/// </summary>
public sealed class SoundClip
{
    public static readonly string[] Extensions = [".mp3", ".wav", ".ogg", ".m4a", ".aac", ".wma", ".flac"];

    /// <summary>Longer files are cut at this length; a minute takes 23 MB at 48 kHz stereo.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(2);

    const double TargetRms = 0.1;     // about -20 dBFS
    const float PeakCeiling = 0.89f;  // about -1 dBFS

    public SoundClip(string path, float[] samples, WaveFormat format)
    {
        Path = path;
        Samples = samples;
        WaveFormat = format;
    }

    public string Path { get; }

    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public float[] Samples { get; }

    public WaveFormat WaveFormat { get; }

    public TimeSpan Duration =>
        TimeSpan.FromSeconds((double)Samples.Length / WaveFormat.Channels / WaveFormat.SampleRate);

    public static bool IsSupported(string path) =>
        Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the length from the file header without decoding the audio.</summary>
    public static TimeSpan ReadDuration(string path)
    {
        using var reader = Open(path);
        return reader.TotalTime < MaxLength ? reader.TotalTime : MaxLength;
    }

    public static SoundClip Load(string path, WaveFormat format)
    {
        using var reader = Open(path);

        ISampleProvider source = reader.ToSampleProvider();
        source = source.WaveFormat.Channels switch
        {
            1 => source.ToStereo(),
            2 => source,
            var channels => throw new NotSupportedException(
                $"It has {channels} channels. Blurt plays mono and stereo files."),
        };
        if (source.WaveFormat.SampleRate != format.SampleRate)
        {
            source = new WdlResamplingSampleProvider(source, format.SampleRate);
        }

        int max = (int)(MaxLength.TotalSeconds * format.SampleRate) * format.Channels;
        var samples = new List<float>();
        var chunk = new float[format.SampleRate * format.Channels];
        int read;
        while (samples.Count < max && (read = source.Read(chunk)) > 0)
        {
            samples.AddRange(chunk.AsSpan(0, read));
        }
        if (samples.Count > max) samples.RemoveRange(max, samples.Count - max);

        var data = samples.ToArray();
        Normalize(data);
        return new SoundClip(path, data, format);
    }

    static WaveStream Open(string path) =>
        System.IO.Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            ? new VorbisWaveReader(path)
            : new AudioFileReader(path);

    /// <summary>
    /// Brings files to a common loudness, so one quiet clip and one loud clip play at similar
    /// levels. The gain is limited so the loudest sample stays under the ceiling, which leaves a
    /// quiet file with one sharp click quieter than the rest.
    /// </summary>
    public static void Normalize(Span<float> samples)
    {
        double sumOfSquares = 0;
        float peak = 0;
        foreach (var sample in samples)
        {
            sumOfSquares += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        if (peak == 0) return;

        double rms = Math.Sqrt(sumOfSquares / samples.Length);
        float gain = (float)Math.Min(TargetRms / rms, PeakCeiling / peak);
        foreach (ref var sample in samples) sample *= gain;
    }
}

/// <summary>
/// One playback of a clip on one output. The gain is read for every buffer, so volume changes
/// apply while the clip plays. Stopping fades out over 10 ms instead of cutting, which clicks.
/// </summary>
public sealed class ClipVoice(SoundClip clip, Func<float> gain) : ISampleProvider
{
    public const int FadeFrames = 480;

    int position;
    int fadeLeft = -1;
    volatile bool stopRequested;
    volatile bool dropped;

    public WaveFormat WaveFormat => clip.WaveFormat;

    /// <summary>The output closed and removed this voice from its mixer, so it will not play further.</summary>
    public bool IsDropped => dropped;

    public void Stop() => stopRequested = true;

    public void Drop() => dropped = true;

    public int Read(Span<float> buffer)
    {
        int channels = clip.WaveFormat.Channels;
        if (stopRequested && fadeLeft < 0) fadeLeft = FadeFrames;

        int length = Math.Min(buffer.Length, clip.Samples.Length - position);
        if (fadeLeft >= 0) length = Math.Min(length, fadeLeft * channels);

        float g = gain();
        var source = clip.Samples.AsSpan(position, length);
        for (int i = 0; i < length; i++)
        {
            float sample = source[i] * g;
            if (fadeLeft >= 0) sample *= (float)(fadeLeft - i / channels) / FadeFrames;
            buffer[i] = sample;
        }

        position += length;
        if (fadeLeft >= 0) fadeLeft -= length / channels;
        return length;
    }
}

/// <summary>A sound playing on one or more outputs at once.</summary>
public sealed class Playback(SoundClip clip, params ClipVoice[] voices)
{
    readonly long started = Environment.TickCount64;

    public SoundClip Clip => clip;

    public bool IsStopped { get; private set; }

    public double Progress => clip.Duration <= TimeSpan.Zero
        ? 1
        : Math.Clamp((Environment.TickCount64 - started) / clip.Duration.TotalMilliseconds, 0, 1);

    /// <remarks>A playback with no voice left, because no output was open or every output closed, is finished.</remarks>
    public bool IsFinished => IsStopped || Progress >= 1 || voices.All(v => v.IsDropped);

    public void Stop()
    {
        IsStopped = true;
        foreach (var voice in voices) voice.Stop();
    }
}
