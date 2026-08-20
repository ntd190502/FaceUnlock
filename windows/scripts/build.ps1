$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot\..
try {
    dotnet restore .\FaceUnlock.Core\FaceUnlock.Core.csproj
    dotnet restore .\FaceUnlock.Agent\FaceUnlock.Agent.csproj
    dotnet restore .\FaceUnlock.Service\FaceUnlock.Service.csproj
    dotnet restore .\FaceUnlock.Shell\FaceUnlock.Shell.csproj
    dotnet restore .\FaceUnlock.ShellTests\FaceUnlock.ShellTests.csproj
    dotnet restore .\FaceUnlock.BleFrameSelfTest\FaceUnlock.BleFrameSelfTest.csproj
    dotnet restore .\FaceUnlock.IpcIntegrationTests\FaceUnlock.IpcIntegrationTests.csproj
    dotnet restore .\FaceUnlock.UnitTests\FaceUnlock.UnitTests.csproj
    dotnet restore .\FaceUnlock.NativeApiSelfTest\FaceUnlock.NativeApiSelfTest.csproj

    dotnet build -c Release --no-restore .\FaceUnlock.Core\FaceUnlock.Core.csproj
    dotnet build -c Release --no-restore .\FaceUnlock.Agent\FaceUnlock.Agent.csproj
    dotnet build -c Release --no-restore .\FaceUnlock.Service\FaceUnlock.Service.csproj
    dotnet build -c Release --no-restore .\FaceUnlock.Shell\FaceUnlock.Shell.csproj
    
    dotnet run -c Release --no-restore --project .\FaceUnlock.BleFrameSelfTest\FaceUnlock.BleFrameSelfTest.csproj
    dotnet run -c Release --no-restore --project .\FaceUnlock.UnitTests\FaceUnlock.UnitTests.csproj
    dotnet run -c Release --no-restore --project .\FaceUnlock.IpcIntegrationTests\FaceUnlock.IpcIntegrationTests.csproj
    dotnet run -c Release --no-restore --project .\FaceUnlock.ShellTests\FaceUnlock.ShellTests.csproj
    dotnet run -c Release --no-restore --project .\FaceUnlock.NativeApiSelfTest\FaceUnlock.NativeApiSelfTest.csproj

    Write-Host 'Managed projects and Phase F Shell Gate tests passed.' -ForegroundColor Green
    Write-Host 'Retired Credential Provider/AuthPackage projects are intentionally absent.'
}
finally {
    Pop-Location
}
