param([string]$DllPath)
$ErrorActionPreference='Stop'
if(-not $DllPath){$DllPath=(Resolve-Path '.\build\Release\FaceUnlockCredentialProvider.dll').Path}
$clsid='{64D6E84B-4969-4B59-A11A-58C3D9FA0110}'
New-Item -Path "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid\InprocServer32" -Force | Out-Null
Set-ItemProperty -Path "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid\InprocServer32" -Name '(default)' -Value $DllPath
Set-ItemProperty -Path "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid\InprocServer32" -Name 'ThreadingModel' -Value 'Apartment'
New-Item -Path "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid" -Force | Out-Null
Set-ItemProperty -Path "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid" -Name '(default)' -Value 'FaceUnlock (scaffold)'
Write-Warning 'This scaffold enumerates zero tiles by design. Read README.md before extending it. Built-in providers were not changed.'
