#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Enable-CredentialProvider.ps1
    Opt-in activation of FaceUnlock Credential Provider.

.WARNING
    DO NOT RUN until CP_SAFE_FOR_LOGONUI_TEST = YES confirmed by harness.
    This modifies Windows Winlogon credential providers registry.
    If the DLL has bugs, Windows lock screen may become unusable.

    Use Disable-CredentialProvider.ps1 or Recovery.ps1 to undo.
#>

$ErrorActionPreference = 'Stop'

$clsid   = '{64D6E84B-4969-4B59-A11A-58C3D9FA0110}'
$dllPath = Join-Path $PSScriptRoot 'FaceUnlockCredentialProvider.dll'

Write-Host '' -ForegroundColor Yellow
Write-Host '==========================================================' -ForegroundColor Yellow
Write-Host '  FaceUnlock Credential Provider — ENABLE (DEBUG OPT-IN)' -ForegroundColor Yellow
Write-Host '==========================================================' -ForegroundColor Yellow
Write-Host ''
Write-Host 'WARNING: Only enable after harness CP_SAFE_FOR_LOGONUI_TEST=YES' -ForegroundColor Red
Write-Host ''

if (-not (Test-Path $dllPath)) {
    Write-Host "[ERROR] DLL not found: $dllPath" -ForegroundColor Red
    exit 1
}

$confirm = Read-Host 'Type YES to confirm enabling the Credential Provider'
if ($confirm -ne 'YES') {
    Write-Host 'Aborted.' -ForegroundColor Yellow
    exit 0
}

# Register COM InprocServer32
$comKey = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\$clsid\InprocServer32"
New-Item -Path $comKey -Force | Out-Null
Set-ItemProperty -Path $comKey -Name '(Default)'      -Value $dllPath
Set-ItemProperty -Path $comKey -Name 'ThreadingModel' -Value 'Apartment'
Write-Host "[+] COM InprocServer32 registered: $dllPath" -ForegroundColor Green

# Register in Winlogon Credential Providers
$cpKey = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid"
New-Item -Path $cpKey -Force | Out-Null
Set-ItemProperty -Path $cpKey -Name '(Default)' -Value 'FaceUnlock'
Write-Host '[+] Credential Provider registered in Winlogon' -ForegroundColor Green

Write-Host ''
Write-Host 'FaceUnlock Credential Provider ENABLED.' -ForegroundColor Green
Write-Host 'Lock screen will show FaceUnlock tile on next lock.' -ForegroundColor Cyan
Write-Host ''
Write-Host 'To disable: run Disable-CredentialProvider.ps1' -ForegroundColor Yellow
Write-Host 'Emergency : run Recovery.ps1 (also in Start Menu)' -ForegroundColor Yellow
