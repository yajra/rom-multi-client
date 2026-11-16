using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ROInstances;

internal sealed record SetupIssue(string Message, bool NeedsRuntime = false);

internal static class GamePrerequisites
{
    internal const string RuntimeHelp = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist/";
    internal static readonly string[] GameFiles = { "ro_win.exe", "UnityPlayer.dll", "GameAssembly.dll" };

    internal static bool HasGameFolder(string folder) => Path.IsPathFullyQualified(folder)
        && File.Exists(Path.Combine(folder, "ro_win.exe")) && Directory.Exists(Path.Combine(folder, "ro_win_Data"));

    internal static SetupIssue? Check(string folder, Func<string, IEnumerable<string>>? imports = null,
        Func<string, bool>? runtimeAvailable = null)
    {
        if (!HasGameFolder(folder))
            return new("Install Ragnarok M: Eternal Love with its official installer first, then choose the game folder containing ro_win.exe and ro_win_Data. If you moved the game, select its new folder.");
        foreach (var file in GameFiles)
            if (!File.Exists(Path.Combine(folder, file)))
                return new($"The game installation is incomplete: {file} is missing. Repair or update the game with its official launcher, then try again.");
        try
        {
            var required = GameFiles.SelectMany(file => (imports ?? ReadImports)(Path.Combine(folder, file)))
                .Where(IsVisualCpp).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var library in required)
                if (!(runtimeAvailable ?? (name => File.Exists(Path.Combine(folder, name))
                    || File.Exists(Path.Combine(Environment.SystemDirectory, name))))(library))
                    return new($"A required library is missing: {library}. Install Microsoft Visual C++ v14 Redistributable (x64) from Microsoft, then try again. Game launch is paused until it is installed.", true);
        }
        catch (Exception error) when (error is IOException || error is BadImageFormatException || error is UnauthorizedAccessException)
        {
            return new("The game files could not be checked. Repair the installation with the official game launcher and confirm you can read its files. " + error.Message);
        }
        return null;
    }

    static bool IsVisualCpp(string name) => Path.GetFileName(name) == name && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        && (name.StartsWith("vcruntime140", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("msvcp140", StringComparison.OrdinalIgnoreCase) || name.Equals("concrt140.dll", StringComparison.OrdinalIgnoreCase));

    // Inspect imports without loading game code into the launcher process.
    internal static IEnumerable<string> ReadImports(string file)
    {
        using var stream = File.OpenRead(file);
        using var pe = new PEReader(stream);
        var header = pe.PEHeaders.PEHeader ?? throw new BadImageFormatException("Missing executable header.");
        var directory = header.ImportTableDirectory;
        if (directory.RelativeVirtualAddress == 0) yield break;
        var descriptors = pe.GetSectionData(directory.RelativeVirtualAddress).GetReader(0, directory.Size);
        while (descriptors.RemainingBytes >= 20)
        {
            var lookup = descriptors.ReadInt32();
            var timestamp = descriptors.ReadInt32();
            var forwarder = descriptors.ReadInt32();
            var name = descriptors.ReadInt32();
            var address = descriptors.ReadInt32();
            if ((lookup | timestamp | forwarder | name | address) == 0) yield break;
            var reader = pe.GetSectionData(name).GetReader();
            var bytes = new List<byte>();
            byte value;
            while ((value = reader.ReadByte()) != 0)
            {
                if (bytes.Count >= 260) throw new BadImageFormatException("Invalid library name.");
                bytes.Add(value);
            }
            yield return Encoding.ASCII.GetString(bytes.ToArray());
        }
    }
}
