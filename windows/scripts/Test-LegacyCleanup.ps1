$ErrorActionPreference = 'Stop'
$testId = 'FaceUnlockLegacyCleanup_' + [Guid]::NewGuid().ToString('N')
$registryRoot = "HKCU:\Software\$testId"
$lsaKey = Join-Path $registryRoot 'Lsa'
$cpKey = Join-Path $registryRoot 'CredentialProvider'
$comKey = Join-Path $registryRoot 'ComClass'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) $testId
$installDir = Join-Path $tempRoot 'install'
$systemDir = Join-Path $tempRoot 'system32'
$dataDir = Join-Path $tempRoot 'data'
$cleanup = Join-Path $PSScriptRoot 'Cleanup-PhaseE.ps1'

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Host "[PASS] $Message"
}

try {
    New-Item -ItemType Directory -Path $installDir,$systemDir,$dataDir -Force | Out-Null
    New-Item -Path $lsaKey,$cpKey,$comKey -Force | Out-Null
    New-ItemProperty -LiteralPath $lsaKey -Name 'Authentication Packages' -PropertyType MultiString -Value @('msv1_0','FaceUnlockAuthPackage','kerberos') -Force | Out-Null
    New-ItemProperty -LiteralPath $lsaKey -Name 'Security Packages' -PropertyType MultiString -Value @('tspkg','FaceUnlockAuthPackage.dll') -Force | Out-Null

    $legacyFiles = @(
        (Join-Path $installDir 'FaceUnlockCredentialProvider.dll'),
        (Join-Path $installDir 'FaceUnlockAuthPackage.dll'),
        (Join-Path $installDir 'Enable-CredentialProvider.ps1'),
        (Join-Path $installDir 'Disable-AuthPackage.ps1'),
        (Join-Path $systemDir 'FaceUnlockAuthPackage.dll'),
        (Join-Path $dataDir 'lsa_secret.dpapi')
    )
    foreach ($file in $legacyFiles) { Set-Content -LiteralPath $file -Value 'legacy-test-only' }
    $currentFile = Join-Path $installDir 'FaceUnlockShell.exe'
    $pairingFile = Join-Path $dataDir 'config.json'
    Set-Content -LiteralPath $currentFile -Value 'current-shell'
    Set-Content -LiteralPath $pairingFile -Value '{"DeviceId":"paired-device"}'

    $args = @{
        Force = $true; TestMode = $true; InstallDir = $installDir; LsaKeyPath = $lsaKey
        CredentialProviderKey = $cpKey; ComClassKey = $comKey
        SystemDirectory = $systemDir; DataDirectory = $dataDir
    }
    & $cleanup @args
    & $cleanup @args

    $auth = @((Get-ItemProperty -LiteralPath $lsaKey -Name 'Authentication Packages').'Authentication Packages')
    $security = @((Get-ItemProperty -LiteralPath $lsaKey -Name 'Security Packages').'Security Packages')
    Assert ($auth -contains 'msv1_0') 'default msv1_0 package preserved'
    Assert ($auth -contains 'kerberos') 'unrelated authentication package preserved'
    Assert ($security -contains 'tspkg') 'unrelated security package preserved'
    Assert (-not ($auth -match 'FaceUnlockAuthPackage')) 'legacy authentication package removed'
    Assert (-not ($security -match 'FaceUnlockAuthPackage')) 'legacy security package removed'
    Assert (-not (Test-Path -LiteralPath $cpKey)) 'legacy Credential Provider key removed'
    Assert (-not (Test-Path -LiteralPath $comKey)) 'legacy COM CLSID key removed'
    Assert (-not ($legacyFiles | Where-Object { Test-Path -LiteralPath $_ })) 'legacy installed files removed'
    Assert (Test-Path -LiteralPath $currentFile) 'current Shell Gate binary preserved'
    Assert (Test-Path -LiteralPath $pairingFile) 'pairing state preserved'
    Write-Host 'Legacy security migration simulation PASS'
}
finally {
    Remove-Item -LiteralPath $registryRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
