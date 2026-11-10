using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ROInstances;

internal static class StartupEnvironment
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetWindowsDirectory(StringBuilder buffer, uint size);

    // Some desktop launchers supply a reduced environment. WPF builds its font
    // URI from windir, so restore it before any WPF controls are initialized.
    internal static void Initialize()
    {
        var current = Environment.GetEnvironmentVariable("windir");
        if (!string.IsNullOrWhiteSpace(current) && Path.IsPathFullyQualified(current) && Directory.Exists(Path.Combine(current, "Fonts"))) return;
        var buffer = new StringBuilder(32768);
        var length = GetWindowsDirectory(buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        Environment.SetEnvironmentVariable("windir", buffer.ToString(), EnvironmentVariableTarget.Process);
    }
}
