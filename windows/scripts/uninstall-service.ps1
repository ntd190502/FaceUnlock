#Requires -RunAsAdministrator
$ErrorActionPreference = 'SilentlyContinue'

sc.exe stop "FaceUnlock Service" | Out-Null
Start-Sleep -Seconds 1
sc.exe delete "FaceUnlock Service" | Out-Null

Write-Host "FaceUnlock Service stopped and uninstalled." -ForegroundColor Green
