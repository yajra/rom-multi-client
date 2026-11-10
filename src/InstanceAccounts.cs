using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace ROInstances;

// Windows passwords stay in the current user's Credential Manager, never in profiles.json.
public static class InstanceAccounts
{
    const string Owner = "ROInstanceLauncher managed game account";
    const string UserList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";
    static string Target(string name) => "ROInstanceLauncher/" + name;
    public static bool ValidName(string name) => name == "ro_win" ||
        (name.StartsWith("ro_win", StringComparison.Ordinal) && int.TryParse(name.Substring(6), out var n) && n >= 1 && n <= 11 && name == "ro_win" + n);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public uint Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct UserInfo
    {
        public string Name, Password;
        public uint PasswordAge, Privilege;
        public string Home, Comment;
        public uint Flags;
        public string Script;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct GroupMember { public string Name; }
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern uint NetLocalGroupAddMembers(string? server, string group, uint level, ref GroupMember member, uint count);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr credential);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern uint NetUserAdd(string? server, uint level, ref UserInfo info, out uint error);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern uint NetUserGetInfo(string? server, string name, uint level, out IntPtr info);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern uint NetUserDel(string? server, string name);
    [DllImport("netapi32.dll")] static extern uint NetApiBufferFree(IntPtr buffer);

    static string? ReadPassword(string name)
    {
        if (!CredRead(Target(name), 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2));
        }
        finally { CredFree(pointer); }
    }

    public static void Ensure(string name)
    {
        if (!ValidName(name)) throw new ArgumentException("Invalid instance Windows username.");
        if (ReadPassword(name) == null)
        {
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(36)) + "aA1!";
            var blob = Marshal.StringToCoTaskMemUni(password);
            try
            {
                var credential = new Credential { Type = 1, TargetName = Target(name), Comment = Owner,
                    Blob = blob, BlobSize = (uint)(password.Length * 2), Persist = 2, UserName = name };
                if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
        }
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add("--setup-instance-user");
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value);
        using var helper = Process.Start(start) ?? throw new InvalidOperationException("Windows account setup did not start.");
        helper.WaitForExit();
        if (helper.ExitCode != 0) throw new InvalidOperationException("Windows account setup failed or was cancelled. The profile was not switched. Existing Windows accounts are never taken over.");
    }

    public static int Setup(string name, string ownerSid)
    {
        try
        {
            if (!ValidName(name) || WindowsIdentity.GetCurrent().User?.Value != ownerSid ||
                !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 1;
            var password = ReadPassword(name) ?? throw new InvalidOperationException("Missing Windows credential.");
            var result = NetUserGetInfo(null, name, 1, out var existing);
            if (result == 0)
            {
                try
                {
                    if (Marshal.PtrToStructure<UserInfo>(existing).Comment != Owner) return 1;
                }
                finally { NetApiBufferFree(existing); }
            }
            else if (result != 2221) throw new Win32Exception((int)result);
            bool created = result == 2221;
            if (created)
            {
                var info = new UserInfo { Name = name, Password = password, Privilege = 1, Comment = Owner,
                    Flags = 0x200 | 0x10000 }; // Normal standard account, generated password does not expire.
                var error = NetUserAdd(null, 1, ref info, out _);
                if (error != 0) throw new Win32Exception((int)error);
            }
            try
            {
                var group = ((NTAccount)new SecurityIdentifier("S-1-5-32-545").Translate(typeof(NTAccount))).Value;
                group = group.Substring(group.IndexOf('\\') + 1);
                var member = new GroupMember { Name = Environment.MachineName + "\\" + name };
                var groupError = NetLocalGroupAddMembers(null, group, 3, ref member, 1);
                if (groupError != 0 && groupError != 1378) throw new Win32Exception((int)groupError);
                using var key = Registry.LocalMachine.CreateSubKey(UserList);
                key.SetValue(name, 0, RegistryValueKind.DWord);
            }
            catch { if (created) NetUserDel(null, name); throw; }
            return 0;
        }
        catch (Exception e) { System.Windows.MessageBox.Show(e.Message, "Could not set up instance Windows user"); return 1; }
    }

    public static ProcessStartInfo Configure(ProcessStartInfo start, string name)
    {
        if (!ValidName(name)) throw new InvalidOperationException("Invalid instance Windows username.");
        var password = ReadPassword(name) ?? throw new InvalidOperationException("The saved Windows credential is missing. Set up this instance again on this Windows account.");
        return ConfigureWithPassword(start, name, password);
    }

    internal static ProcessStartInfo ConfigureWithPassword(ProcessStartInfo start, string name, string password)
    {
        var secure = new SecureString();
        foreach (var character in password) secure.AppendChar(character);
        secure.MakeReadOnly();
        start.UseShellExecute = false;
        start.UserName = name;
        start.Domain = Environment.MachineName;
        start.Password = secure;
        start.LoadUserProfile = true;
        return start;
    }
}
