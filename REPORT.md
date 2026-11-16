# WordLens 测试报告与局限

> 版本 1.0.0 · 目标框架 net8.0-windows · 自包含 win-x64 · 最后核对 2026-11-04
>
> 本文记录 1.0.0 的实测结果、复测结论和已知局限。命令都在仓库根目录执行。

---

## 1. 测试环境

| 项 | 值 |
|---|---|
| 系统 | Windows 10 / 11 x64（当前测试机为 Windows 11） |
| 显示 | 2560 × 1600，125% 缩放 |
| 程序 | 自包含 .NET 8 win-x64，安装到 `%LOCALAPPDATA%\WordLens` |
| 词典 | 内置 ECDICT（135 MB）+ CC-CEDICT（51 MB）两个 SQLite 库 |
| 选区读取 | UI Automation `TextPattern` 优先；Edge PDF 走剪贴板保底 |

---

## 2. 功能实测

| 场景 | 结果 | 选区读取 | 耗时（双击→弹窗） |
|---|---|---|---|
| Edge 网页（HTML） | ✅ 弹出 quick，音标 /ˈkwɪk/ | UI Automation | 108 ms |
| Edge 自带 PDF 阅读器 | ✅ 弹出 telescope，音标 /ˈtɛɫəˌskoʊp/ | 剪贴板保底（已恢复原剪贴板） | 215–273 ms |
| Microsoft Word | ✅ 弹出 began，音标 /biˈɡæn/ | UI Automation | 7–13 ms |
| 记事本（RichEdit） | ✅ 弹出 January | UI Automation | 60 ms 级 |
| 中文词（Edge 网页） | ✅ 和 / hé / 英文释义 / CC-CEDICT | UI Automation | 12 ms |
| 中文「同学」 | ✅ to study at the same school / fellow student / classmate，并以「量词：位 / 个」显示量词 | 本地 SQLite | 14 ms |
| 全局快捷键 Ctrl+Alt+D | ✅ 保留选区后直接弹窗 | UI Automation | 11 ms |
| 输入查词 Ctrl+Shift+Space | ✅ 输入 `telescope` / `国际化` 实时显示完整释义 | 本地 SQLite | 21–34 ms |
| 弹窗「设置」按钮 | ✅ 弹窗右下角，点击打开设置菜单 | — | — |
| 输入查词「设置」按钮 | ✅ 搜索窗口右上角，点击打开设置菜单 | — | — |
| 收藏 / 复制 / 发音 | ✅ 生词本入库、剪贴板内容正确、SAPI 发声 | — | — |
| 设置 → 词典 → 导入 CSV | ✅ 成功导入并显示「成功导入：test-words（2 条）」 | — | — |
| StarDict 解析器单元测试 | ✅ 89 passed, 0 failed（含 `.idx.gz`、dictzip、zip） | — | — |
| 开机自启 | ✅ `HKCU` Run 已写入，进程由 `explorer.exe` 拉起 | — | — |

耗时范围来自多次重复测量（Release 构建）；首次查询会包含 SQLite 页缓存冷启动，
通常比稳态慢 5–10 ms。

截图见 `docs/`：

- `screenshot-web.png` — Edge 网页
- `screenshot-pdf.png` — Edge 自带 PDF
- `screenshot-word.png` — Microsoft Word
- `screenshot-chinese.png` — 中文词与量词
- `screenshot-search.png` — 输入查词
- `screenshot-settings-import.png` — 设置与词典导入

---

## 3. 安装、升级与卸载

| 场景 | 结果 |
|---|---|
| 界面模式安装 | ✅ 装到 `%LOCALAPPDATA%\WordLens`，耗时约 8 秒，无 UAC |
| 静默模式安装（`/silent`） | ✅ 返回 0，快捷方式、自启项、卸载项齐全 |
| 覆盖升级 | ✅ `settings.json`、`user.db`、`user-dicts/` 保留 |
| 卸载 | ✅ 程序目录、桌面/开始菜单快捷方式、`HKCU` 自启项、卸载注册项均清理干净 |
| 安装后首启 | ✅ 托盘图标出现，日志显示词典加载 2/2、两个快捷键注册成功、鼠标钩子安装成功 |

