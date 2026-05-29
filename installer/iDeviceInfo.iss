; ──────────────────────────────────────────────────────────────────────────────
; iDeviceInfo — Inno Setup installer script
; Builds iDeviceInfoSetup.exe from the published single-file exe.
;
; Usage (local):
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\iDeviceInfo.iss
;
; Usage (CI — GitHub Actions):
;   ISCC.exe installer\iDeviceInfo.iss /DMyAppVersion=1.0.0
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
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer
AppComments=iOS device info reader — reads Serial, IMEI, Battery Health from connected devices.
CloseApplications=yes
CloseApplicationsFilter=*{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start {#MyAppName} automatically when Windows starts"; GroupDescription: "Startup options:"; Flags: checked

[Files]
Source: "..\publish\win-x64\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent
