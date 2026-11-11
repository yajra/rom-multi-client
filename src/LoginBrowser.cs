using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ROInstances;

internal static class LoginBrowser
{
    const string EnvironmentKey = "RO_INSTANCE_SSO_PIPE";
    internal static readonly string PipeName = "ROInstanceSSO_" + Guid.NewGuid().ToString("N");
    internal static string Command(string executable, string pipe) => $"\"{executable}\" --open-sso-url {pipe} \"%1\"";
    internal static bool ValidUrl(string value) => value.Length <= 16384 && !value.Contains('\r') && !value.Contains('\n')
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")
        && uri.UserInfo.Length == 0 && uri.Host.Length > 0;

    internal static void ConfigureProbe(ProcessStartInfo start, LoginIdentity identity)
    {
        // An explicit environment must include the verified target's folders:
        // a bundled helper needs its writable extraction path before Main runs.
        LoginDiagnostics.ApplyEnvironment(start, identity);
        start.Environment[EnvironmentKey] = PipeName;
    }

    // Only the managed game user's HTTP handlers are registered. The owner opens
    // the URL through ShellExecute, preserving their actual default browser.
    internal static void Prepare()
    {
        if (!InstanceAccounts.ValidName(Environment.UserName)) return;
        var pipe = Environment.GetEnvironmentVariable(EnvironmentKey);
        if (string.IsNullOrEmpty(pipe) || !pipe.StartsWith("ROInstanceSSO_", StringComparison.Ordinal))
            throw new IOException("The launcher browser connection is unavailable. Restart the launcher and retry.");
        foreach (var protocol in new[] { "http", "https" })
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + protocol);
            key.SetValue("", "URL:" + protocol + " Protocol");
            key.SetValue("URL Protocol", "");
            using var open = key.CreateSubKey(@"shell\open\command");
            open.SetValue("", Command(Environment.ProcessPath!, pipe));
        }
    }

    internal static int Forward(string pipeName, string url)
    {
        if (!pipeName.StartsWith("ROInstanceSSO_", StringComparison.Ordinal) || !ValidUrl(url)) return 1;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.None, TokenImpersonationLevel.Identification);
            pipe.Connect(5000);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(url);
            return 0;
        }
        catch { return 1; }
    }

    internal static Task Listen(CancellationToken cancellation) => Task.Run(async () =>
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var security = new PipeSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
                for (var slot = 0; slot < 12; slot++)
                {
                    var name = slot == 0 ? "ro_win" : "ro_win" + slot;
                    try
                    {
                        var sid = new SecurityIdentifier(LoginDiagnostics.ExpectedSid(name));
                        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    }
                    catch (IdentityNotMappedException) { }
                }
                using var server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, security);
                await server.WaitForConnectionAsync(cancellation);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(server);
                var buffer = new char[16385];
                var length = 0;
                while (length < buffer.Length)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory(length, 1), timeout.Token);
                    if (count == 0 || buffer[length] == '\n') break;
                    length++;
                }
                var url = new string(buffer, 0, length).TrimEnd('\r');
                if (ValidUrl(url))
                    await SignInBrowser.Open(url);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch { await Task.Delay(300, cancellation); }
        }
    }, cancellation);
}
