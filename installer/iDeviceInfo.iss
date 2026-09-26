; ──────────────────────────────────────────────────────────────────────────────
; iDeviceInfo — Inno Setup installer script
; ──────────────────────────────────────────────────────────────────────────────

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName      "iDeviceInfo"
#define MyAppPublisher "iDeviceInfo"
#define MyAppURL       "https://github.com/iDeviceInfo"
#define MyAppExeName   "iDeviceInfo.exe"

[Setup]
AppId={{A3F2C1D0-4B7E-4A9F-8C3D-1E2F5A6B7C8D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\installer\output
OutputBaseFilename=iDeviceInfoSetup
SetupIconFile=..\Resources\iDeviceInfo.ico
WizardSmallImageFile=compiler:WizModernSmallImage.bmp
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer
AppComments=iOS and Android device info reader — reads Serial, IMEI, Battery Health from connected devices.
CloseApplications=yes
CloseApplicationsFilter=*{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\publish\win-x64\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Android platform-tools (adb) — downloaded by the CI build into publish\win-x64\platform-tools
Source: "..\publish\win-x64\platform-tools\*"; DestDir: "{app}\platform-tools"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Desktop shortcut
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

; Start Menu shortcuts
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[UninstallRun]
; adb leaves a background server running that would lock adb.exe
Filename: "{app}\platform-tools\adb.exe"; Parameters: "kill-server"; Flags: runhidden; RunOnceId: "AdbKillServer"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Adb: String;
begin
  // Stop a running adb server from a previous install so the files can be replaced
  Adb := ExpandConstant('{app}\platform-tools\adb.exe');
  if FileExists(Adb) then
    Exec(Adb, 'kill-server', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

[Run]
; Launch the app after install (also as admin, via the shortcut flag)
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent shellexec
