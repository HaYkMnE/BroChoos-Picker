; W10.1 scope: per-user installer/uninstall/registration recovery only.
; No machine-wide behavior, no HKLM writes.

; Product identity. AppName/AppPublisher must equal <Product>/<Company> in Directory.Build.props
; and the winget/Scoop templates under packaging/ (ReleasePipelineTests pins all of them).
; AppId and AppExeName are FROZEN: upgrades and the Add/Remove Programs key ({AppId}_is1) depend on them.
#define AppName "BroChoos Picker"
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#define AppPublisher "BroChoos"
#define AppExeName "BroChoos.exe"
#ifndef AppPublishDir
  #define AppPublishDir "..\\..\\src\\BroChoos.App\\bin\\Release\\net10.0-windows10.0.19041.0\\win-x64\\publish"
#endif

[Setup]
AppId={{D4D2C59D-B9D0-44D8-A44D-2AE5D749F9C5}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
UninstallDisplayName={#AppName}
DefaultDirName={localappdata}\Programs\BroChoos
DefaultGroupName={#AppName}
; Always use the "BroChoos Picker" Start menu folder. The default (yes) would reuse a previous
; install's "BroChoos" folder, so an upgrade would keep the old folder name forever. The old
; folder's shortcuts are removed in [InstallDelete].
UsePreviousGroup=no
DisableProgramGroupPage=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
DisableDirPage=no
Compression=lzma
SolidCompression=yes
WizardStyle=modern
; Relative paths resolve against this script's directory (packaging\BroChoos.Installer), so output lands in
; <repo>\artifacts\installer, which is covered by the repo .gitignore ("artifacts/").
OutputDir=..\..\artifacts\installer
; Release asset name. tools/build-release.ps1 and .github/workflows/release.yml expect exactly this.
OutputBaseFilename=BroChoos-Picker-Setup-{#AppVersion}-x64
UninstallDisplayIcon={app}\{#AppExeName}
; Relative to this script's directory, like OutputDir. Same file as the exe's ApplicationIcon.
SetupIconFile=..\..\src\BroChoos.App\Assets\brochoos.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; Shortcuts written by builds that used AppName "BroChoos". They lived in the "BroChoos" folder,
; which is NOT {group} any more (UsePreviousGroup=no), so the folder is named explicitly.
; {autoprograms} is the folder {group} is created in (per-user here). The folder itself is removed
; only if it is empty, so anything the user put there is kept.
[InstallDelete]
Type: files; Name: "{autoprograms}\BroChoos\BroChoos.lnk"
Type: files; Name: "{autoprograms}\BroChoos\Uninstall BroChoos.lnk"
Type: dirifempty; Name: "{autoprograms}\BroChoos"

[Files]
Source: "{#AppPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[UninstallRun]
Filename: "{app}\{#AppExeName}"; Parameters: "--unregister"; Flags: runhidden skipifdoesntexist; RunOnceId: "BroChoosUnregister"

[Code]
function RunRegistration(const Args: string; const ContextName: string): Integer;
var
  ExitCode: Integer;
  ExecutablePath: string;
begin
  ExitCode := 1;
  ExecutablePath := ExpandConstant('{app}\{#AppExeName}');
  if not FileExists(ExecutablePath) then
  begin
    Log(ContextName + ': executable not found for ' + Args + ': ' + ExecutablePath);
    Result := 1;
    exit;
  end;

  if not Exec(ExecutablePath, Args, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Log(ContextName + ': failed to launch BroChoos for ' + Args);
    Result := 1;
    exit;
  end;

  Log(ContextName + ': BroChoos exited with code ' + IntToStr(ExitCode) + ' for ' + Args);
  Result := ExitCode;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  RegisterExit: Integer;
  UnregisterExit: Integer;
begin
  if CurStep <> ssPostInstall then
    exit;

  RegisterExit := RunRegistration('--register', 'post-install register');
  if RegisterExit = 0 then
    exit;

  // W10.1 registration recovery: one bounded recovery cycle.
  Log('Registration failed; attempting recovery cycle: unregister -> register');

  UnregisterExit := RunRegistration('--unregister', 'recovery unregister');
  if UnregisterExit <> 0 then
  begin
    Log('Recovery unregister failed with code ' + IntToStr(UnregisterExit));
    RaiseException('BroChoos could not complete registration. Recovery step "unregister" failed (exit code ' + IntToStr(UnregisterExit) + '). Check that BroChoos is closed, verify files exist under the selected install folder, then rerun the installer and review the setup log for details.');
  end;

  RegisterExit := RunRegistration('--register', 'recovery register');
  if RegisterExit <> 0 then
  begin
    Log('Recovery register failed with code ' + IntToStr(RegisterExit));
    RaiseException('BroChoos could not complete registration. Recovery step "register" failed (exit code ' + IntToStr(RegisterExit) + '). Check that BroChoos is closed, confirm the install directory is writable for this user, then rerun the installer and review the setup log for details.');
  end;
end;
