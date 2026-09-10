#define MyAppName "Flux"
#ifndef MyAppVersion
#define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "Flux"
#define MyAppExeName "Flux.exe"

[Setup]
AppId={{9DE92493-4479-493D-9ED8-C012A5AE2E5B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Flux
DefaultGroupName=Flux
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts\installer
OutputBaseFilename=Flux-{#MyAppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\Flux.Windows\Assets\Flux.ico
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Flux Windows Installer
VersionInfoProductName={#MyAppName}
CloseApplications=yes
RestartApplications=yes
AllowNetworkDrive=no
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
SetupLogging=yes
LicenseFile=..\LICENSE

[Tasks]
Name: "startup"; Description: "Start Flux with Windows"; GroupDescription: "Additional options:"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional options:"; Flags: unchecked

[Files]
Source: "..\artifacts\Flux-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Flux"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Flux"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Flux"; ValueData: """{app}\{#MyAppExeName}"" --hidden"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Flux"; Flags: nowait postinstall
