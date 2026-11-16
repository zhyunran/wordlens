// StarDict reader tests for WordLens.Import.StarDictDictionary.
//
// Everything the tests need is generated at run time:
//   - plain  .ifo / .idx / .dict           (type-tagged records, BOM + CRLF .ifo)
//   -        .ifo / .idx.gz / .dict        (sametypesequence h)
//   -        .ifo / .idx / .dict.dz        (real dictzip, RA extra field, 58315-byte chunks)
//   -        .zip with .ifo + .idx.gz + .dict.dz in a nested folder
//   -        .ifo with 64-bit offsets      (idxoffsetbits=64 and auto-detected without the key)
//
// The test program prints PASS/FAIL lines and exits with a non-zero code on failure.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using WordLens.Import;

namespace WordLens.Import.Tests
{
    internal static class Program
    {
        private const int DictZipChunkLength = 58315;

        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            try
            {
                Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
            catch (IOException)
            {
                // Redirected console hosts may reject the change; tests still work.
            }

            Console.WriteLine("StarDict reader tests");
            Console.WriteLine("Runtime: " + Environment.Version);

            string fixtureRoot = Path.Combine(AppContext.BaseDirectory, "stardict-fixtures-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(fixtureRoot);
                Fixtures fixtures = CreateFixtures(fixtureRoot);
                Console.WriteLine("Fixtures: " + fixtureRoot);
                Console.WriteLine();
                TestPlainDictionary(fixtures);
                TestGzipIndex(fixtures);
                TestFixedSequence(fixtures);
                TestGzipFallbackInDz(fixtures);
                TestDictZip(fixtures);
                TestZipArchive(fixtures);
                Test64BitOffsets(fixtures);
                TestErrors(fixtures);
                TestDisposal(fixtures);
            }
            catch (Exception exception)
            {
                Check("harness", false, exception.GetType().Name + ": " + exception.Message);
                Console.WriteLine(exception);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(fixtureRoot))
                    {
                        Directory.Delete(fixtureRoot, true);
                    }
                }
                catch (IOException)
                {
                    // Leave the fixture folder behind if Windows still holds a handle.
                }
            }

            Console.WriteLine();
            Console.WriteLine("Result: " + _passed + " passed, " + _failed + " failed.");
            return _failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------
        // Plain .ifo/.idx/.dict with type-tagged records
        // ------------------------------------------------------------------
        private static void TestPlainDictionary(Fixtures fixtures)
        {
            using StarDictDictionary dictionary = StarDictDictionary.Open(fixtures.PlainIfo);

            CheckEqual("plain/bookname", "WordLens Test Dictionary", dictionary.BookName);
            CheckEqual("plain/description-unescaped", "First line.\nSecond line.", dictionary.Description);
            CheckEqual("plain/sametypesequence-empty", string.Empty, dictionary.Sametypesequence);
            CheckEqual("plain/sourcepath", fixtures.PlainIfo, dictionary.SourcePath);
            Check(
                "plain/fixture-declares-wrong-wordcount",
                fixtures.PlainIfoDeclaredWordCount != fixtures.PlainEntryCount,
                "fixture bug: wordcount should be wrong");
            CheckEqual("plain/wordcount-comes-from-idx", fixtures.PlainEntryCount, dictionary.WordCount);

            List<KeyValuePair<string, string>> entries = ReadAll(dictionary);
            CheckEqual("plain/entry-count", fixtures.PlainEntryCount, entries.Count);
            CheckEqual("plain/first-word", "apple", entries[0].Key);

            CheckEqual(
                "plain/html-and-phonetic-prefix",
                fixtures.PlainApple,
                FindValue(entries, "apple") ?? "<missing>");
            CheckEqual(
                "plain/duplicate-headword-second-entry",
                "A second apple entry.",
                FindNthValue(entries, "apple", 1) ?? "<missing>");
            CheckEqual(
                "plain/html-entities-and-nbsp",
                "banana — yellow fruit. Rich in potassium.",
                FindValue(entries, "banana") ?? "<missing>");
            CheckEqual(
                "plain/plain-text-blank-lines-collapsed",
                "cat\n\na small domesticated feline.",
                FindValue(entries, "cat") ?? "<missing>");
            CheckEqual(
                "plain/div-tags-to-newlines",
                "first line\n\nsecond line",
                FindValue(entries, "div") ?? "<missing>");
            CheckEqual(
                "plain/pango-markup-stripped",
                "dog — canine",
                FindValue(entries, "dog") ?? "<missing>");
            CheckEqual(
                "plain/xdxf-stripped",
                "egg [eg] oval reproductive body",
                FindValue(entries, "egg") ?? "<missing>");
            CheckEqual(
                "plain/kingsoft-xml-stripped",
                "fish I caught a fish.",
                FindValue(entries, "fish") ?? "<missing>");
            CheckEqual(
                "plain/mediawiki-cleaned",
                "goat is a mammal of the genus Capra.",
                FindValue(entries, "goat") ?? "<missing>");
            CheckEqual(
                "plain/pinyin-phonetic-prefix",
                fixtures.PlainHouse,
                FindValue(entries, "house") ?? "<missing>");
            CheckEqual(
                "plain/p-and-br-and-entity-stripping",
                "ice\n\nfrozen\nwater\n\nsolid form 'ice'",
                FindValue(entries, "ice") ?? "<missing>");
            CheckEqual(
                "plain/utf8-word",
                "having an accent.",
                FindValue(entries, "naïve") ?? "<missing>");
            CheckEqual(
                "plain/utf8-headword",
                "above; over.",
                FindValue(entries, "Über") ?? "<missing>");
            CheckEqual(
                "plain/zero-length-definition",
                string.Empty,
                FindValue(entries, "void") ?? "<missing>");

            int phoneticLines = 0;
            int markupLeaks = 0;
            int tripleNewlines = 0;
            string firstMarkupLeak = string.Empty;
            foreach (KeyValuePair<string, string> entry in entries)
            {
                if (entry.Value.StartsWith(StarDictDictionary.PhoneticLinePrefix, StringComparison.Ordinal))
                {
                    phoneticLines++;
                }

                if (entry.Value.Contains("<b>", StringComparison.Ordinal) ||
                    entry.Value.Contains("</", StringComparison.Ordinal) ||
                    entry.Value.Contains("&amp;", StringComparison.Ordinal) ||
                    entry.Value.Contains("&nbsp;", StringComparison.Ordinal))
                {
                    markupLeaks++;
                    if (firstMarkupLeak.Length == 0)
                    {
                        firstMarkupLeak = entry.Key;
                    }
                }

                if (entry.Value.Contains("\n\n\n", StringComparison.Ordinal))
                {
                    tripleNewlines++;
                }
            }

            CheckEqual("plain/phonetic-marker-count", 2, phoneticLines);
            Check("plain/no-raw-markup", markupLeaks == 0, "first offending headword: " + firstMarkupLeak);
            Check("plain/no-triple-newlines", tripleNewlines == 0, tripleNewlines + " entries had 3+ newlines");

            CheckEqual(
                "plain/lookup-exact",
                "banana — yellow fruit. Rich in potassium.",
                dictionary.Lookup("banana") ?? "<missing>");
            CheckEqual(
                "plain/lookup-phonetic-entry",
                fixtures.PlainApple,
                dictionary.Lookup("apple") ?? "<missing>");
            CheckEqual("plain/lookup-zero-length-not-null", string.Empty, dictionary.Lookup("void") ?? "<missing>");
            Check("plain/lookup-missing", dictionary.Lookup("does-not-exist") is null);
            Check("plain/lookup-is-case-sensitive", dictionary.Lookup("APPLE") is null);
            Check("plain/lookup-null-for-empty-word", dictionary.Lookup(string.Empty) is null);

            int secondPass = 0;
            foreach (KeyValuePair<string, string> _ in dictionary.ReadEntries())
            {
                secondPass++;
            }

            CheckEqual("plain/readentries-can-be-reenumerated", fixtures.PlainEntryCount, secondPass);
        }

