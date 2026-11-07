using System.IO;
using System.Windows;

namespace Blurt;

public partial class App : Application
{
    Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A second copy could not register the hotkeys the first one holds.
        instance = new Mutex(true, @"Local\Blurt.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Blurt is already running.", "Blurt", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        try
        {
            new MainWindow().Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"Blurt can't write to its folder:\n{AppContext.BaseDirectory}\n\n{ex.Message}\n\n"
                + "Blurt keeps your sounds and settings next to Blurt.exe. Move the Blurt folder somewhere you can write to, such as Documents, and start it again.",
                "Blurt", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
