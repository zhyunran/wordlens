// WordLensSetup.cs - self-contained per-user installer for WordLens.
//
// The installer executable is a tiny .NET Framework 4.8 WinForms stub.  The actual
// application payload is a ZIP archive appended to the end of this stub followed by a
// 16-byte footer:  "WLZIP001" + Int64 little-endian zip length.
//
// Build with:
//   csc /target:winexe /platform:anycpu /optimize+ /win32icon:... /win32manifest:installer.manifest
//       /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
//       /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll
//       /out:WordLensSetup.exe WordLensSetup.cs
// then append the payload ZIP + footer (see build-installer.ps1).
//
// Per-user only: installs into %LOCALAPPDATA%\WordLens, writes HKCU Run, creates
// shortcuts, registers an uninstall entry.  Never requests elevation.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("WordLens Setup")]
[assembly: AssemblyProduct("WordLens")]
[assembly: AssemblyDescription("WordLens per-user installer")]
[assembly: AssemblyCompany("WordLens")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace WordLensSetup
{
    internal static class Program
    {
        public const string AppName = "WordLens";
        public const string AppVersion = "1.0.0";
        private const string ZipMagic = "WLZIP001";

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool silent = HasArg(args, "--silent");
            bool noLaunch = HasArg(args, "--no-launch");

            try
            {
                if (HasArg(args, "--uninstall-run"))
                    return RunUninstallRun(args, silent);
                if (HasArg(args, "--uninstall"))
                    return RunUninstall(installDirOverride: null, silent: silent);
                if (!HasPayload() && Path.GetFileName(Application.ExecutablePath)
                        .StartsWith("uninstall", StringComparison.OrdinalIgnoreCase))
                    return RunUninstall(installDirOverride: null, silent: silent);

                string installDir = GetInstallDir();
                if (silent)
                {
                    Installer.Install(installDir, null, !noLaunch);
                    return 0;
                }
                using (var form = new InstallerForm(installDir, !noLaunch))
                {
                    Application.Run(form);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log("Fatal: " + ex);
                if (!silent)
                    MessageBox.Show("安装失败：\r\n\r\n" + ex.Message + "\r\n\r\n日志：" + LogPath,
                        "WordLens 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static bool HasArg(string[] args, string name)
        {
            if (args == null) return false;
            foreach (string a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string GetInstallDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        }

        public static bool HasPayload()
        {
            try
            {
                using (FileStream fs = File.OpenRead(Application.ExecutablePath))
                {
                    if (fs.Length < 16) return false;
                    fs.Seek(-16, SeekOrigin.End);
                    byte[] footer = new byte[16];
                    ReadExactly(fs, footer, 0, 16);
                    return Encoding.ASCII.GetString(footer, 0, 8) == ZipMagic;
                }
            }
            catch { return false; }
        }

        public static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            int read;
            while (count > 0 && (read = stream.Read(buffer, offset, count)) > 0)
            {
                offset += read;
                count -= read;
            }
        }

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + "\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        public static string LogPath
        {
            get { return Path.Combine(Path.GetTempPath(), "WordLensSetup.log"); }
        }

        // ------------------------------------------------------------------
        // uninstall
        // ------------------------------------------------------------------
        private static int RunUninstall(string installDirOverride, bool silent)
        {
            string installDir = string.IsNullOrEmpty(installDirOverride)
                ? Path.GetDirectoryName(Application.ExecutablePath)
                : installDirOverride;
            if (!silent)
            {
                DialogResult r = MessageBox.Show(
                    "确定要卸载 WordLens 吗？\r\n\r\n" +
                    "将删除程序文件、开始菜单/桌面快捷方式、开机自启项。\r\n" +
                    "已导入的用户词典和生词本也会一并删除。",
                    "卸载 WordLens", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return 0;
            }

            try
            {
                string tempExe = Path.Combine(Path.GetTempPath(),
                    "WordLensUninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(Application.ExecutablePath, tempExe, true);
                var psi = new ProcessStartInfo
                {
                    FileName = tempExe,
                    Arguments = "--uninstall-run \"" + installDir + "\"" + (silent ? " --silent" : ""),
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log("Uninstall bootstrap failed: " + ex);
                MessageBox.Show("无法启动卸载程序：" + ex.Message, "WordLens",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 0;
        }

        private static int RunUninstallRun(string[] args, bool silent)
        {
            string installDir = args.Length > 1 ? args[1] : GetInstallDir();
            Thread.Sleep(700);
            Installer.StopApp();
            Installer.RemoveShortcuts();
            Installer.RemoveAutostart();
            Installer.RemoveUninstallEntry();
            try
            {
                if (Directory.Exists(installDir)) Directory.Delete(installDir, true);
            }
            catch (Exception ex)
            {
                Log("Delete install dir failed: " + ex);
                try
                {
                    Process.Start(new ProcessStartInfo("cmd.exe",
                        "/c rd /s /q \"" + installDir + "\"")
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                }
                catch { }
            }
            if (!silent)
            {
                MessageBox.Show("WordLens 已卸载。", "WordLens",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            Installer.ScheduleSelfDelete(Application.ExecutablePath);
            return 0;
        }
    }

    internal interface IInstallProgress
    {
        void Report(int percent, string status);
    }

    internal sealed class InstallerForm : Form, IInstallProgress
    {
        private readonly string _installDir;
        private readonly bool _launch;
        private Label _status;
        private ProgressBar _bar;
        private Button _close;
        private CheckBox _launchCheck;

        public InstallerForm(string installDir, bool launch)
        {
            _installDir = installDir;
            _launch = launch;

            Text = "WordLens " + Program.AppVersion + " 安装程序";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 250);
            BackColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var title = new Label
            {
                Text = "WordLens 双击查词",
                Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 23, 42),
                AutoSize = true,
                Location = new Point(26, 22)
            };
            var subtitle = new Label
            {
                Text = "Windows 全局双击查词 · 音标 / 释义 / 发音 · 无需管理员权限",
                ForeColor = Color.FromArgb(71, 85, 105),
                AutoSize = true,
                Location = new Point(29, 63)
            };
            var pathLabel = new Label
            {
                Text = "安装到：" + installDir,
                ForeColor = Color.FromArgb(100, 116, 139),
                AutoSize = false,
                Width = 500,
                Height = 20,
                Location = new Point(29, 91)
            };
            _bar = new ProgressBar
            {
                Location = new Point(29, 125),
                Width = 500,
                Height = 16,
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = 100
            };
            _status = new Label
            {
                Text = "准备安装…",
                ForeColor = Color.FromArgb(71, 85, 105),
                AutoSize = false,
                Width = 500,
                Height = 38,
                Location = new Point(29, 149)
            };
            _launchCheck = new CheckBox
            {
                Text = "安装完成后启动 WordLens",
                Checked = launch,
                AutoSize = true,
                Location = new Point(29, 196),
                ForeColor = Color.FromArgb(51, 65, 85)
            };
            _close = new Button
            {
                Text = "关闭",
                Visible = false,
                Location = new Point(452, 200),
                Size = new Size(78, 28)
            };
            _close.Click += delegate { Close(); };

            Controls.Add(title);
            Controls.Add(subtitle);
            Controls.Add(pathLabel);
            Controls.Add(_bar);
            Controls.Add(_status);
            Controls.Add(_launchCheck);
            Controls.Add(_close);

            Shown += delegate { StartInstall(); };
        }

        private void StartInstall()
        {
            _status.Text = "正在安装，请稍候…";
            _launchCheck.Enabled = false;
            bool launch = _launchCheck.Checked;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Installer.Install(_installDir, this, launch);
                    BeginInvoke(new Action(OnInstallDone));
                }
                catch (Exception ex)
                {
                    Program.Log("Install failed: " + ex);
                    BeginInvoke(new Action(delegate { OnInstallFailed(ex); }));
                }
            });
        }

        public void Report(int percent, string status)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (IsDisposed) return;
                    _bar.Value = Math.Max(0, Math.Min(100, percent));
                    _status.Text = status;
                }));
            }
            catch { }
        }

        private void OnInstallDone()
        {
            _bar.Value = 100;
            _status.Text = "安装完成！";
            var timer = new System.Windows.Forms.Timer { Interval = 900 };
            timer.Tick += delegate { timer.Stop(); Close(); };
            timer.Start();
        }

        private void OnInstallFailed(Exception ex)
        {
            _status.Text = "安装失败：" + ex.Message;
            _status.ForeColor = Color.FromArgb(185, 28, 28);
            _launchCheck.Enabled = true;
            _close.Visible = true;
            MessageBox.Show(this, "安装失败：\r\n\r\n" + ex.Message + "\r\n\r\n日志：" + Program.LogPath,
                "WordLens 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal static class Installer
    {
        private const string ZipMagic = "WLZIP001";

        public static void Install(string installDir, IInstallProgress progress, bool launch)
        {
            Program.Log("Install start: dir=" + installDir + " launch=" + launch);
            StopApp();
            string tempZip = Path.Combine(Path.GetTempPath(),
                "WordLensPayload-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                if (progress != null) progress.Report(1, "正在读取安装数据…");
                ExtractPayloadToTemp(tempZip);
                if (progress != null) progress.Report(3, "正在解压程序文件…");
                ExtractZip(tempZip, installDir, progress);
                if (!File.Exists(Path.Combine(installDir, Program.AppName + ".exe")))
                    throw new InvalidDataException("安装包内容不完整：缺少 WordLens.exe");

                if (progress != null) progress.Report(96, "正在创建快捷方式…");
                CreateShortcuts(installDir);
                SetAutostart(installDir);
                WriteUninstallEntry(installDir);
                if (progress != null) progress.Report(99, "正在完成安装…");

                if (launch)
                {
                    try { LaunchApp(installDir); }
                    catch (Exception ex) { Program.Log("Launch failed: " + ex); }
                }
                Program.Log("Install done");
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }

        private static void ExtractPayloadToTemp(string tempZip)
        {
            string exe = Application.ExecutablePath;
            using (FileStream fs = File.OpenRead(exe))
            {
                if (fs.Length < 16) throw new InvalidDataException("安装包损坏（文件过小）");
                fs.Seek(-16, SeekOrigin.End);
                byte[] footer = new byte[16];
                Program.ReadExactly(fs, footer, 0, 16);
                if (Encoding.ASCII.GetString(footer, 0, 8) != ZipMagic)
                    throw new InvalidDataException("安装包损坏：未找到内置数据（请重新下载）");
                long zipLength = BitConverter.ToInt64(footer, 8);
                long zipStart = fs.Length - 16 - zipLength;
                if (zipLength <= 0 || zipStart < 0)
                    throw new InvalidDataException("安装包损坏：数据长度无效");
                fs.Seek(zipStart, SeekOrigin.Begin);
                using (FileStream outFs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None,
                           1024 * 1024, FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    long remain = zipLength;
                    while (remain > 0)
                    {
                        int want = (int)Math.Min(buffer.Length, remain);
                        int read = fs.Read(buffer, 0, want);
                        if (read <= 0) throw new InvalidDataException("读取安装数据失败");
                        outFs.Write(buffer, 0, read);
                        remain -= read;
                    }
                }
            }
        }

        private static void ExtractZip(string zipPath, string installDir, IInstallProgress progress)
        {
            Directory.CreateDirectory(installDir);
            using (FileStream fs = File.OpenRead(zipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                long total = 0;
                foreach (ZipArchiveEntry e in zip.Entries) total += e.Length;
                long done = 0;
                int lastPercent = -1;
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.IndexOf("..", StringComparison.Ordinal) >= 0) continue;
                    if (name.EndsWith("/", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(Path.Combine(installDir,
                            name.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)));
                        continue;
                    }
                    string dest = Path.Combine(installDir, name.Replace('/', Path.DirectorySeparatorChar));
                    string dir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    try { if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal); } catch { }
                    using (Stream input = entry.Open())
                    using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
                               256 * 1024, FileOptions.SequentialScan))
                    {
                        byte[] buffer = new byte[256 * 1024];
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, read);
                            done += read;
                            if (progress != null && total > 0)
                            {
                                int percent = (int)(3 + done * 92 / total);
                                if (percent != lastPercent)
                                {
                                    lastPercent = percent;
                                    progress.Report(percent, "正在解压：" + Path.GetFileName(dest) + "（" + percent + "%）");
                                }
                            }
                        }
                    }
                }
            }
        }

        public static void StopApp()
        {
            foreach (Process p in Process.GetProcessesByName(Program.AppName))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
            Thread.Sleep(400);
        }

        public static void LaunchApp(string installDir)
        {
            string exe = Path.Combine(installDir, Program.AppName + ".exe");
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = installDir,
                UseShellExecute = true
            };
            Process.Start(psi);
        }

        private static void CreateShortcuts(string installDir)
        {
            string exe = Path.Combine(installDir, Program.AppName + ".exe");
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            CreateShortcut(Path.Combine(desktop, Program.AppName + ".lnk"), exe, installDir);
            CreateShortcut(Path.Combine(programs, Program.AppName + ".lnk"), exe, installDir);
        }

        public static void CreateShortcut(string linkPath, string targetPath, string workingDir)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = Activator.CreateInstance(shellType);
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null,
                    shell, new object[] { linkPath });
                Type linkType = link.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { targetPath });
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { workingDir });
                linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link,
                    new object[] { targetPath + ",0" });
                linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "WordLens 双击查词" });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            catch (Exception ex) { Program.Log("Create shortcut failed: " + ex.Message); }
        }

        public static void RemoveShortcuts()
        {
            try
            {
                File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Program.AppName + ".lnk"));
            }
            catch { }
            try
            {
                File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Program.AppName + ".lnk"));
            }
            catch { }
        }

        private static void SetAutostart(string installDir)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (key != null)
                        key.SetValue(Program.AppName,
                            "\"" + Path.Combine(installDir, Program.AppName + ".exe") + "\"");
                }
            }
            catch (Exception ex) { Program.Log("Set autostart failed: " + ex.Message); }
        }

        public static void RemoveAutostart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null) key.DeleteValue(Program.AppName, false);
                }
            }
            catch { }
        }

        private static void WriteUninstallEntry(string installDir)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WordLens"))
                {
                    if (key == null) return;
                    key.SetValue("DisplayName", "WordLens 双击查词");
                    key.SetValue("DisplayVersion", Program.AppVersion);
                    key.SetValue("Publisher", "WordLens");
                    key.SetValue("InstallLocation", installDir);
                    key.SetValue("DisplayIcon",
                        Path.Combine(installDir, Program.AppName + ".exe") + ",0");
                    key.SetValue("UninstallString",
                        "\"" + Path.Combine(installDir, "uninstall.exe") + "\" --uninstall");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    try
                    {
                        long bytes = 0;
                        foreach (string file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
                            bytes += new FileInfo(file).Length;
                        key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Program.Log("Write uninstall entry failed: " + ex.Message); }
        }

        public static void RemoveUninstallEntry()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WordLens", false);
            }
            catch { }
        }

        public static void ScheduleSelfDelete(string exePath)
        {
            try
            {
                string cmd = "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + exePath + "\"";
                var psi = new ProcessStartInfo("cmd.exe", cmd)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
            }
            catch { }
        }
    }
}
