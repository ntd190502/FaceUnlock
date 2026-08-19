#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$clsid = '{64D6E84B-4969-4B59-A11A-58C3D9FA0110}'

# 1. Remove Windows Credential Provider registration
$cpKey = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid"
if (Test-Path $cpKey) {
    Remove-Item -Path $cpKey -Recurse -Force
    Write-Host "Removed Credential Provider registry entry: $cpKey" -ForegroundColor Cyan
}

# 2. Remove COM InprocServer32 registration
$comKey = "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid"
if (Test-Path $comKey) {
    Remove-Item -Path $comKey -Recurse -Force
    Write-Host "Removed COM CLSID registry entry: $comKey" -ForegroundColor Cyan
}

Write-Host "SUCCESS: Unregistered FaceUnlock Credential Provider ($clsid)." -ForegroundColor Green
Write-Host "Default Windows Sign-in options (Password, PIN) are fully restored and unaffected." -ForegroundColor Yellow
