# Check-AuthPackage.ps1
# Diagnostics script to check LSA registration status and machine secret health.

[CmdletBinding()]
param()

$lsaKeyPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Lsa"
$packageName = "FaceUnlockAuthPackage"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  FaceUnlock Authentication Package Diagnostics" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$currentPackages = (Get-ItemProperty -Path $lsaKeyPath -Name "Authentication Packages" -ErrorAction SilentlyContinue)."Authentication Packages"

Write-Host "`n1. LSA Authentication Packages Registry Status:" -ForegroundColor Yellow
if ($currentPackages -contains $packageName) {
    Write-Host "  [OK] $packageName is registered in LSA." -ForegroundColor Green
} else {
    Write-Host "  [NOT REGISTERED] $packageName is not registered in LSA (Passwordless mode will fallback or remain disabled)." -ForegroundColor DarkYellow
}
Write-Host "  Active packages: $($currentPackages -join ', ')"

$system32Dll = "$env:SystemRoot\System32\FaceUnlockAuthPackage.dll"
Write-Host "`n2. System32 DLL Deployment:" -ForegroundColor Yellow
if (Test-Path $system32Dll) {
    $info = Get-Item $system32Dll
    Write-Host "  [OK] DLL present: $system32Dll ($($info.Length) bytes, LastWrite: $($info.LastWriteTime))" -ForegroundColor Green
} else {
    Write-Host "  [MISSING] $system32Dll not found in System32." -ForegroundColor DarkYellow
}

$secretPath = "$env:ProgramData\FaceUnlock\lsa_secret.dpapi"
Write-Host "`n3. LSA Machine Secret Status:" -ForegroundColor Yellow
if (Test-Path $secretPath) {
    try {
        $sInfo = Get-Item $secretPath -ErrorAction Stop
        if ($sInfo.Length -eq 0 -or $sInfo.Length -gt 65536) {
            Write-Host "  [INVALID] Machine secret file has invalid size ($($sInfo.Length) bytes): $secretPath" -ForegroundColor Red
        } else {
            Write-Host "  [GENERATED] Machine secret file present: $secretPath" -ForegroundColor Green
            Write-Host "    Size: $($sInfo.Length) bytes"
            Write-Host "    Last Write: $($sInfo.LastWriteTime)"
            try {
                $acl = Get-Acl $secretPath
                $owner = $acl.Owner
                Write-Host "    Owner: $owner"
            } catch {
                # Ignore ACL query errors
            }
        }
    } catch [System.UnauthorizedAccessException] {
        Write-Host "  [ACCESS DENIED] Cannot access $secretPath (Admin/SYSTEM required)" -ForegroundColor Red
    } catch {
        Write-Host "  [INVALID] Error inspecting $secretPath : $($_.Exception.Message)" -ForegroundColor Red
    }
} else {
    Write-Host "  [NOT GENERATED] Machine secret not present at $secretPath" -ForegroundColor DarkYellow
    Write-Host "    (Will be automatically created by FaceUnlock.Service on startup)"
}

Write-Host "`n============================================================`n" -ForegroundColor Cyan
