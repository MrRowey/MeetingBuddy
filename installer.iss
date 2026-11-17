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
CloseApplications=yes
RestartApplications=no
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

; All application binaries and shortcuts are tracked by Inno Setup.
; Personal meeting data is preserved by default, with an explicit removal option.
[Code]
var
  RemovePersonalData: Boolean;

function InitializeUninstall(): Boolean;
var
  Answer: Integer;
begin
  Answer := MsgBox(
    'Would you also like to permanently remove your Meeting Buddy settings, saved meetings and backups?' + #13#10 + #13#10 +
    'YES: Delete all local Meeting Buddy data.' + #13#10 +
    'NO: Keep your data for a future reinstall.' + #13#10 +
    'CANCEL: Stop uninstalling.',
    mbConfirmation, MB_YESNOCANCEL);
  Result := Answer <> IDCANCEL;
  RemovePersonalData := Answer = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Remove the Windows startup registration even if enabled from app settings. }
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MeetingBuddy');
    if RemovePersonalData then
    begin
      DataDir := ExpandConstant('{localappdata}\MeetingBuddy');
      if DirExists(DataDir) and not DelTree(DataDir, True, True, True) then
        MsgBox('Some personal files could not be deleted from:' + #13#10 + DataDir +
          #13#10 + 'Close any programs using these files and remove the folder manually.', mbError, MB_OK);
    end;
  end;
end;
