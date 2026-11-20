; This installer and its build scripts belong only to Ripley.
#define AppName "Ripley"
#define AppIdentity "CodingWithCalvin.Ripley"
#define AppTitle AppName
#define InstallRoot "{localappdata}\Programs\Coding With Calvin\" + AppName
#define MenuRoot "{userprograms}\" + AppName
#ifndef AppSource
  #error AppSource must point to this app's published payload.
#endif
#ifndef AppVersion
  #error AppVersion is required.
#endif
#ifndef OutputRoot
  #error OutputRoot is required.
#endif
; Local rehearsals compile the same installer with only identity and locations isolated.
#ifdef ValidationRoot
  #ifndef ValidationId
    #error ValidationId is required for an isolated rehearsal.
  #endif
  #undef AppIdentity
  #define AppIdentity ValidationId
  #undef AppTitle
  #define AppTitle AppName + " installer validation"
  #undef InstallRoot
  #define InstallRoot ValidationRoot + "\app with spaces"
  #undef MenuRoot
  #define MenuRoot ValidationRoot + "\shortcuts"
#endif

[Setup]
AppId={#AppIdentity}
AppName={#AppTitle}
AppVersion={#AppVersion}
AppPublisher=Coding With Calvin
AppPublisherURL=https://github.com/CodingWithCalvin/Ripley
AppSupportURL=https://github.com/CodingWithCalvin/Ripley/issues
AppUpdatesURL=https://github.com/CodingWithCalvin/Ripley/releases
DefaultDirName={#InstallRoot}
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupArchitecture=x64
MinVersion=10.0.19041
OutputDir={#OutputRoot}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\Ripley\Assets\Ripley.ico
UninstallDisplayIcon={app}\{#AppName}.exe
LicenseFile={#AppSource}\LICENSE.txt
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
VersionInfoCompany=Coding With Calvin
WizardStyle=modern
Compression=lzma2/normal
SolidCompression=yes
SetupMutex=Local\{#AppIdentity}.Setup
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=yes
Uninstallable=yes

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{#MenuRoot}\{#AppName}"; Filename: "{app}\{#AppName}.exe"; WorkingDir: "{app}"

; No Run, InstallDelete, or UninstallDelete actions. App data and media are never installer-owned.
[Code]
function LockedFileIn(const Directory: String): String;
var
  Entry: TFindRec;
  Path: String;
  Stream: TFileStream;
begin
  Result := '';
  if not DirExists(Directory) then exit;
  if FindFirst(AddBackslash(Directory) + '*', Entry) then
  try
    repeat
      if (Entry.Name <> '.') and (Entry.Name <> '..') then
      begin
        Path := AddBackslash(Directory) + Entry.Name;
        if (Entry.Attributes and $400) <> 0 then
          Result := Path
        else if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          Result := LockedFileIn(Path)
        else if Pos('unins', LowerCase(Entry.Name)) <> 1 then
        begin
          try
            Stream := TFileStream.Create(Path, fmOpenReadWrite or fmShareExclusive);
            Stream.Free;
          except
            Result := Path;
          end;
        end;
        if Result <> '' then exit;
      end;
    until not FindNext(Entry);
  finally
    FindClose(Entry);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExistingText, Busy: String;
  ExistingVersion, NewVersion: Int64;
begin
  Result := '';
  if RegQueryStringValue(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdentity}_is1', 'DisplayVersion', ExistingText) then
  begin
    if not StrToVersion(ExistingText, ExistingVersion) then
    begin
      Result := 'The installed version could not be verified. Uninstall it before installing this version. Your saved data will be kept.';
      exit;
    end;
    if StrToVersion('{#AppVersion}', NewVersion) and (ComparePackedVersion(ExistingVersion, NewVersion) > 0) then
    begin
      Result := 'A newer version of {#AppName} is already installed. Downgrades are not supported.';
      exit;
    end;
  end;
  Busy := LockedFileIn(ExpandConstant('{app}'));
  if Busy <> '' then
    Result := 'Close {#AppName} and its tools after processing finishes, then retry Setup. A file is in use or cannot be updated: ' + Busy;
end;

function InitializeUninstall: Boolean;
var Busy: String;
begin
  Busy := LockedFileIn(ExpandConstant('{app}'));
  Result := Busy = '';
  if not Result then
  begin
    Log('Uninstall refused: file is in use or cannot be updated: ' + Busy);
    if not UninstallSilent then
      MsgBox('Close {#AppName} and its tools after processing finishes, then retry uninstall. Your saved data will be kept.', mbError, MB_OK);
  end;
end;
