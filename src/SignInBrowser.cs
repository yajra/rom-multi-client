using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace ROInstances;

internal static class SignInBrowser
{
    delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(uint flags, uint kind, string association, string verb, StringBuilder output, ref uint length);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    internal static bool SameBrowser(string? executable, string? sid, string expectedExecutable, string expectedSid) =>
        !string.IsNullOrEmpty(executable) && !string.IsNullOrEmpty(expectedExecutable)
        && string.Equals(executable, expectedExecutable, StringComparison.OrdinalIgnoreCase) && sid == expectedSid;

    internal static bool MayFocus(IntPtr original, IntPtr current, IntPtr browser) =>
        current == IntPtr.Zero || current == original || current == browser;

    static string DefaultExecutable(string scheme)
    {
        var buffer = new StringBuilder(32768); uint length = (uint)buffer.Capacity;
        // Resolve on every request so changing the Windows default takes effect
        // immediately. ShellExecute still performs the actual URL opening.
        return AssocQueryString(0x1000, 2, scheme, "open", buffer, ref length) == 0 ? buffer.ToString() : "";
    }

    static IntPtr FindWindow(string executable, string sid)
    {
        var result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0 || GetWindow(window, 4) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(window, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (!SameBrowser(process.MainModule?.FileName, LoginDiagnostics.ProcessSid((int)pid), executable, sid)) return true;
                result = window;
                return false; // EnumWindows visits the most recently raised window first.
            }
            catch { return true; }
        }, IntPtr.Zero);
        return result;
    }

    static bool Focus(IntPtr window)
    {
        if (IsIconic(window)) ShowWindowAsync(window, 9); // Do not unmaximize an already visible browser.
        if (Native.SetForegroundWindow(window) && GetForegroundWindow() == window && !IsIconic(window)) return true;
        // Never join input queues to force focus. A stalled game/browser can
        // otherwise block this call and every application sharing that queue.
        // Windows may grant activation asynchronously; Open retries briefly.
        return false;
    }

    internal static async Task<bool> Open(string url)
    {
        if (!LoginBrowser.ValidUrl(url)) throw new ArgumentException("Only HTTP and HTTPS sign-in links are supported.");
        var original = GetForegroundWindow();
        var executable = DefaultExecutable(new Uri(url).Scheme);
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        using var started = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        for (var attempt = 0; attempt < 20; attempt++)
        {
            // Chrome/Edge/Firefox may reuse an existing process and return no
            // usable Process. Find the owner's browser window independently.
            var window = FindWindow(executable, sid);
            if (window != IntPtr.Zero)
            {
                if (!MayFocus(original, GetForegroundWindow(), window)) return false;
                if (Focus(window)) return true;
            }
            await Task.Delay(100);
        }
        return false;
    }
}
