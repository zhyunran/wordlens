using System;
using Microsoft.Win32;

namespace WordLens.Core
{
    /// <summary>Per-user autostart via HKCU\...\Run (no admin required).</summary>
    public static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "WordLens";
        public const string StartupArgument = "--startup";

        private static string StartupCommand => "\"" + AppPaths.ExePath + "\" " + StartupArgument;

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    string v = k.GetValue(ValueName) as string;
                    return string.Equals(v, StartupCommand, StringComparison.OrdinalIgnoreCase);
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
                    string previous = k.GetValue(ValueName) as string;
                    if (enabled && !string.Equals(previous, StartupCommand, StringComparison.OrdinalIgnoreCase))
                        k.SetValue(ValueName, StartupCommand, RegistryValueKind.String);
                    else if (!enabled && previous != null)
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
