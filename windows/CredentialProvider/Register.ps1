#Requires -RunAsAdministrator
param(
    [string]$DllPath
)

$ErrorActionPreference = 'Stop'

if (-not $DllPath) {
    $possiblePaths = @(
        (Join-Path $PSScriptRoot "build\Release\FaceUnlockCredentialProvider.dll"),
        (Join-Path $PSScriptRoot "build\FaceUnlockCredentialProvider.dll"),
        (Join-Path $PSScriptRoot "bin\Release\FaceUnlockCredentialProvider.dll"),
        (Join-Path (Get-Location) "FaceUnlockCredentialProvider.dll")
    )
    foreach ($p in $possiblePaths) {
        if (Test-Path $p) {
            $DllPath = (Resolve-Path $p).Path
            break
        }
    }
}

if (-not $DllPath -or -not (Test-Path $DllPath)) {
    throw "ERROR: FaceUnlockCredentialProvider.dll not found. Please provide -DllPath <path_to_dll>."
}

$clsid = '{64D6E84B-4969-4B59-A11A-58C3D9FA0110}'

# 1. Register COM InprocServer32
$comKey = "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid\InprocServer32"
New-Item -Path $comKey -Force | Out-Null
Set-ItemProperty -Path $comKey -Name '(default)' -Value $DllPath
Set-ItemProperty -Path $comKey -Name 'ThreadingModel' -Value 'Apartment'

# 2. Register Windows Credential Provider
$cpKey = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid"
New-Item -Path $cpKey -Force | Out-Null
Set-ItemProperty -Path $cpKey -Name '(default)' -Value 'FaceUnlock Credential Provider'

Write-Host "SUCCESS: Registered FaceUnlock Credential Provider ($clsid)" -ForegroundColor Green
Write-Host "DLL Location: $DllPath" -ForegroundColor Cyan
Write-Host "FaceUnlock credential tile is enabled for Logon and Unlock Workstation." -ForegroundColor Yellow
Write-Host "Note: Standard Password and PIN providers remain untouched." -ForegroundColor Gray
