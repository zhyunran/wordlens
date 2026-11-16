using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace WordLens.Core
{
    public sealed class LookupEntry
    {
        public DictInfo Dict { get; set; }
        public EntryRow Row { get; set; }
        public bool FromInflection { get; set; }
        public string MatchedForm { get; set; }
        public string Trad { get; set; }
        public string Simp { get; set; }
        public string Pinyin { get; set; }

        public List<string> TranslationLines
        {
            get
            {
                List<string> lines = SplitLines(Row.Translation);
                if (!IsChinese) return lines;
                List<string> filtered = lines.Where(l => !IsClassifierLine(l)).ToList();
                return filtered;
            }
        }

        /// <summary>
        /// CC-CEDICT appends classifier/measure-word annotations as their own
        /// definitions, e.g. "CL:位[wei4],個|个[ge4]" for 同学. They are not a
        /// meaning, but they contain useful usage information, so show them as
        /// a compact meta line: "量词：位 / 个".
        /// </summary>
        public string ClassifierText
        {
            get
            {
                if (!IsChinese) return null;
                foreach (string line in SplitLines(Row.Translation))
                {
                    if (!IsClassifierLine(line)) continue;
                    string rest = line.TrimStart();
                    rest = rest.Substring(rest.IndexOf(':') + 1).Trim();
                    var names = new List<string>();
                    foreach (string raw in rest.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string part = raw.Trim();
                        int bracket = part.IndexOf('[');
                        if (bracket >= 0) part = part.Substring(0, bracket).Trim();
                        int bar = part.LastIndexOf('|');
                        if (bar >= 0) part = part.Substring(bar + 1).Trim();
                        if (part.Length > 0 && !names.Contains(part)) names.Add(part);
                    }
                    if (names.Count > 0) return "量词：" + string.Join(" / ", names);
                }
                return null;
            }
        }

        private static bool IsClassifierLine(string line)
        {
            string t = (line ?? "").TrimStart();
            return t.StartsWith("CL:", StringComparison.OrdinalIgnoreCase)
                   || t.StartsWith("CL：", StringComparison.OrdinalIgnoreCase);
        }

        public List<string> DefinitionLines
        {
            get { return SplitLines(Row.Definition); }
        }

        public bool IsChinese
        {
            get
            {
                return (Dict != null && Dict.Language != null && Dict.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                       || TextUtil.ContainsCjk(Row.Headword);
            }
        }

        private static List<string> SplitLines(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(s)) return list;
            s = s.Replace("\\n", "\n").Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (string line in s.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }
    }

    public sealed class LookupResult
    {
        public string Query { get; set; }
        public string DisplayQuery { get; set; }
        public bool IsChinese { get; set; }
        public List<LookupEntry> Entries { get; set; } = new List<LookupEntry>();
        public List<string> Suggestions { get; set; } = new List<string>();
        public long ElapsedMs { get; set; }
        public string SourceApp { get; set; }
        public bool Found => Entries.Count > 0;

        public LookupEntry Primary => Entries.Count > 0 ? Entries[0] : null;
    }

    /// <summary>Multi-dictionary exact / inflection / suggestion lookup.</summary>
    public sealed class LookupService
    {
        private readonly DictionaryManager _manager;
        private readonly AppSettings _settings;

        public LookupService(DictionaryManager manager, AppSettings settings)
        {
            _manager = manager;
            _settings = settings;
        }

        public LookupResult Lookup(string query, string sourceApp = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new LookupResult
            {
                Query = query,
                DisplayQuery = query,
                IsChinese = TextUtil.ContainsCjk(query),
                SourceApp = sourceApp
            };
            string cleaned = query == null ? "" : TextUtil.NormalizeApostrophes(query.Trim());
            if (cleaned.Length == 0)
            {
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }
            List<string> variants = TextUtil.Variants(cleaned);
            List<DictDb> dicts = _manager.EnabledDictionaries(result.IsChinese);
            foreach (DictDb db in dicts)
            {
                LookupEntry hit = FindInDictionary(db, variants);
                if (hit != null) result.Entries.Add(hit);
                if (result.Entries.Count >= 4) break;
            }
            if (result.Entries.Count == 0)
            {
                result.Suggestions = Suggest(cleaned, result.IsChinese, dicts);
            }
            result.ElapsedMs = sw.ElapsedMilliseconds;
            Log.Info("lookup '" + cleaned + "' found=" + result.Entries.Count + " suggestions=" + result.Suggestions.Count + " ms=" + result.ElapsedMs);
            return result;
        }

        private LookupEntry FindInDictionary(DictDb db, List<string> variants)
        {
            foreach (string v in variants)
            {
                List<EntryRow> rows = db.FindByFold(v, 6);
                if (rows.Count > 0)
                    return MakeEntry(db, Best(rows, v), v, false);
            }
            // strip-word (alnum-only) match handles "long-time" vs "long time"
            foreach (string v in variants)
            {
                string sw = DictionaryBuilder.StripWord(v);
                if (sw.Length == 0 || sw == v.ToLowerInvariant()) continue;
                List<EntryRow> rows = db.FindBySw(sw, 6);
                if (rows.Count > 0)
                    return MakeEntry(db, Best(rows, v), v, false);
            }
            // known inflection forms in the dictionary's forms table
            foreach (string v in variants)
            {
                string baseWord = db.FindBase(v.ToLowerInvariant());
                if (string.IsNullOrEmpty(baseWord)) continue;
                List<EntryRow> rows = db.FindByFold(baseWord, 6);
                if (rows.Count > 0)
                    return MakeEntry(db, Best(rows, baseWord), v, true);
            }
            // naive suffix stripping as a last resort
            foreach (string v in variants)
            {
                foreach (string cand in TextUtil.LemmaCandidates(v))
                {
                    List<EntryRow> rows = db.FindByFold(cand, 6);
                    if (rows.Count > 0)
                        return MakeEntry(db, Best(rows, cand), v, true);
                }
            }
            return null;
        }

        private static EntryRow Best(List<EntryRow> rows, string query)
        {
            string q = query.ToLowerInvariant();
            return rows
                .OrderByDescending(r => r.Fold == q ? 1000 : 0)
                .ThenByDescending(r => r.Collins)
                .ThenByDescending(r => r.Oxford)
                .ThenByDescending(r => !string.IsNullOrEmpty(r.Translation) ? 500 : 0)
                .ThenBy(r => r.Frq > 0 ? r.Frq : 1000000)
                .ThenByDescending(r => (r.Translation ?? "").Length)
                .First();
        }

        private static LookupEntry MakeEntry(DictDb db, EntryRow row, string matchedForm, bool inflection)
        {
            var entry = new LookupEntry
            {
                Dict = db.Info,
                Row = row,
                FromInflection = inflection,
                MatchedForm = inflection ? matchedForm : null
            };
            if (!string.IsNullOrWhiteSpace(row.Extra))
            {
                try
                {
                    using (JsonDocument doc = JsonDocument.Parse(row.Extra))
                    {
                        JsonElement root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object)
                        {
                            if (root.TryGetProperty("trad", out JsonElement t) && t.ValueKind == JsonValueKind.String)
                                entry.Trad = t.GetString();
                            if (root.TryGetProperty("simp", out JsonElement s) && s.ValueKind == JsonValueKind.String)
                                entry.Simp = s.GetString();
                            if (root.TryGetProperty("pinyin", out JsonElement p) && p.ValueKind == JsonValueKind.String)
                                entry.Pinyin = p.GetString();
                        }
                    }
                }
                catch { }
            }
            return entry;
        }

        private List<string> Suggest(string query, bool chinese, List<DictDb> dicts)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<EntryRow>();
            string prefix = query.ToLowerInvariant();
            foreach (DictDb db in dicts.Take(2))
            {
                foreach (EntryRow r in db.SearchPrefix(prefix, 40))
                {
                    if (seen.Add(r.Headword)) rows.Add(r);
                    if (rows.Count >= 60) break;
                }
                if (rows.Count >= 60) break;
            }
            var ordered = rows
                .OrderBy(r => r.Frq > 0 ? r.Frq : 1000000)
                .ThenByDescending(r => r.Collins)
                .ThenBy(r => r.Headword.Length)
                .Select(r => r.Headword)
                .Where(h => !string.IsNullOrWhiteSpace(h) && h.Length <= 32)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            return ordered;
        }
    }
}
