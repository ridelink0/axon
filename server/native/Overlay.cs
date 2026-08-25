// Overlay - shows the user what Axon just touched.
//
// Codex's macOS build has a designed virtual cursor: it wiggles while the model
// thinks and takes its colour from the wallpaper. Its Windows build instead
// drives the real system cursor, and has a standing bug where the cursor is
// left invisible until you reboot (openai/codex#25200). The reporter there asks
// for the macOS approach - an overlay, rather than manipulating the system
// cursor. This is that.
//
// Axon never calls SetCursor, ShowCursor, or SetSystemCursor. It cannot leave
// your cursor in a bad state because it never touches it.
//
// A moving fake cursor would also be the wrong picture here: Axon usually acts
// through accessibility patterns, where no pointer moves at all. What actually
// happened is "this control, this action", so that is what gets drawn.
//
// Four properties matter, and all four are enforced:
//   - click-through      WS_EX_TRANSPARENT, so it can never intercept a click
//   - never activates    WS_EX_NOACTIVATE, so it cannot steal focus
//   - not a real window  WS_EX_TOOLWINDOW, so it stays out of alt-tab
//   - invisible to capture  WDA_EXCLUDEFROMCAPTURE, so Axon's own screenshots
//                        never contain Axon's own UI

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Axon
{
    internal static class Overlay
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("dwmapi.dll")] static extern int DwmGetColorizationColor(out uint color, out bool opaque);

        const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        static Thread _thread;
        static TraceForm _form;
        static ChromeForm _chrome;
        static System.Windows.Forms.Timer _idleTimer;
        static volatile bool _stopRequested;

        // Claude's own colour, so the ring reads as Claude working rather
        // than as some anonymous program having taken the screen.
        static readonly Color ClaudeColour = Color.FromArgb(217, 119, 87);

        internal static bool StopRequested { get { return _stopRequested; } }

        // Read once and cleared, so one press stops one run.
        internal static bool ConsumeStop()
        {
            if (!_stopRequested) return false;
            _stopRequested = false;
            return true;
        }

        // Called by every acting op. Raises the ring and banner, and keeps
        // them up until Axon has been quiet for a couple of seconds.
        internal static void MarkActive()
        {
            if (!Enabled || _chrome == null) return;
            try
            {
                _chrome.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        _chrome.ShowChrome();
                        if (_idleTimer != null) { _idleTimer.Stop(); _idleTimer.Start(); }
                    }
                    catch { }
                });
            }
            catch { }
        }
        static volatile bool _enabled = true;
        static volatile bool _ready;

        internal static bool Enabled { get { return _enabled && _ready; } }
        static IntPtr _hwnd;
        static IntPtr _chromeHwnd;
        internal static IntPtr Handle { get { return _hwnd; } }
        internal static IntPtr ChromeHandle { get { return _chromeHwnd; } }

        internal static void Start(bool enabled)
        {
            _enabled = enabled;
            if (!enabled) return;
            try
            {
                _thread = new Thread(Run);
                _thread.IsBackground = true;
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Name = "axon-overlay";
                _thread.Start();
                for (int i = 0; i < 40 && !_ready; i++) Thread.Sleep(25);
            }
            catch
            {
                // The overlay is decoration. If it will not start, Axon carries
                // on without it rather than failing the session.
                _enabled = false;
            }
        }

        static void Run()
        {
            try
            {
                _form = new TraceForm(AccentColour());
                _form.CreateControl();
                IntPtr h = _form.Handle;   // forces creation on this thread
                _hwnd = h;                 // captured here, read from anywhere
                try { SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE); } catch { }

                _chrome = new ChromeForm(ClaudeColour);
                _chrome.CreateControl();
                _chromeHwnd = _chrome.Handle;
                try { SetWindowDisplayAffinity(_chromeHwnd, WDA_EXCLUDEFROMCAPTURE); } catch { }

                _idleTimer = new System.Windows.Forms.Timer();
                _idleTimer.Interval = 2200;
                _idleTimer.Tick += delegate { _idleTimer.Stop(); try { _chrome.HideChrome(); } catch { } };

                _ready = true;
                Application.Run(_form);
            }
            catch { _enabled = false; }
            finally { _ready = false; _hwnd = IntPtr.Zero; }
        }

        // Codex derives its cursor colour from the wallpaper. The nearest honest
        // Windows equivalent is the user's own accent colour, which is already
        // the colour their system highlights things with.
        static Color AccentColour()
        {
            try
            {
                uint argb;
                bool opaque;
                if (DwmGetColorizationColor(out argb, out opaque) == 0)
                {
                    Color c = Color.FromArgb((int)(argb & 0x00FFFFFF) | unchecked((int)0xFF000000));
                    // Keep it readable against both light and dark backgrounds.
                    if (c.GetBrightness() < 0.35f) c = ControlPaint.Light(c, 0.4f);
                    return c;
                }
            }
            catch { }
            return Color.FromArgb(255, 0, 120, 212);
        }

        // rect is screen coordinates; label is what Axon just did there.
        internal static void Flash(int[] rect, string label)
        {
            if (!Enabled || rect == null || _form == null) return;
            try
            {
                _form.BeginInvoke((MethodInvoker)delegate
                {
                    try { _form.Show(new Rectangle(rect[0], rect[1], rect[2], rect[3]), label); }
                    catch { }
                });
            }
            catch { /* form torn down mid-call */ }
        }

        internal static void Stop()
        {
            try { if (_form != null) _form.BeginInvoke((MethodInvoker)delegate { Application.ExitThread(); }); }
            catch { }
        }

        // The ring is one layered window spanning the virtual screen, hollow in
        // the middle, so it frames every display without covering any of it.
        // Codex has no equivalent on Windows - its own issue #19305 is a request
        // for "visible indication when Codex is observing or controlling the
        // desktop". Claude Code's macOS build does have it, as a notification
        // reading "Claude is using your computer". This brings that to Windows.
        sealed class ChromeForm : Form
        {
            readonly Color _c;
            readonly Button _stop;

            internal ChromeForm(Color c)
            {
                _c = c;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.Magenta;
                TransparencyKey = Color.Magenta;
                Bounds = SystemInformation.VirtualScreen;

                // This window is the size of every monitor put together, so an
                // unbuffered repaint of it flickers badly. Paint off-screen and
                // blit once.
                SetStyle(ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.UserPaint, true);
                DoubleBuffered = true;

                _stop = new Button();
                _stop.Text = "Stop";
                _stop.FlatStyle = FlatStyle.Flat;
                _stop.BackColor = Color.White;
                _stop.ForeColor = Color.FromArgb(30, 30, 30);
                _stop.FlatAppearance.BorderSize = 0;
                _stop.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
                _stop.Size = new Size(104, 36);
                _stop.Cursor = Cursors.Hand;
                _stop.Click += delegate { _stopRequested = true; HideChrome(); };
                Controls.Add(_stop);
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    // No WS_EX_TRANSPARENT: Stop has to be clickable. NOACTIVATE
                    // still means pressing it never moves the user's focus.
                    cp.ExStyle |= 0x00000080 | 0x08000000 | 0x00080000;
                    return cp;
                }
            }

            static Size BannerSize() { return new Size(380, 54); }

            // Called on every action, so it must do nothing at all once the
            // chrome is already up. Re-setting bounds, re-asserting TopMost or
            // invalidating on each call is what made it strobe.
            internal void ShowChrome()
            {
                if (Visible) return;
                Bounds = SystemInformation.VirtualScreen;
                Size sz = BannerSize();
                int x = (Width - sz.Width) / 2;
                _stop.Location = new Point(x + sz.Width - _stop.Width - 12, 10 + (sz.Height - _stop.Height) / 2);
                Show();
                TopMost = true;
            }

            internal void HideChrome() { if (Visible) Hide(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using (Pen pen = new Pen(_c, 4f))
                {
                    pen.Alignment = PenAlignment.Inset;
                    g.DrawRectangle(pen, new Rectangle(0, 0, Width - 1, Height - 1));
                }

                Size sz = BannerSize();
                Rectangle banner = new Rectangle((Width - sz.Width) / 2, 8, sz.Width, sz.Height);
                using (SolidBrush bg = new SolidBrush(_c))
                using (GraphicsPath path = RoundRect(banner, 8f))
                    g.FillPath(bg, path);

                using (Font f = new Font("Segoe UI", 10.5f, FontStyle.Regular))
                using (SolidBrush fg = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat())
                {
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("Claude is using your computer", f, fg,
                        new RectangleF(banner.X + 16, banner.Y, banner.Width - _stop.Width - 28, banner.Height), sf);
                }
            }

            static GraphicsPath RoundRect(Rectangle r, float radius)
            {
                GraphicsPath p = new GraphicsPath();
                float d = radius * 2;
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }
        }

        sealed class TraceForm : Form
        {
            readonly Color _accent;
            readonly System.Windows.Forms.Timer _timer;
            string _label = "";
            Rectangle _target;

            internal TraceForm(Color accent)
            {
                _accent = accent;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.Magenta;
                TransparencyKey = Color.Magenta;   // hollow centre, border only
                Opacity = 0.95;
                Bounds = new Rectangle(-10000, -10000, 10, 10);

                _timer = new System.Windows.Forms.Timer();
                _timer.Interval = 550;
                _timer.Tick += delegate { _timer.Stop(); Hide(); };
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= 0x00000080   // WS_EX_TOOLWINDOW  - out of alt-tab
                               |  0x08000000   // WS_EX_NOACTIVATE  - never takes focus
                               |  0x00000020   // WS_EX_TRANSPARENT - clicks pass through
                               |  0x00080000;  // WS_EX_LAYERED
                    return cp;
                }
            }

            internal void Show(Rectangle target, string label)
            {
                _label = label ?? "";
                _target = target;
                // Room for the border stroke and the caption above it.
                Bounds = new Rectangle(target.X - 4, target.Y - 22, target.Width + 8, target.Height + 26);
                if (!Visible) Show();
                TopMost = true;
                Invalidate();
                _timer.Stop();
                _timer.Start();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                Rectangle box = new Rectangle(4, 22, _target.Width, _target.Height);
                using (Pen pen = new Pen(_accent, 2f))
                {
                    pen.Alignment = PenAlignment.Inset;
                    g.DrawRectangle(pen, box);
                }

                if (_label.Length > 0)
                {
                    using (Font f = new Font("Segoe UI", 8f, FontStyle.Regular))
                    using (SolidBrush bg = new SolidBrush(_accent))
                    using (SolidBrush fg = new SolidBrush(Color.White))
                    {
                        SizeF size = g.MeasureString(_label, f);
                        RectangleF tag = new RectangleF(4, 22 - size.Height - 2, size.Width + 10, size.Height + 2);
                        using (GraphicsPath path = Rounded(tag, 3f)) g.FillPath(bg, path);
                        g.DrawString(_label, f, fg, tag.X + 5, tag.Y + 1);
                    }
                }
            }

            static GraphicsPath Rounded(RectangleF r, float radius)
            {
                GraphicsPath p = new GraphicsPath();
                float d = radius * 2;
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && _timer != null) _timer.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
