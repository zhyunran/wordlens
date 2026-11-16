using System;
using System.IO;
using System.Text;
using System.Threading;

namespace WordLens.Core
{
    /// <summary>Simple thread-safe rolling file logger.</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static bool _initialized;
        private static int _maxBytes = 8 * 1024 * 1024;

        public static void Init()
        {
            try
            {
                AppPaths.EnsureCreated();
                _initialized = true;
                Info("=== WordLens " + AppInfo.Version + " starting, pid=" + Environment.ProcessId + ", data=" + AppPaths.DataDir + " ===");
            }
            catch { }
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        public static void Error(string message, Exception ex) =>
            Write("ERROR", message + Environment.NewLine + (ex == null ? "" : ex.ToString()));

        private static void Write(string level, string message)
        {
            if (!_initialized) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " +
                          "[" + Thread.CurrentThread.ManagedThreadId + "] " + message;
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.LogDir);
                    string file = Path.Combine(AppPaths.LogDir, "wordlens-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > _maxBytes)
                    {
                        string bak = file + ".1";
                        try { if (File.Exists(bak)) File.Delete(bak); File.Move(file, bak); } catch { }
                    }
                    File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch { }
            }
        }

        public static void OpenLogFolder()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = AppPaths.LogDir,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    public static class AppInfo
    {
        public const string Name = "WordLens";
        public const string Version = "1.0.0";
        public const string DisplayName = "WordLens 双击查词";
        public const string ProjectUrl = "https://github.com/skywind3000/ECDICT";
        public const string CedictUrl = "https://www.mdbg.net/chinese/dictionary?page=cc-cedict";
    }
}
