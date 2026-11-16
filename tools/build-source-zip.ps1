# Assemble the distributable source ZIP from the working tree.
# Intact extras that only exist in the packaged staging copy are pulled from there.
param(
    [string]$Root = (Join-Path $PSScriptRoot '..'),
    [string]$Staging = (Join-Path $PSScriptRoot '..\packaging\source\WordLens-1.0.0'),
    [string]$Out = (Join-Path $PSScriptRoot '..\dist\WordLens-1.0.0-Source.zip')
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path $Root).Path
$Staging = (Resolve-Path $Staging).Path
$name = 'WordLens-1.0.0'
$stage = Join-Path $env:TEMP ('wl-src-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $stage $name
New-Item -ItemType Directory -Force -Path $target | Out-Null

function Copy-Tree {
    param([string]$From, [string]$To, [string[]]$ExcludeDirs = @(), [string[]]$ExcludeFiles = @())
    if (-not (Test-Path $From)) { Write-Host "skip (missing): $From"; return }
    $xd = @('/XD')
    foreach ($d in $ExcludeDirs) { $xd += $d }
    $xf = @()
    foreach ($f in $ExcludeFiles) { $xf += '/XF'; $xf += $f }
    $args = @($From, $To, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/R:1', '/W:1', '/NP')
    if ($ExcludeDirs.Count -gt 0) { $args += $xd }
    if ($ExcludeFiles.Count -gt 0) { $args += $xf }
    & robocopy @args | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed for $From (exit $LASTEXITCODE)" }
}

# 1. Source tree (restored) - never ship build output.
Copy-Tree (Join-Path $Root 'src') (Join-Path $target 'src') @('bin', 'obj')
Copy-Tree (Join-Path $Root 'installer') (Join-Path $target 'installer')
Copy-Tree (Join-Path $Root 'tools') (Join-Path $target 'tools') @() @('e2e-drive.ps1')
Copy-Tree (Join-Path $Root 'docs') (Join-Path $target 'docs')

# 2. Tests: harness + fixtures + UI helper (skip the ad-hoc captured json/png artefacts).
Copy-Tree (Join-Path $Root 'tests\StarDictTests') (Join-Path $target 'tests\StarDictTests') @('bin', 'obj')
Copy-Tree (Join-Path $Root 'tests\uihelper') (Join-Path $target 'tests\uihelper') @('bin', 'obj')
Copy-Tree (Join-Path $Root 'tests\fixtures') (Join-Path $target 'tests\fixtures')

# 3. Files that only exist in the packaged staging copy (scripts, data README, licence, gitignore).
foreach ($rel in @('scripts', 'data\README.md', 'CHANGELOG.md', '.gitignore', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    $src = Join-Path $Staging $rel
    $dst = Join-Path $target $rel
    if (-not (Test-Path $src)) { Write-Host "skip (missing in staging): $rel"; continue }
    if (Test-Path $src -PathType Container) {
        Copy-Tree $src $dst
    } else {
        New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
        Copy-Item $src $dst -Force
    }
}

# 4. Top-level documents from the live tree (README/REPORT/PACKAGING are maintained here).
foreach ($f in @('README.md', 'REPORT.md', 'PACKAGING.md', 'LICENSES.txt', 'install.ps1', 'uninstall.ps1')) {
    $src = Join-Path $Root $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $target $f) -Force } else { Write-Host "skip (missing): $f" }
}

# 5. Drop anything that should never ship.
Get-ChildItem $target -Recurse -File -Include *.pdb, *.user, *.suo | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem $target -Recurse -Directory -Include bin, obj | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path $Out) { Remove-Item $Out -Force }

# Compress-Archive on PowerShell 5.1 stores entry names with backslashes, which GitHub and
# Linux unzip treat as literal characters -> write the archive ourselves with '/' separators.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fs = [System.IO.File]::Create($Out)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        $base = (Resolve-Path $target).Path
        foreach ($file in Get-ChildItem $target -Recurse -File | Sort-Object FullName) {
            $rel = $file.FullName.Substring($base.Length + 1).Replace('\', '/')
            $entry = $archive.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            try {
                $input = [System.IO.File]::OpenRead($file.FullName)
                try { $input.CopyTo($entryStream) } finally { $input.Dispose() }
            }
            finally { $entryStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}
finally { $fs.Dispose() }

$hash = (Get-FileHash $Out -Algorithm SHA256).Hash
$size = (Get-Item $Out).Length
Write-Host ("Source: {0}" -f $Out)
Write-Host ("Size  : {0:N0} bytes" -f $size)
Write-Host ("SHA256: {0}" -f $hash)
Write-Host ("Files : {0}" -f (Get-ChildItem $target -Recurse -File).Count)

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
