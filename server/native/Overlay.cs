// Overlay - everything Axon puts on screen.
//
// Four windows, one colour:
//   - a ring around the whole desktop while Axon is working (click-through)
//   - a banner reading "Claude is using your computer", with Stop
//   - a Claude-coloured cursor showing where Axon is, alongside yours
//   - a marker flashing around each control as it is touched
//
// The ring and the banner are separate windows on purpose. A painted ring on a
// clickable window would put a live 4px band around every screen edge, eating
// clicks meant for the taskbar, corner hot zones and window resize handles. The
// ring is therefore click-through, and the banner - the one thing that must
// accept a click - is a small window of its own.
//
// Axon never calls SetCursor, ShowCursor, or SetSystemCursor. Your pointer is
// never touched, so it cannot be left in a bad state - which is the standing
// Codex bug on Windows (openai/codex#25200), where users are asking for exactly
// this: an overlay cursor instead of hijacking the system one.

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

        // Anthropic's own palette. One accent, everywhere - the ring, the banner,
        // the cursor and the marker all draw from it, so Axon reads as one thing
        // rather than as a colour scheme.
        internal static readonly Color Claude = Color.FromArgb(217, 119, 87);   // #D97757
        internal static readonly Color Ink = Color.FromArgb(20, 20, 19);        // #141413
        internal static readonly Color Paper = Color.FromArgb(250, 249, 245);   // #FAF9F5

        static Thread _thread;
        static TraceForm _marker;
        static RingForm _ring;
        static BannerForm _banner;
        static CursorForm _cursor;
        static System.Windows.Forms.Timer _idleTimer;
        static volatile bool _stopRequested;
        static volatile bool _enabled = true;
        static volatile bool _ready;
        static IntPtr _markerHwnd, _ringHwnd, _bannerHwnd, _cursorHwnd;
        static string _fontName;

        internal static bool Enabled { get { return _enabled && _ready; } }
        internal static bool StopRequested { get { return _stopRequested; } }

        // Any window Axon itself put on screen. Keeps them out of its own
        // listings, and stops its own marker counting as something covering a
        // click target.
        internal static bool IsOwnWindow(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            return h == _markerHwnd || h == _ringHwnd || h == _bannerHwnd || h == _cursorHwnd;
        }

        // Read once and cleared, so one press stops one run.
        internal static bool ConsumeStop()
        {
            if (!_stopRequested) return false;
            _stopRequested = false;
            return true;
        }

        internal static void RequestStop() { _stopRequested = true; }

        // OpenAI Sans and Söhne are licensed faces that cannot ship inside a
        // plugin, so this resolves the closest grotesque actually installed
        // rather than pretending to be either.
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

                _ring = new RingForm();
                _ring.CreateControl();
                _ringHwnd = _ring.Handle;
                Exclude(_ringHwnd);

                _banner = new BannerForm();
                _banner.CreateControl();
                _bannerHwnd = _banner.Handle;
                Exclude(_bannerHwnd);

                _cursor = new CursorForm();
                _cursor.CreateControl();
                _cursorHwnd = _cursor.Handle;
                Exclude(_cursorHwnd);

                _idleTimer = new System.Windows.Forms.Timer();
                _idleTimer.Interval = 2200;
                _idleTimer.Tick += delegate
                {
                    _idleTimer.Stop();
                    try { _ring.HideRing(); } catch { }
                    try { _banner.HideBanner(); } catch { }
                    try { _cursor.HideCursor(); } catch { }
                };

                _ready = true;
                Application.Run(_marker);
            }
            catch { _enabled = false; }
            finally
            {
                _ready = false;
                _markerHwnd = _ringHwnd = _bannerHwnd = _cursorHwnd = IntPtr.Zero;
            }
        }

        static void Exclude(IntPtr h)
        {
            try { SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE); } catch { }
        }

        // Called by every acting op. Raises the chrome and keeps it up until Axon
        // has been quiet for a couple of seconds.
        internal static void MarkActive()
        {
            if (!Enabled || _banner == null) return;
            try
            {
                _banner.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        _ring.ShowRing();
                        _banner.ShowBanner();
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
            if (d > r.Height) d = r.Height;
            if (d > r.Width) d = r.Width;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Color Shade(Color c, float amount)
        {
            return Color.FromArgb(c.A,
                (int)Math.Max(0, Math.Min(255, c.R * (1 - amount))),
                (int)Math.Max(0, Math.Min(255, c.G * (1 - amount))),
                (int)Math.Max(0, Math.Min(255, c.B * (1 - amount))));
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

        // Just the ring. Click-through, so the screen edges stay yours.
        sealed class RingForm : GhostForm
        {
            const int RingW = 4;

            internal RingForm() : base(true) { Bounds = SystemInformation.VirtualScreen; }

            internal void ShowRing()
            {
                if (Visible) return;
                Bounds = SystemInformation.VirtualScreen;
                Show();
                TopMost = true;
            }

            internal void HideRing() { if (Visible) Hide(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                using (Pen pen = new Pen(Claude, RingW))
                {
                    pen.Alignment = PenAlignment.Inset;
                    e.Graphics.DrawRectangle(pen, new Rectangle(0, 0, Width - 1, Height - 1));
                }
            }
        }

        // The banner. Small, and the only surface Axon puts on screen that
        // accepts a click - because Stop has to be pressable.
        //
        // Stop is an emergency brake, not a dialog button: it gets the highest
        // contrast on screen and no ambiguity. Everything else here stays quiet.
        // It is drawn rather than being a Button control, so it is a pill inside
        // a pill instead of a square system widget bolted onto a rounded banner.
        sealed class BannerForm : GhostForm
        {
            const string Message = "Claude is using your computer";
            const string Action = "Stop";
            const int H = 54, PadL = 20, PadR = 8, Gap = 16, StopH = 38, StopPadX = 20;
            const int DotSize = 8, DotGap = 12;

            Rectangle _stopRect;
            bool _hover, _pressed;
            readonly System.Windows.Forms.Timer _pulse;
            float _phase;

            internal BannerForm() : base(false)
            {
                Cursor = Cursors.Default;
                // One piece of motion, and it carries meaning: the dot breathes
                // while Axon is acting, and the whole banner leaves when it stops.
                _pulse = new System.Windows.Forms.Timer();
                _pulse.Interval = 66;
                _pulse.Tick += delegate
                {
                    _phase += 0.06f;
                    if (_phase > 1f) _phase -= 1f;
                    Invalidate(DotRect());
                };
            }

            Rectangle DotRect()
            {
                return new Rectangle(PadL, (H - DotSize) / 2 - 1, DotSize + 6, DotSize + 6);
            }

            int MeasureWidth()
            {
                int textW, actionW;
                using (Graphics g = CreateGraphics())
                using (Font f = new Font(UiFont, 11f, FontStyle.Regular))
                using (Font a = new Font(UiFont, 10.5f, FontStyle.Bold))
                {
                    textW = (int)Math.Ceiling(g.MeasureString(Message, f).Width);
                    actionW = (int)Math.Ceiling(g.MeasureString(Action, a).Width);
                }
                return PadL + DotSize + DotGap + textW + Gap + (actionW + StopPadX * 2) + PadR;
            }

            internal void ShowBanner()
            {
                if (Visible) { return; }
                int w = MeasureWidth();
                Rectangle vs = SystemInformation.VirtualScreen;
                Bounds = new Rectangle(vs.X + (vs.Width - w) / 2, vs.Y + 14, w, H);
                LayoutStop();
                Show();
                TopMost = true;
                _pulse.Start();
            }

            void LayoutStop()
            {
                int actionW;
                using (Graphics g = CreateGraphics())
                using (Font a = new Font(UiFont, 10.5f, FontStyle.Bold))
                    actionW = (int)Math.Ceiling(g.MeasureString(Action, a).Width);
                int w = actionW + StopPadX * 2;
                _stopRect = new Rectangle(Width - PadR - w, (H - StopH) / 2, w, StopH);
            }

            internal void HideBanner()
            {
                _pulse.Stop();
                _hover = _pressed = false;
                if (Visible) Hide();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                bool over = _stopRect.Contains(e.Location);
                if (over != _hover)
                {
                    _hover = over;
                    Cursor = over ? Cursors.Hand : Cursors.Default;
                    Invalidate(Inflate(_stopRect));
                }
                base.OnMouseMove(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                if (_hover || _pressed) { _hover = _pressed = false; Cursor = Cursors.Default; Invalidate(Inflate(_stopRect)); }
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (_stopRect.Contains(e.Location)) { _pressed = true; Invalidate(Inflate(_stopRect)); }
                base.OnMouseDown(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                bool fire = _pressed && _stopRect.Contains(e.Location);
                _pressed = false;
                Invalidate(Inflate(_stopRect));
                if (fire) { RequestStop(); HideBanner(); try { _ring.HideRing(); _cursor.HideCursor(); } catch { } }
                base.OnMouseUp(e);
            }

            static Rectangle Inflate(Rectangle r) { r.Inflate(3, 3); return r; }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                RectangleF body = new RectangleF(0, 0, Width, H);
                using (SolidBrush bg = new SolidBrush(Claude))
                using (GraphicsPath path = RoundRect(body, H / 2f))
                    g.FillPath(bg, path);

                // Status dot: breathes while acting. This is the only motion, and
                // it says something true - Axon is still working.
                float t = (float)((Math.Sin(_phase * Math.PI * 2) + 1) / 2);
                int alpha = (int)(110 + 145 * t);
                float grow = 1f + 0.25f * t;
                float ds = DotSize * grow;
                using (SolidBrush dot = new SolidBrush(Color.FromArgb(alpha, Paper)))
                    g.FillEllipse(dot, PadL + (DotSize - ds) / 2f, (H - ds) / 2f, ds, ds);

                using (Font f = new Font(UiFont, 11f, FontStyle.Regular))
                using (SolidBrush fg = new SolidBrush(Paper))
                using (StringFormat sf = new StringFormat())
                {
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    float x = PadL + DotSize + DotGap;
                    g.DrawString(Message, f, fg, new RectangleF(x, 0, _stopRect.Left - Gap - x, H), sf);
                }

                // The brake. A pill inside a pill - concentric, so it belongs to
                // the banner instead of sitting on top of it.
                Color fill = _pressed ? Shade(Paper, 0.12f) : (_hover ? Color.White : Paper);
                using (SolidBrush sb = new SolidBrush(fill))
                using (GraphicsPath sp = RoundRect(_stopRect, _stopRect.Height / 2f))
                    g.FillPath(sb, sp);

                using (Font a = new Font(UiFont, 10.5f, FontStyle.Bold))
                using (SolidBrush at = new SolidBrush(Ink))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(Action, a, at, _stopRect, sf);
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
                Size = new Size(220, 64);
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
                PointF[] pts = {
                    new PointF(0f,    0f),    new PointF(0f,    23.5f), new PointF(6.2f,  18f),
                    new PointF(9.7f,  26.3f), new PointF(13.8f, 24.3f), new PointF(10.4f, 16f),
                    new PointF(16.6f, 16f),
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
                    // Outline first, so the pointer stays legible on any background.
                    g.DrawPath(edge, arrow);
                    g.FillPath(fill, arrow);
                }

                if (!_showLabel) return;

                const string label = "Claude Cursor";
                using (Font f = new Font(UiFont, 9f, FontStyle.Regular))
                {
                    SizeF sz = g.MeasureString(label, f);
                    RectangleF tag = new RectangleF(ArrowW + LabelGap, ArrowH * 0.45f, sz.Width + 18, sz.Height + 10);
                    using (SolidBrush bg = new SolidBrush(Claude))
                    using (GraphicsPath path = RoundRect(tag, tag.Height / 2f))
                        g.FillPath(bg, path);
                    using (SolidBrush fg = new SolidBrush(Paper))
                        g.DrawString(label, f, fg, tag.X + 9, tag.Y + 5);
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
                    RectangleF tag = new RectangleF(4, 2, sz.Width + 16, TagH - 2);
                    using (SolidBrush bg = new SolidBrush(Claude))
                    using (GraphicsPath path = RoundRect(tag, (TagH - 2) / 2f))
                        g.FillPath(bg, path);
                    using (SolidBrush fg = new SolidBrush(Paper))
                        g.DrawString(_label, f, fg, tag.X + 8, tag.Y + 3);
                }
            }
        }
    }
}
