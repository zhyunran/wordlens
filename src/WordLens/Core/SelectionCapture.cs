using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace WordLens.Core
{
    /// <summary>
    /// Captures the current selection of an arbitrary application.
    ///
    /// UI Automation is tried first. Browser PDF viewers can report old or truncated selections,
    /// so their copy command is used to verify the selection when clipboard fallback is enabled.
    /// </summary>
    public sealed class SelectionCapture : IDisposable
    {
        private sealed class Request
        {
            public IntPtr Hwnd;
            public int X;
            public int Y;
            public Func<bool> IsCurrent;
            public TaskCompletionSource<CaptureResult> Tcs;
        }

        private readonly BlockingCollection<Request> _queue =
            new BlockingCollection<Request>(new ConcurrentQueue<Request>());
        private readonly Thread _thread;
        private readonly AppSettings _settings;
        private readonly bool _clipboardSafeMode;
        private volatile bool _disposed;

        private static readonly HashSet<string> BrowserProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "msedge", "chrome", "firefox", "brave", "vivaldi", "opera"
        };

        private static readonly HashSet<string> DedicatedPdfProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "acrord32", "acrobat", "sumatrapdf", "foxitreader", "pdfxcview", "nitropdf"
        };

        private static readonly Regex PdfFileTitle = new Regex(@"\.pdf(?=$|[\s?#)\]])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private const string ChromiumPdfViewerHost = "mhjfbmdgcfjbbpaeojofohoefgiehjai";

        public SelectionCapture(AppSettings settings = null)
        {
            _settings = settings;

            // If a previous Ctrl+C fallback never cleaned up, the clipboard may still be in an
            // unknown state: run this session on UI Automation only.
            try
            {
                if (File.Exists(AppPaths.ClipboardFallbackMarker))
                {
                    _clipboardSafeMode = true;
                    File.Delete(AppPaths.ClipboardFallbackMarker);
                    Log.Warn("clipboard fallback safe mode: a previous fallback did not finish; " +
                             "fallback disabled for this session");
                }
            }
            catch { }

            _thread = new Thread(Loop) { IsBackground = true, Name = "WordLens.Capture" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void Loop()
        {
            foreach (Request req in _queue.GetConsumingEnumerable())
            {
                CaptureResult result;
                try
                {
                    result = CaptureCore(req);
                }
                catch (Exception ex)
                {
                    Log.Error("Capture failed", ex);
                    result = new CaptureResult
                    {
                        ProcessName = Native.ProcessNameOf(req.Hwnd),
                        Detail = ex.ToString()
                    };
                }
                try { req.Tcs.TrySetResult(result); } catch { }
            }
        }

        public Task<CaptureResult> CaptureAsync(IntPtr hwnd, int x, int y, Func<bool> isCurrent = null)
        {
            Request req = new Request
            {
                Hwnd = hwnd,
                X = x,
                Y = y,
                IsCurrent = isCurrent,
                Tcs = new TaskCompletionSource<CaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            _queue.Add(req);
            return req.Tcs.Task;
        }

        private CaptureResult CaptureCore(Request req)
        {
            Stopwatch sw = Stopwatch.StartNew();

            IntPtr root = Native.GetAncestor(req.Hwnd, 2);
            string title = Native.TitleOf(root != IntPtr.Zero ? root : req.Hwnd);
            if (string.IsNullOrWhiteSpace(title))
                title = Native.TitleOf(req.Hwnd);

            CaptureResult res = new CaptureResult
            {
                X = req.X,
                Y = req.Y,
                ProcessName = Native.ProcessNameOf(req.Hwnd),
                WindowTitle = TextUtil.SafeShort(title, 120)
            };

            StringBuilder log = new StringBuilder();
            string process = NormalizeProcess(res.ProcessName);
            bool isBrowser = BrowserProcesses.Contains(process);
            bool isPdf = DedicatedPdfProcesses.Contains(process);
            string selection = null;
            bool selectionNear = false;
            bool selectionHasBounds = false;
            bool hadPattern = false;
            bool documentLike = false;
            bool staleSelection = false;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (req.IsCurrent?.Invoke() == false) return res;

                AutomationElement fromPoint = null;
                AutomationElement focused = null;
                try { fromPoint = AutomationElement.FromPoint(new Point(req.X, req.Y)); }
                catch (Exception ex) { log.AppendLine("FromPoint: " + ex.Message); }
                try { focused = AutomationElement.FocusedElement; }
                catch (Exception ex) { log.AppendLine("Focused: " + ex.Message); }

                documentLike |= fromPoint != null && IsDocumentLike(fromPoint);
                if (isBrowser && !isPdf)
                    isPdf = IsBrowserPdfContent(fromPoint, title);

                foreach (AutomationElement el in new[] { fromPoint, focused })
                {
                    if (el == null) continue;
                    TryGetSelection(el, req.X, req.Y, out string text, out bool near,
                        out bool rectsKnown, out bool elementHadPattern, log);
                    hadPattern |= elementHadPattern;
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    if (rectsKnown && !near)
                    {
                        staleSelection = true;
                        log.AppendLine("UIA selection bounds are away from the click");
                        continue;
                    }

                    selection = text;
                    selectionNear = near;
                    selectionHasBounds = rectsKnown;
                    break;
                }

                // Prefer the browser's copy command even when UIA reports a plausible PDF range.
                // This also avoids repeatedly reading a range from the previous selection.
                if (selection != null || (isBrowser && isPdf) || !hadPattern || sw.ElapsedMilliseconds > 450)
                    break;
                if (attempt < 2) Thread.Sleep(30);
            }

            res.TextSurfaceDetected = hadPattern || documentLike || isPdf;
            string mode = (_settings?.ClipboardFallback ?? "pdf-only").Trim().ToLowerInvariant();
            bool clipboardEnabled = !_clipboardSafeMode && mode != "off";
            bool verifyBrowserPdf = isBrowser && isPdf && clipboardEnabled;

            // A stale PDF range is a reason to use the viewer's copy command, not to skip it.
            bool tryClipboard = clipboardEnabled &&
                (verifyBrowserPdf || (selection == null && (isPdf || (mode == "always" && documentLike &&
                    !hadPattern && !staleSelection))));
            bool? restored = null;
            if (tryClipboard)
            {
                log.AppendLine(isPdf ? "PDF selection via viewer copy" : "selection via clipboard setting");
                res.Text = ClipboardFallback(req, log, out bool clipboardRestored);
                restored = clipboardRestored;
                if (res.HasText) res.Method = "clipboard";
            }

            // If copy was unavailable, a browser PDF range must at least have bounds at the click.
            if (!res.HasText && selection != null &&
                (!verifyBrowserPdf || (selectionHasBounds && HasTargetForeground(req.Hwnd))))
            {
                res.Text = selection;
                res.Method = "uia";
                res.NearClick = selectionNear;
                res.TextSurfaceDetected = true;
            }

            res.ElapsedMs = sw.ElapsedMilliseconds;
            res.Detail = log.ToString();
            string outcome = res.HasText ? " text='" + TextUtil.SafeShort(res.Text, 40) + "'" :
                " reason=" + (tryClipboard ? "clipboard-no-text" : staleSelection ? "stale-uia" :
                    hadPattern ? "text-pattern-empty" : documentLike || isPdf ? "fallback-not-allowed" : "no-text-surface");
            Log.Info("capture hwnd=" + req.Hwnd + " app=" + res.ProcessName + " method=" + res.Method +
                     " pdf=" + isPdf + " ms=" + res.ElapsedMs + outcome +
                     (restored.HasValue ? " clipboardRestored=" + restored + " detail=" +
                         res.Detail.Trim().Replace(Environment.NewLine, " | ") : ""));
            return res;
        }

        private static bool IsBrowserPdfContent(AutomationElement element, string title)
        {
            // Browser PDF engines can expose a nested document inside a tab whose title and URL
            // contain no PDF filename. Check the document under the pointer, not the focused tab.
            for (int depth = 0; depth < 16 && element != null; depth++)
            {
                try
                {
                    ControlType type = element.Current.ControlType;
                    if (type == ControlType.ToolBar || type == ControlType.Edit ||
                        type == ControlType.TabItem || type == ControlType.MenuItem)
                        return false;

                    if (type == ControlType.Document)
                    {
                        string value = element.GetCurrentPropertyValue(ValuePattern.ValueProperty, true) as string;
                        if (Uri.TryCreate(value, UriKind.Absolute, out Uri uri))
                        {
                            if (uri.Scheme == "chrome-extension" && uri.Host == ChromiumPdfViewerHost)
                                return true;
                            if (uri.IsFile || uri.Scheme == "https" || uri.Scheme == "http")
                                return uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
                        }

                        if (string.Equals(element.Current.Name, "PDF Document", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch { break; }

                try { element = TreeWalker.ControlViewWalker.GetParent(element); }
                catch { break; }
            }

            return PdfFileTitle.IsMatch(title ?? "");
        }

        private static bool IsDocumentLike(AutomationElement el)
        {
            try
            {
                if (el.Current.ControlType == ControlType.Document) return true;
                if ((el.Current.Name ?? "").IndexOf("PDF", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (el.Current.ClassName != null &&
                    el.Current.ClassName.IndexOf("PDF", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { }
            return false;
        }

        private static string NormalizeProcess(string proc)
        {
            proc = (proc ?? "").Trim();
            if (proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                proc = proc.Substring(0, proc.Length - 4);
            return proc;
        }

        /// <summary>Copies the viewer's selection and restores the previous plain-text clipboard.</summary>
        private static string ClipboardFallback(Request req, StringBuilder log, out bool restored)
        {
            restored = false;
            try
            {
                if (!Native.IsClipboardAvailable())
                {
                    log.AppendLine("clipboard busy; skipped copy");
                    return null;
                }

                ClipboardSnapshot snapshot = ClipboardSnapshot.Take();
                if (!snapshot.Captured || (snapshot.HadContent && !snapshot.HasText))
                {
                    log.AppendLine("clipboard has no restorable text snapshot; skipped copy");
                    return null;
                }

                string marker = AppPaths.ClipboardFallbackMarker;
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
                try
                {
                    // The mouse hook reports the second button-down. Some PDF viewers finish
                    // their selection on button-up, so don't send a key chord while it's held.
                    Stopwatch releaseWait = Stopwatch.StartNew();
                    while (releaseWait.ElapsedMilliseconds < 180 &&
                           (Native.IsDown(Native.VK_LBUTTON) || Native.AnyModifierDown()))
                        Thread.Sleep(15);

                    string text = null;
                    uint copySequence = 0;
                    try
                    {
                        if (!CanCopySelection(req, snapshot.Sequence, log)) return null;

                        if (!Native.SendCtrlC())
                        {
                            log.AppendLine("Ctrl+C input rejected");
                            return null;
                        }

                        Stopwatch copyWait = Stopwatch.StartNew();
                        do
                        {
                            Thread.Sleep(25);
                            if (Native.GetClipboardSequenceNumber() != snapshot.Sequence &&
                                ClipboardCompat.TryGetText(req.Hwnd, out text, out copySequence) &&
                                copySequence != snapshot.Sequence && !string.IsNullOrWhiteSpace(text))
                                return text;
                        } while (copyWait.ElapsedMilliseconds < 500);
                        log.AppendLine("viewer copy produced no text");
                        return null;
                    }
                    finally
                    {
                        if (copySequence != 0 && copySequence != snapshot.Sequence)
                        {
                            restored = snapshot.Restore(copySequence);
                            if (!restored) log.AppendLine("clipboard restore skipped or failed");
                        }
                    }
                }
                finally
                {
                    try { File.Delete(marker); }
                    catch (Exception ex) { log.AppendLine("clipboard marker cleanup failed: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("clipboard copy failed: " + ex.Message);
                return null;
            }
        }

        private static bool CanCopySelection(Request req, uint clipboardSequence, StringBuilder log)
        {
            if (!HasTargetForeground(req.Hwnd))
            {
                log.AppendLine("foreground changed; skipped copy");
                return false;
            }

            if (req.IsCurrent?.Invoke() == false || Native.IsDown(Native.VK_LBUTTON) || Native.AnyModifierDown())
            {
                log.AppendLine("input still in progress; skipped copy");
                return false;
            }

            if (Native.GetClipboardSequenceNumber() != clipboardSequence)
            {
                log.AppendLine("clipboard changed after snapshot; skipped copy");
                return false;
            }

            return true;
        }

        private static bool HasTargetForeground(IntPtr hwnd)
        {
            IntPtr foreground = Native.GetForegroundWindow();
            return foreground != IntPtr.Zero &&
                (foreground == hwnd || foreground == Native.GetAncestor(hwnd, 2));
        }

        private static void TryGetSelection(AutomationElement start, int x, int y, out string text,
            out bool near, out bool rectsKnown, out bool hadPattern, StringBuilder log)
        {
            text = null;
            near = true;
            hadPattern = false;
            rectsKnown = false;
            AutomationElement cur = start;
            for (int depth = 0; depth < 10 && cur != null; depth++)
            {
                try
                {
                    if (cur.TryGetCurrentPattern(TextPattern.Pattern, out object patternObj))
                    {
                        hadPattern = true;
                        var tp = (TextPattern)patternObj;
                        TextPatternRange[] ranges = tp.GetSelection();
                        var sb = new StringBuilder();
                        System.Windows.Rect[] rects = null;
                        foreach (TextPatternRange range in ranges)
                        {
                            sb.Append(range.GetText(-1));
                            if (rects == null)
                            {
                                try { rects = range.GetBoundingRectangles(); }
                                catch { }
                            }
                        }
                        string t = sb.ToString();
                        if (!string.IsNullOrWhiteSpace(t))
                        {
                            if (rects != null && rects.Length > 0)
                            {
                                rectsKnown = true;
                                bool contains = false;
                                foreach (System.Windows.Rect r in rects)
                                {
                                    if (r.Contains(x, y) || Distance(r, x, y) <= 16) { contains = true; break; }
                                }
                                near = contains;
                            }
                            text = t;
                            return;
                        }
                        log.AppendLine("TextPattern found but selection empty at " + SafeControlType(cur));
                        break;
                    }
                }
                catch (Exception ex)
                {
                    log.AppendLine("TextPattern error: " + ex.Message);
                }
                try { cur = TreeWalker.ControlViewWalker.GetParent(cur); }
                catch { break; }
            }
        }

        private static string SafeControlType(AutomationElement el)
        {
            try { return el.Current.ControlType.ProgrammaticName; } catch { return "?"; }
        }

        private static double Distance(System.Windows.Rect r, int x, int y)
        {
            double dx = 0, dy = 0;
            if (x < r.Left) dx = r.Left - x; else if (x > r.Right) dx = x - r.Right;
            if (y < r.Top) dy = r.Top - y; else if (y > r.Bottom) dy = y - r.Bottom;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _queue.CompleteAdding(); } catch { }
            try { _thread.Join(500); } catch { }
        }

        /// <summary>Test helper: capture whatever is selected right now in the foreground window.</summary>
        public CaptureResult CaptureAtCursor()
        {
            Native.GetCursorPos(out Native.POINT p);
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) hwnd = Native.WindowFromPoint(p);
            return CaptureAsync(hwnd, p.X, p.Y).GetAwaiter().GetResult();
        }
    }

    /// <summary>Outcome of one capture attempt, including the diagnostic detail log.</summary>
    public sealed class CaptureResult
    {
        public string Text { get; set; }

        public string Method { get; set; } = "none";

        public bool HasText => !string.IsNullOrWhiteSpace(Text);

        public string ProcessName { get; set; } = "";

        public string WindowTitle { get; set; } = "";

        public long ElapsedMs { get; set; }

        public string Detail { get; set; } = "";

        public bool NearClick { get; set; } = true;

        public int X { get; set; }

        public int Y { get; set; }

        public bool TextSurfaceDetected { get; set; }
    }
}
