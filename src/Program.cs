using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ROInstances;

public static class Branding
{
    public const string AppName = "RO Eternal Love Launcher";
    public const string GameName = "Ragnarok M: Eternal Love";
    public static readonly BitmapFrame Icon = LoadIcon();
    static Stream OpenIcon() => typeof(Branding).Assembly.GetManifestResourceStream("EternalLove.ico")
        ?? throw new InvalidOperationException("The Eternal Love icon is missing.");
    static BitmapFrame LoadIcon()
    {
        using var stream = OpenIcon();
        var icon = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        icon.Freeze();
        return icon;
    }
    public static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = OpenIcon();
        using var icon = new System.Drawing.Icon(stream, 32, 32);
        return (System.Drawing.Icon)icon.Clone();
    }
}

public sealed class Profile
{
    public int Slot { get; set; }
    public string Name { get; set; } = "";
    public string Account { get; set; } = "";
    public string Server { get; set; } = "";
    public string ProcessName => Slot == 1 ? "ro_win" : "ro_win" + (Slot - 1);
    public string Title => $"{Branding.GameName} - Instance {Slot}" + (string.IsNullOrWhiteSpace(Name) ? "" : " | " + Name);
}

public sealed class Settings
{
    public bool CheckForUpdates { get; set; } = true;
    public string GameFolder { get; set; } = @"C:\Program Files (x86)\XD\ROLauncher";
    public List<Profile> Profiles { get; set; } = new();
}

public static class Store
{
    public static readonly string Root = Environment.GetCommandLineArgs().Contains("--self-test")
        ? Path.Combine(Path.GetTempPath(), "ROInstanceLaunchers-SelfTest")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ROInstanceLaunchers");
    public static readonly string FilePath = Path.Combine(Root, "profiles.json");
    public static Settings Load(string? filePath = null)
    {
        var target = filePath ?? FilePath;
        if (!File.Exists(target)) return new Settings { Profiles = Enumerable.Range(1,4).Select(i => new Profile { Slot = i }).ToList() };
        var value = JsonSerializer.Deserialize<Settings>(File.ReadAllText(target)) ?? throw new InvalidDataException("The profile file is empty.");
        Validate(value);
        return value;
    }
    public static void Validate(Settings settings)
    {
        if (settings.Profiles.Any(p => p.Slot < 1 || p.Slot > 12) || settings.Profiles.Select(p => p.Slot).Distinct().Count() != settings.Profiles.Count)
            throw new InvalidDataException("Profiles must have unique instance numbers from 1 to 12.");
        if (settings.Profiles.Any(p => p.Name == null || p.Account == null || p.Server == null))
            throw new InvalidDataException("A profile contains an invalid empty field.");
        if (!Path.IsPathFullyQualified(settings.GameFolder)) throw new InvalidDataException("The game folder must be an absolute path.");
    }
    public static void Save(Settings settings, string? filePath = null)
    {
        Validate(settings);
        var target = filePath ?? FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(target)) File.Replace(temporary, target, target + ".bak");
        else File.Move(temporary, target);
    }
}

public static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SetWindowText(IntPtr window, string text);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)] public struct Memory { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, Extended; }
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern bool GlobalMemoryStatusEx(ref Memory memory);
    static long previousIdle, previousKernel, previousUser;
    public static (double Cpu, Memory Memory) ReadUsage()
    {
        GetSystemTimes(out var idle, out var kernel, out var user);
        var total = kernel - previousKernel + user - previousUser;
        var cpu = previousKernel == 0 || total <= 0 ? double.NaN : Math.Clamp(100.0 * (total - idle + previousIdle) / total, 0, 100);
        previousIdle = idle; previousKernel = kernel; previousUser = user;
        var memory = new Memory { Length = (uint)Marshal.SizeOf<Memory>() };
        GlobalMemoryStatusEx(ref memory);
        return (cpu, memory);
    }
}

