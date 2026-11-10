using System;
using System.Collections.Generic;
using Microsoft.Win32;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using System.Text;

namespace ROInstances;

internal static class GameDisplay
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr window, uint message, IntPtr key, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    struct DisplayMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(176)] public int Height;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string? deviceName, int mode, ref DisplayMode display);

    internal static int DesktopHeight()
    {
        var display = new DisplayMode { Size = 220 };
        if (!EnumDisplaySettings(null, -1, ref display) || display.Height <= 0)
            throw new IOException("Could not read the desktop resolution for ROM launch settings.");
        return display.Height;
    }

    internal static int ResolutionIndex(GameDisplayMode mode, int desktopHeight)
    {
        if (mode == GameDisplayMode.Fullscreen) return 1;
        int wanted = mode == GameDisplayMode.Windowed720p ? 720 : 540;
        int index = 1;
        foreach (var height in new[] { 2160, 1440, 1080, 720, 540 })
        {
            if (height + 50 >= desktopHeight) continue;
            index++;
            if (height == wanted) return index;
        }
        throw new IOException($"ROM does not offer {wanted}p on a desktop height of {desktopHeight}px.");
    }

    internal static bool CoversMonitor(Rect window, Rect monitor) => window.Left <= monitor.Left && window.Top <= monitor.Top
        && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    internal static async Task WatchStartup(Process process, string expectedSid, int slot, GameLaunchSettings? options = null)
    {
        if (options?.Display == GameDisplayMode.Fullscreen) { process.Dispose(); return; }
        var attempts = 0;
        var sawWindow = false;
        string result = "No game window was observed.";
        using (process)
        {
            try
            {
                for (var tick = 0; tick < 45; tick++)
                {
                    await Task.Delay(2000);
                    if (process.HasExited) { result = "Game exited."; break; }
                    process.Refresh();
                    var window = process.MainWindowHandle;
                    if (window == IntPtr.Zero || IsIconic(window)) continue;
                    GetWindowThreadProcessId(window, out var windowPid);
                    if (windowPid != process.Id || LoginDiagnostics.ProcessSid(process.Id) != expectedSid)
                        throw new IOException("Could not verify the game window's Windows user.");
                    var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (!GetWindowRect(window, out var bounds) || !GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor))
                        throw new IOException("Could not read the game's display bounds.");
                    sawWindow = true;
                    if (!CoversMonitor(bounds, monitor.Monitor) || IsZoomed(window))
                    { result = "Observed a windowed game window."; continue; }
                    result = "Game switched to fullscreen during startup.";
                    if (attempts >= 3) { result = "Game remained fullscreen after three windowed-mode requests."; break; }
                    // Request Unity's own fullscreen toggle, scoped to this exact
                    // game HWND. Never send global keys or focus the browser/game.
                    if (!PostMessage(window, 0x104, new IntPtr(13), new IntPtr(0x201C0001)) ||
                        !PostMessage(window, 0x105, new IntPtr(13), new IntPtr(unchecked((int)0xE01C0001))))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    attempts++;
                    await Task.Delay(3000);
                }
            }
            catch (Exception e) { result = e.Message; }
            finally
            {
                try
                {
                    Directory.CreateDirectory(Store.Root);
                    File.WriteAllText(Path.Combine(Store.Root, $"display-runtime-{slot}.json"), JsonSerializer.Serialize(new {
                        Launcher = Environment.ProcessPath, AtUtc = DateTime.UtcNow, Slot = slot, Attempts = attempts, SawWindow = sawWindow, Result = result
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }
            }
        }
    }
    internal static readonly string[] Arguments = { "-screen-fullscreen", "0", "-screen-width", "960", "-screen-height", "540" };
    internal static string[] ArgumentsFor(GameLaunchSettings options)
    {
        options.Validate();
        return options.Display switch
        {
            GameDisplayMode.Fullscreen => new[] { "-screen-fullscreen", "1" },
            GameDisplayMode.Windowed720p => new[] { "-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720" },
            _ => (string[])Arguments.Clone()
        };
    }
    internal static readonly IReadOnlyDictionary<string, int> InitialPreferences = new Dictionary<string, int>
    {
        ["Screenmanager Fullscreen mode_h3630240806"] = 3,
        ["Screenmanager Is Fullscreen mode_h3981298716"] = 0,
        ["Screenmanager Resolution Width_h182942802"] = 960,
        ["Screenmanager Resolution Height_h2627697771"] = 540,
        ["Screenmanager Resolution Use Native_h1405027254"] = 0,
        ["fullScreen_h4289535994"] = 0,
        ["WindowsResolution_h2966854996"] = 3
    };

    internal static void WritePreferences(IEnumerable<string> existingNames, Action<string, int> write, GameLaunchSettings? options = null, int desktopHeight = 1080)
    {
        options ??= new();
        options.Validate();
        // Validate the mode before performing any writes.
        ResolutionIndex(options.Display, desktopHeight);
        // Seed missing preferences too: hidden Windows users start with an empty
        // game registry and cannot inherit the owner's working display setup.
        foreach (var preference in InitialPreferences)
            if (PreferenceValue(preference.Key, options, desktopHeight) is int value) write(preference.Key, value);
        foreach (var name in existingNames)
            if (PreferenceValue(name, options, desktopHeight) is int value) write(name, value);
    }

    internal static int? PreferenceValue(string name, GameLaunchSettings? options = null, int desktopHeight = 1080)
    {
        options ??= new();
        bool fullscreen = options.Display == GameDisplayMode.Fullscreen;
        int width = options.Display == GameDisplayMode.Windowed720p ? 1280 : 960;
        int height = options.Display == GameDisplayMode.Windowed720p ? 720 : 540;
        // Match only Unity's display preferences, never account/session values.
        if (name == "fullScreen" || name == "fullScreen_h4289535994") return fullscreen ? 1 : 0;
        if (name.StartsWith("Screenmanager Fullscreen mode_h", StringComparison.Ordinal)) return fullscreen ? 1 : 3;
        if (name.StartsWith("Screenmanager Is Fullscreen mode_h", StringComparison.Ordinal)) return fullscreen ? 1 : 0;
        if (name.StartsWith("Screenmanager Resolution Width_h", StringComparison.Ordinal) ||
            name.StartsWith("Screenmanager Resolution Width Default_h", StringComparison.Ordinal)) return fullscreen ? null : width;
        if (name.StartsWith("Screenmanager Resolution Height_h", StringComparison.Ordinal) ||
            name.StartsWith("Screenmanager Resolution Height Default_h", StringComparison.Ordinal)) return fullscreen ? null : height;
        if (name.StartsWith("Screenmanager Resolution Use Native_h", StringComparison.Ordinal)) return fullscreen ? 1 : 0;
        if (name == "WindowsResolution_h2966854996") return ResolutionIndex(options.Display, desktopHeight);
        return null;
    }

    // Observed in instance 2's PlayerPrefs, 2026-11-10. These are ROM enum
    // values, not literal FPS counts. Do not copy its full settings or login data.
    internal static readonly IReadOnlyDictionary<string, string> GraphicsDefaults = new Dictionary<string, string>
    {
        ["qualityLevel"] = "1", ["targetFrameRate"] = "1", ["resolution"] = "1",
        ["antiRate"] = "1", ["shadowRate"] = "0", ["effectLv"] = "0",
        ["showBloom"] = "false", ["fxaa"] = "false", ["showEnv"] = "false"
    };

    internal static string MergeGraphicsSettings(string settings, GameLaunchSettings? options = null)
    {
        options ??= new();
        options.Validate();
        var graphics = new Dictionary<string, string>(GraphicsDefaults)
        {
            ["qualityLevel"] = ((int)options.Graphics + 1).ToString(),
            // ROM's IDs are Low=1 (30), Medium=3 (45), High=2 (60).
            ["targetFrameRate"] = options.Fps switch { PerformanceLevel.Medium => "3", PerformanceLevel.High => "2", _ => "1" },
            ["antiRate"] = options.Graphics == PerformanceLevel.High ? "2" : "1",
            ["shadowRate"] = ((int)options.Graphics).ToString(),
            ["effectLv"] = ((int)options.Graphics).ToString(),
            ["showBloom"] = options.Graphics == PerformanceLevel.Low ? "false" : "true",
            ["fxaa"] = options.Graphics == PerformanceLevel.Low ? "false" : "true"
        };
        // ROM stores a Lua-style assignment list, including nested tables.
        // Split only at top-level commas so other settings survive byte-for-byte.
        var fields = new List<string>();
        var seen = new HashSet<string>();
        int start = 0, depth = 0;
        char quote = '\0';
        bool escaped = false;
        settings = settings.TrimEnd('\0');
        for (int i = 0; i <= settings.Length; i++)
        {
            if (i == settings.Length || (settings[i] == ',' && depth == 0 && quote == '\0'))
            {
                var field = settings.Substring(start, i - start);
                var equal = field.IndexOf('=');
                var name = equal < 0 ? "" : field.Substring(0, equal).Trim();
                if (graphics.TryGetValue(name, out var value))
                { fields.Add(name + "=" + value); seen.Add(name); }
                else if (field.Length > 0) fields.Add(field);
                start = i + 1;
                continue;
            }
            char c = settings[i];
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == quote) quote = '\0';
            }
            else if (c == '\'' || c == '"') quote = c;
            else if (c == '{' || c == '[' || c == '(') depth++;
            else if (c == '}' || c == ']' || c == ')') depth--;
        }
        if (depth != 0 || quote != '\0') throw new IOException("ROM graphics settings could not be parsed. Existing settings were preserved.");
        foreach (var pair in graphics)
            if (!seen.Contains(pair.Key)) fields.Add(pair.Key + "=" + pair.Value);
        return string.Join(",", fields);
    }

    internal static void Prepare(GameLaunchSettings? options = null)
    {
        options ??= new();
        options.Validate();
        int desktopHeight = DesktopHeight();
        ResolutionIndex(options.Display, desktopHeight);
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\X.D. Network Inc.\Ragnarok M", writable: true);
        const string settingKey = "Setting_h2752851091";
        var saved = key.GetValue(settingKey);
        if (saved != null && saved is not byte[])
            throw new IOException("ROM graphics settings have an unsupported storage type. Existing settings were preserved.");
        var settings = MergeGraphicsSettings(saved is byte[] bytes ? Encoding.UTF8.GetString(bytes) : "", options);
        key.SetValue(settingKey, Encoding.UTF8.GetBytes(settings + "\0"), RegistryValueKind.Binary);
        WritePreferences(key.GetValueNames(), (name, value) => key.SetValue(name, value, RegistryValueKind.DWord), options, desktopHeight);
        key.Flush();
    }

    internal static Dictionary<string, object?> ReadPreferences()
    {
        var result = new Dictionary<string, object?>();
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\X.D. Network Inc.\Ragnarok M");
        if (key != null)
            foreach (var name in key.GetValueNames())
                if (PreferenceValue(name) != null) result[name] = key.GetValue(name);
        return result;
    }
}
