; Inno Setup script for RemoteFlow. Compiled by scripts/publish-windows.ps1, which supplies every
; define below; there are no defaults on purpose, so a hand-run compile fails loudly rather than
; producing an installer that claims the wrong version or architecture.
;
; MSIX was considered and rejected: its container model restricts Credential Manager access and
; arbitrary local filesystem access, which is precisely what an SSH/SFTP client exists to do.
;
; Per-user install. PrivilegesRequired=lowest keeps this out of Program Files and out of an elevation
; prompt, which also means an uninstall cannot touch another account's data.

#ifndef AppVersion
  #error AppVersion must be supplied: ISCC /DAppVersion=0.1.0
#endif
#ifndef FileVersion
  #error FileVersion must be supplied: ISCC /DFileVersion=0.1.0.0
#endif
#ifndef AppArchitecture
  #error AppArchitecture must be supplied: ISCC /DAppArchitecture=x64 (or arm64)
#endif
#ifndef SourceDir
  #error SourceDir must be supplied: the published, self-contained output directory
#endif
#ifndef OutputDir
  #error OutputDir must be supplied: where to write the installer
#endif
#ifndef OutputBaseName
  #error OutputBaseName must be supplied: the installer filename without its extension
#endif
#ifndef RepositoryRoot
  #error RepositoryRoot must be supplied: used for the licence and the setup icon
#endif

#define AppName "RemoteFlow"
#define AppPublisher "michaelou"
#define AppExeName "RemoteFlow.exe"

[Setup]
; Never change AppId: it is how Windows recognises an existing install to upgrade or remove.
AppId={{6A084A9C-3CFB-4C8F-A7A8-AA5B34D9C91F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#FileVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
; One shortcut directly under Programs, not a folder containing one shortcut.
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#AppArchitecture == "arm64" ? "arm64" : "x64compatible"}
ArchitecturesInstallIn64BitMode={#AppArchitecture == "arm64" ? "arm64" : "x64compatible"}
LicenseFile={#RepositoryRoot}\LICENSE
SetupIconFile={#RepositoryRoot}\src\RemoteFlow.UI\Assets\remoteflow.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The app must not be running while its own files are replaced.
CloseApplications=yes
RestartApplications=no
; RemoteFlow holds a mutex of this name for as long as it is running, so Setup and the uninstaller can tell
; and stop to ask rather than replacing or deleting files underneath it. This fires before a single file is
; touched, which CloseApplications cannot, and it covers the uninstaller, which had no such check at all.
; Must stay byte-for-byte identical to RunningInstanceMutex.Name: Windows compares mutex names
; case-sensitively, and a mismatch fails in the unhelpful direction, with Setup concluding nothing is
; running. Session-local rather than Global\, because the install is per-user and the global namespace
; needs a privilege a standard user may not hold.
AppMutex=RemoteFlow-6A084A9C-3CFB-4C8F-A7A8-AA5B34D9C91F

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Unchecked by default: a shortcut on someone's desktop is their decision, not the installer's.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole self-contained publish output, runtime included, so the machine needs no .NET installed.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; WorkingDir matters on both entries: without it the launched RemoteFlow inherits Setup's own current
; directory, which is inside the temp folder SetupLdr extracted itself to. SetupLdr then cannot delete it,
; and every install leaks a directory.
Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; The in-app updater's relaunch. RemoteFlow closes itself, runs this installer silently with /UPDATE, and
; expects to be started again afterwards — the entry above is skipped in silent mode, which is both what
; makes that possible and what makes this one necessary. skipifnotsilent keeps it out of an interactive
; install, where the checkbox above already offers it; the Check keeps it out of every other silent
; install, because a CI smoke test and an administrator deploying RemoteFlow unattended must not have a
; window appear on their screen.
Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Flags: nowait skipifnotsilent; Check: RelaunchAfterUpdateRequested

[Code]
{ Neither /UPDATE nor /PURGEDATA is an Inno Setup switch. Setup and the uninstaller ignore parameters they
  do not recognise and pass them through to [Code], where ParamStr sees them. }

function CommandLineFlagPresent(const Flag: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    if CompareText(ParamStr(Index), Flag) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

{ Named in the Check: parameter of the second [Run] entry above. Setup calls it once, when it decides
  whether to process that entry. Deliberately does not consult WizardSilent: skipifnotsilent already covers
  that, and WizardSilent is unavailable to the uninstaller, so keeping it out leaves the shared helper above
  safe for both. }
function RelaunchAfterUpdateRequested(): Boolean;
begin
  Result := CommandLineFlagPresent('/UPDATE');
end;

{ Uninstall removes the program and nothing else. Connections, settings, host keys, and credential
  references live in %APPDATA%\RemoteFlow, and losing them because someone reinstalled would be the
  worst kind of data loss: silent, and caused by a routine action. The user has to ask for it, either
  by answering the prompt or by passing /PURGEDATA to a silent uninstall. }

function DataDirectory(): String;
begin
  Result := ExpandConstant('{userappdata}\RemoteFlow');
end;

function PurgeRequestedOnCommandLine(): Boolean;
begin
  Result := CommandLineFlagPresent('/PURGEDATA');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Directory: String;
  Purge: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  Directory := DataDirectory();
  if not DirExists(Directory) then
    Exit;

  if UninstallSilent() then
    Purge := PurgeRequestedOnCommandLine()
  else
    { Defaults to No: the destructive answer is never the one a stray Enter selects. }
    Purge := SuppressibleMsgBox(
      'Also delete RemoteFlow''s saved connections, settings, and stored credential references?' + #13#10#13#10 +
      Directory + #13#10#13#10 +
      'Choose No to keep them for a future install.',
      mbConfirmation,
      MB_YESNO or MB_DEFBUTTON2,
      IDNO) = IDYES;

  if Purge then
    DelTree(Directory, True, True, True);
end;
