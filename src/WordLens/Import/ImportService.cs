using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using WordLens.Core;

namespace WordLens.Import
{
    public sealed class ImportOutcome
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string DbPath { get; set; }
        public string Name { get; set; }
        public string Language { get; set; }
        public long Entries { get; set; }
        public string Format { get; set; }
    }

    /// <summary>Dispatches dictionary import by file type and installs the result into the user dictionary folder.</summary>
    public static class ImportService
    {
        public static ImportOutcome Import(string sourcePath, IProgress<string> progress)
        {
            var outcome = new ImportOutcome();
            string tempDb = null;
            string tempDir = null;
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    throw new FileNotFoundException("找不到词典文件", sourcePath);
                string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
                AppPaths.EnsureCreated();
                string tempRoot = AppPaths.TempDir;
                Directory.CreateDirectory(tempRoot);
                tempDir = Path.Combine(tempRoot, "import-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                tempDb = Path.Combine(tempDir, "import.wlcdb");

                long entries = 0;
                string name = Path.GetFileNameWithoutExtension(sourcePath);
                string language = "en";
                string format = ext.TrimStart('.').ToUpperInvariant();

                if (ext == ".wlcdb")
                {
                    File.Copy(sourcePath, tempDb, true);
                    format = "WordLens";
                }
                else if (ext == ".ifo")
                {
                    StarDictImporter.Result r = StarDictImporter.Import(sourcePath, tempDb, progress);
                    entries = r.Entries; name = r.Name; language = r.Language; format = "StarDict";
                }
                else if (ext == ".zip")
                {
                    ImportZip(sourcePath, tempDb, tempDir, progress, ref entries, ref name, ref language, ref format);
                }
                else if (ext == ".csv" || ext == ".tsv" || ext == ".txt")
                {
                    TextDictionaryImporter.Result r = TextDictionaryImporter.Import(sourcePath, tempDb, progress);
                    entries = r.Entries; name = r.Name; language = r.Language;
                    format = ext == ".csv" ? "CSV" : (ext == ".tsv" ? "TSV" : "TXT");
                }
                else if (ext == ".json" || ext == ".jsonl" || ext == ".ndjson")
                {
                    JsonDictionaryImporter.Result r = JsonDictionaryImporter.Import(sourcePath, tempDb, progress);
                    entries = r.Entries; name = r.Name; language = r.Language;
                    if (ext != ".json") format = "JSONL";
                    else format = "JSON";
                }
                else
                {
                    throw new InvalidDataException("不支持的文件类型: " + ext + "\n支持: .ifo / .zip (StarDict), .csv / .tsv / .txt, .json / .jsonl, .wlcdb");
                }

                if (!File.Exists(tempDb) || new FileInfo(tempDb).Length == 0)
                    throw new InvalidDataException("导入后没有生成词典数据");

                DictInfo info;
                using (var probe = new DictDb(tempDb, false))
                {
                    info = probe.Info;
                }
                if (string.IsNullOrWhiteSpace(name)) name = info.Name;
                if (string.IsNullOrWhiteSpace(language)) language = info.Language;
                if (entries <= 0) entries = info.EntryCount;

                string safeName = SanitizeName(name);
                string finalName = safeName + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wlcdb";
                string finalPath = Path.Combine(AppPaths.UserDictDir, finalName);
                Directory.CreateDirectory(AppPaths.UserDictDir);
                File.Move(tempDb, finalPath);

                outcome.Success = true;
                outcome.DbPath = finalPath;
                outcome.Name = string.IsNullOrWhiteSpace(name) ? safeName : name;
                outcome.Language = language;
                outcome.Entries = entries;
                outcome.Format = format;
                Log.Info("Imported dictionary '" + outcome.Name + "' (" + entries + " entries, " + format + ") -> " + finalPath);
                return outcome;
            }
            catch (Exception ex)
            {
                Log.Error("Dictionary import failed for " + sourcePath, ex);
                outcome.Success = false;
                outcome.Error = ex.Message;
                return outcome;
            }
            finally
            {
                try { if (tempDb != null && File.Exists(tempDb)) File.Delete(tempDb); } catch { }
                try { if (tempDir != null && Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static void ImportZip(string zipPath, string tempDb, string workDir, IProgress<string> progress,
            ref long entries, ref string name, ref string language, ref string format)
        {
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                var ifo = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".ifo", StringComparison.OrdinalIgnoreCase));
                if (ifo != null)
                {
                    string extractDir = Path.Combine(workDir, "stardict");
                    Directory.CreateDirectory(extractDir);
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                        if (ext != ".ifo" && ext != ".idx" && ext != ".gz" && ext != ".dict" && ext != ".dz" && ext != ".syn" && ext != ".idx.gz") continue;
                        string dest = Path.Combine(extractDir, entry.Name);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        entry.ExtractToFile(dest, true);
                    }
                    string ifoPath = Path.Combine(extractDir, ifo.FullName);
                    if (!File.Exists(ifoPath)) ifoPath = Directory.GetFiles(extractDir, "*.ifo", SearchOption.AllDirectories).FirstOrDefault();
                    if (ifoPath == null) throw new InvalidDataException("ZIP 中的 StarDict 结构不完整");
                    StarDictImporter.Result r = StarDictImporter.Import(ifoPath, tempDb, progress);
                    entries = r.Entries; name = r.Name; language = r.Language; format = "StarDict";
                    return;
                }
                var csv = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                                                          e.Name.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) ||
                                                          e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
                if (csv != null)
                {
                    string file = Path.Combine(workDir, csv.Name);
                    csv.ExtractToFile(file, true);
                    TextDictionaryImporter.Result r = TextDictionaryImporter.Import(file, tempDb, progress);
                    entries = r.Entries; name = r.Name; language = r.Language;
                    format = "CSV (ZIP)";
                    return;
                }
                var json = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                if (json != null)
                {
                    string file = Path.Combine(workDir, json.Name);
                    json.ExtractToFile(file, true);
                    JsonDictionaryImporter.Result r = JsonDictionaryImporter.Import(file, tempDb, progress);
                    entries = r.Entries; name = r.Name; language = r.Language; format = "JSON (ZIP)";
                    return;
                }
                throw new InvalidDataException("ZIP 中没有找到 .ifo/.csv/.json 词典文件");
            }
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "dictionary";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder();
            foreach (char c in name.Trim())
            {
                if (Array.IndexOf(invalid, c) >= 0) sb.Append('_');
                else if (char.IsControl(c)) continue;
                else sb.Append(c);
                if (sb.Length >= 48) break;
            }
            string s = sb.ToString().Trim(' ', '.', '_');
            return string.IsNullOrWhiteSpace(s) ? "dictionary" : s;
        }
    }
}
