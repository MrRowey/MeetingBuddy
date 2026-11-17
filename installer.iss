#define MyAppName "Meeting Buddy"
#define MyAppVersion "3.4.0-rc"
#define MyAppExeName "MeetingBuddy.exe"
[Setup]
AppId={{A34C86B8-5960-4519-A471-5D6D6B1E2D71}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\Meeting Buddy
DefaultGroupName=Meeting Buddy
OutputDir=installer-output
OutputBaseFilename=MeetingBuddy-Setup-win-x64
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Meeting Buddy"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Meeting Buddy"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"
[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Meeting Buddy"; Flags: nowait postinstall skipifsilent
