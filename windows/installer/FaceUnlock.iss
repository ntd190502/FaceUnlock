; Script generated for Inno Setup 6
#define MyAppName "FaceUnlock"
#define MyAppVersion "1.6.1"
#define MyAppPublisher "FaceUnlock Team"
#define MyAppURL "https://github.com/ntd190502/FaceUnlock"
#define MyAppExeName "FaceUnlock.Agent.exe"

[Setup]
AppId={{D374AE64-964B-4E38-B7C4-9A2534C15A11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=FaceUnlock-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "bin\FaceUnlock.Agent.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\FaceUnlock.Service.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\FaceUnlockShell.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\FaceUnlock-Shell-Recovery.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Enable-ShellGate.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Disable-ShellGate.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Check-ShellGate.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Cleanup-PhaseE.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Setup-Ready.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\FaceUnlock"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\FaceUnlock Recovery"; Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\FaceUnlock-Shell-Recovery.ps1"""
Name: "{group}\FaceUnlock Diagnostics"; Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\Check-ShellGate.ps1"""
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Launch Agent after install
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; This runs first and exits nonzero if explorer restore cannot be verified.
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\Setup-Ready.ps1"" -Mode uninstall -InstallDir ""{app}"""; Flags: runhidden waituntilterminated

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  PowerShellExe: String;
  SetupScript: String;
begin
  if CurStep = ssPostInstall then
  begin
    PowerShellExe := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
    SetupScript := ExpandConstant('{app}\Setup-Ready.ps1');
    if (not Exec(PowerShellExe,
      '-NoProfile -ExecutionPolicy Bypass -File "' + SetupScript + '" -Mode install -InstallDir "' + ExpandConstant('{app}') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
    begin
      RaiseException(Format('FaceUnlock service/Shell Gate health setup failed (exit code %d). Explorer recovery was applied; see installer.log.', [ResultCode]));
    end;
  end;
end;
