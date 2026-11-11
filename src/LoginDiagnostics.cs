using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ROInstances;

public sealed record LoginIdentity(string User, string Sid, string ProfilePath, string LocalLowPath, string LocalAppDataPath, string RoamingAppDataPath,
    bool GameLoginFolderExists, int GameLoginFileCount, DateTime? LastGameLoginWriteUtc)
{
    public string RegistryHive { get; init; } = "";
    public string RegistryProfileId { get; init; } = "";
    public bool GameRegistryExists { get; init; }
    public Dictionary<string, object?> DisplayPreferences { get; init; } = new();
}

public static class LoginDiagnostics
{
    [DllImport("ntdll.dll")] static extern int NtQueryKey(IntPtr key, int kind, IntPtr data, uint length, out uint needed);
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr data, uint length, out uint needed);

    public static string? ProcessSid(int id)
    {
        var process = OpenProcess(0x1000, false, id);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, 8, out var token)) return null;
            try
            {
                GetTokenInformation(token, 1, IntPtr.Zero, 0, out var size);
                if (size == 0) return null;
                var data = Marshal.AllocHGlobal(checked((int)size));
                try { return GetTokenInformation(token, 1, data, size, out _) ? new SecurityIdentifier(Marshal.ReadIntPtr(data)).Value : null; }
                finally { Marshal.FreeHGlobal(data); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    public static string ExpectedSid(string name) => ((SecurityIdentifier)new NTAccount(Environment.MachineName, name).Translate(typeof(SecurityIdentifier))).Value;

    public static LoginIdentity Capture(bool verifyRegistry = false)
    {
        var localLow = KnownFolder("A520A1A4-1780-4FF6-BD18-167343C5AF16");
        var game = Path.Combine(localLow, "X_D_ Network Inc_", "Ragnarok M");
        // Metadata only: never read, hash, copy, or report login-token contents.
        var files = Directory.Exists(game) ? new DirectoryInfo(game).EnumerateFiles("*", SearchOption.AllDirectories).ToArray() : Array.Empty<FileInfo>();
        using var identity = WindowsIdentity.GetCurrent();
        var result = new LoginIdentity(identity.Name, identity.User!.Value, KnownFolder("5E6C858F-0E22-4760-9AFE-EA3317B67173"),
            localLow, KnownFolder("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"), KnownFolder("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"),
            Directory.Exists(game), files.Length, files.Length == 0 ? null : files.Max(f => f.LastWriteTimeUtc));
        if (!verifyRegistry) return result;
        using var software = Registry.CurrentUser.OpenSubKey("Software") ?? throw new IOException("The instance's registry profile is not loaded.");
        NtQueryKey(software.Handle.DangerousGetHandle(), 3, IntPtr.Zero, 0, out var size);
        if (size < 4 || size > 65536) throw new IOException("Could not verify the instance's registry hive.");
        var buffer = Marshal.AllocHGlobal((int)size);
        string hive;
        try
        {
            if (NtQueryKey(software.Handle.DangerousGetHandle(), 3, buffer, size, out _) != 0)
                throw new IOException("Could not identify the instance's registry hive.");
            hive = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4), Marshal.ReadInt32(buffer) / 2)!;
        }
        finally { Marshal.FreeHGlobal(buffer); }
        if (!RegistryMatchesSid(hive, result.Sid))
            throw new IOException("Windows loaded a different registry hive. No game was launched.");
        // A launcher-owned marker verifies stable registry storage without reading
        // or copying the game's account, password, or session-token values.
        using var marker = Registry.CurrentUser.CreateSubKey(@"Software\ROInstanceLauncher\LoginStorage");
        var profileId = marker.GetValue("ProfileId") as string;
        if (string.IsNullOrEmpty(profileId))
        {
            profileId = Guid.NewGuid().ToString("D");
            marker.SetValue("ProfileId", profileId, RegistryValueKind.String);
            marker.Flush();
        }
        using var gameKey = Registry.CurrentUser.OpenSubKey(@"Software\X.D. Network Inc.\Ragnarok M");
        return result with { RegistryHive = hive, RegistryProfileId = profileId, GameRegistryExists = gameKey != null };
    }

    internal static bool RegistryMatchesSid(string hive, string sid) =>
        string.Equals(hive, @"\REGISTRY\USER\" + sid + @"\Software", StringComparison.OrdinalIgnoreCase);

    internal static void ValidateStorage(string expected, string actual)
    {
        if (!Guid.TryParse(actual, out _) || (expected.Length > 0 && expected != actual))
            throw new IOException("This instance's saved registry profile has changed or been reset. Launch was stopped to avoid silently using a fresh sign-in. Your game login was not overwritten.");
    }

    static string KnownFolder(string id)
    {
        var folder = new Guid(id);
        Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref folder, 0, IntPtr.Zero, out var pointer));
        try { return Marshal.PtrToStringUni(pointer)!; } finally { Marshal.FreeCoTaskMem(pointer); }
    }

    internal static void ApplyEnvironment(ProcessStartInfo start, LoginIdentity identity)
    {
        // Alternate credentials do not make inherited environment variables belong
        // to that user. Resolve folders in the loaded target profile first.
        start.Environment["USERPROFILE"] = identity.ProfilePath;
        start.Environment["LOCALAPPDATA"] = identity.LocalAppDataPath;
        start.Environment["APPDATA"] = identity.RoamingAppDataPath;
        start.Environment["TEMP"] = start.Environment["TMP"] = Path.Combine(identity.LocalAppDataPath, "Temp");
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(identity.LocalAppDataPath, "ROInstanceLaunchers", "BundleCache");
        start.Environment["USERNAME"] = identity.User.Substring(identity.User.LastIndexOf('\\') + 1);
        start.Environment["USERDOMAIN"] = Environment.MachineName;
        var root = Path.GetPathRoot(identity.ProfilePath)!;
        start.Environment["HOMEDRIVE"] = root.TrimEnd('\\');
        start.Environment["HOMEPATH"] = identity.ProfilePath.Substring(root.Length - 1);
    }

    public static int WriteIdentity(bool prepareWindowed = false, GameLaunchSettings? options = null)
    {
        var stage = "Reading the Windows profile";
        try
        {
            using var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var identity = Capture(verifyRegistry: true);
            if (prepareWindowed)
            {
                stage = "Applying ROM display and graphics settings";
                GameDisplay.Prepare(options);
                stage = "Preparing the sign-in browser";
                LoginBrowser.Prepare();
            }
            stage = "Reading ROM display preferences";
            identity = identity with { DisplayPreferences = GameDisplay.ReadPreferences() };
            writer.Write(JsonSerializer.Serialize(identity));
            return 0;
        }
        catch (Exception error) { return WriteError(stage, error); }
    }

    internal static int WriteError(string stage, Exception error)
    {
        // Report the operation and exception message only. Never dump profile,
        // registry, game settings, credential, or sign-in URL contents.
        using var writer = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        writer.Write(stage + ": " + error.Message);
        return 1;
    }

    internal static void ConfigureProbeArguments(ProcessStartInfo start, bool prepare, GameLaunchSettings? options)
    {
        if (!prepare) { start.ArgumentList.Add("--probe-instance-user"); return; }
        options ??= new();
        options.Validate();
        start.ArgumentList.Add("--prepare-game-user");
        start.ArgumentList.Add(JsonSerializer.Serialize(options));
    }

    public static async Task<LoginIdentity> Probe(string name, bool prepareWindowed = false, GameLaunchSettings? options = null)
    {
        // Bootstrap without an explicit environment so Windows supplies the
        // target user's folders, including on the first launch of a new profile.
        var targetIdentity = prepareWindowed ? await Probe(name) : null;
        var start = InstanceAccounts.Configure(new ProcessStartInfo(Environment.ProcessPath!) {
            WorkingDirectory = AppContext.BaseDirectory, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }, name);
        ConfigureProbeArguments(start, prepareWindowed, options);
        if (targetIdentity != null) LoginBrowser.ConfigureProbe(start, targetIdentity);
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("Windows could not verify the instance user."); }
        finally { start.Password?.Dispose(); }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(); } catch { } throw new IOException("Windows-user verification timed out. No game was launched."); }
            if (process.ExitCode != 0)
            {
                var detail = (await error).Trim();
                if (detail.Length == 0) detail = $"The profile helper exited with code {process.ExitCode}.";
                throw new IOException("Windows could not prepare the instance's profile. " + detail + " No game was launched.");
            }
            var identity = JsonSerializer.Deserialize<LoginIdentity>(await output) ?? throw new IOException("Windows-user verification returned no identity.");
            if (identity.Sid != ExpectedSid(name)) throw new IOException("Windows started the wrong user profile. No game was launched.");
            if (!RegistryMatchesSid(identity.RegistryHive, identity.Sid) || !Guid.TryParse(identity.RegistryProfileId, out _))
                throw new IOException("Windows could not verify persistent registry storage. No game was launched.");
            return identity;
        }
    }

    public static async Task<int> VerifyStorage(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "ROInstanceLauncher-login-storage-check.txt");
        try
        {
            var first = await Probe(name);
            var second = await Probe(name);
            ValidateStorage(first.RegistryProfileId, second.RegistryProfileId);
            File.WriteAllText(path, $"PASS: {name} loads its own registry hive and retains its storage marker across two process launches.\nGame registry exists: {second.GameRegistryExists}\nThis checks Windows storage, not whether ROM accepts an existing session.");
            return 0;
        }
        catch (Exception e) { File.WriteAllText(path, "FAIL: " + e.Message); return 1; }
    }

    public static async Task<string> Report(Settings settings)
    {
        var results = new List<object>();
        foreach (var profile in settings.Profiles)
        {
            var processes = Game.Find(profile, settings.GameFolder);
            var running = processes.Select(p => new { Pid = p.Id, Sid = ProcessSid(p.Id) }).ToArray();
            processes.ForEach(p => p.Dispose());
            LoginIdentity? identity = null; string? error = null;
            try { if (profile.WindowsUser.Length > 0) identity = await Probe(profile.WindowsUser); }
            catch (Exception e) { error = e.Message; }
            results.Add(new { profile.Slot, profile.WindowsUser, Running = running, Identity = identity, Error = error });
        }
        Directory.CreateDirectory(Store.Root);
        var path = Path.Combine(Store.Root, "login-diagnostics.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { AtUtc = DateTime.UtcNow, Launcher = Environment.ProcessPath, CurrentIdentity = Capture(), Instances = results }, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
