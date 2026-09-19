using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ROInstances;

public static class UpdateTests
{
    public static void Run(Action<bool, string> check)
    {
        string Release(string version, bool draft = false, bool prerelease = false, bool checksum = true) => JsonSerializer.Serialize(new {
            tag_name = version, draft, prerelease,
            assets = checksum ? new[] { new { name = $"RO-Eternal-Love-Launcher-{version.TrimStart('v')}-win-x64.exe", id = 1, size = 100 }, new { name = "SHA256SUMS.txt", id = 2, size = 100 } } : Array.Empty<object>()
        });
        bool Reject(Action action) { try { action(); return false; } catch { return true; } }
        check(Updates.ParseVersion("v1.10.0") > Updates.ParseVersion("v1.9.9"), "update versions compare numerically");
        check(Reject(() => Updates.ParseVersion("v1.2.3-beta.1")) && Reject(() => Updates.ParseVersion("v01.2.3")), "update version rejects prerelease and malformed tags");
        check(Updates.ParseRelease(Release("v1.2.0"), new Version(1,2,0)) == null && Updates.ParseRelease(Release("v1.1.0"), new Version(1,2,0)) == null, "updates never offer current or older versions");
        check(Updates.ParseRelease(Release("v2.0.0", draft: true), new Version(1,2,0)) == null && Updates.ParseRelease(Release("v2.0.0", prerelease: true), new Version(1,2,0)) == null, "updates ignore draft and prerelease versions");
        var release = Updates.ParseRelease(Release("v1.3.0"), new Version(1,2,0));
        check(release?.Version == new Version(1,3,0) && release.AssetId == 1 && release.ChecksumId == 2, "new release resolves exact executable and checksum assets");
        check(Reject(() => Updates.ParseRelease(Release("v1.3.0", checksum: false), new Version(1,2,0))), "incomplete releases cannot be installed");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("new launcher"))).ToLowerInvariant();
        check(Updates.ExpectedHash(hash + "  app.exe\r\n", "app.exe") == hash, "release checksum resolves exact asset name");
        check(Reject(() => Updates.ExpectedHash(hash + "  other.exe", "app.exe")) && Reject(() => Updates.ExpectedHash(hash + "  app.exe\n" + hash + "  app.exe", "app.exe")), "missing and duplicate checksums rejected");
        var root = Path.Combine(Store.Root, "Tests", "Update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "download.exe"); var target = Path.Combine(root, "launcher.exe"); var backup = target + ".previous";
        try
        {
            File.WriteAllText(source, "new launcher"); File.WriteAllText(target, "old launcher");
            Updates.ReplaceAndVerify(source, target, backup, hash, () => File.ReadAllText(target) == "new launcher");
            check(File.ReadAllText(target) == "new launcher" && File.ReadAllText(backup) == "old launcher", "update replacement preserves previous executable");
            File.WriteAllText(target, "old launcher");
            check(Reject(() => Updates.ReplaceAndVerify(source, target, backup, hash, () => false)) && File.ReadAllText(target) == "old launcher", "failed restart rolls back previous executable");
            check(Reject(() => Updates.ReplaceAndVerify(source, target, backup, new string('0',64), () => true)) && File.ReadAllText(target) == "old launcher", "bad checksum cannot modify installed launcher");
            check(Directory.GetFiles(root, "*.update-*").Length == 0, "update staging files are removed after success and rollback");
        }
        finally { foreach (var file in Directory.GetFiles(root)) File.Delete(file); Directory.Delete(root); }
        var legacy = JsonSerializer.Deserialize<Settings>("{\"Profiles\":[{\"Slot\":1}]}")!;
        check(legacy.CheckForUpdates, "existing profiles default to automatic update checks");
    }
}
