; Script generated for Inno Setup 6
#define MyAppName "FaceUnlock"
#define MyAppVersion "1.2.0"
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
Source: "bin\FaceUnlockCredentialProvider.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\FaceUnlockAuthPackage.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\FaceUnlock-Shell-Recovery.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Enable-ShellGate.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Disable-ShellGate.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Check-ShellGate.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Cleanup-PhaseE.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\FaceUnlock-Recovery.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Enable-CredentialProvider.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Disable-CredentialProvider.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Enable-AuthPackage.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Disable-AuthPackage.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Check-AuthPackage.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName} Agent"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} Shell Gate (Test Mode)"; Filename: "{app}\FaceUnlockShell.exe"; Parameters: "--test"
Name: "{group}\FaceUnlock - Check Shell Gate"; Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\Check-ShellGate.ps1"""
Name: "{group}\FaceUnlock - Enable Shell Gate (Manual)"; Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\Enable-ShellGate.ps1"""
Name: "{group}\FaceUnlock - Disable Shell Gate"; Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\Disable-ShellGate.ps1"""
Name: "{group}\FaceUnlock Shell Emergency Recovery"; Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\FaceUnlock-Shell-Recovery.ps1"""
Name: "{group}\FaceUnlock - Cleanup Phase E AuthPackage"; Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\Cleanup-PhaseE.ps1"""
Name: "{group}\FaceUnlock Legacy Recovery"; Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\FaceUnlock-Recovery.ps1"""
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

; ============================================================
; DISABLED: Automatic Credential Provider registration
; ============================================================
; Reason: FaceUnlock Credential Provider had critical bugs that caused
; Windows lock screen to flicker infinitely and become unusable.
; Auto-registration is DISABLED until CP_SAFE_FOR_LOGONUI_TEST=YES
; as confirmed by the standalone CredentialProviderHarness test suite.
;
; To manually enable after validation:
;   Start Menu -> FaceUnlock -> Enable Credential Provider (Advanced)
;   OR run: Enable-CredentialProvider.ps1 as Administrator
; ============================================================
;[Registry]
;Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{64D6E84B-4969-4B59-A11A-58C3D9FA0110}"; ValueType: string; ValueName: ""; ValueData: "FaceUnlock"; Flags: uninsdeletekey

[Run]
; DISABLED: regsvr32 auto-registration — see comment above
; Filename: "regsvr32.exe"; Parameters: "/s ""{app}\FaceUnlockCredentialProvider.dll"""; Flags: runhidden
;
; Create & start Windows Service using sc.exe
Filename: "{sys}\sc.exe"; Parameters: "create ""FaceUnlock Service"" binPath= ""{app}\FaceUnlock.Service.exe"" start= auto displayName= ""FaceUnlock Service"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start ""FaceUnlock Service"""; Flags: runhidden
; Launch Agent after install
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop and remove Windows Service
Filename: "{sys}\sc.exe"; Parameters: "stop ""FaceUnlock Service"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "delete ""FaceUnlock Service"""; Flags: runhidden
; Unregister COM DLL (safe no-op if not registered)
Filename: "regsvr32.exe"; Parameters: "/u /s ""{app}\FaceUnlockCredentialProvider.dll"""; Flags: runhidden
; Also clean up any manually enabled registry keys
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -Command ""Remove-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{{64D6E84B-4969-4B59-A11A-58C3D9FA0110}' -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -Path 'HKLM:\SOFTWARE\Classes\CLSID\{{64D6E84B-4969-4B59-A11A-58C3D9FA0110}' -Recurse -Force -ErrorAction SilentlyContinue"""; Flags: runhidden
