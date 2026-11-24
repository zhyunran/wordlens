# Build WordLens-Setup.exe (per-user installer with an appended payload ZIP).
# Requires Windows PowerShell 5.1, the .NET Framework 4.8 csc.exe and a published app folder.
param(
    [string]$PublishDir = (Join-Path $PSScriptRoot '..\publish'),
    [string]$DataDir = (Join-Path $PSScriptRoot '..\data\build'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\dist'),
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$PublishDir = (Resolve-Path $PublishDir).Path
$DataDir = (Resolve-Path $DataDir).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$assembly = Get-Item -LiteralPath (Join-Path $PublishDir 'WordLens.dll')
$fileVersion = $assembly.VersionInfo
$payloadVersion = '{0}.{1}.{2}' -f $fileVersion.FileMajorPart, $fileVersion.FileMinorPart, $fileVersion.FileBuildPart
if (-not $Version) { $Version = $payloadVersion }
if ($Version -ne $payloadVersion) { throw "Installer version $Version does not match payload version $payloadVersion" }

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path $csc)) { throw "C# compiler not found: $csc" }

# Locate framework assemblies that csc cannot resolve by simple name.
$gac = 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL'
$compression = Get-ChildItem $gac -Directory -Filter 'System.IO.Compression' | ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter 'System.IO.Compression.dll' } | Select-Object -First 1
$compressionFs = Get-ChildItem $gac -Directory -Filter 'System.IO.Compression.FileSystem' | ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter 'System.IO.Compression.FileSystem.dll' } | Select-Object -First 1
if (-not $compression -or -not $compressionFs) { throw 'System.IO.Compression assemblies not found in the GAC' }

$stubDir = Join-Path $env:TEMP ('wl-stub-' + [guid]::NewGuid().ToString('N'))
$payloadDir = Join-Path $env:TEMP ('wl-payload-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stubDir, $payloadDir | Out-Null

try {
    Write-Host 'Compiling installer stub...'
    $versionSource = Join-Path $stubDir 'VersionInfo.cs'
    @(
        '[assembly: System.Reflection.AssemblyVersion("' + $Version + '.0")]'
        '[assembly: System.Reflection.AssemblyFileVersion("' + $Version + '.0")]'
    ) | Set-Content -LiteralPath $versionSource -Encoding ASCII
    $refs = @(
        '/r:System.dll',
        '/r:System.Core.dll',
        '/r:System.Drawing.dll',
        '/r:System.Windows.Forms.dll',
        ('/r:"' + $compression.FullName + '"'),
        ('/r:"' + $compressionFs.FullName + '"')
    )
    & $csc /nologo /target:winexe /platform:anycpu /optimize+ /win32icon:"$repo\src\WordLens\Assets\wordlens.ico" /win32manifest:"$PSScriptRoot\installer.manifest" $refs /out:"$stubDir\WordLensSetup.exe" "$PSScriptRoot\WordLensSetup.cs" "$versionSource"
    if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
    $stub = Join-Path $stubDir 'WordLensSetup.exe'
    Write-Host ("Stub: {0:N0} bytes" -f (Get-Item $stub).Length)

    Write-Host 'Preparing payload...'
    Copy-Item "$PublishDir\*" $payloadDir -Recurse -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $payloadDir 'dicts') | Out-Null
    Copy-Item (Join-Path $DataDir 'builtin-en.wlcdb') (Join-Path $payloadDir 'dicts\builtin-en.wlcdb') -Force
    Copy-Item (Join-Path $DataDir 'builtin-zh.wlcdb') (Join-Path $payloadDir 'dicts\builtin-zh.wlcdb') -Force

    $docsFiles = @('README.md', 'README.en.md', 'REPORT.md', 'LICENSES.txt', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md', 'uninstall.ps1')
    foreach ($f in $docsFiles) {
        $src = Join-Path $repo $f
        if (Test-Path $src) { Copy-Item $src $payloadDir -Force }
    }
    $docsSrc = Join-Path $repo 'docs'
    if (Test-Path $docsSrc) {
        New-Item -ItemType Directory -Force -Path (Join-Path $payloadDir 'docs') | Out-Null
        Copy-Item "$docsSrc\*" (Join-Path $payloadDir 'docs') -Recurse -Force
    }
    Copy-Item $stub (Join-Path $payloadDir 'uninstall.exe') -Force

    # Remove development-only files from the payload if present.
    Get-ChildItem $payloadDir -Filter '*.pdb' -File -ErrorAction SilentlyContinue | Remove-Item -Force

    $zip = Join-Path $stubDir 'payload.zip'
    Write-Host 'Compressing payload (this can take a few minutes)...'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $payloadDir, $zip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )
    Write-Host ("Payload: {0:N0} bytes" -f (Get-Item $zip).Length)

    $setup = Join-Path $OutDir ("WordLens-$Version-Setup.exe")
    if (Test-Path $setup) { Remove-Item $setup -Force }
    Write-Host 'Appending payload...'
    $out = [System.IO.File]::Create($setup)
    try {
        $stubStream = [System.IO.File]::OpenRead($stub)
        try { $stubStream.CopyTo($out) } finally { $stubStream.Dispose() }
        $zipStream = [System.IO.File]::OpenRead($zip)
        try { $zipStream.CopyTo($out) } finally { $zipStream.Dispose() }
        $footer = [System.Text.Encoding]::ASCII.GetBytes('WLZIP001') + [BitConverter]::GetBytes([int64](Get-Item $zip).Length)
        $out.Write($footer, 0, $footer.Length)
    }
    finally {
        $out.Dispose()
    }
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash
    $size = (Get-Item $setup).Length
    Write-Host ''
    Write-Host ("Setup: {0}" -f $setup)
    Write-Host ("Size : {0:N0} bytes" -f $size)
    Write-Host ("SHA256: {0}" -f $hash)
    Set-Content -Path (Join-Path $OutDir 'setup-sha256.txt') -Value ($hash + '  WordLens-' + $Version + '-Setup.exe') -Encoding ASCII
}
finally {
    Remove-Item $stubDir, $payloadDir -Recurse -Force -ErrorAction SilentlyContinue
}
