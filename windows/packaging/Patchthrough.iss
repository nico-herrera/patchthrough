#ifndef AppVersion
  #error AppVersion must be supplied by build-release.ps1
#endif
#ifndef VersionInfoVersion
  #error VersionInfoVersion must be supplied by build-release.ps1
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by build-release.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by build-release.ps1
#endif
#ifndef RepoRoot
  #error RepoRoot must be supplied by build-release.ps1
#endif

#define AppName "Patchthrough"
; The console tool, which owns the command line and the PATH entry.
#define AppExeName "Patchthrough.exe"
; The window and the tray icon, which is what a person launches.
#define AppGuiExeName "PatchthroughApp.exe"
#define AppPublisher "Nico Herrera"
#define AppUrl "https://github.com/nico-herrera/patchthrough"

[Setup]
AppId={{A1CC154A-BD5C-47A6-A420-684DE70B2C6E}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright (C) Nico Herrera
VersionInfoVersion={#VersionInfoVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Patchthrough for Windows setup
VersionInfoCopyright=Copyright (C) Nico Herrera
DefaultDirName={autopf}\Patchthrough
DefaultGroupName=Patchthrough
DisableProgramGroupPage=yes
LicenseFile={#RepoRoot}\LICENSE
UninstallDisplayIcon={app}\{#AppGuiExeName}
OutputDir={#OutputDir}
OutputBaseFilename=Patchthrough-windows-x64-setup
SetupIconFile={#RepoRoot}\windows\packaging\patchthrough.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
ChangesEnvironment=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "addtopath"; Description: "Add Patchthrough to my user PATH"; GroupDescription: "Command line:"; Flags: checkedonce
; Checked by default. Patchthrough records meetings, so it has to be running
; before a meeting starts, and a recorder the user must remember to launch is a
; recorder that misses the first minute.
Name: "startatlogin"; Description: "Start Patchthrough when I sign in"; GroupDescription: "Startup:"

[Icons]
; Without this the install leaves no way to launch the app. It is a tray
; application, so there is no file a user would think to open.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppGuiExeName}"; Comment: "Record a meeting and hand the transcript to your agent"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExeName}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExeName}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletekey
; Start at sign-in. The value name must stay "Patchthrough": LoginLaunch.cs reads
; and writes the same name, so the Settings toggle and this entry are one setting
; rather than two that disagree. The path is quoted because the default install
; directory sits under a name with a space in it. uninsdeletevalue matters as
; much as the write: a leftover entry would launch a deleted executable at every
; sign-in, which Windows reports to the user as a failed startup item.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Patchthrough"; ValueData: """{app}\{#AppGuiExeName}"""; Flags: uninsdeletevalue; Tasks: startatlogin

[Run]
; Offered, not forced. Nothing about the install needs the app running, but a
; user who just installed a tray application expects to see it appear.
Filename: "{app}\{#AppGuiExeName}"; Description: "Start Patchthrough now"; Flags: nowait postinstall skipifsilent

[Code]
const
  UserEnvironmentKey = 'Environment';
  UserPathValue = 'Path';

function TrimTrailingSlashes(Value: String): String;
begin
  Result := Trim(Value);
  while (Length(Result) > 3) and (Result[Length(Result)] = '\') do
    Delete(Result, Length(Result), 1);
end;

function IsAppPath(Value: String): Boolean;
begin
  Result := CompareText(
    TrimTrailingSlashes(Value),
    TrimTrailingSlashes(ExpandConstant('{app}'))) = 0;
end;

function PathContainsApp(const CurrentPath: String): Boolean;
var
  Entries: TArrayOfString;
  Index: Integer;
begin
  Result := False;
  Entries := StringSplit(CurrentPath, [';'], stAll);
  for Index := 0 to GetArrayLength(Entries) - 1 do
  begin
    if IsAppPath(Entries[Index]) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

procedure AddAppToUserPath;
var
  CurrentPath: String;
  NewPath: String;
begin
  if not RegQueryStringValue(HKCU, UserEnvironmentKey, UserPathValue, CurrentPath) then
    CurrentPath := '';
  if PathContainsApp(CurrentPath) then
    Exit;

  if (CurrentPath <> '') and (CurrentPath[Length(CurrentPath)] <> ';') then
    NewPath := CurrentPath + ';' + ExpandConstant('{app}')
  else
    NewPath := CurrentPath + ExpandConstant('{app}');

  if not RegWriteExpandStringValue(HKCU, UserEnvironmentKey, UserPathValue, NewPath) then
    RaiseException('Setup could not add Patchthrough to your user PATH.');
end;

procedure RemoveAppFromUserPath;
var
  CurrentPath: String;
  Entries: TArrayOfString;
  Index: Integer;
  Entry: String;
  NewPath: String;
begin
  if not RegQueryStringValue(HKCU, UserEnvironmentKey, UserPathValue, CurrentPath) then
    Exit;

  Entries := StringSplit(CurrentPath, [';'], stAll);
  NewPath := '';
  for Index := 0 to GetArrayLength(Entries) - 1 do
  begin
    Entry := Entries[Index];
    if (Entry <> '') and not IsAppPath(Entry) then
    begin
      if NewPath <> '' then
        NewPath := NewPath + ';';
      NewPath := NewPath + Entry;
    end;
  end;

  if NewPath <> CurrentPath then
  begin
    if not RegWriteExpandStringValue(HKCU, UserEnvironmentKey, UserPathValue, NewPath) then
      Log('warning: uninstall could not remove Patchthrough from the user PATH');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddAppToUserPath;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveAppFromUserPath;
end;
