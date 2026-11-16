using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WordLens.Core
{
    /// <summary>Per-user settings; serialised as JSON in the data directory.</summary>
    public sealed class AppSettings
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Master switch for double-click lookup (the tray "pause" toggle).</summary>
        public bool LookupOnDoubleClick { get; set; } = true;

        /// <summary>Delay between the double-click and the capture, so apps finish their own selection.</summary>
        public int LookupDelayMs { get; set; } = 150;

        /// <summary>Longest selection we are willing to look up; guards against dragging whole paragraphs.</summary>
        public int MaxSelectionChars { get; set; } = 64;

        public bool AutoSpeak { get; set; }

        public int TtsRate { get; set; }

        public int TtsVolume { get; set; } = 90;

        public string EnglishVoice { get; set; } = "";

        public string ChineseVoice { get; set; } = "";

        public double PopupWidth { get; set; } = 460;

        public double PopupMaxHeight { get; set; } = 440;

        public double FontScale { get; set; } = 1.0;

        public int CloseTimeoutSec { get; set; } = 30;

        public bool CloseOnOutsideClick { get; set; } = true;

        public bool ShowEnglishDefinition { get; set; } = true;

        public bool ShowTags { get; set; } = true;

        public bool HistoryEnabled { get; set; } = true;

        public int HistoryLimit { get; set; } = 500;

        public bool Autostart { get; set; } = true;

        public string HotkeyLookup { get; set; } = "Ctrl+Alt+D";

        public string SearchHotkey { get; set; } = "Ctrl+Shift+Space";

        /// <summary>
        /// When the Ctrl+C clipboard fallback may run: "off", "pdf-only" (default) or "always".
        /// Non-PDF apps expose their selection through UI Automation, so the fallback normally
        /// stays off there - it is the only path that touches the clipboard at all.
        /// </summary>
        public string ClipboardFallback { get; set; } = "pdf-only";

        public List<string> ExcludedProcesses { get; set; } = new List<string>();

        public Dictionary<string, DictionaryState> Dictionaries { get; set; } =
            new Dictionary<string, DictionaryState>(StringComparer.OrdinalIgnoreCase);

        public string Theme { get; set; } = "light";

        public bool FirstRunDone { get; set; }

        [JsonIgnore]
        public string FilePath => AppPaths.SettingsFile;

        public static AppSettings Load()
        {
            try
            {
                AppPaths.EnsureCreated();
                if (File.Exists(AppPaths.SettingsFile))
                {
                    AppSettings settings = JsonSerializer.Deserialize<AppSettings>(
                        File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8), JsonOptions());
                    if (settings != null)
                    {
                        settings.Normalize();
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to load settings, using defaults", ex);
                try
                {
                    if (File.Exists(AppPaths.SettingsFile))
                        File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bad", true);
                }
                catch { }
            }
            AppSettings defaults = new AppSettings();
            defaults.Normalize();
            return defaults;
        }

        public void Save()
        {
            try
            {
                Normalize();
                AppPaths.EnsureCreated();
                string json = JsonSerializer.Serialize(this, JsonOptions());
                string tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile);
                File.Move(tmp, AppPaths.SettingsFile);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to save settings", ex);
            }
        }

        /// <summary>Clamps every numeric field into range and repairs null collections.</summary>
        public void Normalize()
        {
            if (LookupDelayMs < 0) LookupDelayMs = 0;
            if (LookupDelayMs > 2000) LookupDelayMs = 2000;
            if (MaxSelectionChars < 8) MaxSelectionChars = 8;
            if (MaxSelectionChars > 400) MaxSelectionChars = 400;
            if (CloseTimeoutSec < 3) CloseTimeoutSec = 3;
            if (CloseTimeoutSec > 600) CloseTimeoutSec = 600;
            if (PopupWidth < 300) PopupWidth = 300;
            if (PopupWidth > 900) PopupWidth = 900;
            if (PopupMaxHeight < 200) PopupMaxHeight = 200;
            if (PopupMaxHeight > 1000) PopupMaxHeight = 1000;
            if (FontScale < 0.75) FontScale = 0.75;
            if (FontScale > 1.6) FontScale = 1.6;
            if (TtsRate < -10) TtsRate = -10;
            if (TtsRate > 10) TtsRate = 10;
            if (TtsVolume < 0) TtsVolume = 0;
            if (TtsVolume > 100) TtsVolume = 100;
            if (HistoryLimit < 0) HistoryLimit = 0;

            if (Dictionaries == null)
                Dictionaries = new Dictionary<string, DictionaryState>(StringComparer.OrdinalIgnoreCase);
            if (ExcludedProcesses == null) ExcludedProcesses = new List<string>();

            if (HotkeyLookup == null) HotkeyLookup = "";
            if (SearchHotkey == null) SearchHotkey = "";

            if (string.IsNullOrWhiteSpace(ClipboardFallback)) ClipboardFallback = "pdf-only";
            ClipboardFallback = ClipboardFallback.Trim().ToLowerInvariant();
            if (ClipboardFallback != "off" && ClipboardFallback != "pdf-only" && ClipboardFallback != "always")
                ClipboardFallback = "pdf-only";
        }

        /// <summary>Enabled/priority state for one dictionary file, created on first use.</summary>
        public DictionaryState GetDictState(string fileName)
        {
            if (Dictionaries == null)
                Dictionaries = new Dictionary<string, DictionaryState>(StringComparer.OrdinalIgnoreCase);
            if (!Dictionaries.TryGetValue(fileName, out DictionaryState state))
            {
                state = new DictionaryState { Enabled = true, Priority = 100 };
                Dictionaries[fileName] = state;
            }
            return state;
        }

        private static JsonSerializerOptions JsonOptions()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never
            };
        }
    }

    /// <summary>Per-dictionary enabled flag and lookup priority.</summary>
    public sealed class DictionaryState
    {
        public bool Enabled { get; set; } = true;

        public int Priority { get; set; } = 100;
    }
}
