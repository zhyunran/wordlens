# Uninstall script for source/portable installs.
$ErrorActionPreference = 'SilentlyContinue'
$target = Join-Path $env:LOCALAPPDATA 'WordLens'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process WordLens | Stop-Process -Force
Start-Sleep -Milliseconds 800

Remove-ItemProperty -Path $runKey -Name 'WordLens' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'WordLens.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WordLens.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WordLens' -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path $target) {
    $answer = Read-Host "Delete '$target' (settings, imported dictionaries, wordbook)? [y/N]"
    if ($answer -match '^(y|yes)$') {
        Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "Kept $target"
    }
}
Write-Host 'WordLens uninstalled.'