        // ------------------------------------------------------------------
        // .idx.gz + plain .dict, sametypesequence=h
        // ------------------------------------------------------------------
        private static void TestGzipIndex(Fixtures fixtures)
        {
            using StarDictDictionary dictionary = StarDictDictionary.Open(fixtures.GzIfo);

            CheckEqual("gzidx/bookname", "Gzip Index Test", dictionary.BookName);
            CheckEqual("gzidx/sametypesequence", "h", dictionary.Sametypesequence);
            CheckEqual("gzidx/wordcount", 4, dictionary.WordCount);

            List<KeyValuePair<string, string>> entries = ReadAll(dictionary);
            CheckEqual("gzidx/entry-count", 4, entries.Count);
            CheckEqual("gzidx/alpha", "first & foremost", FindValue(entries, "alpha") ?? "<missing>");
            CheckEqual("gzidx/beta-br", "second\nletter", FindValue(entries, "beta") ?? "<missing>");
            CheckEqual("gzidx/gamma", "third", FindValue(entries, "gamma") ?? "<missing>");
            CheckEqual("gzidx/delta", "fourth", FindValue(entries, "delta") ?? "<missing>");
            CheckEqual("gzidx/lookup", "second\nletter", dictionary.Lookup("beta") ?? "<missing>");
        }

        // ------------------------------------------------------------------
        // Fixed sametypesequence with multiple fields ("tm": phonetic + text)
        // ------------------------------------------------------------------
        private static void TestFixedSequence(Fixtures fixtures)
        {
            using StarDictDictionary dictionary = StarDictDictionary.Open(fixtures.FixedIfo);

            CheckEqual("fixedseq/bookname", "Fixed Sequence Test", dictionary.BookName);
            CheckEqual("fixedseq/sametypesequence", "tm", dictionary.Sametypesequence);
            CheckEqual("fixedseq/wordcount", 2, dictionary.WordCount);

            List<KeyValuePair<string, string>> entries = ReadAll(dictionary);
            CheckEqual("fixedseq/entry-count", 2, entries.Count);
            CheckEqual("fixedseq/first", fixtures.FixedFirst, FindValue(entries, "fixed") ?? "<missing>");
            CheckEqual("fixedseq/second", fixtures.FixedSecond, FindValue(entries, "plainword") ?? "<missing>");
            CheckEqual("fixedseq/lookup", fixtures.FixedSecond, dictionary.Lookup("plainword") ?? "<missing>");
        }

        // ------------------------------------------------------------------
        // A .dict.dz file that is actually a plain gzip stream (no RA field).
        // ------------------------------------------------------------------
        private static void TestGzipFallbackInDz(Fixtures fixtures)
        {
            using StarDictDictionary dictionary = StarDictDictionary.Open(fixtures.GzipFallbackIfo);

            CheckEqual("gzipfallback/bookname", "Gzip In Dz Test", dictionary.BookName);
            CheckEqual("gzipfallback/wordcount", 2, dictionary.WordCount);
            CheckEqual("gzipfallback/first", "gzip fallback works", dictionary.Lookup("gz") ?? "<missing>");
            CheckEqual("gzipfallback/second", "second gzip entry", dictionary.Lookup("second") ?? "<missing>");
        }

