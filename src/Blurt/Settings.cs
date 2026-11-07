using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Blurt;

public sealed class SoundSettings
{
    public float Volume { get; set; } = 1f;

    public Hotkey? Hotkey { get; set; }
}

public sealed class Settings
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string? MicrophoneId { get; set; }

    public string? SendId { get; set; }

    public string? MonitorId { get; set; }

    public bool MicrophoneOn { get; set; } = true;

    public bool MonitorOn { get; set; } = true;

    public float MicrophoneVolume { get; set; } = 1f;

    public float SoundsVolume { get; set; } = 0.8f;

    public float MonitorVolume { get; set; } = 0.5f;

    public bool SetupDone { get; set; }

    public Hotkey? StopAllHotkey { get; set; } = new Hotkey(ModifierKeys.Control | ModifierKeys.Alt, Key.Back);

    /// <summary>Per-sound settings, keyed by file name in the sounds folder.</summary>
    public Dictionary<string, SoundSettings> Sounds { get; set; } = [];

    /// <summary>
    /// Returns null when the file does not exist yet. A file that cannot be parsed is kept beside
    /// it as .bad, so saving the defaults does not destroy the user's hotkeys.
    /// </summary>
    public static Settings? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".bad", overwrite: true);
            return new Settings();
        }
    }

    public void Save(string path)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, overwrite: true);
    }
}
