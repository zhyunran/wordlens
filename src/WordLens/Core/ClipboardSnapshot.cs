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

        public uint Sequence { get; private set; }

        public string Text { get; private set; }

        public bool HasText => Text != null;

        private ClipboardSnapshot(bool captured, bool hadContent, string text, uint sequence)
        {
            Captured = captured;
            HadContent = hadContent;
            Text = text;
            Sequence = sequence;
        }

        /// <summary>Reads the current clipboard. <see cref="Captured"/> is false if it stayed locked.</summary>
        public static ClipboardSnapshot Take()
        {
            if (!ClipboardCompat.TrySnapshot(out bool hadContent, out string text, out uint sequence))
                return new ClipboardSnapshot(false, false, null, 0);
            return new ClipboardSnapshot(true, hadContent, text, sequence);
        }

        /// <summary>Puts the captured text back; retries a few times because the clipboard may be busy.</summary>
        public bool Restore(uint expectedSequence)
        {
            if (!Captured || (HadContent && !HasText))
            {
                Log.Warn("Clipboard restore skipped: snapshot was not captured");
                return false;
            }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Native.GetClipboardSequenceNumber() != expectedSequence) return false;
                    if (HasText ? ClipboardCompat.TrySetText(Text, expectedSequence) :
                        ClipboardCompat.TryClear(expectedSequence))
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
