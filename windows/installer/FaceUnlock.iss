; Script generated for Inno Setup 6
#define MyAppName "FaceUnlock"
#define MyAppVersion "1.1.0"
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
; Binaries
Source: "bin\FaceUnlock.Agent.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\FaceUnlock.Service.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\FaceUnlock.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\FaceUnlockCredentialProvider.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\FaceUnlock-Recovery.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName} Agent"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\FaceUnlock Emergency Recovery"; Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\FaceUnlock-Recovery.ps1"""
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Install & start Windows Service
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -Command ""New-Service -Name 'FaceUnlock Service' -BinaryPathName '\"\"{app}\FaceUnlock.Service.exe\"\"' -DisplayName 'FaceUnlock Service' -StartupType Automatic -ErrorAction SilentlyContinue; Start-Service -Name 'FaceUnlock Service' -ErrorAction SilentlyContinue"""; Flags: runhidden
; Register Credential Provider DLL
Filename: "regsvr32.exe"; Parameters: "/s ""{app}\FaceUnlockCredentialProvider.dll"""; Flags: runhidden
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -Command ""$clsid='{64D6E84B-4969-4B59-A11A-58C3D9FA0110}'; $cpKey='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\' + $clsid; New-Item -Path $cpKey -Force -ErrorAction SilentlyContinue | Out-Null; Set-ItemProperty -Path $cpKey -Name '(default)' -Value 'FaceUnlock' -Force -ErrorAction SilentlyContinue"""; Flags: runhidden
; Launch Agent after install
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Unregister Credential Provider
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -Command ""$clsid='{64D6E84B-4969-4B59-A11A-58C3D9FA0110}'; Remove-Item -Path ('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\' + $clsid) -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -Path ('HKCR:\CLSID\' + $clsid) -Recurse -Force -ErrorAction SilentlyContinue"""; Flags: runhidden
; Stop & remove Windows Service
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -Command ""Stop-Service -Name 'FaceUnlock Service' -Force -ErrorAction SilentlyContinue; sc.exe delete 'FaceUnlock Service'"""; Flags: runhidden
