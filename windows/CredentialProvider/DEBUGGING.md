# FaceUnlock Credential Provider — Debugging Guide

## Emergency Recovery

Nếu Windows lock screen bị flicker / không đăng nhập được sau khi cài FaceUnlock:

### Cách 1: Dùng Recovery Script (có trong Start Menu)

```powershell
# Run as Administrator
PowerShell -ExecutionPolicy Bypass -File "C:\Program Files\FaceUnlock\FaceUnlock-Recovery.ps1"
```

Hoặc click Start Menu → FaceUnlock → FaceUnlock Emergency Recovery

### Cách 2: Safe Mode

1. Tắt máy, boot vào Windows Recovery (F8 hoặc giữ Shift khi click Restart)
2. Troubleshoot → Advanced Options → Startup Settings → Enable Safe Mode with Networking
3. Đăng nhập bằng PIN/Password
4. Mở PowerShell as Admin → chạy Recovery.ps1 hoặc lệnh dưới

### Cách 3: Manual Registry Removal

```powershell
# Remove Credential Provider from Winlogon
Remove-Item -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{64D6E84B-4969-4B59-A11A-58C3D9FA0110}" -Recurse -Force -ErrorAction SilentlyContinue

# Remove COM InprocServer32
Remove-Item -Path "HKLM:\SOFTWARE\Classes\CLSID\{64D6E84B-4969-4B59-A11A-58C3D9FA0110}" -Recurse -Force -ErrorAction SilentlyContinue
```

---

## Getting Crash Logs After a Lock Screen Issue

### From PowerShell (after recovery)

```powershell
# Get recent LogonUI / FaceUnlock errors in Application log
Get-WinEvent -FilterHashtable @{
    LogName   = 'Application'
    StartTime = (Get-Date).AddMinutes(-10)
} | Where-Object {
    $_.Message -match 'LogonUI|FaceUnlockCredentialProvider|credentialprovider'
} | Format-List TimeCreated, Id, ProviderName, Message

# Get System log (may contain Winlogon errors)
Get-WinEvent -FilterHashtable @{
    LogName   = 'System'
    StartTime = (Get-Date).AddMinutes(-10)
} | Where-Object {
    $_.Message -match 'LogonUI|Winlogon|FaceUnlock'
} | Format-List TimeCreated, Id, ProviderName, Message
```

### From Event Viewer (GUI)

```
Event Viewer → Windows Logs → Application
```

Filter by Source: Application Error

If LogonUI crashed, look for:
- **Faulting application**: `LogonUI.exe`
- **Faulting module**: `FaceUnlockCredentialProvider.dll`
- **Exception code**: `0xc0000005` (ACCESS_VIOLATION), `0xc0000374` (HEAP_CORRUPTION)
- **Fault offset**: memory offset in DLL

### Windows Error Reporting (WER) Crash Dumps

```powershell
# Check WER local dumps
Get-ChildItem "C:\ProgramData\Microsoft\Windows\WER\ReportQueue" -Recurse | 
    Where-Object { $_.Name -match "LogonUI" } | 
    Sort-Object LastWriteTime -Descending | 
    Select-Object -First 5 FullName, LastWriteTime

# Check user WER reports
Get-ChildItem "$env:LOCALAPPDATA\CrashDumps" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "LogonUI" }
```

### FaceUnlock Provider Log

```
C:\ProgramData\FaceUnlock\logs\credentialprovider.log
C:\ProgramData\FaceUnlock\logs\credentialprovider.log.1
```

```powershell
# View last 50 lines
Get-Content "C:\ProgramData\FaceUnlock\logs\credentialprovider.log" -Tail 50
```

---

## Enabling / Disabling Credential Provider (Debug Builds)

> **WARNING**: Do NOT enable the Credential Provider until CP_SAFE_FOR_LOGONUI_TEST=YES

### Enable (opt-in, manual)

```powershell
# Run as Administrator
PowerShell -ExecutionPolicy Bypass -File "C:\Program Files\FaceUnlock\Enable-CredentialProvider.ps1"
```

### Disable

```powershell
# Run as Administrator
PowerShell -ExecutionPolicy Bypass -File "C:\Program Files\FaceUnlock\Disable-CredentialProvider.ps1"
```

---

## Known Root Causes Fixed (v1.2+)

| Bug | Symptom | Fix |
|-----|---------|-----|
| `SetFieldState` from background thread | CredentialsChanged loop → flicker | Removed SetFieldState from thread; GetFieldState is now polled by LogonUI |
| `events_` not AddRef'd | Use-after-free crash | AddRef in Advise, Release in UnAdvise |
| detached thread access stale events_ | ACCESS_VIOLATION | Thread captures events_ with AddRef under mutex |
| check-then-set race for authInProgress_ | Multiple threads spawned | Atomic check+set in one mutex scope |
| `std::string::npos` instead of `std::wstring::npos` | Incorrect account detection | Fixed to std::wstring::npos |
| Blocking ReserveGrant on LogonUI thread | 5s UI freeze | Moved to after explicit submit only |
| Installer auto-registers CP | Any bug → broken lock screen | Disabled auto-registration |

---

## Verifying the Credential Provider is Safe

Run the standalone harness:

```cmd
CredentialProviderHarness.exe "C:\Program Files\FaceUnlock\FaceUnlockCredentialProvider.dll"
```

All 13 tests must pass before registering in Winlogon.

---

## Test Gate Checklist

- [ ] `CredentialProviderHarness.exe` exits with code 0
- [ ] Test 9 (1000 iterations): PASS
- [ ] Test 10 (Async destruction): PASS
- [ ] Test 11 (IPC unavailable): PASS
- [ ] Test 12 (No CredentialsChanged loop): PASS
- [ ] No SetFieldState calls from background thread
- [ ] COM ref count audit: no negative refcount warnings in log
- [ ] Build: 0 errors, 0 critical warnings

Only after all above: `CP_SAFE_FOR_LOGONUI_TEST = YES`
