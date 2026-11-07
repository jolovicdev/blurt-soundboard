using System.IO;
using System.Windows.Input;

namespace Blurt.Tests;

public sealed class SettingsTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("blurt-settings-").FullName;

    string PathFor(string name) => Path.Combine(folder, name);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void MissingFileMeansFirstRun() => Assert.Null(Settings.Load(PathFor("blurt.json")));

    [Fact]
    public void KeepsHotkeysAndVolumesAcrossARestart()
    {
        var path = PathFor("blurt.json");
        var saved = new Settings { SendId = "", MonitorOn = false, StopAllHotkey = null };
        saved.Sounds["Airhorn.ogg"] = new SoundSettings { Volume = 0.4f, Hotkey = new Hotkey(ModifierKeys.Control | ModifierKeys.Shift, Key.F5) };
        saved.Save(path);

        var loaded = Settings.Load(path)!;

        Assert.Equal("", loaded.SendId);
        Assert.False(loaded.MonitorOn);
        Assert.Null(loaded.StopAllHotkey);
        Assert.Equal(0.4f, loaded.Sounds["Airhorn.ogg"].Volume);
        Assert.Equal(new Hotkey(ModifierKeys.Control | ModifierKeys.Shift, Key.F5), loaded.Sounds["Airhorn.ogg"].Hotkey);
        Assert.Contains("\"Control, Shift\"", File.ReadAllText(path));
    }

    [Fact]
    public void ABrokenFileIsKeptAsideInsteadOfOverwritten()
    {
        var path = PathFor("blurt.json");
        File.WriteAllText(path, "{ \"Sounds\": { oops");

        var loaded = Settings.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal("{ \"Sounds\": { oops", File.ReadAllText(path + ".bad"));
    }
}
