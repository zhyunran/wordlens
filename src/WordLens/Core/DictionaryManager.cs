using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WordLens.Core
{
    /// <summary>Owns the set of open dictionaries (built-in folder + user folder) and their state.</summary>
    public sealed class DictionaryManager : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly object _gate = new object();
        private readonly List<DictDb> _open = new List<DictDb>();
        private readonly List<DictInfo> _infos = new List<DictInfo>();
        private bool _disposed;

        public DictionaryManager(AppSettings settings)
        {
            _settings = settings;
        }

        public IReadOnlyList<DictInfo> Infos
        {
            get { lock (_gate) return _infos.ToList(); }
        }

        public void Reload()
        {
            lock (_gate)
            {
                foreach (DictDb db in _open) db.Dispose();
                _open.Clear();
                _infos.Clear();
                ScanDir(AppPaths.BuiltinDictDir, true);
                ScanDir(AppPaths.UserDictDir, false);
                _open.Sort((a, b) => b.Info.Priority.CompareTo(a.Info.Priority));
                Log.Info("Dictionary manager loaded " + _open.Count + " of " + _infos.Count + " dictionaries");
            }
        }

        private void ScanDir(string dir, bool builtin)
        {
            if (!Directory.Exists(dir)) return;
            string[] files;
            try { files = Directory.GetFiles(dir, "*.wlcdb"); }
            catch { return; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                DictDb db = null;
                try
                {
                    db = new DictDb(file, builtin);
                    string stateKey = Path.GetFileName(file);
                    if (!_settings.Dictionaries.TryGetValue(stateKey, out DictionaryState state))
                    {
                        state = new DictionaryState { Enabled = true, Priority = db.Info.Priority };
                        _settings.Dictionaries[stateKey] = state;
                    }
                    db.Info.Enabled = state.Enabled;
                    db.Info.Priority = state.Priority;
                    _open.Add(db);
                    _infos.Add(db.Info);
                }
                catch (Exception ex)
                {
                    try { db?.Dispose(); } catch { }
                    Log.Warn("Skipping dictionary " + file + ": " + ex.Message);
                }
            }
        }

        public List<DictDb> EnabledDictionaries(bool chinese)
        {
            lock (_gate)
            {
                var list = _open.Where(d => d.Info.Enabled).ToList();
                // language preference first, then priority
                list.Sort((a, b) =>
                {
                    int la = LanguageScore(a.Info.Language, chinese);
                    int lb = LanguageScore(b.Info.Language, chinese);
                    if (la != lb) return lb.CompareTo(la);
                    int p = b.Info.Priority.CompareTo(a.Info.Priority);
                    if (p != 0) return p;
                    return string.Compare(a.Info.Name, b.Info.Name, StringComparison.OrdinalIgnoreCase);
                });
                return list;
            }
        }

        private static int LanguageScore(string lang, bool chinese)
        {
            if (string.IsNullOrEmpty(lang)) return 1;
            bool isZh = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            if (chinese) return isZh ? 2 : 1;
            return isZh ? 1 : 2;
        }

        public DictInfo FindInfo(string filePath)
        {
            lock (_gate)
            {
                return _infos.FirstOrDefault(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            }
        }

        public void SetEnabled(string filePath, bool enabled)
        {
            lock (_gate)
            {
                DictInfo info = _infos.FirstOrDefault(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                if (info == null) return;
                info.Enabled = enabled;
                DictionaryState st = _settings.GetDictState(Path.GetFileName(filePath));
                st.Enabled = enabled;
                _settings.Save();
            }
        }

        public void SetPriority(string filePath, int priority)
        {
            lock (_gate)
            {
                DictInfo info = _infos.FirstOrDefault(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                if (info == null) return;
                info.Priority = priority;
                DictionaryState st = _settings.GetDictState(Path.GetFileName(filePath));
                st.Priority = priority;
                _settings.Save();
                _open.Sort((a, b) => b.Info.Priority.CompareTo(a.Info.Priority));
            }
        }

        public List<string> DeleteUserDict(string filePath)
        {
            var removed = new List<string>();
            lock (_gate)
            {
                DictInfo info = _infos.FirstOrDefault(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                if (info == null || info.IsBuiltin) return removed;
                try
                {
                    DictDb db = _open.FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                    if (db != null) { db.Dispose(); _open.Remove(db); }
                    _infos.Remove(info);
                    string settingsKey = Path.GetFileName(filePath);
                    _settings.Dictionaries.Remove(settingsKey);
                    _settings.Save();
                    File.Delete(filePath);
                    removed.Add(filePath);
                    Log.Info("Deleted user dictionary " + filePath);
                }
                catch (Exception ex)
                {
                    Log.Error("Delete dictionary failed", ex);
                }
            }
            return removed;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_gate)
            {
                foreach (DictDb db in _open) db.Dispose();
                _open.Clear();
            }
        }
    }
}
