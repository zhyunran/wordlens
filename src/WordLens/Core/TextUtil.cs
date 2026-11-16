using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace WordLens.Core
{
    /// <summary>Selection text cleaning / script detection / lemma candidate generation.</summary>
    public static class TextUtil
    {
        private static readonly char[] TrimChars = new char[]
        {
            ' ', '\t', '\r', '\n', '\u00a0', '\u3000',
            '"', '\'', '\u201c', '\u201d', '\u2018', '\u2019',
            '(', ')', '[', ']', '{', '}', '<', '>',
            '.', ',', ';', ':', '!', '?', '\u2026',
            '\u3002', '\uff0c', '\uff1b', '\uff1a', '\uff01', '\uff1f', '\u3001',
            '\u300a', '\u300b', '\u3008', '\u3009', '\u300c', '\u300d', '\u300e', '\u300f',
            '-', '\u2014', '\u2013', '\u00b7', '\u2022', '|', '/', '\\', '~', '`', '^', '*', '=', '+'
        };

        public static bool ContainsCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (IsCjk(c)) return true;
            return false;
        }

        public static bool IsCjk(char c)
        {
            return (c >= '\u3400' && c <= '\u9fff') || (c >= '\uf900' && c <= '\ufaff') ||
                   (c >= '\u3040' && c <= '\u30ff') || (c >= '\uac00' && c <= '\ud7af');
        }

        public static string NormalizeApostrophes(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u02bc', '\'');
        }

        /// <summary>
        /// Cleans raw selected text. Returns null when the selection is not a sensible lookup target.
        /// </summary>
        public static string CleanSelection(string raw, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Replace("\u00a0", " ").Replace("\u3000", " ");
            // collapse whitespace runs inside a line, but keep different lines separate
            s = Regex.Replace(s, @"[ \t]+", " ");
            s = s.Trim();
            if (s.Length == 0) return null;
            if (s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0) return null; // multi-line selections are ignored
            s = s.Trim(TrimChars);
            s = s.Trim();
            if (s.Length == 0) return null;
            if (s.Length > maxChars) return null;
            int letters = 0;
            foreach (char c in s) if (char.IsLetterOrDigit(c)) letters++;
            if (letters == 0) return null;
            // reject any remaining control characters
            foreach (char c in s) if (char.IsControl(c)) return null;
            return s;
        }

        /// <summary>Returns lookup variants (exact first, then normalized variants).</summary>
        public static List<string> Variants(string query)
        {
            var list = new List<string>();
            void Add(string v)
            {
                if (string.IsNullOrWhiteSpace(v)) return;
                v = v.Trim();
                if (v.Length == 0) return;
                if (!list.Contains(v)) list.Add(v);
                string lower = v.ToLowerInvariant();
                if (!list.Contains(lower)) list.Add(lower);
            }
            string q = NormalizeApostrophes(query.Trim());
            Add(q);
            string swapped = q.Replace('-', ' ');
            if (swapped != q) Add(swapped);
            string nospace = q.Replace("-", "").Replace(" ", "");
            if (nospace != q) Add(nospace);
            if (q.EndsWith("'s", StringComparison.OrdinalIgnoreCase) && q.Length > 3)
                Add(q.Substring(0, q.Length - 2));
            return list;
        }

        /// <summary>Naive English inflection candidates, most specific first.</summary>
        public static List<string> LemmaCandidates(string word)
        {
            var res = new List<string>();
            string w = word.ToLowerInvariant();
            if (w.Length < 3) return res;
            void Add(string s) { if (s.Length >= 2 && !res.Contains(s)) res.Add(s); }

            if (w.EndsWith("ies") && w.Length > 3) Add(w.Substring(0, w.Length - 3) + "y");
            if (w.EndsWith("ied") && w.Length > 3) Add(w.Substring(0, w.Length - 3) + "y");
            if (w.EndsWith("ves") && w.Length > 3)
            {
                Add(w.Substring(0, w.Length - 3) + "f");
                Add(w.Substring(0, w.Length - 3) + "fe");
            }
            if (w.EndsWith("es") && w.Length > 2) Add(w.Substring(0, w.Length - 2));
            if (w.EndsWith("s") && !w.EndsWith("ss") && w.Length > 2) Add(w.Substring(0, w.Length - 1));
            if (w.EndsWith("ing") && w.Length > 4)
            {
                Add(w.Substring(0, w.Length - 3));
                Add(w.Substring(0, w.Length - 3) + "e");
                if (w.Length > 5 && w[w.Length - 4] == w[w.Length - 5])
                    Add(w.Substring(0, w.Length - 4));
            }
            if (w.EndsWith("ed") && w.Length > 3)
            {
                Add(w.Substring(0, w.Length - 2));
                Add(w.Substring(0, w.Length - 1));
                if (w.Length > 4 && w[w.Length - 3] == w[w.Length - 4])
                    Add(w.Substring(0, w.Length - 3));
            }
            if (w.EndsWith("er") && w.Length > 3) Add(w.Substring(0, w.Length - 2));
            if (w.EndsWith("est") && w.Length > 4) Add(w.Substring(0, w.Length - 3));
            if (w.EndsWith("ly") && w.Length > 3) Add(w.Substring(0, w.Length - 2));
            return res;
        }

        public static string SafeShort(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "\u2026";
        }
    }

    public static class PhoneticUtil
    {
        /// <summary>
        /// ECDICT phonetics use legacy ASCII glyphs (ә, ', :, ,). Render them as modern IPA
        /// without double-converting values that already contain IPA stress marks.
        /// </summary>
        public static string Pretty(string phonetic, bool chinese)
        {
            if (string.IsNullOrWhiteSpace(phonetic)) return "";
            string p = phonetic.Trim().Trim('/');
            if (chinese) return p;
            p = p.Replace('\u04d9', '\u0259'); // cyrillic schwa -> IPA schwa
            p = p.Replace('\u04d8', '\u018e');
            p = p.Replace("'", "\u02c8");       // primary stress
            p = p.Replace(",", "\u02cc");       // secondary stress
            p = p.Replace(":", "\u02d0");       // length mark
            p = p.Replace("g", "\u0261");       // IPA script g (optional; ECDICT uses g)
            p = Regex.Replace(p, @"\s+", " ");
            return p.Trim();
        }

        public static string WithSlashes(string phonetic, bool chinese)
        {
            string p = Pretty(phonetic, chinese);
            if (string.IsNullOrEmpty(p)) return "";
            if (chinese) return p;
            return "/" + p + "/";
        }
    }
}
