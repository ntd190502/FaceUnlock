<#
Installer orchestration for Phase F/F.1/F.2. This script is intentionally limited to
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
function Get-PairingState {
    if (-not (Test-Path $configPath)) { return [pscustomobject]@{ Paired=$false; Reason='config_missing' } }
    try { $c = Get-Content -Raw $configPath | ConvertFrom-Json } catch { return [pscustomobject]@{ Paired=$false; Reason='config_invalid' } }
    $deviceId = $c.DeviceId; if (-not $deviceId) { $deviceId = $c.device_id }
    $pub = $c.DevicePublicKeyPem; if (-not $pub) { $pub = $c.device_public_key_pem }
    $token = $c.PcToken; if (-not $token) { $token = $c.pc_token }; $secureToken = Join-Path $dataDir 'pctoken.dpapi'
    if (-not $deviceId) { return [pscustomobject]@{ Paired=$false; Reason='device_id_missing' } }
    if (-not $pub) { return [pscustomobject]@{ Paired=$false; Reason='device_public_key_missing' } }
    if (-not $token -and -not (Test-Path $secureToken)) { return [pscustomobject]@{ Paired=$false; Reason='pc_token_missing' } }
    return [pscustomobject]@{ Paired=$true; Reason=$(if($token){'paired_legacy_token'}else{'paired_secure_token'}) }
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
    Log "[SERVICE] exists=$([bool]$svc)"
    $exePath = Join-Path $InstallDir 'FaceUnlock.Service.exe'
    if (-not (Test-Path $exePath)) { throw "Service executable is missing: $exePath" }
    $expectedPath = '"' + $exePath + '"'
    if (-not $svc) {
        Log '[SERVICE] create requested via New-Service'
        try {
            New-Service -Name $serviceName -BinaryPathName $expectedPath -DisplayName 'FaceUnlock Service' -StartupType Automatic -ErrorAction Stop
            Log '[SERVICE] create PASS'
        }
        catch { Log "[SERVICE] create FAIL type=$($_.Exception.GetType().Name) message=$($_.Exception.Message)"; throw }
    } else {
        if ($svc.Status -eq 'Running') { Stop-Service -Name $serviceName -Force -ErrorAction Stop; Log '[SERVICE] stop PASS' }
        Log '[SERVICE] repair binPath requested'
    }
    if ($svc) {
        & "$env:SystemRoot\System32\sc.exe" config $serviceName "binPath= $expectedPath" 'start= auto' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Existing service config failed (exit=$LASTEXITCODE)" }
    }
    & "$env:SystemRoot\System32\sc.exe" failure $serviceName 'reset= 86400' 'actions= restart/2000/restart/5000/restart/10000' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Service recovery policy failed (exit=$LASTEXITCODE)" }
    Log '[SERVICE] crash recovery restart policy PASS'
    $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
    Log "[SERVICE] path=$($serviceInfo.PathName)"
    Log "[SERVICE] startup=$($serviceInfo.StartMode)"
    Start-Service -Name $serviceName -ErrorAction Stop
    Log '[SERVICE] start requested'
    for ($i=0; $i -lt 10; $i++) {
        Start-Sleep -Seconds 1
        $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq 'Running') {
            $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
            if ($serviceInfo.StartMode -ne 'Auto') { throw "Service startup mode is $($serviceInfo.StartMode), expected Auto" }
            Log '[SERVICE] running PASS'; Log '[SERVICE] health PASS (service running)'; return
        }
    }
    throw 'Service health failed: service did not reach Running within 10 seconds'
}

if ($Mode -eq 'uninstall') {
    Log 'uninstall requested: restoring explorer before removing binaries'
    RestoreExplorer
    Log 'uninstall shell restore PASS'
    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -eq 'Running') { Stop-Service -Name $serviceName -Force -ErrorAction Stop }
        & "$env:SystemRoot\System32\sc.exe" delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Service delete failed (exit=$LASTEXITCODE)" }
        Log 'uninstall service delete PASS'
    } else { Log 'uninstall service missing: continuing safely' }
    exit 0
}

$wasEnabled = ShellGateEnabled
$pairingState = Get-PairingState
$paired = $pairingState.Paired
$serviceBefore = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$metadataBefore = Test-Path $configPath
$installMode = if ($serviceBefore -and $metadataBefore) { 'upgrade' } elseif ($metadataBefore) { 'repair' } else { 'fresh' }
Log "install mode=$installMode paired=$paired shell_enabled_before=$wasEnabled"
Log "[PAIRING] state=$(if($paired){'paired'}else{'unpaired'}) reason=$($pairingState.Reason)"

try {
    if (-not (Test-Path $shellExe) -or -not (Test-Path $recovery) -or -not (Test-Path $enable) -or -not (Test-Path $disable)) {
        throw 'Required Shell Gate binary or recovery script is missing'
    }
    EnsureService
    Log 'service automatic/running PASS'

    # A deliberate user-disabled upgrade stays disabled. Only a paired fresh
    # install is auto-enabled; paired enabled upgrades retain the enabled state.
    if (-not $paired) {
        Log "[SHELL] before=$(if($wasEnabled){'FaceUnlock'}else{'Explorer'})"
        if ($wasEnabled) { RestoreExplorer; Log '[SHELL] unpaired policy forced disable PASS' } else { Log '[SHELL] unpaired policy already disabled' }
        if (ShellGateEnabled) { throw 'Unpaired policy verification failed: FaceUnlock Shell is still active' }
        Log '[SHELL] after=Explorer'
        Log '[FINAL] paired=False service=Running shell=Disabled desktop=Explorer ready=False'
        exit 0
    }
    if (PhaseEActive) { throw 'Deprecated Phase E LSA package is active; Shell Gate will not be enabled' }

    if ($wasEnabled -or $installMode -eq 'fresh') {
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
