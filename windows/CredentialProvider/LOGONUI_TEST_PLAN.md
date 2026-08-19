# FaceUnlock Credential Provider — LogonUI Test & Validation Plan

> [!WARNING]
> DO NOT execute this manual test until all safety harness automated tests pass in CI/CD.
> Follow this checklist step-by-step to prevent lockout or login loop.

---

## 1. PRECHECK (Pre-flight safety inspection)

Before enabling the Credential Provider, verify the following:

1. **FaceUnlock Service Status:**
   - Run `Get-Service "FaceUnlock Service"` in PowerShell (Admin).
   - Ensure Status is `Running` (or service is reachable).
2. **DLL Existence:**
   - Confirm `C:\Program Files\FaceUnlock\FaceUnlockCredentialProvider.dll` exists and is signed/valid Release build x64.
3. **Recovery Scripts Available:**
   - Confirm `C:\Program Files\FaceUnlock\Disable-CredentialProvider.ps1` exists.
   - Confirm `C:\Program Files\FaceUnlock\FaceUnlock-Recovery.ps1` exists.
4. **Alternative Sign-in Providers:**
   - Verify Windows PIN and Password login options are functioning properly and NOT filtered out.

---

## 2. ENABLE (Manual Activation)

Open PowerShell as **Administrator** and run:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
cd "C:\Program Files\FaceUnlock"
.\Enable-CredentialProvider.ps1
```

Type `YES` when prompted to confirm registration.

---

## 3. REGISTRY CHECK (Verification)

Verify that the registry entries were created cleanly:

1. **COM InprocServer32:**
   ```powershell
   Get-ItemProperty -Path "HKLM:\SOFTWARE\Classes\CLSID\{64D6E84B-4969-4B59-A11A-58C3D9FA0110}\InprocServer32"
   ```
   - `(Default)`: `C:\Program Files\FaceUnlock\FaceUnlockCredentialProvider.dll`
   - `ThreadingModel`: `Apartment`

2. **Winlogon Credential Provider:**
   ```powershell
   Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{64D6E84B-4969-4B59-A11A-58C3D9FA0110}"
   ```
   - `(Default)`: `FaceUnlock`

---

## 4. FIRST TEST (Live Lock Screen Verification)

> [!IMPORTANT]
> A machine reboot is **NOT** required.
> Only lock the workstation (`Win + L`) when you have an administrative session or SSH/safe mode fallback ready.

1. Lock the workstation using `Win + L`.
2. Inspect the login screen:
   - Ensure no screen flickering / loop.
   - Select the **FaceUnlock** tile.
   - Trigger Face ID authentication or verify status text shows `Sign in with iPhone Face ID` / `Unlock with iPhone Face ID`.
   - Verify fallback to Password/PIN works at any time by switching tiles.
3. Check `C:\ProgramData\FaceUnlock\logs\credentialprovider.log` for execution trace.

---

## 5. ROLLBACK (Deactivation / Emergency Recovery)

If any unexpected issue occurs or testing is complete:

### Standard Disable:
```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
& "C:\Program Files\FaceUnlock\Disable-CredentialProvider.ps1"
```

### Emergency Recovery (also available in Start Menu):
```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
& "C:\Program Files\FaceUnlock\FaceUnlock-Recovery.ps1"
```

*Both scripts only remove the FaceUnlock CLSID and never alter default Windows Authentication packages (Password / PIN).*
