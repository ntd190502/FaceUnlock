#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Disable-CredentialProvider.ps1
    Safely remove FaceUnlock from Windows Credential Providers.
    Does NOT touch Password, PIN, or any other provider.
#>

$ErrorActionPreference = 'SilentlyContinue'
$clsid = '{64D6E84B-4969-4B59-A11A-58C3D9FA0110}'

Write-Host '==========================================================' -ForegroundColor Cyan
Write-Host '  FaceUnlock Credential Provider — DISABLE' -ForegroundColor Cyan
Write-Host '==========================================================' -ForegroundColor Cyan

# Remove Winlogon registration
$cpKey = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid"
if (Test-Path $cpKey) {
    Remove-Item -Path $cpKey -Recurse -Force
    Write-Host '[+] Removed from Winlogon Credential Providers' -ForegroundColor Green
} else {
    Write-Host '[-] Winlogon key not found (already removed)' -ForegroundColor Yellow
}

# Remove COM registration
$comKey = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\$clsid"
if (Test-Path $comKey) {
    Remove-Item -Path $comKey -Recurse -Force
    Write-Host '[+] Removed COM InprocServer32 registration' -ForegroundColor Green
} else {
    Write-Host '[-] COM CLSID key not found (already removed)' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'FaceUnlock Credential Provider DISABLED.' -ForegroundColor Green
Write-Host 'Password / PIN providers are unaffected.' -ForegroundColor Cyan
