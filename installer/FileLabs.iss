; Inno Setup script for File Labs. Build it through build.ps1, which publishes the app first and
; passes the version in.

#define AppName "File Labs"
#define AppExe "FileLabs.exe"
#define AppPublisher "Protagonist Labs"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
; Never change AppId: it is what ties an update to the installation it replaces.
AppId={{7E4C2B9A-3F1D-4C8E-9A6B-51D0C2E8F4A7}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; Per-user: no UAC prompt, and the default-file-manager keys it writes are per-user too (HKCU).
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

CloseApplications=force
RestartApplications=no

OutputDir=..\dist
OutputBaseFilename=FileLabs-{#AppVersion}-setup
SetupIconFile=..\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "defaultfm"; Description: "Use File Labs instead of File Explorer (opening folders and drives, Win+E). Can be changed later in Settings."; GroupDescription: "Windows integration:"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; The app writes the keys itself (same code as the Settings switch), pointing at the installed exe.
Filename: "{app}\{#AppExe}"; Parameters: "--set-default"; StatusMsg: "Setting File Labs as the default file manager..."; Flags: runhidden waituntilterminated; Tasks: defaultfm
; An update closes every File Labs process, the Win+E agent included: bring it back if it was on.
Filename: "{app}\{#AppExe}"; Parameters: "--agent"; Flags: nowait runhidden; Check: AgentWanted
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Hand folders back to File Explorer before the exe the keys point at disappears.
Filename: "{app}\{#AppExe}"; Parameters: "--unset-default"; Flags: runhidden waituntilterminated; RunOnceId: "UnsetDefault"

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\FileLabs"

[Code]
{ Setup cannot replace files the running app holds open, so stop it first. }
procedure StopFileLabs();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(300);
end;

{ The agent's autostart entry exists while File Labs is the default file manager. When the
  defaultfm task runs, --set-default starts the agent itself, so this only covers updates. }
function AgentWanted(): Boolean;
begin
  Result := RegValueExists(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'File Labs Agent')
            and not WizardIsTaskSelected('defaultfm');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopFileLabs();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopFileLabs();
  Result := True;
end;
