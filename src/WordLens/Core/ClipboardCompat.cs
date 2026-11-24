using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace WordLens.Core
{
    /// <summary>
    /// Minimal, OLE-free clipboard access used for the Ctrl+C fallback.
    ///
    /// WPF's Clipboard.SetDataObject() calls ole32!OleFlushClipboard(), which can
    /// access-violate while serialising Word OLE proxies. Here we only ever read
    /// or write CF_UNICODETEXT through the plain Win32 clipboard API, so OLE is
    /// never involved. Non-text clipboard formats are intentionally not restored
    /// (the previous implementation's all-format snapshot was the crash source).
    /// </summary>
    internal static class ClipboardCompat
    {
        private const uint CF_TEXT = 1;
        private const uint CF_OEMTEXT = 7;
        private const uint CF_UNICODETEXT = 13;
        private const uint CF_LOCALE = 16;
        private const uint GMEM_MOVEABLE = 0x0002;
        private const int MaxTextChars = 4 * 1024 * 1024;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardOwner();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint EnumClipboardFormats(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        /// <summary>
        /// Reads whether the clipboard has content and its Unicode text, without
        /// materialising any OLE/COM format. Returns false if the clipboard stays
        /// busy, or if the text is unreasonably large.
        /// </summary>
        public static bool TrySnapshot(out bool hadContent, out string text, out uint sequence)
        {
            hadContent = false;
            text = null;
            sequence = 0;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    Thread.Sleep(20);
                    continue;
                }
                try
                {
                    // The fallback can restore text only. Leave HTML, images, files and other
                    // clipboard formats alone even when they also have a plain-text version.
                    for (uint format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
                    {
                        hadContent = true;
                        if (format != CF_TEXT && format != CF_OEMTEXT &&
                            format != CF_UNICODETEXT && format != CF_LOCALE)
                            return false;
                    }
                    if (IsClipboardFormatAvailable(CF_UNICODETEXT) && !TryReadOpenClipboard(out text))
                        return false;
                    sequence = Native.GetClipboardSequenceNumber();
                    return sequence != 0;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            return false;
        }

        /// <summary>Reads the viewer's copy and its sequence while the clipboard is open.</summary>
        public static bool TryGetText(IntPtr source, out string text, out uint sequence)
        {
            text = null;
            sequence = 0;
            if (!OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                IntPtr owner = GetClipboardOwner();
                if (owner == IntPtr.Zero) return false;
                int sourcePid = Native.PidOf(source);
                if (sourcePid == 0) return false;
                if (Native.PidOf(owner) != sourcePid)
                {
                    string process = Native.ProcessNameOf(source);
                    if (string.IsNullOrEmpty(process) || !string.Equals(process, Native.ProcessNameOf(owner),
                        StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                bool hasText = IsClipboardFormatAvailable(CF_UNICODETEXT) && TryReadOpenClipboard(out text);
                sequence = Native.GetClipboardSequenceNumber();
                return hasText;
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static bool TryReadOpenClipboard(out string text)
        {
            text = null;
            IntPtr handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return false;
            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return false;
            try
            {
                string value = Marshal.PtrToStringUni(pointer);
                if (value == null || value.Length > MaxTextChars) return false;
                text = value;
                return true;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }

        /// <summary>Replaces the clipboard with the given text, without touching OLE at all.</summary>
        public static bool TrySetText(string text, uint? expectedSequence = null)
        {
            if (text == null) return false;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    Thread.Sleep(20);
                    continue;
                }
                IntPtr hMem = IntPtr.Zero;
                try
                {
                    int bytes = (text.Length + 1) * 2;
                    hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                    if (hMem == IntPtr.Zero) return false;
                    IntPtr p = GlobalLock(hMem);
                    if (p == IntPtr.Zero) return false;
                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                        Marshal.WriteInt16(p, text.Length * 2, 0);
                    }
                    finally
                    {
                        GlobalUnlock(hMem);
                    }
                    if (expectedSequence.HasValue && Native.GetClipboardSequenceNumber() != expectedSequence.Value)
                        return false;
                    if (!EmptyClipboard()) return false;
                    if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero) return false;
                    hMem = IntPtr.Zero; // ownership transferred to the clipboard
                    return true;
                }
                finally
                {
                    if (hMem != IntPtr.Zero) GlobalFree(hMem);
                    CloseClipboard();
                }
            }
            return false;
        }

        /// <summary>Empties the clipboard (used when the original state had no content).</summary>
        public static bool TryClear(uint expectedSequence)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    Thread.Sleep(20);
                    continue;
                }
                try
                {
                    if (Native.GetClipboardSequenceNumber() != expectedSequence) return false;
                    if (EmptyClipboard()) return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            return false;
        }
    }
}
