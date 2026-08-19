<#
.SYNOPSIS
    Enable-ShellGate.ps1 — Manually enable FaceUnlock Shell Gate.
.DESCRIPTION
    Configures Windows user shell to launch FaceUnlockShell.exe --shell before explorer.exe.
    Requires manual confirmation, validates files, backups current settings, and supports -DryRun.
#>

[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$Force,
    [string]$CustomShellPath
)

$ErrorActionPreference = "Stop"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  FaceUnlock Shell Gate Registration" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# 1. Locate FaceUnlockShell.exe
$shellExe = $CustomShellPath
if (-not $shellExe) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
    $candidates = @(
        "$scriptDir\FaceUnlockShell.exe",
        "$scriptDir\bin\FaceUnlockShell.exe",
        "$env:ProgramFiles\FaceUnlock\FaceUnlockShell.exe",
        "$scriptDir\..\FaceUnlock.Shell\bin\Release\net8.0-windows10.0.26100.0\FaceUnlockShell.exe",
        "$scriptDir\..\FaceUnlock.Shell\bin\Debug\net8.0-windows10.0.26100.0\FaceUnlockShell.exe"
    )
    foreach ($cand in $candidates) {
        if (Test-Path $cand) {
            $shellExe = (Resolve-Path $cand).Path
            break
        }
    }
}

if (-not $shellExe -or -not (Test-Path $shellExe)) {
    Write-Error "FaceUnlockShell.exe not found. Please build the project or specify -CustomShellPath."
    exit 1
}

# 2. Get OS Edition & Target Registry Key
$os = Get-CimInstance Win32_OperatingSystem
$osCaption = $os.Caption
$hkcuWinlogon = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon"

# Target Shell string
$targetShellValue = "`"$shellExe`" --shell"

# 3. Read Current Shell
$currentShell = "explorer.exe"
if (Test-Path $hkcuWinlogon) {
    $prop = (Get-ItemProperty -Path $hkcuWinlogon -Name "Shell" -ErrorAction SilentlyContinue).Shell
    if ($prop) { $currentShell = $prop }
}

# Backup Path
$backupDir = "$env:ProgramData\FaceUnlock\backups"
$backupFile = "$backupDir\Shell_Backup_Original.json"

if ($DryRun) {
    Write-Host "`n[DRY RUN SUMMARY]" -ForegroundColor Yellow
    Write-Host "  Windows edition:     $osCaption"
    Write-Host "  Registration method: User Winlogon Shell (HKCU)"
    Write-Host "  Current shell:       $currentShell"
    Write-Host "  Target shell:        $targetShellValue"
    Write-Host "  FaceUnlockShell:     $shellExe"
    Write-Host "  Backup path:         $backupFile"
    Write-Host "  DryRun active:       NO changes will be made to registry." -ForegroundColor Green
    exit 0
}

# 4. User Confirmation
if (-not $Force) {
    Write-Host "`nConfiguration details:" -ForegroundColor Cyan
    Write-Host "  Target Shell: $targetShellValue"
    Write-Host "  Registry Key: $hkcuWinlogon\Shell"
    $confirm = Read-Host "`nDo you want to enable FaceUnlock Shell Gate? (Type 'YES' to confirm)"
    if ($confirm -ne "YES") {
        Write-Host "Operation cancelled by user." -ForegroundColor Yellow
        exit 0
    }
}

# 5. Backup current value if not already backed up
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
if (-not (Test-Path $backupFile)) {
    $backupData = @{
        Timestamp = (Get-Date -Format "o")
        OriginalShell = $currentShell
        TargetShell = $targetShellValue
        FaceUnlockExe = $shellExe
    }
    $backupData | ConvertTo-Json | Set-Content -Path $backupFile -Encoding utf8
    Write-Host "`nSaved original shell backup to: $backupFile" -ForegroundColor Green
}

# 6. Apply Registry Change
if (-not (Test-Path $hkcuWinlogon)) {
    New-Item -Path $hkcuWinlogon -Force | Out-Null
}

Set-ItemProperty -Path $hkcuWinlogon -Name "Shell" -Value $targetShellValue -Force
Write-Host "Registered Shell Gate in HKCU Winlogon." -ForegroundColor Green

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host "  FaceUnlock Shell Gate is now ENABLED" -ForegroundColor Green
Write-Host "  To test, sign out and sign back in." -ForegroundColor Green
Write-Host "  To disable at any time, run: Disable-ShellGate.ps1" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
