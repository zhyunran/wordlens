# One-shot build: data -> dictionaries -> self-contained app -> installer.
# Requirements: Windows x64, .NET 8 SDK, Python 3.10+, PowerShell 5.1.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
    if (-not $dotnet) { throw 'dotnet 8 SDK not found in PATH. Install it from https://dotnet.microsoft.com/download/dotnet/8.0' }
    $python = (Get-Command python -ErrorAction SilentlyContinue)
    if (-not $python) { throw 'Python 3 not found in PATH. Install Python 3.10+ and enable "Add to PATH".' }

    if (!(Test-Path 'data\raw\ecdict.csv') -or !(Test-Path 'data\raw\ipa\en_US.txt')) {
        & powershell -ExecutionPolicy Bypass -File tools\fetch_data.ps1
    }

    Write-Host '=== Building dictionaries ==='
    & python tools\build_dicts.py --raw data\raw --out data\build
    if ($LASTEXITCODE -ne 0) { throw 'dictionary build failed' }

    Write-Host '=== Publishing app ==='
    & dotnet publish src\WordLens\WordLens.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

    Write-Host '=== Building installer ==='
    & powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -PublishDir publish -DataDir data\build -OutDir dist
    if ($LASTEXITCODE -ne 0) { throw 'installer build failed' }

    Write-Host ''
    Write-Host 'Done. Installer: dist\WordLens-1.0.0-Setup.exe'
}
finally {
    Pop-Location
}