        // ------------------------------------------------------------------
        // .dict.dz (real dictzip random access) - start, middle, end,
        // a chunk-straddling entry, and a full round trip.
        // ------------------------------------------------------------------
        private static void TestDictZip(Fixtures fixtures)
        {
            using StarDictDictionary dictionary = StarDictDictionary.Open(fixtures.DzIfo);

            CheckEqual("dictzip/bookname", "Dictzip Test Dictionary", dictionary.BookName);
            CheckEqual("dictzip/sametypesequence", "m", dictionary.Sametypesequence);
            CheckEqual("dictzip/wordcount", 3000, dictionary.WordCount);
            Check(
                "dictzip/fixture-spans-multiple-chunks",
                fixtures.DzChunkCount >= 5,
                "fixture only produced " + fixtures.DzChunkCount + " chunks");

            CheckEqual("dictzip/first-entry", fixtures.DzExpected[0], dictionary.Lookup("w0000") ?? "<missing>");
            CheckEqual("dictzip/middle-entry", fixtures.DzExpected[1500], dictionary.Lookup("w1500") ?? "<missing>");
            CheckEqual("dictzip/last-entry", fixtures.DzExpected[2999], dictionary.Lookup("w2999") ?? "<missing>");
            Check(
                "dictzip/straddling-entry-exists",
                fixtures.DzStraddlingWord.Length > 0,
                "fixture contains no chunk-straddling entry");
            CheckEqual(
                "dictzip/straddling-entry-content",
                fixtures.DzStraddlingExpected,
                dictionary.Lookup(fixtures.DzStraddlingWord) ?? "<missing>");

            List<KeyValuePair<string, string>> entries = ReadAll(dictionary);
            CheckEqual("dictzip/entry-count", 3000, entries.Count);

            int mismatches = 0;
            string firstMismatch = string.Empty;
            for (int i = 0; i < entries.Count && i < fixtures.DzExpected.Count; i++)
            {
                string expectedWord = "w" + i.ToString("D4", CultureInfo.InvariantCulture);
                string expectedDefinition = fixtures.DzExpected[i];
                if (!string.Equals(entries[i].Key, expectedWord, StringComparison.Ordinal) ||
                    !string.Equals(entries[i].Value, expectedDefinition, StringComparison.Ordinal))
                {
                    if (mismatches == 0)
                    {
                        firstMismatch = "index " + i + ": got (" + entries[i].Key + ", " +
                                        Quote(entries[i].Value) + ")";
                    }

                    mismatches++;
                }
            }

            Check("dictzip/all-entries-roundtrip", mismatches == 0, mismatches + " mismatched; first: " + firstMismatch);
        }

        // ------------------------------------------------------------------
        // .zip archive containing .ifo + .idx.gz + .dict.dz in a subfolder
        // ------------------------------------------------------------------
        private static void TestZipArchive(Fixtures fixtures)
        {
            using StarDictDictionary dictionary = StarDictDictionary.Open(fixtures.ZipPath);

            CheckEqual("zip/bookname", "Zip Test Dictionary", dictionary.BookName);
            CheckEqual("zip/description", "Inside zip.\nSecond line.", dictionary.Description);
            CheckEqual("zip/sametypesequence", "h", dictionary.Sametypesequence);
            CheckEqual("zip/sourcepath-is-archive", fixtures.ZipPath, dictionary.SourcePath);
            CheckEqual("zip/wordcount", 120, dictionary.WordCount);

            List<KeyValuePair<string, string>> entries = ReadAll(dictionary);
            CheckEqual("zip/entry-count", 120, entries.Count);

            int mismatches = 0;
            string firstMismatch = string.Empty;
            for (int i = 0; i < entries.Count; i++)
            {
                string word = "zip" + i.ToString("D4", CultureInfo.InvariantCulture);
                string definition = word + "\nentry " + i.ToString(CultureInfo.InvariantCulture) + " & more";
                if (!string.Equals(entries[i].Key, word, StringComparison.Ordinal) ||
                    !string.Equals(entries[i].Value, definition, StringComparison.Ordinal))
                {
                    if (mismatches == 0)
                    {
                        firstMismatch = "index " + i + ": got (" + entries[i].Key + ", " +
                                        Quote(entries[i].Value) + ")";
                    }

                    mismatches++;
                }
            }

            Check("zip/all-entries-roundtrip", mismatches == 0, mismatches + " mismatched; first: " + firstMismatch);
            CheckEqual("zip/last-entry-lookup", "zip0119\nentry 119 & more", dictionary.Lookup("zip0119") ?? "<missing>");
        }

        // ------------------------------------------------------------------
        // 64-bit .idx offsets, with and without the .ifo hint
        // ------------------------------------------------------------------
        private static void Test64BitOffsets(Fixtures fixtures)
        {
            using (StarDictDictionary dictionary = StarDictDictionary.Open(fixtures.X64Ifo))
            {
                CheckEqual("idx64/bookname", "64-bit Index Test", dictionary.BookName);
                CheckEqual("idx64/wordcount", 3, dictionary.WordCount);
                CheckEqual("idx64/lookup-one", "first entry", dictionary.Lookup("one") ?? "<missing>");
                CheckEqual("idx64/lookup-two", "second entry", dictionary.Lookup("two") ?? "<missing>");
                CheckEqual("idx64/lookup-three", "third entry", dictionary.Lookup("three") ?? "<missing>");

                List<KeyValuePair<string, string>> entries = ReadAll(dictionary);
                CheckEqual("idx64/entry-count", 3, entries.Count);
                CheckEqual("idx64/first-word", "one", entries[0].Key);
            }

            using (StarDictDictionary dictionary = StarDictDictionary.Open(fixtures.X64AutoIfo))
            {
                CheckEqual("idx64/autodetect-wordcount", 3, dictionary.WordCount);
                CheckEqual("idx64/autodetect-lookup", "first entry", dictionary.Lookup("one") ?? "<missing>");
                CheckEqual("idx64/autodetect-last", "third entry", dictionary.Lookup("three") ?? "<missing>");
            }
        }

