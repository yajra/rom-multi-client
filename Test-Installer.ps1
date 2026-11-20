[CmdletBinding()]
param([Parameter(Mandatory)][string]$SetupPath)
$ErrorActionPreference = 'Stop'
# This test installs machine-wide. Never run it on a personal installation.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Installer integration tests require a disposable GitHub-hosted Windows runner.'
}
$installDir = Join-Path $env:ProgramFiles 'RO Eternal Love Launcher'
if (Test-Path -LiteralPath $installDir) { throw 'Test requires a clean installation directory.' }
$profileDir = Join-Path $env:LOCALAPPDATA 'ROInstanceLaunchers'
New-Item -ItemType Directory -Path $profileDir -Force | Out-Null
$profile = Join-Path $profileDir 'profiles.json'
if (Test-Path -LiteralPath $profile) { throw 'Test requires an unused profile directory.' }
$sentinel = '{"GameFolder":"C:\\ExampleGame","Profiles":[]}'
Set-Content -LiteralPath $profile -Value $sentinel -NoNewline
$gamePath = Join-Path $env:RUNNER_TEMP 'ro_win.exe'
Copy-Item -LiteralPath "$env:SystemRoot\System32\ping.exe" -Destination $gamePath
$game = Start-Process $gamePath -ArgumentList '-t 127.0.0.1' -WindowStyle Hidden -PassThru
function Run-Setup([string]$Path, [string]$LogName) {
    $log = Join-Path $env:RUNNER_TEMP $LogName
    Write-Output "Starting $LogName"
    $process = Start-Process -FilePath $Path -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + $log + '"')) -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(90000)) {
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log }
        $process.Kill()
        throw "Setup timed out: $LogName"
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { Get-Content -LiteralPath $log; throw "Setup failed: $($process.ExitCode)" }
}
function Check-Preserved {
    if ((Get-Content -LiteralPath $profile -Raw) -cne $sentinel) { throw 'Setup changed saved profiles.' }
    if ($game.HasExited) { throw 'Setup stopped the game process.' }
}
try {
    Run-Setup $SetupPath 'launcher-install.log'
    $installed = Join-Path $installDir 'RO Instance Launcher.exe'
    $source = Join-Path $PSScriptRoot 'artifacts\release-app\RO Instance Launcher.exe'
    if ((Get-FileHash $installed).Hash -ne (Get-FileHash $source).Hash) { throw 'Installed executable differs from tested build.' }
    if (!(Test-Path (Join-Path $installDir 'installed.txt'))) { throw 'Setup update marker is missing.' }
    $shortcut = Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) 'Programs\RO Eternal Love Launcher.lnk'
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    if ($link.TargetPath -ne $installed -or $link.WorkingDirectory -ne $installDir) { throw 'Invalid Start menu shortcut.' }
    & (Join-Path $PSScriptRoot 'Test.ps1') -SkipBuild -AppPath $installed
    Check-Preserved
    Run-Setup $SetupPath 'launcher-reinstall.log'
    Check-Preserved
    Run-Setup (Join-Path $installDir 'unins000.exe') 'launcher-uninstall.log'
    if (Test-Path -LiteralPath $installed) { throw 'Uninstall left the installed executable.' }
    if (Test-Path -LiteralPath $shortcut) { throw 'Uninstall left the Start menu shortcut.' }
    Check-Preserved
    Write-Output 'PASS: install, reinstall, installed self-tests, shortcuts, uninstall, profiles and running game preservation.'
}
finally { if (!$game.HasExited) { $game.Kill(); $game.WaitForExit() }; $game.Dispose() }
