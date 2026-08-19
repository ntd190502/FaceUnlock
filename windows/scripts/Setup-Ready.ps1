<#
Installer orchestration for Phase F/F.1. This script is intentionally limited to
the post-logon Shell Gate and never touches LSA, passwords, PIN, or AutoLogon.
#>
[CmdletBinding()]
param(
    [ValidateSet('install','uninstall')][string]$Mode = 'install',
    [string]$InstallDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$serviceName = 'FaceUnlock Service'
$dataDir = Join-Path $env:ProgramData 'FaceUnlock'
$logDir = Join-Path $dataDir 'logs'
$logFile = Join-Path $logDir 'installer.log'
$shellExe = Join-Path $InstallDir 'FaceUnlockShell.exe'
$recovery = Join-Path $InstallDir 'FaceUnlock-Shell-Recovery.ps1'
$enable = Join-Path $InstallDir 'Enable-ShellGate.ps1'
$disable = Join-Path $InstallDir 'Disable-ShellGate.ps1'
$configPath = Join-Path $dataDir 'config.json'
$winlogon = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon'

New-Item -ItemType Directory -Path $logDir -Force | Out-Null
function Log([string]$Message) { Add-Content -Path $logFile -Value "[$((Get-Date).ToUniversalTime().ToString('o'))] $Message" }
function IsPaired {
    if (-not (Test-Path $configPath)) { return $false }
    try { $c = Get-Content -Raw $configPath | ConvertFrom-Json; return [bool]($c.DeviceId -and $c.DevicePublicKeyPem -and $c.PcToken) }
    catch { return $false }
}
function ShellGateEnabled {
    try { return ((Get-ItemProperty -Path $winlogon -Name Shell -ErrorAction SilentlyContinue).Shell -match 'FaceUnlockShell\.exe') }
    catch { return $false }
}
function PhaseEActive {
    try { return ((Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name 'Authentication Packages' -ErrorAction SilentlyContinue).'Authentication Packages' -match 'FaceUnlock') }
    catch { return $false }
}
function RestoreExplorer {
    & $disable -Force
    $now = (Get-ItemProperty -Path $winlogon -Name Shell -ErrorAction SilentlyContinue).Shell
    if ($now -and $now -notmatch '^explorer\.exe$') { throw "Windows Shell restore verification failed: $now" }
}
function EnsureService {
    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        & "$env:SystemRoot\System32\sc.exe" create $serviceName "binPath= `"$InstallDir\FaceUnlock.Service.exe`"" "start= auto" "displayName= FaceUnlock Service" | Out-Null
    } else {
        & "$env:SystemRoot\System32\sc.exe" config $serviceName "binPath= `"$InstallDir\FaceUnlock.Service.exe`"" "start= auto" | Out-Null
    }
    Start-Service -Name $serviceName -ErrorAction SilentlyContinue
    $svc = Get-Service -Name $serviceName -ErrorAction Stop
    if ($svc.Status -ne 'Running') { throw "Service is not running (state=$($svc.Status))" }
}

if ($Mode -eq 'uninstall') {
    Log 'uninstall requested: restoring explorer before removing binaries'
    RestoreExplorer
    Log 'uninstall shell restore PASS'
    exit 0
}

$wasEnabled = ShellGateEnabled
$paired = IsPaired
$upgrade = Test-Path $configPath
Log "install mode=$(if($upgrade){'upgrade/repair'}else{'fresh'}) paired=$paired shell_enabled_before=$wasEnabled"

try {
    if (-not (Test-Path $shellExe) -or -not (Test-Path $recovery) -or -not (Test-Path $enable) -or -not (Test-Path $disable)) {
        throw 'Required Shell Gate binary or recovery script is missing'
    }
    EnsureService
    Log 'service automatic/running PASS'

    # A deliberate user-disabled upgrade stays disabled. Only a paired fresh
    # install is auto-enabled; paired enabled upgrades retain the enabled state.
    if (-not $paired) {
        if ($wasEnabled) { RestoreExplorer }
        Log 'unpaired: Shell Gate remains disabled; Agent must complete pairing'
        exit 0
    }
    if (PhaseEActive) { throw 'Deprecated Phase E LSA package is active; Shell Gate will not be enabled' }

    if ($wasEnabled -or -not $upgrade) {
        & $enable -Force -CustomShellPath $shellExe
        if (-not (ShellGateEnabled)) { throw 'Shell Gate registry verification failed' }
        Log "shell gate $(if($wasEnabled){'preserved'}else{'enabled'}) PASS"
    } else {
        Log 'upgrade preserved user-disabled Shell Gate state'
    }
    Log 'final state PASS'
}
catch {
    Log "rollback: $($_.Exception.Message)"
    try { RestoreExplorer; Log 'rollback explorer restore PASS' } catch { Log "rollback explorer restore FAILED: $($_.Exception.Message)" }
    exit 1
}
