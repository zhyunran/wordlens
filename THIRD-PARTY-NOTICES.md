# Third-party notices

## Application code

WordLens application code is released under the MIT License (see `LICENSE`).

## ECDICT (English → Chinese dictionary data)

- Source: https://github.com/skywind3000/ECDICT
- License: MIT License
- Copyright (c) 2025 Linwei

Built into `builtin-en.wlcdb`: 770,611 entries with Chinese translations,
English definitions, phonetics, exam tags and frequency data.

## CC-CEDICT (Chinese → English dictionary data)

- Source: https://www.mdbg.net/chinese/dictionary?page=cc-cedict
- License: Creative Commons Attribution-ShareAlike 4.0 International
  (CC BY-SA 4.0) — https://creativecommons.org/licenses/by-sa/4.0/
- Copyright: CC-CEDICT contributors

Built into `builtin-zh.wlcdb`: 250,638 rows (simplified + traditional),
with pinyin and English definitions.

## ipa-dict (IPA transcriptions)

- Source: https://github.com/open-dict-data/ipa-dict
- License: MIT License
- Copyright (c) 2016 dohliam

Merged into `builtin-en.wlcdb` for 83,424 English entries (en_US, standard
IPA stress placement).

## .NET / WPF / Windows Forms

- Source: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf
- License: MIT License — Copyright (c) .NET Foundation and Contributors

Self-contained publish output includes the .NET runtime and WPF.

## StarDict parser

Implemented for WordLens (`src/WordLens/Import/StarDict.cs`), no third-party
code.

## Redistribution note

If you redistribute the prebuilt installer or the built `.wlcdb` dictionary
databases, keep the attribution above. The CC-CEDICT data is CC BY-SA 4.0:
derived databases containing it must be shared under the same license.