public static class Game
{
    public static List<Process> Find(Profile profile, string folder)
    {
        var expected = Path.GetFullPath(Path.Combine(folder, profile.ProcessName + ".exe"));
        var found = new List<Process>();
        foreach (var p in Process.GetProcessesByName(profile.ProcessName))
        {
            try { if (string.Equals(p.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) { found.Add(p); continue; } }
            catch { }
            p.Dispose();
        }
        return found;
    }
    public static void EnsureFiles(Profile profile, string folder)
    {
        var root = Path.GetFullPath(folder);
        var original = Path.Combine(root, "ro_win.exe");
        var shared = Path.Combine(root, "ro_win_Data");
        if (!File.Exists(original) || !Directory.Exists(shared)) throw new IOException("Choose your Ragnarok M: Eternal Love folder containing ro_win.exe and ro_win_Data.");
        if (profile.Slot == 1) return;
        var executable = Path.Combine(root, profile.ProcessName + ".exe");
        var data = Path.Combine(root, profile.ProcessName + "_Data");
        if (Directory.Exists(data))
        {
            var entry = new DirectoryInfo(data);
            if ((entry.Attributes & FileAttributes.ReparsePoint) == 0 || !string.Equals(entry.ResolveLinkTarget(true)?.FullName.TrimEnd('\\'), shared.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"{entry.Name} is not a link to the shared data folder. Existing files were left unchanged.");
        }
        else
        {
            var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            var command = "New-Item -ItemType Junction -Path '" + data.Replace("'", "''") + "' -Target '" + shared.Replace("'", "''") + "' -ErrorAction Stop | Out-Null";
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-EncodedCommand"); info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
            using var process = Process.Start(info) ?? throw new IOException("Could not create the shared-data link.");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("Could not create the shared-data link. " + error);
        }
        // Refresh the small launcher after game updates; caller guarantees this instance is stopped.
        File.Copy(original, executable, true);
    }
    public static ProcessStartInfo StartInfo(Profile profile, string folder)
    {
        var game = Path.Combine(folder, profile.ProcessName + ".exe");
        return new ProcessStartInfo(game) { WorkingDirectory = folder, UseShellExecute = true };
    }
}

public static class Program
{
    public static readonly string PipeName = "ROInstances_" + Environment.UserName;
    [STAThread] public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--apply-update") return Updates.Apply(args);
        if (args.Length == 1 && args[0] == "--check-updates") return Updates.CommandLineAsync(false).GetAwaiter().GetResult();
        if (args.Length == 1 && args[0] == "--install-update")
        {
            using var updateLock = new Mutex(true, "Local\\" + PipeName, out var canUpdate);
            if (!canUpdate)
            {
                Directory.CreateDirectory(Updates.Root);
                File.WriteAllText(Path.Combine(Updates.Root, "check.txt"), "Exit the launcher before using --install-update, or use Update and restart in its window.");
                return 1;
            }
            return Updates.CommandLineAsync(true).GetAwaiter().GetResult();
        }
        if (args.Contains("--self-test")) return SelfTest.Run();
        var preview = args.Length == 2 && (args[0] == "--preview" || args[0] == "--preview-editor");
        var launchSlot = args.Length == 2 && args[0] == "--launch" && int.TryParse(args[1], out var slot) && slot >= 1 && slot <= 12 ? slot : 0;
        using var single = new Mutex(true, "Local\\" + PipeName, out var created);
        if (!created && !preview)
        {
            try { using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out); pipe.Connect(2500); using var writer = new StreamWriter(pipe); writer.WriteLine(launchSlot); }
            catch { MessageBox.Show("The launcher is already starting. Open it from the notification area in a moment.", Branding.AppName); }
            return 0;
        }
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { MessageBox.Show(e.Exception.Message, Branding.AppName); e.Handled = true; };
        try
        {
            var window = new LauncherWindow(preview);
            app.MainWindow = window;
            if (args.Length == 2 && args[0] == "--update-complete")
                window.ContentRendered += (_, _) => Updates.MarkStarted(args[1]);
            if (preview)
            {
                Window target = args[0] == "--preview-editor" ? new ProfileEditor(new Profile { Slot = 1, Name = "Main account" }) : window;
                target.Show();
                target.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                target.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(target);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(args[1]); encoder.Save(output);
                window.AllowExit = true; target.Close(); if (target != window) window.Close(); return 0;
            }
            _ = Task.Run(async () => {
                while (true)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await server.WaitForConnectionAsync();
                        using var reader = new StreamReader(server);
                        var line = await reader.ReadLineAsync();
                        if (int.TryParse(line, out var requested)) _ = window.Dispatcher.BeginInvoke(new Action(() => { window.Reveal(); if (requested > 0) _ = window.Launch(requested); }));
                    }
                    catch { await Task.Delay(1000); }
                }
            });
            if (launchSlot > 0)
            {
                EventHandler? launchOnce = null;
                launchOnce = (_, _) => { window.ContentRendered -= launchOnce; _ = window.Dispatcher.BeginInvoke(new Action(() => { _ = window.Launch(launchSlot); }), DispatcherPriority.ApplicationIdle); };
                window.ContentRendered += launchOnce;
            }
            window.Show();
            app.Run();
        }
        catch (Exception e) { MessageBox.Show(e.Message + "\n\nYour saved profiles have not been replaced.", Branding.AppName); return 1; }
        return 0;
    }
}

