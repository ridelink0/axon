// Computer Use host - persistent Windows UI Automation driver.
//
// Speaks line-delimited JSON on stdin/stdout. One host per Computer Use session, so
// AutomationElement references stay live between calls and a tree lookup costs
// milliseconds instead of re-walking from the desktop root every time.
//
// This is compiled locally at install time by the csc.exe that ships with
// every Windows .NET Framework install. It is shipped as source, not as a
// binary, so what runs on your machine is exactly what you can read here.
//
// Language level is C# 5 - that is what the in-box compiler supports. No string
// interpolation, no null-conditional operators, no expression-bodied members.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

namespace Axon
{
    internal static class Native
    {
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("shcore.dll")] internal static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] internal static extern bool BlockInput(bool block);

        // Exclusive mode: hold off the user's physical mouse and keyboard for the
        // length of a single action, so their hand cannot land in the middle of
        // one. BlockInput stops physical input only - synthetic input still goes
        // through, which is what makes this usable at all.
        //
        // Four independent ways out, because a tool that can lock you out of your
        // own machine must never be able to do so permanently:
        //   1. Esc. The keyboard hook lives in this same process, and the blocking
        //      thread still receives input, so Esc reaches us and releases.
        //   2. A watchdog releases unconditionally after ReleaseAfterMs.
        //   3. Scope is one action - milliseconds - never a whole run.
        //   4. Windows releases it by itself the moment this process exits, and
        //      Ctrl+Alt+Del can never be blocked by anything.
        const int ReleaseAfterMs = 4000;
        static readonly object _blockLock = new object();
        static bool _blocked;
        static DateTime _blockedAt;
        static System.Threading.Thread _watchdog;

        internal static bool BeginExclusive()
        {
            lock (_blockLock)
            {
                if (_blocked) return true;
                if (!BlockInput(true)) return false;
                _blocked = true;
                _blockedAt = DateTime.UtcNow;
                StartWatchdog();
                return true;
            }
        }

        internal static void EndExclusive()
        {
            lock (_blockLock)
            {
                if (!_blocked) return;
                BlockInput(false);
                _blocked = false;
            }
        }

        internal static bool InputBlocked { get { lock (_blockLock) { return _blocked; } } }

        static void StartWatchdog()
        {
            if (_watchdog != null) return;
            _watchdog = new System.Threading.Thread(delegate()
            {
                // Nothing about a stuck action may leave the machine unusable.
                while (true)
                {
                    System.Threading.Thread.Sleep(200);
                    lock (_blockLock)
                    {
                        if (_blocked && (DateTime.UtcNow - _blockedAt).TotalMilliseconds > ReleaseAfterMs)
                        {
                            BlockInput(false);
                            _blocked = false;
                        }
                    }
                }
            });
            _watchdog.IsBackground = true;
            _watchdog.Name = "computer-use-input-watchdog";
            _watchdog.Start();
        }

        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }

        // Which top-level window actually owns a screen point.
        internal static IntPtr RootWindowAt(int x, int y)
        {
            POINT p = new POINT();
            p.X = x; p.Y = y;
            IntPtr h = WindowFromPoint(p);
            if (h == IntPtr.Zero) return IntPtr.Zero;
            IntPtr root = GetAncestor(h, 2 /* GA_ROOT */);
            return root == IntPtr.Zero ? h : root;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
        [StructLayout(LayoutKind.Explicit)]
        internal struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT { public uint type; public INPUTUNION u; }

        const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
        const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
        const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
        const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_EXTENDEDKEY = 0x0001;

        static int InputSize { get { return Marshal.SizeOf(typeof(INPUT)); } }

        static INPUT MouseInput(uint flags, uint data)
        {
            INPUT i = new INPUT();
            i.type = INPUT_MOUSE;
            i.u.mi.dwFlags = flags;
            i.u.mi.mouseData = data;
            return i;
        }

        static INPUT KeyInput(ushort vk, ushort scan, uint flags)
        {
            INPUT i = new INPUT();
            i.type = INPUT_KEYBOARD;
            i.u.ki.wVk = vk;
            i.u.ki.wScan = scan;
            i.u.ki.dwFlags = flags;
            return i;
        }

        internal static void MouseButton(string button, bool down)
        {
            uint f;
            if (button == "right") f = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
            else if (button == "middle") f = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
            else f = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
            Presence.NoteSelfInput();
            SendInput(1, new INPUT[] { MouseInput(f, 0) }, InputSize);
        }

        internal static void MouseWheel(int delta, bool horizontal)
        {
            Presence.NoteSelfInput();
            SendInput(1, new INPUT[] { MouseInput(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, unchecked((uint)delta)) }, InputSize);
        }

        // Unicode injection is keyboard-layout independent, so text types the
        // same on a Dvorak or AZERTY machine as on QWERTY.
        internal static void TypeUnicode(string text)
        {
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c == '\n') { KeyTap(0x0D); continue; }
                if (c == '\t') { KeyTap(0x09); continue; }
                INPUT[] pair = new INPUT[]
                {
                    KeyInput(0, (ushort)c, KEYEVENTF_UNICODE),
                    KeyInput(0, (ushort)c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP)
                };
                Presence.NoteSelfInput();
                SendInput(2, pair, InputSize);
            }
        }

        static bool IsExtended(ushort vk)
        {
            switch (vk)
            {
                case 0x21: case 0x22: case 0x23: case 0x24:
                case 0x25: case 0x26: case 0x27: case 0x28:
                case 0x2C: case 0x2D: case 0x2E:
                case 0x5B: case 0x5C: case 0x5D:
                case 0xA3: case 0xA5:
                    return true;
            }
            return false;
        }

        internal static void KeyDown(ushort vk)
        {
            Presence.NoteSelfInput();
            SendInput(1, new INPUT[] { KeyInput(vk, 0, IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0) }, InputSize);
        }

        internal static void KeyUp(ushort vk)
        {
            uint f = KEYEVENTF_KEYUP | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
            Presence.NoteSelfInput();
            SendInput(1, new INPUT[] { KeyInput(vk, 0, f) }, InputSize);
        }

        internal static void KeyTap(ushort vk) { KeyDown(vk); KeyUp(vk); }

        // Windows refuses SetForegroundWindow from a thread that does not own
        // the current input queue, so borrow the target's queue for the call.
        internal static bool ForceForeground(IntPtr hwnd)
        {
            if (!IsWindow(hwnd)) return false;
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE
            IntPtr fg = GetForegroundWindow();
            if (fg == hwnd) return true;

            uint pid;
            uint targetThread = GetWindowThreadProcessId(hwnd, out pid);
            uint self = GetCurrentThreadId();
            uint fgThread = 0;
            if (fg != IntPtr.Zero) fgThread = GetWindowThreadProcessId(fg, out pid);

            bool a1 = false, a2 = false;
            try
            {
                if (targetThread != 0 && targetThread != self) a1 = AttachThreadInput(self, targetThread, true);
                if (fgThread != 0 && fgThread != self) a2 = AttachThreadInput(self, fgThread, true);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (a1) AttachThreadInput(self, targetThread, false);
                if (a2) AttachThreadInput(self, fgThread, false);
            }
            return GetForegroundWindow() == hwnd;
        }

        internal static bool IsCloaked(IntPtr hwnd)
        {
            int cloaked = 0;
            int hr = DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out cloaked, sizeof(int));
            return hr == 0 && cloaked != 0;
        }
    }

    // Typed failure. Every error the agent can hit carries a stable code so it
    // can branch on the cause instead of pattern-matching English.
    internal class AxonError : Exception
    {
        public string Code;
        public string Hint;
        public AxonError(string code, string message, string hint) : base(message)
        {
            Code = code;
            Hint = hint;
        }
        public AxonError(string code, string message) : this(code, message, null) { }
    }

    internal class Snapshot
    {
        public List<AutomationElement> Elements = new List<AutomationElement>();
        public IntPtr Hwnd;
        public string Title;
        public DateTime Taken;
    }

    internal static class Program
    {
        static Dictionary<string, Snapshot> _snapshots = new Dictionary<string, Snapshot>();
        static int _snapSeq = 0;
        const int MaxSnapshots = 8;
        static string _dpiMode = "none";
        static readonly int _selfPid = System.Diagnostics.Process.GetCurrentProcess().Id;
        static JavaScriptSerializer _js;

        static readonly string[] ShellClasses = new string[]
        {
            "Shell_TrayWnd", "Progman", "WorkerW", "Windows.UI.Core.CoreWindow",
            "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
            "Windows.Internal.Shell.TabProxyWindow", "ForegroundStaging",
            "MultitaskingViewFrame", "XamlExplorerHostIslandWindow"
        };

        static int Main(string[] argv)
        {
            // Build-time warm-up: touch the expensive machinery once so the
            // antivirus scan and the JIT are paid for before Claude ever waits
            // on a call, then leave.
            bool warmup = false;
            foreach (string s in argv) if (s == "--warmup") warmup = true;
            if (warmup)
            {
                try
                {
                    SetDpiAwareness();
                    AutomationElement root = AutomationElement.RootElement;
                    root.FindAll(TreeScope.Children, Condition.TrueCondition);
                    new JavaScriptSerializer().Serialize(new Dictionary<string, object>());
                }
                catch { }
                Console.Out.WriteLine("{\"event\":\"warm\"}");
                return 0;
            }

            SetDpiAwareness();
            if (Environment.GetEnvironmentVariable("CU_PRESENCE") != "off") Presence.Start();
            Presence.OnPanic = delegate { Native.EndExclusive(); Overlay.RequestStop(); };
            Overlay.Start(Environment.GetEnvironmentVariable("CU_OVERLAY") != "off");
            Configure(
                EnvInt("CU_IDLE_MS", 1200),
                EnvInt("CU_WAIT_MS", 6000),
                Environment.GetEnvironmentVariable("CU_MODE"));

            _js = new JavaScriptSerializer();
            _js.MaxJsonLength = 64 * 1024 * 1024;

            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                Console.InputEncoding = new UTF8Encoding(false);
            }
            catch { /* redirected pipes can refuse an encoding change; harmless */ }

            Dictionary<string, object> ready = new Dictionary<string, object>();
            ready["event"] = "ready";
            ready["dpi_mode"] = _dpiMode;
            ready["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            ready["clr"] = Environment.Version.ToString();
            ready["presence"] = Presence.HooksOk;
            ready["overlay"] = Overlay.Enabled;
            WriteLine(ready);

            while (true)
            {
                string line;
                try { line = Console.In.ReadLine(); }
                catch { break; }
                if (line == null) break;
                line = line.Trim();
                if (line.Length == 0) continue;

                object reqId = null;
                try
                {
                    Dictionary<string, object> req = (Dictionary<string, object>)_js.DeserializeObject(line);
                    reqId = Get(req, "id");
                    string op = Str(Get(req, "op"));
                    Dictionary<string, object> a = Get(req, "args") as Dictionary<string, object>;
                    if (a == null) a = new Dictionary<string, object>();

                    if (op == "shutdown")
                    {
                        Dictionary<string, object> bye = new Dictionary<string, object>();
                        bye["bye"] = true;
                        Native.EndExclusive();
                        Respond(reqId, bye, 0);
                        return 0;
                    }

                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    object result = Dispatch(op, a);
                    sw.Stop();
                    Respond(reqId, result, sw.ElapsedMilliseconds);
                }
                catch (AxonError ax)
                {
                    RespondError(reqId, ax.Code, ax.Message, ax.Hint);
                }
                catch (Exception ex)
                {
                    RespondError(reqId, "host_error", ex.GetType().Name + ": " + ex.Message, null);
                }
            }
            return 0;
        }

        static void SetDpiAwareness()
        {
            // Per-monitor v2 first. Without real DPI awareness the host sees a
            // virtualised desktop and every coordinate lands offset on any
            // display scaled above 100%.
            try { if (Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) { _dpiMode = "per-monitor-v2"; return; } }
            catch { }
            try { if (Native.SetProcessDpiAwareness(2) == 0) { _dpiMode = "per-monitor"; return; } }
            catch { }
            try { if (Native.SetProcessDPIAware()) { _dpiMode = "system"; return; } }
            catch { }
        }

        static object Dispatch(string op, Dictionary<string, object> a)
        {
            switch (op)
            {
                case "ping": return OpPing();
                case "presence": return OpPresence(a);
                case "list_apps": return OpListApps(a);
                case "snapshot": return OpSnapshot(a);
                case "click": return OpClick(a);
                case "type": return OpType(a);
                case "set_value": return OpSetValue(a);
                case "key": return OpKey(a);
                case "scroll": return OpScroll(a);
                case "focus": return OpFocus(a);
                case "close_window": return OpCloseWindow(a);
                case "wait_for": return OpWaitFor(a);
                case "screenshot": return OpScreenshot(a);
                default: throw new AxonError("unknown_op", "Unknown operation '" + op + "'.");
            }
        }

        // ---- protocol plumbing ------------------------------------------------

        static void WriteLine(object obj)
        {
            string json = _js.Serialize(obj);
            Console.Out.WriteLine(json);
            Console.Out.Flush();
        }

        static void Respond(object id, object result, long ms)
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["id"] = id;
            m["ok"] = true;
            m["ms"] = ms;
            m["result"] = result;
            WriteLine(m);
        }

        static void RespondError(object id, string code, string message, string hint)
        {
            try
            {
                Dictionary<string, object> err = new Dictionary<string, object>();
                err["code"] = code;
                err["message"] = message;
                if (hint != null) err["hint"] = hint;
                Dictionary<string, object> m = new Dictionary<string, object>();
                m["id"] = id;
                m["ok"] = false;
                m["error"] = err;
                WriteLine(m);
            }
            catch
            {
                // Reporting the failure must never become the failure. If even
                // this cannot be serialised, emit a minimal hand-built reply so
                // the caller gets an answer instead of hanging until timeout.
                try
                {
                    string sid = id == null ? "null" : _js.Serialize(id);
                    Console.Out.WriteLine("{\"id\":" + sid + ",\"ok\":false,\"error\":{\"code\":\"host_error\",\"message\":\"unserialisable error\"}}");
                    Console.Out.Flush();
                }
                catch { }
            }
        }

        static object Get(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v)) return v;
            return null;
        }

        static string Str(object o) { return o == null ? null : Convert.ToString(o, CultureInfo.InvariantCulture); }

        static int Int(object o, int fallback)
        {
            if (o == null) return fallback;
            try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        static long Long(object o, long fallback)
        {
            if (o == null) return fallback;
            try { return Convert.ToInt64(o, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        static bool Bool(object o, bool fallback)
        {
            if (o == null) return fallback;
            try { return Convert.ToBoolean(o); }
            catch { return fallback; }
        }

        // ---- element helpers --------------------------------------------------

        static int[] RectOf(AutomationElement el)
        {
            try
            {
                System.Windows.Rect r = el.Current.BoundingRectangle;
                if (r.IsEmpty || double.IsInfinity(r.X) || double.IsNaN(r.X)) return null;
                if (r.Width <= 0 || r.Height <= 0) return null;
                return new int[] { (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height };
            }
            catch { return null; }
        }

        static List<string> PatternsOf(AutomationElement el)
        {
            List<string> names = new List<string>();
            try
            {
                AutomationPattern[] ps = el.GetSupportedPatterns();
                foreach (AutomationPattern p in ps)
                {
                    // ProgrammaticName looks like "InvokePatternIdentifiers.Pattern".
                    // The useful part is the prefix before the dot, minus its
                    // "PatternIdentifiers" suffix, which yields "Invoke".
                    string n = p.ProgrammaticName;
                    if (string.IsNullOrEmpty(n)) continue;
                    int dot = n.IndexOf('.');
                    if (dot >= 0) n = n.Substring(0, dot);
                    if (n.EndsWith("PatternIdentifiers"))
                        n = n.Substring(0, n.Length - "PatternIdentifiers".Length);
                    else if (n.EndsWith("Pattern"))
                        n = n.Substring(0, n.Length - "Pattern".Length);
                    if (n.Length > 0) names.Add(n);
                }
            }
            catch { }
            return names;
        }

        static bool Has(List<string> patterns, string name) { return patterns.Contains(name); }

        // The appshot idea: pull whatever text the control exposes, including
        // content scrolled outside the viewport, which pixels can never show.
        static string TextOf(AutomationElement el, List<string> patterns)
        {
            string outText = null;
            if (Has(patterns, "Value"))
            {
                try
                {
                    ValuePattern vp = el.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
                    if (vp != null) outText = vp.Current.Value;
                }
                catch { }
            }
            if (string.IsNullOrEmpty(outText) && Has(patterns, "Text"))
            {
                try
                {
                    TextPattern tp = el.GetCurrentPattern(TextPattern.Pattern) as TextPattern;
                    if (tp != null) outText = tp.DocumentRange.GetText(4000);
                }
                catch { }
            }
            if (string.IsNullOrEmpty(outText)) return null;
            outText = outText.Replace("\r\n", "\n");
            if (outText.Length > 4000) outText = outText.Substring(0, 4000) + "...[truncated]";
            return outText;
        }

        static Dictionary<string, object> StateOf(AutomationElement el, List<string> patterns)
        {
            Dictionary<string, object> st = new Dictionary<string, object>();
            try
            {
                AutomationElement.AutomationElementInformation c = el.Current;
                if (!c.IsEnabled) st["disabled"] = true;
                if (c.IsOffscreen) st["offscreen"] = true;
                if (c.IsKeyboardFocusable) st["focusable"] = true;
                if (c.HasKeyboardFocus) st["focused"] = true;
            }
            catch { }
            if (Has(patterns, "Toggle"))
            {
                try
                {
                    TogglePattern tp = el.GetCurrentPattern(TogglePattern.Pattern) as TogglePattern;
                    if (tp != null) st["toggle"] = tp.Current.ToggleState.ToString();
                }
                catch { }
            }
            if (Has(patterns, "SelectionItem"))
            {
                try
                {
                    SelectionItemPattern sp = el.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
                    if (sp != null) st["selected"] = sp.Current.IsSelected;
                }
                catch { }
            }
            if (Has(patterns, "ExpandCollapse"))
            {
                try
                {
                    ExpandCollapsePattern ep = el.GetCurrentPattern(ExpandCollapsePattern.Pattern) as ExpandCollapsePattern;
                    if (ep != null) st["expand"] = ep.Current.ExpandCollapseState.ToString();
                }
                catch { }
            }
            if (Has(patterns, "RangeValue"))
            {
                try
                {
                    RangeValuePattern rp = el.GetCurrentPattern(RangeValuePattern.Pattern) as RangeValuePattern;
                    if (rp != null)
                    {
                        st["value"] = rp.Current.Value;
                        st["min"] = rp.Current.Minimum;
                        st["max"] = rp.Current.Maximum;
                    }
                }
                catch { }
            }
            if (st.Count == 0) return null;
            return st;
        }

        static string RoleOf(AutomationElement el)
        {
            try
            {
                string n = el.Current.ControlType.ProgrammaticName;
                if (n != null && n.StartsWith("ControlType.")) n = n.Substring("ControlType.".Length);
                return n;
            }
            catch { return "Unknown"; }
        }

        static string NameOf(AutomationElement el)
        {
            try { return el.Current.Name; } catch { return null; }
        }

        static string AidOf(AutomationElement el)
        {
            try { return el.Current.AutomationId; } catch { return null; }
        }

        // ---- window discovery -------------------------------------------------

        static bool IsSelfWindow(AutomationElement el)
        {
            try
            {
                if (el.Current.ProcessId == _selfPid) return true;
                IntPtr h = new IntPtr(el.Current.NativeWindowHandle);
                return Overlay.IsOwnWindow(h);
            }
            catch { return false; }
        }

        static bool IsRealWindow(AutomationElement el)
        {
            try
            {
                AutomationElement.AutomationElementInformation c = el.Current;
                IntPtr h = new IntPtr(c.NativeWindowHandle);
                if (h == IntPtr.Zero) return false;
                if (!Native.IsWindow(h)) return false;
                if (!Native.IsWindowVisible(h)) return false;
                if (Native.IsCloaked(h)) return false;
                if (Overlay.IsOwnWindow(h)) return false;
                foreach (string s in ShellClasses) if (s == c.ClassName) return false;
                int[] r = RectOf(el);
                if (r == null) return false;
                // Hidden helper windows park themselves far off the virtual screen.
                if (r[0] < -30000 || r[1] < -30000) return false;
                if (r[2] < 40 || r[3] < 40) return false;
                return true;
            }
            catch { return false; }
        }

        static object OpListApps(Dictionary<string, object> a)
        {
            bool includeHidden = Bool(Get(a, "include_hidden"), false);
            AutomationElement root = AutomationElement.RootElement;
            AutomationElementCollection wins = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            List<object> list = new List<object>();
            IntPtr fg = Native.GetForegroundWindow();

            foreach (AutomationElement w in wins)
            {
                // Computer Use's own windows are never user windows, so they are excluded
                // unconditionally - include_hidden reveals the user's minimized
                // windows, not Computer Use's marker.
                if (IsSelfWindow(w)) continue;
                if (!includeHidden && !IsRealWindow(w)) continue;
                try
                {
                    AutomationElement.AutomationElementInformation c = w.Current;
                    IntPtr h = new IntPtr(c.NativeWindowHandle);
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["hwnd"] = (long)c.NativeWindowHandle;
                    e["title"] = c.Name;
                    e["class"] = c.ClassName;
                    e["pid"] = c.ProcessId;
                    string pname, ppath;
                    ProcessInfo(c.ProcessId, out pname, out ppath);
                    e["process"] = pname;
                    e["path"] = ppath;
                    e["rect"] = RectOf(w);
                    e["minimized"] = Native.IsIconic(h);
                    e["foreground"] = (h == fg);
                    list.Add(e);
                }
                catch { }
            }

            Dictionary<string, object> res = new Dictionary<string, object>();
            res["windows"] = list;
            res["dpi_mode"] = _dpiMode;
            return res;
        }

        // Process.MainModule is a slow call - tens of milliseconds each - and
        // list_apps used to make one per window every time it ran. Names and
        // paths do not change for the life of a pid, so they are looked up once.
        static Dictionary<int, string[]> _procCache = new Dictionary<int, string[]>();

        static void ProcessInfo(int pid, out string name, out string path)
        {
            string[] hit;
            if (_procCache.TryGetValue(pid, out hit)) { name = hit[0]; path = hit[1]; return; }
            name = null; path = null;
            try
            {
                System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById(pid);
                name = p.ProcessName;
                try { path = p.MainModule.FileName; } catch { }
            }
            catch { }
            // Bounded, because pids are recycled and a long session sees many.
            if (_procCache.Count > 256) _procCache.Clear();
            _procCache[pid] = new string[] { name, path };
        }

        static AutomationElement ResolveWindow(Dictionary<string, object> a)
        {
            AutomationElement root = AutomationElement.RootElement;
            object hwndArg = Get(a, "hwnd");
            if (hwndArg != null)
            {
                long want = Long(hwndArg, 0);
                AutomationElementCollection wins = root.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement w in wins)
                {
                    try { if ((long)w.Current.NativeWindowHandle == want) return w; }
                    catch { }
                }
                return null;
            }
            string title = Str(Get(a, "title"));
            if (!string.IsNullOrEmpty(title))
            {
                AutomationElementCollection wins = root.FindAll(TreeScope.Children, Condition.TrueCondition);
                AutomationElement partial = null;
                foreach (AutomationElement w in wins)
                {
                    if (!IsRealWindow(w)) continue;
                    try
                    {
                        string n = w.Current.Name;
                        if (n == null) continue;
                        if (n == title) return w;
                        if (partial == null && n.ToLowerInvariant().Contains(title.ToLowerInvariant())) partial = w;
                    }
                    catch { }
                }
                return partial;
            }
            return null;
        }

        static AutomationElement RequireWindow(Dictionary<string, object> a)
        {
            AutomationElement w = ResolveWindow(a);
            if (w == null)
                throw new AxonError("window_not_found", "No window matched hwnd/title.",
                    "Call list_apps and pass an hwnd from its result.");
            return w;
        }

        // ---- snapshot ---------------------------------------------------------

        class Frame
        {
            public AutomationElement El;
            public int Depth;
            public Frame(AutomationElement el, int depth) { El = el; Depth = depth; }
        }

        static readonly string[] InteractivePatterns = new string[]
        { "Invoke", "Toggle", "Value", "SelectionItem", "ExpandCollapse", "Scroll", "RangeValue" };

        static object OpSnapshot(Dictionary<string, object> a)
        {
            AutomationElement win = RequireWindow(a);
            int maxNodes = Int(Get(a, "max_nodes"), 400);
            int maxDepth = Int(Get(a, "max_depth"), 14);
            bool interactiveOnly = Bool(Get(a, "interactive_only"), false);

            TreeWalker walker = TreeWalker.ControlViewWalker;
            List<AutomationElement> elements = new List<AutomationElement>();
            List<object> nodes = new List<object>();
            bool truncated = false;

            // Iterative walk keeps ordering stable and cannot blow the stack on
            // a deep tree such as browser content.
            Stack<Frame> stack = new Stack<Frame>();
            stack.Push(new Frame(win, 0));

            while (stack.Count > 0)
            {
                if (nodes.Count >= maxNodes) { truncated = true; break; }
                Frame f = stack.Pop();
                AutomationElement el = f.El;

                int[] rect = RectOf(el);
                List<string> patterns = PatternsOf(el);
                bool interactive = false;
                foreach (string ip in InteractivePatterns) { if (patterns.Contains(ip)) { interactive = true; break; } }

                bool include = true;
                if (interactiveOnly && f.Depth > 0 && !interactive) include = false;
                if (rect == null && f.Depth > 0) include = false;

                if (include)
                {
                    Dictionary<string, object> node = new Dictionary<string, object>();
                    node["i"] = nodes.Count;
                    node["role"] = RoleOf(el);
                    node["depth"] = f.Depth;
                    string nm = NameOf(el);
                    if (!string.IsNullOrEmpty(nm)) node["name"] = nm;
                    string aid = AidOf(el);
                    if (!string.IsNullOrEmpty(aid)) node["aid"] = aid;
                    if (rect != null) node["rect"] = rect;
                    if (patterns.Count > 0) node["patterns"] = patterns;
                    string txt = TextOf(el, patterns);
                    if (txt != null) node["text"] = txt;
                    Dictionary<string, object> st = StateOf(el, patterns);
                    if (st != null) node["state"] = st;
                    nodes.Add(node);
                    elements.Add(el);
                }

                if (f.Depth < maxDepth)
                {
                    List<AutomationElement> kids = new List<AutomationElement>();
                    try
                    {
                        AutomationElement k = walker.GetFirstChild(el);
                        int guard = 0;
                        while (k != null && guard < 500)
                        {
                            kids.Add(k);
                            k = walker.GetNextSibling(k);
                            guard++;
                        }
                    }
                    catch { }
                    for (int j = kids.Count - 1; j >= 0; j--) stack.Push(new Frame(kids[j], f.Depth + 1));
                }
            }

            _snapSeq++;
            string sid = "s" + _snapSeq.ToString(CultureInfo.InvariantCulture);
            Snapshot snap = new Snapshot();
            snap.Elements = elements;
            snap.Taken = DateTime.UtcNow;
            try
            {
                snap.Hwnd = new IntPtr(win.Current.NativeWindowHandle);
                snap.Title = win.Current.Name;
            }
            catch { }
            _snapshots[sid] = snap;

            // Bound memory: drop oldest once past the cap.
            while (_snapshots.Count > MaxSnapshots)
            {
                string oldestKey = null;
                DateTime oldest = DateTime.MaxValue;
                foreach (KeyValuePair<string, Snapshot> kv in _snapshots)
                {
                    if (kv.Value.Taken < oldest) { oldest = kv.Value.Taken; oldestKey = kv.Key; }
                }
                if (oldestKey == null) break;
                _snapshots.Remove(oldestKey);
            }

            Dictionary<string, object> res = new Dictionary<string, object>();
            res["snapshot_id"] = sid;
            res["hwnd"] = (long)snap.Hwnd;
            res["title"] = snap.Title;
            res["rect"] = RectOf(win);
            res["node_count"] = nodes.Count;
            res["truncated"] = truncated;
            res["nodes"] = nodes;
            return res;
        }

        // ---- target resolution -------------------------------------------------

        static AutomationElement SnapshotElement(Dictionary<string, object> a)
        {
            string sid = Str(Get(a, "snapshot_id"));
            if (string.IsNullOrEmpty(sid))
            {
                if (_snapSeq == 0)
                    throw new AxonError("no_snapshot", "No snapshot has been taken yet.",
                        "Call snapshot on the target window, then act on an index from it.");
                sid = "s" + _snapSeq.ToString(CultureInfo.InvariantCulture);
            }
            Snapshot snap;
            if (!_snapshots.TryGetValue(sid, out snap))
                throw new AxonError("snapshot_expired", "Snapshot " + sid + " is no longer held.",
                    "Take a fresh snapshot and use its indices.");

            int idx = Int(Get(a, "index"), -1);
            if (idx < 0 || idx >= snap.Elements.Count)
                throw new AxonError("index_out_of_range",
                    "Index " + idx + " is outside snapshot " + sid + " (0.." + (snap.Elements.Count - 1) + ").",
                    "Re-read the snapshot listing.");

            AutomationElement el = snap.Elements[idx];
            // Liveness probe, so a destroyed control fails loudly here instead of
            // silently acting on whatever now occupies its coordinates.
            try { ControlType ignored = el.Current.ControlType; }
            catch
            {
                throw new AxonError("element_stale", "Element " + idx + " from " + sid + " no longer exists.",
                    "The UI changed. Take a fresh snapshot.");
            }
            return el;
        }

        static Dictionary<string, ControlType> _controlTypes;

        static ControlType LookupControlType(string role)
        {
            if (_controlTypes == null)
            {
                _controlTypes = new Dictionary<string, ControlType>(StringComparer.OrdinalIgnoreCase);
                ControlType[] all = new ControlType[]
                {
                    ControlType.Button, ControlType.Calendar, ControlType.CheckBox, ControlType.ComboBox,
                    ControlType.Edit, ControlType.Hyperlink, ControlType.Image, ControlType.ListItem,
                    ControlType.List, ControlType.Menu, ControlType.MenuBar, ControlType.MenuItem,
                    ControlType.ProgressBar, ControlType.RadioButton, ControlType.ScrollBar, ControlType.Slider,
                    ControlType.Spinner, ControlType.StatusBar, ControlType.Tab, ControlType.TabItem,
                    ControlType.Text, ControlType.ToolBar, ControlType.ToolTip, ControlType.Tree,
                    ControlType.TreeItem, ControlType.Custom, ControlType.Group, ControlType.Thumb,
                    ControlType.DataGrid, ControlType.DataItem, ControlType.Document, ControlType.SplitButton,
                    ControlType.Window, ControlType.Pane, ControlType.Header, ControlType.HeaderItem,
                    ControlType.Table, ControlType.TitleBar, ControlType.Separator
                };
                foreach (ControlType ct in all)
                {
                    string n = ct.ProgrammaticName;
                    if (n != null && n.StartsWith("ControlType.")) n = n.Substring("ControlType.".Length);
                    _controlTypes[n] = ct;
                }
            }
            ControlType found;
            if (_controlTypes.TryGetValue(role, out found)) return found;
            return null;
        }

        static AutomationElement FindBySelector(Dictionary<string, object> a)
        {
            // Validate the selector before resolving a window. A malformed
            // selector is the caller's mistake either way, and reporting it as
            // window_not_found because the window happened to close as well
            // sends them looking in entirely the wrong place.
            Dictionary<string, object> sel = Get(a, "selector") as Dictionary<string, object>;
            if (sel == null)
                throw new AxonError("bad_selector", "Selector must be an object.", null);
            if (string.IsNullOrEmpty(Str(Get(sel, "automation_id")))
                && string.IsNullOrEmpty(Str(Get(sel, "name")))
                && string.IsNullOrEmpty(Str(Get(sel, "role"))))
                throw new AxonError("bad_selector",
                    "Selector needs at least one of automation_id, name, or role.", null);

            AutomationElement win = RequireWindow(a);

            List<Condition> conds = new List<Condition>();
            string aid = Str(Get(sel, "automation_id"));
            if (!string.IsNullOrEmpty(aid))
                conds.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, aid));
            string name = Str(Get(sel, "name"));
            if (!string.IsNullOrEmpty(name))
                conds.Add(new PropertyCondition(AutomationElement.NameProperty, name));
            string role = Str(Get(sel, "role"));
            if (!string.IsNullOrEmpty(role))
            {
                ControlType ct = LookupControlType(role);
                if (ct == null)
                    throw new AxonError("bad_selector", "Unknown role '" + role + "'.",
                        "Use a role string exactly as it appears in a snapshot, such as Button or Edit.");
                conds.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ct));
            }
            if (conds.Count == 0)
                throw new AxonError("bad_selector", "Selector needs at least one of automation_id, name, or role.", null);

            Condition cond = conds.Count == 1 ? conds[0] : new AndCondition(conds.ToArray());
            AutomationElement found = win.FindFirst(TreeScope.Descendants, cond);
            if (found == null)
                throw new AxonError("element_not_found", "No element matched the selector.",
                    "Take a snapshot to see what the window actually exposes.");
            return found;
        }

        class Target
        {
            public AutomationElement El;
            public string Mode;
            public int[] Point;
        }

        static Target ResolveTarget(Dictionary<string, object> a)
        {
            Target t = new Target();
            if (Get(a, "index") != null)
            {
                t.El = SnapshotElement(a);
                t.Mode = "index";
                return t;
            }
            if (Get(a, "selector") != null)
            {
                t.El = FindBySelector(a);
                t.Mode = "selector";
                return t;
            }
            object pt = Get(a, "point");
            if (pt != null)
            {
                object[] arr = pt as object[];
                if (arr == null || arr.Length < 2)
                    throw new AxonError("bad_target", "point must be [x, y].", null);
                t.Mode = "point";
                t.Point = new int[] { Int(arr[0], 0), Int(arr[1], 0) };
                return t;
            }
            throw new AxonError("no_target", "Provide index, selector, or point.",
                "Prefer index from a snapshot. point is a last resort for canvas-drawn UI.");
        }

        static int[] ClickPointOf(AutomationElement el)
        {
            // The provider nominates its own clickable point, which beats a
            // centre-of-rect guess for irregular or partly covered controls.
            try
            {
                System.Windows.Point p = el.GetClickablePoint();
                return new int[] { (int)p.X, (int)p.Y };
            }
            catch { }
            int[] r = RectOf(el);
            if (r == null)
                throw new AxonError("no_click_point", "Element has no clickable point and no on-screen rectangle.",
                    "It may be scrolled out of view. Scroll it into view first.");
            return new int[] { r[0] + r[2] / 2, r[1] + r[3] / 2 };
        }

        // ---- coexistence -------------------------------------------------------
        //
        // Computer Use shares one desktop with a human. Reading a window never touches
        // input, and neither does invoking an accessibility pattern, so most of
        // what Computer Use does is genuinely invisible. Only two things intrude: moving
        // the cursor and taking the foreground. Those are what this gates.

        static int _idleMs = 1200;      // quiet for this long counts as "not using it"
        static int _waitMs = 6000;      // how long to wait for a gap before giving up
        static string _mode = "share";  // share | yield | take

        internal static void Configure(int idleMs, int waitMs, string mode)
        {
            if (idleMs > 0) _idleMs = idleMs;
            if (waitMs >= 0) _waitMs = waitMs;
            if (!string.IsNullOrEmpty(mode)) _mode = mode;
        }

        static string ModeOf(Dictionary<string, object> a)
        {
            string m = Str(Get(a, "mode"));
            if (string.IsNullOrEmpty(m)) return _mode;
            return m;
        }

        // Cheap gate on every action: never contend for the window the human is
        // actively typing or clicking in. Cursor movement over a window is not
        // enough to claim it - only deliberate input is.
        // A press of Stop on the banner halts the run. It is consumed here so
        // one press stops one run, and the caller is told plainly.
        static void GuardStopped()
        {
            if (Overlay.ConsumeStop())
                throw new AxonError("stopped_by_user",
                    "The user pressed Stop on the on-screen banner.",
                    "They have taken the machine back. Do not retry: say what you had done so far and ask before continuing.");
        }

        static void GuardSameWindow(Dictionary<string, object> a, IntPtr target)
        {
            GuardStopped();
            Overlay.MarkActive();
            // take and exclusive both mean "go now". exclusive additionally
            // holds the user off, so waiting for a gap would defeat its purpose.
            string m0 = ModeOf(a);
            if (m0 == "take" || m0 == "exclusive") return;
            if (!Presence.HooksOk || target == IntPtr.Zero) return;
            if (!Presence.Busy(_idleMs)) return;
            // Only their own window counts. Being busy elsewhere is exactly the
            // case Computer Use is built for, and a window Computer Use itself just raised is
            // not one the user was working in.
            if (Hwnd(Presence.CommitWindow) != Hwnd(target)) return;
            if (Native.GetForegroundWindow() != target) return;
            throw new AxonError("user_in_window",
                "The user is working in that window right now.",
                "Work somewhere else, wait for them to stop, or pass mode:\"take\" if they asked you to drive this window while they watch.");
        }

        // Gate immediately before anything that moves the cursor or steals the
        // foreground. Returns the number of ms spent waiting, or -1 if none.
        static int GuardDisturb(Dictionary<string, object> a)
        {
            GuardStopped();
            Overlay.MarkActive();
            string mode = ModeOf(a);
            if (mode == "take" || mode == "exclusive") return -1;
            if (!Presence.Available) return -1;
            if (!Presence.Active(_idleMs)) return -1;

            if (mode == "yield")
                throw new AxonError("user_busy",
                    "The user is using the mouse or keyboard, and this step needs the cursor or the foreground.",
                    "Reading and pattern-based actions still work while they are busy. Retry when they pause.");

            int start = Environment.TickCount;
            if (!Presence.WaitForQuiet(_idleMs, _waitMs))
                throw new AxonError("user_busy",
                    "Waited " + _waitMs + "ms but the user is still active, and this step needs the cursor or the foreground.",
                    "Reading and pattern-based actions still work while they are busy. Retry when they pause, or pass mode:\"take\" to interrupt them.");
            int waited = Environment.TickCount - start;
            return waited > 0 ? waited : -1;
        }

        static void NoteWait(Dictionary<string, object> res, int waited)
        {
            if (waited > 0) res["waited_for_user_ms"] = waited;
        }

        static object OpPresence(Dictionary<string, object> a)
        {
            Dictionary<string, object> r = Presence.Report();
            r["user_active"] = Presence.Active(_idleMs);
            r["user_busy"] = Presence.Busy(_idleMs);
            r["idle_threshold_ms"] = _idleMs;
            r["stop_requested"] = Overlay.StopRequested;
            r["input_blocked"] = Native.InputBlocked;
            r["mode"] = _mode;
            IntPtr fg = Native.GetForegroundWindow();
            r["foreground_hwnd"] = Hwnd(fg);
            try
            {
                if (fg != IntPtr.Zero)
                {
                    AutomationElement fe = AutomationElement.FromHandle(fg);
                    if (fe != null)
                    {
                        r["foreground_title"] = fe.Current.Name;
                        uint pid;
                        Native.GetWindowThreadProcessId(fg, out pid);
                        try { r["foreground_process"] = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
                        catch { }
                    }
                }
            }
            catch { }
            return r;
        }

        // ---- actions ----------------------------------------------------------

        // Window handles reach Computer Use from two directions: as an int from
        // UIAutomation's NativeWindowHandle, and as an IntPtr from user32. On a
        // handle with the high bit set those sign-extend differently, so every
        // comparison goes through the same 32-bit normalisation.
        internal static long Hwnd(IntPtr h) { return (long)(int)h; }
        internal static long Hwnd(long h) { return (long)(int)h; }

        static IntPtr HwndArg(Dictionary<string, object> a)
        {
            long h = Long(Get(a, "hwnd"), 0);
            return h == 0 ? IntPtr.Zero : new IntPtr(h);
        }

        // Keystrokes go wherever focus is, not to a window we name, so sending
        // them while another app is in front types into that app instead. Raise
        // the intended window first and refuse if it will not come forward.
        static void RequireForeground(IntPtr expect) { RequireForeground(expect, null, null); }

        static void RequireForeground(IntPtr expect, Dictionary<string, object> a, Dictionary<string, object> res)
        {
            if (expect == IntPtr.Zero) return;
            if (Native.GetForegroundWindow() == expect) return;
            if (a != null) NoteWait(res, GuardDisturb(a));
            Native.ForceForeground(expect);
            System.Threading.Thread.Sleep(120);
            if (Native.GetForegroundWindow() != expect)
                throw new AxonError("window_not_focused",
                    "The target window is not in front, so keystrokes would go to whatever is.",
                    "Another app is holding the foreground, often a modal dialog. Resolve that first.");
        }

        static int EnvInt(string name, int fallback)
        {
            string v = Environment.GetEnvironmentVariable(name);
            int n;
            if (!string.IsNullOrEmpty(v) && int.TryParse(v, out n) && n >= 0) return n;
            return fallback;
        }

        static bool Exclusive(Dictionary<string, object> a) { return ModeOf(a) == "exclusive"; }

        static void PhysicalClick(Dictionary<string, object> a, int[] point, string button, int clicks, IntPtr expectWindow, Dictionary<string, object> res)
        {
            // This is one of only two things Computer Use does that the user can feel.
            NoteWait(res, GuardDisturb(a));

            // Borrow the pointer, then put it back where they left it.
            Native.POINT saved;
            bool haveSaved = Native.GetCursorPos(out saved);

            // A real click lands on whatever is topmost at that point. If
            // something covers the target, the click would go to the wrong app,
            // so confirm ownership rather than clicking blind.
            if (expectWindow != IntPtr.Zero)
            {
                IntPtr owner = Native.RootWindowAt(point[0], point[1]);
                // Computer Use's own marker and banner never count as a cover.
                if (Overlay.IsOwnWindow(owner)) owner = expectWindow;
                if (owner != IntPtr.Zero && owner != expectWindow)
                {
                    // Only take/exclusive may raise a covered window - those are
                    // the modes where the user has said to drive. In share and
                    // yield, raising it would yank the user away from whatever
                    // they are doing in front, so instead refuse and let Claude
                    // use the pattern path, which needs no foreground at all.
                    string m = ModeOf(a);
                    bool mayRaise = (m == "take" || m == "exclusive");
                    if (mayRaise)
                    {
                        Native.ForceForeground(expectWindow);
                        System.Threading.Thread.Sleep(120);
                        owner = Native.RootWindowAt(point[0], point[1]);
                    }
                    if (owner != IntPtr.Zero && owner != expectWindow)
                        throw new AxonError("obscured",
                            "That control is behind another window, so a real click would hit the wrong one.",
                            "Target it by index or selector instead - the pattern path clicks it where it sits, without raising it over what the user is doing.");
                }
            }
            bool held = Exclusive(a) && Native.BeginExclusive();
            try
            {
                Native.SetCursorPos(point[0], point[1]);
                System.Threading.Thread.Sleep(16);
                for (int n = 0; n < clicks; n++)
                {
                    Native.MouseButton(button, true);
                    System.Threading.Thread.Sleep(12);
                    Native.MouseButton(button, false);
                    if (n < clicks - 1) System.Threading.Thread.Sleep(60);
                }
            }
            finally { if (held) Native.EndExclusive(); }
            if (held) res["input_held"] = true;
            // Always put the pointer back. The click is the action; leaving the
            // cursor parked on the control just makes the user's own pointer
            // appear to jump away. Even in take mode, snapping it back the moment
            // the click lands is what the user expects - a brief flick, not a
            // relocation. Only skip when input is being held (exclusive), where
            // the user's pointer is frozen anyway and cannot be confused.
            if (haveSaved && !held)
            {
                Native.SetCursorPos(saved.X, saved.Y);
                res["cursor_restored"] = true;
            }
        }

        // UIA pattern calls block until the target app's message pump reports
        // idle, and an app that never quite does pins the host for seconds. The
        // action still has to complete, so cutting it short would mean reporting
        // a click that never happened. The deadline is therefore generous: it
        // exists to stop a wedged provider hanging the session forever, not to
        // rush a slow one. Anything that hits it is reported as slow_provider
        // so the caller knows the outcome is unconfirmed.
        const int PatternDeadlineMs = 15000;

        static bool RunPattern(Action act)
        {
            Exception failure = null;
            using (System.Threading.ManualResetEventSlim done = new System.Threading.ManualResetEventSlim(false))
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try { act(); }
                    catch (Exception ex) { failure = ex; }
                    finally { try { done.Set(); } catch { } }
                });
                bool finished = done.Wait(PatternDeadlineMs);
                if (finished && failure != null) throw failure;
                return finished;
            }
        }

        // What the element looks like now that the action has landed. This is
        // the difference between "clicked" and "clicked, and the label now reads
        // pressed:3" - it saves Claude a whole follow-up snapshot per action,
        // which is the single most common wasted round-trip.
        static void AddNowState(Dictionary<string, object> res, AutomationElement el)
        {
            try
            {
                List<string> patterns = PatternsOf(el);

                // Only elements that carry state have a meaningful "after". A
                // plain button does not, and asking one for its value or text
                // costs two UIA timeouts - four seconds - for nothing.
                bool stateful = Has(patterns, "Value") || Has(patterns, "Toggle")
                             || Has(patterns, "SelectionItem") || Has(patterns, "ExpandCollapse")
                             || Has(patterns, "RangeValue");
                if (!stateful) return;

                Dictionary<string, object> now = new Dictionary<string, object>();
                // Value only. TextPattern is the slow one and adds nothing here.
                string v = null;
                if (Has(patterns, "Value"))
                {
                    try
                    {
                        ValuePattern vp = el.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
                        if (vp != null) v = vp.Current.Value;
                    }
                    catch { }
                }
                if (!string.IsNullOrEmpty(v)) now["text"] = v.Length > 200 ? v.Substring(0, 200) + "..." : v;
                Dictionary<string, object> st = StateOf(el, patterns);
                if (st != null)
                {
                    object o;
                    if (st.TryGetValue("toggle", out o)) now["toggle"] = o;
                    if (st.TryGetValue("selected", out o)) now["selected"] = o;
                    if (st.TryGetValue("expand", out o)) now["expand"] = o;
                    if (st.TryGetValue("value", out o)) now["value"] = o;
                    if (st.TryGetValue("disabled", out o)) now["disabled"] = o;
                }
                if (now.Count > 0) res["now"] = now;
            }
            catch { /* the element may have gone; the action still succeeded */ }
        }

        // Draw what just happened, where it happened - but only if the user can
        // actually see the spot. When Computer Use acts on a window behind the
        // one the user is working in, drawing a marker there would paint on top
        // of the user's front window instead: "going over their thing". So the
        // marker and cursor are suppressed when the target control is covered by
        // another window, and the top banner alone signals that work is going on.
        static void Trace(AutomationElement el, string label, IntPtr expectWindow)
        {
            if (!Overlay.Enabled || el == null) return;
            try
            {
                int[] r = RectOf(el);
                if (r == null) return;
                if (IsOccluded(r, expectWindow)) return;

                // Name the control, not just the verb. "click: Save" is
                // readable from across the room; a bare rectangle is not.
                string name = NameOf(el);
                if (!string.IsNullOrEmpty(name))
                {
                    if (name.Length > 34) name = name.Substring(0, 34) + "...";
                    label = label + ": " + name;
                }
                Overlay.Flash(r, label);
            }
            catch { }
        }

        // True only when we are CONFIDENT the control is behind another window:
        // the caller told us which window it belongs to (the MCP layer always
        // does), and the pixel at the control's centre is owned by a different,
        // real application window. If the target window is unknown, or the pixel
        // belongs to the target or to Computer Use's own overlay, it is treated
        // as visible - so the marker and cursor show in the normal foreground
        // case, and are hidden only for genuine behind-another-window work.
        static bool IsOccluded(int[] r, IntPtr expectWindow)
        {
            try
            {
                if (expectWindow == IntPtr.Zero) return false;
                int cx = r[0] + r[2] / 2;
                int cy = r[1] + r[3] / 2;
                IntPtr onTop = Native.RootWindowAt(cx, cy);
                if (onTop == IntPtr.Zero || Overlay.IsOwnWindow(onTop)) return false;

                IntPtr expRoot = Native.GetAncestor(expectWindow, 2 /* GA_ROOT */);
                if (expRoot == IntPtr.Zero) expRoot = expectWindow;
                return onTop != expRoot;
            }
            catch { return false; }
        }

        static object OpClick(Dictionary<string, object> a)
        {
            GuardSameWindow(a, HwndArg(a));
            Target t = ResolveTarget(a);
            string button = Str(Get(a, "button"));
            if (string.IsNullOrEmpty(button)) button = "left";
            int clicks = Int(Get(a, "clicks"), 1);
            bool forcePhysical = Bool(Get(a, "physical"), false);

            Dictionary<string, object> res = new Dictionary<string, object>();

            if (t.Mode == "point")
            {
                PhysicalClick(a, t.Point, button, clicks, HwndArg(a), res);
                res["method"] = "physical";
                res["point"] = t.Point;
                return res;
            }

            AutomationElement el = t.El;
            List<string> patterns = PatternsOf(el);

            // Pattern invoke is the fast deterministic path: no cursor movement,
            // no dependence on the window being unobscured or on top.
            if (!forcePhysical && button == "left" && clicks == 1)
            {
                try
                {
                    if (Has(patterns, "Invoke"))
                    {
                        InvokePattern p = el.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                        if (p != null)
                        {
                            Trace(el, "click", HwndArg(a));
                            InvokePattern ip = p;
                            if (!RunPattern(delegate { ip.Invoke(); })) res["slow_provider"] = true;
                            res["method"] = "invoke_pattern";
                            AddNowState(res, el);
                            return res;
                        }
                    }
                    if (Has(patterns, "Toggle"))
                    {
                        TogglePattern p = el.GetCurrentPattern(TogglePattern.Pattern) as TogglePattern;
                        if (p != null)
                        {
                            Trace(el, "toggle", HwndArg(a));
                            TogglePattern tp2 = p;
                            if (!RunPattern(delegate { tp2.Toggle(); })) res["slow_provider"] = true;
                            res["method"] = "toggle_pattern";
                            res["toggle"] = p.Current.ToggleState.ToString();
                            return res;
                        }
                    }
                    if (Has(patterns, "SelectionItem"))
                    {
                        SelectionItemPattern p = el.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
                        if (p != null)
                        {
                            Trace(el, "select", HwndArg(a));
                            SelectionItemPattern sp2 = p;
                            if (!RunPattern(delegate { sp2.Select(); })) res["slow_provider"] = true;
                            res["method"] = "selection_pattern";
                            AddNowState(res, el);
                            return res;
                        }
                    }
                    if (Has(patterns, "ExpandCollapse"))
                    {
                        ExpandCollapsePattern p = el.GetCurrentPattern(ExpandCollapsePattern.Pattern) as ExpandCollapsePattern;
                        if (p != null)
                        {
                            if (p.Current.ExpandCollapseState == ExpandCollapseState.Collapsed) p.Expand();
                            else p.Collapse();
                            res["method"] = "expand_collapse_pattern";
                            res["state"] = p.Current.ExpandCollapseState.ToString();
                            return res;
                        }
                    }
                }
                catch (AxonError) { throw; }
                catch { /* fall through to the physical path */ }
            }

            try { el.SetFocus(); } catch { }
            Trace(el, "click", HwndArg(a));
            int[] point = ClickPointOf(el);
            PhysicalClick(a, point, button, clicks, HwndArg(a), res);
            res["method"] = "physical";
            res["point"] = point;
            System.Threading.Thread.Sleep(60);
            AddNowState(res, el);
            return res;
        }

        static object OpSetValue(Dictionary<string, object> a)
        {
            GuardSameWindow(a, HwndArg(a));
            Target t = ResolveTarget(a);
            if (t.Mode == "point")
                throw new AxonError("bad_target", "set_value needs an element, not a point.", "Use index or selector.");
            AutomationElement el = t.El;
            List<string> patterns = PatternsOf(el);
            string text = Str(Get(a, "text"));
            if (text == null) text = "";

            Dictionary<string, object> res = new Dictionary<string, object>();

            if (Has(patterns, "Value"))
            {
                ValuePattern vp = null;
                try { vp = el.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern; }
                catch { }
                if (vp != null)
                {
                    if (vp.Current.IsReadOnly)
                        throw new AxonError("readonly", "That field is read-only.", null);
                    try
                    {
                        Trace(el, "type", HwndArg(a));
                        vp.SetValue(text);
                        res["method"] = "value_pattern";
                        res["value"] = vp.Current.Value;
                        AddNowState(res, el);
                        return res;
                    }
                    catch { /* fall through to typing */ }
                }
            }

            // The value pattern was unavailable, so this falls back to real
            // keystrokes - which the user can feel.
            NoteWait(res, GuardDisturb(a));
            try { el.SetFocus(); }
            catch
            {
                throw new AxonError("focus_failed", "Could not focus that element to type into it.",
                    "Call focus on the window first.");
            }
            System.Threading.Thread.Sleep(40);
            Native.KeyDown(0x11); Native.KeyTap(0x41); Native.KeyUp(0x11); // ctrl+a
            System.Threading.Thread.Sleep(20);
            Native.TypeUnicode(text);
            res["method"] = "typed";
            return res;
        }

        static object OpType(Dictionary<string, object> a)
        {
            string text = Str(Get(a, "text"));
            if (text == null) text = "";
            Dictionary<string, object> res = new Dictionary<string, object>();
            GuardSameWindow(a, HwndArg(a));
            if (Get(a, "index") != null || Get(a, "selector") != null)
            {
                Target t = ResolveTarget(a);
                try { t.El.SetFocus(); } catch { }
                System.Threading.Thread.Sleep(40);
            }
            else
            {
                // No element named, so this types into whatever holds focus.
                RequireForeground(HwndArg(a), a, res);
            }
            bool held = Exclusive(a) && Native.BeginExclusive();
            try { Native.TypeUnicode(text); }
            finally { if (held) Native.EndExclusive(); }
            if (held) res["input_held"] = true;
            res["typed"] = text.Length;
            return res;
        }

        static Dictionary<string, ushort> _vk;

        static ushort VkOf(string token)
        {
            if (_vk == null)
            {
                _vk = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
                _vk["enter"] = 0x0D; _vk["return"] = 0x0D; _vk["tab"] = 0x09;
                _vk["esc"] = 0x1B; _vk["escape"] = 0x1B; _vk["space"] = 0x20;
                _vk["backspace"] = 0x08; _vk["back"] = 0x08;
                _vk["delete"] = 0x2E; _vk["del"] = 0x2E; _vk["insert"] = 0x2D;
                _vk["home"] = 0x24; _vk["end"] = 0x23; _vk["pageup"] = 0x21; _vk["pagedown"] = 0x22;
                _vk["up"] = 0x26; _vk["down"] = 0x28; _vk["left"] = 0x25; _vk["right"] = 0x27;
                _vk["ctrl"] = 0x11; _vk["control"] = 0x11; _vk["shift"] = 0x10;
                _vk["alt"] = 0x12; _vk["win"] = 0x5B; _vk["meta"] = 0x5B;
                for (int i = 1; i <= 12; i++) _vk["f" + i.ToString(CultureInfo.InvariantCulture)] = (ushort)(0x70 + i - 1);
            }
            string t = token.Trim();
            ushort v;
            if (_vk.TryGetValue(t, out v)) return v;
            if (t.Length == 1)
            {
                char ch = char.ToUpperInvariant(t[0]);
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')) return (ushort)ch;
            }
            throw new AxonError("unknown_key", "Unrecognised key '" + token + "'.",
                "Use names like ctrl+s, alt+f4, enter, tab, f5.");
        }

        static object OpKey(Dictionary<string, object> a)
        {
            string chord = Str(Get(a, "keys"));
            if (string.IsNullOrEmpty(chord))
                throw new AxonError("bad_keys", "Empty key chord.", null);
            string[] rawParts = chord.Split('+');
            List<string> parts = new List<string>();
            foreach (string p in rawParts) if (p.Trim().Length > 0) parts.Add(p.Trim());
            if (parts.Count == 0) throw new AxonError("bad_keys", "Empty key chord.", null);

            List<ushort> mods = new List<ushort>();
            for (int i = 0; i < parts.Count - 1; i++) mods.Add(VkOf(parts[i]));
            ushort main = VkOf(parts[parts.Count - 1]);

            Dictionary<string, object> res = new Dictionary<string, object>();
            GuardSameWindow(a, HwndArg(a));
            RequireForeground(HwndArg(a), a, res);

            foreach (ushort m in mods) { Native.KeyDown(m); System.Threading.Thread.Sleep(8); }
            Native.KeyTap(main);
            System.Threading.Thread.Sleep(8);
            for (int i = mods.Count - 1; i >= 0; i--) Native.KeyUp(mods[i]);

            res["sent"] = chord;
            return res;
        }

        static object OpScroll(Dictionary<string, object> a)
        {
            int amount = Int(Get(a, "amount"), -3);
            bool horizontal = Bool(Get(a, "horizontal"), false);
            Dictionary<string, object> res = new Dictionary<string, object>();
            GuardSameWindow(a, HwndArg(a));

            if (Get(a, "index") != null || Get(a, "selector") != null)
            {
                Target t = ResolveTarget(a);
                AutomationElement el = t.El;
                List<string> patterns = PatternsOf(el);
                if (Has(patterns, "Scroll"))
                {
                    try
                    {
                        ScrollPattern p = el.GetCurrentPattern(ScrollPattern.Pattern) as ScrollPattern;
                        if (p != null)
                        {
                            bool big = Math.Abs(amount) >= 3;
                            ScrollAmount unit;
                            if (amount < 0) unit = big ? ScrollAmount.LargeIncrement : ScrollAmount.SmallIncrement;
                            else unit = big ? ScrollAmount.LargeDecrement : ScrollAmount.SmallDecrement;
                            if (horizontal) p.Scroll(unit, ScrollAmount.NoAmount);
                            else p.Scroll(ScrollAmount.NoAmount, unit);
                            res["method"] = "scroll_pattern";
                            return res;
                        }
                    }
                    catch { }
                }
                try
                {
                    int[] pt = ClickPointOf(el);
                    Native.SetCursorPos(pt[0], pt[1]);
                }
                catch { }
            }
            else
            {
                object pt = Get(a, "point");
                object[] arr = pt as object[];
                if (arr != null && arr.Length >= 2) Native.SetCursorPos(Int(arr[0], 0), Int(arr[1], 0));
            }

            NoteWait(res, GuardDisturb(a));
            System.Threading.Thread.Sleep(16);
            Native.MouseWheel(amount * 120, horizontal);
            res["method"] = "wheel";
            res["delta"] = amount * 120;
            return res;
        }

        static object OpFocus(Dictionary<string, object> a)
        {
            AutomationElement win = RequireWindow(a);
            IntPtr h;
            string title;
            try
            {
                h = new IntPtr(win.Current.NativeWindowHandle);
                title = win.Current.Name;
            }
            catch { throw new AxonError("window_gone", "The window disappeared while resolving it.", null); }

            Dictionary<string, object> res = new Dictionary<string, object>();
            NoteWait(res, GuardDisturb(a));
            bool ok = Native.ForceForeground(h);
            System.Threading.Thread.Sleep(120);

            res["focused"] = ok;
            res["hwnd"] = (long)h;
            res["title"] = title;
            return res;
        }

        static object OpCloseWindow(Dictionary<string, object> a)
        {
            AutomationElement win = RequireWindow(a);
            IntPtr h;
            string title;
            try
            {
                h = new IntPtr(win.Current.NativeWindowHandle);
                title = win.Current.Name;
            }
            catch { throw new AxonError("window_gone", "The window disappeared while resolving it.", null); }

            // WM_CLOSE to one specific window. Computer Use has no process-termination
            // op at all: closing a window lets the app run its own save prompts,
            // where killing a process discards unsaved work without asking.
            Native.SendMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
            System.Threading.Thread.Sleep(250);

            Dictionary<string, object> res = new Dictionary<string, object>();
            res["closed"] = title;
            res["still_open"] = Native.IsWindow(h);
            return res;
        }

        static object OpWaitFor(Dictionary<string, object> a)
        {
            int timeout = Int(Get(a, "timeout_ms"), 5000);
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeout);
            DateTime started = DateTime.UtcNow;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    AutomationElement found = FindBySelector(a);
                    if (found != null)
                    {
                        Dictionary<string, object> res = new Dictionary<string, object>();
                        res["found"] = true;
                        res["role"] = RoleOf(found);
                        res["name"] = NameOf(found);
                        res["rect"] = RectOf(found);
                        res["waited_ms"] = (long)(DateTime.UtcNow - started).TotalMilliseconds;
                        return res;
                    }
                }
                catch (AxonError ax)
                {
                    // A malformed selector or a vanished window is a real error;
                    // only "not there yet" is worth retrying.
                    if (ax.Code != "element_not_found") throw;
                }
                System.Threading.Thread.Sleep(200);
            }
            throw new AxonError("wait_timeout", "Element did not appear within " + timeout + "ms.",
                "Take a snapshot to see the current state of the window.");
        }

        static object OpScreenshot(Dictionary<string, object> a)
        {
            int maxWidth = Int(Get(a, "max_width"), 1200);
            int quality = Int(Get(a, "quality"), 60);
            if (quality < 20) quality = 20;
            if (quality > 95) quality = 95;

            Rectangle region = Rectangle.Empty;
            bool wantedWindow = Get(a, "hwnd") != null || Get(a, "title") != null;

            if (wantedWindow)
            {
                AutomationElement win = RequireWindow(a);
                IntPtr h = new IntPtr(win.Current.NativeWindowHandle);
                if (Native.IsIconic(h))
                    throw new AxonError("window_minimized", "That window is minimized; there is nothing to capture.",
                        "Call focus on it first.");
                Native.RECT rr;
                if (!Native.GetWindowRect(h, out rr))
                    throw new AxonError("window_rect_failed", "Could not read that window's bounds.",
                        "The window may be closing. Re-list windows and try again.");
                region = new Rectangle(rr.Left, rr.Top, rr.Right - rr.Left, rr.Bottom - rr.Top);
                // Falling back to a full-screen grab here would quietly hand back
                // something the caller never asked for, so it is an error instead.
                if (region.Width <= 0 || region.Height <= 0)
                    throw new AxonError("window_rect_failed", "That window reports a zero-size rectangle.",
                        "It may be minimized or mid-close.");
            }
            else
            {
                Rectangle vs = SystemInformation.VirtualScreen;
                region = new Rectangle(vs.X, vs.Y, vs.Width, vs.Height);
            }
            if (region.Width <= 0 || region.Height <= 0)
                throw new AxonError("bad_region", "Capture region has no area.", null);

            byte[] bytes;
            int nw, nh;
            using (Bitmap full = new Bitmap(region.Width, region.Height))
            {
                using (Graphics g = Graphics.FromImage(full))
                    g.CopyFromScreen(region.Location, Point.Empty, region.Size);

                double scale = Math.Min(1.0, maxWidth / (double)region.Width);
                nw = Math.Max(1, (int)(region.Width * scale));
                nh = Math.Max(1, (int)(region.Height * scale));

                using (Bitmap small = new Bitmap(nw, nh))
                {
                    using (Graphics g2 = Graphics.FromImage(small))
                    {
                        g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g2.DrawImage(full, 0, 0, nw, nh);
                    }
                    ImageCodecInfo enc = null;
                    foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                        if (c.MimeType == "image/jpeg") { enc = c; break; }

                    using (MemoryStream ms = new MemoryStream())
                    {
                        if (enc != null)
                        {
                            EncoderParameters ep = new EncoderParameters(1);
                            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                            small.Save(ms, enc, ep);
                        }
                        else
                        {
                            small.Save(ms, ImageFormat.Jpeg);
                        }
                        bytes = ms.ToArray();
                    }
                }
            }

            Dictionary<string, object> res = new Dictionary<string, object>();
            res["mime"] = "image/jpeg";
            res["width"] = nw;
            res["height"] = nh;
            res["source"] = new int[] { region.X, region.Y, region.Width, region.Height };
            res["bytes"] = bytes.Length;
            res["data"] = Convert.ToBase64String(bytes);
            return res;
        }

        static object OpPing()
        {
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["ok"] = true;
            res["dpi_mode"] = _dpiMode;
            res["clr"] = Environment.Version.ToString();
            res["snapshots"] = _snapshots.Count;
            return res;
        }
    }
}
