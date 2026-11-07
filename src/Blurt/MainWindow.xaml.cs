using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Blurt.Audio;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace Blurt;

public partial class MainWindow : Window
{
    const string CableDownloadUrl = "https://vb-audio.com/Cable/";
    const string CableOutputName = "CABLE Output (VB-Audio Virtual Cable)";
    const int MeterSegments = 14;
    const int MaxLockedRetries = 25;
    const double MinPadWidth = 184;
    const double PadGap = 16;

    public static readonly DependencyProperty PadColumnsProperty =
        DependencyProperty.Register(nameof(PadColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(4));

    static readonly AudioDevice SendOff = new("", "Off");

    /// <summary>Copied from the free sounds on first start, so the board does not open empty.</summary>
    static readonly string[] StarterSounds = ["airhorn", "rimshot", "sad-trombone", "drumroll", "tada", "crickets"];

    readonly string soundsFolder = Path.Combine(AppContext.BaseDirectory, "sounds");
    readonly string galleryFolder = Path.Combine(AppContext.BaseDirectory, "gallery");
    readonly string settingsPath = Path.Combine(AppContext.BaseDirectory, "blurt.json");
    readonly Settings settings;
    readonly bool firstRun;
    readonly AudioEngine engine = new();
    readonly ObservableCollection<SoundItem> sounds = [];
    readonly ObservableCollection<GalleryItem> gallery = [];
    readonly ICollectionView soundsView;
    readonly ICollectionView galleryView;
    readonly Dictionary<string, DateTime> loadedVersions = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> failedVersions = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<GalleryItem> previewsLoading = [];
    readonly List<Rectangle> meter = [];
    readonly DispatcherTimer reloadTimer;
    readonly DispatcherTimer deviceTimer;
    readonly DispatcherTimer toastTimer;
    readonly DispatcherTimer frameTimer;
    readonly FileSystemWatcher watcher;
    GlobalHotkeys? hotkeys;
    SoundClip? testChime;
    Border? openSheet;
    string? sendName;
    bool cableFound;
    bool updatingDevices;
    bool loadingBoard;
    bool reloadPending;
    bool capturingKey;
    int lockedRetries;
    bool anyPlaying;
    double meterLevel;
    int litSegments = -1;

    public MainWindow()
    {
        Directory.CreateDirectory(soundsFolder);
        // Seeding, settings and added sounds all write here, so an unwritable folder fails now, not mid-use.
        using (File.Create(Path.Combine(AppContext.BaseDirectory, "blurt.write-test"), 1, FileOptions.DeleteOnClose)) { }
        var loaded = Settings.Load(settingsPath);
        firstRun = loaded is null;
        settings = loaded ?? new Settings();

        InitializeComponent();

        engine.SoundsVolume = settings.SoundsVolume;
        engine.MonitorVolume = settings.MonitorVolume;
        engine.Mic.Volume = settings.MicrophoneVolume;
        engine.Mic.Muted = !settings.MicrophoneOn;
        engine.Problem += message => Dispatcher.BeginInvoke(() =>
        {
            ShowToast(message, warning: true);
            UpdateLamp();
        });

        MicToggle.IsChecked = settings.MicrophoneOn;
        MonitorToggle.IsChecked = settings.MonitorOn;
        MonitorToggle.Content = settings.MonitorOn ? "On" : "Off";
        MicVolume.Value = settings.MicrophoneVolume;
        SoundsVolume.Value = settings.SoundsVolume;
        MonitorVolume.Value = settings.MonitorVolume;
        FolderPath.Text = soundsFolder;
        BuildMeter();

        soundsView = CollectionViewSource.GetDefaultView(sounds);
        soundsView.Filter = item => Matches(SearchBox, ((SoundItem)item).Name);
        galleryView = CollectionViewSource.GetDefaultView(gallery);
        galleryView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GalleryItem.Category)));
        galleryView.Filter = item => Matches(FreeSearch, ((GalleryItem)item).Name);
        Pads.ItemsSource = soundsView;
        FreeSounds.ItemsSource = galleryView;

        reloadTimer = OneShot(TimeSpan.FromMilliseconds(400), () => _ = LoadBoardAsync());
        deviceTimer = OneShot(TimeSpan.FromMilliseconds(300), RefreshDevices);
        toastTimer = OneShot(TimeSpan.FromSeconds(5), () => Toast.Visibility = Visibility.Collapsed);
        frameTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => OnFrame(), Dispatcher);
        engine.Notifications.DeviceAdded += (_, _) => Restart(deviceTimer);
        engine.Notifications.DeviceRemoved += (_, _) => Restart(deviceTimer);
        engine.Notifications.DeviceStateChanged += (_, _) => Restart(deviceTimer);
        engine.Notifications.DefaultDeviceChanged += (_, _) => Restart(deviceTimer);
        engine.DeviceLost += () => Dispatcher.BeginInvoke(() => Restart(deviceTimer));

        watcher = new FileSystemWatcher(soundsFolder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() => Restart(reloadTimer));
        watcher.Created += changed;
        watcher.Changed += changed;
        watcher.Deleted += changed;
        watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => Restart(reloadTimer));
        watcher.EnableRaisingEvents = true;

        SourceInitialized += (_, _) =>
        {
            hotkeys = new GlobalHotkeys(this);
            RegisterHotkeys();
        };
        Loaded += async (_, _) => await StartAsync();
        Closing += (_, _) => Shutdown();

        RefreshDevices();
    }

    /// <summary>Carts fill the board's width, so the last column never leaves a ragged gap.</summary>
    public int PadColumns
    {
        get => (int)GetValue(PadColumnsProperty);
        set => SetValue(PadColumnsProperty, value);
    }

    async Task StartAsync()
    {
        LoadGallery();
        if (firstRun) SeedStarterSounds();
        await LoadBoardAsync();
        if (!settings.SetupDone && engine.SendId is null) OpenSheet(ConnectSheet);
    }

    void Shutdown()
    {
        StopAll();
        SaveSettings();
        frameTimer.Stop();
        watcher.Dispose();
        hotkeys?.Dispose();
        engine.Dispose();
    }

    // Pads

    async Task LoadBoardAsync()
    {
        if (loadingBoard)
        {
            reloadPending = true;
            return;
        }
        loadingBoard = true;
        try
        {
            do
            {
                reloadPending = false;
                await SyncBoardAsync();
            }
            while (reloadPending);
        }
        finally
        {
            loadingBoard = false;
        }
    }

    async Task SyncBoardAsync()
    {
        var files = Directory.EnumerateFiles(soundsFolder)
            .Where(SoundClip.IsSupported)
            .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var current = sounds.ToDictionary(s => s.Clip.Path, StringComparer.OrdinalIgnoreCase);
        var next = new List<SoundItem>();
        var failures = new List<string>();
        bool retry = false;

        foreach (var path in files)
        {
            var version = File.GetLastWriteTimeUtc(path);
            current.TryGetValue(path, out var existing);
            if (existing is not null && loadedVersions.GetValueOrDefault(path) == version)
            {
                next.Add(existing);
                continue;
            }
            // A file that failed to load stays off the board, without another message, until it changes.
            if (failedVersions.GetValueOrDefault(path) == version) continue;
            try
            {
                var clip = await Task.Run(() => SoundClip.Load(path, AudioEngine.Format));
                var key = Path.GetFileName(path);
                if (!settings.Sounds.TryGetValue(key, out var soundSettings))
                {
                    soundSettings = new SoundSettings();
                    settings.Sounds[key] = soundSettings;
                }
                existing?.Playback?.Stop();
                next.Add(new SoundItem(clip, soundSettings));
                loadedVersions[path] = version;
                failedVersions.Remove(path);
            }
            catch (IOException e) when (IsTransient(e))
            {
                // Usually a file that is still being copied in, or one deleted since the folder was listed.
                retry = true;
            }
            catch (Exception e) when (e is IOException || IsLoadError(e))
            {
                failedVersions[path] = version;
                failures.Add($"{Path.GetFileName(path)}: {e.Message}");
            }
        }
        foreach (var path in failedVersions.Keys.Except(files, StringComparer.OrdinalIgnoreCase).ToList())
        {
            failedVersions.Remove(path);
        }

        foreach (var gone in sounds.Except(next))
        {
            gone.Playback?.Stop();
            loadedVersions.Remove(gone.Clip.Path);
        }
        if (!sounds.SequenceEqual(next))
        {
            sounds.Clear();
            foreach (var item in next) sounds.Add(item);
        }

        RegisterHotkeys();
        UpdateBoardState();
        UpdateFreeSounds();
        SaveSettings();
        // The watcher reports a copied file again when its writer finishes, so retries are capped
        // for files that some other app keeps locked.
        lockedRetries = retry ? lockedRetries + 1 : 0;
        if (retry && lockedRetries <= MaxLockedRetries) Restart(reloadTimer);
        if (failures.Count > 0)
        {
            var more = failures.Count > 1 ? $" {failures.Count - 1} more files didn't load either." : "";
            ShowToast($"Couldn't load {failures[0]}.{more}", warning: true);
        }
    }

    void UpdateBoardState()
    {
        SearchHint.Text = sounds.Count switch { 0 or 1 => "Search sounds", var n => $"Search {n} sounds" };
        bool nothingShown = soundsView.IsEmpty;
        bool searchMiss = sounds.Count > 0 && nothingShown;
        EmptyState.Visibility = nothingShown ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = searchMiss ? "No sound with that name" : "No sounds yet";
        EmptyBody.Text = searchMiss
            ? "Clear the search to see all your sounds, or add a new one."
            : "Add free sounds or your own files. You can also drop audio files anywhere on this window.";
    }

    void Toggle(SoundItem item)
    {
        if (item.Playback is { } playing)
        {
            playing.Stop();
            item.Playback = null;
            return;
        }
        item.Playback = engine.Play(item.Clip, () => (float)item.Volume);
        if (engine.SendId is null && engine.MonitorId is null)
        {
            ShowToast("Nothing can hear this yet. Set up VB-CABLE or turn on Headphones.", warning: true);
        }
    }

    void StopAll()
    {
        foreach (var item in sounds.Cast<PlayableItem>().Concat(gallery))
        {
            item.Playback?.Stop();
            item.Playback = null;
        }
    }

    void AddFiles(IReadOnlyCollection<string> paths)
    {
        int added = 0;
        int skipped = 0;
        foreach (var path in paths)
        {
            if (!SoundClip.IsSupported(path) || !File.Exists(path))
            {
                skipped++;
                continue;
            }
            if (string.Equals(Path.GetDirectoryName(path), soundsFolder, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                File.Copy(path, UniqueSoundPath(Path.GetFileName(path)));
                added++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ShowToast($"Couldn't copy {Path.GetFileName(path)}: {e.Message}", warning: true);
                return;
            }
        }

        var message = added switch { 0 => "", 1 => "Added 1 sound.", var n => $"Added {n} sounds." };
        if (skipped > 0) message += $" Skipped {skipped} file{(skipped == 1 ? "" : "s")} that Blurt can't play.";
        if (message.Length > 0) ShowToast(message.Trim(), warning: added == 0);
        _ = LoadBoardAsync();
    }

    string UniqueSoundPath(string fileName)
    {
        var path = Path.Combine(soundsFolder, fileName);
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(soundsFolder, $"{name} ({n}){extension}");
        return path;
    }

    // Free sounds

    void LoadGallery()
    {
        if (!Directory.Exists(galleryFolder)) return;
        foreach (var folder in Directory.EnumerateDirectories(galleryFolder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var category = GalleryItem.Pretty(Path.GetFileName(folder));
            foreach (var file in Directory.EnumerateFiles(folder).Where(SoundClip.IsSupported).Order(StringComparer.OrdinalIgnoreCase))
            {
                gallery.Add(new GalleryItem(file, category));
            }
        }
        UpdateFreeSounds();
        _ = ReadGalleryLengthsAsync();
    }

    async Task ReadGalleryLengthsAsync()
    {
        foreach (var item in gallery.ToList())
        {
            try
            {
                item.Duration = await Task.Run(() => SoundClip.ReadDuration(item.Path));
            }
            catch (Exception e) when (e is IOException || IsLoadError(e))
            {
                gallery.Remove(item);
            }
        }
    }

    void SeedStarterSounds()
    {
        foreach (var item in gallery.Where(g => StarterSounds.Contains(Path.GetFileNameWithoutExtension(g.Path))))
        {
            var target = Path.Combine(soundsFolder, item.BoardFileName);
            if (!File.Exists(target)) File.Copy(item.Path, target);
        }
    }

    void UpdateFreeSounds()
    {
        foreach (var item in gallery) item.OnBoard = File.Exists(Path.Combine(soundsFolder, item.BoardFileName));
        FreeEmpty.Visibility = galleryView.IsEmpty && gallery.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void StopPreviews()
    {
        foreach (var item in gallery)
        {
            item.Playback?.Stop();
            item.Playback = null;
        }
    }

    // Keys

    void RegisterHotkeys()
    {
        // While the Set key dialog is open, keys stay released so they reach the dialog.
        if (hotkeys is null || capturingKey) return;
        hotkeys.Clear();

        var stopAll = settings.StopAllHotkey;
        bool stopTaken = stopAll is { } key && !hotkeys.Register(key, StopAll);
        StopAllLegend.Text = stopAll is null ? "No key" : stopTaken ? $"{stopAll} is taken" : stopAll.ToString();
        StopAllLegend.Foreground = Brush(stopTaken ? "Redline" : "Pencil");
        StopAllKeyText.Text = stopAll?.ToString() ?? "None";

        foreach (var item in sounds)
        {
            item.HotkeyTaken = item.Hotkey is { } hotkey && !hotkeys.Register(hotkey, () => Toggle(item));
        }
    }

    /// <summary>
    /// Changed is false when the user cancels. Global keys are released while the dialog is open,
    /// so pressing a combination already in use reaches the dialog instead of playing a sound.
    /// </summary>
    (Hotkey? Value, bool Changed) AskHotkey(string target, Hotkey? current)
    {
        capturingKey = true;
        hotkeys?.Clear();
        var dialog = new HotkeyWindow(target, current) { Owner = this };
        bool saved;
        try
        {
            saved = dialog.ShowDialog() == true;
        }
        finally
        {
            capturingKey = false;
        }
        RegisterHotkeys();
        return (dialog.Result, saved);
    }

    void AssignHotkey(SoundItem? sound, Hotkey? hotkey)
    {
        if (hotkey is { } key)
        {
            foreach (var other in sounds.Where(s => s != sound && s.Hotkey == key))
            {
                other.Hotkey = null;
                ShowToast(sound is null
                    ? $"{key} now stops all sounds instead of playing {other.Name}."
                    : $"{key} now plays {sound.Name} instead of {other.Name}.");
            }
            if (sound is not null && settings.StopAllHotkey == key)
            {
                settings.StopAllHotkey = null;
                ShowToast($"{key} now plays {sound.Name}. Stop all has no key.");
            }
            // Sounds not on the board right now keep their saved keys, which would collide when they return.
            var onBoard = sounds.Select(s => s.Settings).ToHashSet();
            foreach (var saved in settings.Sounds.Values.Where(s => s.Hotkey == key && !onBoard.Contains(s)))
            {
                saved.Hotkey = null;
            }
        }
        if (sound is null) settings.StopAllHotkey = hotkey;
        else sound.Hotkey = hotkey;
        RegisterHotkeys();
        SaveSettings();
    }

    // Devices and the on-air lamp

    void RefreshDevices()
    {
        var microphones = engine.Microphones();
        var outputs = engine.Outputs();
        var cable = outputs.FirstOrDefault(AudioEngine.IsCableInput);

        string? micId = Available(settings.MicrophoneId, microphones) ?? engine.DefaultMicrophoneId();
        string? sendId = settings.SendId == SendOff.Id ? null : Available(settings.SendId, outputs) ?? cable?.Id;
        string? monitorId = Available(settings.MonitorId, outputs) ?? engine.DefaultMonitorId();
        string? monitorTarget = settings.MonitorOn ? monitorId : null;

        if (micId != engine.MicId) engine.SetMicrophone(micId);
        if (sendId != engine.SendId) engine.SetSend(sendId);
        if (monitorTarget != engine.MonitorId) engine.SetMonitor(monitorTarget);

        updatingDevices = true;
        MicCombo.ItemsSource = microphones;
        MicCombo.SelectedItem = microphones.FirstOrDefault(d => d.Id == micId);
        var sendChoices = outputs.Prepend(SendOff).ToList();
        SendCombo.ItemsSource = sendChoices;
        SendCombo.SelectedItem = sendChoices.FirstOrDefault(d => d.Id == (sendId ?? SendOff.Id));
        MonitorCombo.ItemsSource = outputs;
        MonitorCombo.SelectedItem = outputs.FirstOrDefault(d => d.Id == monitorId);
        updatingDevices = false;

        sendName = outputs.FirstOrDefault(d => d.Id == engine.SendId)?.Name;
        cableFound = cable is not null;
        UpdateMicLabel();
        UpdateLamp();
    }

    void UpdateLamp()
    {
        bool live = engine.SendId is not null;
        bool turnedOff = settings.SendId == SendOff.Id;
        Lamp.Background = live ? Brush("Redline") : Brush("Paper");
        LampDot.Fill = live ? Brushes.White : Brush("LedOff");
        LampText.Text = live ? "On air" : "Off air";
        LampText.Foreground = live ? Brushes.White : Brush("Pencil");
        StatusText.Text = live
            ? sendName is not null && AudioEngine.IsCableInput(new AudioDevice("", sendName))
                ? "Your mic and sounds go out through VB-CABLE"
                : $"Your mic and sounds go out through {sendName}"
            : turnedOff ? "Sending is turned off in Settings"
            : cableFound ? "VB-CABLE didn't open. Try Settings."
            : "Install VB-CABLE to reach your voice chat";
        ConnectButton.Visibility = live || turnedOff ? Visibility.Collapsed : Visibility.Visible;

        Step1Badge.Background = cableFound ? Brush("Ink") : Brushes.Transparent;
        Step1Badge.BorderBrush = cableFound ? Brush("Ink") : Brush("Pencil");
        Step1Mark.Text = cableFound ? "" : "1";
        Step1Mark.FontFamily = (FontFamily)FindResource(cableFound ? "IconFont" : "UiFont");
        Step1Mark.Foreground = cableFound ? Brush("Paper") : Brush("Ink");
        CableLed.Fill = Brush(cableFound ? "Ink" : "LedOff");
        CableStatus.Text = cableFound ? "VB-CABLE found" : "Waiting for VB-CABLE. Blurt notices it by itself.";
        DownloadButton.Visibility = cableFound ? Visibility.Collapsed : Visibility.Visible;
        TestButton.IsEnabled = live;
    }

    void UpdateMicLabel()
    {
        MicToggle.Content = engine.MicId is null ? "No mic" : settings.MicrophoneOn ? "On" : "Muted";
    }

    static string? Available(string? id, IReadOnlyList<AudioDevice> devices) =>
        id is not null && devices.Any(d => d.Id == id) ? id : null;

    // Mic meter

    void BuildMeter()
    {
        for (int i = 0; i < MeterSegments; i++)
        {
            var segment = new Rectangle
            {
                Width = 2,
                Height = 16,
                Margin = new Thickness(0, 0, 4, 0),
            };
            meter.Add(segment);
            Meter.Children.Add(segment);
        }
        PaintMeter(0);
    }

    void PaintMeter(int lit)
    {
        if (lit == litSegments) return;
        litSegments = lit;
        for (int i = 0; i < meter.Count; i++)
        {
            // The top two segments warn that the mic is close to clipping.
            meter[i].Fill = Brush(i >= lit ? "LedOff" : i >= MeterSegments - 2 ? "Redline" : "Ink");
        }
    }

    void OnFrame()
    {
        foreach (var item in sounds) item.Tick();
        foreach (var item in gallery) item.Tick();
        ShowStopAllArmed(sounds.Any(s => s.IsPlaying));

        double peak = engine.Mic.Muted || engine.MicId is null ? 0 : engine.Mic.Peak * engine.Mic.Volume;
        double level = peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 54) / 54, 0, 1);
        meterLevel = Math.Max(level, meterLevel - 0.035);
        PaintMeter((int)Math.Round(meterLevel * MeterSegments));
    }

    /// <summary>Stop all lights up tally red while any sound on the board is playing.</summary>
    void ShowStopAllArmed(bool playing)
    {
        if (playing == anyPlaying) return;
        anyPlaying = playing;
        StopAllButton.Background = Brush(playing ? "Redline" : "Paper");
        StopAllButton.BorderBrush = Brush(playing ? "RedlineDeep" : "Ink");
        StopAllLegend.Foreground = playing ? Brushes.White : Brush("Pencil");
        StopAllGlyph.Fill = playing ? Brushes.White : Brush("Ink");
        StopAllButton.Foreground = playing ? Brushes.White : Brush("Ink");
    }

    // Side panels

    void OpenSheet(Border sheet)
    {
        if (openSheet == sheet) return;
        if (openSheet is not null) openSheet.Visibility = Visibility.Collapsed;
        openSheet = sheet;
        SheetLayer.Visibility = Visibility.Visible;
        sheet.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
        {
            var slide = new TranslateTransform(sheet.Width, 0);
            sheet.RenderTransform = slide;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(sheet.Width, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
    }

    void CloseSheet()
    {
        if (openSheet is null) return;
        if (openSheet == AddSheet) StopPreviews();
        openSheet.Visibility = Visibility.Collapsed;
        openSheet = null;
        SheetLayer.Visibility = Visibility.Collapsed;
    }

    // Helpers

    static bool Matches(TextBox? search, string name) =>
        search is null || name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);

    static bool IsTransient(IOException e) => e is FileNotFoundException
        || e.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021); // sharing or lock violation

    static bool IsLoadError(Exception e) => e is InvalidDataException or FormatException or NotSupportedException
        or COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException
        or EndOfStreamException;

    Brush Brush(string key) => (Brush)FindResource(key);

    DispatcherTimer OneShot(TimeSpan interval, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = interval };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    void ShowToast(string message, bool warning = false)
    {
        ToastText.Text = message;
        ToastLed.Visibility = warning ? Visibility.Visible : Visibility.Collapsed;
        Toast.Visibility = Visibility.Visible;
        Restart(toastTimer);
    }

    void SaveSettings()
    {
        try
        {
            settings.Save(settingsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast($"Couldn't save settings: {e.Message}", warning: true);
        }
    }

    static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    static SoundItem? SoundOf(object sender) => (sender as FrameworkElement)?.DataContext as SoundItem;

    /// <summary>A two-note chime for checking that the voice chat app receives the cable.</summary>
    static SoundClip MakeTestChime()
    {
        var format = AudioEngine.Format;
        int frames = format.SampleRate * 7 / 10;
        var samples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / format.SampleRate;
            bool second = t >= 0.16;
            double local = second ? t - 0.16 : t;
            double frequency = second ? 1318.5 : 880;
            double envelope = Math.Min(1, local * 400) * Math.Exp(-local * 7);
            float sample = (float)(0.3 * envelope * Math.Sin(2 * Math.PI * frequency * local));
            samples[2 * i] = sample;
            samples[2 * i + 1] = sample;
        }
        return new SoundClip("Test chime", samples, format);
    }

    // Event handlers

    void Pad_Click(object sender, RoutedEventArgs e)
    {
        if (SoundOf(sender) is { } item) Toggle(item);
    }

    void PadMore_Click(object sender, RoutedEventArgs e)
    {
        var more = (FrameworkElement)sender;
        var pad = ((Panel)more.Parent).Children.OfType<Button>().First(b => b.Name == "Pad");
        var menu = pad.ContextMenu;
        menu.PlacementTarget = more;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void MenuSetKey_Click(object sender, RoutedEventArgs e)
    {
        if (SoundOf(sender) is not { } item) return;
        var (hotkey, changed) = AskHotkey(item.Name, item.Hotkey);
        if (changed) AssignHotkey(item, hotkey);
    }

    void MenuRemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (SoundOf(sender) is { } item) AssignHotkey(item, null);
    }

    void MenuShowFile_Click(object sender, RoutedEventArgs e)
    {
        if (SoundOf(sender) is { } item) Process.Start("explorer.exe", $"/select,\"{item.Clip.Path}\"");
    }

    void MenuRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SoundOf(sender) is not { } item) return;
        item.Playback?.Stop();
        if (RecycleBin.Send(item.Clip.Path, new WindowInteropHelper(this).Handle)) ShowToast($"Removed {item.Name}.");
        else ShowToast($"Kept {item.Name}. It wasn't moved to the Recycle Bin.", warning: true);
        _ = LoadBoardAsync();
    }

    async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GalleryItem item) return;
        if (item.Playback is { } playing)
        {
            playing.Stop();
            item.Playback = null;
            return;
        }
        if (engine.MonitorId is null)
        {
            ShowToast("Turn on Headphones at the bottom to preview sounds.", warning: true);
            return;
        }
        if (!previewsLoading.Add(item)) return;
        try
        {
            // Decoded per preview and dropped when it ends, so the gallery holds no audio in memory.
            var clip = await Task.Run(() => SoundClip.Load(item.Path, AudioEngine.Format));
            if (openSheet == AddSheet && engine.MonitorId is not null) item.Playback = engine.Preview(clip);
        }
        catch (Exception ex) when (ex is IOException || IsLoadError(ex))
        {
            ShowToast($"Couldn't play {item.Name}: {ex.Message}", warning: true);
        }
        finally
        {
            previewsLoading.Remove(item);
        }
    }

    void FreeSoundAdd_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GalleryItem item) return;
        var target = Path.Combine(soundsFolder, item.BoardFileName);
        try
        {
            if (!File.Exists(target)) File.Copy(item.Path, target);
            item.OnBoard = true;
            ShowToast($"Added {item.Name}.");
            _ = LoadBoardAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"Couldn't add {item.Name}: {ex.Message}", warning: true);
        }
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        soundsView.Refresh();
        UpdateBoardState();
    }

    void FreeSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        galleryView.Refresh();
        UpdateFreeSounds();
    }

    void Lamp_Click(object sender, RoutedEventArgs e) => OpenSheet(ConnectSheet);

    void OpenAdd_Click(object sender, RoutedEventArgs e) => OpenSheet(AddSheet);

    void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        RefreshDevices();
        OpenSheet(SettingsSheet);
    }

    void CloseSheet_Click(object sender, RoutedEventArgs e) => CloseSheet();

    void Scrim_MouseDown(object sender, MouseButtonEventArgs e) => CloseSheet();

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && openSheet is not null)
        {
            CloseSheet();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused)
        {
            SearchBox.Clear();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && openSheet is null)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    void Pads_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Each cart carries its gap on the right, so the width per column includes it.
        PadColumns = Math.Max(1, (int)(e.NewSize.Width / (MinPadWidth + PadGap)));
    }

    void ConnectDone_Click(object sender, RoutedEventArgs e)
    {
        settings.SetupDone = true;
        SaveSettings();
        CloseSheet();
    }

    void Download_Click(object sender, RoutedEventArgs e) => Open(CableDownloadUrl);

    async void CopyName_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CableOutputName);
        }
        catch (ExternalException)
        {
            ShowToast("Couldn't copy. Another app is holding the clipboard, so try again.", warning: true);
            return;
        }
        CopyText.Text = "Copied";
        await Task.Delay(2000);
        CopyText.Text = "Copy";
    }

    void TestChime_Click(object sender, RoutedEventArgs e)
    {
        testChime ??= MakeTestChime();
        engine.PlayToSend(testChime);
        ShowToast("Played a chime into the cable. Your voice app's mic meter should have moved.");
    }

    void StopAll_Click(object sender, RoutedEventArgs e) => StopAll();

    void StopAllSetKey_Click(object sender, RoutedEventArgs e)
    {
        var (hotkey, changed) = AskHotkey("Stop all", settings.StopAllHotkey);
        if (changed) AssignHotkey(null, hotkey);
    }

    void StopAllRemoveKey_Click(object sender, RoutedEventArgs e) => AssignHotkey(null, null);

    void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var patterns = string.Join(';', SoundClip.Extensions.Select(x => "*" + x));
        var dialog = new OpenFileDialog
        {
            Title = "Choose sounds",
            Multiselect = true,
            Filter = $"Audio files|{patterns}",
        };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e) => Open(soundsFolder);

    void MicToggle_Click(object sender, RoutedEventArgs e)
    {
        settings.MicrophoneOn = MicToggle.IsChecked == true;
        engine.Mic.Muted = !settings.MicrophoneOn;
        UpdateMicLabel();
        SaveSettings();
    }

    void MonitorToggle_Click(object sender, RoutedEventArgs e)
    {
        settings.MonitorOn = MonitorToggle.IsChecked == true;
        MonitorToggle.Content = settings.MonitorOn ? "On" : "Off";
        if (!settings.MonitorOn) StopPreviews();
        RefreshDevices();
        SaveSettings();
    }

    void MicVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        settings.MicrophoneVolume = (float)e.NewValue;
        engine.Mic.Volume = (float)e.NewValue;
    }

    void SoundsVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        settings.SoundsVolume = (float)e.NewValue;
        engine.SoundsVolume = (float)e.NewValue;
    }

    void MonitorVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        settings.MonitorVolume = (float)e.NewValue;
        engine.MonitorVolume = (float)e.NewValue;
    }

    void MicCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingDevices || MicCombo.SelectedItem is not AudioDevice device) return;
        settings.MicrophoneId = device.Id;
        RefreshDevices();
        SaveSettings();
    }

    void SendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingDevices || SendCombo.SelectedItem is not AudioDevice device) return;
        settings.SendId = device.Id;
        RefreshDevices();
        SaveSettings();
    }

    void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingDevices || MonitorCombo.SelectedItem is not AudioDevice device) return;
        settings.MonitorId = device.Id;
        RefreshDevices();
        SaveSettings();
    }

    void Window_DragEnter(object sender, DragEventArgs e)
    {
        bool audio = DroppedFiles(e).Any(SoundClip.IsSupported);
        e.Effects = audio ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFiles(e).Any(SoundClip.IsSupported) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when the pointer moves between child elements.
        var position = e.GetPosition(this);
        if (position.X <= 0 || position.Y <= 0 || position.X >= ActualWidth || position.Y >= ActualHeight)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        CloseSheet();
        AddFiles(DroppedFiles(e));
    }

    static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
}
