# Download the raw dictionary sources used by tools/build_dicts.py.
# Everything lands in data/raw/ (git-ignored); about 75 MB in total.
param(
    [string]$RawDir = (Join-Path $PSScriptRoot '..\data\raw')
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$RawDir = [System.IO.Path]::GetFullPath($RawDir)
New-Item -ItemType Directory -Force -Path $RawDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $RawDir 'ipa') | Out-Null

$sources = @(
    @{ Url = 'https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv'; Out = 'ecdict.csv'; Hint = 'ECDICT, MIT, ~66 MB' },
    @{ Url = 'https://raw.githubusercontent.com/skywind3000/ECDICT/master/lemma.en.txt'; Out = 'lemma.en.txt'; Hint = 'ECDICT lemma list, MIT, ~2.3 MB' },
    @{ Url = 'https://www.mdbg.net/chinese/export/cedict/cedict_1_0_ts_utf-8_mdbg.zip'; Out = 'cedict.zip'; Hint = 'CC-CEDICT, CC BY-SA 4.0, ~4 MB' },
    @{ Url = 'https://raw.githubusercontent.com/open-dict-data/ipa-dict/master/data/en_US.txt'; Out = 'ipa\en_US.txt'; Hint = 'ipa-dict en_US, MIT, ~3.2 MB' }
)

foreach ($src in $sources) {
    $dest = Join-Path $RawDir $src.Out
    if (Test-Path -LiteralPath $dest) {
        $size = (Get-Item -LiteralPath $dest).Length
        Write-Host ("[skip] {0} already exists ({1:N0} bytes)" -f $src.Out, $size)
        continue
    }
    Write-Host ("[get ] {0}  ({1})" -f $src.Out, $src.Hint)
    try {
        Invoke-WebRequest -Uri $src.Url -OutFile $dest -UseBasicParsing
    }
    catch {
        Remove-Item -LiteralPath $dest -Force -ErrorAction SilentlyContinue
        throw ("Download failed for {0}: {1}`nDownload it manually from {2} and save it as data\raw\{0}" -f $src.Out, $_.Exception.Message, $src.Url)
    }
    $size = (Get-Item -LiteralPath $dest).Length
    if ($size -lt 1024) { throw ("{0} looks too small ({1} bytes); the download probably failed" -f $src.Out, $size) }
    Write-Host ("[ ok ] {0}  {1:N0} bytes" -f $src.Out, $size)
}

Write-Host ''
Write-Host 'Raw data ready. Next:'
Write-Host '  python tools\build_dicts.py --raw data\raw --out data\build'