        // ------------------------------------------------------------------
        // Error handling
        // ------------------------------------------------------------------
        private static void TestErrors(Fixtures fixtures)
        {
            CheckThrows<FileNotFoundException>(
                "errors/missing-file",
                () => StarDictDictionary.Open(Path.Combine(fixtures.Root, "does-not-exist.ifo")));
            CheckThrows<InvalidDataException>(
                "errors/not-an-ifo",
                () => StarDictDictionary.Open(fixtures.BadIfoPath));
            CheckThrows<InvalidDataException>(
                "errors/missing-index",
                () => StarDictDictionary.Open(fixtures.NoIndexIfoPath));
            CheckThrows<InvalidDataException>(
                "errors/empty-index",
                () => StarDictDictionary.Open(fixtures.EmptyIndexIfoPath));
            CheckThrows<ArgumentException>(
                "errors/blank-path",
                () => StarDictDictionary.Open("   "));
            CheckThrows<ArgumentNullException>(
                "errors/null-path",
                () => StarDictDictionary.Open(null!));
        }

        private static void TestDisposal(Fixtures fixtures)
        {
            StarDictDictionary dictionary = StarDictDictionary.Open(fixtures.PlainIfo);
            dictionary.Lookup("apple");
            dictionary.Dispose();
            dictionary.Dispose(); // must be idempotent

            CheckThrows<ObjectDisposedException>(
                "dispose/readentries",
                () =>
                {
                    foreach (KeyValuePair<string, string> _ in dictionary.ReadEntries())
                    {
                    }
                });
            CheckThrows<ObjectDisposedException>(
                "dispose/lookup",
                () => dictionary.Lookup("apple"));
        }

