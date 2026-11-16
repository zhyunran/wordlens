using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WordLens.Core;

namespace WordLens.Import
{
    /// <summary>Importer for tabular dictionaries: CSV / TSV / TXT (UTF-8).</summary>
    public static class TextDictionaryImporter
    {
        public sealed class Result
        {
            public string DbPath;
            public string Name;
            public string Language;
            public long Entries;
        }

        private static readonly string[] WordHeaders =
            { "word", "headword", "term", "entry", "english", "spelling", "单词", "词条", "词目", "词" };
        private static readonly string[] PhoneticHeaders =
            { "phonetic", "ipa", "pronunciation", "pinyin", "phonetics", "音标", "发音", "拼音" };
        private static readonly string[] TranslationHeaders =
            { "translation", "meaning", "meanings", "chinese", "释义", "翻译", "中文", "解释", "词义", "意思" };
        private static readonly string[] DefinitionHeaders =
            { "definition", "definitions", "def", "english_definition", "英文释义", "英文解释", "定义" };
        private static readonly string[] PosHeaders = { "pos", "partofspeech", "part_of_speech", "词性" };
        private static readonly string[] TagHeaders = { "tag", "tags", "标签", "考试", "category" };

        public static Result Import(string sourcePath, string destPath, IProgress<string> progress)
        {
            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            char delimiter = DetectDelimiter(sourcePath, ext);
            List<string> sample = new List<string>();
            using (var reader = new StreamReader(sourcePath, Encoding.UTF8, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null && sample.Count < 40)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith(";")) continue;
                    sample.Add(line);
                }
            }
            if (sample.Count == 0) throw new InvalidDataException("文件中没有可读取的行");

            List<string> firstCells = SplitLine(sample[0], delimiter);
            bool hasHeader = DetectHeader(firstCells, out int wIdx, out int pIdx, out int tIdx, out int dIdx, out int posIdx, out int tagIdx);
            if (!hasHeader)
            {
                wIdx = 0;
                pIdx = firstCells.Count >= 3 ? 1 : -1;
                tIdx = firstCells.Count >= 3 ? 2 : (firstCells.Count >= 2 ? 1 : -1);
                dIdx = firstCells.Count >= 4 ? 3 : -1;
                posIdx = -1;
                tagIdx = -1;
            }
            else
            {
                wIdx = Math.Max(0, wIdx);
            }

            string name = Path.GetFileNameWithoutExtension(sourcePath);
            long count = 0;
            string language = "en";
            var headSamples = new List<string>();
            using (var builder = new Core.DictionaryBuilder(destPath))
            {
                using (var reader = new StreamReader(sourcePath, Encoding.UTF8, true))
                {
                    string line;
                    bool firstData = true;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (line.StartsWith("#") || line.StartsWith(";")) continue;
                        if (hasHeader && firstData) { firstData = false; continue; }
                        firstData = false;
                        List<string> cells = SplitLine(line, delimiter);
                        string word = Cell(cells, wIdx);
                        if (string.IsNullOrWhiteSpace(word)) continue;
                        if (word.Length > 120) continue;
                        string phonetic = Cell(cells, pIdx);
                        string translation = Cell(cells, tIdx);
                        string definition = Cell(cells, dIdx);
                        string pos = Cell(cells, posIdx);
                        string tags = Cell(cells, tagIdx);
                        if (string.IsNullOrWhiteSpace(translation) && string.IsNullOrWhiteSpace(definition))
                        {
                            // fall back: use remaining columns as the meaning
                            var rest = new List<string>();
                            int from = Math.Max(wIdx, tIdx) + 1;
                            for (int i = from; i < cells.Count; i++)
                                if (!string.IsNullOrWhiteSpace(cells[i])) rest.Add(cells[i].Trim());
                            if (rest.Count > 0) translation = string.Join("\n", rest);
                        }
                        builder.AddEntry(word, phonetic, translation, definition, pos, tags);
                        count++;
                        if (headSamples.Count < 400) headSamples.Add(word);
                        if (count % 2500 == 0) progress?.Report("已导入 " + count + " 条…");
                    }
                }
                language = DetectLanguage(headSamples);
                builder.SetMeta("name", name);
                builder.SetMeta("language", language);
                builder.SetMeta("type", "user");
                builder.SetMeta("source", "文件导入: " + Path.GetFileName(sourcePath));
                builder.SetMeta("description", "用户导入的 " + ext.TrimStart('.').ToUpperInvariant() + " 词典");
                builder.SetMeta("version", DateTime.Now.ToString("yyyy.MM.dd"));
                builder.SetMeta("license", "");
                builder.SetMeta("built_at", DateTime.Now.ToString("s"));
                builder.SetMeta("priority", "120");
                builder.SetMeta("app", "WordLens");
                builder.Commit();
            }
            return new Result { DbPath = destPath, Name = name, Language = language, Entries = count };
        }

        private static string Cell(List<string> cells, int idx)
        {
            if (idx < 0 || idx >= cells.Count) return "";
            return (cells[idx] ?? "").Trim();
        }

        private static char DetectDelimiter(string path, string ext)
        {
            try
            {
                using (var r = new StreamReader(path, Encoding.UTF8, true))
                {
                    string line = r.ReadLine();
                    if (line == null) return ',';
                    int tabs = Count(line, '\t');
                    int commas = Count(line, ',');
                    int semis = Count(line, ';');
                    if (tabs >= commas && tabs >= semis && tabs > 0) return '\t';
                    if (semis > commas) return ';';
                    return ',';
                }
            }
            catch { return ','; }
        }

        private static int Count(string s, char c)
        {
            int n = 0;
            foreach (char ch in s) if (ch == c) n++;
            return n;
        }

        private static bool DetectHeader(List<string> cells, out int w, out int p, out int t, out int d, out int pos, out int tag)
        {
            w = p = t = d = pos = tag = -1;
            for (int i = 0; i < cells.Count; i++)
            {
                string h = (cells[i] ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
                if (h.Length == 0 || h.Length > 24) continue;
                if (w < 0 && MatchAny(h, WordHeaders)) { w = i; continue; }
                if (p < 0 && MatchAny(h, PhoneticHeaders)) { p = i; continue; }
                if (t < 0 && MatchAny(h, TranslationHeaders)) { t = i; continue; }
                if (d < 0 && MatchAny(h, DefinitionHeaders)) { d = i; continue; }
                if (pos < 0 && MatchAny(h, PosHeaders)) { pos = i; continue; }
                if (tag < 0 && MatchAny(h, TagHeaders)) { tag = i; continue; }
            }
            return w >= 0;
        }

        private static bool MatchAny(string h, string[] names)
        {
            foreach (string n in names)
                if (h == n || h.Contains(n)) return true;
            return false;
        }

        public static List<string> SplitLine(string line, char delimiter)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == delimiter) { result.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
            }
            result.Add(sb.ToString());
            return result;
        }

        private static string DetectLanguage(List<string> headwords)
        {
            int cjk = 0, latin = 0;
            foreach (string w in headwords)
            {
                foreach (char c in w)
                {
                    if (TextUtil.IsCjk(c)) cjk++;
                    else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) latin++;
                }
            }
            if (cjk > 0 && cjk >= latin * 0.2) return "zh";
            return "en";
        }
    }
}
