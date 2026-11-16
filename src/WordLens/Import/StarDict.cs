// StarDict.cs
//
// A dependency-free StarDict reader for WordLens. Supports:
//   - .ifo files next to .idx / .idx.gz and .dict / .dict.dz / .dict.gz
//   - .zip archives that contain a complete dictionary (optionally in a subfolder)
//   - dictzip random access (gzip "RA" extra field, 58315-byte chunks)
//   - 32-bit and 64-bit .idx offsets (StarDict 2.x / 3.0)
//   - both fixed sametypesequence records and type-tagged records
//   - the common StarDict text formats: m, l, h, g, x, k, w, t, y, n
//
// Target: .NET 6 / C# 10. No NuGet dependencies - only System.* assemblies.
//
// Thread-safety: one instance may be enumerated / queried from several threads
// concurrently. Dispose must not race with readers.

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace WordLens.Import
{
    /// <summary>
    /// Read-only access to a StarDict dictionary.
    /// <para>
    /// <see cref="Open(string)"/> accepts either the path of a StarDict <c>.ifo</c> file
    /// (with <c>.idx</c>/<c>.idx.gz</c> and <c>.dict</c>/<c>.dict.dz</c> next to it) or the
    /// path of a <c>.zip</c> archive containing the whole dictionary.
    /// </para>
    /// <para>
    /// Entries are read lazily by <see cref="ReadEntries"/>; dictionary payload is fetched
    /// on demand (including random access into dictzip files).
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// using var dictionary = StarDictDictionary.Open(@"C:\dicts\stardict.ifo");
    /// Console.WriteLine(dictionary.BookName + ": " + dictionary.WordCount + " words");
    /// foreach (var entry in dictionary.ReadEntries())
    /// {
    ///     // entry.Key = headword, entry.Value = definition (may start with __PHONETIC__...)
    ///     Console.WriteLine(entry.Key);
    /// }
    /// </code>
    /// </example>
    public sealed class StarDictDictionary : IDisposable
    {
        /// <summary>
        /// Prefix of the optional phonetic line returned by <see cref="ReadEntries"/>.
        /// The line is <c>__PHONETIC__&lt;phonetic&gt;</c> followed by a newline and the definition.
        /// </summary>
        public const string PhoneticLinePrefix = "__PHONETIC__";

        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

        private readonly IfoMetadata _ifo;
        private readonly byte[] _index;
        private readonly int _offsetBits;
        private readonly IByteSource _dictionary;
        private readonly int _wordCount;
        private readonly string _sourcePath;
        private int _disposed;

        private StarDictDictionary(IfoMetadata ifo, byte[] index, int offsetBits, IByteSource dictionary, int wordCount, string sourcePath)
        {
            _ifo = ifo;
            _index = index;
            _offsetBits = offsetBits;
            _dictionary = dictionary;
            _wordCount = wordCount;
            _sourcePath = sourcePath;
        }

        /// <summary>
        /// Opens a StarDict dictionary from an <c>.ifo</c> file or from a <c>.zip</c> archive.
        /// </summary>
        /// <param name="ifoOrZipPath">
        /// Absolute or relative path of a StarDict <c>.ifo</c> file, or of a <c>.zip</c>
        /// archive containing a complete dictionary. A trailing BOM and CRLF line endings
        /// in the <c>.ifo</c> are tolerated.
        /// </param>
        /// <returns>An open <see cref="StarDictDictionary"/>; the caller owns it and must dispose it.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="ifoOrZipPath"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="ifoOrZipPath"/> is empty or whitespace.</exception>
        /// <exception cref="FileNotFoundException">The file does not exist.</exception>
        /// <exception cref="InvalidDataException">
        /// The file is not a StarDict dictionary, an expected companion file is missing,
        /// or the index cannot be decoded.
        /// </exception>
        /// <exception cref="IOException">An I/O error occurred while reading the dictionary.</exception>
        /// <exception cref="UnauthorizedAccessException">The file cannot be read.</exception>
        public static StarDictDictionary Open(string ifoOrZipPath)
        {
            if (ifoOrZipPath is null)
            {
                throw new ArgumentNullException(nameof(ifoOrZipPath));
            }

            if (ifoOrZipPath.Trim().Length == 0)
            {
                throw new ArgumentException("A StarDict .ifo or .zip path is required.", nameof(ifoOrZipPath));
            }

            string fullPath = Path.GetFullPath(ifoOrZipPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"StarDict file not found: '{fullPath}'.", fullPath);
            }

            return LooksLikeZipArchive(fullPath) ? OpenZip(fullPath) : OpenIfo(fullPath);
        }

        /// <summary>Gets the dictionary book name from the <c>.ifo</c> file (empty when absent).</summary>
        public string BookName => _ifo.BookName;

        /// <summary>
        /// Gets the dictionary description from the <c>.ifo</c> file. Literal <c>\n</c>
        /// escape sequences commonly used by StarDict are converted to real newlines.
        /// </summary>
        public string Description => _ifo.Description;

        /// <summary>
        /// Gets the raw <c>sametypesequence</c> value from the <c>.ifo</c> file
        /// (empty when the dictionary uses type-tagged records).
        /// </summary>
        public string Sametypesequence => _ifo.Sametypesequence;

        /// <summary>
        /// Gets the number of index entries actually present in the <c>.idx</c> data.
        /// This intentionally ignores the (sometimes stale) <c>wordcount</c> header value.
        /// </summary>
        public int WordCount => _wordCount;

        /// <summary>
        /// Gets the absolute path that was passed to <see cref="Open(string)"/>: the
        /// <c>.ifo</c> file for directory-based dictionaries, or the <c>.zip</c> file.
        /// </summary>
        public string SourcePath => _sourcePath;

        /// <summary>
        /// Lazily enumerates all dictionary entries in <c>.idx</c> order.
        /// <para>
        /// Each value is the definition converted to readable plain text: HTML/Pango/XDXF/
        /// KingSoft/MediaWiki markup is removed, HTML entities are decoded, block tags become
        /// line breaks and runs of more than two newlines are collapsed.
        /// </para>
        /// <para>
        /// Phonetics are not merged into the text. When an entry carries an English phonetic
        /// (<c>t</c>) or a Chinese/Japanese phonetic (<c>y</c>), the returned value starts with
        /// a line <c>__PHONETIC__&lt;phonetic&gt;</c> followed by a newline and then the
        /// definition text (which may be empty). Entries without phonetics do not start with
        /// that marker. An importer should split the first line only when it starts with
        /// <see cref="PhoneticLinePrefix"/>.
        /// </para>
        /// <para>
        /// Duplicate headwords (homographs) are yielded as separate entries in index order.
        /// </para>
        /// </summary>
        /// <returns>A lazy sequence of (headword, plain-text definition) pairs.</returns>
        /// <exception cref="ObjectDisposedException">The dictionary has been disposed.</exception>
        /// <exception cref="InvalidDataException">The dictionary payload is truncated or corrupt.</exception>
        public IEnumerable<KeyValuePair<string, string>> ReadEntries()
        {
            ThrowIfDisposed();
            return ReadEntriesCore();
        }

        /// <summary>
        /// Finds an entry whose headword equals <paramref name="word"/> exactly
        /// (ordinal comparison) and returns its formatted definition.
        /// </summary>
        /// <param name="word">Headword to look up. Comparison is case-sensitive and exact.</param>
        /// <returns>The definition text, or <c>null</c> when no entry matches.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="word"/> is <c>null</c>.</exception>
        /// <exception cref="ObjectDisposedException">The dictionary has been disposed.</exception>
        /// <exception cref="InvalidDataException">The dictionary payload is truncated or corrupt.</exception>
        public string? Lookup(string word)
        {
            if (word is null)
            {
                throw new ArgumentNullException(nameof(word));
            }

            ThrowIfDisposed();

            int position = 0;
            while (TryReadIndexEntry(ref position, out string headword, out long offset, out int size))
            {
                if (string.Equals(headword, word, StringComparison.Ordinal))
                {
                    return ReadDefinition(offset, size);
                }
            }

            return null;
        }

        /// <summary>Releases the underlying file handle(s). Calling it more than once is safe.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _dictionary.Dispose();
        }

        private static StarDictDictionary OpenIfo(string ifoPath)
        {
            string directory = Path.GetDirectoryName(ifoPath) ?? Directory.GetCurrentDirectory();
            string baseName = Path.GetFileNameWithoutExtension(ifoPath);

            IfoMetadata metadata = IfoMetadata.Parse(ReadTextFile(ifoPath), ifoPath);

            string? indexFile = FindCompanionFile(directory, baseName, ".idx", ".idx.gz");
            if (indexFile is null)
            {
                throw new InvalidDataException(
                    $"The StarDict index file '{baseName}.idx' (or '{baseName}.idx.gz') was not found next to '{ifoPath}'.");
            }

            string? dictionaryFile = FindCompanionFile(directory, baseName, ".dict", ".dict.dz", ".dict.gz");
            if (dictionaryFile is null)
            {
                throw new InvalidDataException(
                    $"The StarDict dictionary file '{baseName}.dict' (or '{baseName}.dict.dz' / '{baseName}.dict.gz') was not found next to '{ifoPath}'.");
            }

            byte[] index = LoadIndexFile(indexFile);
            IByteSource dictionary = OpenDictionaryFile(dictionaryFile);
            try
            {
                int offsetBits = ResolveIndexLayout(index, metadata.OffsetBits, dictionary.Length, out int wordCount);
                return new StarDictDictionary(metadata, index, offsetBits, dictionary, wordCount, ifoPath);
            }
            catch
            {
                dictionary.Dispose();
                throw;
            }
        }

        private static StarDictDictionary OpenZip(string zipPath)
        {
            using FileStream fileStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using ZipArchive archive = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: false);

            var ifoEntries = new List<ZipArchiveEntry>();
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.Name.Length > 0 && entry.Name.EndsWith(".ifo", StringComparison.OrdinalIgnoreCase))
                {
                    ifoEntries.Add(entry);
                }
            }

            if (ifoEntries.Count == 0)
            {
                throw new InvalidDataException($"The zip archive '{zipPath}' does not contain a StarDict .ifo file.");
            }

            ifoEntries.Sort((left, right) => string.Compare(left.FullName, right.FullName, StringComparison.OrdinalIgnoreCase));

            var failures = new List<string>();
            foreach (ZipArchiveEntry ifoEntry in ifoEntries)
            {
                try
                {
                    return OpenZipDictionary(archive, ifoEntry, zipPath);
                }
                catch (Exception exception) when (exception is InvalidDataException || exception is IOException || exception is UnauthorizedAccessException)
                {
                    failures.Add(exception.Message);
                }
            }

            throw new InvalidDataException(
                $"No usable StarDict dictionary was found in the zip archive '{zipPath}'. " + string.Join(" | ", failures));
        }

        private static StarDictDictionary OpenZipDictionary(ZipArchive archive, ZipArchiveEntry ifoEntry, string zipPath)
        {
            string displayPath = zipPath + "!" + ifoEntry.FullName;
            IfoMetadata metadata = IfoMetadata.Parse(DecodeText(ReadZipEntryBytes(ifoEntry)), displayPath);

            ZipArchiveEntry? indexEntry = FindZipCompanion(archive, ifoEntry.FullName, ".idx", ".idx.gz");
            if (indexEntry is null)
            {
                throw new InvalidDataException(
                    $"The zip archive does not contain the .idx (or .idx.gz) file that belongs to '{ifoEntry.FullName}'.");
            }

            ZipArchiveEntry? dictionaryEntry = FindZipCompanion(archive, ifoEntry.FullName, ".dict", ".dict.dz", ".dict.gz");
            if (dictionaryEntry is null)
            {
                throw new InvalidDataException(
                    $"The zip archive does not contain the .dict (or .dict.dz / .dict.gz) file that belongs to '{ifoEntry.FullName}'.");
            }

            byte[] index = LoadZipIndex(indexEntry);
            IByteSource rawDictionary = new MemoryByteSource(ReadZipEntryBytes(dictionaryEntry));
            IByteSource dictionary = OpenDictionaryPayload(rawDictionary, dictionaryEntry.FullName);
            try
            {
                int offsetBits = ResolveIndexLayout(index, metadata.OffsetBits, dictionary.Length, out int wordCount);
                return new StarDictDictionary(metadata, index, offsetBits, dictionary, wordCount, zipPath);
            }
            catch
            {
                dictionary.Dispose();
                throw;
            }
        }

        private IEnumerable<KeyValuePair<string, string>> ReadEntriesCore()
        {
            int position = 0;
            while (TryReadIndexEntry(ref position, out string headword, out long offset, out int size))
            {
                ThrowIfDisposed();
                yield return new KeyValuePair<string, string>(headword, ReadDefinition(offset, size));
            }
        }

        private bool TryReadIndexEntry(ref int position, out string headword, out long offset, out int size)
        {
            headword = string.Empty;
            offset = 0;
            size = 0;

            int start = position;
            if (start >= _index.Length)
            {
                return false;
            }

            int terminator = Array.IndexOf(_index, (byte)0, start);
            if (terminator < 0)
            {
                position = _index.Length;
                return false;
            }

            int offsetBytes = _offsetBits == 64 ? 8 : 4;
            int valuesStart = terminator + 1;
            if (valuesStart + offsetBytes + 4 > _index.Length)
            {
                position = _index.Length;
                return false;
            }

            headword = Utf8.GetString(_index, start, terminator - start);

            if (offsetBytes == 8)
            {
                ulong rawOffset = BinaryPrimitives.ReadUInt64BigEndian(_index.AsSpan(valuesStart));
                if (rawOffset > long.MaxValue)
                {
                    position = _index.Length;
                    return false;
                }

                offset = (long)rawOffset;
            }
            else
            {
                offset = BinaryPrimitives.ReadUInt32BigEndian(_index.AsSpan(valuesStart));
            }

            uint rawSize = BinaryPrimitives.ReadUInt32BigEndian(_index.AsSpan(valuesStart + offsetBytes));
            size = rawSize > int.MaxValue ? int.MaxValue : (int)rawSize;
            position = valuesStart + offsetBytes + 4;
            return true;
        }

        private string ReadDefinition(long offset, int size)
        {
            long length = _dictionary.Length;
            if (size <= 0 || offset < 0 || offset >= length)
            {
                return string.Empty;
            }

            long available = length - offset;
            int count = size;
            if (count > available)
            {
                count = (int)Math.Min(available, int.MaxValue);
            }

            if (count <= 0)
            {
                return string.Empty;
            }

            byte[] data = new byte[count];
            _dictionary.Read(offset, data, 0, count);
            return EntryTextFormatter.Format(data, _ifo.Sametypesequence);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(StarDictDictionary));
            }
        }

        private static int ResolveIndexLayout(byte[] index, int declaredOffsetBits, long dictionaryLength, out int wordCount)
        {
            IndexScan chosen = ScanIndex(index, declaredOffsetBits, dictionaryLength);
            int offsetBits = declaredOffsetBits;

            if (chosen.EntryCount == 0 || chosen.OutOfBounds > 0 || chosen.AllOffsetsZero)
            {
                int alternateBits = declaredOffsetBits == 64 ? 32 : 64;
                IndexScan alternate = ScanIndex(index, alternateBits, dictionaryLength);
                if (IsBetterIndexScan(alternate, chosen))
                {
                    chosen = alternate;
                    offsetBits = alternateBits;
                }
            }

            if (chosen.EntryCount == 0)
            {
                throw new InvalidDataException(
                    "The StarDict .idx data contains no readable entries. The index may be corrupt or use an unsupported layout.");
            }

            wordCount = chosen.EntryCount;
            return offsetBits;
        }

        private static IndexScan ScanIndex(byte[] index, int offsetBits, long dictionaryLength)
        {
            int offsetBytes = offsetBits == 64 ? 8 : 4;
            int count = 0;
            int outOfBounds = 0;
            bool trailingData = false;
            bool allOffsetsZero = true;
            bool sawNonEmptySize = false;
            int position = 0;

            while (position < index.Length)
            {
                int terminator = Array.IndexOf(index, (byte)0, position);
                if (terminator < 0)
                {
                    trailingData = true;
                    break;
                }

                int valuesStart = terminator + 1;
                if (valuesStart + offsetBytes + 4 > index.Length)
                {
                    trailingData = true;
                    break;
                }

                long offset;
                if (offsetBytes == 8)
                {
                    ulong rawOffset = BinaryPrimitives.ReadUInt64BigEndian(index.AsSpan(valuesStart));
                    offset = rawOffset > long.MaxValue ? long.MaxValue : (long)rawOffset;
                }
                else
                {
                    offset = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(valuesStart));
                }

                uint rawSize = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(valuesStart + offsetBytes));
                count++;

                if (offset != 0)
                {
                    allOffsetsZero = false;
                }

                if (rawSize > 0)
                {
                    sawNonEmptySize = true;
                }

                if (offset < 0 || offset > dictionaryLength || rawSize > dictionaryLength ||
                    offset > dictionaryLength - rawSize)
                {
                    outOfBounds++;
                }

                position = valuesStart + offsetBytes + 4;
            }

            bool suspiciousZeroOffsets = count > 0 && allOffsetsZero && sawNonEmptySize && dictionaryLength > 0;
            return new IndexScan(count, outOfBounds, trailingData, suspiciousZeroOffsets);
        }

        private static bool IsBetterIndexScan(IndexScan candidate, IndexScan current)
        {
            if (candidate.EntryCount == 0)
            {
                return false;
            }

            if (current.EntryCount == 0)
            {
                return true;
            }

            // Only switch to a layout that consumes the whole index with every record
            // inside the payload. This rescues 64-bit indexes whose .ifo omits
            // idxoffsetbits while keeping a valid-but-truncated 32-bit index on its
            // declared layout (its alternate parse is almost always dirty too).
            if (candidate.OutOfBounds > 0 || candidate.TrailingData)
            {
                return false;
            }

            bool currentIsSuspicious = current.OutOfBounds > 0 || current.TrailingData || current.AllOffsetsZero;
            return currentIsSuspicious;
        }

        private static string? FindCompanionFile(string directory, string baseName, params string[] suffixes)
        {
            foreach (string suffix in suffixes)
            {
                string exact = Path.Combine(directory, baseName + suffix);
                if (File.Exists(exact))
                {
                    return exact;
                }
            }

            foreach (string suffix in suffixes)
            {
                string[] matches;
                try
                {
                    matches = Directory.GetFiles(directory, "*" + suffix, SearchOption.TopDirectoryOnly);
                }
                catch (IOException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }

                if (matches.Length == 1)
                {
                    return matches[0];
                }
            }

            return null;
        }

        private static byte[] LoadIndexFile(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                byte[] decompressed = GzipDecompressBytes(raw, path);
                if (decompressed.Length == 0)
                {
                    throw new InvalidDataException($"The StarDict index file '{path}' is empty after decompression.");
                }

                return decompressed;
            }

            if (raw.Length == 0)
            {
                throw new InvalidDataException($"The StarDict index file '{path}' is empty.");
            }

            return raw;
        }

        private static byte[] LoadZipIndex(ZipArchiveEntry entry)
        {
            byte[] raw = ReadZipEntryBytes(entry);
            if (entry.FullName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                byte[] decompressed = GzipDecompressBytes(raw, entry.FullName);
                if (decompressed.Length == 0)
                {
                    throw new InvalidDataException($"The StarDict index entry '{entry.FullName}' is empty after decompression.");
                }

                return decompressed;
            }

            if (raw.Length == 0)
            {
                throw new InvalidDataException($"The StarDict index entry '{entry.FullName}' is empty.");
            }

            return raw;
        }

        private static IByteSource OpenDictionaryFile(string path)
        {
            return OpenDictionaryPayload(new FileByteSource(path), path);
        }

        /// <summary>
        /// Wraps a raw dictionary payload. Takes ownership of <paramref name="source"/>:
        /// it is disposed when the returned source is disposed, or immediately when the
        /// payload has to be decompressed into memory.
        /// </summary>
        private static IByteSource OpenDictionaryPayload(IByteSource source, string displayName)
        {
            if (displayName.EndsWith(".dz", StringComparison.OrdinalIgnoreCase))
            {
                DictZipByteSource? dictZip = DictZipByteSource.TryCreate(source);
                if (dictZip is not null)
                {
                    return dictZip;
                }

                try
                {
                    return new MemoryByteSource(GzipDecompress(source, displayName));
                }
                catch (InvalidDataException exception)
                {
                    throw new InvalidDataException(
                        $"'{displayName}' is neither a valid dictzip (.dict.dz) nor a gzip stream.", exception);
                }
                finally
                {
                    source.Dispose();
                }
            }

            if (displayName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return new MemoryByteSource(GzipDecompress(source, displayName));
                }
                finally
                {
                    source.Dispose();
                }
            }

            return source;
        }

        private static string ReadTextFile(string path)
        {
            return DecodeText(File.ReadAllBytes(path));
        }

        private static string DecodeText(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Utf8.GetString(bytes, 3, bytes.Length - 3);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            return Utf8.GetString(bytes);
        }

        private static byte[] ReadZipEntryBytes(ZipArchiveEntry entry)
        {
            using Stream stream = entry.Open();
            int capacity = entry.Length > 0 && entry.Length <= int.MaxValue ? (int)entry.Length : 0;
            using var memory = new MemoryStream(capacity);
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        private static ZipArchiveEntry? FindZipCompanion(ZipArchive archive, string ifoFullName, params string[] suffixes)
        {
            string prefix = ifoFullName.Substring(0, ifoFullName.Length - ".ifo".Length);

            foreach (string suffix in suffixes)
            {
                string wanted = prefix + suffix;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.Name.Length > 0 && string.Equals(entry.FullName, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry;
                    }
                }
            }

            int slash = prefix.LastIndexOf('/');
            string directoryPrefix = slash >= 0 ? prefix.Substring(0, slash + 1) : string.Empty;

            foreach (string suffix in suffixes)
            {
                ZipArchiveEntry? match = null;
                int matchCount = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.Name.Length == 0)
                    {
                        continue;
                    }

                    if (directoryPrefix.Length > 0 &&
                        !entry.FullName.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string relative = entry.FullName.Substring(directoryPrefix.Length);
                    if (relative.IndexOf('/') >= 0 || !relative.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    match = entry;
                    matchCount++;
                }

                if (matchCount == 1)
                {
                    return match;
                }
            }

            return null;
        }

        private static bool LooksLikeZipArchive(string path)
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length < 4)
                {
                    return false;
                }

                int first = stream.ReadByte();
                int second = stream.ReadByte();
                int third = stream.ReadByte();
                int fourth = stream.ReadByte();
                return first == 'P' && second == 'K' &&
                       ((third == 3 && fourth == 4) || (third == 5 && fourth == 6) || (third == 7 && fourth == 8));
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static byte[] GzipDecompressBytes(byte[] bytes, string displayName)
        {
            try
            {
                using var input = new MemoryStream(bytes, writable: false);
                return GzipDecompress(input);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException($"'{displayName}' is not a valid gzip file.", exception);
            }
        }

        private static byte[] GzipDecompress(IByteSource source, string displayName)
        {
            using var stream = new ByteSourceStream(source);
            try
            {
                return GzipDecompress(stream);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException($"'{displayName}' is not a valid gzip stream.", exception);
            }
        }

        private static byte[] GzipDecompress(Stream input)
        {
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        private static int CountDigits(string value)
        {
            int count = 0;
            while (count < value.Length && char.IsDigit(value[count]))
            {
                count++;
            }

            return count;
        }

        private readonly struct IndexScan
        {
            public IndexScan(int entryCount, int outOfBounds, bool trailingData, bool allOffsetsZero)
            {
                EntryCount = entryCount;
                OutOfBounds = outOfBounds;
                TrailingData = trailingData;
                AllOffsetsZero = allOffsetsZero;
            }

            public int EntryCount { get; }

            public int OutOfBounds { get; }

            public bool TrailingData { get; }

            public bool AllOffsetsZero { get; }
        }

        private interface IByteSource : IDisposable
        {
            long Length { get; }

            void Read(long offset, byte[] buffer, int bufferOffset, int count);
        }

        private sealed class FileByteSource : IByteSource
        {
            private readonly FileStream _stream;
            private readonly long _length;
            private readonly string _path;
            private readonly object _sync = new object();
            private bool _disposed;

            public FileByteSource(string path)
            {
                _path = path;
                _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
                _length = _stream.Length;
            }

            public long Length => _length;

            public void Read(long offset, byte[] buffer, int bufferOffset, int count)
            {
                if (count == 0)
                {
                    return;
                }

                lock (_sync)
                {
                    if (_disposed)
                    {
                        throw new ObjectDisposedException(nameof(FileByteSource));
                    }

                    int total = 0;
                    while (total < count)
                    {
                        int read = RandomAccess.Read(
                            _stream.SafeFileHandle,
                            buffer.AsSpan(bufferOffset + total, count - total),
                            offset + total);
                        if (read <= 0)
                        {
                            throw new InvalidDataException(
                                $"Unexpected end of file while reading '{_path}' at offset {offset + total}.");
                        }

                        total += read;
                    }
                }
            }

            public void Dispose()
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    _stream.Dispose();
                }
            }
        }

        private sealed class MemoryByteSource : IByteSource
        {
            private readonly byte[] _data;

            public MemoryByteSource(byte[] data)
            {
                _data = data ?? throw new ArgumentNullException(nameof(data));
            }

            public long Length => _data.Length;

            public void Read(long offset, byte[] buffer, int bufferOffset, int count)
            {
                if (offset < 0 || count < 0 || offset > _data.Length - count)
                {
                    throw new ArgumentOutOfRangeException(nameof(offset));
                }

                if (bufferOffset < 0 || bufferOffset > buffer.Length - count)
                {
                    throw new ArgumentOutOfRangeException(nameof(bufferOffset));
                }

                Buffer.BlockCopy(_data, (int)offset, buffer, bufferOffset, count);
            }

            public void Dispose()
            {
            }
        }

        /// <summary>
        /// Random-access reader for a dictzip (.dict.dz) payload. Each chunk is independently
        /// deflate-compressed; chunks are decoded on demand and kept in a small LRU cache.
        /// The gzip "RA" extra field supplies the chunk length and the compressed sizes.
        /// </summary>
        private sealed class DictZipByteSource : IByteSource
        {
            private const int ChunkCacheCapacity = 6;
            private const int TrailerSize = 8;

            private readonly IByteSource _compressed;
            private readonly int _chunkLength;
            private readonly int _chunkCount;
            private readonly int[] _chunkSizes;
            private readonly long[] _chunkOffsets;
            private readonly long _length;
            private readonly Dictionary<int, byte[]> _cache = new Dictionary<int, byte[]>();
            private readonly LinkedList<int> _lru = new LinkedList<int>();
            private readonly object _sync = new object();
            private MemoryByteSource? _wholeFile;
            private bool _disposed;

            private DictZipByteSource(IByteSource compressed, long dataOffset, int chunkLength, int chunkCount, int[] chunkSizes)
            {
                _compressed = compressed;
                _chunkLength = chunkLength;
                _chunkCount = chunkCount;
                _chunkSizes = chunkSizes;
                _chunkOffsets = new long[chunkCount];

                long position = dataOffset;
                for (int i = 0; i < chunkCount; i++)
                {
                    _chunkOffsets[i] = position;
                    position += chunkSizes[i];
                }

                byte[] lastChunk = DecompressChunkRaw(chunkCount - 1);
                _length = (long)(chunkCount - 1) * chunkLength + lastChunk.Length;
                AddToCache(chunkCount - 1, lastChunk);
            }

            public long Length => _length;

            public static DictZipByteSource? TryCreate(IByteSource compressed)
            {
                long fileLength = compressed.Length;
                if (fileLength < 18)
                {
                    return null;
                }

                int prefixLength = (int)Math.Min(fileLength, 300000L);
                byte[] prefix = new byte[prefixLength];
                compressed.Read(0, prefix, 0, prefixLength);

                if (prefix[0] != 0x1F || prefix[1] != 0x8B || prefix[2] != 0x08)
                {
                    return null;
                }

                int flags = prefix[3];
                if ((flags & 0x04) == 0)
                {
                    return null;
                }

                int position = 10;
                if (position + 2 > prefix.Length)
                {
                    return null;
                }

                int extraLength = prefix[position] | (prefix[position + 1] << 8);
                position += 2;
                int extraEnd = position + extraLength;
                if (extraEnd > prefix.Length)
                {
                    return null;
                }

                int chunkLength = 0;
                int chunkCount = 0;
                int[]? chunkSizes = null;

                int scan = position;
                while (scan + 4 <= extraEnd)
                {
                    int subFieldId = (prefix[scan] << 8) | prefix[scan + 1];
                    int subFieldLength = prefix[scan + 2] | (prefix[scan + 3] << 8);
                    scan += 4;
                    if (scan + subFieldLength > extraEnd)
                    {
                        break;
                    }

                    if (subFieldId == 0x5241 && subFieldLength >= 6) // "RA"
                    {
                        int version = prefix[scan] | (prefix[scan + 1] << 8);
                        int parsedChunkLength = prefix[scan + 2] | (prefix[scan + 3] << 8);
                        int parsedChunkCount = prefix[scan + 4] | (prefix[scan + 5] << 8);
                        if (version != 1 || parsedChunkLength <= 0 || parsedChunkCount <= 0 ||
                            6 + 2 * parsedChunkCount > subFieldLength)
                        {
                            return null;
                        }

                        int[] sizes = new int[parsedChunkCount];
                        int sizePosition = scan + 6;
                        for (int i = 0; i < parsedChunkCount; i++)
                        {
                            int size = prefix[sizePosition + (i * 2)] | (prefix[sizePosition + (i * 2) + 1] << 8);
                            if (size <= 0)
                            {
                                return null;
                            }

                            sizes[i] = size;
                        }

                        chunkLength = parsedChunkLength;
                        chunkCount = parsedChunkCount;
                        chunkSizes = sizes;
                    }

                    scan += subFieldLength;
                }

                if (chunkSizes is null)
                {
                    return null;
                }

                position = extraEnd;

                if ((flags & 0x08) != 0)
                {
                    while (position < prefix.Length && prefix[position] != 0)
                    {
                        position++;
                    }

                    if (position >= prefix.Length)
                    {
                        return null;
                    }

                    position++;
                }

                if ((flags & 0x10) != 0)
                {
                    while (position < prefix.Length && prefix[position] != 0)
                    {
                        position++;
                    }

                    if (position >= prefix.Length)
                    {
                        return null;
                    }

                    position++;
                }

                if ((flags & 0x02) != 0)
                {
                    position += 2;
                    if (position > prefix.Length)
                    {
                        return null;
                    }
                }

                long dataOffset = position;
                long compressedTotal = 0;
                foreach (int size in chunkSizes)
                {
                    compressedTotal += size;
                }

                long remainder = fileLength - dataOffset - compressedTotal;
                if (remainder != 0 && remainder != TrailerSize)
                {
                    return null;
                }

                try
                {
                    return new DictZipByteSource(compressed, dataOffset, chunkLength, chunkCount, chunkSizes);
                }
                catch (Exception exception) when (exception is InvalidDataException || exception is IOException)
                {
                    return null;
                }
            }

            public void Read(long offset, byte[] buffer, int bufferOffset, int count)
            {
                if (buffer is null)
                {
                    throw new ArgumentNullException(nameof(buffer));
                }

                if (offset < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(offset));
                }

                if (count < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(count));
                }

                if (bufferOffset < 0 || bufferOffset > buffer.Length - count)
                {
                    throw new ArgumentOutOfRangeException(nameof(bufferOffset));
                }

                if (offset > _length || count > _length - offset)
                {
                    throw new ArgumentOutOfRangeException(nameof(offset));
                }

                lock (_sync)
                {
                    if (_disposed)
                    {
                        throw new ObjectDisposedException(nameof(DictZipByteSource));
                    }

                    if (_wholeFile is not null)
                    {
                        _wholeFile.Read(offset, buffer, bufferOffset, count);
                        return;
                    }

                    try
                    {
                        ReadCore(offset, buffer, bufferOffset, count);
                    }
                    catch (Exception exception) when (exception is InvalidDataException || exception is IOException)
                    {
                        byte[] whole;
                        try
                        {
                            whole = GzipDecompress(_compressed, "dictzip");
                        }
                        catch (Exception fallbackException) when (fallbackException is InvalidDataException || fallbackException is IOException)
                        {
                            throw new InvalidDataException(
                                "The .dict.dz file could not be decoded: neither its dictzip chunks nor the whole gzip stream are valid.",
                                exception);
                        }

                        _wholeFile = new MemoryByteSource(whole);
                        _wholeFile.Read(offset, buffer, bufferOffset, count);
                    }
                }
            }

            public void Dispose()
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    _cache.Clear();
                    _lru.Clear();
                    _compressed.Dispose();
                }
            }

            private static byte[]? InflateChunk(byte[] data, int length, int capacity)
            {
                try
                {
                    using var input = new MemoryStream(data, 0, length, writable: false);
                    using var output = new MemoryStream(capacity);
                    using (var inflater = new DeflateStream(input, CompressionMode.Decompress))
                    {
                        inflater.CopyTo(output);
                    }

                    return output.ToArray();
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            }

            private byte[] DecompressChunkRaw(int index)
            {
                int compressedSize = _chunkSizes[index];
                // Append an empty final stored deflate block (BFINAL=1, BTYPE=00).
                // dictzip chunks end at a byte boundary after the encoder's flush, but are not
                // terminated, so this lets DeflateStream finish cleanly. Some writers store each
                // chunk as a complete deflate stream; the fallback retry below handles those.
                byte[] compressed = new byte[compressedSize + 5];
                _compressed.Read(_chunkOffsets[index], compressed, 0, compressedSize);
                compressed[compressedSize] = 0x01;
                compressed[compressedSize + 1] = 0x00;
                compressed[compressedSize + 2] = 0x00;
                compressed[compressedSize + 3] = 0xFF;
                compressed[compressedSize + 4] = 0xFF;

                byte[]? result = InflateChunk(compressed, compressedSize + 5, _chunkLength);
                if (result is null)
                {
                    result = InflateChunk(compressed, compressedSize, _chunkLength);
                }

                if (result is null)
                {
                    throw new InvalidDataException($"StarDict dictzip chunk {index} could not be decompressed.");
                }

                return result;
            }

            private int ExpectedChunkLength(int index)
            {
                if (index == _chunkCount - 1)
                {
                    return (int)(_length - (long)index * _chunkLength);
                }

                return _chunkLength;
            }

            private byte[] DecompressChunk(int index)
            {
                byte[] data = DecompressChunkRaw(index);
                int expected = ExpectedChunkLength(index);
                if (data.Length < expected)
                {
                    throw new InvalidDataException(
                        $"StarDict dictzip chunk {index} decoded to {data.Length} bytes; expected {expected}. The .dict.dz file may be corrupt.");
                }

                if (data.Length > expected)
                {
                    byte[] trimmed = new byte[expected];
                    Buffer.BlockCopy(data, 0, trimmed, 0, expected);
                    data = trimmed;
                }

                return data;
            }

            private byte[] GetChunk(int index)
            {
                if (_cache.TryGetValue(index, out byte[]? cached))
                {
                    _lru.Remove(index);
                    _lru.AddLast(index);
                    return cached;
                }

                byte[] data = DecompressChunk(index);
                AddToCache(index, data);
                return data;
            }

            private void AddToCache(int index, byte[] data)
            {
                _cache[index] = data;
                _lru.AddLast(index);
                while (_cache.Count > ChunkCacheCapacity)
                {
                    LinkedListNode<int>? first = _lru.First;
                    if (first is null)
                    {
                        break;
                    }

                    _lru.RemoveFirst();
                    _cache.Remove(first.Value);
                }
            }

            private void ReadCore(long offset, byte[] buffer, int bufferOffset, int count)
            {
                long position = offset;
                int destination = bufferOffset;
                int remaining = count;

                while (remaining > 0)
                {
                    int chunkIndex = (int)(position / _chunkLength);
                    int withinChunk = (int)(position % _chunkLength);
                    byte[] chunk = GetChunk(chunkIndex);
                    int available = chunk.Length - withinChunk;
                    if (available <= 0)
                    {
                        throw new InvalidDataException(
                            $"StarDict dictzip chunk {chunkIndex} is shorter than the index expects. The .dict.dz file may be corrupt.");
                    }

                    int copy = Math.Min(available, remaining);
                    Buffer.BlockCopy(chunk, withinChunk, buffer, destination, copy);
                    position += copy;
                    destination += copy;
                    remaining -= copy;
                }
            }
        }

        private sealed class ByteSourceStream : Stream
        {
            private readonly IByteSource _source;
            private long _position;

            public ByteSourceStream(IByteSource source)
            {
                _source = source;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => _source.Length;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _source.Length)
                {
                    return 0;
                }

                int available = (int)Math.Min(count, _source.Length - _position);
                if (available <= 0)
                {
                    return 0;
                }

                _source.Read(_position, buffer, offset, available);
                _position += available;
                return available;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class IfoMetadata
        {
            private readonly Dictionary<string, string> _values;

            private IfoMetadata(Dictionary<string, string> values, int offsetBits)
            {
                _values = values;
                OffsetBits = offsetBits;
            }

            public string BookName => GetValue("bookname");

            public string Description => UnescapeDescription(GetValue("description"));

            public string Sametypesequence => GetValue("sametypesequence");

            public int OffsetBits { get; }

            public static IfoMetadata Parse(string text, string displayPath)
            {
                if (text.Length > 0 && text[0] == '\uFEFF')
                {
                    text = text.Substring(1);
                }

                string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                int headerIndex = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    string candidate = lines[i].Trim();
                    if (candidate.Length == 0)
                    {
                        continue;
                    }

                    if (!candidate.StartsWith("StarDict's dict ifo file", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"'{displayPath}' is not a StarDict .ifo file: the first line must be \"StarDict's dict ifo file\".");
                    }

                    headerIndex = i;
                    break;
                }

                if (headerIndex < 0)
                {
                    throw new InvalidDataException($"'{displayPath}' is empty; expected a StarDict .ifo file.");
                }

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = headerIndex + 1; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    values[key] = value;
                }

                int offsetBits = 32;
                if (values.TryGetValue("idxoffsetbits", out string? bitsValue) &&
                    int.TryParse(bitsValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedBits))
                {
                    offsetBits = parsedBits == 64 ? 64 : 32;
                }
                else if (GetVersionMajor(values) >= 3)
                {
                    offsetBits = 64;
                }

                return new IfoMetadata(values, offsetBits);
            }

            private static int GetVersionMajor(Dictionary<string, string> values)
            {
                if (!values.TryGetValue("version", out string? version) || version.Length == 0)
                {
                    return 2;
                }

                int digits = CountDigits(version);
                if (digits > 0 &&
                    int.TryParse(version.Substring(0, digits), NumberStyles.Integer, CultureInfo.InvariantCulture, out int major))
                {
                    return major;
                }

                return 2;
            }

            private static string UnescapeDescription(string value)
            {
                if (value.IndexOf('\\') < 0)
                {
                    return value;
                }

                return value.Replace("\\n", "\n").Replace("\\r", string.Empty).Replace("\\t", "\t");
            }

            private string GetValue(string key)
            {
                return _values.TryGetValue(key, out string? value) ? value : string.Empty;
            }
        }

        /// <summary>
        /// Converts raw StarDict entry payloads into the plain-text contract exposed by
        /// <see cref="ReadEntries"/>.
        /// </summary>
        private static class EntryTextFormatter
        {
            private static readonly Regex HtmlScriptStyleRegex = new Regex(
                @"<\s*(script|style)\b[^>]*>.*?<\s*/\s*\1\s*>",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

            private static readonly Regex HtmlCommentRegex = new Regex(
                "<!--.*?-->",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

            private static readonly Regex HtmlBreakRegex = new Regex(
                @"<\s*(?:br|hr)\s*/?\s*>|<\s*/\s*(?:p|div|li|tr|h[1-6]|blockquote|pre|table|ul|ol|dd|dt)\s*>|<\s*(?:p|div|li|tr|h[1-6]|blockquote|pre|table|ul|ol)\b[^>]*>",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

            private static readonly Regex AnyTagRegex = new Regex(
                "<[^>]*>",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

            private static readonly Regex WikiRefRegex = new Regex(
                @"<\s*ref\b[^>]*>.*?<\s*/\s*ref\s*>",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

            private static readonly Regex WikiLinkWithLabelRegex = new Regex(
                @"\[\[[^\]|]*\|([^\]]*)\]\]",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

            private static readonly Regex WikiLinkRegex = new Regex(
                @"\[\[([^\]]*)\]\]",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

            private static readonly Regex WikiExternalLinkRegex = new Regex(
                @"\[(?:https?|ftp)://[^\s\]]+\s*([^\]]*)\]",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

            private static readonly Regex WikiHeadingRegex = new Regex(
                @"^[ \t]*={1,6}[ \t]*(.*?)[ \t]*={1,6}[ \t]*$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

            private static readonly Regex WikiListMarkerRegex = new Regex(
                @"^[ \t]*[*#:;]+[ \t]*",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

            private static readonly Regex WikiEmphasisRegex = new Regex(
                "'{2,5}",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

            public static string Format(byte[] data, string sameTypeSequence)
            {
                if (data.Length == 0)
                {
                    return string.Empty;
                }

                var definitions = new List<string>();
                string? phonetic = null;

                if (sameTypeSequence.Length > 0)
                {
                    ParseFixedSequence(data, sameTypeSequence, definitions, ref phonetic);
                }
                else
                {
                    ParseTaggedSequence(data, definitions, ref phonetic);
                }

                string body = definitions.Count == 0 ? string.Empty : string.Join("\n", definitions);
                if (!string.IsNullOrEmpty(phonetic))
                {
                    return StarDictDictionary.PhoneticLinePrefix + phonetic + "\n" + body;
                }

                return body;
            }

            private static bool IsBinaryType(char type)
            {
                return type == 'W' || type == 'P' || type == 'X';
            }

            private static void ParseFixedSequence(byte[] data, string types, List<string> definitions, ref string? phonetic)
            {
                int position = 0;
                for (int i = 0; i < types.Length && position < data.Length; i++)
                {
                    char type = types[i];
                    bool last = i == types.Length - 1;

                    if (IsBinaryType(type))
                    {
                        if (position + 4 > data.Length)
                        {
                            break;
                        }

                        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
                        position += 4;
                        int available = data.Length - position;
                        position += (int)Math.Min(declaredLength, (uint)available);
                        continue;
                    }

                    int end;
                    if (last)
                    {
                        end = data.Length;
                    }
                    else
                    {
                        int terminator = Array.IndexOf(data, (byte)0, position);
                        end = terminator >= 0 ? terminator : data.Length;
                    }

                    AddField(type, data.AsSpan(position, end - position), definitions, ref phonetic);

                    if (last)
                    {
                        break;
                    }

                    position = end + 1;
                }
            }

            private static void ParseTaggedSequence(byte[] data, List<string> definitions, ref string? phonetic)
            {
                int position = 0;
                while (position < data.Length)
                {
                    char type = (char)data[position];
                    position++;

                    if (IsBinaryType(type))
                    {
                        if (position + 4 > data.Length)
                        {
                            break;
                        }

                        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
                        position += 4;
                        int available = data.Length - position;
                        position += (int)Math.Min(declaredLength, (uint)available);
                        continue;
                    }

                    int terminator = Array.IndexOf(data, (byte)0, position);
                    int end = terminator >= 0 ? terminator : data.Length;
                    AddField(type, data.AsSpan(position, end - position), definitions, ref phonetic);
                    position = terminator >= 0 ? terminator + 1 : data.Length;
                }
            }

            private static void AddField(char type, ReadOnlySpan<byte> payload, List<string> definitions, ref string? phonetic)
            {
                int length = payload.Length;
                while (length > 0 && payload[length - 1] == 0)
                {
                    length--;
                }

                if (length == 0)
                {
                    return;
                }

                string text = Utf8.GetString(payload.Slice(0, length));
                if (text.IndexOf('\r') >= 0)
                {
                    text = text.Replace("\r\n", "\n").Replace('\r', '\n');
                }

                char lowerType = char.ToLowerInvariant(type);
                if (lowerType == 't' || lowerType == 'y')
                {
                    if (phonetic is null)
                    {
                        string value = NormalizePhonetic(text);
                        if (value.Length > 0)
                        {
                            phonetic = value;
                        }
                    }

                    return;
                }

                switch (lowerType)
                {
                    case 'h':
                        AddDefinition(definitions, HtmlToPlainText(text));
                        break;
                    case 'g':
                        AddDefinition(definitions, MarkupToPlainText(text, false));
                        break;
                    case 'x':
                    case 'k':
                        AddDefinition(definitions, MarkupToPlainText(text, true));
                        break;
                    case 'w':
                        AddDefinition(definitions, WikiToPlainText(text));
                        break;
                    case 'r':
                        break;
                    default:
                        AddDefinition(definitions, NormalizeText(text));
                        break;
                }
            }

            private static void AddDefinition(List<string> definitions, string value)
            {
                if (value.Length > 0)
                {
                    definitions.Add(value);
                }
            }

            private static string NormalizePhonetic(string value)
            {
                string text = AnyTagRegex.Replace(value, " ");
                text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');
                text = NormalizeText(text);
                return text.Replace('\n', ' ').Trim();
            }

            private static string HtmlToPlainText(string value)
            {
                string text = HtmlScriptStyleRegex.Replace(value, " ");
                text = HtmlCommentRegex.Replace(text, " ");
                text = HtmlBreakRegex.Replace(text, "\n");
                text = AnyTagRegex.Replace(text, string.Empty);
                text = WebUtility.HtmlDecode(text);
                text = text.Replace('\u00A0', ' ');
                return NormalizeText(text);
            }

            private static string MarkupToPlainText(string value, bool separateWithSpace)
            {
                string text = HtmlCommentRegex.Replace(value, " ");
                text = AnyTagRegex.Replace(text, separateWithSpace ? " " : string.Empty);
                text = WebUtility.HtmlDecode(text);
                text = text.Replace('\u00A0', ' ');
                return NormalizeText(text);
            }

            private static string WikiToPlainText(string value)
            {
                string text = HtmlCommentRegex.Replace(value, " ");
                text = WikiRefRegex.Replace(text, " ");
                text = HtmlBreakRegex.Replace(text, "\n");
                text = WikiLinkWithLabelRegex.Replace(text, "$1");
                text = WikiLinkRegex.Replace(text, "$1");
                text = WikiExternalLinkRegex.Replace(text, "$1");
                text = WikiHeadingRegex.Replace(text, "$1");
                text = WikiListMarkerRegex.Replace(text, string.Empty);
                text = WikiEmphasisRegex.Replace(text, string.Empty);
                text = AnyTagRegex.Replace(text, string.Empty);
                text = WebUtility.HtmlDecode(text);
                text = text.Replace('\u00A0', ' ');
                return NormalizeText(text);
            }

            private static string NormalizeText(string value)
            {
                if (value.Length == 0)
                {
                    return string.Empty;
                }

                var builder = new StringBuilder(value.Length);
                int start = 0;
                while (start < value.Length)
                {
                    int end = start;
                    while (end < value.Length && value[end] != '\n' && value[end] != '\r')
                    {
                        end++;
                    }

                    AppendCollapsedLine(builder, value, start, end);
                    if (end >= value.Length)
                    {
                        break;
                    }

                    if (value[end] == '\r' && end + 1 < value.Length && value[end + 1] == '\n')
                    {
                        end += 2;
                    }
                    else
                    {
                        end++;
                    }

                    builder.Append('\n');
                    start = end;
                }

                int begin = 0;
                int finish = builder.Length;
                while (begin < finish && (builder[begin] == '\n' || builder[begin] == ' '))
                {
                    begin++;
                }

                while (finish > begin && (builder[finish - 1] == '\n' || builder[finish - 1] == ' '))
                {
                    finish--;
                }

                if (begin >= finish)
                {
                    return string.Empty;
                }

                var result = new StringBuilder(finish - begin);
                int newlineCount = 0;
                for (int i = begin; i < finish; i++)
                {
                    char character = builder[i];
                    if (character == '\n')
                    {
                        newlineCount++;
                        continue;
                    }

                    if (newlineCount > 0)
                    {
                        if (newlineCount >= 2)
                        {
                            result.Append('\n');
                        }

                        result.Append('\n');
                        newlineCount = 0;
                    }

                    result.Append(character);
                }

                return result.ToString();
            }

            private static void AppendCollapsedLine(StringBuilder builder, string value, int start, int end)
            {
                int first = start;
                while (first < end && IsInlineWhiteSpace(value[first]))
                {
                    first++;
                }

                int last = end;
                while (last > first && IsInlineWhiteSpace(value[last - 1]))
                {
                    last--;
                }

                bool pendingSpace = false;
                for (int i = first; i < last; i++)
                {
                    char character = value[i];
                    if (IsInlineWhiteSpace(character))
                    {
                        pendingSpace = true;
                        continue;
                    }

                    if (pendingSpace)
                    {
                        builder.Append(' ');
                        pendingSpace = false;
                    }

                    builder.Append(character);
                }
            }

            private static bool IsInlineWhiteSpace(char character)
            {
                return character == '\0' || char.IsWhiteSpace(character);
            }
        }
    }
}
