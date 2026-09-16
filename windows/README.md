# FaceUnlock Windows Solution

Native Windows .NET 8 solution implementing the **Post-Logon Shell Gate**, background SYSTEM service, pairing agent, and BLE authentication client.

---

## Component Overview

| Project | Role | Runtime Context |
| :--- | :--- | :--- |
| `FaceUnlock.Core` | Common library; DPAPI token storage, ECDSA P-256 validation, BLE GATT framing, and IPC models. | Shared DLL |
| `FaceUnlock.Service` | SYSTEM background service; owns session gate authority, watchdog, and hardware BLE lease manager. | `NT AUTHORITY\SYSTEM` |
| `FaceUnlock.Shell` | Post-Logon Shell Gate (`FaceUnlockShell.exe`); blocks desktop entry and captures escape shortcuts before Explorer. | Interactive Session |
| `FaceUnlock.Agent` | User-facing WPF desktop interface; handles QR pairing, diagnostics, and testing controls. | Interactive Session |
| `FaceUnlock.AlertUI` | Standalone on-demand interactive dialog; spawned in active session only when a critical alert occurs. | Interactive Session |

---

## Post-Logon Shell Gate Architecture

FaceUnlock implements a true Post-Logon Shell Gate:
- **Pre-Explorer Gatekeeper**: The Windows shell is set to `FaceUnlockShell.exe`. The Windows desktop (`explorer.exe`) is never launched until a cryptographic grant is consumed.
- **IPC Authority & Watchdog**: `FaceUnlock.Service.exe` actively monitors the shell process. If an unauthorized user attempts to kill the shell or spawn `explorer.exe` via Task Manager, the Service terminates Explorer and relaunches the lock gate.
- **Escape Shortcut Interception**: Low-level keyboard guards intercept common exit shortcuts (Alt+Tab, Win keys, Alt+F4) while locked.
- **Safe Mode Immunity**: Windows Safe Mode and WinRE emergency recovery environments remain unaffected. An automated recovery script (`FaceUnlock-Shell-Recovery.ps1`) is provided in the installation root to revert to standard Windows Explorer at any time.

---

## Bluetooth Low Energy (BLE) Lease Management

- **Zero Radio Footprint**: If Bluetooth was initially OFF before a lock request, FaceUnlock temporarily activates it for authorization and safely turns it back OFF when the lease completes.
- **Multi-Transport Deduplication**: Online (Telegram) and offline (BLE) transports share the same cryptographic logical request ID, preventing duplicate Face ID prompts on the phone.

---

## Building Locally

To build all Windows components locally using .NET 8 SDK:

```powershell
dotnet build windows/FaceUnlock.Core/FaceUnlock.Core.csproj -c Release
dotnet build windows/FaceUnlock.Service/FaceUnlock.Service.csproj -c Release
dotnet build windows/FaceUnlock.Agent/FaceUnlock.Agent.csproj -c Release
dotnet build windows/FaceUnlock.Shell/FaceUnlock.Shell.csproj -c Release
dotnet build windows/FaceUnlock.AlertUI/FaceUnlock.AlertUI.csproj -c Release
```

Or run the automated build script:
```powershell
powershell -ExecutionPolicy Bypass -File windows/scripts/build.ps1
```

To run the complete unit test suite:
```powershell
dotnet run --project windows/FaceUnlock.UnitTests/FaceUnlock.UnitTests.csproj
dotnet run --project windows/FaceUnlock.BleFrameSelfTest/FaceUnlock.BleFrameSelfTest.csproj
```
