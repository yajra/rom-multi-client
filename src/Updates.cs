using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ROInstances;

public sealed record LauncherRelease(Version Version, string AssetName, long AssetId, long Size, long ChecksumId);

public static class Updates
{
    const string Api = "https://api.github.com/repos/yajra/rom-multi-client";
    public const string ReleasePage = "https://github.com/yajra/rom-multi-client/releases/latest";
    public static Version CurrentVersion => new(typeof(Updates).Assembly.GetName().Version!.ToString(3));
    public static string Root => Path.Combine(Store.Root, "Updates");
    public static string ResultPath => Path.Combine(Root, "result.txt");
    public static Version ParseVersion(string tag)
    {
        if (!Regex.IsMatch(tag, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")) throw new InvalidDataException("The release version is not a stable semantic version.");
        return Version.Parse(tag.TrimStart('v'));
    }
    public static LauncherRelease? ParseRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var version = ParseVersion(root.GetProperty("tag_name").GetString()!);
        if (version <= current) return null;
        var name = $"RO-Eternal-Love-Launcher-{version}-win-x64.exe";
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var binaries = assets.Where(a => a.GetProperty("name").GetString() == name).ToArray();
        var checksums = assets.Where(a => a.GetProperty("name").GetString() == "SHA256SUMS.txt").ToArray();
        if (binaries.Length != 1 || checksums.Length != 1) throw new InvalidDataException("The release download is not ready. Try again later.");
        var size = binaries[0].GetProperty("size").GetInt64();
        var id = binaries[0].GetProperty("id").GetInt64();
        var checksumId = checksums[0].GetProperty("id").GetInt64();
        if (size < 1 || size > 512L * 1024 * 1024 || id <= 0 || checksumId <= 0) throw new InvalidDataException("Invalid release asset.");
        return new LauncherRelease(version, name, id, size, checksumId);
    }
    static HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
    static async Task<HttpResponseMessage> Request(HttpClient client, string url, bool binary, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("RO-Eternal-Love-Launcher/" + CurrentVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(binary ? "application/octet-stream" : "application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (binary && (int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
        {
            var location = response.Headers.Location;
            response.Dispose();
            if (location == null || !location.IsAbsoluteUri || location.Scheme != "https" || location.Host != "release-assets.githubusercontent.com")
                throw new IOException("GitHub returned an unexpected download location.");
            // Follow only the expected public GitHub asset CDN.
            response = await client.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, cancel);
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode; response.Dispose();
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                throw new IOException("GitHub could not provide the public release. It may be temporarily unavailable or the API rate limit was reached. Try again later or visit Release notes.");
            throw new IOException("GitHub could not serve the update. Try again later.");
        }
        return response;
    }
    public static async Task<LauncherRelease?> CheckAsync(CancellationToken cancel)
    {
        using var client = Client();
        using var response = await Request(client, Api + "/releases/latest", false, cancel);
        return ParseRelease(await response.Content.ReadAsStringAsync(cancel), CurrentVersion);
    }
    public static async Task<int> CommandLineAsync(bool install)
    {
        Directory.CreateDirectory(Root);
        var report = Path.Combine(Root, "check.txt");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var release = await CheckAsync(timeout.Token);
            File.WriteAllText(report, release == null ? $"Launcher v{CurrentVersion} is up to date." : $"Launcher v{release.Version} is available.");
            if (install && release != null)
            {
                var download = await DownloadAsync(release, new Progress<int>(), timeout.Token);
                await PrepareInstallAsync(download.Path, download.Hash);
                File.WriteAllText(report, $"Verified v{release.Version}; installer is waiting for this launcher to exit.");
            }
            return 0;
        }
        catch (Exception e) { File.WriteAllText(report, "Update failed: " + e.Message); return 1; }
    }
    public static string ExpectedHash(string manifest, string assetName)
    {
        var matches = manifest.Split('\n').Select(line => Regex.Match(line.Trim(), @"^([a-fA-F0-9]{64})\s+\*?(.+)$"))
            .Where(match => match.Success && match.Groups[2].Value == assetName).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The release checksum is missing or ambiguous.");
        return matches[0].Groups[1].Value.ToLowerInvariant();
    }
    public static bool HashMatches(string path, string hash)
    {
        using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), hash, StringComparison.OrdinalIgnoreCase);
    }
    public static async Task<(string Path, string Hash)> DownloadAsync(LauncherRelease release, IProgress<int> progress, CancellationToken cancel)
    {

        using var client = Client();
        using var checksum = await Request(client, Api + "/releases/assets/" + release.ChecksumId, true, cancel);
        if (checksum.Content.Headers.ContentLength > 65536) throw new IOException("Invalid checksum download.");
        var hash = ExpectedHash(await checksum.Content.ReadAsStringAsync(cancel), release.AssetName);
        Directory.CreateDirectory(Root);
        var destination = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            using var response = await Request(client, Api + "/releases/assets/" + release.AssetId, true, cancel);
            await using (var input = await response.Content.ReadAsStreamAsync(cancel))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, cancel)) != 0)
                {
                    total += read;
                    if (total > release.Size) throw new IOException("The update download exceeded its expected size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    progress.Report((int)(total * 100 / release.Size));
                }
                if (total != release.Size) throw new IOException("The update download was incomplete.");
            }
            if (!HashMatches(destination, hash)) throw new IOException("The update checksum did not match. The current launcher was not changed.");
            var version = FileVersionInfo.GetVersionInfo(destination);
            if (new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart) != release.Version || version.ProductName != Branding.AppName)
                throw new IOException("The downloaded file is not the expected launcher version.");
            return (destination, hash);
        }
        catch { if (File.Exists(destination)) File.Delete(destination); throw; }
    }
    public static async Task PrepareInstallAsync(string download, string hash)
    {
        var target = Environment.ProcessPath ?? throw new IOException("Cannot locate this launcher.");
        if (Path.GetFileName(target).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Run the packaged launcher to install updates.");
        // Test write access before asking the running launcher to exit.
        var probe = Path.Combine(Path.GetDirectoryName(target)!, ".ro-write-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
        var nonce = Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(download) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--apply-update", target, Environment.ProcessId.ToString(), hash, nonce }) info.ArgumentList.Add(arg);
        using var helper = Process.Start(info) ?? throw new IOException("Could not start the update installer.");
        var ready = Path.Combine(Root, nonce + ".ready");
        for (var i = 0; i < 300; i++)
        {
            if (File.Exists(ready)) { File.Delete(ready); return; }
            if (helper.HasExited) break;
            await Task.Delay(100);
        }
        try { if (!helper.HasExited) helper.Kill(); } catch { }
        throw new IOException("The update installer could not start. Your current launcher is unchanged.");
    }
    // File replacement is kept separate so failure and rollback can be tested without touching an installation.
    internal static void ReplaceAndVerify(string source, string target, string backup, string hash, Func<bool> startAndVerify)
    {
        if (!HashMatches(source, hash)) throw new IOException("Update checksum verification failed.");
        var staged = target + ".update-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, staged, false);
            if (!HashMatches(staged, hash)) throw new IOException("Staged update checksum verification failed.");
            File.Replace(staged, target, backup);
            try { if (!startAndVerify()) throw new IOException("The new launcher did not start successfully."); }
            catch { File.Copy(backup, target, true); throw; }
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
    public static int Apply(string[] args)
    {
        Directory.CreateDirectory(Root);
        try
        {
            if (args.Length != 5 || !int.TryParse(args[2], out var pid) || !Regex.IsMatch(args[3], "^[a-fA-F0-9]{64}$") || !Guid.TryParseExact(args[4], "N", out _)) return 1;
            var target = Path.GetFullPath(args[1]);
            using var parent = Process.GetProcessById(pid);
            if (!string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)) throw new IOException("The update target does not match the running launcher.");
            var source = Environment.ProcessPath!;
            if (!HashMatches(source, args[3])) throw new IOException("Update checksum verification failed.");
            File.WriteAllText(Path.Combine(Root, args[4] + ".ready"), "ready");
            if (!parent.WaitForExit(60000)) throw new IOException("The launcher did not close; update cancelled.");
            var marker = Path.Combine(Root, args[4] + ".started");
            try
            {
                ReplaceAndVerify(source, target, target + ".previous", args[3], () => {
                    var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
                    start.ArgumentList.Add("--update-complete"); start.ArgumentList.Add(args[4]);
                    using var updated = Process.Start(start) ?? throw new IOException("Could not restart the launcher.");
                    for (var i = 0; i < 300; i++)
                    {
                        if (File.Exists(marker)) { File.Delete(marker); return true; }
                        if (updated.HasExited) return false;
                        Thread.Sleep(100);
                    }
                    updated.Kill(); updated.WaitForExit(10000); return false;
                });
                File.WriteAllText(ResultPath, "Launcher updated successfully. Your game windows were left running.");
                return 0;
            }
            catch
            {
                File.WriteAllText(ResultPath, "The update could not start. The previous launcher was restored. Try checking for updates again.");
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                throw;
            }
        }
        catch (Exception e)
        {
            try { File.WriteAllText(Path.Combine(Root, "install-error.txt"), e.Message); } catch { }
            return 1;
        }
    }
    public static void MarkStarted(string nonce)
    {
        if (Guid.TryParseExact(nonce, "N", out _)) { Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, nonce + ".started"), "started"); }
    }
}