安装后的启动日志（节选）：

```
=== WordLens 1.0.0 starting ===
[INFO] Dictionary manager loaded 2 of 2 dictionaries
[INFO] Hotkey registered: Ctrl+Alt+D
[INFO] Hotkey registered: Ctrl+Shift+Space
[INFO] Mouse hook installed
[INFO] WordLens tray mode ready. enabled=True dicts=2 autostart=True
```

---

## 4. 词典导入复测

用 `tests/fixtures` 下的三种测试文件，通过 `WordLens.exe --import`：

| 文件 | 格式 | 结果 |
|---|---|---|
| `stardict-test.zip` | StarDict（zip：ifo/idx/dict） | ✅ 3 条 |
| `test-words.csv` | CSV 表格 | ✅ 2 条 |
| `test-words.json` | JSON 对象数组 | ✅ 1 条 |

导入产物落在 `user-dicts/*.wlcdb`，不触碰内置词典；停用、调优先级、删除均
在设置窗口验证通过。

### StarDict 解析器单元测试

```powershell
dotnet run --project tests\StarDictTests\StarDictTests.csproj -c Release
```

```
Result: 89 passed, 0 failed.
```

覆盖 `.idx` / `.idx.gz`（gzip 回退）/ dictzip 分块跨界词条 / zip 包 / 64 位 idx
自动识别 / 错误路径（缺文件、非 ifo、空索引、空路径）/ Dispose 后行为。该工程
用 `<Compile Include="..\..\src\WordLens\Import\StarDict.cs">` 直接链接正式源码，
因此测的是交付代码本身。

---

## 5. 已知局限与未验证项

1. **管理员权限窗口**（任务管理器、管理员 cmd）普通权限进程读不到选区 → 不弹窗。
   这是 Windows 的安全边界，本工具刻意不申请 UAC。
2. **PDF 依赖剪贴板保底**：Edge PDF 的 UI Automation 不暴露选区，因此走
   「`Ctrl+C` → 读 `CF_UNICODETEXT` → 还原纯文本」；不还原富格式。剪贴板被别的
   程序占用时会跳过（日志 `clipboard busy`），可重试。
3. **剪贴板安全模式**：保底期间写标记文件；若上次保底未正常收尾，下次启动
   自动降级为「仅 UIA」。
4. **标准 IPA 覆盖 83,424 条**英文词条，其余显示经规范化的 ECDICT 音标。
5. **离线、不做整句翻译**；选中超过 64 字符（可调 8–400）不弹窗。
6. **未验证项**：
   - 多显示器混合 DPI 下的弹窗定位；
   - 重启电脑后的登录自启（注册表项与拉起路径已确认，但没有做完整重启验证）；
   - 管理员权限下的 WordLens 对提权程序的选区读取。
7. **数据来源与许可**：ECDICT（MIT）、CC-CEDICT（CC BY-SA 4.0）、ipa-dict（MIT）。
   程序不联网。

---

## 6. 版本完整性说明

1.0.0 的源码在交付前经历过一次本机文件损坏事故，部分源文件从已发布的 Release
程序集反编译恢复。恢复后做了两层校验：

- **结构**：45 个类型与事故前程序集逐一对应，无缺失、无多余；其中
  `Settings`、`ClipboardCompat`、`ClipboardSnapshot` 三个类的反编译结果逐行相同。
- **行为**：新旧二进制跑同一批查询，输出 JSON 逐字节比较，只有 `elapsedMs`
  不同（`quick`、`telescope`、`began`、`perceived`、`同学`、`和`、`国际化`、
  `extraordinary`、`quikc` 共 9 组）。

因此上文 10 月 26–29 日的实测结论对当前源码仍然成立；唯一保留的来源差异是
`MouseHook.cs` 采用事故后改写的版本（事件队列实现），已重新编译并通过上表
全部场景。

---

*安装包与源码包的 SHA-256 见仓库 Release 页和 `SHA256SUMS.txt`（发布资产目录）。*
