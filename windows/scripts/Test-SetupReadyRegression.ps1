# Real Windows SCM/IPC regression test. Restricted to ephemeral GitHub Actions runners.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$InstallDir)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This destructive SCM smoke test is restricted to an ephemeral GitHub Actions Windows runner.' }
$serviceName = 'FaceUnlock Service'
$serviceExe = [IO.Path]::GetFullPath((Join-Path $InstallDir 'FaceUnlock.Service.exe'))
$setup = Join-Path $InstallDir 'Setup-Ready.ps1'
$cleanupTest = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\Test-LegacyCleanup.ps1'
$dataDir = Join-Path $env:ProgramData 'FaceUnlock'
$dataBackup = Join-Path $env:RUNNER_TEMP ("FaceUnlock-ProgramData-Backup-" + [Guid]::NewGuid().ToString('N'))
$configPath = Join-Path $dataDir 'config.json'
$tokenPath = Join-Path $dataDir 'pctoken.dpapi'
$winlogon = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon'
$originalShell = (Get-ItemProperty -Path $winlogon -Name Shell -ErrorAction SilentlyContinue).Shell

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "[FAIL] $Message" }
    Write-Host "[PASS] $Message"
}
function Remove-TestService {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -eq 'Running') { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue }
    if ($service) {
        & "$env:SystemRoot\System32\sc.exe" delete $serviceName | Out-Null
        for ($i=0; $i -lt 20 -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
    }
}
function Test-PipePing {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'FaceUnlock.Auth.v1', [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(2000)
        $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 1024, $true); $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 1024, $true)
        try {
            $id = [Guid]::NewGuid().ToString('N')
            $writer.WriteLine((@{protocol_version=1;command='ping';request_id=$id;client_type='setup_regression'} | ConvertTo-Json -Compress))
            $response = $reader.ReadLine() | ConvertFrom-Json
            return ($response.status -ieq 'ok' -and $response.request_id -eq $id)
        } finally { $writer.Dispose(); $reader.Dispose() }
    } catch { return $false } finally { $pipe.Dispose() }
}
function Assert-ServiceHealthy {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    Assert ($null -ne $service) 'FaceUnlock Service exists'
    Assert ($service.Status -eq 'Running') 'FaceUnlock Service is Running'
    $info = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    Assert ($info.StartMode -eq 'Auto') 'FaceUnlock Service start mode is Auto'
    $actualPath = [Environment]::ExpandEnvironmentVariables($info.PathName).Trim().Trim('"')
    Assert ([string]::Equals($actualPath, $serviceExe, [StringComparison]::OrdinalIgnoreCase)) 'FaceUnlock Service path is correct'
    Assert (Test-PipePing) 'FaceUnlock.Auth.v1 IPC ping is healthy'
}

if (-not (Test-Path $setup) -or -not (Test-Path $serviceExe)) { throw 'Staged Setup-Ready.ps1 or FaceUnlock.Service.exe is missing.' }
$hadExistingData = Test-Path $dataDir

try {
    Remove-TestService
    if ($hadExistingData) { Move-Item -LiteralPath $dataDir -Destination $dataBackup }
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    Set-Content -LiteralPath $configPath -Value '{"DeviceId":"regression-device","DevicePublicKeyPem":"regression-public-key"}' -Encoding utf8
    Set-Content -LiteralPath $tokenPath -Value 'paired-secure-token-sentinel' -Encoding utf8
    $configHash = (Get-FileHash $configPath -Algorithm SHA256).Hash
    $tokenHash = (Get-FileHash $tokenPath -Algorithm SHA256).Hash
    if (-not (Test-Path $winlogon)) { New-Item -Path $winlogon -Force | Out-Null }
    Set-ItemProperty -Path $winlogon -Name Shell -Value "`"$(Join-Path $InstallDir 'FaceUnlockShell.exe')`" --shell" -Force

    & $setup -Mode install -InstallDir $InstallDir
    if ($LASTEXITCODE -ne 0) {
        Get-Content (Join-Path $dataDir 'logs\installer.log') -Tail 100 -ErrorAction SilentlyContinue
        throw "Setup missing-service repair failed (exit=$LASTEXITCODE)"
    }
    Assert-ServiceHealthy
    Assert ((Get-ItemProperty -Path $winlogon -Name Shell).Shell -match 'FaceUnlockShell\.exe') 'Shell Gate preserved after missing-service repair'
    Assert ((Get-FileHash $configPath -Algorithm SHA256).Hash -eq $configHash) 'Pairing config preserved'
    Assert ((Get-FileHash $tokenPath -Algorithm SHA256).Hash -eq $tokenHash) 'paired_secure_token preserved'

    & $cleanupTest
    if ($LASTEXITCODE -ne 0) { throw 'Legacy migration regression failed while service existed.' }
    Assert-ServiceHealthy

    Stop-Service -Name $serviceName -Force
    & "$env:SystemRoot\System32\sc.exe" config $serviceName 'binPath= "C:\FaceUnlock-Wrong-Path.exe"' 'start= demand' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create wrong-path regression state.' }
    & $setup -Mode install -InstallDir $InstallDir
    if ($LASTEXITCODE -ne 0) {
        Get-Content (Join-Path $dataDir 'logs\installer.log') -Tail 100 -ErrorAction SilentlyContinue
        throw "Setup existing-service repair failed (exit=$LASTEXITCODE)"
    }
    Assert-ServiceHealthy
    Assert ((Get-ItemProperty -Path $winlogon -Name Shell).Shell -match 'FaceUnlockShell\.exe') 'Shell Gate preserved on idempotent second install'
    Assert ((Get-FileHash $configPath -Algorithm SHA256).Hash -eq $configHash -and (Get-FileHash $tokenPath -Algorithm SHA256).Hash -eq $tokenHash) 'Pairing preserved on idempotent second install'
    Write-Host 'Setup service recovery regression PASS'
}
finally {
    Remove-TestService
    if ($null -eq $originalShell) { Remove-ItemProperty -Path $winlogon -Name Shell -ErrorAction SilentlyContinue }
    else { Set-ItemProperty -Path $winlogon -Name Shell -Value $originalShell -Force }
    Remove-Item -LiteralPath $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    if ($hadExistingData -and (Test-Path $dataBackup)) { Move-Item -LiteralPath $dataBackup -Destination $dataDir }
}
