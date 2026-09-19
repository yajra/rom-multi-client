using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ROInstances;

public sealed class Profile
{
    public int Slot { get; set; }
    public string Name { get; set; } = "";
    public string Account { get; set; } = "";
    public string Server { get; set; } = "";
    public bool Isolated { get; set; }
    public string Sandbox { get; set; } = "";
    public string ProcessName => Slot == 1 ? "ro_win" : "ro_win" + (Slot - 1);
    public string Title => $"Ragnarok M - Instance {Slot}" + (string.IsNullOrWhiteSpace(Name) ? "" : " | " + Name);
}

public sealed class Settings
{
    public string GameFolder { get; set; } = @"C:\Program Files (x86)\XD\ROLauncher";
    public List<Profile> Profiles { get; set; } = new();
}

public static class Store
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ROInstanceLaunchers");
    public static readonly string FilePath = Path.Combine(Root, "profiles.json");
    public static Settings Load(string? filePath = null)
    {
        var target = filePath ?? FilePath;
        if (!File.Exists(target)) return new Settings { Profiles = Enumerable.Range(1,4).Select(i => new Profile { Slot = i, Sandbox = "RO" + i }).ToList() };
        var value = JsonSerializer.Deserialize<Settings>(File.ReadAllText(target)) ?? throw new InvalidDataException("The profile file is empty.");
        Validate(value);
        return value;
    }
    public static void Validate(Settings settings)
    {
        if (settings.Profiles.Count == 0 || settings.Profiles.Any(p => p.Slot < 1 || p.Slot > 12) || settings.Profiles.Select(p => p.Slot).Distinct().Count() != settings.Profiles.Count)
            throw new InvalidDataException("Profiles must have unique instance numbers from 1 to 12.");
        if (settings.Profiles.Any(p => p.Name == null || p.Account == null || p.Server == null || p.Sandbox == null))
            throw new InvalidDataException("A profile contains an invalid empty field.");
        foreach (var profile in settings.Profiles.Where(p => p.Isolated)) Sandboxie.ValidateName(profile.Sandbox);
        var boxes = settings.Profiles.Where(p => p.Isolated).Select(p => p.Sandbox).ToArray();
        if (boxes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != boxes.Length)
            throw new InvalidDataException("Use a different sandbox for each account.");
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
    public static string? SandboxiePath => new[] { @"C:\Program Files\Sandboxie-Plus\Start.exe", @"C:\Program Files\Sandboxie\Start.exe" }.FirstOrDefault(File.Exists);
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
        if (!File.Exists(original) || !Directory.Exists(shared)) throw new IOException("Choose the game folder containing ro_win.exe and ro_win_Data.");
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
        if (!profile.Isolated) return new ProcessStartInfo(game) { WorkingDirectory = folder, UseShellExecute = true };
        var sandboxie = SandboxiePath ?? throw new IOException("Sandboxie Plus is not installed. Open Account setup for installation and sandbox instructions, or edit this profile to use Shared login.");
        var info = new ProcessStartInfo(sandboxie) { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("/box:" + profile.Sandbox);
        info.ArgumentList.Add(game);
        return info;
    }
}

public static class Program
{
    public static readonly string PipeName = "ROInstances_" + Environment.UserName;
    [STAThread] public static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTest.Run();
        if (args.Length == 2 && args[0] == "--prepare-sandbox")
        {
            Directory.CreateDirectory(Store.Root);
            try
            {
                var start = Game.SandboxiePath ?? throw new IOException("Sandboxie is not installed.");
                var sandboxCreated = Sandboxie.EnsureAsync(args[1], start).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(Store.Root,"sandbox-setup.txt"), $"{args[1]}: " + (sandboxCreated ? "created with persistent storage." : "existing persistent sandbox verified."));
                return 0;
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(Store.Root,"sandbox-setup.txt"),error.Message); return 1; }
        }
        var preview = args.Length == 2 && (args[0] == "--preview" || args[0] == "--preview-editor");
        var launchSlot = args.Length == 2 && args[0] == "--launch" && int.TryParse(args[1], out var slot) && slot >= 1 && slot <= 12 ? slot : 0;
        using var single = new Mutex(true, "Local\\" + PipeName, out var created);
        if (!created && !preview)
        {
            try { using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out); pipe.Connect(2500); using var writer = new StreamWriter(pipe); writer.WriteLine(launchSlot); }
            catch { MessageBox.Show("The launcher is already starting. Open it from the notification area in a moment.", "RO Instance Launcher"); }
            return 0;
        }
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { MessageBox.Show(e.Exception.Message, "RO Instance Launcher"); e.Handled = true; };
        try
        {
            var window = new LauncherWindow(preview);
            app.MainWindow = window;
            if (preview)
            {
                Window target = args[0] == "--preview-editor" ? new ProfileEditor(new Profile { Slot = 1, Sandbox = "RO1", Name = "Main account" }) : window;
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
        catch (Exception e) { MessageBox.Show(e.Message + "\n\nYour saved profiles have not been replaced.", "RO Instance Launcher"); return 1; }
        return 0;
    }
}

