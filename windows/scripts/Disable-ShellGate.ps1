<#
.SYNOPSIS
    Disable-ShellGate.ps1 — Disable FaceUnlock Shell Gate and restore explorer.exe.
.DESCRIPTION
    Restores the Windows User Shell to default explorer.exe.
#>

[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Disable FaceUnlock Shell Gate" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$hkcuWinlogon = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon"

if (Test-Path $hkcuWinlogon) {
    $prop = (Get-ItemProperty -Path $hkcuWinlogon -Name "Shell" -ErrorAction SilentlyContinue).Shell
    if ($prop) {
        Write-Host "Removing HKCU Shell override ('$prop')..." -ForegroundColor Yellow
        Remove-ItemProperty -Path $hkcuWinlogon -Name "Shell" -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host "  FaceUnlock Shell Gate is now DISABLED" -ForegroundColor Green
Write-Host "  Windows User Shell restored to default explorer.exe." -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
