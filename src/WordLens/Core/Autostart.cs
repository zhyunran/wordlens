using System;
using Microsoft.Win32;

namespace WordLens.Core
{
    /// <summary>Per-user autostart via HKCU\...\Run (no admin required).</summary>
    public static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "WordLens";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    string v = k.GetValue(ValueName) as string;
                    if (string.IsNullOrWhiteSpace(v)) return false;
                    string path = v.Trim().Trim('"');
                    return string.Equals(path, AppPaths.ExePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey, true))
                {
                    if (k == null) return false;
                    if (enabled)
                        k.SetValue(ValueName, "\"" + AppPaths.ExePath + "\"", RegistryValueKind.String);
                    else if (k.GetValue(ValueName) != null)
                        k.DeleteValue(ValueName, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Autostart update failed", ex);
                return false;
            }
        }
    }
}
