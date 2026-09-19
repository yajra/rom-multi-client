[CmdletBinding()]
param([switch]$UpdateExistingInstances)
$ErrorActionPreference = 'Stop'
$appPath = Join-Path $PSScriptRoot 'artifacts\app\RO Instance Launcher.exe'
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) { throw 'Run Build.ps1 first.' }
$desktopPath = [Environment]::GetFolderPath('Desktop')
$shell = New-Object -ComObject WScript.Shell
$mainPath = Join-Path $desktopPath 'RO Eternal Love Launcher.lnk'
$previousPath = Join-Path $desktopPath 'RO Instance Launcher.lnk'
if ((Test-Path -LiteralPath $previousPath) -and !(Test-Path -LiteralPath $mainPath)) {
    Move-Item -LiteralPath $previousPath -Destination $mainPath
}
$main = $shell.CreateShortcut($mainPath)
$main.TargetPath = $appPath
$main.WorkingDirectory = Split-Path -Parent $appPath
$main.IconLocation = "$appPath,0"
$main.Description = 'Manage Ragnarok M: Eternal Love instances and character profiles'
$main.Save()
Write-Output "Created: $mainPath"
if ($UpdateExistingInstances) {
    $backupRoot = Join-Path $env:LOCALAPPDATA ('ROInstanceLaunchers\ShortcutBackups\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    foreach ($file in Get-ChildItem -LiteralPath $desktopPath -Filter '*.lnk') {
        if ($file.BaseName -notmatch '^(?:Ragnarok M|RO Eternal Love) - Instance ([1-9]|1[0-2])$') { continue }
        $slot = [int]$Matches[1]
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $backupRoot $file.Name)
        $shortcut = $shell.CreateShortcut($file.FullName)
        $shortcut.TargetPath = $appPath
        $shortcut.Arguments = "--launch $slot"
        $shortcut.WorkingDirectory = Split-Path -Parent $appPath
        $shortcut.IconLocation = "$appPath,0"
        $shortcut.Description = "Launch or show Ragnarok M: Eternal Love instance $slot"
        $shortcut.Save()
        $brandedPath = Join-Path $desktopPath "RO Eternal Love - Instance $slot.lnk"
        if ($file.FullName -ne $brandedPath -and !(Test-Path -LiteralPath $brandedPath)) {
            Move-Item -LiteralPath $file.FullName -Destination $brandedPath
        }
        Write-Output "Updated: $brandedPath"
    }
}
