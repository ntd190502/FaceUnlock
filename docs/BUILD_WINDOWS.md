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
4. `FaceUnlock.Shell`
5. Shell Gate, IPC, transport/Bluetooth lease, and BLE framing tests.

The retired Credential Provider/AuthPackage projects and their harnesses are not
part of the repository or build. `Cleanup-PhaseE.ps1` is packaged only for upgrade
migration. The Windows release workflow publishes Agent, Service, and Shell, validates
the installer staging set, then compiles `FaceUnlock-Setup.exe` with Inno Setup 6.
CI also runs `FaceUnlock.NativeApiSelfTest` plus a real SCM/named-pipe Setup repair
test on an ephemeral Windows runner.

Install the service only after Agent online/BLE approval tests pass:

```powershell
.\scripts\install-service.ps1
```

Keep Windows PIN/password recovery providers enabled.
