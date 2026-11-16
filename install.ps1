# WordLens installer (per-user, no admin required)
# Build first:  dotnet publish src\WordLens\WordLens.csproj -c Release -r win-x64 --self-contained true -o publish
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'publish'
$dicts = Join-Path $root 'data\build'
$target = Join-Path $env:LOCALAPPDATA 'WordLens'

if (!(Test-Path $publish)) { throw "publish folder not found: $publish" }
if (!(Test-Path (Join-Path $dicts 'builtin-en.wlcdb'))) { throw "built dictionaries not found: $dicts" }

Get-Process WordLens -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item "$publish\*" -Destination $target -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $target 'dicts') | Out-Null
Copy-Item (Join-Path $dicts 'builtin-en.wlcdb') (Join-Path $target 'dicts\builtin-en.wlcdb') -Force
Copy-Item (Join-Path $dicts 'builtin-zh.wlcdb') (Join-Path $target 'dicts\builtin-zh.wlcdb') -Force

# shortcuts
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

# autostart (HKCU, no admin)
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $runKey -Force | Out-Null
Set-ItemProperty -Path $runKey -Name 'WordLens' -Value ('"' + (Join-Path $target 'WordLens.exe') + '"') -Type String

# start via explorer so the process is not a child of this shell
Start-Process explorer.exe -ArgumentList ('"' + $desktop + '"')
Write-Host "WordLens installed to $target and started."
