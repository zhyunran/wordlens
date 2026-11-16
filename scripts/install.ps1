# Portable per-user install from a local publish/ folder (no installer needed).
# Requires: dotnet publish already produced .\publish and dictionaries in .\data\build.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'publish'
$dicts = Join-Path $root 'data\build'
$target = Join-Path $env:LOCALAPPDATA 'WordLens'

if (!(Test-Path (Join-Path $publish 'WordLens.exe'))) {
    throw "publish not found: run  dotnet publish src\WordLens\WordLens.csproj -c Release -r win-x64 --self-contained true -o publish"
}
if (!(Test-Path (Join-Path $dicts 'builtin-en.wlcdb'))) {
    throw "dictionaries not found: run  python tools\build_dicts.py --raw data\raw --out data\build"
}

Get-Process WordLens -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item "$publish\*" -Destination $target -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $target 'dicts') | Out-Null
Copy-Item (Join-Path $dicts 'builtin-en.wlcdb') (Join-Path $target 'dicts\builtin-en.wlcdb') -Force
Copy-Item (Join-Path $dicts 'builtin-zh.wlcdb') (Join-Path $target 'dicts\builtin-zh.wlcdb') -Force

$ws = New-Object -ComObject WScript.Shell
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WordLens.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'WordLens.lnk'
foreach ($lnkPath in @($startMenu, $desktop)) {
    $lnk = $ws.CreateShortcut($lnkPath)
    $lnk.TargetPath = Join-Path $target 'WordLens.exe'
    $lnk.WorkingDirectory = $target
    $lnk.IconLocation = (Join-Path $target 'WordLens.exe') + ',0'
    $lnk.Description = 'WordLens 双击查词'
    $lnk.Save()
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $runKey -Force | Out-Null
Set-ItemProperty -Path $runKey -Name 'WordLens' -Value ('"' + (Join-Path $target 'WordLens.exe') + '"') -Type String

Start-Process explorer.exe -ArgumentList ('"' + $desktop + '"')
Write-Host "WordLens installed to $target and started."
