$ErrorActionPreference='Stop'
$exe=(Resolve-Path "$PSScriptRoot\..\FaceUnlock.Service\bin\Release\net8.0-windows10.0.19041.0\FaceUnlock.Service.exe").Path
sc.exe create FaceUnlockService binPath= "`"$exe`"" start= auto
sc.exe description FaceUnlockService "FaceUnlock iPhone approval bridge"
sc.exe start FaceUnlockService
