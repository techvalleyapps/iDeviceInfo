; ──────────────────────────────────────────────────────────────────────────────
; iDeviceInfo — Inno Setup installer script
; Builds iDeviceInfoSetup.exe from the published single-file exe.
;
; Usage (local):
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\iDeviceInfo.iss
;
; Usage (CI — GitHub Actions):
;   ISCC.exe installer\iDeviceInfo.iss /DMyAppVersion=%VERSION%
; ──────────────────────────────────────────────────────────────────────────────

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName      "iDeviceInfo"
#define MyAppPublisher "iDeviceInfo"
#define MyAppURL       "https://github.com/iDeviceInfo"
#define MyAppExeName   "iDeviceInfo.exe"
#define MyAppMutex     "iDeviceInfo_SingleInstance"

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
SetupIconFile=
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
AppMutex={#MyAppMutex}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer

; Tell Windows this is a modern, DPI-aware app
AppComments=iOS device info reader — reads Serial, IMEI, Battery Health from connected devices.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; \
  Description: "Start {#MyAppName} automatically when Windows starts"; \
  GroupDescription: "Startup:"; \
  Flags: checked

[Files]
; The single-file self-contained exe produced by dotnet publish
Source: "..\publish\win-x64\{#MyAppExeName}"; \
  DestDir: "{app}"; \
  Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Registry]
; Auto-start on Windows login (current user, so no UAC needed at runtime)
Root: HKCU; \
  Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; \
  ValueName: "{#MyAppName}"; \
  ValueData: """{app}\{#MyAppExeName}"""; \
  Flags: uninsdeletevalue; \
  Tasks: autostart

[Run]
; Kill any running instance before overwriting the exe
Filename: "taskkill"; \
  Parameters: "/f /im {#MyAppExeName}"; \
  Flags: runhidden shellexec waituntilterminated; \
  StatusMsg: "Stopping existing instance..."

; Launch the app after install completes (optional, user can skip)
Filename: "{app}\{#MyAppExeName}"; \
  Description: "Launch {#MyAppName} now"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the running app before uninstalling
Filename: "taskkill"; \
  Parameters: "/f /im {#MyAppExeName}"; \
  Flags: runhidden shellexec waituntilterminated

[Code]
// Show a friendly message if the app is running when the user starts the installer.
// The [Run] taskkill above handles it silently, but this gives better UX.
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
