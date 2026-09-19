using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ROInstances;

public static class UpdateCredentials
{
    const string Target = "ROEternalLoveLauncher:github.com:yajra/ROInstanceLauncher";
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public uint Flags, Type;
        public string TargetName, Comment;
        public long LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr credential);
    public static void Save(string token)
    {
        var bytes = Encoding.Unicode.GetBytes(token.Trim());
        if (bytes.Length == 0 || bytes.Length > 2560) throw new ArgumentException("Enter a valid GitHub access token.");
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = Target, Comment = "Read-only launcher release access", BlobSize = (uint)bytes.Length, Blob = blob, Persist = 2, UserName = "GitHub" };
            if (!CredWrite(ref credential, 0)) throw new InvalidOperationException("Windows could not save the update credential.");
        }
        finally { for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0); Marshal.FreeHGlobal(blob); Array.Clear(bytes); }
    }
    public static void Remove()
    {
        if (!CredDelete(Target, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new InvalidOperationException("Windows could not remove the update credential.");
    }
    public static async Task<string?> ReadAsync()
    {
        if (CredRead(Target, 1, 0, out var pointer))
        {
            try { var credential = Marshal.PtrToStructure<Credential>(pointer); return Marshal.PtrToStringUni(credential.Blob, (int)credential.BlobSize / 2); }
            finally { CredFree(pointer); }
        }
        // GitHub CLI owns its stored credential. Never persist or log its output here.
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gh", "auth token --hostname github.com") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (process == null) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { try { process.Kill(); } catch { } return null; }
            await error;
            return process.ExitCode == 0 ? (await output).Trim() : null;
        }
        catch { return null; }
    }
}
