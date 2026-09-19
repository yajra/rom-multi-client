[CmdletBinding()]
param([switch]$SkipBuild, [string]$AppPath)
$ErrorActionPreference = 'Stop'
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') }
if (-not $AppPath) { $AppPath = Join-Path $PSScriptRoot 'artifacts\app\RO Instance Launcher.exe' }
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) { throw 'Build the application first.' }
$testProcess = Start-Process -FilePath $appPath -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
if (-not $testProcess.WaitForExit(60000)) {
    $testProcess.Kill()
    throw 'Self-tests did not finish within 60 seconds.'
}
$testProcess.Refresh()
$reportPath = Join-Path $env:LOCALAPPDATA 'ROInstanceLaunchers\self-test.txt'
if (Test-Path -LiteralPath $reportPath) { Get-Content -LiteralPath $reportPath }
if ($testProcess.ExitCode -ne 0) { throw "Tests failed with exit code $($testProcess.ExitCode)." }
