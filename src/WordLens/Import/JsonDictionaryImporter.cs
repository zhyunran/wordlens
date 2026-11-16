using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WordLens.Core;
using System.Text.Json;

namespace WordLens.Import
{
    /// <summary>Importer for JSON / JSONL dictionaries.</summary>
    public static class JsonDictionaryImporter
    {
        public sealed class Result
        {
            public string DbPath;
            public string Name;
            public string Language;
            public long Entries;
        }

        public static Result Import(string sourcePath, string destPath, IProgress<string> progress)
        {
            string name = Path.GetFileNameWithoutExtension(sourcePath);
            string language = "en";
            long count = 0;
            var headSamples = new List<string>();
            using (var builder = new Core.DictionaryBuilder(destPath))
            {
                if (Path.GetExtension(sourcePath).Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(sourcePath).Equals(".ndjson", StringComparison.OrdinalIgnoreCase))
                {
                    ImportJsonLines(sourcePath, builder, ref count, headSamples, progress);
                }
                else
                {
                    string text = File.ReadAllText(sourcePath, Encoding.UTF8);
                    using (JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                        MaxDepth = 32
                    }))
                    {
                        JsonElement root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Array)
                        {
                            ImportArray(root, builder, ref count, headSamples, progress);
                        }
                        else if (root.ValueKind == JsonValueKind.Object)
                        {
                            if (root.TryGetProperty("entries", out JsonElement entries) && entries.ValueKind == JsonValueKind.Array)
                            {
                                ImportArray(entries, builder, ref count, headSamples, progress);
                            }
                            else if (root.TryGetProperty("words", out JsonElement words) && words.ValueKind == JsonValueKind.Array)
                            {
                                ImportArray(words, builder, ref count, headSamples, progress);
                            }
                            else
                            {
                                foreach (JsonProperty prop in root.EnumerateObject())
                                {
                                    string w = prop.Name;
                                    var values = new List<string>();
                                    CollectStrings(prop.Value, values);
                                    builder.AddEntry(w, "", string.Join("\n", values), "");
                                    count++;
                                    if (headSamples.Count < 400) headSamples.Add(w);
                                }
                            }
                        }
                        else
                        {
                            throw new InvalidDataException("不支持的 JSON 结构（需要数组或对象）");
                        }
                    }
                }
                language = DetectLanguage(headSamples);
                builder.SetMeta("name", name);
                builder.SetMeta("language", language);
                builder.SetMeta("type", "user");
                builder.SetMeta("source", "JSON 导入: " + Path.GetFileName(sourcePath));
                builder.SetMeta("description", "用户导入的 JSON 词典");
                builder.SetMeta("version", DateTime.Now.ToString("yyyy.MM.dd"));
                builder.SetMeta("license", "");
                builder.SetMeta("built_at", DateTime.Now.ToString("s"));
                builder.SetMeta("priority", "120");
                builder.SetMeta("app", "WordLens");
                builder.Commit();
            }
            return new Result { DbPath = destPath, Name = name, Language = language, Entries = count };
        }

        private static void ImportJsonLines(string path, Core.DictionaryBuilder builder, ref long count,
            List<string> headSamples, IProgress<string> progress)
        {
            using (var reader = new StreamReader(path, Encoding.UTF8, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    using (JsonDocument doc = JsonDocument.Parse(line))
                    {
                        if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                        if (!TryAddObject(doc.RootElement, builder)) continue;
                        count++;
                        string w = GetString(doc.RootElement, "word", "headword", "term");
                        if (headSamples.Count < 400 && !string.IsNullOrEmpty(w)) headSamples.Add(w);
                        if (count % 2500 == 0) progress?.Report("已导入 " + count + " 条…");
                    }
                }
            }
        }

        private static void ImportArray(JsonElement array, Core.DictionaryBuilder builder, ref long count,
            List<string> headSamples, IProgress<string> progress)
        {
            foreach (JsonElement el in array.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object)
                {
                    if (!TryAddObject(el, builder)) continue;
                    string w = GetString(el, "word", "headword", "term");
                    if (headSamples.Count < 400 && !string.IsNullOrEmpty(w)) headSamples.Add(w);
                }
                else if (el.ValueKind == JsonValueKind.Array)
                {
                    var cells = new List<string>();
                    foreach (JsonElement x in el.EnumerateArray())
                        cells.Add(x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString());
                    if (cells.Count >= 2)
                    {
                        builder.AddEntry(cells[0], cells.Count >= 3 ? cells[1] : "", cells[cells.Count >= 3 ? 2 : 1], "");
                        count++;
                        if (headSamples.Count < 400) headSamples.Add(cells[0]);
                    }
                }
                if (count % 2500 == 0) progress?.Report("已导入 " + count + " 条…");
            }
        }

        private static bool TryAddObject(JsonElement obj, Core.DictionaryBuilder builder)
        {
            string word = GetString(obj, "word", "headword", "term", "单词", "词条", "text");
            if (string.IsNullOrWhiteSpace(word)) return false;
            string phonetic = GetString(obj, "phonetic", "ipa", "pronunciation", "pinyin", "音标", "拼音");
            string translation = GetString(obj, "translation", "meanings", "meaning", "chinese", "释义", "翻译", "中文", "解释");
            if (string.IsNullOrEmpty(translation))
            {
                var list = new List<string>();
                if (obj.TryGetProperty("translations", out JsonElement t)) CollectStrings(t, list);
                if (obj.TryGetProperty("definitions", out JsonElement d)) CollectStrings(d, list);
                translation = string.Join("\n", list);
            }
            string definition = GetString(obj, "definition", "def", "english_definition", "英文释义");
            if (string.IsNullOrEmpty(definition))
            {
                var list = new List<string>();
                if (obj.TryGetProperty("definitions", out JsonElement d2) && string.IsNullOrEmpty(translation))
                    CollectStrings(d2, list);
                definition = string.Join("\n", list);
            }
            string pos = GetString(obj, "pos", "part_of_speech", "词性");
            string tags = GetString(obj, "tags", "tag", "标签");
            if (string.IsNullOrEmpty(tags) && obj.TryGetProperty("tags", out JsonElement tg))
            {
                var list = new List<string>();
                CollectStrings(tg, list);
                tags = string.Join(" ", list);
            }
            if (string.IsNullOrWhiteSpace(translation) && string.IsNullOrWhiteSpace(definition))
            {
                var values = new List<string>();
                CollectStrings(obj, values);
                translation = string.Join("\n", values);
            }
            if (string.IsNullOrWhiteSpace(translation) && string.IsNullOrWhiteSpace(definition)) return false;
            builder.AddEntry(word, phonetic, translation, definition, pos, tags);
            return true;
        }

        private static string GetString(JsonElement obj, params string[] names)
        {
            foreach (string name in names)
            {
                if (obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String)
                {
                    string s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            return "";
        }

        private static void CollectStrings(JsonElement el, List<string> list)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    string s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement c in el.EnumerateArray())
                    {
                        if (c.ValueKind == JsonValueKind.String)
                        {
                            string x = c.GetString();
                            if (!string.IsNullOrWhiteSpace(x)) list.Add(x);
                        }
                        else if (c.ValueKind == JsonValueKind.Object)
                        {
                            foreach (string key in new[] { "translation", "meaning", "definition", "def", "text" })
                            {
                                if (c.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String)
                                {
                                    string x = v.GetString();
                                    if (!string.IsNullOrWhiteSpace(x)) { list.Add(x); break; }
                                }
                            }
                        }
                    }
                    break;
                case JsonValueKind.Object:
                    foreach (string key in new[] { "translation", "translations", "meaning", "definition", "definitions", "text" })
                    {
                        if (el.TryGetProperty(key, out JsonElement v))
                        {
                            CollectStrings(v, list);
                            if (list.Count > 0) break;
                        }
                    }
                    break;
            }
        }

        private static string DetectLanguage(List<string> headwords)
        {
            int cjk = 0, latin = 0;
            foreach (string w in headwords)
            {
                foreach (char c in w)
                {
                    if (Core.TextUtil.IsCjk(c)) cjk++;
                    else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) latin++;
                }
            }
            return cjk > 0 && cjk >= latin * 0.2 ? "zh" : "en";
        }
    }
}
