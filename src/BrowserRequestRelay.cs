using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace ROInstances;

// Windows' app chooser cannot run in these secondary logons. Observe only
// web-opening requests made by verified managed game processes. No ETL files,
// browser history, or authentication URLs are written to disk.
internal static class BrowserRequestRelay
{
    static readonly SemaphoreSlim gate = new(1);
    static NamedPipeServerStream? connection;
    static Process? helper;
    static string folder = "";
    internal static int OpenedRequests;
    // No BOM on IPC: AutoFlush must not write a header while both peers are
    // constructing writers before their first read.
    static StreamWriter Writer(Stream stream) => new(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
    static StreamReader Reader(Stream stream) => new(stream, Encoding.UTF8, false, 1024, true);
    static NamedPipeServerStream CreateConnection(string name)
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, acl);
    }

    internal static async Task<bool> TestHandshake()
    {
        var name = "ROBrowserRequests_Test_" + Guid.NewGuid().ToString("N");
        using var server = CreateConnection(name);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var writer = Writer(server); using var reader = Reader(server);
            await writer.WriteLineAsync("game-folder");
            return await reader.ReadLineAsync() == "ready";
        });
        var clientTask = Task.Run(async () =>
        {
            await client.ConnectAsync();
            using var writer = Writer(client); using var reader = Reader(client);
            var folder = await reader.ReadLineAsync();
            await writer.WriteLineAsync("ready");
            return folder == "game-folder";
        });
        try { var results = await Task.WhenAll(serverTask, clientTask).WaitAsync(TimeSpan.FromSeconds(3)); return results[0] && results[1]; }
        catch (TimeoutException) { return false; }
    }
    static void Status(string message)
    {
        try
        {
            Directory.CreateDirectory(Store.Root);
            File.WriteAllText(Path.Combine(Store.Root, "browser-relay-status.txt"), DateTime.UtcNow.ToString("O") + " " + message);
        }
        catch { }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);

    static bool BrowserRequestImage(string image) =>
        string.Equals(Path.GetFileName(image), "OpenWith.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(image), "rundll32.exe", StringComparison.OrdinalIgnoreCase);

    internal static string? WebRequest(string image, string commandLine)
    {
        if (!BrowserRequestImage(image) || commandLine.Length > 32767) return null;
        var args = CommandLineToArgvW(commandLine, out var count);
        if (args == IntPtr.Zero) return null;
        try
        {
            if (string.Equals(Path.GetFileName(image), "rundll32.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (count != 3) return null;
                var handler = Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, IntPtr.Size))!;
                var comma = handler.LastIndexOf(',');
                if (comma < 0 || !string.Equals(Path.GetFileName(handler.Substring(0, comma)), "url.dll", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(handler.Substring(comma + 1), "FileProtocolHandler", StringComparison.OrdinalIgnoreCase)) return null;
                var target = Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, 2 * IntPtr.Size))!;
                return LoginBrowser.ValidUrl(target) ? target : null;
            }
            // Accept exactly one web URL, never an executable, local file, or
            // browser switch. Preserve OAuth query strings verbatim.
            string? url = null;
            for (var i = 1; i < count; i++)
            {
                var value = Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, i * IntPtr.Size))!;
                if (!LoginBrowser.ValidUrl(value)) continue;
                if (url != null) return null;
                url = value;
            }
            return url;
        }
        finally { LocalFree(args); }
    }

    internal static bool ManagedParent(int pid, string gameFolder)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var name = process.ProcessName;
            return InstanceAccounts.ValidName(name)
                && string.Equals(process.MainModule?.FileName, Path.Combine(gameFolder, name + ".exe"), StringComparison.OrdinalIgnoreCase)
                && LoginDiagnostics.ProcessSid(pid) == LoginDiagnostics.ExpectedSid(name);
        }
        catch { return false; }
    }

    internal static async Task Ensure(string gameFolder)
    {
        await gate.WaitAsync();
        try
        {
            gameFolder = Path.GetFullPath(gameFolder).TrimEnd(Path.DirectorySeparatorChar);
            if (connection?.IsConnected == true && helper is { HasExited: false } && folder == gameFolder) return;
            connection?.Dispose(); helper?.Dispose(); connection = null; helper = null;
            var name = "ROBrowserRequests_" + Guid.NewGuid().ToString("N");
            var server = CreateConnection(name);
            try
            {
                Status("Starting the sign-in helper; waiting for administrator approval.");
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                start.ArgumentList.Add("--browser-request-relay"); start.ArgumentList.Add(Environment.ProcessId.ToString()); start.ArgumentList.Add(name);
                helper = Process.Start(start) ?? throw new IOException("The sign-in browser relay did not start.");
                Status("Waiting for the sign-in helper connection.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await server.WaitForConnectionAsync(timeout.Token);
                Status("Connected to the sign-in helper.");
                if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var pid) || pid != helper.Id)
                    throw new IOException("The sign-in browser relay identity could not be verified.");
                var writer = Writer(server);
                await writer.WriteLineAsync(gameFolder);
                Status("Waiting for Windows process-event monitoring to become ready.");
                var reader = Reader(server);
                if (await reader.ReadLineAsync(timeout.Token) != "ready") throw new IOException("Windows could not start the sign-in browser relay.");
                connection = server; folder = gameFolder;
                Status("Ready. Sign-in links will open in the launcher user's default browser.");
                _ = Receive(server, reader, writer);
            }
            catch { server.Dispose(); throw; }
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        { throw new IOException("Administrator approval was cancelled. The separate-account sign-in browser was not started."); }
        finally { gate.Release(); }
    }

    static async Task Receive(NamedPipeServerStream server, StreamReader reader, StreamWriter writer)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } url)
            {
                if (url == "status:unsupported") Status("A managed game requested the app chooser without a supported web URL.");
                if (LoginBrowser.ValidUrl(url))
                {
                    var focused = await SignInBrowser.Open(url);
                    Status((focused ? "Opened and focused sign-in in the current user's default browser." : "Opened sign-in; Windows did not grant browser focus.")
                        + " Requests: " + Interlocked.Increment(ref OpenedRequests));
                }
            }
        }
        catch { Status("Browser relay disconnected. Launch an instance again to reconnect."); }
        finally { reader.Dispose(); writer.Dispose(); server.Dispose(); }
    }

    internal static int Run(int ownerPid, string pipeName)
    {
        if (!pipeName.StartsWith("ROBrowserRequests_", StringComparison.Ordinal)
            || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 1;
        try
        {
            void Stage(string value) { try { Directory.CreateDirectory(Store.Root); File.WriteAllText(Path.Combine(Store.Root, "browser-relay-helper.txt"), value); } catch { } }
            Stage("Verifying launcher");
            using var owner = Process.GetProcessById(ownerPid);
            if (LoginDiagnostics.ProcessSid(ownerPid) != WindowsIdentity.GetCurrent().User!.Value
                || !string.Equals(owner.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 1;
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            Stage("Connecting");
            pipe.Connect(15000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverPid) || serverPid != ownerPid) return 1;
            using var reader = Reader(pipe);
            using var writer = Writer(pipe);
            var gameFolder = reader.ReadLine();
            Stage("Received game folder");
            if (gameFolder == null || !Path.IsPathFullyQualified(gameFolder) || !Directory.Exists(gameFolder)) return 1;
            using var session = new TraceEventSession("ROBrowserRequests-" + Environment.ProcessId);
            Stage("Created trace session");
            // Enable the kernel provider before accessing Source, which starts
            // the session in current TraceEvent versions.
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);
            Stage("Enabled process events");
            var requests = Channel.CreateBounded<string>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropWrite });
            session.Source.Kernel.ProcessStart += data =>
            {
                // Check the image and parent before touching the command line.
                if (!BrowserRequestImage(data.ImageFileName) || !ManagedParent(data.ParentID, gameFolder)) return;
                var url = WebRequest(data.ImageFileName, data.CommandLine);
                requests.Writer.TryWrite(url ?? "status:unsupported");
            };
            writer.WriteLine("ready");
            var trace = Task.Run(() => session.Source.Process());
            using var cancellation = new CancellationTokenSource();
            var send = Task.Run(async () =>
            {
                await foreach (var url in requests.Reader.ReadAllAsync(cancellation.Token)) await writer.WriteLineAsync(url);
            });
            Task.WhenAny(trace, send, owner.WaitForExitAsync(), reader.ReadLineAsync()).GetAwaiter().GetResult();
            cancellation.Cancel(); session.Stop();
            try { Task.WhenAll(trace, send).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            return 0;
        }
        catch (Exception error)
        {
            try { Directory.CreateDirectory(Store.Root); File.WriteAllText(Path.Combine(Store.Root, "browser-relay-helper.txt"), error.ToString()); } catch { }
            return 1;
        }
    }
}
