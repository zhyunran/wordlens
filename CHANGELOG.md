# Changelog

## 1.0.1 (2026-11-24)

- PDF 浏览器选区与阅读器复制结果核对，改善旧选区和残缺文字造成的漏弹、错词。
- 默认随 Windows 登录安静启动到托盘。移除定时工作集裁剪和强制垃圾回收。
- Windows x64 安装 EXE 内置运行时和离线词典，可直接覆盖升级。

- Verify browser PDF selections against the viewer's copied text and protect the clipboard during fallback.
- Start quietly in the tray at Windows login. Remove periodic working-set trimming and forced garbage collection.
- Ship a Windows x64 setup EXE with the desktop runtime and offline dictionaries.

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
