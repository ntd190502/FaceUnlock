#Requires -RunAsAdministrator
$ErrorActionPreference = 'SilentlyContinue'

$clsid = '{64D6E84B-4969-4B59-A11A-58C3D9FA0110}'

Write-Host "=========================================" -ForegroundColor Yellow
Write-Host " FaceUnlock Emergency Recovery Script" -ForegroundColor Yellow
Write-Host "=========================================" -ForegroundColor Yellow

# 1. Unregister Credential Provider from Winlogon
$cpKey = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid"
if (Test-Path $cpKey) {
    Remove-Item -Path $cpKey -Recurse -Force
    Write-Host "[+] Removed Credential Provider registry key" -ForegroundColor Green
}

# 2. Unregister COM CLSID
$comKey = "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid"
if (Test-Path $comKey) {
    Remove-Item -Path $comKey -Recurse -Force
    Write-Host "[+] Removed COM InprocServer32 registration" -ForegroundColor Green
}

# 3. Stop FaceUnlock Service if running
$service = Get-Service -Name "FaceUnlock Service" -ErrorAction SilentlyContinue
if ($service -and $service.Status -eq 'Running') {
    Stop-Service -Name "FaceUnlock Service" -Force
    Write-Host "[+] Stopped FaceUnlock Service" -ForegroundColor Green
}

Write-Host "Recovery completed successfully." -ForegroundColor Green
Write-Host "Windows Sign-in options (PIN/Password) are unaffected and active." -ForegroundColor Cyan
