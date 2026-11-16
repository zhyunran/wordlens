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
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;
        private const int MaxTextChars = 4 * 1024 * 1024;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

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
        public static bool TrySnapshot(out bool hadContent, out string text)
        {
            hadContent = false;
            text = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    Thread.Sleep(20);
                    continue;
                }
                try
                {
                    uint fmt = 0;
                    int count = 0;
                    while ((fmt = EnumClipboardFormats(fmt)) != 0)
                    {
                        count++;
                        if (count > 4096) break;
                    }
                    hadContent = count > 0;
                    if (!hadContent || !IsClipboardFormatAvailable(CF_UNICODETEXT))
                        return true;

                    IntPtr h = GetClipboardData(CF_UNICODETEXT);
                    if (h == IntPtr.Zero) return true;
                    IntPtr p = GlobalLock(h);
                    if (p == IntPtr.Zero) return true;
                    try
                    {
                        string s = Marshal.PtrToStringUni(p);
                        if (s != null && s.Length > MaxTextChars)
                            return false;
                        text = s;
                    }
                    finally
                    {
                        GlobalUnlock(h);
                    }
                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            return false;
        }

        /// <summary>Reads CF_UNICODETEXT; used to poll the clipboard after sending Ctrl+C.</summary>
        public static bool TryGetText(out string text)
        {
            text = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    Thread.Sleep(15);
                    continue;
                }
                try
                {
                    if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return false;
                    IntPtr h = GetClipboardData(CF_UNICODETEXT);
                    if (h == IntPtr.Zero) return false;
                    IntPtr p = GlobalLock(h);
                    if (p == IntPtr.Zero) return false;
                    try
                    {
                        string s = Marshal.PtrToStringUni(p);
                        if (string.IsNullOrEmpty(s)) return false;
                        text = s;
                        return true;
                    }
                    finally
                    {
                        GlobalUnlock(h);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            return false;
        }

        /// <summary>Replaces the clipboard with the given text, without touching OLE at all.</summary>
        public static bool TrySetText(string text)
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
                    if (!EmptyClipboard()) return false;
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
        public static bool TryClear()
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
