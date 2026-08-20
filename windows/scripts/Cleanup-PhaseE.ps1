<#
.SYNOPSIS
    One-time migration cleanup for the removed FaceUnlock Windows security stack.
.DESCRIPTION
    MIGRATION CLEANUP ONLY. This is not a runtime feature and must never enable a
    Credential Provider or authentication package. It removes only the historical
    FaceUnlock CLSID and FaceUnlockAuthPackage entries, preserves all Windows/default
    authentication packages (including msv1_0), removes obsolete installed files,
    and never reboots automatically. A loaded legacy DLL is scheduled for deletion
    at the next reboot only after its registry registration has been removed.
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Force,
    [string]$InstallDir = $PSScriptRoot,
    [string]$LsaKeyPath = 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa',
    [string]$CredentialProviderKey = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{64D6E84B-4969-4B59-A11A-58C3D9FA0110}',
    [string]$ComClassKey = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\{64D6E84B-4969-4B59-A11A-58C3D9FA0110}',
    [string]$SystemDirectory = (Join-Path $env:SystemRoot 'System32'),
    [string]$DataDirectory = (Join-Path $env:ProgramData 'FaceUnlock'),
    [switch]$TestMode
)

$ErrorActionPreference = 'Stop'
$legacyPackageName = 'FaceUnlockAuthPackage'
$scheduledDeletes = [System.Collections.Generic.List[string]]::new()

function Write-MigrationLog([string]$Message) {
    Write-Host "[LEGACY MIGRATION] $Message"
}

function Test-IsLegacyPackage([object]$Value) {
    if ($null -eq $Value) { return $false }
    $name = [IO.Path]::GetFileNameWithoutExtension(([string]$Value).Trim())
    return [string]::Equals($name, $legacyPackageName, [StringComparison]::OrdinalIgnoreCase)
}

function Get-PackageList([string]$Name) {
    $value = (Get-ItemProperty -LiteralPath $LsaKeyPath -Name $Name -ErrorAction SilentlyContinue).$Name
    if ($null -eq $value) { return @() }
    return @($value)
}

function Set-PackageList([string]$Name, [AllowEmptyCollection()][string[]]$Values) {
    Set-ItemProperty -LiteralPath $LsaKeyPath -Name $Name -Value $Values -ErrorAction Stop
}

function Remove-ExactRegistryKey([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-MigrationLog "$Label absent; no action needed"
        return
    }
    if ($Force -or $PSCmdlet.ShouldProcess($Path, "Remove legacy $Label")) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
        Write-MigrationLog "$Label removed"
    }
}

function Add-DeleteOnReboot([string]$Path) {
    if ($TestMode) { throw "Test cleanup could not remove file: $Path" }
    if (-not ('FaceUnlockLegacyCleanup.NativeMethods' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace FaceUnlockLegacyCleanup {
    public static class NativeMethods {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool MoveFileEx(string existingName, string newName, int flags);
    }
}
'@
    }
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not [FaceUnlockLegacyCleanup.NativeMethods]::MoveFileEx($fullPath, $null, 4)) {
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "Failed to schedule legacy file deletion (Win32=$errorCode): $fullPath"
    }
    $scheduledDeletes.Add($fullPath)
    Write-MigrationLog "scheduled locked file for deletion on next reboot: $fullPath"
}

function Remove-LegacyFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    if (-not ($Force -or $PSCmdlet.ShouldProcess($Path, 'Remove obsolete FaceUnlock file'))) { return }
    try {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
        Write-MigrationLog "removed obsolete file: $Path"
    }
    catch {
        Write-MigrationLog "file is locked; scheduling safe delete: $Path"
        Add-DeleteOnReboot $Path
    }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $TestMode -and -not $isAdmin) {
    throw 'Legacy migration cleanup requires Administrator privileges.'
}
if (-not (Test-Path -LiteralPath $LsaKeyPath)) {
    throw "LSA registry key not found: $LsaKeyPath"
}

$authPackages = @(Get-PackageList 'Authentication Packages')
$securityPackages = @(Get-PackageList 'Security Packages')
$legacyLsaFound = ($authPackages | Where-Object { Test-IsLegacyPackage $_ }).Count -gt 0 -or
                  ($securityPackages | Where-Object { Test-IsLegacyPackage $_ }).Count -gt 0

