#ifndef AppVersion
  #error AppVersion must be supplied by Build-Release.ps1
#endif

[Setup]
AppId={{7AD79029-311D-4F71-9682-E4835D1FF488}
AppName=RO Eternal Love Launcher
AppVersion={#AppVersion}
AppPublisher=yajra
AppPublisherURL=https://github.com/yajra/rom-multi-client
DefaultDirName={autopf}\RO Eternal Love Launcher
DisableDirPage=yes
UsePreviousAppDir=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts\dist
OutputBaseFilename=RO-Eternal-Love-Launcher-{#AppVersion}-win-x64-Setup
SetupIconFile=..\src\Assets\EternalLove.ico
UninstallDisplayIcon={app}\RO Instance Launcher.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=RO Instance Launcher.exe
RestartApplications=no
UninstallDisplayName=RO Eternal Love Launcher

[Files]
Source: "..\artifacts\release-app\RO Instance Launcher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "installed.txt"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Icons]
Name: "{commonprograms}\RO Eternal Love Launcher"; Filename: "{app}\RO Instance Launcher.exe"; WorkingDir: "{app}"
Name: "{commondesktop}\RO Eternal Love Launcher"; Filename: "{app}\RO Instance Launcher.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\RO Instance Launcher.exe"; Description: "Open RO Eternal Love Launcher"; Flags: nowait postinstall skipifsilent runasoriginaluser

; Only installer-owned files are removed. Profiles, credentials and games are
; outside this directory and must never be listed in UninstallDelete.
