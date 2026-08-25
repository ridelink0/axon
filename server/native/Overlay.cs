// Overlay - everything Axon puts on screen.
//
// Three pieces, one colour:
//   - a ring around the whole desktop while Axon is working, with a banner
//     reading "Claude is using your computer" and a Stop button
//   - a Claude-coloured cursor showing where Axon is, alongside your own
//   - a marker flashing around each control as it is touched
//
// Axon never calls SetCursor, ShowCursor, or SetSystemCursor. Your pointer is
// never touched, so it cannot be left in a bad state - which is the standing
// Codex bug on Windows (openai/codex#25200), where users are asking for exactly
// this: an overlay cursor instead of hijacking the system one.
//
// Everything here is:
//   - click-through      WS_EX_TRANSPARENT, so it can never take a click
//                        (the Stop button is the one deliberate exception)
//   - never activating   WS_EX_NOACTIVATE, so it cannot steal focus
//   - out of alt-tab     WS_EX_TOOLWINDOW
//   - invisible to capture  WDA_EXCLUDEFROMCAPTURE, so Axon's screenshots never
//                        contain Axon's own UI

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
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        // Anthropic's primary accent, from their own brand palette. One colour,
        // everywhere - the ring, the banner, the cursor and the marker all draw
        // from this, so Axon reads as one thing rather than a palette.
        internal static readonly Color Claude = Color.FromArgb(217, 119, 87);   // #D97757
        internal static readonly Color Ink = Color.FromArgb(20, 20, 19);        // #141413
        internal static readonly Color Paper = Color.FromArgb(250, 249, 245);   // #FAF9F5

        static Thread _thread;
        static TraceForm _marker;
        static ChromeForm _chrome;
        static CursorForm _cursor;
        static System.Windows.Forms.Timer _idleTimer;
        static volatile bool _stopRequested;
        static volatile bool _enabled = true;
        static volatile bool _ready;
        static IntPtr _markerHwnd, _chromeHwnd, _cursorHwnd;
        static string _fontName;

        internal static bool Enabled { get { return _enabled && _ready; } }
        internal static bool StopRequested { get { return _stopRequested; } }

        // Any window Axon itself put on screen. Used to keep them out of its own
        // listings, and to stop its own marker counting as something covering a
        // click target.
        internal static bool IsOwnWindow(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            return h == _markerHwnd || h == _chromeHwnd || h == _cursorHwnd;
        }

        // Read once and cleared, so one press stops one run.
        internal static bool ConsumeStop()
        {
            if (!_stopRequested) return false;
            _stopRequested = false;
            return true;
        }

        // OpenAI Sans and Söhne are licensed faces that cannot ship inside a
        // plugin, so this picks the closest grotesque actually present on the
        // machine rather than pretending to be them.
        internal static string UiFont
        {
            get
            {
                if (_fontName != null) return _fontName;
                string[] wanted = { "Segoe UI Variable Text", "Inter", "Segoe UI", "Arial" };
                foreach (string want in wanted)
                {
                    try
                    {
                        foreach (FontFamily f in FontFamily.Families)
                        {
                            if (string.Equals(f.Name, want, StringComparison.OrdinalIgnoreCase))
                            {
                                _fontName = want;
                                return _fontName;
                            }
                        }
                    }
                    catch { }
                }
                _fontName = "Segoe UI";
                return _fontName;
            }
        }

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
                for (int i = 0; i < 60 && !_ready; i++) Thread.Sleep(25);
            }
            catch
            {
                // The overlay is presentation. If it will not start, Axon carries
                // on without it rather than failing the session.
                _enabled = false;
            }
        }

        static void Run()
        {
            try
            {
                _marker = new TraceForm();
                _marker.CreateControl();
                _markerHwnd = _marker.Handle;
                Exclude(_markerHwnd);

                _chrome = new ChromeForm();
                _chrome.CreateControl();
                _chromeHwnd = _chrome.Handle;
                Exclude(_chromeHwnd);

                _cursor = new CursorForm();
                _cursor.CreateControl();
                _cursorHwnd = _cursor.Handle;
                Exclude(_cursorHwnd);

                _idleTimer = new System.Windows.Forms.Timer();
                _idleTimer.Interval = 2200;
                _idleTimer.Tick += delegate
                {
                    _idleTimer.Stop();
                    try { _chrome.HideChrome(); } catch { }
                    try { _cursor.HideCursor(); } catch { }
                };

                _ready = true;
                Application.Run(_marker);
            }
            catch { _enabled = false; }
            finally { _ready = false; _markerHwnd = _chromeHwnd = _cursorHwnd = IntPtr.Zero; }
        }

        static void Exclude(IntPtr h)
        {
            try { SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE); } catch { }
        }

        // Called by every acting op. Raises the ring, banner and cursor, and
        // keeps them up until Axon has been quiet for a couple of seconds.
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

        // rect is screen coordinates; label is what Axon just did there.
        internal static void Flash(int[] rect, string label)
        {
            if (!Enabled || rect == null || _marker == null) return;
            try
            {
                _marker.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        Rectangle r = new Rectangle(rect[0], rect[1], rect[2], rect[3]);
                        _marker.Flash(r, label);
                        // The cursor sits on whatever Axon just touched, which is
                        // the honest answer to "where is Claude right now".
                        if (_cursor != null) _cursor.MoveTo(r.Left + r.Width / 2, r.Top + r.Height / 2);
                    }
                    catch { }
                });
            }
            catch { }
        }

        internal static void Stop()
        {
            try { if (_marker != null) _marker.BeginInvoke((MethodInvoker)delegate { Application.ExitThread(); }); }
            catch { }
        }

        static GraphicsPath RoundRect(RectangleF r, float radius)
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

        // Shared base: layered, topmost, never activating, never focusable.
        abstract class GhostForm : Form
        {
            protected GhostForm(bool clickThrough)
            {
                ClickThrough = clickThrough;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.Magenta;
                TransparencyKey = Color.Magenta;
                SetStyle(ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.UserPaint, true);
                DoubleBuffered = true;
            }

            protected bool ClickThrough;
            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= 0x00000080    // WS_EX_TOOLWINDOW  - out of alt-tab
                               |  0x08000000    // WS_EX_NOACTIVATE  - never takes focus
                               |  0x00080000;   // WS_EX_LAYERED
                    if (ClickThrough) cp.ExStyle |= 0x00000020;  // WS_EX_TRANSPARENT
                    return cp;
                }
            }
        }

        // The ring and the banner. The one window here that accepts a click,
        // because Stop has to be pressable.
        sealed class ChromeForm : GhostForm
        {
            readonly Button _stop;
            const int Pad = 18, Gap = 14, BannerH = 52, RingW = 4;

            internal ChromeForm() : base(false)
            {
                _stop = new Button();
                _stop.Text = "Stop";
                _stop.FlatStyle = FlatStyle.Flat;
                _stop.BackColor = Paper;
                _stop.ForeColor = Ink;
                _stop.FlatAppearance.BorderSize = 0;
                _stop.FlatAppearance.MouseOverBackColor = Color.White;
                _stop.Font = new Font(UiFont, 10.5f, FontStyle.Bold);
                _stop.Size = new Size(96, 34);
                _stop.Cursor = Cursors.Hand;
                _stop.Click += delegate { _stopRequested = true; HideChrome(); };
                Controls.Add(_stop);
            }

            const string Message = "Claude is using your computer";

            // Sized to its contents, so a bigger button widens the banner rather
            // than squeezing the text onto one cramped line.
            Rectangle BannerRect()
            {
                int textW;
                using (Graphics g = CreateGraphics())
                using (Font f = new Font(UiFont, 11f, FontStyle.Regular))
                    textW = (int)Math.Ceiling(g.MeasureString(Message, f).Width);
                int w = Pad + textW + Gap + _stop.Width + Pad;
                return new Rectangle((Width - w) / 2, 10, w, BannerH);
            }

            // Called on every action, so it must do nothing once already up.
            // Re-setting bounds or invalidating each time is what made it strobe.
            internal void ShowChrome()
            {
                if (Visible) return;
                Bounds = SystemInformation.VirtualScreen;
                Rectangle b = BannerRect();
                _stop.Location = new Point(b.Right - Pad - _stop.Width, b.Y + (b.Height - _stop.Height) / 2);
                Show();
                TopMost = true;
            }

            internal void HideChrome() { if (Visible) Hide(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                using (Pen pen = new Pen(Claude, RingW))
                {
                    pen.Alignment = PenAlignment.Inset;
                    g.DrawRectangle(pen, new Rectangle(0, 0, Width - 1, Height - 1));
                }

                Rectangle b = BannerRect();
                using (SolidBrush bg = new SolidBrush(Claude))
                using (GraphicsPath path = RoundRect(b, 10f))
                    g.FillPath(bg, path);

                using (Font f = new Font(UiFont, 11f, FontStyle.Regular))
                using (SolidBrush fg = new SolidBrush(Paper))
                using (StringFormat sf = new StringFormat())
                {
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    g.DrawString(Message, f, fg,
                        new RectangleF(b.X + Pad, b.Y, b.Width - Pad - Gap - _stop.Width, b.Height), sf);
                }
            }
        }

        // Claude's own pointer, next to yours. It does not chase the mouse - it
        // sits on whatever Axon last touched, which is the useful thing to show.
        sealed class CursorForm : GhostForm
        {
            readonly System.Windows.Forms.Timer _hover;
            bool _showLabel;
            const int ArrowW = 22, ArrowH = 32, LabelGap = 6;

            internal CursorForm() : base(true)
            {
                Size = new Size(200, 60);
                // The label appears when your real pointer comes near Claude's.
                // Polling beats hit-testing here: this window stays fully
                // click-through, so it can never swallow a click meant for an app.
                _hover = new System.Windows.Forms.Timer();
                _hover.Interval = 120;
                _hover.Tick += delegate { UpdateHover(); };
            }

            internal void MoveTo(int x, int y)
            {
                Location = new Point(x, y);
                if (!Visible) { Show(); TopMost = true; _hover.Start(); }
                Invalidate();
            }

            internal void HideCursor()
            {
                _hover.Stop();
                _showLabel = false;
                if (Visible) Hide();
            }

            void UpdateHover()
            {
                POINT p;
                if (!GetCursorPos(out p)) return;
                Rectangle near = new Rectangle(Left - 24, Top - 24, ArrowW + 48, ArrowH + 48);
                bool want = near.Contains(p.X, p.Y);
                if (want != _showLabel) { _showLabel = want; Invalidate(); }
            }

            static GraphicsPath ArrowPath()
            {
                // A standard pointer silhouette, scaled to ArrowW x ArrowH.
                PointF[] pts = {
                    new PointF(0f,   0f),   new PointF(0f,   23.5f), new PointF(6.2f, 18f),
                    new PointF(9.7f, 26.3f),new PointF(13.8f,24.3f), new PointF(10.4f,16f),
                    new PointF(16.6f,16f),
                };
                GraphicsPath p = new GraphicsPath();
                p.AddPolygon(pts);
                return p;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                using (GraphicsPath arrow = ArrowPath())
                using (SolidBrush fill = new SolidBrush(Claude))
                using (Pen edge = new Pen(Paper, 1.6f))
                {
                    edge.LineJoin = LineJoin.Round;
                    // Outline first so the pointer stays legible on any background.
                    g.DrawPath(edge, arrow);
                    g.FillPath(fill, arrow);
                }

                if (!_showLabel) return;

                const string label = "Claude Cursor";
                using (Font f = new Font(UiFont, 9f, FontStyle.Regular))
                {
                    SizeF sz = g.MeasureString(label, f);
                    RectangleF tag = new RectangleF(ArrowW + LabelGap, ArrowH * 0.45f, sz.Width + 16, sz.Height + 8);
                    using (SolidBrush bg = new SolidBrush(Claude))
                    using (GraphicsPath path = RoundRect(tag, 5f))
                        g.FillPath(bg, path);
                    using (SolidBrush fg = new SolidBrush(Paper))
                        g.DrawString(label, f, fg, tag.X + 8, tag.Y + 4);
                }
            }
        }

        // The marker that flashes around each control as it is touched.
        sealed class TraceForm : GhostForm
        {
            readonly System.Windows.Forms.Timer _timer;
            string _label = "";
            Rectangle _target;
            const int TagH = 24;

            internal TraceForm() : base(true)
            {
                Opacity = 0.96;
                Bounds = new Rectangle(-10000, -10000, 10, 10);
                _timer = new System.Windows.Forms.Timer();
                _timer.Interval = 700;
                _timer.Tick += delegate { _timer.Stop(); Hide(); };
            }

            internal void Flash(Rectangle target, string label)
            {
                _label = label ?? "";
                _target = target;
                Bounds = new Rectangle(target.X - 4, target.Y - TagH - 2, target.Width + 8, target.Height + TagH + 6);
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
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                Rectangle box = new Rectangle(4, TagH + 2, _target.Width, _target.Height);
                using (Pen pen = new Pen(Claude, 2f))
                {
                    pen.Alignment = PenAlignment.Inset;
                    g.DrawRectangle(pen, box);
                }

                if (_label.Length == 0) return;
                using (Font f = new Font(UiFont, 8.5f, FontStyle.Regular))
                {
                    SizeF sz = g.MeasureString(_label, f);
                    RectangleF tag = new RectangleF(4, 2, sz.Width + 14, TagH - 2);
                    using (SolidBrush bg = new SolidBrush(Claude))
                    using (GraphicsPath path = RoundRect(tag, 4f))
                        g.FillPath(bg, path);
                    using (SolidBrush fg = new SolidBrush(Paper))
                        g.DrawString(_label, f, fg, tag.X + 7, tag.Y + 3);
                }
            }
        }
    }
}
