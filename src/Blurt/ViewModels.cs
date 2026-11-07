using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Blurt.Audio;

namespace Blurt;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Shared by board and gallery items: something that can be playing.</summary>
public abstract class PlayableItem : Observable
{
    Playback? playback;
    double progress;
    string countdown = "";

    public Playback? Playback
    {
        get => playback;
        set
        {
            playback = value;
            Progress = 0;
            Countdown = value is null ? "" : FormatLength(value.Clip.Duration);
            Raise();
            Raise(nameof(IsPlaying));
        }
    }

    public bool IsPlaying => playback is not null;

    public double Progress
    {
        get => progress;
        private set => Set(ref progress, value);
    }

    /// <summary>Time left while playing, counting down like a radio jingle cart.</summary>
    public string Countdown
    {
        get => countdown;
        private set => Set(ref countdown, value);
    }

    /// <summary>Called on every UI frame; clears the playback once it ends.</summary>
    public void Tick()
    {
        if (playback is null) return;
        if (playback.IsFinished)
        {
            Playback = null;
            return;
        }
        Progress = playback.Progress;
        Countdown = FormatLength(playback.Clip.Duration * (1 - Progress));
    }

    public static string FormatLength(TimeSpan length) => length.TotalSeconds < 10
        ? length.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture) + " s"
        : $"{(int)length.TotalMinutes}:{length.Seconds:00}";
}

public sealed class SoundItem(SoundClip clip, SoundSettings settings) : PlayableItem
{
    bool hotkeyTaken;

    public SoundClip Clip { get; } = clip;

    public SoundSettings Settings => settings;

    public string Name => Clip.Name;

    public string Length => FormatLength(Clip.Duration);

    public double Volume
    {
        get => settings.Volume;
        set
        {
            settings.Volume = (float)value;
            Raise();
        }
    }

    public Hotkey? Hotkey
    {
        get => settings.Hotkey;
        set
        {
            settings.Hotkey = value;
            Raise();
            Raise(nameof(HotkeyText));
        }
    }

    public string HotkeyText => settings.Hotkey?.ToString() ?? "";

    /// <summary>Another application owns the combination, so it does nothing in Blurt.</summary>
    public bool HotkeyTaken
    {
        get => hotkeyTaken;
        set => Set(ref hotkeyTaken, value);
    }
}

public sealed class GalleryItem(string path, string category) : PlayableItem
{
    TimeSpan? duration;
    bool onBoard;

    public string Path { get; } = path;

    public string Category { get; } = category;

    /// <summary>"sad-trombone.ogg" reads as "Sad trombone".</summary>
    public string Name { get; } = Pretty(System.IO.Path.GetFileNameWithoutExtension(path));

    public string BoardFileName => Name + System.IO.Path.GetExtension(Path);

    /// <summary>Read from the file header; the audio itself is only decoded for a preview.</summary>
    public TimeSpan? Duration
    {
        get => duration;
        set
        {
            duration = value;
            Raise();
            Raise(nameof(Length));
        }
    }

    public string Length => duration is { } length ? FormatLength(length) : "";

    public bool OnBoard
    {
        get => onBoard;
        set => Set(ref onBoard, value);
    }

    public static string Pretty(string slug)
    {
        var words = slug.Replace('-', ' ').Replace('_', ' ').Trim();
        return words.Length == 0 ? slug : char.ToUpper(words[0], CultureInfo.CurrentCulture) + words[1..];
    }
}
