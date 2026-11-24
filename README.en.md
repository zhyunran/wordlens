# WordLens developer guide

[简体中文使用说明](README.md) · [Windows installer](https://github.com/zhyunran/wordlens/releases/latest)

WordLens is an offline Windows tray dictionary for Chinese-speaking readers. Double-clicking a word in another app shows its meaning beside the selection. The UI is in Simplified Chinese. The code demonstrates a local text-selection and lookup workflow built with C#, WPF, Windows UI Automation, SQLite, and SAPI.

## Source map

| Component | Responsibility |
| --- | --- |
| [MouseHook.cs](src/WordLens/Core/MouseHook.cs) | Global left-button events and double-click detection |
| [SelectionCapture.cs](src/WordLens/Core/SelectionCapture.cs) | UI Automation `TextPattern` ranges, PDF recognition, and the viewer's copy command |
| [ClipboardCompat.cs](src/WordLens/Core/ClipboardCompat.cs) | Win32 Unicode text and sequence-guarded clipboard restoration |
| [LookupService.cs](src/WordLens/Core/LookupService.cs) | Local dictionary lookup and inflection handling |
| [PopupController.cs](src/WordLens/Core/PopupController.cs) | Lookup orchestration and the non-activating WPF popup |
| [Autostart.cs](src/WordLens/Core/Autostart.cs) | Per-user login registration for the quiet `--startup` path |
| [Import/](src/WordLens/Import) | StarDict, tabular, JSON, and WordLens dictionary importers |
| [installer/](installer) | WinForms installer bootstrapper and embedded application payload |

For browser PDF surfaces, WordLens can verify an incomplete or stale UI Automation range with the viewer's copied text. It checks the foreground window before sending the copy shortcut, protects clipboard formats it cannot faithfully restore, and looks up the text supplied by the viewer. Ordinary text controls typically use UI Automation alone.

## Build

Use a Windows x64 development machine with the .NET 8 SDK and Python 3.10+. Packaging uses Windows PowerShell 5.1 and the system .NET Framework C# compiler.

```powershell
powershell -ExecutionPolicy Bypass -File tools\fetch_data.ps1
python tools\build_dicts.py --raw data\raw --out data\build
dotnet publish src\WordLens\WordLens.csproj -c Release -r win-x64 --self-contained true -o publish
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

The setup EXE in `dist/` includes the .NET desktop runtime and both offline dictionaries. See [PACKAGING.md](PACKAGING.md) for the installer format and [data/README.md](data/README.md) for data generation.

## Test

```powershell
$env:WORDLENS_HOME = Join-Path $env:TEMP ('wordlens-dev-' + [guid]::NewGuid())
$env:WORDLENS_DICT_DIR = Join-Path $PWD 'data\build'
$out = Join-Path $env:WORDLENS_HOME 'query.json'
Start-Process .\publish\WordLens.exe -ArgumentList ('--query telescope --out "{0}"' -f $out) -Wait
Get-Content $out
dotnet run --project tests\StarDictTests\StarDictTests.csproj -c Release
```

The StarDict suite exercises the production parser. When changing capture, test ordinary text controls and PDF viewers separately. Compare the visible selection, the captured string, and the clipboard contents using synthetic text.

## Licenses

The application code is [MIT](LICENSE). The bundled dictionaries use ECDICT and ipa-dict under MIT and CC-CEDICT under CC BY-SA 4.0. Retain the data licenses and attribution when redistributing the databases. [Third-party notices](THIRD-PARTY-NOTICES.md)
