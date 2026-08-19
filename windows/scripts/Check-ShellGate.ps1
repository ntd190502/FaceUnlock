<#
.SYNOPSIS
    Check-ShellGate.ps1 — Diagnostics tool for FaceUnlock Shell Gate status.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Continue"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "FaceUnlock Shell Gate Diagnostics" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# 1. Shell Executable
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$candidates = @(
    "$scriptDir\FaceUnlockShell.exe",
    "$scriptDir\bin\FaceUnlockShell.exe",
    "$env:ProgramFiles\FaceUnlock\FaceUnlockShell.exe",
    "$scriptDir\..\FaceUnlock.Shell\bin\Release\net8.0-windows10.0.26100.0\FaceUnlockShell.exe",
    "$scriptDir\..\FaceUnlock.Shell\bin\Debug\net8.0-windows10.0.26100.0\FaceUnlockShell.exe"
)
$shellFound = $false
$shellPathFound = ""
foreach ($c in $candidates) {
    if (Test-Path $c) {
        $shellFound = $true
        $shellPathFound = (Resolve-Path $c).Path
        break
    }
}
$exeStatus = if ($shellFound) { "PRESENT ($shellPathFound)" } else { "MISSING" }
$exeColor  = if ($shellFound) { "Green" } else { "Red" }
Write-Host "Shell executable: $exeStatus" -ForegroundColor $exeColor

# 2. Service Status
$service = Get-Service -Name "FaceUnlock Service" -ErrorAction SilentlyContinue
$serviceStatus = "STOPPED"
if ($service) {
    $serviceStatus = $service.Status.ToString().ToUpper()
}
$svcColor = if ($serviceStatus -eq "RUNNING") { "Green" } else { "Yellow" }
Write-Host "Service:          $serviceStatus" -ForegroundColor $svcColor

# 3. Pairing Status
$cfgPath = "$env:ProgramData\FaceUnlock\config.json"
function Get-PairingState {
    if (-not (Test-Path $cfgPath)) { return @{ Paired=$false; Reason='config_missing' } }
    try { $cfg = Get-Content -Raw $cfgPath | ConvertFrom-Json } catch { return @{ Paired=$false; Reason='config_invalid' } }
    $id=$cfg.DeviceId; if(-not $id){$id=$cfg.device_id}; $pub=$cfg.DevicePublicKeyPem; if(-not $pub){$pub=$cfg.device_public_key_pem}; $token=$cfg.PcToken; if(-not $token){$token=$cfg.pc_token}
    if(-not $id){return @{ Paired=$false; Reason='device_id_missing' }}; if(-not $pub){return @{ Paired=$false; Reason='device_public_key_missing' }}; if(-not $token){return @{ Paired=$false; Reason='pc_token_missing' }}
    return @{ Paired=$true; Reason='paired' }
}
$pairingState=Get-PairingState
$isPaired = $pairingState.Paired
$pairStatus = if ($isPaired) { "PAIRED" } else { "NOT PAIRED" }
$pairColor  = if ($isPaired) { "Green" } else { "Yellow" }
Write-Host "Pairing:          $pairStatus" -ForegroundColor $pairColor
Write-Host "Pairing reason:   $($pairingState.Reason)" -ForegroundColor $pairColor

# 4. Current Windows Shell
$hkcuWinlogon = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon"
$currentShell = "explorer.exe (Default System Shell)"
$isShellGate = $false
if (Test-Path $hkcuWinlogon) {
    $prop = (Get-ItemProperty -Path $hkcuWinlogon -Name "Shell" -ErrorAction SilentlyContinue).Shell
    if ($prop) {
        $currentShell = $prop
        if ($prop -match "FaceUnlockShell") {
            $isShellGate = $true
        }
    }
}
$shellGateStatus = if ($isShellGate) { "ENABLED" } else { "DISABLED" }
$shellGateColor  = if ($isShellGate) { "Green" } else { "Yellow" }
Write-Host "Current Shell:    $currentShell" -ForegroundColor Cyan
Write-Host "Shell Gate:       $shellGateStatus" -ForegroundColor $shellGateColor

# 5. Backup
$backupDir = "$env:ProgramData\FaceUnlock\backups"
$backupFile = "$backupDir\Shell_Backup_Original.json"
$hasBackup = Test-Path $backupFile
$backupStatus = if ($hasBackup) { "PRESENT" } else { "MISSING" }
$backupColor  = if ($hasBackup) { "Green" } else { "Yellow" }
Write-Host "Backup:           $backupStatus" -ForegroundColor $backupColor

# 6. Recovery script
$recoveryScript = "$scriptDir\FaceUnlock-Shell-Recovery.ps1"
$hasRecovery = Test-Path $recoveryScript
$recStatus = if ($hasRecovery) { "PRESENT" } else { "MISSING" }
$recColor  = if ($hasRecovery) { "Green" } else { "Red" }
Write-Host "Recovery script:  $recStatus" -ForegroundColor $recColor

# 7. LSA Phase E check
$lsaKeyPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Lsa"
$hasPhaseE = $false
if (Test-Path $lsaKeyPath) {
    $authPackages = (Get-ItemProperty -Path $lsaKeyPath -Name "Authentication Packages" -ErrorAction SilentlyContinue)."Authentication Packages"
    if ($authPackages -match "FaceUnlock") { $hasPhaseE = $true }
}
$phaseEStatus = if ($hasPhaseE) { "REGISTERED WARNING (Run Cleanup-PhaseE.ps1)" } else { "DISABLED" }
$phaseEColor  = if ($hasPhaseE) { "Red" } else { "Green" }
Write-Host "LSA Phase E:      $phaseEStatus" -ForegroundColor $phaseEColor

# 8. Security Level
Write-Host "Security level:   STANDARD POST-LOGON SHELL GATE" -ForegroundColor Cyan
Write-Host "Notice:           User with administrative/recovery access may bypass the post-logon shell gate." -ForegroundColor Gray
Write-Host "============================================================" -ForegroundColor Cyan