        // ------------------------------------------------------------------
        // Fixture generation
        // ------------------------------------------------------------------
        private static Fixtures CreateFixtures(string root)
        {
            var fixtures = new Fixtures { Root = root };

            const string applePhonetic = "ˈæp.əl";

            // Plain dictionary: type-tagged records (no sametypesequence), HTML/Pango/XDXF/
            // KingSoft/MediaWiki content, entity decoding, phonetics and UTF-8 headwords.
            string plainDirectory = Path.Combine(root, "plain");
            Directory.CreateDirectory(plainDirectory);
            List<TestEntry> plainEntries = Sorted(new List<TestEntry>
            {
                Entry(
                    "apple",
                    ('t', applePhonetic),
                    ('h', "<b>apple</b> <i>n.</i><br/>1. a round fruit &amp; a tree.<br/><br/><br/>2. <span style=\"color:red\">red</span> or green.</p>")),
                Entry("apple", ('m', "A second apple entry.")),
                Entry("banana", ('h', "<i>banana</i> — yellow fruit.&nbsp;Rich in <b>potassium</b>.")),
                Entry("cat", ('m', "cat\n\n\n\na small domesticated feline.")),
                Entry("div", ('h', "<div>first line</div><div>second line</div>")),
                Entry("dog", ('g', "<span foreground=\"blue\">dog</span> — <b>canine</b>")),
                Entry("egg", ('x', "<k>egg</k> <tr>[eg]</tr> <def>oval reproductive body</def>")),
                Entry("fish", ('k', "<单词块><词条><解释>fish</解释><例句>I caught a fish.</例句></词条></单词块>")),
                Entry("goat", ('w', "'''goat''' is a [[mammal]] of the [[Capra|genus Capra]].")),
                Entry("house", ('y', "fángzi"), ('h', "<b>house</b><br>A building for people to live in.")),
                Entry("ice", ('h', "<p>ice</p><p>frozen<br>water</p><p><br/></p><p>solid form &#39;ice&#39;</p>")),
                Entry("naïve", ('m', "having an accent.")),
                Entry("void"),
                Entry("Über", ('m', "above; over.")),
            });

            BuiltDictionary plain = BuildIndexedDictionary(plainEntries, null, 32);
            File.WriteAllBytes(Path.Combine(plainDirectory, "plain.dict"), plain.Payload);
            File.WriteAllBytes(Path.Combine(plainDirectory, "plain.idx"), plain.Index);
            File.WriteAllBytes(
                Path.Combine(plainDirectory, "plain.ifo"),
                BuildIfo(
                    "WordLens Test Dictionary",
                    "First line.\\nSecond line.",
                    plainEntries.Count + 5, // deliberately stale wordcount
                    string.Empty,
                    32,
                    "2.4.2"));

            fixtures.PlainIfo = Path.Combine(plainDirectory, "plain.ifo");
            fixtures.PlainEntryCount = plainEntries.Count;
            fixtures.PlainIfoDeclaredWordCount = plainEntries.Count + 5;
            fixtures.PlainApple = StarDictDictionary.PhoneticLinePrefix + applePhonetic +
                                  "\napple n.\n1. a round fruit & a tree.\n\n2. red or green.";
            fixtures.PlainHouse = StarDictDictionary.PhoneticLinePrefix +
                                  "fángzi\nhouse\nA building for people to live in.";

            // Gzip-compressed index plus a plain .dict.
            string gzipDirectory = Path.Combine(root, "gzidx");
            Directory.CreateDirectory(gzipDirectory);
            List<TestEntry> gzipEntries = Sorted(new List<TestEntry>
            {
                Entry("alpha", ('h', "<p>first &amp; foremost</p>")),
                Entry("beta", ('h', "<b>second</b><br>letter")),
                Entry("delta", ('h', "<div>fourth</div>")),
                Entry("gamma", ('h', "third")),
            });

            BuiltDictionary gzip = BuildIndexedDictionary(gzipEntries, "h", 32);
            File.WriteAllBytes(Path.Combine(gzipDirectory, "gzidx.dict"), gzip.Payload);
            File.WriteAllBytes(Path.Combine(gzipDirectory, "gzidx.idx.gz"), GzipBytes(gzip.Index));
            File.WriteAllBytes(
                Path.Combine(gzipDirectory, "gzidx.ifo"),
                BuildIfo("Gzip Index Test", "Index is gzipped.\\nSecond line.", 4, "h", 32, "2.4.2"));
            fixtures.GzIfo = Path.Combine(gzipDirectory, "gzidx.ifo");

            // Fixed sametypesequence "tm" (phonetic then plain text; the last field has no
            // NUL terminator). Exercises the non-tagged record layout.
            string fixedSequenceDirectory = Path.Combine(root, "fixedseq");
            Directory.CreateDirectory(fixedSequenceDirectory);
            List<TestEntry> fixedSequenceEntries = Sorted(new List<TestEntry>
            {
                Entry("fixed", ('t', "fɪkst"), ('m', "set in place; not movable")),
                Entry("plainword", ('t', "pleɪn"), ('m', "no markup")),
            });

            BuiltDictionary fixedSequence = BuildIndexedDictionary(fixedSequenceEntries, "tm", 32);
            File.WriteAllBytes(Path.Combine(fixedSequenceDirectory, "fixedseq.dict"), fixedSequence.Payload);
            File.WriteAllBytes(Path.Combine(fixedSequenceDirectory, "fixedseq.idx"), fixedSequence.Index);
            File.WriteAllBytes(
                Path.Combine(fixedSequenceDirectory, "fixedseq.ifo"),
                BuildIfo("Fixed Sequence Test", "sametypesequence=tm", 2, "tm", 32, "2.4.2"));
            fixtures.FixedIfo = Path.Combine(fixedSequenceDirectory, "fixedseq.ifo");
            fixtures.FixedFirst = StarDictDictionary.PhoneticLinePrefix + "fɪkst\nset in place; not movable";
            fixtures.FixedSecond = StarDictDictionary.PhoneticLinePrefix + "pleɪn\nno markup";

            // A .dict.dz that contains a plain single-stream gzip instead of dictzip
            // chunks: the reader must transparently decompress it.
            string gzipFallbackDirectory = Path.Combine(root, "gzipfallback");
            Directory.CreateDirectory(gzipFallbackDirectory);
            List<TestEntry> gzipFallbackEntries = Sorted(new List<TestEntry>
            {
                Entry("gz", ('m', "gzip fallback works")),
                Entry("second", ('m', "second gzip entry")),
            });

            BuiltDictionary gzipFallback = BuildIndexedDictionary(gzipFallbackEntries, "m", 32);
            File.WriteAllBytes(Path.Combine(gzipFallbackDirectory, "gzipfallback.dict.dz"), GzipBytes(gzipFallback.Payload));
            File.WriteAllBytes(Path.Combine(gzipFallbackDirectory, "gzipfallback.idx"), gzipFallback.Index);
            File.WriteAllBytes(
                Path.Combine(gzipFallbackDirectory, "gzipfallback.ifo"),
                BuildIfo("Gzip In Dz Test", "A .dz file that is plain gzip.", 2, "m", 32, "2.4.2"));
            fixtures.GzipFallbackIfo = Path.Combine(gzipFallbackDirectory, "gzipfallback.ifo");

            // Real dictzip: multiple 58315-byte random-access chunks.
            string dictZipDirectory = Path.Combine(root, "dictzip");
            Directory.CreateDirectory(dictZipDirectory);
            const int dictZipEntryCount = 3000;
            var dictZipEntries = new List<TestEntry>(dictZipEntryCount);
            var dictZipExpected = new List<string>(dictZipEntryCount);
            for (int i = 0; i < dictZipEntryCount; i++)
            {
                string definition = BuildDictZipDefinition(i);
                dictZipExpected.Add(definition);
                dictZipEntries.Add(Entry("w" + i.ToString("D4", CultureInfo.InvariantCulture), ('m', definition)));
            }

            dictZipEntries = Sorted(dictZipEntries);
            BuiltDictionary dictZip = BuildIndexedDictionary(dictZipEntries, "m", 32);
            fixtures.DzChunkCount = (dictZip.Payload.Length + DictZipChunkLength - 1) / DictZipChunkLength;
            fixtures.DzExpected.AddRange(dictZipExpected);
            fixtures.DzStraddlingWord = string.Empty;
            fixtures.DzStraddlingExpected = string.Empty;
            for (int i = 0; i < dictZipEntries.Count; i++)
            {
                long offset = dictZip.Offsets[i].Offset;
                int size = dictZip.Offsets[i].Size;
                if (size > 0 && (offset % DictZipChunkLength) + size > DictZipChunkLength)
                {
                    fixtures.DzStraddlingWord = dictZipEntries[i].Word;
                    fixtures.DzStraddlingExpected = dictZipExpected[i];
                    break;
                }
            }

            File.WriteAllBytes(Path.Combine(dictZipDirectory, "dz.dict.dz"), BuildDictZip(dictZip.Payload));
            File.WriteAllBytes(Path.Combine(dictZipDirectory, "dz.idx"), dictZip.Index);
            File.WriteAllBytes(
                Path.Combine(dictZipDirectory, "dz.ifo"),
                BuildIfo("Dictzip Test Dictionary", "Random access fixture.", dictZipEntryCount, "m", 32, "2.4.2"));
            fixtures.DzIfo = Path.Combine(dictZipDirectory, "dz.ifo");

            // Zip archive with a nested dictionary using .idx.gz + .dict.dz.
            const int zipEntryCount = 120;
            var zipEntries = new List<TestEntry>(zipEntryCount);
            for (int i = 0; i < zipEntryCount; i++)
            {
                string index = i.ToString("D4", CultureInfo.InvariantCulture);
                zipEntries.Add(Entry(
                    "zip" + index,
                    ('h', "<b>zip" + index + "</b><br>entry " + i.ToString(CultureInfo.InvariantCulture) + " &amp; more")));
            }

            zipEntries = Sorted(zipEntries);
            BuiltDictionary zip = BuildIndexedDictionary(zipEntries, "h", 32);
            string zipPath = Path.Combine(root, "wordlens-dictionary.zip");
            WriteZip(
                zipPath,
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["nested/wordlens-zipdict.ifo"] = BuildIfo(
                        "Zip Test Dictionary",
                        "Inside zip.\\nSecond line.",
                        zipEntryCount,
                        "h",
                        32,
                        "2.4.2"),
                    ["nested/wordlens-zipdict.idx.gz"] = GzipBytes(zip.Index),
                    ["nested/wordlens-zipdict.dict.dz"] = BuildDictZip(zip.Payload),
                });
            fixtures.ZipPath = zipPath;

