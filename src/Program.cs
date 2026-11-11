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
    public string WindowsUser { get; set; } = "";
    public string LoginStorageId { get; set; } = "";
    public GameLaunchSettings LaunchSettings { get; set; } = new();
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
        if (settings.Profiles.Any(p => p.Name == null || p.Account == null || p.Server == null || p.LoginStorageId == null))
            throw new InvalidDataException("A profile contains an invalid empty field.");
        if (settings.Profiles.Any(p => p.WindowsUser == null || (p.WindowsUser.Length > 0 && p.WindowsUser != p.ProcessName)))
            throw new InvalidDataException("Windows users must match their instance executable names.");
        if (!Path.IsPathFullyQualified(settings.GameFolder)) throw new InvalidDataException("The game folder must be an absolute path.");
        foreach (var profile in settings.Profiles)
        {
            if (profile.LaunchSettings == null) throw new InvalidDataException("An instance has missing launch settings.");
            profile.LaunchSettings.Validate();
        }
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
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static string? ImagePath(Process process)
    {
        var handle = OpenProcess(0x1000, false, process.Id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(32768); var size = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }
    public static List<Process> Find(Profile profile, string folder)
    {
        var expected = Path.GetFullPath(Path.Combine(folder, profile.ProcessName + ".exe"));
        var found = new List<Process>();
        foreach (var p in Process.GetProcessesByName(profile.ProcessName))
        {
            try { if (string.Equals(ImagePath(p), expected, StringComparison.OrdinalIgnoreCase)) { found.Add(p); continue; } }
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
    public static ProcessStartInfo StartInfo(Profile profile, string folder, bool useCurrentUser = false, GameLaunchSettings? options = null)
    {
        var game = Path.Combine(folder, profile.ProcessName + ".exe");
        var start = new ProcessStartInfo(game) { WorkingDirectory = folder, UseShellExecute = true };
        foreach (var argument in GameDisplay.ArgumentsFor(options ?? profile.LaunchSettings)) start.ArgumentList.Add(argument);
        return profile.WindowsUser.Length == 0 || useCurrentUser ? start : InstanceAccounts.Configure(start, profile.WindowsUser);
    }
}

public static class Program
{
    public static readonly string PipeName = "ROInstances_" + Environment.UserName;
    [STAThread] public static int Main(string[] args)
    {
        StartupEnvironment.Initialize();
        if (args.Length == 1 && args[0] == "--check-browser-focus")
        {
            var focused = SignInBrowser.Open("https://example.com/ro-launcher-focus-check").GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "ROInstanceLauncher-browser-focus.txt"), focused ? "PASS: Default browser is the foreground window." : "FAIL: Default browser did not receive foreground focus.");
            return focused ? 0 : 1;
        }
        if (args.Length == 3 && args[0] == "--browser-request-relay" && int.TryParse(args[1], out var relayOwner))
            return BrowserRequestRelay.Run(relayOwner, args[2]);
        if (args.Length == 3 && args[0] == "--open-sso-url") return LoginBrowser.Forward(args[1], args[2]);
        if (args.Length == 1 && args[0] == "--probe-instance-user") return LoginDiagnostics.WriteIdentity();
        if (args.Length == 1 && args[0] == "--prepare-windowed-user") return LoginDiagnostics.WriteIdentity(prepareWindowed: true);
        if (args.Length == 2 && args[0] == "--prepare-game-user")
        {
            try
            {
                var options = JsonSerializer.Deserialize<GameLaunchSettings>(args[1]) ?? throw new InvalidDataException("Missing launch settings.");
                options.Validate();
                return LoginDiagnostics.WriteIdentity(prepareWindowed: true, options);
            }
            catch (Exception error) { return LoginDiagnostics.WriteError("Reading instance launch settings", error); }
        }
        if (args.Length == 2 && args[0] == "--verify-login-storage") return LoginDiagnostics.VerifyStorage(args[1]).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "--setup-instance-user") return InstanceAccounts.Setup(args[1], args[2]);
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
        var preview = args.Length == 2 && (args[0] == "--preview" || args[0] == "--preview-editor" || args[0] == "--preview-settings");
        var launchSlot = args.Length == 2 && args[0] == "--launch" && int.TryParse(args[1], out var slot) && slot >= 1 && slot <= 12 ? slot : 0;
        using var single = new Mutex(true, "Local\\" + PipeName, out var created);
        if (!created && !preview)
        {
            try { using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out); pipe.Connect(2500); using var writer = new StreamWriter(pipe); writer.WriteLine(launchSlot); }
            catch { MessageBox.Show("The launcher is already starting. Open it from the notification area in a moment.", Branding.AppName); }
            return 0;
        }
        using var browserCancellation = new CancellationTokenSource();
        if (!preview) _ = LoginBrowser.Listen(browserCancellation.Token);
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
                if (args[0] == "--preview-settings")
                {
                    window.Show();
                    window.Dispatcher.BeginInvoke(new Action(() => {
                        var dialog = app.Windows.OfType<Window>().Single(w => w.Owner == window);
                        SavePreview(dialog, args[1]); dialog.Close();
                    }), DispatcherPriority.ApplicationIdle);
                    window.ShowSettings(); window.AllowExit = true; window.Close(); return 0;
                }
                Window target = args[0] == "--preview-editor" ? new ProfileEditor(new Profile { Slot = 1, Name = "Main account" }) : window;
                target.Show();
                target.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                target.UpdateLayout();
                SavePreview(target, args[1]);
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
        catch (Exception e)
        {
            var log = Path.Combine(Path.GetTempPath(), "ROInstanceLauncher-startup-error.txt");
            try { File.WriteAllText(log, e.ToString()); } catch { }
            MessageBox.Show(e.GetBaseException().Message + "\n\nYour saved profiles have not been replaced.\nDetails: " + log, Branding.AppName);
            return 1;
        }
        return 0;
    }
    static void SavePreview(Window target, string path)
    {
        target.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(target);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}

