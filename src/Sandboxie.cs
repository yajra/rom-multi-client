using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ROInstances;

public sealed record SandboxCommandResult(int ExitCode, string Output, string Error);

public static class Sandboxie
{
    public static void ValidateName(string name)
    {
        if (!Regex.IsMatch(name ?? "", @"^[A-Za-z][A-Za-z0-9_]{0,31}$") ||
            name!.Equals("GlobalSettings", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("UserSettings_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Template_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a sandbox name of 1–32 letters, numbers or underscores, starting with a letter. Configuration section names are reserved.");
    }

    public static Task<bool> EnsureAsync(string name, string startPath)
    {
        ValidateName(name);
        var iniPath = Path.Combine(Path.GetDirectoryName(startPath)!, "SbieIni.exe");
        if (!File.Exists(iniPath)) throw new IOException("Sandboxie configuration tool SbieIni.exe is missing. Repair the Sandboxie installation before launching.");
        return EnsureAsync(name, args => RunAsync(iniPath, args), () => RunAsync(startPath, new[] { "/reload" }));
    }

    // Injection keeps tests independent of the user's Sandboxie installation and configuration.
    public static async Task<bool> EnsureAsync(string name,
        Func<string[], Task<SandboxCommandResult>> configure,
        Func<Task<SandboxCommandResult>> reload)
    {
        ValidateName(name);
        async Task<string> Command(params string[] args)
        {
            var result = await configure(args).ConfigureAwait(false);
            RequireSuccess(result, $"configure sandbox {name}");
            return result.Output;
        }
        static string[] Lines(string value) => value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        async Task Verify()
        {
            var enabled = Lines(await Command("query", name, "Enabled"));
            if (!enabled.Any(v => v.Equals("y", StringComparison.OrdinalIgnoreCase) || v.StartsWith("y,", StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"Sandbox {name} is disabled or unavailable. Enable it in Sandboxie Plus, then retry. Existing settings were preserved.");
            var autoDelete = Lines(await Command("query", name, "AutoDelete"));
            if (autoDelete.Any(v => v.Equals("y", StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"Sandbox {name} automatically deletes its contents. Turn off automatic deletion in Sandboxie Plus before using it for a remembered login.");
        }
        var sections = Lines(await Command("query", "*"));
        if (sections.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            await Verify().ConfigureAwait(false);
            return false;
        }
        // Enable last: a partially configured box must not be launched.
        await Command("set", name, "AutoDelete", "n");
        await Command("set", name, "AutoRecover", "n");
        await Command("set", name, "Enabled", "y");
        RequireSuccess(await reload().ConfigureAwait(false), "reload the Sandboxie configuration");
        var created = Lines(await Command("query", "*"));
        if (!created.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new IOException($"Sandboxie did not confirm creation of {name}. The game was not started.");
        await Verify().ConfigureAwait(false);
        return true;
    }

    static void RequireSuccess(SandboxCommandResult result, string operation)
    {
        if (result.ExitCode == 0) return;
        var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim();
        if (detail.Length > 400) detail = detail[..400];
        throw new IOException($"Could not {operation} (exit {result.ExitCode}). Check that the Sandboxie service is running and that configuration changes are permitted." + (detail.Length == 0 ? "" : "\n" + detail));
    }

    public static async Task<SandboxCommandResult> RunAsync(string path, string[] arguments)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start Sandboxie's configuration tool.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false) != exited)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new IOException("Sandboxie configuration timed out. Check its service and any configuration permission prompt, then retry.");
        }
        await exited.ConfigureAwait(false);
        return new SandboxCommandResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
