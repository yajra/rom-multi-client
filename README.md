# RO Instance Launcher

A standalone Windows desktop app for managing multiple Ragnarok M game windows from one shared installation. This project is independent of RO Codex and its Laravel repository.

## Features

- Detect existing instances and launch, show, or request a normal close.
- Save a profile name, account label/email, and server or character note per instance.
- Apply instance-specific window titles, including the saved profile name.
- Show CPU load, available RAM, and remaining Windows memory commitment capacity.
- Warn before launching with low memory headroom.
- Prepare additional small executable copies and junctions to the original `ro_win_Data` folder.
- Keep running in the notification area when its window is closed.
- Optionally launch profiles in distinct Sandboxie Plus sandboxes.

## Requirements

- Windows 10 or 11.
- .NET 8 SDK to build, or .NET 8 Windows Desktop Runtime to run the published app.
- An existing Ragnarok M installation containing `ro_win.exe` and `ro_win_Data`.
- Write access to the game directory when preparing an instance or refreshing its executable after an update.

## Build, test, and run

From this project directory, run in PowerShell:

```powershell
.\Build.ps1
.\Test.ps1 -SkipBuild
& '.\artifacts\app\RO Instance Launcher.exe'
```

The entire `artifacts\app` directory is the runnable application; keep its DLL and runtime configuration files with the EXE. Build outputs are ignored by Git. Exit the launcher through its notification-area menu before rebuilding its published files.

Create a desktop shortcut with:

```powershell
.\Install-Shortcuts.ps1
```

To also point existing `Ragnarok M - Instance N` desktop shortcuts at this app:

```powershell
.\Install-Shortcuts.ps1 -UpdateExistingInstances
```

Previous instance shortcuts are backed up outside the repository. Moving the project requires rebuilding and recreating its shortcuts.

## Using the app

Open **RO Instance Launcher** from the desktop. Use **Edit** to name each account, **Launch** to start it, **Show** to bring its window forward, and **Close** to request a normal game exit. **Add instance** saves a new profile without immediately starting another game. The application supports up to 12 profiles; available hardware determines how many can run well.

Use **Game folder** if your installation is elsewhere. The initial default is `C:\Program Files (x86)\XD\ROLauncher`. The original data folder stays shared. The launcher does not overwrite unrelated existing data directories.

Closing the app window hides it in the notification area. Double-click its icon to reopen, or use **Exit launcher (leave games running)** to stop the app without closing games.

## Accounts and remembered logins

**Saved labels do not save or switch a login session.** In Shared login mode, the game still uses its last signed-in account. Profile names, optional email labels, and notes are plain text on the current Windows account; do not put passwords in those fields.

The application does not capture passwords, export registry credentials, or copy login tokens. Actual separate remembered logins require separate storage. Optional Sandboxie Plus support uses the documented `Start.exe /box:NAME game.exe` command. It does not install Sandboxie or alter its configuration.

To experiment with isolation, follow **Account setup** in the app. Create a different persistent sandbox for each profile, turn off automatic sandbox deletion, close the corresponding existing game, select Separate login, and sign in through the game yourself. Verify that login persists after closing and reopening before relying on it. Game/Sandboxie compatibility has not been validated, and game sessions may expire normally.

- [Sandboxie Plus official downloads](https://sandboxie-plus.com/downloads/)
- [Sandboxie command-line reference](https://sandboxie-plus.github.io/sandboxie-docs/Content/StartCommandLine/)

## Local data

Personal state remains outside this repository at `%LOCALAPPDATA%\ROInstanceLaunchers`:

| File | Purpose |
| --- | --- |
| `profiles.json` | Saved game path and account profiles |
| `profiles.json.bak` | Previous successful profile save |
| `self-test.txt` | Latest test report |
| `Tests\profiles.json` | Synthetic test data only |
| `ShortcutBackups\` | Backups from shortcut migration |

No game assets, credentials, or personal profile files are committed. Existing profiles from the earlier local launcher are reused automatically.

## Development and validation

`src\Program.cs` contains the native WPF UI, profile persistence, process controls, resource metrics, optional Sandboxie launch integration, and self-tests. `src\ROInstances.csproj` defines the .NET Windows app. There are no third-party NuGet dependencies.

The self-tests cover instance mapping, titles, JSON round trips, invalid and duplicate profile rejection, launch targets, system metrics, atomic saving and backup recovery, and the actual profile editor Save/Cancel interactions. They do not launch or stop game processes, test Sandboxie compatibility, or authenticate accounts.

Optional layout previews:

```powershell
& '.\artifacts\app\RO Instance Launcher.exe' --preview "$env:TEMP\ro-launcher-preview.png"
& '.\artifacts\app\RO Instance Launcher.exe' --preview-editor "$env:TEMP\ro-editor-preview.png"
```

Instance shortcuts call `--launch N`. If the launcher is already running, a current-user named pipe forwards the request to it. If that game is already running, the launcher brings it forward instead of starting a duplicate.
