[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\ROInstances.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Project Version must use major.minor.patch.' }
$trackedVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'version.txt') -Raw).Trim()
$manifestVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '.release-please-manifest.json') -Raw | ConvertFrom-Json).'.'
if ($version -ne $trackedVersion -or $version -ne $manifestVersion) { throw 'Project, version.txt, and release manifest versions must match.' }
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne "v$version") {
    throw "Release tag $env:GITHUB_REF_NAME does not match project version v$version."
}
$publishPath = Join-Path $PSScriptRoot 'artifacts\release-app'
$distPath = Join-Path $PSScriptRoot 'artifacts\dist'
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishPath --nologo `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=embedded
if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
$publishedExe = Join-Path $publishPath 'RO Instance Launcher.exe'
& (Join-Path $PSScriptRoot 'Test.ps1') -SkipBuild -AppPath $publishedExe
New-Item -ItemType Directory -Path $distPath -Force | Out-Null
$assetName = "RO-Eternal-Love-Launcher-$version-win-x64.exe"
$assetPath = Join-Path $distPath $assetName
Copy-Item -LiteralPath $publishedExe -Destination $assetPath -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $distPath 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CHANGELOG.md') -Destination (Join-Path $distPath 'CHANGELOG.md') -Force
$hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $distPath 'SHA256SUMS.txt') -Value "$hash  $assetName" -Encoding ascii
Write-Output "Release executable: $assetPath"
Write-Output "SHA-256: $hash"
