using System;
using System.Collections.Generic;
using System.Threading;

namespace WordLens.Core
{
    public sealed class VoiceInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public int Language { get; set; }
        public override string ToString() => Description + (Language == 0 ? "" : " (" + Language + ")");
    }

    /// <summary>
    /// Pronunciation through the built-in Windows SAPI voices (Microsoft Zira/David/Huihui).
    /// Must be used from the WPF (STA) UI thread.
    /// </summary>
    public sealed class TtsService : IDisposable
    {
        private readonly AppSettings _settings;
        private dynamic _voice;
        private List<VoiceInfo> _voices;
        private string _selectedVoiceName;
        private readonly object _gate = new object();

        public TtsService(AppSettings settings)
        {
            _settings = settings;
        }

        public IReadOnlyList<VoiceInfo> Voices
        {
            get
            {
                EnsureVoice();
                return _voices ?? new List<VoiceInfo>();
            }
        }

        private void EnsureVoice()
        {
            if (_voice != null) return;
            lock (_gate)
            {
                if (_voice != null) return;
                try
                {
                    Type t = Type.GetTypeFromProgID("SAPI.SpVoice");
                    if (t == null) throw new InvalidOperationException("SAPI.SpVoice not available");
                    _voice = Activator.CreateInstance(t);
                    _voices = new List<VoiceInfo>();
                    dynamic tokens = _voice.GetVoices();
                    int count = (int)tokens.Count;
                    for (int i = 0; i < count; i++)
                    {
                        dynamic tok = tokens.Item(i);
                        string desc = "";
                        int lang = 0;
                        try { desc = (string)tok.GetDescription(); } catch { }
                        try { object a = tok.GetAttribute("Language"); lang = Convert.ToInt32(a); } catch { }
                        _voices.Add(new VoiceInfo { Name = desc, Description = desc, Language = lang });
                    }
                    Log.Info("SAPI voices: " + string.Join("; ", _voices.ConvertAll(v => v.Description + "[" + v.Language + "]")));
                }
                catch (Exception ex)
                {
                    Log.Error("SAPI init failed", ex);
                    _voice = null;
                    _voices = new List<VoiceInfo>();
                }
            }
        }

        public void Speak(string text, bool chinese)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                EnsureVoice();
                if (_voice == null) return;
                dynamic v = _voice;
                v.Rate = Math.Max(-10, Math.Min(10, _settings.TtsRate));
                v.Volume = Math.Max(0, Math.Min(100, _settings.TtsVolume));
                string wanted = chinese ? _settings.ChineseVoice : _settings.EnglishVoice;
                if (string.IsNullOrWhiteSpace(wanted))
                    SelectAutoVoice(chinese);
                else
                    SelectVoice(wanted, chinese);
                // 1 = SVSFlagsAsync, 2 = SVSFPurgeBeforeSpeak
                v.Speak(text, 3);
            }
            catch (Exception ex)
            {
                Log.Error("SAPI speak failed", ex);
            }
        }

        public void Stop()
        {
            try
            {
                if (_voice == null) return;
                _voice.Speak("", 2);
            }
            catch { }
        }

        private void SelectVoice(string wanted, bool chinese)
        {
            if (string.IsNullOrWhiteSpace(wanted) || string.Equals(_selectedVoiceName, wanted, StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                dynamic tokens = _voice.GetVoices();
                int count = (int)tokens.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic tok = tokens.Item(i);
                    string desc = "";
                    try { desc = (string)tok.GetDescription(); } catch { }
                    if (desc.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _voice.Voice = tok;
                        _selectedVoiceName = wanted;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("SelectVoice failed: " + ex.Message);
            }
        }

        private int _autoLanguage;
        private void SelectAutoVoice(bool chinese)
        {
            int want = chinese ? 1 : 0;
            if (_autoLanguage == want && _voice != null) return;
            try
            {
                dynamic tokens = _voice.GetVoices();
                int count = (int)tokens.Count;
                dynamic best = null;
                for (int i = 0; i < count; i++)
                {
                    dynamic tok = tokens.Item(i);
                    int lang = 0;
                    try { lang = Convert.ToInt32(tok.GetAttribute("Language")); } catch { }
                    bool isZh = lang == 804 || lang == 0x0804 || lang == 0x0404 || lang == 0x0C04 || lang == 0x1004;
                    bool isEn = lang == 409 || lang == 0x0409 || lang == 0x0809 || lang == 0x0C09;
                    if (chinese && isZh) { best = tok; break; }
                    if (!chinese && isEn) { best = tok; break; }
                }
                if (best != null)
                {
                    _voice.Voice = best;
                    _autoLanguage = want;
                }
            }
            catch (Exception ex) { Log.Warn("SelectAutoVoice failed: " + ex.Message); }
        }

        public string PickAutoVoice(bool chinese)
        {
            // The recognizer's default voice is used unless the user configured one.
            if (chinese && !string.IsNullOrWhiteSpace(_settings.ChineseVoice)) return _settings.ChineseVoice;
            if (!chinese && !string.IsNullOrWhiteSpace(_settings.EnglishVoice)) return _settings.EnglishVoice;
            return null;
        }

        public void Dispose()
        {
            try
            {
                if (_voice != null) _voice.Speak("", 2);
            }
            catch { }
            _voice = null;
        }
    }
}
