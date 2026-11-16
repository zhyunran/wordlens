using System;
using System.IO;

namespace WordLens.Core
{
    /// <summary>Well-known filesystem locations. All per-user, no admin rights needed.</summary>
    public static class AppPaths
    {
        public static string ExeDir
        {
            get
            {
                string dir = AppContext.BaseDirectory;
                return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        public static string ExePath
        {
            get
            {
                try
                {
                    string p = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(p)) return p;
                }
                catch { }
                return Path.Combine(ExeDir, "WordLens.exe");
            }
        }

        public static string DataDir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("WORDLENS_HOME");
                if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WordLens");
            }
        }

        public static string BuiltinDictDir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("WORDLENS_DICT_DIR");
                if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
                return Path.Combine(ExeDir, "dicts");
            }
        }

        public static string UserDictDir => Path.Combine(DataDir, "user-dicts");
        public static string ClipboardFallbackMarker => Path.Combine(DataDir, "clipboard-fallback.pending");
        public static string SettingsFile => Path.Combine(DataDir, "settings.json");
        public static string UserDbFile => Path.Combine(DataDir, "user.db");
        public static string LogDir => Path.Combine(DataDir, "logs");
        public static string TempDir => Path.Combine(DataDir, "temp");
        public static string InstalledMarker => Path.Combine(DataDir, ".installed");

        public static void EnsureCreated()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(UserDictDir);
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(TempDir);
        }
    }
}