public sealed class LauncherWindow : Window
{
    readonly Settings settings;
    readonly bool preview;
    readonly StackPanel cards = new();
    readonly TextBlock cpuValue = Text("Measuring.", 22, "#F4F7FC");
    readonly TextBlock ramValue = Text("-", 22, "#F4F7FC");
    readonly TextBlock commitValue = Text("-", 22, "#F4F7FC");
    readonly TextBlock countValue = Text("-", 22, "#F4F7FC");
    readonly TextBlock feedback = Text("Ready. Your existing game windows stay open.", 12, "#AAB6CC");
    readonly Dictionary<int, (TextBlock Status, TextBlock Usage, Button Launch, Button Focus, Button Stop)> rows = new();
    readonly Dictionary<int, DateTime> pending = new();
    readonly HashSet<int> starting = new();
    readonly HashSet<int> stopping = new();
    readonly HashSet<int> selected = new();
    readonly Dictionary<int, CheckBox> selections = new();
    readonly CheckBox checkAll = new() { Content = "Check all", Foreground = Brush("#E9EFFA"), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
    readonly TextBlock selectionCount = Text("Select instances to launch together", 12, "#AAB6CC");
    readonly Button launchSelected;
    bool batchLaunching;
    bool updatingSelection;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    Forms.NotifyIcon? tray;
    System.Drawing.Icon? trayIcon;
    public bool AllowExit;
    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    public static TextBlock Text(string text, double size = 14, string color = "#E9EFFA") => new() { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    static Border Panel(UIElement child, string background = "#172237", int padding = 18) => new() { Child = child, Background = Brush(background), CornerRadius = new CornerRadius(12), Padding = new Thickness(padding), BorderBrush = Brush("#2B3952"), BorderThickness = new Thickness(1) };
    static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(15,9,15,9), Margin = new Thickness(5,0,0,0), Background = Brush(primary ? "#8BE6CB" : "#26364F"), Foreground = Brush(primary ? "#102923" : "#F0F5FF"), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, FontSize = 13, MinHeight = 36 };
        button.Click += (_, _) => action(); return button;
    }
    public LauncherWindow(bool isPreview, Settings? previewSettings = null)
    {
        preview = isPreview;
        settings = isPreview && previewSettings != null ? previewSettings : Store.Load();
        launchSelected = Button("Launch selected", () => { _ = LaunchSelected(); }, true);
        if (!preview) Store.Save(settings);
        Title = Branding.AppName; Icon = Branding.Icon; Width = 1100; Height = 820; MinWidth = 950; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#0D1626"); FontFamily = new FontFamily("Segoe UI");
        var shell = new Grid { Margin = new Thickness(30,24,30,20) };
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0,0,0,23) };
        var headerButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerButtons.Children.Add(Button("Game folder", ChooseFolder));
        headerButtons.Children.Add(Button("+ Add instance", AddProfile, true));
        DockPanel.SetDock(headerButtons, Dock.Right); header.Children.Add(headerButtons);
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        brand.Children.Add(new System.Windows.Controls.Image { Source = Branding.Icon, Width = 64, Height = 64, Margin = new Thickness(0,0,16,0), VerticalAlignment = VerticalAlignment.Center, ToolTip = Branding.GameName });
        var heading = new StackPanel();
        heading.Children.Add(Text("ETERNAL LOVE  /  MULTI-INSTANCE LAUNCHER", 10, "#8BE6CB"));
        var title = Text(Branding.GameName, 25); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0,6,0,3); heading.Children.Add(title);
        heading.Children.Add(Text("Launch and manage your Eternal Love game windows.", 12, "#AAB6CC")); brand.Children.Add(heading); header.Children.Add(brand);
        shell.Children.Add(header);
        var metrics = new UniformGridCompat(4) { Margin = new Thickness(0,0,0,20) };
        foreach (var item in new[] { ("RUNNING", countValue), ("CPU LOAD", cpuValue), ("AVAILABLE RAM", ramValue), ("MEMORY HEADROOM", commitValue) })
        { var stack = new StackPanel(); stack.Children.Add(Text(item.Item1, 10, "#AAB6CC")); item.Item2.Margin = new Thickness(0,7,0,0); stack.Children.Add(item.Item2); var panel = Panel(stack); panel.Margin = new Thickness(0,0,10,0); metrics.Children.Add(panel); }
        Grid.SetRow(metrics,1); shell.Children.Add(metrics);
        var intro = new DockPanel { Margin = new Thickness(0,0,0,12) };
        intro.MinHeight = 36;
        DockPanel.SetDock(launchSelected, Dock.Right); intro.Children.Add(launchSelected);
        checkAll.Margin = new Thickness(0,0,18,0);
        checkAll.Click += (_, _) => SetAllSelected(selected.Count != settings.Profiles.Count);
        DockPanel.SetDock(checkAll, Dock.Left); intro.Children.Add(checkAll);
        intro.Children.Add(selectionCount);
        Grid.SetRow(intro,2); shell.Children.Add(intro);
        var scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0,0,8,0) }; Grid.SetRow(scroll,3); shell.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0,14,0,0) };
        feedback.Margin = new Thickness(0,0,0,8); footer.Children.Add(feedback);
        footer.Children.Add(Text("Profile labels identify your Eternal Love windows. Sign in through the game; remembered login is shared.", 11, "#8D9DB7"));
        footer.Children.Add(Text("Closing this window keeps the launcher in the notification area while it manages your titles.", 11, "#8D9DB7"));
        footer.Children.Add(new UpdatesPanel(settings, preview, () => !batchLaunching && starting.Count == 0,
            () => { AllowExit = true; Close(); System.Windows.Application.Current.Shutdown(); }));
        Grid.SetRow(footer,4); shell.Children.Add(footer); Content = shell;
        RenderCards(); Refresh();
        if (!preview)
        {
            trayIcon = Branding.CreateTrayIcon();
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = Branding.AppName, Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open Eternal Love launcher", null, (_, _) => Dispatcher.Invoke(Reveal));
            menu.Items.Add("Exit launcher (leave games running)", null, (_, _) => Dispatcher.Invoke(() => { AllowExit = true; Close(); System.Windows.Application.Current.Shutdown(); }));
            tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(Reveal);
            timer.Tick += (_, _) => Refresh(); timer.Start();
        }
        Closing += (_, e) => { if (!AllowExit) { e.Cancel = true; Hide(); } else { timer.Stop(); tray?.Dispose(); trayIcon?.Dispose(); } };
    }
    public void Reveal() { Show(); WindowState = WindowState.Normal; Activate(); }
    void Say(string message, bool error = false) { feedback.Text = message; feedback.Foreground = Brush(error ? "#FFB4A6" : "#AAB6CC"); }
    void RenderCards()
    {
        cards.Children.Clear(); rows.Clear(); selections.Clear();
        foreach (var profile in settings.Profiles.OrderBy(p => p.Slot))
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions[0].Width = new GridLength(84);
            var select = new CheckBox { IsChecked = selected.Contains(profile.Slot), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,12,0), ToolTip = "Select Instance " + profile.Slot };
            System.Windows.Automation.AutomationProperties.SetName(select, "Select Instance " + profile.Slot);
            select.Checked += (_, _) => Select(profile.Slot, true);
            select.Unchecked += (_, _) => Select(profile.Slot, false);
            selections[profile.Slot] = select;
            var identifier = new StackPanel { Orientation = Orientation.Horizontal };
            identifier.Children.Add(select);
            var number = Text(profile.Slot.ToString("00"), 23, "#8BE6CB"); number.FontWeight = FontWeights.SemiBold; identifier.Children.Add(number); row.Children.Add(identifier);
            var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,12,0) };
            var name = Text(string.IsNullOrWhiteSpace(profile.Name) ? "Instance " + profile.Slot : profile.Name, 17); name.FontWeight = FontWeights.SemiBold; identity.Children.Add(name);
            var detail = string.IsNullOrWhiteSpace(profile.Account) ? "No account label yet" : profile.Account;
            if (!string.IsNullOrWhiteSpace(profile.Server)) detail += "  �  " + profile.Server;
            identity.Children.Add(Text(detail,12,"#AAB6CC"));
            Grid.SetColumn(identity,1); row.Children.Add(identity);
            var state = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var status = Text("Stopped",12,"#AAB6CC"); var usage = Text("Ready to launch",11,"#8D9DB7"); usage.Margin = new Thickness(0,5,0,0); state.Children.Add(status); state.Children.Add(usage); Grid.SetColumn(state,2); row.Children.Add(state);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var edit = Button("Edit", () => Edit(profile));
            var focus = Button("Show", () => Focus(profile)); var stop = Button("Close", () => { _ = Stop(profile); });
            var launch = Button("Launch", () => { _ = Launch(profile.Slot); }, true);
            actions.Children.Add(Button("Delete", () => DeleteProfile(profile)));
            actions.Children.Add(edit); actions.Children.Add(focus); actions.Children.Add(stop); actions.Children.Add(launch); Grid.SetColumn(actions,3); row.Children.Add(actions);
            rows[profile.Slot] = (status,usage,launch,focus,stop);
            var card = Panel(row); card.Margin = new Thickness(0,0,0,10); cards.Children.Add(card);
        }
        UpdateSelection();
    }
    void Select(int slot, bool value)
    {
        if (value) selected.Add(slot); else selected.Remove(slot);
        if (!updatingSelection) UpdateSelection();
    }
    void SetAllSelected(bool value)
    {
        updatingSelection = true;
        try { foreach (var box in selections.Values) box.IsChecked = value; }
        finally { updatingSelection = false; UpdateSelection(); }
    }
    void UpdateSelection()
    {
        checkAll.IsChecked = selected.Count == 0 ? false : selected.Count == settings.Profiles.Count ? true : null;
        selectionCount.Text = selected.Count == 0 ? "Select instances to launch together" : $"{selected.Count} selected � running instances are skipped";
        launchSelected.Content = $"Launch selected ({selected.Count})";
        launchSelected.Visibility = selected.Count > 0 && !batchLaunching ? Visibility.Visible : Visibility.Collapsed;
    }
    async Task LaunchSelected(Func<int, Task<bool>>? launch = null, int delayMs = 2000)
    {
        if (batchLaunching || selected.Count == 0) return;
        var slots = selected.OrderBy(slot => slot).ToArray();
        batchLaunching = true; UpdateSelection(); Refresh();
        try
        {
            for (var i = 0; i < slots.Length; i++)
            {
                Say($"Launching selection {i + 1} of {slots.Length} � Instance {slots[i]}.");
                if (!await (launch?.Invoke(slots[i]) ?? Launch(slots[i], true)))
                {
                    Say($"Stopped the launch queue at Instance {slots[i]}. Remaining instances were not launched.", true);
                    return;
                }
                if (i + 1 < slots.Length) await Task.Delay(delayMs);
            }
            Say($"Finished launching {slots.Length} selected instances. Already running instances were skipped.");
        }
        finally { batchLaunching = false; UpdateSelection(); Refresh(); }
    }
    internal static void TestSelection(Action<bool, string> check)
    {
        var window = new LauncherWindow(true, new Settings {
            GameFolder = Path.Combine(Store.Root, "Tests", "NoGame"),
            Profiles = Enumerable.Range(1, 3).Select(slot => new Profile { Slot = slot }).ToList()
        });
        try
        {
            check(window.launchSelected.Visibility == Visibility.Collapsed, "empty selection hides bulk launch");
            window.selections[2].IsChecked = true;
            check(window.selected.SetEquals(new[] { 2 }) && window.checkAll.IsChecked == null, "individual selection sets partial check-all state");
            window.checkAll.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            check(window.selected.Count == 3 && window.checkAll.IsChecked == true, "check all selects every profile from partial selection");
            window.RenderCards();
            check(window.selections.Values.All(box => box.IsChecked == true), "selection survives profile card refresh");
            window.checkAll.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            check(window.selected.Count == 0 && window.checkAll.IsChecked == false, "check all clears a complete selection");
            window.selections[3].IsChecked = true;
            window.selections[1].IsChecked = true;
            var launched = new List<int>();
            window.LaunchSelected(slot => {
                check(window.launchSelected.Visibility == Visibility.Collapsed, "bulk action hidden while launching");
                launched.Add(slot);
                window.selections[2].IsChecked = true;
                return Task.FromResult(true);
            }, 0).GetAwaiter().GetResult();
            check(launched.SequenceEqual(new[] { 1, 3 }), "batch uses ordered selection snapshot and excludes unchecked profiles");
            launched.Clear();
            window.LaunchSelected(slot => { launched.Add(slot); return Task.FromResult(false); }, 0).GetAwaiter().GetResult();
            check(launched.SequenceEqual(new[] { 1 }) && !window.batchLaunching && window.launchSelected.Visibility == Visibility.Visible, "cancelled launch stops queue and restores controls");
            var profile = window.settings.Profiles[0];
            try { window.RemoveProfile(profile, _ => throw new IOException("Test save failure")); }
            catch (IOException) { }
            check(window.settings.Profiles[0] == profile && window.selected.Contains(profile.Slot), "failed deletion preserves profile order and selection");
            window.stopping.Add(profile.Slot);
            bool blocked = false;
            try { window.RemoveProfile(profile, _ => { }); }
            catch (InvalidOperationException) { blocked = true; }
            check(blocked && window.settings.Profiles.Contains(profile), "deletion blocked during close");
            window.stopping.Clear();
            window.Stop(profile).GetAwaiter().GetResult();
            check(!window.stopping.Contains(profile.Slot) && window.feedback.Text.Contains("already stopped"), "close of stopped instance reports result and resets controls");
            var deletionFile = Path.Combine(Store.Root, "Tests", "deleted-profiles.json");
            window.RemoveProfile(profile, value => Store.Save(value, deletionFile));
            check(Store.Load(deletionFile).Profiles.Select(p => p.Slot).SequenceEqual(new[] { 2, 3 }) && !window.selected.Contains(1) && !window.rows.ContainsKey(1), "deletion persists without renumbering and clears selection and card");
            foreach (var remaining in window.settings.Profiles.ToArray())
                window.RemoveProfile(remaining, value => Store.Save(value, deletionFile));
            check(Store.Load(deletionFile).Profiles.Count == 0 && window.rows.Count == 0, "last instance can be deleted and empty profiles reload");
        }
        finally { window.AllowExit = true; window.Close(); }
    }
    void Refresh()
    {
        var (cpu,memory) = Native.ReadUsage();
        cpuValue.Text = double.IsNaN(cpu) ? "Measuring." : $"{cpu:F0}%";
        ramValue.Text = $"{memory.AvailablePhysical / 1073741824.0:F1} GB";
        commitValue.Text = $"{memory.AvailablePageFile / 1073741824.0:F1} GB";
        commitValue.Foreground = Brush(memory.AvailablePageFile < 5UL * 1024 * 1024 * 1024 ? "#EBCB8B" : "#F4F7FC");
        commitValue.ToolTip = "Remaining Windows memory commitment capacity, including the pagefile. Each game currently needs several GB.";
        int running = 0;
        foreach (var profile in settings.Profiles)
        {
            var processes = Game.Find(profile, settings.GameFolder);
            var row = rows[profile.Slot];
            var isPending = pending.TryGetValue(profile.Slot, out var since) && DateTime.UtcNow - since < TimeSpan.FromSeconds(45);
            if (processes.Count > 0) { running++; pending.Remove(profile.Slot); }
            else if (pending.ContainsKey(profile.Slot) && !isPending) { pending.Remove(profile.Slot); Say($"Instance {profile.Slot} did not appear. Check the game message before trying again.",true); }
            row.Status.Text = processes.Count > 0 ? "? Running" : isPending || starting.Contains(profile.Slot) ? "Starting." : "	 Stopped";
            row.Status.Foreground = Brush(processes.Count > 0 ? "#8BE6CB" : "#AAB6CC");
            row.Usage.Text = processes.Count > 0 ? $"{processes.Sum(p => SafeWorkingSet(p)) / 1073741824.0:F1} GB in RAM" : isPending ? "Waiting for window" : "Ready to launch";
            row.Stop.IsEnabled = !stopping.Contains(profile.Slot);
            row.Stop.Content = stopping.Contains(profile.Slot) ? "Closing..." : "Close";
            row.Launch.Visibility = !stopping.Contains(profile.Slot) && !batchLaunching && processes.Count == 0 && !isPending && !starting.Contains(profile.Slot) ? Visibility.Visible : Visibility.Collapsed;
            row.Focus.Visibility = row.Stop.Visibility = processes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var process in processes)
            {
                try { if (!preview && process.MainWindowHandle != IntPtr.Zero && process.MainWindowTitle != profile.Title) Native.SetWindowText(process.MainWindowHandle,profile.Title); } catch { }
                process.Dispose();
            }
        }
        countValue.Text = $"{running} / {settings.Profiles.Count}";
    }
    static long SafeWorkingSet(Process p) { try { return p.WorkingSet64; } catch { return 0; } }
    public async Task<bool> Launch(int slot, bool fromBatch = false)
    {
        if (batchLaunching && !fromBatch) return false;
        var profile = settings.Profiles.FirstOrDefault(p => p.Slot == slot);
        if (profile == null) { Say("That instance is not in your saved profiles.",true); return false; }
        if (stopping.Contains(slot)) return false;
        if (starting.Contains(slot) || pending.ContainsKey(slot)) return true;
        var existing = Game.Find(profile,settings.GameFolder);
        if (existing.Count > 0) { existing.ForEach(p => p.Dispose()); if (!fromBatch) Focus(profile); return true; }
        starting.Add(slot); Refresh();
        try
        {
            var usage = Native.ReadUsage().Memory;
            if ((usage.AvailablePageFile < 5UL * 1024 * 1024 * 1024 || usage.AvailablePhysical < 3UL * 1024 * 1024 * 1024) && MessageBox.Show(this,"Memory is tight. Another Eternal Love instance may need 3-5 GB and could cause stuttering or fail to start.\n\nLaunch this instance anyway?","Low memory headroom",MessageBoxButton.YesNo,MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
            var start = Game.StartInfo(profile,settings.GameFolder);
            await Task.Run(() => Game.EnsureFiles(profile,settings.GameFolder));
            var process = Process.Start(start) ?? throw new IOException("Windows could not start this instance.");
            pending[slot] = DateTime.UtcNow;
            Say($"Starting Instance {slot}.");
            process.Dispose();
            return true;
        }
        catch (Exception e) { Say(e.Message,true); MessageBox.Show(this,e.Message,"Could not launch",MessageBoxButton.OK,MessageBoxImage.Warning); return false; }
        finally { starting.Remove(slot); Refresh(); }
    }
    void Focus(Profile profile)
    {
        foreach (var p in Game.Find(profile,settings.GameFolder)) { try { if (p.MainWindowHandle != IntPtr.Zero) { Native.ShowWindow(p.MainWindowHandle,9); Native.SetForegroundWindow(p.MainWindowHandle); } } finally { p.Dispose(); } }
    }
    async Task Stop(Profile profile)
    {
        if (!stopping.Add(profile.Slot)) return;
        var processes = new List<Process>();
        try
        {
            processes = Game.Find(profile, settings.GameFolder);
            Refresh();
            if (processes.Count == 0) { Say($"Instance {profile.Slot} is already stopped."); return; }
            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited) continue;
                    process.CloseMainWindow();
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        Native.ShowWindow(process.MainWindowHandle, 9);
                        Native.SetForegroundWindow(process.MainWindowHandle);
                    }
                }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            Say($"Waiting for Instance {profile.Slot} to close. Confirm any exit prompt in the game.");
            await Task.WhenAll(processes.Select(p => Task.Run(() => p.WaitForExit(5000))));
            var remaining = processes.Where(p => !p.HasExited).ToArray();
            if (remaining.Length > 0)
            {
                if (MessageBox.Show(this, $"Instance {profile.Slot} is still running. Force close it?\n\nUnsaved game progress may be lost.",
                    "Force close instance?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                { Say($"Instance {profile.Slot} is still running. Close was not forced."); return; }
                foreach (var process in remaining)
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                }
                await Task.WhenAll(remaining.Select(p => Task.Run(() => p.WaitForExit(5000))));
            }
            Say(processes.All(p => p.HasExited) ? $"Instance {profile.Slot} closed." : $"Instance {profile.Slot} is still closing.");
        }
        catch (Exception e) { Say($"Could not close Instance {profile.Slot}: {e.Message}", true); }
        finally
        {
            foreach (var process in processes) process.Dispose();
            stopping.Remove(profile.Slot);
            Refresh();
        }
    }
    bool RemoveProfile(Profile profile, Action<Settings> save)
    {
        if (batchLaunching || starting.Contains(profile.Slot) || pending.ContainsKey(profile.Slot) || stopping.Contains(profile.Slot))
            throw new InvalidOperationException("Wait for the current launch or close operation before deleting this instance.");
        var index = settings.Profiles.IndexOf(profile);
        if (index < 0) return false;
        settings.Profiles.RemoveAt(index);
        try { save(settings); }
        catch { settings.Profiles.Insert(index, profile); throw; }
        selected.Remove(profile.Slot);
        RenderCards(); Refresh();
        return true;
    }
    void DeleteProfile(Profile profile)
    {
        if (MessageBox.Show(this, $"Delete Instance {profile.Slot} from the launcher?\n\nIts saved labels will be removed. Game files and any running game will be kept.",
            "Delete instance?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            if (RemoveProfile(profile, value => Store.Save(value))) Say($"Deleted Instance {profile.Slot}. Game files and running games were kept.");
        }
        catch (Exception e) { Say(e.Message, true); }
    }
    void Edit(Profile profile)
    {
        var original = JsonSerializer.Serialize(profile);
        var editor = new ProfileEditor(profile) { Owner = this };
        if (editor.ShowDialog() != true) return;
        try { Store.Save(settings); RenderCards(); Refresh(); Say($"Saved Instance {profile.Slot}. Names apply now."); }
        catch (Exception e) { var restore = JsonSerializer.Deserialize<Profile>(original)!; settings.Profiles[settings.Profiles.IndexOf(profile)] = restore; MessageBox.Show(this,e.Message,"Could not save profile"); }
    }
    void AddProfile()
    {
        var next = Enumerable.Range(1,12).FirstOrDefault(i => settings.Profiles.All(p => p.Slot != i));
        if (next == 0) { Say("This launcher supports up to 12 profiles.",true); return; }
        var profile = new Profile { Slot = next };
        var editor = new ProfileEditor(profile) { Owner = this };
        if (editor.ShowDialog() != true) return;
        settings.Profiles.Add(profile);
        try { Store.Save(settings); RenderCards(); Refresh(); Say($"Instance {next} saved. Its small EXE and shared-data link will be prepared on first launch."); }
        catch (Exception e) { settings.Profiles.Remove(profile); MessageBox.Show(this,e.Message,"Could not add profile"); }
    }
    void ChooseFolder()
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Select your Ragnarok M: Eternal Love game folder", InitialDirectory = settings.GameFolder };
        if (picker.ShowDialog(this) != true) return;
        if (!File.Exists(Path.Combine(picker.FolderName,"ro_win.exe")) || !Directory.Exists(Path.Combine(picker.FolderName,"ro_win_Data"))) { Say("That folder must contain ro_win.exe and ro_win_Data.",true); return; }
        var previous = settings.GameFolder; settings.GameFolder = picker.FolderName;
        try { Store.Save(settings); Refresh(); Say("Game folder saved."); } catch (Exception e) { settings.GameFolder = previous; Say(e.Message,true); }
    }

}

