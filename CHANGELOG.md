# Changelog

## 1.0.0 (2026-10-26)

First public release.

- Global double-click lookup in any Windows application, including Edge's
  built-in PDF viewer, Word, Notepad and browsers.
- Selection capture via UI Automation with a clipboard-safe Ctrl+C fallback
  (the previous clipboard contents are restored).
- Built-in offline English→Chinese (ECDICT) and Chinese→English (CC-CEDICT)
  dictionaries, with standard IPA merged from ipa-dict.
- Phonetic display normalisation, inflection fallback (`perceived → perceive`).
- Windows SAPI pronunciation (Zira / David / Huihui), rate and volume settings.
- Favorites, history, copy, browser search.
- Dictionary import: StarDict (ifo/zip, dictzip), CSV/TSV/TXT, JSON/JSONL,
  `.wlcdb`; enable/disable, priority ordering, delete.
- No-activate popup window, DPI aware, screen-edge flipping, Esc/click-outside/
  timeout dismissal.
- Per-user installation, desktop/start-menu shortcuts, HKCU autostart,
  uninstall entry, tray icon, single instance.
- Self-contained .NET 8 build and a double-click installer (no admin rights).
