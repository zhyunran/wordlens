using System;
using System.Collections.Generic;
using System.IO;

namespace WordLens.Import
{
    /// <summary>Importer for StarDict dictionaries (.ifo directory or .zip archive).</summary>
    public static class StarDictImporter
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
            using (StarDictDictionary dict = StarDictDictionary.Open(sourcePath))
            {
                string name = string.IsNullOrWhiteSpace(dict.BookName)
                    ? Path.GetFileNameWithoutExtension(sourcePath)
                    : dict.BookName.Trim();
                string language = "en";
                long count = 0;
                var headSamples = new List<string>(512);
                using (var builder = new Core.DictionaryBuilder(destPath))
                {
                    foreach (KeyValuePair<string, string> pair in dict.ReadEntries())
                    {
                        string word = pair.Key;
                        string value = pair.Value ?? "";
                        if (string.IsNullOrWhiteSpace(word)) continue;
                        string phonetic = "";
                        if (value.StartsWith(StarDictDictionary.PhoneticLinePrefix, StringComparison.Ordinal))
                        {
                            int nl = value.IndexOf('\n');
                            if (nl < 0)
                            {
                                phonetic = value.Substring(StarDictDictionary.PhoneticLinePrefix.Length).Trim();
                                value = "";
                            }
                            else
                            {
                                phonetic = value.Substring(StarDictDictionary.PhoneticLinePrefix.Length,
                                    nl - StarDictDictionary.PhoneticLinePrefix.Length).Trim();
                                value = value.Substring(nl + 1);
                            }
                        }
                        string translation = value.Trim();
                        builder.AddEntry(word, phonetic, translation, "");
                        count++;
                        if (headSamples.Count < 400) headSamples.Add(word);
                        if (count % 5000 == 0) progress?.Report("已导入 " + count + " 条…");
                    }
                    language = DetectLanguage(headSamples);
                    builder.SetMeta("name", name);
                    builder.SetMeta("language", language);
                    builder.SetMeta("type", "user");
                    builder.SetMeta("source", "StarDict 导入: " + Path.GetFileName(sourcePath));
                    builder.SetMeta("description", "用户导入的 StarDict 词典");
                    builder.SetMeta("version", DateTime.Now.ToString("yyyy.MM.dd"));
                    builder.SetMeta("license", "");
                    builder.SetMeta("built_at", DateTime.Now.ToString("s"));
                    builder.SetMeta("priority", "120");
                    builder.SetMeta("app", "WordLens");
                    builder.Commit();
                }
                return new Result { DbPath = destPath, Name = name, Language = language, Entries = count };
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
