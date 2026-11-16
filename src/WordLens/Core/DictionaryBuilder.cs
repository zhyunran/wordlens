using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WordLens.Core
{
    /// <summary>Creates a .wlcdb (SQLite) dictionary file.</summary>
    public sealed class DictionaryBuilder : IDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly SqliteCommand _entryCmd;
        private readonly SqliteCommand _formCmd;
        private readonly SqliteTransaction _tx;
        private readonly Dictionary<string, string> _meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private long _entries;
        private long _forms;
        private bool _committed;

        public DictionaryBuilder(string path)
        {
            AppPaths.EnsureCreated();
            if (File.Exists(path)) File.Delete(path);
            _conn = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            _conn.Open();
            Exec("PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-32000;");
            Exec(@"
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE entries(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  headword TEXT NOT NULL, fold TEXT NOT NULL, sw TEXT,
  phonetic TEXT, translation TEXT, definition TEXT, pos TEXT, tags TEXT,
  exchange TEXT, extra TEXT,
  collins INTEGER DEFAULT 0, oxford INTEGER DEFAULT 0, bnc INTEGER DEFAULT 0, frq INTEGER DEFAULT 0);
CREATE TABLE forms(form TEXT PRIMARY KEY, base TEXT);");
            _tx = _conn.BeginTransaction();
            _entryCmd = _conn.CreateCommand();
            _entryCmd.Transaction = _tx;
            _entryCmd.CommandText = @"INSERT INTO entries(headword, fold, sw, phonetic, translation, definition, pos, tags, exchange, extra, collins, oxford, bnc, frq)
VALUES($h,$f,$s,$p,$t,$d,$pos,$tags,$ex,$extra,$col,$ox,$bnc,$frq)";
            _entryCmd.Parameters.Add("$h", SqliteType.Text);
            _entryCmd.Parameters.Add("$f", SqliteType.Text);
            _entryCmd.Parameters.Add("$s", SqliteType.Text);
            _entryCmd.Parameters.Add("$p", SqliteType.Text);
            _entryCmd.Parameters.Add("$t", SqliteType.Text);
            _entryCmd.Parameters.Add("$d", SqliteType.Text);
            _entryCmd.Parameters.Add("$pos", SqliteType.Text);
            _entryCmd.Parameters.Add("$tags", SqliteType.Text);
            _entryCmd.Parameters.Add("$ex", SqliteType.Text);
            _entryCmd.Parameters.Add("$extra", SqliteType.Text);
            _entryCmd.Parameters.Add("$col", SqliteType.Integer);
            _entryCmd.Parameters.Add("$ox", SqliteType.Integer);
            _entryCmd.Parameters.Add("$bnc", SqliteType.Integer);
            _entryCmd.Parameters.Add("$frq", SqliteType.Integer);
            _entryCmd.Prepare();
            _formCmd = _conn.CreateCommand();
            _formCmd.Transaction = _tx;
            _formCmd.CommandText = "INSERT OR REPLACE INTO forms(form, base) VALUES($f,$b)";
            _formCmd.Parameters.Add("$f", SqliteType.Text);
            _formCmd.Parameters.Add("$b", SqliteType.Text);
            _formCmd.Prepare();
        }

        public long EntryCount => _entries;

        public void SetMeta(string key, string value)
        {
            if (!string.IsNullOrEmpty(key)) _meta[key] = value ?? "";
        }

        public void AddEntry(string headword, string phonetic, string translation, string definition,
            string pos = null, string tags = null, string exchange = null, string extra = null,
            int collins = 0, int oxford = 0, int bnc = 0, int frq = 0)
        {
            if (string.IsNullOrWhiteSpace(headword)) return;
            string h = headword.Trim();
            if (h.Length == 0) return;
            string fold = h.ToLowerInvariant();
            string sw = StripWord(h);
            _entryCmd.Parameters["$h"].Value = h;
            _entryCmd.Parameters["$f"].Value = fold;
            _entryCmd.Parameters["$s"].Value = sw;
            _entryCmd.Parameters["$p"].Value = phonetic ?? "";
            _entryCmd.Parameters["$t"].Value = translation ?? "";
            _entryCmd.Parameters["$d"].Value = definition ?? "";
            _entryCmd.Parameters["$pos"].Value = pos ?? "";
            _entryCmd.Parameters["$tags"].Value = tags ?? "";
            _entryCmd.Parameters["$ex"].Value = exchange ?? "";
            _entryCmd.Parameters["$extra"].Value = extra ?? "";
            _entryCmd.Parameters["$col"].Value = collins;
            _entryCmd.Parameters["$ox"].Value = oxford;
            _entryCmd.Parameters["$bnc"].Value = bnc;
            _entryCmd.Parameters["$frq"].Value = frq;
            _entryCmd.ExecuteNonQuery();
            _entries++;
        }

        public void AddForm(string form, string baseWord)
        {
            if (string.IsNullOrWhiteSpace(form) || string.IsNullOrWhiteSpace(baseWord)) return;
            string f = form.Trim().ToLowerInvariant();
            string b = baseWord.Trim().ToLowerInvariant();
            if (f.Length == 0 || b.Length == 0 || f == b) return;
            _formCmd.Parameters["$f"].Value = f;
            _formCmd.Parameters["$b"].Value = b;
            _formCmd.ExecuteNonQuery();
            _forms++;
        }

        public void Commit()
        {
            if (_committed) return;
            _committed = true;
            try
            {
                _tx.Commit();
                foreach (var kv in _meta)
                {
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES($k,$v)";
                        cmd.Parameters.AddWithValue("$k", kv.Key);
                        cmd.Parameters.AddWithValue("$v", kv.Value ?? "");
                        cmd.ExecuteNonQuery();
                    }
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES('entry_count',$v)";
                    cmd.Parameters.AddWithValue("$v", _entries.ToString());
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES('form_count',$v)";
                    cmd.Parameters.AddWithValue("$v", _forms.ToString());
                    cmd.ExecuteNonQuery();
                }
                Exec("CREATE INDEX idx_entries_fold ON entries(fold); CREATE INDEX idx_entries_sw ON entries(sw); CREATE INDEX idx_forms_form ON forms(form); ANALYZE;");
            }
            finally
            {
                try { _conn.Dispose(); } catch { }
            }
        }

        private void Exec(string sql)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        public static string StripWord(string w)
        {
            var sb = new StringBuilder(w.Length);
            foreach (char c in w)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public void Dispose()
        {
            if (!_committed)
            {
                try { _tx.Rollback(); } catch { }
                try { _conn.Dispose(); } catch { }
            }
        }
    }
}
