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

`src\Program.cs` contains the native WPF UI, profile persistence, process controls, resource metrics, and self-tests. `src\ROInstances.csproj` defines the .NET Windows app. There are no third-party NuGet dependencies.

The self-tests cover instance mapping, titles, JSON round trips, duplicate profile rejection, direct launch targets, migration of older profiles, system metrics, atomic saving and backup recovery, and the actual profile editor Save/Cancel interactions. They do not launch or stop game processes or authenticate accounts.

Optional layout previews:

```powershell
& '.\artifacts\app\RO Instance Launcher.exe' --preview "$env:TEMP\ro-launcher-preview.png"
& '.\artifacts\app\RO Instance Launcher.exe' --preview-editor "$env:TEMP\ro-editor-preview.png"
& '.\artifacts\app\RO Instance Launcher.exe' --preview-settings "$env:TEMP\ro-settings-preview.png"
```

Instance shortcuts call `--launch N`. If the launcher is already running, a current-user named pipe forwards the request to it. If that game is already running, the launcher brings it forward instead of starting a duplicate.

