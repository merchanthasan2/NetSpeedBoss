; NetSpeed Boss installer (Inno Setup 6)
; Build:  ISCC.exe /DAppVersion=1.0.0 installer\NetSpeedBoss.iss   (or: build.ps1 -Installer)

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "NetSpeed Boss"
#define AppExe "NetSpeedBoss.exe"
#define AppUrl "https://github.com/merchanthasan2/NetSpeedBoss"

[Setup]
AppId={{7C1E4A52-9B3D-4E8F-A6D1-2F5B8C9E0A41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Quantum Step
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
; per-user install: no administrator rights or UAC prompt needed
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64
MinVersion=10.0
WizardStyle=modern
WizardSizePercent=100
SetupIconFile=..\src\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
OutputBaseFilename=NetSpeedBoss-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nSee your live download and upload speed right on the taskbar, find out which apps are using your connection, and keep track of how much data you use every day and month.%n%nIt is free, open source, and needs no administrator rights.
FinishedLabel=Setup has finished installing [name].%n%nYour speed appears on the taskbar next to the clock. Drag it to move it, double-click it for the dashboard, and right-click it for options.

[Tasks]
Name: "startup"; Description: "Start {#AppName} automatically when I sign in to Windows"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "..\bin\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Live network speed on your taskbar"
Name: "{group}\{#AppName} Dashboard"; Filename: "{app}\{#AppExe}"; Parameters: "--dashboard"; Comment: "Speed graph, apps and data usage"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; same value the app's own "Start with Windows" switch uses, so both stay in sync
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NetSpeedBoss"; \
  ValueData: """{app}\{#AppExe}"" --startup"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN NetSpeedBoss /F"; Flags: runhidden; RunOnceId: "DelTask"

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  { close a running copy so the exe can be replaced during upgrades }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'NetSpeedBoss');
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) and DirExists(ExpandConstant('{userappdata}\NetSpeedBoss')) then
    if MsgBox('Also delete your NetSpeed Boss settings and data usage history?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\NetSpeedBoss'), True, True, True);
end;