public sealed class LauncherWindow : Window
{
    readonly Settings settings;
    readonly bool preview;
    readonly StackPanel cards = new();
    readonly TextBlock cpuValue = Text("Measuring…", 22, "#F4F7FC");
    readonly TextBlock ramValue = Text("—", 22, "#F4F7FC");
    readonly TextBlock commitValue = Text("—", 22, "#F4F7FC");
    readonly TextBlock countValue = Text("—", 22, "#F4F7FC");
    readonly TextBlock feedback = Text("Ready. Your existing game windows stay open.", 12, "#AAB6CC");
    readonly Dictionary<int, (TextBlock Status, TextBlock Usage, Button Launch, Button Focus, Button Stop)> rows = new();
    readonly Dictionary<int, DateTime> pending = new();
    readonly HashSet<int> starting = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    Forms.NotifyIcon? tray;
    public bool AllowExit;
    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    public static TextBlock Text(string text, double size = 14, string color = "#E9EFFA") => new() { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    static Border Panel(UIElement child, string background = "#172237", int padding = 18) => new() { Child = child, Background = Brush(background), CornerRadius = new CornerRadius(12), Padding = new Thickness(padding), BorderBrush = Brush("#2B3952"), BorderThickness = new Thickness(1) };
    static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(15,9,15,9), Margin = new Thickness(5,0,0,0), Background = Brush(primary ? "#8BE6CB" : "#26364F"), Foreground = Brush(primary ? "#102923" : "#F0F5FF"), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, FontSize = 13, MinHeight = 36 };
        button.Click += (_, _) => action(); return button;
    }
    public LauncherWindow(bool isPreview)
    {
        preview = isPreview;
        settings = Store.Load();
        if (!preview) Store.Save(settings);
        Title = "RO Instance Launcher"; Width = 1100; Height = 820; MinWidth = 950; MinHeight = 640;
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
        headerButtons.Children.Add(Button("Account setup", AccountSetup));
        headerButtons.Children.Add(Button("+ Add instance", AddProfile, true));
        DockPanel.SetDock(headerButtons, Dock.Right); header.Children.Add(headerButtons);
        var heading = new StackPanel();
        heading.Children.Add(Text("RO  /  MULTI-INSTANCE", 11, "#8BE6CB"));
        var title = Text("Your adventuring party", 28); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0,6,0,3); heading.Children.Add(title);
        heading.Children.Add(Text("One installation. A clear home for every account.", 13, "#AAB6CC")); header.Children.Add(heading);
        shell.Children.Add(header);
        var metrics = new UniformGridCompat(4) { Margin = new Thickness(0,0,0,20) };
        foreach (var item in new[] { ("RUNNING", countValue), ("CPU LOAD", cpuValue), ("AVAILABLE RAM", ramValue), ("MEMORY HEADROOM", commitValue) })
        { var stack = new StackPanel(); stack.Children.Add(Text(item.Item1, 10, "#AAB6CC")); item.Item2.Margin = new Thickness(0,7,0,0); stack.Children.Add(item.Item2); var panel = Panel(stack); panel.Margin = new Thickness(0,0,10,0); metrics.Children.Add(panel); }
        Grid.SetRow(metrics,1); shell.Children.Add(metrics);
        var intro = new DockPanel { Margin = new Thickness(0,0,0,12) };
        var subtitle = Text("ACCOUNT PROFILES", 11, "#AAB6CC"); intro.Children.Add(subtitle);
        var explain = Text("Edit a profile to save its name, account and server.", 12, "#AAB6CC"); explain.HorizontalAlignment = HorizontalAlignment.Right; intro.Children.Add(explain);
        Grid.SetRow(intro,2); shell.Children.Add(intro);
        var scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0,0,8,0) }; Grid.SetRow(scroll,3); shell.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0,14,0,0) };
        feedback.Margin = new Thickness(0,0,0,8); footer.Children.Add(feedback);
        footer.Children.Add(Text("Shared login uses the last account. Separate remembered logins require a persistent sandbox per profile.", 11, "#8D9DB7"));
        footer.Children.Add(Text("Closing this window keeps the launcher in the notification area while it manages your titles.", 11, "#8D9DB7"));
        Grid.SetRow(footer,4); shell.Children.Add(footer); Content = shell;
        RenderCards(); Refresh();
        if (!preview)
        {
            tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "RO Instance Launcher", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open launcher", null, (_, _) => Dispatcher.Invoke(Reveal));
            menu.Items.Add("Exit launcher (leave games running)", null, (_, _) => Dispatcher.Invoke(() => { AllowExit = true; Close(); System.Windows.Application.Current.Shutdown(); }));
            tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(Reveal);
            timer.Tick += (_, _) => Refresh(); timer.Start();
        }
        Closing += (_, e) => { if (!AllowExit) { e.Cancel = true; Hide(); } else { timer.Stop(); tray?.Dispose(); } };
    }
    public void Reveal() { Show(); WindowState = WindowState.Normal; Activate(); }
    void Say(string message, bool error = false) { feedback.Text = message; feedback.Foreground = Brush(error ? "#FFB4A6" : "#AAB6CC"); }
    void RenderCards()
    {
        cards.Children.Clear(); rows.Clear();
        foreach (var profile in settings.Profiles.OrderBy(p => p.Slot))
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var number = Text(profile.Slot.ToString("00"), 23, "#8BE6CB"); number.FontWeight = FontWeights.SemiBold; row.Children.Add(number);
            var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,12,0) };
            var name = Text(string.IsNullOrWhiteSpace(profile.Name) ? "Instance " + profile.Slot : profile.Name, 17); name.FontWeight = FontWeights.SemiBold; identity.Children.Add(name);
            var detail = string.IsNullOrWhiteSpace(profile.Account) ? "No account label yet" : profile.Account;
            if (!string.IsNullOrWhiteSpace(profile.Server)) detail += "  ·  " + profile.Server;
            identity.Children.Add(Text(detail,12,"#AAB6CC"));
            var mode = Text(profile.Isolated ? "Separate login  ·  " + profile.Sandbox : "Shared login",11,profile.Isolated ? "#8BE6CB" : "#EBCB8B"); mode.Margin = new Thickness(0,5,0,0); identity.Children.Add(mode);
            Grid.SetColumn(identity,1); row.Children.Add(identity);
            var state = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var status = Text("Stopped",12,"#AAB6CC"); var usage = Text("Ready to launch",11,"#8D9DB7"); usage.Margin = new Thickness(0,5,0,0); state.Children.Add(status); state.Children.Add(usage); Grid.SetColumn(state,2); row.Children.Add(state);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var edit = Button("Edit", () => Edit(profile));
            var focus = Button("Show", () => Focus(profile)); var stop = Button("Close", () => Stop(profile));
            var launch = Button("Launch", () => { _ = Launch(profile.Slot); }, true);
            actions.Children.Add(edit); actions.Children.Add(focus); actions.Children.Add(stop); actions.Children.Add(launch); Grid.SetColumn(actions,3); row.Children.Add(actions);
            rows[profile.Slot] = (status,usage,launch,focus,stop);
            var card = Panel(row); card.Margin = new Thickness(0,0,0,10); cards.Children.Add(card);
        }
    }
    void Refresh()
    {
        var (cpu,memory) = Native.ReadUsage();
        cpuValue.Text = double.IsNaN(cpu) ? "Measuring…" : $"{cpu:F0}%";
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
            else if (pending.ContainsKey(profile.Slot) && !isPending) { pending.Remove(profile.Slot); Say($"Instance {profile.Slot} did not appear. Check the game or Sandboxie message before trying again.",true); }
            row.Status.Text = processes.Count > 0 ? "● Running" : isPending || starting.Contains(profile.Slot) ? "Starting…" : "○ Stopped";
            row.Status.Foreground = Brush(processes.Count > 0 ? "#8BE6CB" : "#AAB6CC");
            row.Usage.Text = processes.Count > 0 ? $"{processes.Sum(p => SafeWorkingSet(p)) / 1073741824.0:F1} GB in RAM" : isPending ? "Waiting for window" : "Ready to launch";
            row.Launch.IsEnabled = processes.Count == 0 && !isPending && !starting.Contains(profile.Slot);
            row.Focus.IsEnabled = processes.Count > 0; row.Stop.IsEnabled = processes.Count > 0;
            foreach (var process in processes)
            {
                try { if (!preview && process.MainWindowHandle != IntPtr.Zero && process.MainWindowTitle != profile.Title) Native.SetWindowText(process.MainWindowHandle,profile.Title); } catch { }
                process.Dispose();
            }
        }
        countValue.Text = $"{running} / {settings.Profiles.Count}";
    }
    static long SafeWorkingSet(Process p) { try { return p.WorkingSet64; } catch { return 0; } }
    public async Task Launch(int slot)
    {
        var profile = settings.Profiles.FirstOrDefault(p => p.Slot == slot);
        if (profile == null) { Say("That instance is not in your saved profiles.",true); return; }
        if (starting.Contains(slot) || pending.ContainsKey(slot)) return;
        var existing = Game.Find(profile,settings.GameFolder);
        if (existing.Count > 0) { existing.ForEach(p => p.Dispose()); Focus(profile); return; }
        starting.Add(slot); Refresh();
        try
        {
            var usage = Native.ReadUsage().Memory;
            if ((usage.AvailablePageFile < 5UL * 1024 * 1024 * 1024 || usage.AvailablePhysical < 3UL * 1024 * 1024 * 1024) && MessageBox.Show(this,"Memory is tight. Another RO instance may need 3–5 GB and could cause stuttering or fail to start.\n\nLaunch this instance anyway?","Low memory headroom",MessageBoxButton.YesNo,MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var start = Game.StartInfo(profile,settings.GameFolder);
            if (profile.Isolated)
            {
                Say($"Preparing sandbox {profile.Sandbox}…");
                await Sandboxie.EnsureAsync(profile.Sandbox, start.FileName);
            }
            await Task.Run(() => Game.EnsureFiles(profile,settings.GameFolder));
            var process = Process.Start(start) ?? throw new IOException("Windows could not start this instance.");
            pending[slot] = DateTime.UtcNow;
            Say($"Starting Instance {slot}" + (profile.Isolated ? $" in {profile.Sandbox}. Log in inside that sandbox." : ".") );
            if (profile.Isolated)
            {
                var completed = await Task.WhenAny(process.WaitForExitAsync(),Task.Delay(3000));
                if (completed.IsCompleted && process.HasExited && process.ExitCode != 0) { pending.Remove(slot); throw new IOException($"Sandboxie could not launch the game in {profile.Sandbox} (exit {process.ExitCode}). The sandbox was configured; check Sandboxie's message for the launch failure."); }
            }
            process.Dispose();
        }
        catch (Exception e) { Say(e.Message,true); MessageBox.Show(this,e.Message,"Could not launch",MessageBoxButton.OK,MessageBoxImage.Warning); }
        finally { starting.Remove(slot); Refresh(); }
    }
    void Focus(Profile profile)
    {
        foreach (var p in Game.Find(profile,settings.GameFolder)) { try { if (p.MainWindowHandle != IntPtr.Zero) { Native.ShowWindow(p.MainWindowHandle,9); Native.SetForegroundWindow(p.MainWindowHandle); } } finally { p.Dispose(); } }
    }
    void Stop(Profile profile)
    {
        foreach (var p in Game.Find(profile,settings.GameFolder)) { try { p.CloseMainWindow(); } finally { p.Dispose(); } }
        Say($"Asked Instance {profile.Slot} to close normally. Any game confirmation remains in its window.");
    }
    void Edit(Profile profile)
    {
        var original = JsonSerializer.Serialize(profile);
        var editor = new ProfileEditor(profile) { Owner = this };
        if (editor.ShowDialog() != true) return;
        try { Store.Save(settings); RenderCards(); Refresh(); Say($"Saved Instance {profile.Slot}. Names apply now; login storage changes apply on its next launch."); }
        catch (Exception e) { var restore = JsonSerializer.Deserialize<Profile>(original)!; settings.Profiles[settings.Profiles.IndexOf(profile)] = restore; MessageBox.Show(this,e.Message,"Could not save profile"); }
    }
    void AddProfile()
    {
        var next = Enumerable.Range(1,12).FirstOrDefault(i => settings.Profiles.All(p => p.Slot != i));
        if (next == 0) { Say("This launcher supports up to 12 profiles.",true); return; }
        var profile = new Profile { Slot = next, Sandbox = "RO" + next };
        var editor = new ProfileEditor(profile) { Owner = this };
        if (editor.ShowDialog() != true) return;
        settings.Profiles.Add(profile);
        try { Store.Save(settings); RenderCards(); Refresh(); Say($"Instance {next} saved. Its small EXE and shared-data link will be prepared on first launch."); }
        catch (Exception e) { settings.Profiles.Remove(profile); MessageBox.Show(this,e.Message,"Could not add profile"); }
    }
    void ChooseFolder()
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Select the folder containing ro_win.exe", InitialDirectory = settings.GameFolder };
        if (picker.ShowDialog(this) != true) return;
        if (!File.Exists(Path.Combine(picker.FolderName,"ro_win.exe")) || !Directory.Exists(Path.Combine(picker.FolderName,"ro_win_Data"))) { Say("That folder must contain ro_win.exe and ro_win_Data.",true); return; }
        var previous = settings.GameFolder; settings.GameFolder = picker.FolderName;
        try { Store.Save(settings); Refresh(); Say("Game folder saved."); } catch (Exception e) { settings.GameFolder = previous; Say(e.Message,true); }
    }
    void AccountSetup()
    {
        var dialog = new Window { Owner = this, Title = "Remember an account per instance", Width = 600, Height = 660, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#0D1626") };
        var stack = new StackPanel { Margin = new Thickness(28) };
        stack.Children.Add(Text("Keep each account in its own sandbox",22));
        var description = Text("Account labels help identify windows. They do not save a login session. Shared login mode continues to use the game's last account.",14,"#AAB6CC"); description.Margin = new Thickness(0,15,0,20); stack.Children.Add(description);
        foreach (var line in new[] { "1. Install Sandboxie Plus from its official website.", "2. Close the corresponding game window. Edit its profile here and select Separate login. Use a different sandbox name per account.", "3. Launch. The app creates a missing sandbox with persistent storage automatically. Existing sandbox settings are preserved.", "4. If the game supports sandboxing, sign in through the game, then close and reopen it to test account persistence." })
        { var text = Text(line,14); text.Margin = new Thickness(0,0,0,14); stack.Children.Add(text); }
        stack.Children.Add(Text("The tested RO client rejects Sandboxie with THEMIS Error 1000-1. Automatic setup does not resolve that game restriction. The launcher does not capture passwords or copy login tokens.",12,"#EBCB8B"));
        var buttons = new WrapPanel { Margin = new Thickness(0,22,0,0) };
        buttons.Children.Add(Button("Official download", () => Process.Start(new ProcessStartInfo("https://sandboxie-plus.com/downloads/") { UseShellExecute = true }),true));
        buttons.Children.Add(Button("Open Sandboxie", () => { var path = Game.SandboxiePath; var manager = path == null ? null : Path.Combine(Path.GetDirectoryName(path)!,"SandMan.exe"); if (manager != null && File.Exists(manager)) Process.Start(new ProcessStartInfo(manager) { UseShellExecute = true }); else MessageBox.Show(dialog,"Sandboxie Plus is not installed in its default location."); }));
        buttons.Children.Add(Button("Done",dialog.Close)); stack.Children.Add(buttons); dialog.Content = stack; dialog.ShowDialog();
    }
}

public sealed class UniformGridCompat : System.Windows.Controls.Primitives.UniformGrid { public UniformGridCompat(int columns) { Columns = columns; } }

public sealed class ProfileEditor : Window
{
    public ProfileEditor(Profile profile)
    {
        Title = "Edit Instance " + profile.Slot; Width = 520; Height = 645; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
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
        var modeLabel = LauncherWindow.Text("Login storage",12,"#AAB6CC"); modeLabel.Margin = new Thickness(0,16,0,6); panel.Children.Add(modeLabel);
        var mode = new ComboBox { FontSize = 14, Padding = new Thickness(9), ItemsSource = new[] { "Shared login (normal launch)", "Separate login (Sandboxie Plus)" }, SelectedIndex = profile.Isolated ? 1 : 0 }; panel.Children.Add(mode);
        var boxName = Field("Sandbox name  ·  created automatically when missing",profile.Sandbox);
        boxName.IsEnabled = profile.Isolated; mode.SelectionChanged += (_, _) => boxName.IsEnabled = mode.SelectedIndex == 1;
        var hint = LauncherWindow.Text("Labels are saved on this PC. Passwords are entered only in the game. Separate login requires Sandboxie and a restart of this instance.",12,"#AAB6CC"); hint.Margin = new Thickness(0,16,0,16); panel.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(18,9,18,9), Margin = new Thickness(0,0,8,0), IsCancel = true }; buttons.Children.Add(cancel);
        var save = new Button { Content = "Save profile", Padding = new Thickness(18,9,18,9), IsDefault = true };
        save.Click += (_, _) => {
            if (mode.SelectedIndex == 1) { try { Sandboxie.ValidateName(boxName.Text.Trim()); } catch (InvalidDataException error) { MessageBox.Show(this,error.Message); return; } }
            profile.Name = name.Text.Trim(); profile.Account = account.Text.Trim(); profile.Server = server.Text.Trim(); profile.Isolated = mode.SelectedIndex == 1; profile.Sandbox = boxName.Text.Trim(); DialogResult = true;
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
            var settings = new Settings { Profiles = Enumerable.Range(1,4).Select(i => new Profile { Slot = i, Sandbox = "RO" + i }).ToList() };
            Store.Validate(settings);
            Check(settings.Profiles[0].ProcessName == "ro_win" && settings.Profiles[3].ProcessName == "ro_win3","instance-to-executable mapping");
            settings.Profiles[0].Name = "Main account";
            Check(settings.Profiles[0].Title == "Ragnarok M - Instance 1 | Main account","account window title");
            var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
            Store.Validate(copy); Check(copy.Profiles[0].Name == "Main account","profile JSON round-trip");
            copy.Profiles[1].Slot = 1;
            bool rejected = false; try { Store.Validate(copy); } catch (InvalidDataException) { rejected = true; } Check(rejected,"duplicate instance rejected");
            copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
            copy.Profiles[0].Isolated = copy.Profiles[1].Isolated = true; copy.Profiles[1].Sandbox = "ro1";
            rejected = false; try { Store.Validate(copy); } catch (InvalidDataException) { rejected = true; } Check(rejected,"shared sandbox rejected for separate accounts");
            copy.Profiles[1].Sandbox = "bad /box:name";
            rejected = false; try { Store.Validate(copy); } catch (InvalidDataException) { rejected = true; } Check(rejected,"invalid sandbox argument rejected");
            var start = Game.StartInfo(settings.Profiles[3],settings.GameFolder);
            Check(start.FileName.EndsWith("ro_win3.exe") && start.WorkingDirectory == settings.GameFolder,"launch target and working directory");
            Check(Native.ReadUsage().Memory.TotalPhysical > 0,"live memory metrics");
            var testFile = Path.Combine(Store.Root,"Tests","profiles.json");
            Store.Save(settings,testFile);
            settings.Profiles[0].Account = "test@example.invalid";
            Store.Save(settings,testFile);
            Check(Store.Load(testFile).Profiles[0].Account == "test@example.invalid", "atomic save and reload from disk");
            Check(Store.Load(testFile + ".bak").Profiles[0].Account == "", "previous profiles preserved in backup");
            var edited = new Profile { Slot = 1, Sandbox = "RO1" };
            var editor = new ProfileEditor(edited);
            editor.Loaded += (_, _) => editor.Dispatcher.BeginInvoke(new Action(() => {
                var content = (StackPanel)editor.Content;
                var fields = content.Children.OfType<TextBox>().ToArray();
                fields[0].Text = "  Saved main  "; fields[1].Text = "user@example.invalid"; fields[2].Text = "Server A";
                var actions = content.Children.OfType<StackPanel>().Last();
                actions.Children.OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Check(editor.ShowDialog() == true && edited.Name == "Saved main" && edited.Account == "user@example.invalid" && edited.Server == "Server A", "profile editor Save interaction");
            var cancelled = new Profile { Slot = 2, Name = "Original", Sandbox = "RO2" };
            var cancelEditor = new ProfileEditor(cancelled);
            cancelEditor.Loaded += (_, _) => cancelEditor.Dispatcher.BeginInvoke(new Action(() => {
                ((StackPanel)cancelEditor.Content).Children.OfType<TextBox>().First().Text = "Unsaved";
                cancelEditor.DialogResult = false;
            }));
            Check(cancelEditor.ShowDialog() == false && cancelled.Name == "Original", "profile editor cancellation preserves values");
            SandboxieTests.Run(Check);
            Directory.CreateDirectory(Store.Root); File.WriteAllLines(Path.Combine(Store.Root,"self-test.txt"),report); return 0;
        }
        catch (Exception e) { report.Add(e.ToString()); Directory.CreateDirectory(Store.Root); File.WriteAllLines(Path.Combine(Store.Root,"self-test.txt"),report); return 1; }
    }
}
