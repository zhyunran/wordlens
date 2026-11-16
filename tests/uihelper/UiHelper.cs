using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

// UI automation helper for WordLens end-to-end testing.
// All screen coordinates are PHYSICAL pixels (process is PerMonitorV2 aware).
class UiHelper
{
    // ---------------- Win32 ----------------
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr val);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT_KEYBD[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int idx);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern int GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    delegate bool EnumProc(IntPtr h, IntPtr lp);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT_KEYBD { public uint type; public KEYBDINPUT ki; public ulong pad; }

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_UNICODE = 0x0004;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    const uint MOUSEEVENTF_WHEEL = 0x0800;
    const uint GA_ROOT = 2;

    static void InitDpi()
    {
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
        try { SetProcessDPIAware(); } catch { }
    }

    static int VK(string name)
    {
        name = name.ToLowerInvariant();
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c >= 'A' && c <= 'Z') return c;
            if (c >= '0' && c <= '9') return c;
        }
        switch (name)
        {
            case "ctrl": case "control": return 0x11;
            case "shift": return 0x10;
            case "alt": return 0x12;
            case "win": case "lwin": return 0x5B;
            case "enter": case "return": return 0x0D;
            case "tab": return 0x09;
            case "esc": case "escape": return 0x1B;
            case "space": return 0x20;
            case "back": case "backspace": return 0x08;
            case "delete": case "del": return 0x2E;
            case "up": return 0x26;
            case "down": return 0x28;
            case "left": return 0x25;
            case "right": return 0x27;
            case "home": return 0x24;
            case "end": return 0x23;
            case "pageup": return 0x21;
            case "pagedown": return 0x22;
            case "insert": return 0x2D;
            case "f1": return 0x70; case "f2": return 0x71; case "f3": return 0x72; case "f4": return 0x73;
            case "f5": return 0x74; case "f6": return 0x75; case "f7": return 0x76; case "f8": return 0x77;
            case "f9": return 0x78; case "f10": return 0x79; case "f11": return 0x7A; case "f12": return 0x7B;
            case "-": return 0xBD; case "=": return 0xBB; case ",": return 0xBC; case ".": return 0xBE; case "/": return 0xBF;
            case ";": return 0xBA; case "'": return 0xDE; case "[": return 0xDB; case "]": return 0xDD; case "\\": return 0xDC; case "`": return 0xC0;
        }
        throw new Exception("unknown key " + name);
    }

    static void SendKeyVk(int vk, bool up)
    {
        INPUT_KEYBD[] a = new INPUT_KEYBD[1];
        a[0].type = INPUT_KEYBOARD;
        a[0].ki.vk = (ushort)vk;
        a[0].ki.flags = up ? KEYEVENTF_KEYUP : 0;
        SendInput(1, a, Marshal.SizeOf(typeof(INPUT_KEYBD)));
    }

    static void SendUnicodeChar(char ch)
    {
        INPUT_KEYBD[] a = new INPUT_KEYBD[2];
        for (int i = 0; i < 2; i++)
        {
            a[i].type = INPUT_KEYBOARD;
            a[i].ki.vk = 0;
            a[i].ki.scan = ch;
            a[i].ki.flags = KEYEVENTF_UNICODE | (i == 1 ? KEYEVENTF_KEYUP : 0);
        }
        SendInput(2, a, Marshal.SizeOf(typeof(INPUT_KEYBD)));
    }

    static void TypeText(string text)
    {
        foreach (char ch in text)
        {
            if (ch == '\n') { SendKeyVk(0x0D, false); SendKeyVk(0x0D, true); Thread.Sleep(15); }
            else if (ch == '\t') { SendKeyVk(0x09, false); SendKeyVk(0x09, true); Thread.Sleep(15); }
            else if (ch == '\r') { }
            else { SendUnicodeChar(ch); Thread.Sleep(2); }
        }
    }

    static void KeyCombo(string spec)
    {
        string[] parts = spec.Split('+');
        List<int> mods = new List<int>();
        int main = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i].Trim();
            int vk = VK(p);
            if (i < parts.Length - 1) mods.Add(vk); else main = vk;
        }
        for (int i = 0; i < mods.Count; i++) SendKeyVk(mods[i], false);
        if (main != 0) { SendKeyVk(main, false); SendKeyVk(main, true); }
        for (int i = mods.Count - 1; i >= 0; i--) SendKeyVk(mods[i], true);
    }

    static void ClickAt(int x, int y, bool right)
    {
        SetCursorPos(x, y);
        Thread.Sleep(20);
        if (right)
        {
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, IntPtr.Zero);
        }
        else
        {
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        }
    }

    static void DoubleClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(30);
        for (int i = 0; i < 2; i++)
        {
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(25);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(60);
        }
    }

    // ---------------- UIA ----------------
    static string Describe(AutomationElement e)
    {
        if (e == null) return "null";
        try
        {
            return "Name='" + e.Current.Name + "' Class='" + e.Current.ClassName + "' Type=" + e.Current.ControlType.ProgrammaticName +
                   " AutoId='" + e.Current.AutomationId + "' Rect=" + e.Current.BoundingRectangle;
        }
        catch (Exception ex) { return "ERR " + ex.Message; }
    }

    static string SelectionFromElement(AutomationElement el, StringBuilder trace)
    {
        AutomationElement cur = el;
        for (int depth = 0; depth < 10 && cur != null; depth++)
        {
            try
            {
                object pat;
                if (cur.TryGetCurrentPattern(TextPattern.Pattern, out pat))
                {
                    TextPattern tp = (TextPattern)pat;
                    TextPatternRange[] ranges = tp.GetSelection();
                    StringBuilder sb = new StringBuilder();
                    foreach (TextPatternRange r in ranges) sb.Append(r.GetText(-1));
                    string s = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        trace.AppendLine("UIA-TextPattern@" + depth + " on " + Describe(cur));
                        return s;
                    }
                }
            }
            catch (Exception ex) { trace.AppendLine("TextPattern@" + depth + " err: " + ex.Message); }
            try { cur = TreeWalker.ControlViewWalker.GetParent(cur); }
            catch { break; }
        }
        return null;
    }

    static string UiaSelection(int x, int y, StringBuilder trace)
    {
        // 1) focused element
        try
        {
            AutomationElement f = AutomationElement.FocusedElement;
            trace.AppendLine("Focused: " + Describe(f));
            string s = SelectionFromElement(f, trace);
            if (s != null) return s;
        }
        catch (Exception ex) { trace.AppendLine("Focused err: " + ex.Message); }
        // 2) element under point (when coords provided)
        if (x >= 0)
        {
            try
            {
                AutomationElement p = AutomationElement.FromPoint(new System.Windows.Point(x, y));
                trace.AppendLine("Point: " + Describe(p));
                string s = SelectionFromElement(p, trace);
                if (s != null) return s;
            }
            catch (Exception ex) { trace.AppendLine("Point err: " + ex.Message); }
        }
        return null;
    }

    static IDataObject SnapshotClipboard()
    {
        try
        {
            IDataObject d = Clipboard.GetDataObject();
            if (d == null) return null;
            DataObject copy = new DataObject();
            string[] formats = d.GetFormats(false);
            foreach (string f in formats)
            {
                try
                {
                    object o = d.GetData(f);
                    if (o != null) copy.SetData(f, o);
                }
                catch { }
            }
            return copy;
        }
        catch { return null; }
    }

    static void RestoreClipboard(IDataObject snap)
    {
        if (snap == null) return;
        for (int i = 0; i < 3; i++)
        {
            try { Clipboard.SetDataObject(snap, true); return; }
            catch { Thread.Sleep(80); }
        }
    }

    static string CopyViaClipboard(StringBuilder trace, out bool clipboardWasRestored)
    {
        clipboardWasRestored = false;
        try
        {
            IDataObject snap = SnapshotClipboard();
            uint seq0 = GetClipboardSequenceNumber();
            KeyCombo("ctrl+c");
            string got = null;
            for (int i = 0; i < 12; i++)
            {
                Thread.Sleep(40);
                uint seq1 = GetClipboardSequenceNumber();
                if (seq1 != seq0)
                {
                    try { if (Clipboard.ContainsText()) got = Clipboard.GetText(); } catch { }
                    break;
                }
            }
            trace.AppendLine("clipboard fallback seq changed: " + (got != null));
            RestoreClipboard(snap);
            clipboardWasRestored = snap != null;
            return got;
        }
        catch (Exception ex)
        {
            trace.AppendLine("clipboard fallback err: " + ex.Message);
            return null;
        }
    }

    // ---------------- window utils ----------------
    static List<string> ListWindows()
    {
        List<string> res = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr lp)
        {
            if (!IsWindowVisible(h)) return true;
            try
            {
                RECT r; GetWindowRect(h, out r);
                StringBuilder t = new StringBuilder(512); GetWindowText(h, t, 512);
                StringBuilder c = new StringBuilder(256); GetClassName(h, c, 256);
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (r.R - r.L <= 0) return true;
                res.Add(h.ToInt64() + "\t" + pid + "\t" + r.L + "," + r.T + "," + (r.R - r.L) + "," + (r.B - r.T) + "\t" + c + "\t" + t);
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return res;
    }

    static IntPtr FindWindowByTitle(string sub)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr lp)
        {
            if (!IsWindowVisible(h)) return true;
            StringBuilder t = new StringBuilder(512); GetWindowText(h, t, 512);
            if (t.ToString().IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static void ForceForeground(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9);
        uint pidDummy;
        uint tid = GetWindowThreadProcessId(h, out pidDummy);
        uint cur = GetCurrentThreadId();
        IntPtr fg = GetForegroundWindow();
        uint p2;
        uint tidFg = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out p2) : 0;
        try { AttachThreadInput(cur, tid, true); AttachThreadInput(cur, tidFg, true); } catch { }
        ShowWindow(h, 5);
        SetForegroundWindow(h);
        Thread.Sleep(150);
        try { AttachThreadInput(cur, tid, false); AttachThreadInput(cur, tidFg, false); } catch { }
    }

    static void Screenshot(string path, int x, int y, int w, int h)
    {
        if (w <= 0) { w = GetSystemMetrics(78); x = GetSystemMetrics(76); } // SM_CXVIRTUALSCREEN / SM_XVIRTUALSCREEN
        if (h <= 0) { h = GetSystemMetrics(79); y = GetSystemMetrics(77); }
        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }

    static string JsonEscape(string s)
    {
        if (s == null) return "null";
        StringBuilder sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    static void WriteOut(string outPath, Dictionary<string, string> kv, StringBuilder log)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{");
        bool first = true;
        foreach (KeyValuePair<string, string> p in kv)
        {
            if (!first) sb.Append(",");
            first = false;
            sb.Append(JsonEscape(p.Key)).Append(":").Append(p.Value == null ? "null" : JsonEscape(p.Value));
        }
        if (log != null && log.Length > 0)
        {
            sb.Append(",").Append(JsonEscape("log")).Append(":").Append(JsonEscape(log.ToString()));
        }
        sb.Append("}");
        if (outPath != null) File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine(sb.ToString());
    }

    [STAThread]
    static int Main(string[] args)
    {
        InitDpi();
        StringBuilder log = new StringBuilder();
        Dictionary<string, string> outKv = new Dictionary<string, string>();
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        string outPath = null;
        List<string> a = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) { outPath = args[++i]; }
            else a.Add(args[i]);
        }
        try
        {
            switch (cmd)
            {
                case "move":
                    SetCursorPos(int.Parse(a[0]), int.Parse(a[1]));
                    break;
                case "click":
                    ClickAt(int.Parse(a[0]), int.Parse(a[1]), a.Count > 2 && a[2] == "right");
                    break;
                case "dblclick":
                    DoubleClickAt(int.Parse(a[0]), int.Parse(a[1]));
                    break;
                case "type":
                    TypeText(Encoding.UTF8.GetString(Convert.FromBase64String(a[0])));
                    break;
                case "key":
                    KeyCombo(a[0]);
                    break;
                case "wait":
                    Thread.Sleep(int.Parse(a[0]));
                    break;
                case "wheel":
                    mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)int.Parse(a[0])), IntPtr.Zero);
                    break;
                case "shot":
                    if (a.Count >= 5) Screenshot(a[0], int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]));
                    else Screenshot(a[0], 0, 0, 0, 0);
                    outKv["file"] = a[0];
                    break;
                case "clipget":
                    outKv["text"] = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                    break;
                case "clipset":
                    Clipboard.SetText(Encoding.UTF8.GetString(Convert.FromBase64String(a[0])));
                    break;
                case "clipsnap":
                    outKv["formats"] = string.Join(",", Clipboard.GetDataObject().GetFormats());
                    break;
                case "fg":
                    {
                        IntPtr h = GetForegroundWindow();
                        StringBuilder t = new StringBuilder(512); GetWindowText(h, t, 512);
                        uint pid; GetWindowThreadProcessId(h, out pid);
                        outKv["hwnd"] = h.ToInt64().ToString();
                        outKv["pid"] = pid.ToString();
                        outKv["title"] = t.ToString();
                        outKv["dpi"] = GetDpiForWindow(h).ToString();
                    }
                    break;
                case "cursor":
                    {
                        POINT p; GetCursorPos(out p);
                        outKv["x"] = p.X.ToString(); outKv["y"] = p.Y.ToString();
                        POINT q = new POINT(); q.X = p.X; q.Y = p.Y;
                        IntPtr mon = MonitorFromPoint(q, 2);
                        uint dx, dy; GetDpiForMonitor(mon, 0, out dx, out dy);
                        outKv["dpiX"] = dx.ToString(); outKv["dpiY"] = dy.ToString();
                        outKv["screenW"] = GetSystemMetrics(0).ToString(); outKv["screenH"] = GetSystemMetrics(1).ToString();
                    }
                    break;
                case "windows":
                    outKv["list"] = string.Join("\n", ListWindows());
                    break;
                case "activate":
                    {
                        IntPtr h = FindWindowByTitle(string.Join(" ", a.ToArray()));
                        outKv["found"] = (h != IntPtr.Zero).ToString();
                        if (h != IntPtr.Zero) { ForceForeground(h); }
                    }
                    break;
                case "uia-sel":
                    {
                        int x = a.Count > 0 ? int.Parse(a[0]) : -1;
                        int y = a.Count > 1 ? int.Parse(a[1]) : -1;
                        outKv["text"] = UiaSelection(x, y, log);
                    }
                    break;
                case "uia-focus":
                    {
                        AutomationElement f = AutomationElement.FocusedElement;
                        outKv["info"] = Describe(f);
                        AutomationElement p = AutomationElement.FromPoint(new System.Windows.Point(a.Count > 0 ? int.Parse(a[0]) : 0, a.Count > 1 ? int.Parse(a[1]) : 0));
                        outKv["point"] = Describe(p);
                    }
                    break;
                case "uia-tree":
                    {
                        int x = int.Parse(a[0]), y = int.Parse(a[1]);
                        int depth = a.Count > 2 ? int.Parse(a[2]) : 3;
                        AutomationElement root = AutomationElement.FromPoint(new System.Windows.Point(x, y));
                        StringBuilder sb = new StringBuilder();
                        DumpTree(root, 0, depth, sb);
                        outKv["tree"] = sb.ToString();
                    }
                    break;
                case "select-at":
                    {
                        // double-click then try UIA, then clipboard fallback
                        int x = int.Parse(a[0]), y = int.Parse(a[1]);
                        int delay = a.Count > 2 ? int.Parse(a[2]) : 150;
                        string mode = a.Count > 3 ? a[3] : "both";
                        DoubleClickAt(x, y);
                        Thread.Sleep(delay);
                        string uia = null, clip = null;
                        if (mode != "clip") uia = UiaSelection(x, y, log);
                        if (mode != "uia" && string.IsNullOrWhiteSpace(uia))
                        {
                            bool restored;
                            clip = CopyViaClipboard(log, out restored);
                            outKv["clipboardRestored"] = restored.ToString();
                        }
                        outKv["uia"] = uia;
                        outKv["clip"] = clip;
                        outKv["result"] = !string.IsNullOrWhiteSpace(uia) ? uia : clip;
                    }
                    break;
                case "clipcopy":
                    {
                        // plain ctrl+c and report new clipboard text (no restore)
                        bool restored;
                        outKv["text"] = CopyViaClipboard(log, out restored);
                    }
                    break;
                default:
                    Console.WriteLine("usage: uihelper <cmd> ... --out file.json");
                    Console.WriteLine("cmds: move click dblclick type key wait shot clipget clipset fg windows activate uia-sel uia-focus uia-tree select-at cursor");
                    break;
            }
        }
        catch (Exception ex)
        {
            outKv["error"] = ex.Message;
            log.AppendLine(ex.ToString());
        }
        WriteOut(outPath, outKv, log);
        return 0;
    }

    static void DumpTree(AutomationElement e, int depth, int maxDepth, StringBuilder sb)
    {
        if (e == null || depth > maxDepth) return;
        sb.Append(new string(' ', depth * 2)).Append(Describe(e)).AppendLine();
        AutomationElement child = TreeWalker.ControlViewWalker.GetFirstChild(e);
        while (child != null)
        {
            DumpTree(child, depth + 1, maxDepth, sb);
            child = TreeWalker.ControlViewWalker.GetNextSibling(child);
        }
    }
}
