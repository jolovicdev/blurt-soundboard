using System.IO;
using Blurt.Audio;
using NAudio.Wave;

namespace Blurt.Tests;

public class SoundClipTests
{
    static readonly WaveFormat Stereo = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    static SoundClip Constant(float value, int frames) =>
        new("test.wav", Enumerable.Repeat(value, frames * 2).ToArray(), Stereo);

    static float[] ReadAll(ClipVoice voice, int bufferLength)
    {
        var output = new List<float>();
        var buffer = new float[bufferLength];
        int read;
        while ((read = voice.Read(buffer)) > 0) output.AddRange(buffer[..read]);
        return output.ToArray();
    }

    [Fact]
    public void LoadsAMonoFileAsStereoAtTheEngineRate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blurt-test-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 1)))
            {
                var tone = Enumerable.Range(0, 44100)
                    .Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 44100)))
                    .SelectMany(BitConverter.GetBytes)
                    .ToArray();
                writer.Write(tone);
            }

            var clip = SoundClip.Load(path, Stereo);

            Assert.Equal(48000, clip.WaveFormat.SampleRate);
            Assert.InRange(clip.Samples.Length, 2 * 47500, 2 * 48100);
            Assert.InRange(clip.Duration.TotalSeconds, 0.98, 1.01);
            Assert.Equal(clip.Samples[2000], clip.Samples[2001]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NormalizeBringsQuietAndLoudFilesToTheSameLevel()
    {
        var quiet = Enumerable.Range(0, 4800).Select(i => 0.02f * MathF.Sin(i * 0.1f)).ToArray();
        var loud = Enumerable.Range(0, 4800).Select(i => 0.6f * MathF.Sin(i * 0.1f)).ToArray();

        SoundClip.Normalize(quiet);
        SoundClip.Normalize(loud);

        Assert.Equal(Rms(loud), Rms(quiet), 3);
        Assert.Equal(0.1, Rms(loud), 2);
    }

    [Fact]
    public void NormalizeKeepsPeaksUnderTheCeiling()
    {
        var spiky = new float[4800];
        spiky[100] = 0.05f;
        spiky[200] = 0.001f;

        SoundClip.Normalize(spiky);

        Assert.InRange(spiky.Max(), 0.885f, 0.895f);
    }

    [Fact]
    public void NormalizeLeavesSilenceAlone()
    {
        var silence = new float[100];
        SoundClip.Normalize(silence);
        Assert.All(silence, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void VoicePlaysTheWholeClipWithItsGainThenEnds()
    {
        var voice = new ClipVoice(Constant(0.5f, 1000), () => 0.5f);

        var output = ReadAll(voice, 300);

        Assert.Equal(2000, output.Length);
        Assert.All(output, s => Assert.Equal(0.25f, s));
    }

    [Fact]
    public void VoiceReadsTheGainOnEveryBuffer()
    {
        float gain = 1f;
        var voice = new ClipVoice(Constant(1f, 1000), () => gain);
        var buffer = new float[100];

        voice.Read(buffer);
        Assert.Equal(1f, buffer[99]);
        gain = 0.25f;
        voice.Read(buffer);
        Assert.Equal(0.25f, buffer[0]);
    }

    [Fact]
    public void StoppingFadesOutInsteadOfCutting()
    {
        var voice = new ClipVoice(Constant(1f, 48000), () => 1f);
        voice.Read(new float[200]);

        voice.Stop();
        var tail = ReadAll(voice, 256);

        Assert.Equal(ClipVoice.FadeFrames * 2, tail.Length);
        Assert.Equal(1f, tail[0]);
        Assert.True(tail[^1] < 0.01f);
        for (int i = 2; i < tail.Length; i += 2) Assert.True(tail[i] < tail[i - 2]);
    }

    [Fact]
    public void PlaybackOfAnEmptyClipIsFinishedAtOnce()
    {
        var empty = Constant(0f, 0);
        var playback = new Playback(empty, new ClipVoice(empty, () => 1f));

        Assert.Equal(1, playback.Progress);
        Assert.True(playback.IsFinished);
    }

    [Fact]
    public void PlaybackEndsOnceEveryVoiceIsDropped()
    {
        var clip = Constant(1f, 48000 * 10);
        var send = new ClipVoice(clip, () => 1f);
        var monitor = new ClipVoice(clip, () => 1f);
        var playback = new Playback(clip, send, monitor);

        send.Drop();
        Assert.False(playback.IsFinished);
        monitor.Drop();
        Assert.True(playback.IsFinished);
    }

    static double Rms(float[] samples) => Math.Sqrt(samples.Average(s => (double)s * s));
}
