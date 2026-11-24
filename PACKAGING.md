# WordLens 打包说明

本文档说明如何从源码生成交付物，以及安装包内部的格式。命令均在仓库根目录执行。

## 0. 前置条件

| 依赖 | 用途 | 说明 |
|---|---|---|
| .NET SDK 8 | 编译 / 发布 | 需在 `PATH` 中，或用 SDK 的完整路径调用 `dotnet.exe` |
| Python 3.10+ | 构建离线词典 | 只用标准库；`python tools\build_dicts.py` |
| Windows PowerShell 5.1 + .NET Framework 4.8 `csc` | 编译安装器外壳 | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` |
| 词典构建产物 | 打进安装包 | `data/build/builtin-en.wlcdb`、`data/build/builtin-zh.wlcdb` |

词典数据本身不入库（见 `.gitignore`），需要时用 `tools/fetch_data.ps1` 下载
原始数据、再运行 `tools/build_dicts.py` 重新生成，见 `data/README.md`。

## 1. 编译与发布

```powershell
dotnet build   src\WordLens\WordLens.csproj -c Debug
dotnet publish src\WordLens\WordLens.csproj -c Release -r win-x64 --self-contained true -o publish
```

`publish` 是自包含 win-x64 目录（约 150 MB，含运行时），安装器和便携安装
脚本都从这里取文件。

冒烟检查（不启动界面）：

```powershell
$env:WORDLENS_DICT_DIR = "$PWD\data\build"      # 开发树没有 ExeDir\dicts，用环境变量指向
& dotnet publish\WordLens.dll --query telescope --out $env:TEMP\q.json
& dotnet publish\WordLens.dll --diag --out $env:TEMP\d.json
```

`WORDLENS_HOME` 可把 `settings.json`/`user.db` 重定向到临时目录，避免污染
真实用户数据。

## 2. 单元测试

```powershell
dotnet run --project tests\StarDictTests\StarDictTests.csproj -c Release
```

期望输出 `Result: 89 passed, 0 failed.`（覆盖 .idx、.idx.gz、dictzip、zip、
64 位 idx、错误路径与释放行为）。该测试工程直接以
`<Compile Include="..\..\src\WordLens\Import\StarDict.cs">` 链接源文件，因此
测的就是正式代码。

## 3. 生成安装器

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

安装器从已发布的 `WordLens.dll` 读取版本。手动传入 `-Version` 时，脚本会校验它与程序一致。

脚本做的事：

1. 用 `csc` 编译 `installer\WordLensSetup.cs`（`/win32icon` 取应用图标）
   → 约 64 KB 外壳；
2. 把 `publish\*` 全量复制到临时 payload，追加 `dicts\*.wlcdb`、根目录的
   `README.md`/`README.en.md`/`REPORT.md`/`LICENSES.txt`/`LICENSE`/
   `THIRD-PARTY-NOTICES.md`/`CHANGELOG.md`/`uninstall.ps1`、以及 `docs\*.png`；
3. 压缩 payload ZIP（耗时数分钟，约 138 MB）；
4. 把 ZIP 追加到外壳之后，再写 16 字节尾标记 `WLZIP001` + int64 长度；
5. 输出 `dist\WordLens-<版本>-Setup.exe` 与 `dist\setup-sha256.txt`。

安装器运行时按尾标记定位 ZIP，解压到 `%LOCALAPPDATA%\WordLens`，写 `HKCU`
自启项与快捷方式，不需要管理员权限。尾标记格式：

```
[PE 外壳][payload ZIP][ "WLZIP001" ][ int64 little-endian ZIP 长度 ]
```

只改安装目录里的文档而不重新构建时，可以用程序改写 payload：读出 ZIP 到
文件、用 `System.IO.Compression.ZipFile` 的 `Update` 模式替换条目、再把
「外壳字节 + 新 ZIP + 尾标记 + 新长度」拼回 `Setup.exe`。`ZipArchive` 的
Update 模式会原样复制未改动条目的压缩数据，替换几个文档只需几百毫秒；改完
务必用 ZIP 校验确认所有条目的 CRC 都正常。

## 4. 生成源码包

源码包就是"仓库根目录去掉构建产物"的 ZIP。推荐直接用 git 生成，保证与
仓库当前提交一致：

```powershell
git archive --format=zip --output dist\WordLens-1.0.0-Source.zip HEAD
```

如果手动组装，需要排除：

```
bin/ obj/ publish/ dist/ packaging/ _recovery/ reports/
data/raw/ data/build/            # 已下载/生成的词典数据
tests/*.json tests/*.png         # 测试中间产物
```

应当包含：`src/`、`installer/`、`tests/StarDictTests/`、`tests/uihelper/`、
`tests/fixtures/`、`tools/`、`scripts/`、`docs/`、`README.md`、`README.en.md`、
`REPORT.md`、`PACKAGING.md`、`LICENSE`、`THIRD-PARTY-NOTICES.md`、
`CHANGELOG.md`、`.gitignore`、`LICENSES.txt`、`install.ps1`、`uninstall.ps1`。

参考命令：

```powershell
$tmp = Join-Path $env:TEMP 'wl-src'
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
$stage = Join-Path $tmp 'WordLens-1.0.0'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
robocopy . $stage /E /XD bin obj publish dist packaging _recovery reports data\raw data\build /XF *.pdb
Compress-Archive -Path "$stage\*" -DestinationPath dist\WordLens-1.0.0-Source.zip -CompressionLevel Optimal
```

注意：PowerShell 5.1 的 `Compress-Archive` 会把路径分隔符写成反斜杠，GitHub
和 Linux `unzip` 会把 `src\WordLens\...` 当成一个文件名；需要跨平台时用
`git archive` 或 `System.IO.Compression.ZipArchive` 自己写 `/` 分隔的条目
（`tools/build-source-zip.ps1` 就是这么做的）。

## 5. 校验与发布

```powershell
Get-FileHash dist\WordLens-1.0.0-Setup.exe  -Algorithm SHA256
Get-FileHash dist\WordLens-1.0.0-Source.zip -Algorithm SHA256
```

把两个哈希写进 `SHA256SUMS.txt` 与 Release 说明。发布规则：

- 安装包、源码包**只作为 Release asset**，不提交进仓库；`.gitignore` 必须
  在第一次 `git add` 之前就位。
- asset 文件名只用 ASCII：`WordLens-1.0.0-Setup.exe`、
  `WordLens-1.0.0-Source.zip`。带中文的名字上传后会被吞字。
- Release 正文用 `--notes-file` 写，不要命令行拼接；中英两节都要有，并列出
  本版更新日志（来源 `CHANGELOG.md`）。
- tag 用 `v1.0.0` 这种形式；tag 一旦发布不要移动，需要改内容就发新版本。

## 6. 装完后的自检

```powershell
Get-Content "$env:LOCALAPPDATA\WordLens\logs\$(Get-ChildItem "$env:LOCALAPPDATA\WordLens\logs" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty Name)" -Tail 8
```

期望看到：

```
Dictionary manager loaded 2 of 2 dictionaries
Hotkey registered: Ctrl+Alt+D
Hotkey registered: Ctrl+Shift+Space
Mouse hook installed
WordLens tray mode ready. enabled=True dicts=2 autostart=True
```

若出现 `Not a WordLens dictionary (missing meta table)`，说明 `dicts\*.wlcdb`
损坏或不完整，用 `tools\_check_dicts.py` 检查后从 `data/build` 重新复制。

## 7. 卸载

托盘右键退出 → 运行安装目录里的 `uninstall.exe`（或 `uninstall.ps1`），会删除
自启项、快捷方式和 `%LOCALAPPDATA%\WordLens` 整个目录。用户词典与设置都在该
目录内，一并清除。
