# RO Instance Launcher

A standalone Windows desktop app for managing multiple Ragnarok M game windows from one shared installation. This project is independent of RO Codex and its Laravel repository.

## Download

Open the [latest GitHub release](https://github.com/yajra/ROInstanceLauncher/releases/latest) and download **RO-Instance-Launcher-VERSION-win-x64.exe**. It is a single executable for Windows x64 with the .NET runtime included: no installer or separate runtime installation is needed. Put it in a permanent folder and double-click it. Saved profiles stay in your local app-data folder when replacing the EXE with a newer release.

Releases include `SHA256SUMS.txt` for verifying the download with PowerShell's `Get-FileHash -Algorithm SHA256`. The executable is currently unsigned, so Windows may show a publisher/SmartScreen prompt. This repository is private; downloading requires an account with repository access.

## Features

- Detect existing instances and launch, show, or request a normal close.
- Save a profile name, account label/email, and server or character note per instance.
- Apply instance-specific window titles, including the saved profile name.
- Show CPU load, available RAM, and remaining Windows memory commitment capacity.
- Warn before launching with low memory headroom.
- Prepare additional small executable copies and junctions to the original `ro_win_Data` folder.
- Keep running in the notification area when its window is closed.
- Optionally launch profiles in distinct Sandboxie Plus sandboxes, creating missing persistent sandboxes automatically.

## Requirements

- Windows 10 or 11.
- .NET 8 SDK to build. The development build requires the .NET 8 Windows Desktop Runtime; the downloadable release includes it.
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

## GitHub builds and releases

The **Windows build and release** workflow builds and tests a self-contained Windows x64 executable on every push to `main`, pull request to `main`, and manual workflow run. Download the build from the run's **Artifacts** section. Build artifacts are retained for 14 days.

Pushing a version tag also publishes a GitHub Release with the tested EXE, README, and SHA-256 checksum. The tag must match `<Version>` in `src/ROInstances.csproj`:

```powershell
# First update the project Version, commit it, and push main.
git tag v1.0.1
git push origin v1.0.1
```

The workflow publishes releases using GitHub's built-in token; no personal access token or extra secret is required. A failed build or test prevents publication. Tags identify immutable release versions; use a new version for subsequent releases.

To produce and test the same single-file download locally:

```powershell
.\Build-Release.ps1
```

Output is in `artifacts\dist`. The development build and currently running launcher are not replaced by this command.

## Using the app

Open **RO Instance Launcher** from the desktop. Use **Edit** to name each account, **Launch** to start it, **Show** to bring its window forward, and **Close** to request a normal game exit. **Add instance** saves a new profile without immediately starting another game. The application supports up to 12 profiles; available hardware determines how many can run well.

Use **Game folder** if your installation is elsewhere. The initial default is `C:\Program Files (x86)\XD\ROLauncher`. The original data folder stays shared. The launcher does not overwrite unrelated existing data directories.

Closing the app window hides it in the notification area. Double-click its icon to reopen, or use **Exit launcher (leave games running)** to stop the app without closing games.

## Accounts and remembered logins

**Saved labels do not save or switch a login session.** In Shared login mode, the game still uses its last signed-in account. Profile names, optional email labels, and notes are plain text on the current Windows account; do not put passwords in those fields.

The application does not capture passwords, export registry credentials, or copy login tokens. Actual separate remembered logins require separate storage. Optional Sandboxie Plus support uses the documented `Start.exe /box:NAME game.exe` command. It does not install Sandboxie. Before launching, it uses `SbieIni.exe` to create a missing sandbox with `AutoDelete=n`, `AutoRecover=n`, and `Enabled=y`, then reloads and verifies the configuration. Existing sandbox settings are preserved. An existing disabled or auto-deleting sandbox produces an actionable error instead of silently changing its settings or starting the game.

**Compatibility result:** the 2026-09-19 test with Sandboxie Plus 1.18.4 reached game startup, then failed with **THEMIS Error 1000-1: Can't run in virtual machine environment!** Use Shared login for the tested client. Separate login persistence is not working in this configuration. See the [integration trial](docs/Sandboxie-compatibility.md).

For a future client confirmed compatible by the publisher, follow **Account setup** in the app. Close the corresponding existing game, select Separate login, and use a different sandbox name for each profile. Launch creates the sandbox when needed. Sign in through the game yourself and verify that login persists after closing and reopening before relying on it. Game sessions may expire normally.

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

`src\Program.cs` contains the native WPF UI, profile persistence, process controls, resource metrics, and self-tests. `src\Sandboxie.cs` manages verified automatic sandbox preparation, with isolated tests in `src\SandboxieTests.cs`. `src\ROInstances.csproj` defines the .NET Windows app. There are no third-party NuGet dependencies.

The self-tests cover instance mapping, titles, JSON round trips, invalid and duplicate profile rejection, launch targets, system metrics, atomic saving and backup recovery, and the actual profile editor Save/Cancel interactions. Sandbox preparation tests cover creation, idempotency, preserved settings, disabled/auto-deleting sandboxes, configuration failures, and reserved names. They use a fake configuration tool and do not alter real sandboxes, launch or stop game processes, test game compatibility, or authenticate accounts.

To prepare and verify a real sandbox without starting a game, run the executable with `--prepare-sandbox RO3`. This intentionally creates the named sandbox if missing and writes the result to `%LOCALAPPDATA%\ROInstanceLaunchers\sandbox-setup.txt`.

Optional layout previews:

```powershell
& '.\artifacts\app\RO Instance Launcher.exe' --preview "$env:TEMP\ro-launcher-preview.png"
& '.\artifacts\app\RO Instance Launcher.exe' --preview-editor "$env:TEMP\ro-editor-preview.png"
```

Instance shortcuts call `--launch N`. If the launcher is already running, a current-user named pipe forwards the request to it. If that game is already running, the launcher brings it forward instead of starting a duplicate.
