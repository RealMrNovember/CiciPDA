; Inno Setup 6 script. Built by installer\build-installer.ps1 (needs ISCC.exe; NOT verified on this machine - Inno Setup is not installed).
#define AppName "CiciPDA"
#ifndef AppVersion
  #define AppVersion "2.1.4"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{B5C0E9B4-6F8E-4C1B-9F55-3C1A7A3E6D21}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisherURL=https://cicibyte.com
AppSupportURL=https://github.com/RealMrNovember/CiciPDA/issues
AppUpdatesURL=https://github.com/RealMrNovember/CiciPDA/releases
AppCopyright=Copyright (c) 2026 CiciByte Technology, Inc.
VersionInfoCompany=CiciByte Technology, Inc.
VersionInfoProductName=CiciPDA
VersionInfoVersion={#AppVersion}
AppPublisher=CiciByte Technology, Inc.
DefaultDirName={autopf}\CiciByte\CiciPDA
DefaultGroupName=CiciByte
PrivilegesRequired=admin
; Lets automation validate the installer without UAC: setup.exe /CURRENTUSER /DIR=...
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=CiciPDA-{#AppVersion}-windows-x64-Setup
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\CiciPDA.exe
SetupIconFile=..\src\app\app.ico
WizardStyle=modern
; Code signing: pass /Ssigntool="signtool sign ..." to ISCC and uncomment the next line for release builds.
; SignTool=signtool
; Closes a running CiciPDA so files can be replaced.
CloseApplications=yes

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\THIRD_PARTY.md"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\CiciPDA.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\CiciPDA.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Registry]
; The virtual adapter, routes and DNS need administrator rights; ask for them at launch instead of making the user find a setting.
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"; ValueType: string; ValueName: "{app}\CiciPDA.exe"; ValueData: "~ RUNASADMIN"; Flags: uninsdeletevalue; Check: IsAdminInstallMode

[Run]
; CiciPDA.exe is flagged to run as administrator, which plain CreateProcess refuses (error 740). shellexec goes through ShellExecute,
; which shows the UAC prompt and then starts the app. It also relaunches the app after a silent in-app update (/SILENT);
; fully silent runs (/VERYSILENT, used by automated validation) never launch it.
Filename: "{app}\CiciPDA.exe"; Description: "Launch CiciPDA"; Flags: nowait postinstall shellexec; Check: NotVerySilent

[Code]
// True unless the installer was started with /VERYSILENT (automated validation). An in-app update runs with /SILENT and does relaunch the app.
function NotVerySilent: Boolean;
var
  i: Integer;
begin
  Result := True;
  for i := 1 to ParamCount do
    if CompareText(ParamStr(i), '/VERYSILENT') = 0 then Result := False;
end;
