using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Blurt.Audio;

public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// Owns the audio devices. The microphone and every sound go to the send device, which is what
/// the voice chat app records; sounds also go to the monitor device so the user hears them. The
/// microphone is never routed to the monitor, so nobody hears their own voice.
/// </summary>
/// <remarks>
/// A mixer is only read while its device is open, so sounds go only to open outputs, and each
/// output's queue is cleared whenever it opens, closes or fails. Otherwise sounds triggered while
/// an output is closed would wait in its mixer and all play at once when it opens.
///
/// Everything runs at one format. In shared mode Windows converts the microphone into it and
/// converts it to each output's own format, so the engine never resamples live audio itself.
/// </remarks>
public sealed class AudioEngine : IDisposable
{
    public static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    const int OutputLatencyMs = 30;
    const int CaptureBufferMs = 20;

    readonly MMDeviceEnumerator enumerator = new();
    readonly MixingSampleProvider sendSounds = new(Format) { ReadFully = true };
    readonly MixingSampleProvider monitorSounds = new(Format) { ReadFully = true };
    readonly MixingSampleProvider send;
    WasapiRecorder? recorder;
    WasapiPlayer? sendPlayer;
    WasapiPlayer? monitorPlayer;

    public AudioEngine()
    {
        send = new MixingSampleProvider([Mic, sendSounds]) { ReadFully = true };
        Notifications = enumerator.CreateNotificationClient();
    }

    public MicInput Mic { get; } = new(Format);

    /// <summary>Device change events, raised on the thread that created the engine.</summary>
    public MMDeviceNotificationClient Notifications { get; }

    public float SoundsVolume { get; set; } = 0.8f;

    public float MonitorVolume { get; set; } = 0.5f;

    public string? MicId { get; private set; }

    public string? SendId { get; private set; }

    public string? MonitorId { get; private set; }

    /// <summary>A device failed to open or stopped. The message is written for the user.</summary>
    public event Action<string>? Problem;

    /// <summary>An open device stopped on its own, for example because it was unplugged or reset.</summary>
    public event Action? DeviceLost;

    /// <summary>VB-CABLE's virtual speaker. Audio played into it comes out of its virtual microphone.</summary>
    public static bool IsCableInput(AudioDevice device) =>
        device.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase);

    // Recording a virtual cable as the microphone would loop Blurt's own output back into it.
    static bool IsVirtualCable(AudioDevice device) =>
        device.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
        && device.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<AudioDevice> Microphones() => Devices(DataFlow.Capture).Where(d => !IsVirtualCable(d)).ToList();

    public IReadOnlyList<AudioDevice> Outputs() => Devices(DataFlow.Render);

    public string? DefaultMicrophoneId() =>
        DefaultId(DataFlow.Capture) is { } id && Microphones().Any(d => d.Id == id) ? id : Microphones().FirstOrDefault()?.Id;

    /// <summary>The default output, unless that is a virtual cable, which the user cannot hear.</summary>
    public string? DefaultMonitorId()
    {
        var outputs = Outputs().Where(d => !IsVirtualCable(d)).ToList();
        return DefaultId(DataFlow.Render) is { } id && outputs.Any(d => d.Id == id) ? id : outputs.FirstOrDefault()?.Id;
    }

    public void SetMicrophone(string? id)
    {
        recorder?.Dispose();
        recorder = null;
        Mic.Reset();
        MicId = null;
        if (id is null) return;

        try
        {
            var device = enumerator.GetDevice(id);
            var next = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithFormat(Format)
                .WithBufferLength(CaptureBufferMs)
                .Build();
            next.DataAvailable += (buffer, flags, _, _) =>
            {
                if (flags.HasFlag(AudioClientBufferFlags.Silent)) Mic.WriteSilence(buffer.Length / sizeof(float));
                else Mic.Write(MemoryMarshal.Cast<byte, float>(buffer));
            };
            next.RecordingStopped += (_, e) =>
            {
                if (e.Exception is null) return;
                if (MicId == id) MicId = null;
                Problem?.Invoke($"The microphone stopped: {e.Exception.Message}");
                DeviceLost?.Invoke();
            };
            next.StartRecording();
            recorder = next;
            MicId = id;
        }
        // Windows privacy settings that block microphone access surface as UnauthorizedAccessException.
        catch (Exception e) when (e is COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            Problem?.Invoke($"Couldn't open the microphone: {e.Message}");
        }
    }

    public void SetSend(string? id)
    {
        CloseSend();
        sendPlayer = Open(id, send, "the voice chat output", () =>
        {
            if (SendId == id) CloseSend();
        });
        SendId = sendPlayer is null ? null : id;
    }

    public void SetMonitor(string? id)
    {
        CloseMonitor();
        monitorPlayer = Open(id, monitorSounds, "your headphones", () =>
        {
            if (MonitorId == id) CloseMonitor();
        });
        MonitorId = monitorPlayer is null ? null : id;
    }

    /// <summary>Plays a clip to the voice chat and to the user, on whichever of them is open.</summary>
    public Playback Play(SoundClip clip, Func<float> clipVolume)
    {
        var voices = new List<ClipVoice>(2);
        if (sendPlayer is not null) voices.Add(Start(sendSounds, new ClipVoice(clip, () => clipVolume() * SoundsVolume)));
        if (monitorPlayer is not null) voices.Add(Start(monitorSounds, new ClipVoice(clip, () => clipVolume() * MonitorVolume)));
        return new Playback(clip, [.. voices]);
    }

    /// <summary>Plays a clip to the user only.</summary>
    public Playback Preview(SoundClip clip) => monitorPlayer is null
        ? new Playback(clip)
        : new Playback(clip, Start(monitorSounds, new ClipVoice(clip, () => MonitorVolume)));

    /// <summary>Plays a clip to the voice chat only.</summary>
    public Playback PlayToSend(SoundClip clip) => sendPlayer is null
        ? new Playback(clip)
        : new Playback(clip, Start(sendSounds, new ClipVoice(clip, () => SoundsVolume)));

    public void Dispose()
    {
        recorder?.Dispose();
        sendPlayer?.Dispose();
        monitorPlayer?.Dispose();
        Notifications.Dispose();
        enumerator.Dispose();
    }

    static ClipVoice Start(MixingSampleProvider mixer, ClipVoice voice)
    {
        mixer.AddMixerInput(voice);
        return voice;
    }

    void CloseSend()
    {
        sendPlayer?.Dispose();
        sendPlayer = null;
        DropAll(sendSounds);
        SendId = null;
    }

    void CloseMonitor()
    {
        monitorPlayer?.Dispose();
        monitorPlayer = null;
        DropAll(monitorSounds);
        MonitorId = null;
    }

    // Only called after the mixer's player is disposed, so no render thread is reading the inputs.
    static void DropAll(MixingSampleProvider mixer)
    {
        foreach (var voice in mixer.MixerInputs.OfType<ClipVoice>().ToList()) voice.Drop();
        mixer.RemoveAllMixerInputs();
    }

    /// <remarks>
    /// When a stream dies, for example because the device was unplugged, onFailure closes it, so
    /// the next refresh opens the device again instead of treating it as already open.
    /// </remarks>
    WasapiPlayer? Open(string? id, ISampleProvider source, string role, Action onFailure)
    {
        if (id is null) return null;
        try
        {
            var device = enumerator.GetDevice(id);
            var player = new WasapiPlayerBuilder().WithDevice(device).WithLatency(OutputLatencyMs).Build();
            player.Init(source);
            player.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is null) return;
                onFailure();
                Problem?.Invoke($"Playback to {role} stopped: {e.Exception.Message}");
                DeviceLost?.Invoke();
            };
            player.Play();
            return player;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or NotSupportedException)
        {
            Problem?.Invoke($"Couldn't open {role}: {e.Message}");
            return null;
        }
    }

    List<AudioDevice> Devices(DataFlow flow)
    {
        var devices = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            devices.Add(new AudioDevice(device.ID, device.FriendlyName));
            device.Dispose();
        }
        return devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // The communications role is the headset voice chat apps use, which is where the user expects
    // to hear sounds; the plain default is often the speakers.
    string? DefaultId(DataFlow flow)
    {
        if (!enumerator.TryGetDefaultAudioEndpoint(flow, Role.Communications, out var device) || device is null) return null;
        using (device) return device.ID;
    }
}
