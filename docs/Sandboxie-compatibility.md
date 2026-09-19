# Sandboxie integration trial

Test date: 2026-09-19. Sandboxie Plus version: 1.18.4 (Windows x64).

## Result

The local Ragnarok M Windows client rejected startup inside a standard persistent sandbox with:

> THEMIS Error 1000-1: Can't run in virtual machine environment!

This is a game compatibility failure. The launcher successfully started the game in the selected sandbox; Sandboxie's `/box:RO4 /listpids` output included the game's process ID. The game then displayed the error before reaching login. Separate remembered logins were therefore **not verified** and must not be represented as working for this client.

The failed sandbox processes were stopped, and the affected profile was restored to Shared login. The other game instances were left running. Sandboxie and the empty-of-active-processes RO4 configuration remain installed for reference.

## Configuration tested

```ini
[RO4]
Enabled=y
AutoDelete=n
AutoRecover=n
```

The game ran through `Start.exe /box:RO4` with its normal executable and working directory. No shared registry-write exceptions, protection bypasses, credential exports, or copied account tokens were used. The launcher continued to use its existing shared-data junction.

## Supported behavior after this trial

Use Shared login for this game client. Saved profile names and account labels continue to identify instances, but they do not isolate the game's last login. Keep Sandboxie mode available only as an experimental option for a future officially compatible client. Confirm support with the game publisher before relying on sandboxed login persistence.

Do not delete sandbox contents if a later supported test stores a login there; deletion would remove that sandbox's saved state.

## References

- [Sandboxie official releases](https://github.com/sandboxie-plus/Sandboxie/releases)
- [Start command line and process listing](https://sandboxie-plus.github.io/sandboxie-docs/Content/StartCommandLine/)
- [Configuration command-line tool](https://sandboxie-plus.github.io/sandboxie-docs/Content/SbieIniCommandLine/)
