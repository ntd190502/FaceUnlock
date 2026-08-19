# Build Windows

Requirements: Windows 11, Visual Studio 2022/2026 with Desktop development with .NET and Desktop development with C++, .NET 8 SDK, Windows 10/11 SDK.

PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd windows
.\scripts\build.ps1
```

The build script restores/builds:

1. `FaceUnlock.Core`
2. `FaceUnlock.Agent`
3. `FaceUnlock.Service`
4. `FaceUnlock.BleFrameSelfTest`, then runs the BLE framing round-trip self-test.

`CredentialProvider` is deliberately separate and is **not** part of this phase/build. Build it only via its own CMake/Visual Studio flow when working on that later phase.

Install the service only after Agent online/BLE approval tests pass:

```powershell
.\scripts\install-service.ps1
```

Keep Windows PIN/password recovery providers enabled.