public sealed class UniformGridCompat : System.Windows.Controls.Primitives.UniformGrid { public UniformGridCompat(int columns) { Columns = columns; } }

public sealed class ProfileEditor : Window
{
    public ProfileEditor(Profile profile)
    {
        Title = "Eternal Love � Edit Instance " + profile.Slot; Icon = Branding.Icon; Width = 520; Height = 490; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(13,22,38)); FontFamily = new FontFamily("Segoe UI");
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(LauncherWindow.Text("Instance " + profile.Slot,24));
        TextBox Field(string label, string value)
        {
            var text = LauncherWindow.Text(label,12,"#AAB6CC"); text.Margin = new Thickness(0,16,0,6); panel.Children.Add(text);
            var box = new TextBox { Text = value, FontSize = 14, Padding = new Thickness(9), MaxLength = 80 }; panel.Children.Add(box); return box;
        }
        var name = Field("Profile name  �  appears in the game title",profile.Name);
        var account = Field("Account label or email  �  optional",profile.Account);
        var server = Field("Server / character note  �  optional",profile.Server);
        var hint = LauncherWindow.Text("Labels are saved on this PC and do not switch accounts. Enter passwords only in the game.",12,"#AAB6CC"); hint.Margin = new Thickness(0,16,0,16); panel.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(18,9,18,9), Margin = new Thickness(0,0,8,0), IsCancel = true }; buttons.Children.Add(cancel);
        var save = new Button { Content = "Save profile", Padding = new Thickness(18,9,18,9), IsDefault = true };
        save.Click += (_, _) => {
            profile.Name = name.Text.Trim(); profile.Account = account.Text.Trim(); profile.Server = server.Text.Trim(); DialogResult = true;
        };
        buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel;
    }
}

