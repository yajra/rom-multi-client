[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\ROInstances.csproj'
$output = Join-Path $PSScriptRoot 'artifacts\app'
& dotnet publish $project -c Release --no-self-contained -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
Write-Output "Application: $(Join-Path $output 'RO Instance Launcher.exe')"
