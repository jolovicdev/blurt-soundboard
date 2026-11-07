using Blurt.Audio;
using NAudio.Wave;

namespace Blurt.Tests;

public class MicInputTests
{
    // 1 kHz mono keeps the numbers readable: 1 ms is 1 sample.
    static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(1000, 1);

    static MicInput Create() => new(Format, targetMs: 30, slackMs: 40, capacityMs: 200);

    static float[] Ramp(int start, int length) => Enumerable.Range(start, length).Select(i => (float)i).ToArray();

    static float[] Read(MicInput mic, int length)
    {
        var buffer = new float[length];
        Assert.Equal(length, mic.Read(buffer));
        return buffer;
    }

    [Fact]
    public void StaysSilentUntilTheTargetIsBuffered()
    {
        var mic = Create();
        mic.Write(Ramp(1, 20));

        Assert.All(Read(mic, 10), s => Assert.Equal(0f, s));
        Assert.Equal(20, mic.BufferedSamples);

        mic.Write(Ramp(21, 10));
        Assert.Equal(Ramp(1, 10), Read(mic, 10));
    }

    [Fact]
    public void DropsTheOldestAudioWhenTheBufferRunsLong()
    {
        var mic = Create();
        mic.Write(Ramp(1, 80));

        // 80 buffered is over target + slack (70), so it skips back to the newest 30.
        Assert.Equal(Ramp(51, 10), Read(mic, 10));
        Assert.Equal(20, mic.BufferedSamples);
    }

    [Fact]
    public void KeepsSmallFluctuationsWithoutDropping()
    {
        var mic = Create();
        mic.Write(Ramp(1, 60));

        Assert.Equal(Ramp(1, 10), Read(mic, 10));
    }

    [Fact]
    public void PadsAnUnderrunWithSilenceAndRefillsBeforePlayingAgain()
    {
        var mic = Create();
        mic.Write(Ramp(1, 30));

        var first = Read(mic, 40);
        Assert.Equal(Ramp(1, 30), first[..30]);
        Assert.All(first[30..], s => Assert.Equal(0f, s));

        mic.Write(Ramp(31, 10));
        Assert.All(Read(mic, 5), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void OverflowKeepsTheNewestAudio()
    {
        var mic = Create();
        mic.Write(Ramp(1, 150));
        mic.Write(Ramp(151, 100));

        Assert.Equal(200, mic.BufferedSamples);
        // Over the high mark, so it skips to the newest 30: samples 221..250.
        Assert.Equal(Ramp(221, 5), Read(mic, 5));
    }

    [Fact]
    public void WrapsAroundTheRing()
    {
        var mic = Create();
        // 20 packets of 30 pass through a 200-sample ring three times over.
        for (int i = 0; i < 20; i++)
        {
            mic.Write(Ramp(1 + i * 30, 30));
            Assert.Equal(Ramp(1 + i * 30, 30), Read(mic, 30));
        }
    }

    [Fact]
    public void AppliesVolumeAndMute()
    {
        var mic = Create();
        mic.Write(Enumerable.Repeat(0.5f, 40).ToArray());
        mic.Volume = 0.5f;
        Assert.All(Read(mic, 10), s => Assert.Equal(0.25f, s));

        mic.Muted = true;
        Assert.All(Read(mic, 10), s => Assert.Equal(0f, s));
        Assert.Equal(20, mic.BufferedSamples);
    }

    [Fact]
    public void ReportsThePeakOfTheLatestPacket()
    {
        var mic = Create();
        mic.Write([0.1f, -0.7f, 0.3f]);
        Assert.Equal(0.7f, mic.Peak);

        mic.WriteSilence(10);
        Assert.Equal(0f, mic.Peak);
    }
}
