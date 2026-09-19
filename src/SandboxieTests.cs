using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ROInstances;

public static class SandboxieTests
{
    sealed class FakeConfiguration
    {
        public Dictionary<string, Dictionary<string,string>> Boxes = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Writes = new();
        public int Reloads;
        public string? FailSetting;
        public bool FailRead, FailReload, IgnoreWrites;
        public Task<SandboxCommandResult> Command(string[] args)
        {
            if (args[0] == "query")
            {
                if (FailRead) return Task.FromResult(new SandboxCommandResult(5,"","Access denied"));
                if (args[1] == "*") return Task.FromResult(new SandboxCommandResult(0,string.Join("\r\n",Boxes.Keys),""));
                var value = Boxes.TryGetValue(args[1],out var box) && box.TryGetValue(args[2],out var setting) ? setting : "";
                return Task.FromResult(new SandboxCommandResult(0,value,""));
            }
            Writes.Add(string.Join(" ",args));
            if (args[2] == FailSetting) return Task.FromResult(new SandboxCommandResult(5,"","Configuration is locked"));
            if (!IgnoreWrites)
            {
                if (!Boxes.TryGetValue(args[1],out var box)) Boxes[args[1]] = box = new(StringComparer.OrdinalIgnoreCase);
                box[args[2]] = args[3];
            }
            return Task.FromResult(new SandboxCommandResult(0,"",""));
        }
        public Task<SandboxCommandResult> Reload() { Reloads++; return Task.FromResult(new SandboxCommandResult(FailReload ? 1 : 0,"","")); }
        public bool Ensure(string name = "RO3") => Sandboxie.EnsureAsync(name,Command,Reload).GetAwaiter().GetResult();
    }
    public static void Run(Action<bool,string> check)
    {
        static bool Rejects(Action action) { try { action(); return false; } catch (IOException) { return true; } catch (InvalidDataException) { return true; } }
        var missing = new FakeConfiguration();
        check(missing.Ensure() && missing.Boxes["RO3"]["AutoDelete"] == "n" && missing.Boxes["RO3"]["AutoRecover"] == "n" && missing.Reloads == 1,"missing sandbox created with persistent isolated writes");
        check(missing.Writes.Last() == "set RO3 Enabled y","new sandbox enabled only after persistence configuration");
        missing.Boxes["RO3"]["CustomSetting"] = "keep";
        missing.Writes.Clear();
        check(!missing.Ensure("ro3") && missing.Writes.Count == 0 && missing.Reloads == 1 && missing.Boxes["RO3"]["CustomSetting"] == "keep","existing sandbox reused case-insensitively without changing settings");
        missing.Boxes["RO3"]["AutoDelete"] = "y";
        check(Rejects(() => missing.Ensure()) && missing.Writes.Count == 0,"auto-deleting sandbox rejected without overriding user settings");
        missing.Boxes["RO3"]["AutoDelete"] = "n"; missing.Boxes["RO3"]["Enabled"] = "n";
        check(Rejects(() => missing.Ensure()) && missing.Writes.Count == 0,"disabled existing sandbox rejected without enabling it");
        var readFailure = new FakeConfiguration { FailRead = true };
        check(Rejects(() => readFailure.Ensure()) && readFailure.Writes.Count == 0,"configuration read failure prevents creation");
        var writeFailure = new FakeConfiguration { FailSetting = "AutoRecover" };
        check(Rejects(() => writeFailure.Ensure()) && !writeFailure.Boxes["RO3"].ContainsKey("Enabled") && writeFailure.Reloads == 0,"partial configuration failure leaves sandbox disabled");
        var reloadFailure = new FakeConfiguration { FailReload = true };
        check(Rejects(() => reloadFailure.Ensure()),"reload failure prevents launch readiness");
        var unconfirmed = new FakeConfiguration { IgnoreWrites = true };
        check(Rejects(() => unconfirmed.Ensure()),"creation must be confirmed by readback");
        foreach (var invalid in new[] { "GlobalSettings", "UserSettings_test", "Template_test", "RO3 /terminate_all", "" })
        {
            var fake = new FakeConfiguration();
            check(Rejects(() => fake.Ensure(invalid)) && fake.Writes.Count == 0,"invalid or reserved sandbox name rejected: " + invalid);
        }
    }
}
