#define MyAppName "Flux"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Flux"
#define MyAppExeName "Flux.exe"

[Setup]
AppId={{9DE92493-4479-493D-9ED8-C012A5AE2E5B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Flux
DefaultGroupName=Flux
PrivilegesRequired=lowest
OutputDir=..\artifacts\installer
OutputBaseFilename=Flux-{#MyAppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\Flux.Windows\Assets\Flux.ico

[Tasks]
Name: "startup"; Description: "Start Flux with Windows"; GroupDescription: "Additional options:"; Flags: unchecked

[Files]
Source: "..\artifacts\Flux-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Flux"; Filename: "{app}\{#MyAppExeName}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Flux"; ValueData: """{app}\{#MyAppExeName}"" --hidden"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Flux"; Flags: nowait postinstall skipifsilent
