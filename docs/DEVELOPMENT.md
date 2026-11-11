# Development guide

[Back to the launcher](../README.md)

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

To update existing instance shortcuts with Eternal Love names and the client icon:

```powershell
.\Install-Shortcuts.ps1 -UpdateExistingInstances
```

Previous instance shortcuts are backed up outside the repository. Moving the project requires rebuilding and recreating its shortcuts.

## GitHub builds and releases

The **Windows build and release** workflow builds and tests a self-contained Windows x64 executable on every push to `main`, pull request to `main`, and manual workflow run. Download the build from the run's **Artifacts** section. Build artifacts are retained for 14 days.

[Release Please](https://github.com/googleapis/release-please-action) reads Conventional Commits after a successful main build and opens or updates a release PR. It updates [CHANGELOG.md](../CHANGELOG.md), version.txt, the release manifest, and the C# project version together.

| Commit | SemVer change |
| --- | --- |
| fix: correct a problem | Patch |
| feat: add a capability | Minor |
| feat!: change compatibility, or a BREAKING CHANGE: footer | Major |
| docs:, test:, ci:, refactor:, chore: | No release on their own; omitted from release notes |

Use Conventional Commits for all new commits and squash-merge titles. Historical commits are unchanged; automation starts from v1.0.2.

Review and merge the release PR when ready. The merged main build tests the new version. The same workflow then creates a draft release, attaches the tested EXE, README, changelog, and checksum, and publishes it. Failed builds prevent release creation; failed uploads leave a draft. No manual version bump or tag is needed.

The workflow uses GitHub's built-in token, job-scoped write permissions, and the repository's **Allow GitHub Actions to create and approve pull requests** setting. It does not auto-approve or auto-merge PRs. Release PR checks may need maintainer approval. Approve their workflow run, or run **Windows build and release** manually on the release PR branch, and verify it passes before merging. The merged main commit is always tested again before publication.

If upload or publication fails after a draft is created, rerun the failed jobs in that original workflow run to reuse its release outputs and tested artifacts.

To produce and test the same single-file download locally:

```powershell
.\Build-Release.ps1
```

Output is in `artifacts\dist`. The development build and currently running launcher are not replaced by this command.

## Development and validation

`src\Program.cs` contains the native WPF UI, profile persistence, process controls, resource metrics, and self-tests. `src\ROInstances.csproj` defines the .NET Windows app. `BrowserRequestRelay.cs` uses Microsoft's TraceEvent package to relay URL requests from verified managed game processes through `rundll32 url.dll,FileProtocolHandler` or `OpenWith.exe`. The elevated helper only observes process-start events; the unelevated launcher opens URLs through the current user's Windows shell. Pipe endpoints verify both peer process IDs, and the helper requires the same Windows identity and executable as its owner. Events are processed in memory without an ETL file; unrelated command lines are not read by the event callback. The helper ends when its owner or pipe exits.

The self-tests cover instance mapping, titles, JSON round trips, duplicate profile rejection, direct launch targets, migration of older profiles, system metrics, atomic saving and backup recovery, and the actual profile editor Save/Cancel interactions. They do not launch or stop game processes or authenticate accounts.

`SignInBrowser.cs` opens URLs through the current user's Windows default association and finds an existing browser window by executable path and Windows SID. Focus retries stop after twenty attempts or if the user switches to another window. Never use `AttachThreadInput` here: sharing input queues can make foreground activation block on the game or browser. Windows may decline the focus request. The `--check-browser-focus` diagnostic opens a harmless example.com page and writes its foreground-window result to `%TEMP%\ROInstanceLauncher-browser-focus.txt`.

Separate-user preparation first probes the loaded Windows profile without an explicit environment. The preparation helper then receives that verified user's profile and temporary folders, the browser pipe, and a private `DOTNET_BUNDLE_EXTRACT_BASE_DIR` under its local app data. Removing `TEMP` and `TMP` without supplying a bundle cache makes a single-file helper try to extract into `C:\Windows\.net` before managed code starts. The self-tests start a child helper with the browser environment; run them against the single-file build to cover this failure.

Optional layout previews:

```powershell
& '.\artifacts\app\RO Instance Launcher.exe' --preview "$env:TEMP\ro-launcher-preview.png"
& '.\artifacts\app\RO Instance Launcher.exe' --preview-editor "$env:TEMP\ro-editor-preview.png"
& '.\artifacts\app\RO Instance Launcher.exe' --preview-settings "$env:TEMP\ro-settings-preview.png"
```

Instance shortcuts call `--launch N`. If the launcher is already running, a current-user named pipe forwards the request to it. If that game is already running, the launcher brings it forward instead of starting a duplicate.


## ROM startup settings

Each profile persists an immutable `GameLaunchSettings` value with display, graphics, and FPS choices. Missing fields in older profiles default to windowed 540p, low graphics, and low FPS. Invalid enum values and null settings are rejected. The editor replaces this value only on Save; an in-progress launch retains its settings snapshot.

`GameDisplay` applies these choices to the target Windows user's loaded registry before starting ROM. Normal-user launches use the same path. Hidden-user preparation receives only the settings JSON through `ProcessStartInfo.ArgumentList`; the helper validates it before writing. Fullscreen launches bypass the windowed-mode startup watcher.

Client mappings were verified on 2026-11-10 against locally extracted `Game`, `FunctionPerformanceSetting`, and `SetViewEffectState` scripts from the installed client's patched `refactory.unity3d`, `functionsystem.unity3d`, and `framework.unity3d`. Extracted scripts remain outside the repository.

- Display: ROM's first resolution entry is fullscreen. It then includes descending resolutions from 2160, 1440, 1080, 720, and 540 only when `height + 50 < desktopHeight`. Therefore 540p/720p PlayerPrefs indices vary with desktop height. The launcher queries the primary desktop's current mode with `EnumDisplaySettings` and computes the matching index. Windowed launches also pass explicit Unity dimensions; fullscreen leaves dimensions to ROM.
- FPS: Low = 30 FPS (saved ID 1), Medium = 45 FPS (ID 3), High = 60 FPS (ID 2).
- Graphics: Low/Medium/High use IDs 1/2/3. Baseline preset anti-aliasing values are 1/1/2; shadows and effects are 0/1/2; bloom and FXAA are false/true/true. Environment remains false. ROM can adapt graphics to hardware or map conditions at runtime.

Preferences live under `HKCU\Software\X.D. Network Inc.\Ragnarok M`. `Setting_h2752851091` is UTF-8, NUL-terminated REG_BINARY containing Lua-style assignments. Only the graphics/FPS fields are merged; nested tables, audio, controls, and unrelated fields are preserved. Unsupported storage types or unbalanced settings fail before writing. The complete source profile is never copied.

Self-tests cover JSON migration and round-trips, invalid options, all presets and independent graphics/FPS selection, desktop-dependent resolution indices, fullscreen flags, helper argument transport, all launch modes, and actual editor Save/Cancel interactions. Preview rendering verifies layout. Tests do not restart live games; end-to-end game verification occurs on the next launch.
