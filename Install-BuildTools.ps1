[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$toolRoot = Join-Path $PSScriptRoot 'artifacts\tools'
$compiler = Join-Path $toolRoot 'InnoSetup\ISCC.exe'
if (Test-Path -LiteralPath $compiler) { return $compiler }
New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
$download = Join-Path $toolRoot 'innosetup-7.1.1-x64.exe'
Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe' -OutFile $download
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne '8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0') {
    throw 'Inno Setup download checksum did not match.'
}
$toolDir = Join-Path $toolRoot 'InnoSetup'
$process = Start-Process -FilePath $download -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', ('/DIR="' + $toolDir + '"')) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $compiler)) { throw 'Inno Setup installation failed.' }
return $compiler
