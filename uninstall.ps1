# WordLens uninstaller (per-user, no admin required)
$ErrorActionPreference = 'SilentlyContinue'
$target = Join-Path $env:LOCALAPPDATA 'WordLens'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Write-Host 'Stopping WordLens...'
Get-Process WordLens | Stop-Process -Force
Start-Sleep -Milliseconds 800

Write-Host 'Removing autostart entry...'
Remove-ItemProperty -Path $runKey -Name 'WordLens' -ErrorAction SilentlyContinue

Write-Host 'Removing shortcuts...'
$desktop = [Environment]::GetFolderPath('Desktop')
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WordLens.lnk'
Remove-Item (Join-Path $desktop 'WordLens.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item $startMenu -Force -ErrorAction SilentlyContinue

if (Test-Path $target) {
    $answer = Read-Host "Delete '$target' and all user settings/imported dictionaries? [y/N]"
    if ($answer -match '^(y|yes)$') {
        Write-Host 'Deleting program folder...'
        Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "Kept $target (you can delete it manually later)."
    }
}

Write-Host 'WordLens has been uninstalled.'