            // 64-bit offsets, declared in the .ifo...
            string x64Directory = Path.Combine(root, "idx64");
            Directory.CreateDirectory(x64Directory);
            List<TestEntry> x64Entries = Sorted(new List<TestEntry>
            {
                Entry("one", ('m', "first entry")),
                Entry("three", ('m', "third entry")),
                Entry("two", ('m', "second entry")),
            });

            BuiltDictionary x64 = BuildIndexedDictionary(x64Entries, "m", 64);
            File.WriteAllBytes(Path.Combine(x64Directory, "idx64.dict"), x64.Payload);
            File.WriteAllBytes(Path.Combine(x64Directory, "idx64.idx"), x64.Index);
            File.WriteAllBytes(
                Path.Combine(x64Directory, "idx64.ifo"),
                BuildIfo("64-bit Index Test", "Uses 8-byte offsets.", 3, "m", 64, "3.0.0"));
            fixtures.X64Ifo = Path.Combine(x64Directory, "idx64.ifo");

            // ...and 64-bit offsets without the idxoffsetbits hint (auto-detection path).
            File.WriteAllBytes(Path.Combine(x64Directory, "auto64.dict"), x64.Payload);
            File.WriteAllBytes(Path.Combine(x64Directory, "auto64.idx"), x64.Index);
            File.WriteAllBytes(
                Path.Combine(x64Directory, "auto64.ifo"),
                BuildIfo("64-bit Index Without Hint", "No idxoffsetbits key.", 3, "m", 32, "2.4.2"));
            fixtures.X64AutoIfo = Path.Combine(x64Directory, "auto64.ifo");

            // Invalid inputs.
            string badDirectory = Path.Combine(root, "bad");
            Directory.CreateDirectory(badDirectory);
            File.WriteAllText(Path.Combine(badDirectory, "bad.ifo"), "this is not a StarDict dictionary\r\n");
            fixtures.BadIfoPath = Path.Combine(badDirectory, "bad.ifo");

            string noIndexDirectory = Path.Combine(root, "noindex");
            Directory.CreateDirectory(noIndexDirectory);
            File.WriteAllBytes(Path.Combine(noIndexDirectory, "noindex.dict"), Array.Empty<byte>());
            File.WriteAllBytes(
                Path.Combine(noIndexDirectory, "noindex.ifo"),
                BuildIfo("No Index", string.Empty, 0, "m", 32, "2.4.2"));
            fixtures.NoIndexIfoPath = Path.Combine(noIndexDirectory, "noindex.ifo");

            string emptyIndexDirectory = Path.Combine(root, "emptyindex");
            Directory.CreateDirectory(emptyIndexDirectory);
            File.WriteAllBytes(Path.Combine(emptyIndexDirectory, "emptyindex.dict"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(emptyIndexDirectory, "emptyindex.idx"), Array.Empty<byte>());
            File.WriteAllBytes(
                Path.Combine(emptyIndexDirectory, "emptyindex.ifo"),
                BuildIfo("Empty Index", string.Empty, 0, "m", 32, "2.4.2"));
            fixtures.EmptyIndexIfoPath = Path.Combine(emptyIndexDirectory, "emptyindex.ifo");

            return fixtures;
        }

        private static string BuildDictZipDefinition(int index)
        {
            var builder = new StringBuilder();
            builder.Append("Definition #").Append(index.ToString("D4", CultureInfo.InvariantCulture)).Append(": ");
            int repeats = 1 + (index % 6);
            for (int i = 0; i < repeats; i++)
            {
                builder.Append("the quick brown fox jumps over the lazy dog. ");
            }

            if (index % 11 == 0)
            {
                builder.Append("café — naïve. ");
            }

            if (index % 7 == 0)
            {
                builder.Append('x', 40 + (index % 80));
            }

            // Plain-text formatting trims trailing whitespace per line; mirror that in the
            // expected value so the comparison stays exact.
            return builder.ToString().TrimEnd();
        }

