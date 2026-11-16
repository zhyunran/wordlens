using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace WordLens.Core
{
    public sealed class HistoryItem
    {
        public string Word { get; set; }
        public string Display { get; set; }
        public long Timestamp { get; set; }
        public int Count { get; set; }
        public string Source { get; set; }
    }

    public sealed class FavoriteItem
    {
        public string Word { get; set; }
        public string Display { get; set; }
        public string Dict { get; set; }
        public string Phonetic { get; set; }
        public string Translation { get; set; }
        public long Added { get; set; }
    }

    /// <summary>Favorites and lookup history stored in %LOCALAPPDATA%\WordLens\user.db.</summary>
    public sealed class HistoryStore : IDisposable
    {
        private readonly object _gate = new object();
        private SqliteConnection _conn;

        public HistoryStore()
        {
        }

        private SqliteConnection Conn
        {
            get
            {
                if (_conn != null) return _conn;
                AppPaths.EnsureCreated();
                _conn = new SqliteConnection("Data Source=" + AppPaths.UserDbFile);
                _conn.Open();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS favorites(
  word TEXT PRIMARY KEY, display TEXT, dict TEXT, phonetic TEXT, translation TEXT, added INTEGER);
CREATE TABLE IF NOT EXISTS history(
  word TEXT PRIMARY KEY, display TEXT, ts INTEGER, count INTEGER, source TEXT);";
                    cmd.ExecuteNonQuery();
                }
                return _conn;
            }
        }

        public void AddHistory(string display, string source)
        {
            if (string.IsNullOrWhiteSpace(display)) return;
            try
            {
                lock (_gate)
                {
                    using (var cmd = Conn.CreateCommand())
                    {
                        cmd.CommandText = @"
INSERT INTO history(word, display, ts, count, source) VALUES($w,$d,$t,1,$s)
ON CONFLICT(word) DO UPDATE SET ts=$t, count=count+1, display=$d, source=$s;";
                        cmd.Parameters.AddWithValue("$w", display.ToLowerInvariant());
                        cmd.Parameters.AddWithValue("$d", display);
                        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        cmd.Parameters.AddWithValue("$s", source ?? "");
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex) { Log.Warn("AddHistory failed: " + ex.Message); }
        }

        public List<HistoryItem> ListHistory(int limit)
        {
            var list = new List<HistoryItem>();
            try
            {
                lock (_gate)
                {
                    using (var cmd = Conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT word, display, ts, count, source FROM history ORDER BY ts DESC LIMIT $n";
                        cmd.Parameters.AddWithValue("$n", Math.Max(1, limit));
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                list.Add(new HistoryItem
                                {
                                    Word = r.IsDBNull(0) ? "" : r.GetString(0),
                                    Display = r.IsDBNull(1) ? "" : r.GetString(1),
                                    Timestamp = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                                    Count = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                                    Source = r.IsDBNull(4) ? "" : r.GetString(4)
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("ListHistory failed: " + ex.Message); }
            return list;
        }

        public void ClearHistory()
        {
            try
            {
                lock (_gate)
                {
                    using (var cmd = Conn.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM history";
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex) { Log.Warn("ClearHistory failed: " + ex.Message); }
        }

        public bool IsFavorite(string display)
        {
            if (string.IsNullOrWhiteSpace(display)) return false;
            try
            {
                lock (_gate)
                {
                    using (var cmd = Conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT COUNT(1) FROM favorites WHERE word=$w";
                        cmd.Parameters.AddWithValue("$w", display.ToLowerInvariant());
                        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
                    }
                }
            }
            catch { return false; }
        }

        public bool ToggleFavorite(string display, string dict, string phonetic, string translation)
        {
            if (string.IsNullOrWhiteSpace(display)) return false;
            try
            {
                lock (_gate)
                {
                    string key = display.ToLowerInvariant();
                    bool exists;
                    using (var cmd = Conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT COUNT(1) FROM favorites WHERE word=$w";
                        cmd.Parameters.AddWithValue("$w", key);
                        exists = Convert.ToInt64(cmd.ExecuteScalar()) > 0;
                    }
                    using (var cmd = Conn.CreateCommand())
                    {
                        if (exists)
                        {
                            cmd.CommandText = "DELETE FROM favorites WHERE word=$w";
                            cmd.Parameters.AddWithValue("$w", key);
                        }
                        else
                        {
                            cmd.CommandText = @"INSERT INTO favorites(word, display, dict, phonetic, translation, added)
                                                VALUES($w,$d,$k,$p,$t,$a)";
                            cmd.Parameters.AddWithValue("$w", key);
                            cmd.Parameters.AddWithValue("$d", display);
                            cmd.Parameters.AddWithValue("$k", dict ?? "");
                            cmd.Parameters.AddWithValue("$p", phonetic ?? "");
                            cmd.Parameters.AddWithValue("$t", translation ?? "");
                            cmd.Parameters.AddWithValue("$a", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        }
                        cmd.ExecuteNonQuery();
                    }
                    return !exists;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("ToggleFavorite failed: " + ex.Message);
                return false;
            }
        }

        public List<FavoriteItem> ListFavorites()
        {
            var list = new List<FavoriteItem>();
            try
            {
                lock (_gate)
                {
                    using (var cmd = Conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT word, display, dict, phonetic, translation, added FROM favorites ORDER BY added DESC";
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                list.Add(new FavoriteItem
                                {
                                    Word = r.IsDBNull(0) ? "" : r.GetString(0),
                                    Display = r.IsDBNull(1) ? "" : r.GetString(1),
                                    Dict = r.IsDBNull(2) ? "" : r.GetString(2),
                                    Phonetic = r.IsDBNull(3) ? "" : r.GetString(3),
                                    Translation = r.IsDBNull(4) ? "" : r.GetString(4),
                                    Added = r.IsDBNull(5) ? 0 : r.GetInt64(5)
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("ListFavorites failed: " + ex.Message); }
            return list;
        }

        public void RemoveFavorite(string display)
        {
            try
            {
                lock (_gate)
                {
                    using (var cmd = Conn.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM favorites WHERE word=$w";
                        cmd.Parameters.AddWithValue("$w", (display ?? "").ToLowerInvariant());
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex) { Log.Warn("RemoveFavorite failed: " + ex.Message); }
        }

        public void Dispose()
        {
            try { _conn?.Dispose(); } catch { }
            _conn = null;
        }
    }
}