public sealed class LauncherWindow : Window
{
    readonly Settings settings;
    readonly bool preview;
    readonly StackPanel cards = new();
    readonly TextBlock cpuValue = Text("\u2014", 22, "#F4F7FC");
    readonly TextBlock ramValue = Text("—", 22, "#F4F7FC");
    readonly TextBlock commitValue = Text("—", 22, "#F4F7FC");
    readonly TextBlock countValue = Text("—", 22, "#F4F7FC");
    readonly TextBlock folderStatus = Text("", 12, "#AAB6CC");
    readonly TextBlock folderLabel = Text("", 16);
    readonly TextBlock folderDot = Text("\u25cf", 20, "#74F8B0");
    readonly TextBlock feedback = Text("Ready to launch.", 14, "#E9EFFA");
    readonly Dictionary<int, (TextBlock Status, TextBlock Usage, Button Launch, Button Focus, MenuItem Stop)> rows = new();
    readonly Dictionary<int, DateTime> pending = new();
    readonly HashSet<int> starting = new();
    readonly HashSet<int> stopping = new();
    readonly HashSet<int> selected = new();
    readonly Dictionary<int, CheckBox> selections = new();
    readonly CheckBox checkAll = new() { Content = "Select all", Foreground = Brush("#E9EFFA"), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
    readonly TextBlock selectionCount = Text("Select instances to launch together", 12, "#AAB6CC");
    readonly Button launchSelected;
    readonly UpdatesPanel updates;
    bool batchLaunching;
    bool updatingSelection;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    Forms.NotifyIcon? tray;
    System.Drawing.Icon? trayIcon;
    public bool AllowExit;
    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    public static TextBlock Text(string text, double size = 14, string color = "#E9EFFA") => new() { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    static Border Panel(UIElement child, string background = "#172237", int padding = 18) => new() { Child = child, Background = LauncherTheme.Surface(), CornerRadius = new CornerRadius(9), Padding = new Thickness(padding), BorderBrush = Brush("#354B6C"), BorderThickness = new Thickness(1) };
    internal static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(18,8,18,8), Margin = new Thickness(14,0,0,0), Background = primary ? LauncherTheme.Surface("#85FFC0", "#62EEA4") : LauncherTheme.Surface("#223550", "#18283F"), Foreground = Brush(primary ? "#08251C" : "#F0F5FF"), BorderBrush = Brush(primary ? "#87FFC2" : "#526681"), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand, FontSize = 16, FontWeight = FontWeights.SemiBold, MinHeight = 40 };
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Chrome";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var focusRing = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focusRing.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.White, "Chrome"));
        focusRing.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "Chrome"));
        template.Triggers.Add(focusRing); button.Template = template;
        var style = new Style(typeof(Button));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.85)); style.Triggers.Add(hover);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.45)); style.Triggers.Add(disabled);
        button.Style = style;
        button.Click += (_, _) => action(); return button;
    }
    public LauncherWindow(bool isPreview, Settings? previewSettings = null)
    {
        preview = isPreview;
        settings = isPreview && previewSettings != null ? previewSettings : Store.Load();
        launchSelected = Button("Launch selected", () => { _ = LaunchSelected(); }, true);
        if (!preview) Store.Save(settings);
        Title = Branding.AppName; Icon = Branding.Icon; Width = 1100; Height = 840; MinWidth = 1000; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = LauncherTheme.Surface("#0B152B", "#070F22"); FontFamily = new FontFamily("Segoe UI");
        Resources.Add(typeof(CheckBox), LauncherTheme.CheckBoxStyle);
        var shell = new Grid { Margin = new Thickness(20,18,20,18) };
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(4,0,0,16) };
        var headerButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var settingsButton = Button("\u2699  Settings", ShowSettings);
        settingsButton.ToolTip = "Update preferences and release notes";
        headerButtons.Children.Add(settingsButton);
        headerButtons.Children.Add(Button("+ Add instance", AddProfile, true));
        DockPanel.SetDock(headerButtons, Dock.Right); header.Children.Add(headerButtons);
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        brand.Children.Add(new System.Windows.Controls.Image { Source = Branding.Icon, Width = 98, Height = 98, Margin = new Thickness(0,0,20,0), VerticalAlignment = VerticalAlignment.Center, ToolTip = Branding.GameName });
        var heading = new StackPanel();
        heading.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(Text("ETERNAL LOVE / MULTI-INSTANCE LAUNCHER", 13, "#BDCAE1"));
        var title = Text(Branding.GameName, 30); title.FontWeight = FontWeights.Bold; title.Margin = new Thickness(0,3,0,4); heading.Children.Add(title);
        heading.Children.Add(Text("Your accounts. One launcher.", 19, "#AAB6CC")); brand.Children.Add(heading); header.Children.Add(brand);
        shell.Children.Add(header);
        shell.RowDefinitions.Insert(1, new RowDefinition { Height = GridLength.Auto });
        var folderBar = new DockPanel();
        var changeFolder = Button("Change", ChooseFolder);
        changeFolder.Background = Brushes.Transparent; changeFolder.BorderThickness = new Thickness(0);
        changeFolder.FontWeight = FontWeights.Normal; changeFolder.Foreground = Brush("#A9D3FC");
        changeFolder.MinHeight = 26; changeFolder.Padding = new Thickness(0);
        changeFolder.Content = new TextBlock { Text = "Change", TextDecorations = TextDecorations.Underline };
        DockPanel.SetDock(changeFolder, Dock.Right); folderBar.Children.Add(changeFolder);
        folderDot.Margin = new Thickness(4,0,12,0); DockPanel.SetDock(folderDot, Dock.Left); folderBar.Children.Add(folderDot);
        folderLabel.FontWeight = FontWeights.Bold; DockPanel.SetDock(folderLabel, Dock.Left); folderBar.Children.Add(folderLabel);
        var divider = Text("|", 18, "#9AAFCB"); divider.Margin = new Thickness(18,0,18,0);
        DockPanel.SetDock(divider, Dock.Left); folderBar.Children.Add(divider);
        folderStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        folderStatus.TextWrapping = TextWrapping.NoWrap;
        folderBar.Children.Add(folderStatus);
        folderStatus.FontSize = 15;
        var folderPanel = Panel(folderBar, padding: 12); folderPanel.Margin = new Thickness(0,0,0,18);
        Grid.SetRow(folderPanel,1); shell.Children.Add(folderPanel);
        var metrics = new UniformGridCompat(4) { Margin = new Thickness(0,0,-14,22) };
        foreach (var item in new[] { ("RUNNING", countValue, "play"), ("CPU", cpuValue, "cpu"), ("AVAILABLE RAM", ramValue, "ram"), ("MEMORY HEADROOM", commitValue, "layers") })
        {
            var metric = new DockPanel(); var symbol = LauncherTheme.Icon(item.Item3); symbol.Margin = new Thickness(0,0,20,0);
            DockPanel.SetDock(symbol, Dock.Left); metric.Children.Add(symbol);
            var stack = new StackPanel(); stack.Children.Add(Text(item.Item1, 13, "#B4C6E0"));
            item.Item2.FontSize = 25; item.Item2.FontWeight = FontWeights.Bold; item.Item2.Margin = new Thickness(0,3,0,0); stack.Children.Add(item.Item2);
            metric.Children.Add(stack); var panel = Panel(metric, padding: 16); panel.Margin = new Thickness(0,0,14,0); metrics.Children.Add(panel);
        }
        Grid.SetRow(metrics,2); shell.Children.Add(metrics);
        var intro = new DockPanel { Margin = new Thickness(0,0,0,16) };
        intro.MinHeight = 46;
        DockPanel.SetDock(launchSelected, Dock.Right); intro.Children.Add(launchSelected);
        checkAll.Margin = new Thickness(0,0,18,0);
        checkAll.FontSize = 17;
        checkAll.Click += (_, _) => SetAllSelected(selected.Count != settings.Profiles.Count);
        DockPanel.SetDock(checkAll, Dock.Right); intro.Children.Add(checkAll);
        var listHeading = new StackPanel();
        var listTitle = Text("Your instances", 28); listTitle.FontWeight = FontWeights.Bold;
        listHeading.Children.Add(listTitle); intro.Children.Add(listHeading);
        launchSelected.ToolTip = "Launch checked instances in order. Already running games are skipped.";
        Grid.SetRow(intro,3); shell.Children.Add(intro);
        var scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0,0,8,0) }; Grid.SetRow(scroll,4); shell.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(4,18,0,0) };
        var footerLine = new DockPanel();
        var footerMessage = new StackPanel();
        feedback.Margin = new Thickness(0,0,0,3);
        var readyLine = new StackPanel { Orientation = Orientation.Horizontal };
        var readyIcon = Text("\u2713", 14, "#08251C"); readyIcon.FontWeight = FontWeights.Bold; readyIcon.HorizontalAlignment = HorizontalAlignment.Center;
        readyLine.Children.Add(new Border { Child = readyIcon, Background = Brush("#74F8B0"), Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(0,0,14,0) });
        readyLine.Children.Add(feedback); footerMessage.Children.Add(readyLine);
        var trayHint = Text("Closing the launcher keeps it in the notification area.", 13, "#AAB6CC"); trayHint.Margin = new Thickness(30,0,0,0); footerMessage.Children.Add(trayHint);
        updates = new UpdatesPanel(settings, preview, () => !batchLaunching && starting.Count == 0,
            () => { AllowExit = true; Close(); System.Windows.Application.Current.Shutdown(); }, compact: true);
        var updateSummary = updates.CreateSummary(); updateSummary.MaxWidth = 300;
        DockPanel.SetDock(updateSummary, Dock.Right); footerLine.Children.Add(updateSummary); footerLine.Children.Add(footerMessage);
        footer.Children.Add(footerLine);
        footer.Children.Add(updates);
        Grid.SetRow(footer,5); shell.Children.Add(footer); Content = shell;
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
    internal void ShowSettings() => updates.ShowSettings(this);
    void Say(string message, bool error = false) { feedback.Text = message; feedback.Foreground = Brush(error ? "#FFB4A6" : "#AAB6CC"); }
    void RenderCards()
    {
        cards.Children.Clear(); rows.Clear(); selections.Clear();
        if (settings.Profiles.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20) };
            empty.Children.Add(Text("Add your first instance", 22));
            var hint = Text("Create a profile for each game window you want to launch.", 13, "#AAB6CC");
            hint.Margin = new Thickness(0,8,0,18); empty.Children.Add(hint);
            empty.Children.Add(Button("+ Add instance", AddProfile, true));
            cards.Children.Add(Panel(empty));
        }
        foreach (var profile in settings.Profiles.OrderBy(p => p.Slot))
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions[0].Width = new GridLength(140);
            row.ColumnDefinitions[2].Width = new GridLength(230);
            var select = new CheckBox { IsChecked = selected.Contains(profile.Slot), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,12,0), ToolTip = "Select Instance " + profile.Slot };
            System.Windows.Automation.AutomationProperties.SetName(select, "Select Instance " + profile.Slot);
            select.Checked += (_, _) => Select(profile.Slot, true);
            select.Unchecked += (_, _) => Select(profile.Slot, false);
            selections[profile.Slot] = select;
            var identifier = new StackPanel { Orientation = Orientation.Horizontal };
            identifier.Children.Add(select);
            var number = Text(profile.Slot.ToString("00"), 17, "#08251C"); number.FontWeight = FontWeights.Bold; number.HorizontalAlignment = HorizontalAlignment.Center;
            identifier.Children.Add(new Border { Child = number, Width = 42, Height = 34, CornerRadius = new CornerRadius(8), Background = LauncherTheme.Surface("#85FFC0", "#62EEA4"), VerticalAlignment = VerticalAlignment.Center });
            identifier.Children.Add(new Border { Width = 1, Height = 38, Background = Brush("#687C97"), Margin = new Thickness(24,0,20,0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(identifier);
            var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,12,0) };
            var name = Text(string.IsNullOrWhiteSpace(profile.Name) ? "Instance " + profile.Slot : profile.Name, 19); name.FontWeight = FontWeights.Bold; identity.Children.Add(name);
            var detail = string.IsNullOrWhiteSpace(profile.Account) ? "Add a name with Edit" : profile.Account;
            if (!string.IsNullOrWhiteSpace(profile.Server)) detail += "  ·  " + profile.Server;
            identity.Children.Add(Text(detail,15,"#BDCAE1"));
            Grid.SetColumn(identity,1); row.Children.Add(identity);
            var state = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var status = Text("Not running",16,"#BDCAE1"); status.FontWeight = FontWeights.SemiBold;
            var usage = Text("",14,"#AAB6CC"); usage.Margin = new Thickness(23,2,0,0); state.Children.Add(status); state.Children.Add(usage); Grid.SetColumn(state,2); row.Children.Add(state);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var edit = Button("Edit", () => Edit(profile));
            var focus = Button("Show game", () => Focus(profile), true);
            var stop = new MenuItem { Header = "Close game" };
            stop.Click += (_, _) => { _ = Stop(profile); };
            var launch = Button("Launch", () => { _ = Launch(profile.Slot); }, true);
            var remove = new MenuItem { Header = "Remove profile" };
            remove.Click += (_, _) => DeleteProfile(profile);
            remove.ToolTip = "Remove saved labels. Game files and running games are kept.";
            edit.ToolTip = "Change the name, account label, or character note.";
            focus.ToolTip = "Bring this game window to the front.";
            stop.ToolTip = "Ask this game window to close.";
            var menu = new ContextMenu(); menu.Items.Add(stop); menu.Items.Add(new Separator()); menu.Items.Add(remove);
            var more = Button("\u22ef", () => { }); more.FontSize = 18; more.ToolTip = "More actions for Instance " + profile.Slot;
            System.Windows.Automation.AutomationProperties.SetName(more, "More actions for Instance " + profile.Slot);
            more.ContextMenu = menu; menu.PlacementTarget = more; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            more.Click += (_, _) => menu.IsOpen = true;
            focus.MinWidth = launch.MinWidth = 152; edit.MinWidth = 95;
            more.Width = 42; more.Padding = new Thickness(0);
            actions.Children.Add(focus); actions.Children.Add(launch); actions.Children.Add(edit); actions.Children.Add(more);
            Grid.SetColumn(actions,3); row.Children.Add(actions);
            rows[profile.Slot] = (status,usage,launch,focus,stop);
            var card = Panel(row, padding: 12); card.Margin = new Thickness(0,0,0,10); cards.Children.Add(card);
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
        selectionCount.Text = selected.Count == 0 ? "Select instances to launch together" : $"{selected.Count} selected · running instances are skipped";
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
                Say($"Launching selection {i + 1} of {slots.Length} · Instance {slots[i]}…");
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
        var folderReady = File.Exists(Path.Combine(settings.GameFolder, "ro_win.exe")) && Directory.Exists(Path.Combine(settings.GameFolder, "ro_win_Data"));
        folderLabel.Text = folderReady ? "Game folder ready" : "Choose game folder";
        folderDot.Foreground = Brush(folderReady ? "#74F8B0" : "#EBCB8B");
        folderStatus.Text = settings.GameFolder;
        folderStatus.ToolTip = settings.GameFolder;
        folderStatus.Foreground = Brush(folderReady ? "#BDCAE1" : "#EBCB8B");
        var (cpu,memory) = Native.ReadUsage();
        cpuValue.Text = double.IsNaN(cpu) ? "\u2014" : $"{cpu:F0}%";
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
            row.Status.Text = stopping.Contains(profile.Slot) ? "Closing..." : processes.Count > 0 ? "\u25cf  Running" : isPending || starting.Contains(profile.Slot) ? "Starting..." : "\u25cf  Not running";
            row.Status.Foreground = Brush(processes.Count > 0 ? "#74F8B0" : "#BDCAE1");
            row.Usage.Text = processes.Count > 0 ? $"{processes.Sum(p => SafeWorkingSet(p)) / 1073741824.0:F1} GB RAM" : isPending ? "Waiting for window" : "";
            row.Usage.Visibility = string.IsNullOrEmpty(row.Usage.Text) ? Visibility.Collapsed : Visibility.Visible;
            row.Stop.IsEnabled = !stopping.Contains(profile.Slot);
            row.Stop.Header = stopping.Contains(profile.Slot) ? "Closing..." : "Close game";
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
    public async Task<bool> Launch(int slot, bool fromBatch = false, bool useCurrentUser = false)
    {
        if (batchLaunching && !fromBatch) return false;
        var profile = settings.Profiles.FirstOrDefault(p => p.Slot == slot);
        if (profile == null) { Say("That instance is not in your saved profiles.",true); return false; }
        if (stopping.Contains(slot)) return false;
        if (starting.Contains(slot) || pending.ContainsKey(slot)) return true;
        var existing = Game.Find(profile,settings.GameFolder);
        if (existing.Count > 0)
        {
            var expectedSid = useCurrentUser || profile.WindowsUser.Length == 0
                ? System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value : LoginDiagnostics.ExpectedSid(profile.WindowsUser);
            var matches = existing.All(p => LoginDiagnostics.ProcessSid(p.Id) == expectedSid);
            existing.ForEach(p => p.Dispose());
            if (!matches) { Say("This game is already running under a different or unverified Windows user. Close this game window completely, then Launch again. Its login has not been isolated.", true); return false; }
            if (useCurrentUser) { Say("Close this game instance before launching it with your normal Windows login.", true); return false; }
            if (profile.WindowsUser.Length > 0)
            {
                try { await BrowserRequestRelay.Ensure(settings.GameFolder); }
                catch (Exception e) { Say(e.Message, true); return false; }
            }
            if (!fromBatch) Focus(profile);
            if (profile.WindowsUser.Length == 0 && !useCurrentUser)
                Say("This running instance still shares your Windows login. Close it, then Launch to set up its own remembered sign-in.", true);
            return true;
        }
        starting.Add(slot); Refresh();
        try
        {
            var usage = Native.ReadUsage().Memory;
            if ((usage.AvailablePageFile < 5UL * 1024 * 1024 * 1024 || usage.AvailablePhysical < 3UL * 1024 * 1024 * 1024) && MessageBox.Show(this,"Memory is tight. Another Eternal Love instance may need 3–5 GB and could cause stuttering or fail to start.\n\nLaunch this instance anyway?","Low memory headroom",MessageBoxButton.YesNo,MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
            if (profile.WindowsUser.Length == 0 && !useCurrentUser)
            {
                Say($"Setting up separate remembered sign-in for Instance {slot}. Windows may request administrator approval.");
                await Task.Run(() => InstanceAccounts.Ensure(profile.ProcessName));
                profile.WindowsUser = profile.ProcessName;
                try { Store.Save(settings); }
                catch { profile.WindowsUser = ""; throw; }
                RenderCards();
            }
            LoginIdentity? identity = null;
            var launchSettings = profile.LaunchSettings;
            launchSettings.Validate();
            if (profile.WindowsUser.Length > 0 && !useCurrentUser)
            {
                await BrowserRequestRelay.Ensure(settings.GameFolder);
                identity = await LoginDiagnostics.Probe(profile.WindowsUser, prepareWindowed: true, launchSettings);
                LoginDiagnostics.ValidateStorage(profile.LoginStorageId, identity.RegistryProfileId);
                if (profile.LoginStorageId.Length == 0)
                {
                    profile.LoginStorageId = identity.RegistryProfileId;
                    try { Store.Save(settings); }
                    catch { profile.LoginStorageId = ""; throw; }
                }
            }
            if (identity == null) GameDisplay.Prepare(launchSettings);
            Directory.CreateDirectory(Store.Root);
            File.WriteAllText(Path.Combine(Store.Root, $"display-launch-{slot}.json"), JsonSerializer.Serialize(new {
                Launcher = Environment.ProcessPath, AtUtc = DateTime.UtcNow, profile.WindowsUser, UseCurrentUser = useCurrentUser,
                Arguments = GameDisplay.ArgumentsFor(launchSettings), LaunchSettings = launchSettings,
                DisplayPreferences = identity?.DisplayPreferences ?? GameDisplay.ReadPreferences()
            }, new JsonSerializerOptions { WriteIndented = true }));
            var start = Game.StartInfo(profile,settings.GameFolder,useCurrentUser,launchSettings);
            if (identity != null) LoginDiagnostics.ApplyEnvironment(start, identity);
            await Task.Run(() => Game.EnsureFiles(profile,settings.GameFolder));
            Process process;
            try { process = Process.Start(start) ?? throw new IOException("Windows could not start this instance."); }
            finally { start.Password?.Dispose(); }
            pending[slot] = DateTime.UtcNow;
            Say(useCurrentUser ? $"Starting Instance {slot} with your normal Windows login. Remembered game login is shared for this launch." : $"Starting Instance {slot}.");
            _ = GameDisplay.WatchStartup(process, identity?.Sid ?? System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, slot, launchSettings);
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
        try { editor.PrepareAccount(); Store.Save(settings); RenderCards(); Refresh(); Say($"Saved Instance {profile.Slot}. Launch settings apply next time."); }
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
        try { editor.PrepareAccount(); Store.Save(settings); RenderCards(); Refresh(); Say($"Instance {next} saved. Select Launch when you are ready to open the game."); }
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
    readonly Profile profile;
    public void PrepareAccount()
    {
        if (profile.WindowsUser.Length > 0) return;
        InstanceAccounts.Ensure(profile.ProcessName);
        profile.WindowsUser = profile.ProcessName;
    }
    public ProfileEditor(Profile profile)
    {
        this.profile = profile;
        Title = "Eternal Love · Edit Instance " + profile.Slot; Icon = Branding.Icon; Width = 520; Height = 490; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(13,22,38)); FontFamily = new FontFamily("Segoe UI");
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(LauncherWindow.Text("Instance " + profile.Slot,24));
        TextBox Field(string label, string value)
        {
            var text = LauncherWindow.Text(label,12,"#AAB6CC"); text.Margin = new Thickness(0,16,0,6); panel.Children.Add(text);
            var box = new TextBox { Text = value, FontSize = 14, Padding = new Thickness(9), MaxLength = 80 }; panel.Children.Add(box); return box;
        }
        var name = Field("Profile name  ·  appears in the game title",profile.Name);
        var account = Field("Account label or email  ·  optional",profile.Account);
        var server = Field("Server / character note  ·  optional",profile.Server);
        Height = 650; Width = 600; MaxHeight = SystemParameters.WorkArea.Height - 32;
        var launchTitle = LauncherWindow.Text("Game launch settings", 16);
        launchTitle.Margin = new Thickness(0,22,0,8); panel.Children.Add(launchTitle);
        var choices = new UniformGridCompat(3);
        ComboBox Choice(string label, string[] values, int selected)
        {
            var group = new StackPanel { Margin = new Thickness(0,0,10,0) };
            var box = new ComboBox { ItemsSource = values, SelectedIndex = selected, FontSize = 14,
                Padding = new Thickness(8), MinHeight = 36, HorizontalContentAlignment = HorizontalAlignment.Left };
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            group.Children.Add(new Label { Content = label, Target = box, Foreground = new SolidColorBrush(Color.FromRgb(170,182,204)), Padding = new Thickness(0,0,0,6) });
            group.Children.Add(box); choices.Children.Add(group); return box;
        }
        var display = Choice("Display", new[] { "540p (windowed)", "720p (windowed)", "Fullscreen" }, (int)profile.LaunchSettings.Display);
        var graphics = Choice("Graphics", new[] { "Low", "Medium", "High" }, (int)profile.LaunchSettings.Graphics);
        var fps = Choice("FPS", new[] { "Low · 30 FPS", "Medium · 45 FPS", "High · 60 FPS" }, (int)profile.LaunchSettings.Fps);
        panel.Children.Add(choices);
        var launchHint = LauncherWindow.Text("Applied on the next launch. Defaults: 540p, low graphics, low FPS.",12,"#AAB6CC");
        launchHint.Margin = new Thickness(0,10,0,0); launchHint.TextWrapping = TextWrapping.Wrap; panel.Children.Add(launchHint);
        var explanation = LauncherWindow.Text(profile.WindowsUser.Length == 0
            ? "This instance remembers its own game login. Saving will ask for Windows administrator approval to finish setup. Sign into ROM once after setup."
            : "This instance remembers its own game login. ROM may ask you to sign in again if your session expires.",12,"#AAB6CC");
        explanation.Margin = new Thickness(0,18,0,0); explanation.TextWrapping = TextWrapping.Wrap; panel.Children.Add(explanation);
        var hint = LauncherWindow.Text("Windows credentials stay in Credential Manager. Enter game passwords only in ROM.",12,"#AAB6CC"); hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0,16,0,16); panel.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(18,9,18,9), Margin = new Thickness(0,0,8,0), IsCancel = true }; buttons.Children.Add(cancel);
        var save = new Button { Content = "Save profile", Padding = new Thickness(18,9,18,9), IsDefault = true };
        save.Click += (_, _) => {
            profile.LaunchSettings = new GameLaunchSettings { Display = (GameDisplayMode)display.SelectedIndex,
                Graphics = (PerformanceLevel)graphics.SelectedIndex, Fps = (PerformanceLevel)fps.SelectedIndex };
            profile.Name = name.Text.Trim(); profile.Account = account.Text.Trim(); profile.Server = server.Text.Trim(); DialogResult = true;
        };
        buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
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
            var originalWinDir = Environment.GetEnvironmentVariable("windir");
            try
            {
                Environment.SetEnvironmentVariable("windir", null);
                StartupEnvironment.Initialize();
                Check(Path.IsPathFullyQualified(Environment.GetEnvironmentVariable("windir")!) && Directory.Exists(Path.Combine(Environment.GetEnvironmentVariable("windir")!, "Fonts")), "missing Windows font environment restored");
                Environment.SetEnvironmentVariable("windir", "invalid-relative-directory");
                StartupEnvironment.Initialize();
                Check(Path.IsPathFullyQualified(Environment.GetEnvironmentVariable("windir")!), "invalid Windows font environment restored");
            }
            finally { Environment.SetEnvironmentVariable("windir", originalWinDir); }
            Check(settings.Profiles[0].ProcessName == "ro_win" && settings.Profiles[3].ProcessName == "ro_win3","instance-to-executable mapping");
            settings.Profiles[0].Name = "Main account";
            Check(settings.Profiles[0].Title == "Ragnarok M: Eternal Love - Instance 1 | Main account","account window title");
            var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
            Store.Validate(copy); Check(copy.Profiles[0].Name == "Main account","profile JSON round-trip");
            copy.Profiles[1].Slot = 1;
            bool rejected = false; try { Store.Validate(copy); } catch (InvalidDataException) { rejected = true; } Check(rejected,"duplicate instance rejected");
            var legacy = JsonSerializer.Deserialize<Settings>("""{"Profiles":[{"Slot":3,"Name":"Main","Account":"user@example.invalid","Server":"A","Isolated":true,"Sandbox":"RO3"}]}""")!;
            Store.Validate(legacy);
            Check(legacy.Profiles[0].WindowsUser == "", "legacy profiles retain current Windows user");
            Check(legacy.Profiles[0].LaunchSettings == new GameLaunchSettings(), "legacy profiles default to windowed 540p and low graphics and FPS");
            var configured = new Profile { Slot = 1, LaunchSettings = new GameLaunchSettings {
                Display = GameDisplayMode.Windowed720p, Graphics = PerformanceLevel.High, Fps = PerformanceLevel.Medium } };
            Check(JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(configured))!.LaunchSettings == configured.LaunchSettings,
                "instance launch settings survive JSON round-trip");
            Check(Game.StartInfo(configured, settings.GameFolder).ArgumentList.Contains("1280"), "launch uses the instance display choice");
            var independentSettings = GameDisplay.MergeGraphicsSettings("", configured.LaunchSettings);
            Check(independentSettings.Contains("qualityLevel=3") && independentSettings.Contains("targetFrameRate=3"),
                "graphics and FPS can be selected independently");
            Check(GameDisplay.DesktopHeight() > 0, "native desktop height is available for ROM resolution mapping");
            var probeStart = new ProcessStartInfo();
            LoginDiagnostics.ConfigureProbeArguments(probeStart, true, configured.LaunchSettings);
            Check(probeStart.ArgumentList[0] == "--prepare-game-user"
                && JsonSerializer.Deserialize<GameLaunchSettings>(probeStart.ArgumentList[1]) == configured.LaunchSettings,
                "hidden-user preparation receives the same instance settings");
            var invalidProbe = new ProcessStartInfo(Environment.ProcessPath!) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
            };
            invalidProbe.ArgumentList.Add("--prepare-game-user");
            invalidProbe.ArgumentList.Add("{\"Fps\":99}");
            using (var helper = Process.Start(invalidProbe)!)
            {
                var error = helper.StandardError.ReadToEndAsync();
                if (!helper.WaitForExit(10000)) { helper.Kill(); throw new IOException("Profile error reporting test timed out."); }
                Check(helper.ExitCode == 1 && error.GetAwaiter().GetResult().Contains("Reading instance launch settings: Choose 540p"),
                    "profile helper reports the underlying failure without loading or changing a profile");
            }
            Check(GameDisplay.ResolutionIndex(GameDisplayMode.Windowed540p, 1080) == 3
                && GameDisplay.ResolutionIndex(GameDisplayMode.Windowed720p, 1080) == 2
                && GameDisplay.ResolutionIndex(GameDisplayMode.Windowed540p, 2160) == 5
                && GameDisplay.ResolutionIndex(GameDisplayMode.Windowed720p, 2160) == 4
                && GameDisplay.ResolutionIndex(GameDisplayMode.Fullscreen, 2160) == 1,
                "ROM resolution IDs follow its desktop-dependent resolution list");
            foreach (var level in Enum.GetValues<PerformanceLevel>())
            {
                var options = new GameLaunchSettings { Graphics = level, Fps = level };
                var merged = GameDisplay.MergeGraphicsSettings("soundVolume=0.25", options);
                var fpsId = level == PerformanceLevel.Medium ? 3 : level == PerformanceLevel.High ? 2 : 1;
                Check(merged.Contains($"qualityLevel={(int)level + 1}") && merged.Contains($"targetFrameRate={fpsId}")
                    && merged.Contains($"shadowRate={(int)level}") && merged.Contains($"effectLv={(int)level}")
                    && merged.Contains("soundVolume=0.25"), $"{level} graphics and FPS use verified ROM preset values");
            }
            var fullOptions = new GameLaunchSettings { Display = GameDisplayMode.Fullscreen };
            var fullPrefs = new Dictionary<string, int>();
            GameDisplay.WritePreferences(Array.Empty<string>(), (key, value) => fullPrefs[key] = value, fullOptions);
            Check(GameDisplay.ArgumentsFor(fullOptions).SequenceEqual(new[] { "-screen-fullscreen", "1" })
                && fullPrefs["WindowsResolution_h2966854996"] == 1 && fullPrefs["fullScreen_h4289535994"] == 1
                && fullPrefs["Screenmanager Resolution Use Native_h1405027254"] == 1
                && !fullPrefs.ContainsKey("Screenmanager Resolution Width_h182942802"), "fullscreen uses native resolution without window-size overrides");
            bool invalidLaunchRejected = false;
            try { (new GameLaunchSettings { Fps = (PerformanceLevel)99 }).Validate(); }
            catch (InvalidDataException) { invalidLaunchRejected = true; }
            Check(invalidLaunchRejected, "invalid persisted launch options are rejected");
            Check(Game.StartInfo(new Profile { Slot = 1 }, settings.GameFolder).ArgumentList.SequenceEqual(new[] { "-screen-fullscreen", "0", "-screen-width", "960", "-screen-height", "540" }),
                "new and legacy instances launch windowed before separate login setup");
            Check(LoginBrowser.Command(@"C:\Launcher Folder\Launcher.exe", "ROInstanceSSO_test") == "\"C:\\Launcher Folder\\Launcher.exe\" --open-sso-url ROInstanceSSO_test \"%1\"", "SSO browser command quotes executable and URL");
            var browserProbe = new ProcessStartInfo();
            browserProbe.Environment["USERPROFILE"] = @"C:\Users\LauncherOwner";
            browserProbe.Environment["LOCALAPPDATA"] = @"C:\Users\LauncherOwner\AppData\Local";
            browserProbe.Environment["APPDATA"] = @"C:\Users\LauncherOwner\AppData\Roaming";
            browserProbe.Environment["SystemRoot"] = @"C:\Windows";
            var probeIdentity = LoginDiagnostics.Capture();
            LoginBrowser.ConfigureProbe(browserProbe, probeIdentity);
            var bundleProbe = new ProcessStartInfo(Environment.ProcessPath!) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            LoginBrowser.ConfigureProbe(bundleProbe, probeIdentity);
            bundleProbe.ArgumentList.Add("--probe-instance-user");
            using (var helper = Process.Start(bundleProbe)!)
            {
                var output = helper.StandardOutput.ReadToEndAsync();
                var error = helper.StandardError.ReadToEndAsync();
                if (!helper.WaitForExit(10000)) { helper.Kill(); throw new IOException("Bundled profile test timed out."); }
                Check(helper.ExitCode == 0 && JsonSerializer.Deserialize<LoginIdentity>(output.GetAwaiter().GetResult())?.Sid
                    == System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value,
                    "bundled profile helper starts with the browser environment: " + error.GetAwaiter().GetResult());
            }
            Check(browserProbe.Environment["USERPROFILE"] == probeIdentity.ProfilePath
                && browserProbe.Environment["LOCALAPPDATA"] == probeIdentity.LocalAppDataPath
                && browserProbe.Environment["APPDATA"] == probeIdentity.RoamingAppDataPath
                && browserProbe.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] == Path.Combine(probeIdentity.LocalAppDataPath, "ROInstanceLaunchers", "BundleCache")
                && browserProbe.Environment["SystemRoot"] == @"C:\Windows",
                "SSO preparation uses verified profile folders and a private bundle cache");
            Check(LoginBrowser.ValidUrl("https://accounts.google.com/o/oauth2/auth?state=test") && !LoginBrowser.ValidUrl("file:///C:/test") && !LoginBrowser.ValidUrl("https://example.com/\nargument") && !LoginBrowser.ValidUrl("https://user:password@example.com"), "SSO forwarding accepts web URLs and rejects non-web commands and malformed input");
            Check(BrowserRequestRelay.WebRequest(@"C:\Windows\System32\OpenWith.exe", "\"C:\\Windows\\System32\\OpenWith.exe\" \"https://example.com/login?state=a%2Bb&redirect_uri=http%3A%2F%2Flocalhost\"")
                == "https://example.com/login?state=a%2Bb&redirect_uri=http%3A%2F%2Flocalhost", "browser fallback preserves the exact sign-in URL");
            Check(BrowserRequestRelay.WebRequest("rundll32.exe", "rundll32.exe url.dll,FileProtocolHandler \"https://example.com/login?state=a%2Bb&provider=google\"")
                == "https://example.com/login?state=a%2Bb&provider=google", "Unity rundll32 web requests reach the current user's browser relay");
            Check(BrowserRequestRelay.WebRequest("rundll32.exe", "rundll32.exe shell32.dll,Control_RunDLL https://example.com") == null,
                "browser relay rejects unrelated rundll32 entry points");
            Check(BrowserRequestRelay.WebRequest("cmd.exe", "cmd.exe https://example.com") == null
                && BrowserRequestRelay.WebRequest("OpenWith.exe", "OpenWith.exe C:\\private.txt") == null
                && BrowserRequestRelay.WebRequest("OpenWith.exe", "OpenWith.exe https://first.example https://second.example") == null,
                "browser fallback rejects unrelated programs, local files, and ambiguous URL requests");
            Check(!BrowserRequestRelay.ManagedParent(Environment.ProcessId, AppContext.BaseDirectory), "browser fallback rejects non-game parent processes");
            Check(BrowserRequestRelay.TestHandshake().GetAwaiter().GetResult(), "browser helper handshake completes without both pipe endpoints blocking on startup");
            Check(SignInBrowser.SameBrowser(@"C:\Browser\browser.exe", "owner", @"c:\browser\BROWSER.EXE", "owner")
                && !SignInBrowser.SameBrowser(@"C:\Browser\browser.exe", "game-user", @"C:\Browser\browser.exe", "owner")
                && !SignInBrowser.SameBrowser(@"C:\Other\browser.exe", "owner", @"C:\Browser\browser.exe", "owner"),
                "sign-in focus targets only the current owner's default browser executable");
            Check(SignInBrowser.MayFocus(new IntPtr(1), new IntPtr(1), new IntPtr(2))
                && !SignInBrowser.MayFocus(new IntPtr(1), new IntPtr(3), new IntPtr(2)),
                "delayed browser focus respects a switch to an unrelated window");
            Check(GameDisplay.PreferenceValue("Screenmanager Fullscreen mode_h123") == 3
                && GameDisplay.PreferenceValue("Screenmanager Is Fullscreen mode_h456") == 0
                && GameDisplay.PreferenceValue("Screenmanager Resolution Width_h123") == 960
                && GameDisplay.PreferenceValue("Screenmanager Resolution Height_h456") == 540
                && GameDisplay.PreferenceValue("Screenmanager Resolution Use Native_h123") == 0,
                "saved fullscreen and native-resolution preferences are reset to a bounded window");
            Check(GameDisplay.PreferenceValue("account_h123") == null && GameDisplay.PreferenceValue("session_h456") == null
                && GameDisplay.PreferenceValue("Screenmanager unrelated_h123") == null, "window mode preparation leaves login and unrelated preferences untouched");
            var freshDisplay = new Dictionary<string, int>();
            GameDisplay.WritePreferences(Array.Empty<string>(), (name, value) => freshDisplay[name] = value);
            Check(freshDisplay["Screenmanager Fullscreen mode_h3630240806"] == 3
                && freshDisplay["Screenmanager Is Fullscreen mode_h3981298716"] == 0
                && freshDisplay["Screenmanager Resolution Width_h182942802"] == 960
                && freshDisplay["Screenmanager Resolution Height_h2627697771"] == 540,
                "fresh Windows user receives windowed settings even without an existing game registry");
            var existingDisplay = new Dictionary<string, int> { ["session_h123"] = 42, ["Screenmanager Fullscreen mode_h3630240806"] = 1 };
            var lowSettings = GameDisplay.MergeGraphicsSettings("bgmVolume=0.5,qualityLevel=3,targetFrameRate=3,autoPlayChatChannel={qualityLevel=9,other=2},custom='a,b'");
            Check(lowSettings.Contains("qualityLevel=1,targetFrameRate=1") && lowSettings.Contains("bgmVolume=0.5")
                && lowSettings.Contains("autoPlayChatChannel={qualityLevel=9,other=2},custom='a,b'"),
                "low graphics and FPS preserve unrelated and nested settings");
            Check(GameDisplay.MergeGraphicsSettings(lowSettings) == lowSettings
                && GameDisplay.MergeGraphicsSettings("").Contains("targetFrameRate=1"), "startup settings seed new users and remain idempotent");
            bool malformedSettingsRejected = false;
            try { GameDisplay.MergeGraphicsSettings("custom={broken"); } catch (IOException) { malformedSettingsRejected = true; }
            Check(malformedSettingsRejected, "malformed ROM settings are rejected before writing");
            Check(GameDisplay.Arguments.SequenceEqual(new[] { "-screen-fullscreen", "0", "-screen-width", "960", "-screen-height", "540" })
                && freshDisplay["WindowsResolution_h2966854996"] == 3, "launch arguments and ROM resolution select 540p");
            GameDisplay.WritePreferences(existingDisplay.Keys.ToArray(), (name, value) => existingDisplay[name] = value);
            Check(existingDisplay["session_h123"] == 42 && existingDisplay["Screenmanager Fullscreen mode_h3630240806"] == 3,
                "existing user fullscreen is corrected without replacing sign-in preferences");
            Check(freshDisplay["fullScreen_h4289535994"] == 0 && GameDisplay.PreferenceValue("fullScreen") == 0,
                "ROM fullscreen preference is disabled alongside Unity display preferences");
            Check(GameDisplay.CoversMonitor(new GameDisplay.Rect { Left = -1920, Top = 0, Right = 0, Bottom = 1080 },
                new GameDisplay.Rect { Left = -1920, Top = 0, Right = 0, Bottom = 1080 })
                && !GameDisplay.CoversMonitor(new GameDisplay.Rect { Left = 10, Top = 30, Right = 1290, Bottom = 750 },
                new GameDisplay.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }), "runtime display check distinguishes fullscreen on a secondary monitor from a bounded game window");
            Check(LoginDiagnostics.RegistryMatchesSid(@"\REGISTRY\USER\S-1-5-21-123\Software", "S-1-5-21-123")
                && !LoginDiagnostics.RegistryMatchesSid(@"\REGISTRY\USER\S-1-5-21-456\Software", "S-1-5-21-123"), "registry ownership rejects another user's hive");
            var storageId = Guid.NewGuid().ToString("D");
            LoginDiagnostics.ValidateStorage("", storageId);
            LoginDiagnostics.ValidateStorage(storageId, storageId);
            bool resetRejected = false;
            try { LoginDiagnostics.ValidateStorage(storageId, Guid.NewGuid().ToString("D")); } catch (IOException) { resetRejected = true; }
            Check(resetRejected, "registry profile reset blocks launch instead of silently losing remembered sign-in");
            var storageProfile = new Profile { Slot = 2, LoginStorageId = storageId };
            Check(JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(storageProfile))!.LoginStorageId == storageId, "registry storage identity persists across launcher reloads");
            Check(InstanceAccounts.ValidName("ro_win") && InstanceAccounts.ValidName("ro_win11") && !InstanceAccounts.ValidName("ro_win12") && !InstanceAccounts.ValidName("ro_win01") && !InstanceAccounts.ValidName("Administrator"), "managed username bounds");
            var invalidUser = new Settings { Profiles = new() { new Profile { Slot = 2, WindowsUser = "Administrator" } } };
            bool invalidUserRejected = false; try { Store.Validate(invalidUser); } catch (InvalidDataException) { invalidUserRejected = true; }
            Check(invalidUserRejected, "unrelated Windows account rejected");
            var recovery = Game.StartInfo(new Profile { Slot = 2, WindowsUser = "ro_win1" }, settings.GameFolder, useCurrentUser: true);
            Check(recovery.UseShellExecute && recovery.UserName.Length == 0 && recovery.ArgumentList.SequenceEqual(new[] { "-screen-fullscreen", "0", "-screen-width", "960", "-screen-height", "540" }), "SSO recovery launches windowed under current user without hidden credentials");
            var isolated = InstanceAccounts.ConfigureWithPassword(new ProcessStartInfo("game.exe") { WorkingDirectory = "C:\\Game" }, "ro_win1", "test-only");
            Check(!isolated.UseShellExecute && isolated.LoadUserProfile && isolated.UserName == "ro_win1" && isolated.Domain == Environment.MachineName && isolated.Password!.Length == 9 && isolated.WorkingDirectory == "C:\\Game" && isolated.ArgumentList.Count == 0, "isolated launch loads user profile without command-line password");
            isolated.Password!.Dispose();
            var targetIdentity = new LoginIdentity(Environment.MachineName + "\\ro_win1", "test-sid", @"C:\Users\ro_win1",
                @"C:\Users\ro_win1\AppData\LocalLow", @"C:\Users\ro_win1\AppData\Local", @"C:\Users\ro_win1\AppData\Roaming", false, 0, null);
            var targetStart = new ProcessStartInfo("game.exe");
            targetStart.Environment["USERPROFILE"] = @"C:\Users\LauncherOwner";
            targetStart.Environment["APPDATA"] = @"C:\Users\LauncherOwner\AppData\Roaming";
            LoginDiagnostics.ApplyEnvironment(targetStart, targetIdentity);
            Check(targetStart.Environment["USERPROFILE"] == targetIdentity.ProfilePath && targetStart.Environment["APPDATA"] == targetIdentity.RoamingAppDataPath
                && targetStart.Environment["LOCALAPPDATA"] == targetIdentity.LocalAppDataPath && targetStart.Environment["TEMP"] == @"C:\Users\ro_win1\AppData\Local\Temp"
                && targetStart.Environment["TMP"] == targetStart.Environment["TEMP"] && targetStart.Environment["USERNAME"] == "ro_win1"
                && targetStart.Environment["HOMEDRIVE"] == "C:" && targetStart.Environment["HOMEPATH"] == @"\Users\ro_win1",
                "separate login replaces launcher owner profile and browser environment");
            var capturedIdentity = LoginDiagnostics.Capture();
            Check(Path.IsPathFullyQualified(capturedIdentity.ProfilePath) && Path.IsPathFullyQualified(capturedIdentity.LocalAppDataPath)
                && Path.IsPathFullyQualified(capturedIdentity.RoamingAppDataPath), "login diagnostic resolves native Windows profile folders");
            var managed = new Profile { Slot = 2, WindowsUser = "ro_win1" };
            Check(JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(managed))!.WindowsUser == "ro_win1" && !JsonSerializer.Serialize(managed).Contains("Password"), "managed profile persists username without credentials");
            Check(legacy.Profiles[0].Name == "Main" && legacy.Profiles[0].Account == "user@example.invalid" && legacy.Profiles[0].Server == "A", "legacy profile labels preserved");
            var migrated = JsonSerializer.Serialize(legacy);
            Check(!migrated.Contains("Isolated") && !migrated.Contains("Sandbox"), "obsolete isolation settings removed on save");
            var legacyStart = Game.StartInfo(legacy.Profiles[0], legacy.GameFolder);
            Check(legacyStart.FileName.EndsWith("ro_win2.exe") && legacyStart.UseShellExecute && legacyStart.ArgumentList.SequenceEqual(new[] { "-screen-fullscreen", "0", "-screen-width", "960", "-screen-height", "540" }), "legacy isolated profile launches game directly in windowed mode");
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
                var content = (StackPanel)((ScrollViewer)editor.Content).Content;
                var fields = content.Children.OfType<TextBox>().ToArray();
                fields[0].Text = "  Saved main  "; fields[1].Text = "user@example.invalid"; fields[2].Text = "Server A";
                var options = content.Children.OfType<UniformGridCompat>().Single().Children.OfType<StackPanel>()
                    .Select(group => group.Children.OfType<ComboBox>().Single()).ToArray();
                options[0].SelectedIndex = 2; options[1].SelectedIndex = 1; options[2].SelectedIndex = 2;
                var actions = content.Children.OfType<StackPanel>().Last();
                actions.Children.OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Check(editor.ShowDialog() == true && edited.Name == "Saved main" && edited.Account == "user@example.invalid" && edited.Server == "Server A", "profile editor Save interaction");
            Check(edited.LaunchSettings == new GameLaunchSettings { Display = GameDisplayMode.Fullscreen,
                Graphics = PerformanceLevel.Medium, Fps = PerformanceLevel.High }, "editor saves all three instance launch choices");
            var cancelled = new Profile { Slot = 2, Name = "Original" };
            var cancelEditor = new ProfileEditor(cancelled);
            cancelEditor.Loaded += (_, _) => cancelEditor.Dispatcher.BeginInvoke(new Action(() => {
                ((StackPanel)((ScrollViewer)cancelEditor.Content).Content).Children.OfType<TextBox>().First().Text = "Unsaved";
                ((StackPanel)((ScrollViewer)cancelEditor.Content).Content).Children.OfType<UniformGridCompat>().Single().Children.OfType<StackPanel>()
                    .First().Children.OfType<ComboBox>().Single().SelectedIndex = 1;
                cancelEditor.DialogResult = false;
            }));
            Check(cancelEditor.ShowDialog() == false && cancelled.Name == "Original" && cancelled.LaunchSettings == new GameLaunchSettings(), "profile editor cancellation preserves values");
            LauncherWindow.TestSelection(Check);
            UpdateTests.Run(Check);
            Directory.CreateDirectory(Store.Root); File.WriteAllLines(Path.Combine(Store.Root,"self-test.txt"),report); return 0;
        }
        catch (Exception e) { report.Add(e.ToString()); Directory.CreateDirectory(Store.Root); File.WriteAllLines(Path.Combine(Store.Root,"self-test.txt"),report); return 1; }
    }
}
