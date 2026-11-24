; Cliplet installer — double-click Setup to install / upgrade
#define MyAppName "Cliplet"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "Cliplet"
#define MyAppExeName "Cliplet.exe"
#define MyAppURL "https://github.com"

[Setup]
; Same AppId as 1.0.0 → Inno treats this as an upgrade of the existing install
AppId={{A7C3E91B-4F2D-4B8A-9E1C-CLIPLETWIN01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; On upgrade, reuse previous install folder and skip directory page
UsePreviousAppDir=yes
UsePreviousTasks=yes
UsePreviousGroup=yes
DisableDirPage=auto
OutputDir=..\dist
OutputBaseFilename=Cliplet-Setup
SetupIconFile=
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
; Close running Cliplet so files can be overwritten
CloseApplications=force
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
WizardSizePercent=100
DisableWelcomePage=no
; Single-instance mutex used by the app
AppMutex=Local\Cliplet.Windows.SingleInstance

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项:"; Flags: checkedonce
Name: "autostart"; Description: "开机时自动启动 Cliplet"; GroupDescription: "附加选项:"; Flags: checkedonce

[Files]
; ignoreversion → always replace with files from this package (upgrade-safe)
Source: "..\publish-installer\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Cliplet"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 Cliplet"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Cliplet\thumbs"
Type: filesandordirs; Name: "{localappdata}\Cliplet\images"

[Code]
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  { Ensure old process is gone before overwriting binaries }
  Exec('taskkill.exe', '/IM Cliplet.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  Exec('taskkill.exe', '/IM Cliplet.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
