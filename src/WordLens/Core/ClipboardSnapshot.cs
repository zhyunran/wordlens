using System;
using System.Threading;

namespace WordLens.Core
{
    /// <summary>
    /// Text-only snapshot of the clipboard, taken before the Ctrl+C fallback so the user's
    /// original clipboard can be put back afterwards. Deliberately narrower than a full
    /// all-format snapshot: rich formats go through OLE and were the source of the crash.
    /// </summary>
    public sealed class ClipboardSnapshot
    {
        public bool Captured { get; private set; }

        public bool HadContent { get; private set; }

        public string Text { get; private set; }

        public bool HasText => !string.IsNullOrEmpty(Text);

        private ClipboardSnapshot(bool captured, bool hadContent, string text)
        {
            Captured = captured;
            HadContent = hadContent;
            Text = text;
        }

        /// <summary>Reads the current clipboard. <see cref="Captured"/> is false if it stayed locked.</summary>
        public static ClipboardSnapshot Take()
        {
            if (!ClipboardCompat.TrySnapshot(out bool hadContent, out string text))
                return new ClipboardSnapshot(false, false, null);
            return new ClipboardSnapshot(true, hadContent, text);
        }

        /// <summary>Puts the captured text back; retries a few times because the clipboard may be busy.</summary>
        public bool Restore()
        {
            if (!Captured)
            {
                Log.Warn("Clipboard restore skipped: snapshot was not captured");
                return false;
            }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (HasText ? ClipboardCompat.TrySetText(Text) : ClipboardCompat.TryClear())
                        return true;
                }
                catch (Exception ex)
                {
                    Log.Warn("Clipboard restore attempt failed: " + ex.Message);
                }
                Thread.Sleep(35);
            }
            Log.Warn("Clipboard restore failed");
            return false;
        }
    }
}
