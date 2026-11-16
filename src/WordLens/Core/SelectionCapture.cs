using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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
    /// UI Automation is always tried first: it is instant and has no side effects. Only PDF
    /// viewers (whose selections UI Automation does not expose) fall back to the Ctrl+C trick,
    /// and that fallback is guarded by a marker file so an interrupted run cannot leave the
    /// clipboard machinery in a bad state — the next start then disables the fallback entirely.
    /// </summary>
    public sealed class SelectionCapture : IDisposable
    {
        private sealed class Request
        {
            public IntPtr Hwnd;
            public int X;
            public int Y;
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

        public Task<CaptureResult> CaptureAsync(IntPtr hwnd, int x, int y)
        {
            Request req = new Request
            {
                Hwnd = hwnd,
                X = x,
                Y = y,
                Tcs = new TaskCompletionSource<CaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            _queue.Add(req);
            return req.Tcs.Task;
        }

        private CaptureResult CaptureCore(Request req)
        {
            Stopwatch sw = Stopwatch.StartNew();

            IntPtr root = Native.GetAncestor(req.Hwnd, 2);
            string title = TextUtil.SafeShort(Native.TitleOf(root != IntPtr.Zero ? root : req.Hwnd), 120);
            if (string.IsNullOrWhiteSpace(title))
                title = TextUtil.SafeShort(Native.TitleOf(req.Hwnd), 120);

            CaptureResult res = new CaptureResult
            {
                X = req.X,
                Y = req.Y,
                ProcessName = Native.ProcessNameOf(req.Hwnd),
                WindowTitle = title
            };

            StringBuilder log = new StringBuilder();
            bool hadPattern = false;
            bool documentLike = false;
            bool staleSelection = false;

            // A double-click can fire before the target app has finished selecting; retry briefly
            // while the point still exposes a text pattern.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                AutomationElement fromPoint = null;
                AutomationElement focused = null;
                try { fromPoint = AutomationElement.FromPoint(new Point(req.X, req.Y)); }
                catch (Exception ex) { log.AppendLine("FromPoint: " + ex.Message); }
                try { focused = AutomationElement.FocusedElement; }
                catch (Exception ex) { log.AppendLine("Focused: " + ex.Message); }

                if (fromPoint != null && IsDocumentLike(fromPoint)) documentLike = true;

                foreach (AutomationElement el in new[] { fromPoint, focused })
                {
                    if (el == null) continue;
                    if (!TryGetSelection(el, req.X, req.Y, out string text, out bool near,
                            out bool rectsKnown, out bool elementHadPattern, log))
                        continue;

                    if (elementHadPattern) hadPattern = true;
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    if (!rectsKnown || near)
                    {
                        res.Text = text;
                        res.Method = "uia";
                        res.NearClick = near;
                        res.TextSurfaceDetected = true;
                        res.ElapsedMs = sw.ElapsedMilliseconds;
                        res.Detail = log.ToString();
                        Log.Info("capture hwnd=" + req.Hwnd + " app=" + res.ProcessName + " method=uia ms=" +
                                 res.ElapsedMs + " text='" + TextUtil.SafeShort(res.Text, 40) + "'");
                        return res;
                    }

                    staleSelection = true;
                    log.AppendLine("UIA selection is not near the click point - treating as stale");
                }

                if (!hadPattern) break;
                Thread.Sleep(30);
                if (sw.ElapsedMilliseconds > 450)
                {
                    log.AppendLine("capture budget reached after " + sw.ElapsedMilliseconds + " ms");
                    break;
                }
            }

            bool isPdf = IsPdfTarget(res);
            res.TextSurfaceDetected = hadPattern || documentLike || isPdf;

            if (staleSelection)
            {
                log.AppendLine("skipping clipboard fallback because UIA reported a stale selection");
                res.Detail = log.ToString();
                res.ElapsedMs = sw.ElapsedMilliseconds;
                return res;
            }

            if (hadPattern && !isPdf)
            {
                log.AppendLine("text pattern present but selection empty - clipboard fallback disabled");
                res.Detail = log.ToString();
                res.ElapsedMs = sw.ElapsedMilliseconds;
                Log.Info("capture hwnd=" + req.Hwnd + " app=" + res.ProcessName +
                         " method=none reason=text-pattern-empty ms=" + res.ElapsedMs);
                return res;
            }

            if (!documentLike && !isPdf)
            {
                log.AppendLine("no text surface detected under the cursor");
                res.Detail = log.ToString();
                res.ElapsedMs = sw.ElapsedMilliseconds;
                Log.Info("capture hwnd=" + req.Hwnd + " app=" + res.ProcessName +
                         " method=none reason=no-text-surface ms=" + res.ElapsedMs);
                return res;
            }

            if (!ClipboardFallbackAllowed(res, log))
            {
                res.Detail = log.ToString();
                res.ElapsedMs = sw.ElapsedMilliseconds;
                Log.Info("capture hwnd=" + req.Hwnd + " app=" + res.ProcessName +
                         " method=none reason=fallback-not-allowed ms=" + res.ElapsedMs);
                return res;
            }

            // Ctrl+C only lands on the window that currently has the foreground; if the user
            // switched away while we were probing UI Automation, skip instead of stealing focus.
            IntPtr foreground = Native.GetForegroundWindow();
            IntPtr rootNow = Native.GetAncestor(req.Hwnd, 2);
            if (foreground != req.Hwnd && foreground != rootNow && foreground != IntPtr.Zero)
            {
                log.AppendLine("foreground changed: " + foreground + " != " + req.Hwnd + " (root " + rootNow + ")");
                res.Detail = log.ToString();
                res.ElapsedMs = sw.ElapsedMilliseconds;
                return res;
            }

            bool restored;
            string clipText = ClipboardFallback(req, log, out restored);
            if (clipText != null)
            {
                res.Text = clipText;
                res.Method = "clipboard";
            }
            res.Detail = log.ToString();
            res.ElapsedMs = sw.ElapsedMilliseconds;
            Log.Info("capture hwnd=" + req.Hwnd + " app=" + res.ProcessName + " method=" + res.Method +
                     " ms=" + res.ElapsedMs + " text='" + TextUtil.SafeShort(res.Text, 40) +
                     "' clipboardRestored=" + restored);
            return res;
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

        private static bool IsPdfTarget(CaptureResult res)
        {
            string proc = NormalizeProcess(res.ProcessName);
            if (DedicatedPdfProcesses.Contains(proc)) return true;
            if (!BrowserProcesses.Contains(proc)) return false;
            // Browsers are only treated as PDF viewers when the document itself says so.
            string title = res.WindowTitle ?? "";
            if (title.IndexOf(".pdf", StringComparison.OrdinalIgnoreCase) < 0)
                return title.IndexOf("PDF", StringComparison.OrdinalIgnoreCase) >= 0;
            return true;
        }

        private bool ClipboardFallbackAllowed(CaptureResult res, StringBuilder log)
        {
            if (_clipboardSafeMode)
            {
                log.AppendLine("clipboard fallback disabled by safe mode after an unfinished fallback");
                return false;
            }
            string mode = (_settings?.ClipboardFallback ?? "pdf-only").Trim().ToLowerInvariant();
            if (mode == "off")
            {
                log.AppendLine("clipboard fallback disabled by setting");
                return false;
            }
            if (mode == "always") return true;
            if (IsPdfTarget(res)) return true;
            log.AppendLine("clipboard fallback skipped for non-PDF app " + NormalizeProcess(res.ProcessName));
            return false;
        }

        /// <summary>
        /// Sends Ctrl+C, waits for the clipboard sequence number to change, then puts the
        /// previous text back. The marker file brackets the whole operation so a crash here is
        /// detected on the next start.
        /// </summary>
        private static string ClipboardFallback(Request req, StringBuilder log, out bool restored)
        {
            restored = false;
            string text = null;
            try
            {
                if (!Native.IsClipboardAvailable())
                {
                    log.AppendLine("clipboard busy - skipping Ctrl+C fallback");
                    return null;
                }

                ClipboardSnapshot snapshot = ClipboardSnapshot.Take();
                if (!snapshot.Captured)
                {
                    log.AppendLine("clipboard snapshot failed - skipping Ctrl+C fallback");
                    return null;
                }

                string marker = AppPaths.ClipboardFallbackMarker;
                try
                {
                    File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
                }
                catch (Exception ex)
                {
                    log.AppendLine("clipboard fallback marker failed: " + ex.Message);
                    return null;
                }

                try
                {
                    uint seq = Native.GetClipboardSequenceNumber();
                    Native.SendCtrlC();
                    text = WaitForClipboard(seq, 320);
                    if (text == null)
                    {
                        log.AppendLine("first ctrl+c produced no clipboard change; retrying");
                        Thread.Sleep(80);
                        Native.SendCtrlC();
                        text = WaitForClipboard(seq, 320);
                    }
                    restored = snapshot.Restore();
                    log.AppendLine("clipboard fallback: " + (text == null ? "no text" : "ok") + ", restored=" + restored);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(marker)) File.Delete(marker);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("clipboard fallback error: " + ex.Message);
            }
            return text;
        }

        private static string WaitForClipboard(uint seq0, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                Thread.Sleep(25);
                waited += 25;
                if (Native.GetClipboardSequenceNumber() != seq0 &&
                    ClipboardCompat.TryGetText(out string text) && !string.IsNullOrWhiteSpace(text))
                    return text;
            }
            return null;
        }

        private static bool TryGetSelection(AutomationElement start, int x, int y, out string text,
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
                            return true;
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
            return hadPattern;
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
