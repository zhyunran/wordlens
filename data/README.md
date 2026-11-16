# Dictionary data

The built dictionary databases (`.wlcdb`, about 187 MB total) are **not**
committed to this repository. Rebuild them with the scripts below.

## 1. Download raw data

```powershell
powershell -ExecutionPolicy Bypass -File tools\fetch_data.ps1
```

This downloads into `data/raw/`:

| File | Source | Size | License |
|---|---|---|---|
| `ecdict.csv` | https://github.com/skywind3000/ECDICT | ~66 MB | MIT |
| `lemma.en.txt` | https://github.com/skywind3000/ECDICT | ~2.3 MB | MIT |
| `cedict.zip` | https://www.mdbg.net/chinese/export/cedict/cedict_1_0_ts_utf-8_mdbg.zip | ~4 MB | CC BY-SA 4.0 |
| `ipa/en_US.txt` | https://github.com/open-dict-data/ipa-dict | ~3.2 MB | MIT |

## 2. Build the databases

```powershell
python tools\build_dicts.py --raw data\raw --out data\build
```

Output:

- `data/build/builtin-en.wlcdb` — 770,611 entries, 96,577 inflected forms
- `data/build/builtin-zh.wlcdb` — 250,638 simplified/traditional rows
- `data/build/data-stats.json` — build statistics

The script only needs the Python standard library.

## 3. Where the app looks for dictionaries

- Built-in: `<exe dir>\dicts\*.wlcdb`
- User imports: `%LOCALAPPDATA%\WordLens\user-dicts\*.wlcdb`
- Override during development: `WORDLENS_DICT_DIR` and `WORDLENS_HOME` environment variables

## Redistribution

ECDICT and ipa-dict are MIT. CC-CEDICT is CC BY-SA 4.0; derived databases that
contain it must be redistributed under the same license with attribution.
See `THIRD-PARTY-NOTICES.md` in the repository root.
