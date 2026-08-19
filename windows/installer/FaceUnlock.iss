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
Source: "bin\FaceUnlock.Agent.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\FaceUnlock.Service.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\FaceUnlock.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\FaceUnlockCredentialProvider.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\FaceUnlock-Recovery.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName} Agent"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\FaceUnlock Emergency Recovery"; Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\FaceUnlock-Recovery.ps1"""
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Register Credential Provider in Winlogon
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{{64D6E84B-4969-4B59-A11A-58C3D9FA0110}"; ValueType: string; ValueName: ""; ValueData: "FaceUnlock"; Flags: uninsdeletekey

[Run]
; Register COM InprocServer32 DLL
Filename: "regsvr32.exe"; Parameters: "/s ""{app}\FaceUnlockCredentialProvider.dll"""; Flags: runhidden
; Create & start Windows Service using sc.exe
Filename: "{sys}\sc.exe"; Parameters: "create ""FaceUnlock Service"" binPath= ""{app}\FaceUnlock.Service.exe"" start= auto displayName= ""FaceUnlock Service"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start ""FaceUnlock Service"""; Flags: runhidden
; Launch Agent after install
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop and remove Windows Service
Filename: "{sys}\sc.exe"; Parameters: "stop ""FaceUnlock Service"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "delete ""FaceUnlock Service"""; Flags: runhidden
; Unregister COM DLL
Filename: "regsvr32.exe"; Parameters: "/u /s ""{app}\FaceUnlockCredentialProvider.dll"""; Flags: runhidden
