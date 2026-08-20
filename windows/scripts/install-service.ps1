#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$possibleExes = @(
    (Join-Path $PSScriptRoot "..\FaceUnlock.Service\bin\Release\net8.0-windows10.0.26100.0\FaceUnlock.Service.exe"),
    (Join-Path $PSScriptRoot "..\FaceUnlock.Service\bin\Release\net8.0-windows10.0.26100.0\win-x64\FaceUnlock.Service.exe"),
    (Join-Path $PSScriptRoot "..\dist\FaceUnlock-Service\FaceUnlock.Service.exe")
)

$exe = $null
foreach ($p in $possibleExes) {
    if (Test-Path $p) {
        $exe = (Resolve-Path $p).Path
        break
    }
}

if (-not $exe) {
    throw "ERROR: FaceUnlock.Service.exe was not found. Please build the Service first."
}

$serviceName = 'FaceUnlock Service'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $service) {
    New-Service -Name $serviceName -DisplayName $serviceName -BinaryPathName "`"$exe`"" -StartupType Automatic | Out-Null
} elseif ($service.Status -eq 'Running') {
    Stop-Service -Name $serviceName -Force
}

sc.exe config "FaceUnlock Service" binPath= "`"$exe`"" start= auto
if ($LASTEXITCODE -ne 0) { throw "Could not repair FaceUnlock Service configuration (exit=$LASTEXITCODE)" }
sc.exe description "FaceUnlock Service" "FaceUnlock Phase F Shell Gate and phone approval broker"
sc.exe failure "FaceUnlock Service" reset= 86400 actions= restart/5000/restart/10000/restart/60000
sc.exe start "FaceUnlock Service"

Write-Host "FaceUnlock Service successfully installed and started (Automatic with auto-recovery)." -ForegroundColor Green
Write-Host "Binary: $exe" -ForegroundColor Cyan
