sc.exe stop FaceUnlockService | Out-Null
Start-Sleep -Seconds 1
sc.exe delete FaceUnlockService