        private static BuiltDictionary BuildIndexedDictionary(List<TestEntry> entries, string? sameTypeSequence, int offsetBits)
        {
            var result = new BuiltDictionary();
            using var payload = new MemoryStream();
            using var index = new MemoryStream();

            foreach (TestEntry entry in entries)
            {
                byte[] data = BuildEntryData(entry, sameTypeSequence);
                long offset = payload.Position;
                payload.Write(data, 0, data.Length);
                result.Offsets.Add((offset, data.Length));

                byte[] word = Encoding.UTF8.GetBytes(entry.Word);
                index.Write(word, 0, word.Length);
                index.WriteByte(0);
                if (offsetBits == 64)
                {
                    WriteInt64BE(index, offset);
                }
                else
                {
                    WriteInt32BE(index, (int)offset);
                }

                WriteInt32BE(index, data.Length);
            }

            result.Payload = payload.ToArray();
            result.Index = index.ToArray();
            return result;
        }

        private static byte[] BuildEntryData(TestEntry entry, string? sameTypeSequence)
        {
            using var output = new MemoryStream();
            if (sameTypeSequence is null)
            {
                foreach ((char Type, string Text) field in entry.Fields)
                {
                    output.WriteByte((byte)field.Type);
                    byte[] text = Encoding.UTF8.GetBytes(field.Text);
                    output.Write(text, 0, text.Length);
                    output.WriteByte(0);
                }

                return output.ToArray();
            }

            if (entry.Fields.Count != sameTypeSequence.Length)
            {
                throw new InvalidOperationException(
                    "Entry '" + entry.Word + "' has " + entry.Fields.Count + " fields but sametypesequence '" +
                    sameTypeSequence + "' requires " + sameTypeSequence.Length + ".");
            }

            for (int i = 0; i < sameTypeSequence.Length; i++)
            {
                (char Type, string Text) field = entry.Fields[i];
                if (field.Type != sameTypeSequence[i])
                {
                    throw new InvalidOperationException(
                        "Entry '" + entry.Word + "' field " + i + " is '" + field.Type +
                        "' but sametypesequence has '" + sameTypeSequence[i] + "'.");
                }

                byte[] text = Encoding.UTF8.GetBytes(field.Text);
                output.Write(text, 0, text.Length);
                if (i != sameTypeSequence.Length - 1)
                {
                    output.WriteByte(0);
                }
            }

            return output.ToArray();
        }

        private static byte[] BuildIfo(
            string bookName,
            string description,
            int wordCount,
            string sameTypeSequence,
            int offsetBits,
            string version)
        {
            var builder = new StringBuilder();
            builder.Append("StarDict's dict ifo file\r\n");
            builder.Append("version=").Append(version).Append("\r\n");
            builder.Append("wordcount=").Append(wordCount.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            builder.Append("idxfilesize=12345678\r\n"); // deliberately wrong; must be ignored
            builder.Append("bookname=").Append(bookName).Append("\r\n");
            builder.Append("sametypesequence=").Append(sameTypeSequence).Append("\r\n");
            if (offsetBits == 64)
            {
                builder.Append("idxoffsetbits=64\r\n");
            }

            builder.Append("description=").Append(description).Append("\r\n");
            builder.Append("author=WordLens tests\r\n");
            builder.Append("email=\r\n");
            builder.Append("website=https://example.invalid/\r\n");
            builder.Append("date=2026.01.01\r\n");
            builder.Append("unknown_key=ignored\r\n");

            byte[] body = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(builder.ToString());
            byte[] withBom = new byte[body.Length + 3];
            withBom[0] = 0xEF;
            withBom[1] = 0xBB;
            withBom[2] = 0xBF;
            Buffer.BlockCopy(body, 0, withBom, 3, body.Length);
            return withBom;
        }

        private static byte[] BuildDictZip(byte[] payload)
        {
            int chunkCount = Math.Max(1, (payload.Length + DictZipChunkLength - 1) / DictZipChunkLength);
            if (chunkCount > ushort.MaxValue)
            {
                throw new InvalidOperationException("Fixture is too large for a dictzip RA header.");
            }

            var compressedChunks = new List<byte[]>(chunkCount);
            int offset = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                int size = Math.Min(DictZipChunkLength, payload.Length - offset);
                using var compressedStream = new MemoryStream();
                using (var deflate = new DeflateStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(payload, offset, size);
                }

                byte[] chunk = compressedStream.ToArray();
                if (chunk.Length == 0 || chunk.Length > ushort.MaxValue)
                {
                    throw new InvalidOperationException("Unexpected compressed chunk size: " + chunk.Length + ".");
                }

                compressedChunks.Add(chunk);
                offset += size;
            }

            using var output = new MemoryStream();
            output.WriteByte(0x1F);
            output.WriteByte(0x8B);
            output.WriteByte(0x08);
            output.WriteByte(0x0C); // FEXTRA | FNAME
            WriteUInt32LE(output, 0); // MTIME
            output.WriteByte(0); // XFL
            output.WriteByte(3); // OS = Unix

            // Extra field: "RA" subfield with version, chunk length, chunk count and sizes.
            WriteUInt16LE(output, 4 + 6 + (chunkCount * 2));
            output.WriteByte((byte)'R');
            output.WriteByte((byte)'A');
            WriteUInt16LE(output, 6 + (chunkCount * 2));
            WriteUInt16LE(output, 1); // dictzip version
            WriteUInt16LE(output, DictZipChunkLength);
            WriteUInt16LE(output, chunkCount);
            foreach (byte[] chunk in compressedChunks)
            {
                WriteUInt16LE(output, chunk.Length);
            }

            // FNAME - exercises the header skipping logic.
            byte[] fileName = Encoding.ASCII.GetBytes("fixture.dict");
            output.Write(fileName, 0, fileName.Length);
            output.WriteByte(0);

            foreach (byte[] chunk in compressedChunks)
            {
                output.Write(chunk, 0, chunk.Length);
            }

            WriteUInt32LE(output, Crc32(payload));
            WriteUInt32LE(output, (uint)payload.Length);
            return output.ToArray();
        }