public static class SelfTest
{
    public static int Run()
    {
        var report = new List<string>();
        void Check(bool condition,string name) { if (!condition) throw new Exception("FAIL: " + name); report.Add("PASS: " + name); }
        try
        {
            var settings = new Settings { Profiles = Enumerable.Range(1,4).Select(i => new Profile { Slot = i }).ToList() };
            Store.Validate(settings);
            Check(settings.Profiles[0].ProcessName == "ro_win" && settings.Profiles[3].ProcessName == "ro_win3","instance-to-executable mapping");
            settings.Profiles[0].Name = "Main account";
            Check(settings.Profiles[0].Title == "Ragnarok M: Eternal Love - Instance 1 | Main account","account window title");
            var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
            Store.Validate(copy); Check(copy.Profiles[0].Name == "Main account","profile JSON round-trip");
            copy.Profiles[1].Slot = 1;
            bool rejected = false; try { Store.Validate(copy); } catch (InvalidDataException) { rejected = true; } Check(rejected,"duplicate instance rejected");
            var legacy = JsonSerializer.Deserialize<Settings>("""{"Profiles":[{"Slot":3,"Name":"Main","Account":"user@example.invalid","Server":"A","Isolated":true,"Sandbox":"RO3"}]}""")!;
            Store.Validate(legacy);
            Check(legacy.Profiles[0].Name == "Main" && legacy.Profiles[0].Account == "user@example.invalid" && legacy.Profiles[0].Server == "A", "legacy profile labels preserved");
            var migrated = JsonSerializer.Serialize(legacy);
            Check(!migrated.Contains("Isolated") && !migrated.Contains("Sandbox"), "obsolete isolation settings removed on save");
            var legacyStart = Game.StartInfo(legacy.Profiles[0], legacy.GameFolder);
            Check(legacyStart.FileName.EndsWith("ro_win2.exe") && legacyStart.UseShellExecute && legacyStart.ArgumentList.Count == 0, "legacy isolated profile launches game directly");
            var start = Game.StartInfo(settings.Profiles[3],settings.GameFolder);
            Check(start.FileName.EndsWith("ro_win3.exe") && start.WorkingDirectory == settings.GameFolder,"launch target and working directory");
            Check(Native.ReadUsage().Memory.TotalPhysical > 0,"live memory metrics");
            var testFile = Path.Combine(Store.Root,"Tests","profiles.json");
            Store.Save(settings,testFile);
            settings.Profiles[0].Account = "test@example.invalid";
            Store.Save(settings,testFile);
            Check(Store.Load(testFile).Profiles[0].Account == "test@example.invalid", "atomic save and reload from disk");
            Check(Store.Load(testFile + ".bak").Profiles[0].Account == "", "previous profiles preserved in backup");
            var edited = new Profile { Slot = 1 };
            var editor = new ProfileEditor(edited);
            editor.Loaded += (_, _) => editor.Dispatcher.BeginInvoke(new Action(() => {
                var content = (StackPanel)editor.Content;
                var fields = content.Children.OfType<TextBox>().ToArray();
                fields[0].Text = "  Saved main  "; fields[1].Text = "user@example.invalid"; fields[2].Text = "Server A";
                var actions = content.Children.OfType<StackPanel>().Last();
                actions.Children.OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Check(editor.ShowDialog() == true && edited.Name == "Saved main" && edited.Account == "user@example.invalid" && edited.Server == "Server A", "profile editor Save interaction");
            var cancelled = new Profile { Slot = 2, Name = "Original" };
            var cancelEditor = new ProfileEditor(cancelled);
            cancelEditor.Loaded += (_, _) => cancelEditor.Dispatcher.BeginInvoke(new Action(() => {
                ((StackPanel)cancelEditor.Content).Children.OfType<TextBox>().First().Text = "Unsaved";
                cancelEditor.DialogResult = false;
            }));
            Check(cancelEditor.ShowDialog() == false && cancelled.Name == "Original", "profile editor cancellation preserves values");
            LauncherWindow.TestSelection(Check);
            UpdateTests.Run(Check);
            Directory.CreateDirectory(Store.Root); File.WriteAllLines(Path.Combine(Store.Root,"self-test.txt"),report); return 0;
        }
        catch (Exception e) { report.Add(e.ToString()); Directory.CreateDirectory(Store.Root); File.WriteAllLines(Path.Combine(Store.Root,"self-test.txt"),report); return 1; }
    }
}