if ($legacyLsaFound -and -not $TestMode) {
    $backupDir = Join-Path $DataDirectory 'backups'
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    $backupFile = Join-Path $backupDir "Lsa_Backup_Legacy_$(Get-Date -Format 'yyyyMMdd_HHmmss').reg"
    & reg.exe export 'HKLM\SYSTEM\CurrentControlSet\Control\Lsa' $backupFile /y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not back up the LSA registry (exit=$LASTEXITCODE)" }
    Write-MigrationLog "LSA backup created: $backupFile"
}

$newAuthPackages = @($authPackages | Where-Object { -not (Test-IsLegacyPackage $_) })
if (-not ($newAuthPackages -contains 'msv1_0')) {
    $newAuthPackages = @('msv1_0') + $newAuthPackages
}
if ($newAuthPackages.Count -ne $authPackages.Count -or -not ($authPackages -contains 'msv1_0')) {
    if ($Force -or $PSCmdlet.ShouldProcess('LSA Authentication Packages', "Remove $legacyPackageName and preserve msv1_0")) {
        Set-PackageList 'Authentication Packages' $newAuthPackages
        Write-MigrationLog 'Authentication Packages cleaned; msv1_0 preserved'
    }
}

$newSecurityPackages = @($securityPackages | Where-Object { -not (Test-IsLegacyPackage $_) })
if ($newSecurityPackages.Count -ne $securityPackages.Count) {
    if ($Force -or $PSCmdlet.ShouldProcess('LSA Security Packages', "Remove $legacyPackageName")) {
        Set-PackageList 'Security Packages' $newSecurityPackages
        Write-MigrationLog 'Security Packages cleaned'
    }
}

$legacyCpDll = Join-Path $InstallDir 'FaceUnlockCredentialProvider.dll'
if (-not $TestMode -and (Test-Path -LiteralPath $legacyCpDll -PathType Leaf)) {
    $unregister = Start-Process -FilePath "$env:SystemRoot\System32\regsvr32.exe" `
        -ArgumentList @('/u','/s',('"' + $legacyCpDll + '"')) -PassThru -WindowStyle Hidden
    if (-not $unregister.WaitForExit(10000)) {
        $unregister.Kill()
        $unregister.WaitForExit()
        Write-MigrationLog 'legacy COM unregister timed out; continuing with exact registry removal'
    } else {
        Write-MigrationLog "legacy COM unregister attempted (exit=$($unregister.ExitCode))"
    }
}
Remove-ExactRegistryKey $CredentialProviderKey 'Credential Provider CLSID'
Remove-ExactRegistryKey $ComClassKey 'COM CLSID'

$legacyInstallFiles = @(
    'FaceUnlockCredentialProvider.dll',
    'FaceUnlockAuthPackage.dll',
    'Enable-CredentialProvider.ps1',
    'Disable-CredentialProvider.ps1',
    'Enable-AuthPackage.ps1',
    'Disable-AuthPackage.ps1',
    'Check-AuthPackage.ps1',
    'FaceUnlock-Recovery.ps1'
)
foreach ($name in $legacyInstallFiles) { Remove-LegacyFile (Join-Path $InstallDir $name) }
Remove-LegacyFile (Join-Path $SystemDirectory 'FaceUnlockAuthPackage.dll')
Remove-LegacyFile (Join-Path $DataDirectory 'lsa_secret.dpapi')

$authAfter = @(Get-PackageList 'Authentication Packages')
$securityAfter = @(Get-PackageList 'Security Packages')
if (($authAfter | Where-Object { Test-IsLegacyPackage $_ }).Count -gt 0 -or
    ($securityAfter | Where-Object { Test-IsLegacyPackage $_ }).Count -gt 0) {
    throw 'Legacy FaceUnlock authentication package registration remains after cleanup.'
}
if (-not ($authAfter -contains 'msv1_0')) {
    throw 'Default Windows authentication package msv1_0 is missing after cleanup.'
}
if ((Test-Path -LiteralPath $CredentialProviderKey) -or (Test-Path -LiteralPath $ComClassKey)) {
    throw 'Legacy FaceUnlock Credential Provider registry registration remains after cleanup.'
}

Write-MigrationLog "PASS; default authentication stack preserved; scheduled_deletes=$($scheduledDeletes.Count); reboot_not_requested"