        private static byte[] GzipBytes(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(data, 0, data.Length);
            }

            return output.ToArray();
        }

        private static void WriteZip(string path, Dictionary<string, byte[]> entries)
        {
            using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);
            foreach (KeyValuePair<string, byte[]> pair in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
                using Stream stream = entry.Open();
                stream.Write(pair.Value, 0, pair.Value.Length);
            }
        }

        private static uint[]? _crcTable;

        private static uint Crc32(byte[] data)
        {
            if (_crcTable is null)
            {
                _crcTable = BuildCrcTable();
            }

            uint[] table = _crcTable;
            uint crc = 0xFFFFFFFFu;
            foreach (byte value in data)
            {
                crc = (crc >> 8) ^ table[(crc ^ value) & 0xFF];
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }

        private static void WriteUInt16LE(Stream stream, int value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
        }

        private static void WriteUInt32LE(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 24) & 0xFF));
        }

        private static void WriteInt32BE(Stream stream, int value)
        {
            stream.WriteByte((byte)((value >> 24) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)(value & 0xFF));
        }

        private static void WriteInt64BE(Stream stream, long value)
        {
            for (int shift = 56; shift >= 0; shift -= 8)
            {
                stream.WriteByte((byte)((value >> shift) & 0xFF));
            }
        }

        private static List<TestEntry> Sorted(IEnumerable<TestEntry> entries)
        {
            return entries.OrderBy(entry => entry.Word, StringComparer.Ordinal).ToList();
        }

        private static TestEntry Entry(string word, params (char Type, string Text)[] fields)
        {
            var entry = new TestEntry(word);
            entry.Fields.AddRange(fields);
            return entry;
        }

        // ------------------------------------------------------------------
        // Tiny assertion harness
        // ------------------------------------------------------------------
        private static void Check(string name, bool condition, string? details = null)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("[PASS] " + name);
            }
            else
            {
                _failed++;
                Console.WriteLine("[FAIL] " + name + (details is null ? string.Empty : " -- " + details));
            }
        }

        private static void CheckEqual(string name, string expected, string actual)
        {
            Check(name, string.Equals(expected, actual, StringComparison.Ordinal),
                "expected: " + Quote(expected) + "; actual: " + Quote(actual));
        }

        private static void CheckEqual(string name, int expected, int actual)
        {
            Check(name, expected == actual, "expected: " + expected.ToString(CultureInfo.InvariantCulture) +
                                            "; actual: " + actual.ToString(CultureInfo.InvariantCulture));
        }

        private static void CheckThrows<TException>(string name, Action action) where TException : Exception
        {
            try
            {
                action();
                Check(name, false, "expected " + typeof(TException).Name + " but nothing was thrown");
            }
            catch (TException)
            {
                Check(name, true);
            }
            catch (Exception exception)
            {
                Check(name, false,
                    "expected " + typeof(TException).Name + " but got " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static string Quote(string value)
        {
            if (value.Length > 220)
            {
                value = value.Substring(0, 220) + "...";
            }

            return "\"" + value
                .Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t") + "\"";
        }

        private static List<KeyValuePair<string, string>> ReadAll(StarDictDictionary dictionary)
        {
            var entries = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> entry in dictionary.ReadEntries())
            {
                entries.Add(entry);
            }

            return entries;
        }

        private static string? FindValue(List<KeyValuePair<string, string>> entries, string word)
        {
            foreach (KeyValuePair<string, string> entry in entries)
            {
                if (string.Equals(entry.Key, word, StringComparison.Ordinal))
                {
                    return entry.Value;
                }
            }

            return null;
        }

        private static string? FindNthValue(List<KeyValuePair<string, string>> entries, string word, int index)
        {
            int seen = 0;
            foreach (KeyValuePair<string, string> entry in entries)
            {
                if (!string.Equals(entry.Key, word, StringComparison.Ordinal))
                {
                    continue;
                }

                if (seen == index)
                {
                    return entry.Value;
                }

                seen++;
            }

            return null;
        }

        private sealed class Fixtures
        {
            public string Root = string.Empty;
            public string PlainIfo = string.Empty;
            public int PlainEntryCount;
            public int PlainIfoDeclaredWordCount;
            public string PlainApple = string.Empty;
            public string PlainHouse = string.Empty;
            public string GzIfo = string.Empty;
            public string FixedIfo = string.Empty;
            public string FixedFirst = string.Empty;
            public string FixedSecond = string.Empty;
            public string GzipFallbackIfo = string.Empty;
            public string DzIfo = string.Empty;
            public string DzStraddlingWord = string.Empty;
            public string DzStraddlingExpected = string.Empty;
            public int DzChunkCount;
            public readonly List<string> DzExpected = new List<string>();
            public string ZipPath = string.Empty;
            public string X64Ifo = string.Empty;
            public string X64AutoIfo = string.Empty;
            public string BadIfoPath = string.Empty;
            public string NoIndexIfoPath = string.Empty;
            public string EmptyIndexIfoPath = string.Empty;
        }

        private sealed class TestEntry
        {
            public TestEntry(string word)
            {
                Word = word;
            }

            public string Word { get; }

            public List<(char Type, string Text)> Fields { get; } = new List<(char Type, string Text)>();
        }

        private sealed class BuiltDictionary
        {
            public byte[] Payload = Array.Empty<byte>();
            public byte[] Index = Array.Empty<byte>();
            public readonly List<(long Offset, int Size)> Offsets = new List<(long Offset, int Size)>();
        }
    }
}
