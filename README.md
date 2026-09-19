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

[Release Please](https://github.com/googleapis/release-please-action) reads Conventional Commits after a successful main build and opens or updates a release PR. It updates [CHANGELOG.md](CHANGELOG.md), version.txt, the release manifest, and the C# project version together.

| Commit | SemVer change |
| --- | --- |
| fix: correct a problem | Patch |
| feat: add a capability | Minor |
| feat!: change compatibility, or a BREAKING CHANGE: footer | Major |
| docs:, test:, ci:, refactor:, chore: | No release on their own; included in the next release notes |

Use Conventional Commits for all new commits and squash-merge titles. Historical commits are unchanged; automation starts from v1.0.2.

Review and merge the release PR when ready. The merged main build tests the new version. The same workflow then creates a draft release, attaches the tested EXE, README, changelog, and checksum, and publishes it. Failed builds prevent release creation; failed uploads leave a draft. No manual version bump or tag is needed.

The workflow uses GitHub's built-in token, job-scoped write permissions, and the repository's **Allow GitHub Actions to create and approve pull requests** setting. It does not auto-approve or auto-merge PRs. Bot-created PRs do not trigger other workflows with that token; run **Windows build and release** manually on the release PR branch to validate it before merging. The merged main commit is always tested again before publication.

If upload or publication fails after a draft is created, rerun the failed jobs in that original workflow run to reuse its release outputs and tested artifacts.

To produce and test the same single-file download locally:

```powershell
.\Build-Release.ps1
```

Output is in `artifacts\dist`. The development build and currently running launcher are not replaced by this command.

## Using the app

Open **RO Instance Launcher** from the desktop. Use **Edit** to name each account, **Launch** to start it, **Show** to bring its window forward, and **Close** to request a normal game exit. **Add instance** saves a new profile without immediately starting another game. The application supports up to 12 profiles; available hardware determines how many can run well.

Check the instances you want, or use **Check all**, then click **Launch selected**. Clicking Check all again clears the selection. The launcher starts the selected instances in number order with a short pause between them and skips games already running or starting. A failed launch or declined memory warning stops the remaining queue. Selection changes during a launch apply to the next batch. Selections remain while the launcher is open and reset when it exits.

Use **Game folder** if your installation is elsewhere. The initial default is `C:\Program Files (x86)\XD\ROLauncher`. The original data folder stays shared. The launcher does not overwrite unrelated existing data directories.

Closing the app window hides it in the notification area. Double-click its icon to reopen, or use **Exit launcher (leave games running)** to stop the app without closing games.

## Accounts and remembered logins

**Saved labels do not save or switch a login session.** All instances launch normally and share the game's last signed-in account. Profile names, optional email labels, and notes are plain text on the current Windows account; do not put passwords in those fields.

The application does not capture passwords, export registry credentials, or copy login tokens. Sign in through the game. Older profile files are migrated automatically while preserving names, account labels, and notes.

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

`src\Program.cs` contains the native WPF UI, profile persistence, process controls, resource metrics, and self-tests. `src\ROInstances.csproj` defines the .NET Windows app. There are no third-party NuGet dependencies.

The self-tests cover instance mapping, titles, JSON round trips, duplicate profile rejection, direct launch targets, migration of older profiles, system metrics, atomic saving and backup recovery, and the actual profile editor Save/Cancel interactions. They do not launch or stop game processes or authenticate accounts.

Optional layout previews:

```powershell
& '.\artifacts\app\RO Instance Launcher.exe' --preview "$env:TEMP\ro-launcher-preview.png"
& '.\artifacts\app\RO Instance Launcher.exe' --preview-editor "$env:TEMP\ro-editor-preview.png"
```

Instance shortcuts call `--launch N`. If the launcher is already running, a current-user named pipe forwards the request to it. If that game is already running, the launcher brings it forward instead of starting a duplicate.
