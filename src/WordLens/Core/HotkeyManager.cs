using System;
using System.Windows.Interop;

namespace WordLens.Core
{
    /// <summary>Parses a "Ctrl+Alt+D" style string and registers it as a global hotkey.</summary>
    public sealed class HotkeyManager : IDisposable
    {
        public const int HotkeyIdLookup = 0xA11;
        public const int HotkeyIdSearch = 0xA12;
        private const int HwndMessage = -3; // HWND_MESSAGE
        private HwndSource _source;
        private uint _mods;
        private uint _vk;
        private bool _registered;
        private readonly int _id;

        public HotkeyManager(int id = HotkeyIdLookup)
        {
            _id = id;
        }

        public event EventHandler HotkeyPressed;

        public string CurrentHotkey { get; private set; } = "";
        public bool IsRegistered => _registered;

        public bool Register(string hotkey)
        {
            Unregister();
            if (string.IsNullOrWhiteSpace(hotkey)) return false;
            if (!TryParse(hotkey, out uint mods, out uint vk, out string error))
            {
                Log.Warn("Invalid hotkey '" + hotkey + "': " + error);
                return false;
            }
            try
            {
                if (_source == null)
                {
                    var p = new HwndSourceParameters("WordLensHotkeyWindow" + _id)
                    {
                        Width = 0,
                        Height = 0,
                        WindowStyle = 0,
                        ParentWindow = new IntPtr(HwndMessage)
                    };
                    _source = new HwndSource(p);
                    _source.AddHook(WndProc);
                }
                _mods = mods;
                _vk = vk;
                _registered = Native.RegisterHotKey(_source.Handle, _id, mods, vk);
                if (_registered)
                {
                    CurrentHotkey = hotkey;
                    Log.Info("Hotkey registered: " + hotkey);
                }
                else
                {
                    Log.Warn("RegisterHotKey failed for " + hotkey + ", win32 error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                }
                return _registered;
            }
            catch (Exception ex)
            {
                Log.Error("Hotkey registration failed", ex);
                return false;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == _id)
            {
                if (HotkeyPressed != null) HotkeyPressed(this, EventArgs.Empty);
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Unregister()
        {
            try
            {
                if (_registered && _source != null)
                    Native.UnregisterHotKey(_source.Handle, _id);
            }
            catch { }
            _registered = false;
        }

        public static bool TryParse(string text, out uint mods, out uint vk, out string error)
        {
            mods = 0;
            vk = 0;
            error = "";
            if (string.Equals((text ?? "").Trim(), "copilot", StringComparison.OrdinalIgnoreCase))
            {
                // The Copilot key on Windows 11 sends Win+Shift+F23.
                mods = 0x0008 | 0x0004;
                vk = 0x86;
                return true;
            }
            string[] parts = text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                error = "empty";
                return false;
            }
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                string lower = p.ToLowerInvariant();
                if (lower == "ctrl" || lower == "control") { mods |= 0x0002; continue; }
                if (lower == "alt") { mods |= 0x0001; continue; }
                if (lower == "shift") { mods |= 0x0004; continue; }
                if (lower == "win" || lower == "windows" || lower == "lwin") { mods |= 0x0008; continue; }
                if (i != parts.Length - 1) { error = "modifier must come first: " + p; return false; }
                vk = KeyToVk(lower);
                if (vk == 0) { error = "unknown key " + p; return false; }
            }
            if (vk == 0) { error = "no main key"; return false; }
            if ((mods & 0x000F) == 0) { error = "at least one modifier required"; return false; }
            return true;
        }

        private static uint KeyToVk(string key)
        {
            if (key.Length == 1)
            {
                char c = char.ToUpperInvariant(key[0]);
                if (c >= 'A' && c <= 'Z') return c;
                if (c >= '0' && c <= '9') return c;
            }
            switch (key)
            {
                case "f1": return 0x70;
                case "f2": return 0x71;
                case "f3": return 0x72;
                case "f4": return 0x73;
                case "f5": return 0x74;
                case "f6": return 0x75;
                case "f7": return 0x76;
                case "f8": return 0x77;
                case "f9": return 0x78;
                case "f10": return 0x79;
                case "f11": return 0x7A;
                case "f12": return 0x7B;
                case "f13": return 0x7C;
                case "f14": return 0x7D;
                case "f15": return 0x7E;
                case "f16": return 0x7F;
                case "f17": return 0x80;
                case "f18": return 0x81;
                case "f19": return 0x82;
                case "f20": return 0x83;
                case "f21": return 0x84;
                case "f22": return 0x85;
                case "f23": return 0x86;
                case "f24": return 0x87;
                case "space": return 0x20;
                case "tab": return 0x09;
                case "enter": return 0x0D;
                case "backspace": return 0x08;
                case "delete": return 0x2E;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pageup": return 0x21;
                case "pagedown": return 0x22;
                case "up": return 0x26;
                case "down": return 0x28;
                case "left": return 0x25;
                case "right": return 0x27;
                case "-": return 0xBD;
                case "=": return 0xBB;
                case ",": return 0xBC;
                case ".": return 0xBE;
                case "/": return 0xBF;
                case ";": return 0xBA;
                case "'": return 0xDE;
                case "[": return 0xDB;
                case "]": return 0xDD;
                case "\\": return 0xDC;
                case "`": return 0xC0;
            }
            return 0;
        }

        public void Dispose()
        {
            Unregister();
            try
            {
                if (_source != null)
                {
                    _source.RemoveHook(WndProc);
                    _source.Dispose();
                    _source = null;
                }
            }
            catch { }
        }
    }
}
