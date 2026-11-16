using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace WordLens.Core
{
    public sealed class DictInfo
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string Name { get; set; }
        public string Language { get; set; }
        public string Type { get; set; }
        public string Source { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public string License { get; set; }
        public long EntryCount { get; set; }
        public long FormCount { get; set; }
        public bool IsBuiltin { get; set; }
        public bool Enabled { get; set; }
        public int Priority { get; set; }
    }

    public sealed class EntryRow
    {
        public long Id { get; set; }
        public string Headword { get; set; } = "";
        public string Fold { get; set; } = "";
        public string Phonetic { get; set; } = "";
        public string Translation { get; set; } = "";
        public string Definition { get; set; } = "";
        public string Pos { get; set; } = "";
        public string Tags { get; set; } = "";
        public string Exchange { get; set; } = "";
        public string Extra { get; set; } = "";
        public int Collins { get; set; }
        public int Oxford { get; set; }
        public int Bnc { get; set; }
        public int Frq { get; set; }
    }

    /// <summary>A single read-only SQLite dictionary (.wlcdb).</summary>
    public sealed class DictDb : IDisposable
    {
        private readonly object _gate = new object();
        private SqliteConnection _conn;
        private bool _disposed;

        public DictInfo Info { get; private set; }

        public DictDb(string path, bool builtin)
        {
            FilePath = path;
            _conn = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Cache=Private;Pooling=False");
            _conn.Open();
            try
            {
                // Small per-dictionary page cache (1 MB). Read-only indexed
                // files: this trades a little disk I/O for lower memory.
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA cache_size=-1024; PRAGMA mmap_size=0;";
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
            Info = new DictInfo
            {
                FilePath = path,
                FileName = System.IO.Path.GetFileName(path),
                IsBuiltin = builtin,
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                Language = "en",
                Type = builtin ? "builtin" : "user",
                EntryCount = 0,
                Enabled = true,
                Priority = 100
            };
            LoadMeta();
        }

        public string FilePath { get; }

        private void LoadMeta()
        {
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT key, value FROM meta";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            if (!r.IsDBNull(0))
                                meta[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
                    }
                }
            }
            catch { }
            if (meta.Count == 0) throw new InvalidOperationException("Not a WordLens dictionary (missing meta table)");
            Info.Name = Get(meta, "name", Info.Name);
            Info.Language = Get(meta, "language", "en");
            Info.Type = Get(meta, "type", Info.IsBuiltin ? "builtin" : "user");
            Info.Source = Get(meta, "source", "");
            Info.Description = Get(meta, "description", "");
            Info.Version = Get(meta, "version", "");
            Info.License = Get(meta, "license", "");
            Info.EntryCount = ParseLong(Get(meta, "entry_count", ""), 0);
            Info.FormCount = ParseLong(Get(meta, "form_count", ""), 0);
            if (Info.EntryCount <= 0)
            {
                try
                {
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT COUNT(1) FROM entries";
                        Info.EntryCount = Convert.ToInt64(cmd.ExecuteScalar());
                    }
                }
                catch { }
            }
        }

        private static string Get(Dictionary<string, string> meta, string key, string def)
        {
            return meta.TryGetValue(key, out string v) && !string.IsNullOrWhiteSpace(v) ? v : def;
        }

        private static long ParseLong(string s, long def)
        {
            return long.TryParse(s, out long v) ? v : def;
        }

        public List<EntryRow> FindByFold(string fold, int limit)
        {
            return Query("SELECT id, headword, fold, phonetic, translation, definition, pos, tags, exchange, extra, collins, oxford, bnc, frq FROM entries WHERE fold=$k ORDER BY collins DESC, oxford DESC LIMIT $n",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$k", fold);
                    cmd.Parameters.AddWithValue("$n", limit);
                });
        }

        public List<EntryRow> FindBySw(string sw, int limit)
        {
            return Query("SELECT id, headword, fold, phonetic, translation, definition, pos, tags, exchange, extra, collins, oxford, bnc, frq FROM entries WHERE sw=$k ORDER BY collins DESC, oxford DESC LIMIT $n",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$k", sw);
                    cmd.Parameters.AddWithValue("$n", limit);
                });
        }

        public string FindBase(string form)
        {
            try
            {
                lock (_gate)
                {
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT base FROM forms WHERE form=$f LIMIT 1";
                        cmd.Parameters.AddWithValue("$f", form);
                        object v = cmd.ExecuteScalar();
                        return v == null || v == DBNull.Value ? null : Convert.ToString(v);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("FindBase failed in " + Info.FileName + ": " + ex.Message);
                return null;
            }
        }

        public List<EntryRow> SearchPrefix(string prefix, int limit)
        {
            return Query("SELECT id, headword, fold, phonetic, translation, definition, pos, tags, exchange, extra, collins, oxford, bnc, frq FROM entries WHERE fold >= $p AND fold < $p2 ORDER BY fold LIMIT $n",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$p", prefix);
                    cmd.Parameters.AddWithValue("$p2", prefix + "\uffff");
                    cmd.Parameters.AddWithValue("$n", limit);
                });
        }

        private List<EntryRow> Query(string sql, Action<SqliteCommand> bind)
        {
            var list = new List<EntryRow>();
            if (_disposed) return list;
            try
            {
                lock (_gate)
                {
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.CommandText = sql;
                        bind(cmd);
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                list.Add(new EntryRow
                                {
                                    Id = r.GetInt64(0),
                                    Headword = r.IsDBNull(1) ? "" : r.GetString(1),
                                    Fold = r.IsDBNull(2) ? "" : r.GetString(2),
                                    Phonetic = r.IsDBNull(3) ? "" : r.GetString(3),
                                    Translation = r.IsDBNull(4) ? "" : r.GetString(4),
                                    Definition = r.IsDBNull(5) ? "" : r.GetString(5),
                                    Pos = r.IsDBNull(6) ? "" : r.GetString(6),
                                    Tags = r.IsDBNull(7) ? "" : r.GetString(7),
                                    Exchange = r.IsDBNull(8) ? "" : r.GetString(8),
                                    Extra = r.IsDBNull(9) ? "" : r.GetString(9),
                                    Collins = r.IsDBNull(10) ? 0 : r.GetInt32(10),
                                    Oxford = r.IsDBNull(11) ? 0 : r.GetInt32(11),
                                    Bnc = r.IsDBNull(12) ? 0 : r.GetInt32(12),
                                    Frq = r.IsDBNull(13) ? 0 : r.GetInt32(13)
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Dictionary query failed in " + Info.FileName + ": " + ex.Message);
            }
            return list;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _conn?.Dispose(); } catch { }
            _conn = null;
        }
    }
}
