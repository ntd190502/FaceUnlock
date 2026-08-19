$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot\..
try {
    dotnet restore .\FaceUnlock.Core\FaceUnlock.Core.csproj
    dotnet restore .\FaceUnlock.Agent\FaceUnlock.Agent.csproj
    dotnet restore .\FaceUnlock.Service\FaceUnlock.Service.csproj
    dotnet restore .\FaceUnlock.BleFrameSelfTest\FaceUnlock.BleFrameSelfTest.csproj

    dotnet build -c Release --no-restore .\FaceUnlock.Core\FaceUnlock.Core.csproj
    dotnet build -c Release --no-restore .\FaceUnlock.Agent\FaceUnlock.Agent.csproj
    dotnet build -c Release --no-restore .\FaceUnlock.Service\FaceUnlock.Service.csproj
    dotnet run -c Release --no-restore --project .\FaceUnlock.BleFrameSelfTest\FaceUnlock.BleFrameSelfTest.csproj

    Write-Host 'Managed projects built and BLE framing self-test passed.' -ForegroundColor Green
    Write-Host 'CredentialProvider is separate and intentionally not built here: use CMake + Visual Studio x64.'
}
finally {
    Pop-Location
}
