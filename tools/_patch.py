import io
p = 'server/native/Overlay.cs'
s = io.open(p, encoding='utf-8').read()

def sub(old, new, label):
    global s
    if old not in s:
        raise SystemExit('MISS ' + label)
    s = s.replace(old, new, 1)

sub("        static IntPtr _hwnd;\n        internal static IntPtr Handle { get { return _hwnd; } }",
    "        static IntPtr _hwnd;\n"
    "        static IntPtr _chromeHwnd;\n"
    "        internal static IntPtr Handle { get { return _hwnd; } }\n"
    "        internal static IntPtr ChromeHandle { get { return _chromeHwnd; } }", 'handles')

sub("        static Thread _thread;\n        static TraceForm _form;",
    "        static Thread _thread;\n"
    "        static TraceForm _form;\n"
    "        static ChromeForm _chrome;\n"
    "        static System.Windows.Forms.Timer _idleTimer;\n"
    "        static volatile bool _stopRequested;\n"
    "\n"
    "        // Claude's own colour, so the ring reads as Claude working rather\n"
    "        // than as some anonymous program having taken the screen.\n"
    "        static readonly Color ClaudeColour = Color.FromArgb(217, 119, 87);\n"
    "\n"
    "        internal static bool StopRequested { get { return _stopRequested; } }\n"
    "\n"
    "        // Read once and cleared, so one press stops one run.\n"
    "        internal static bool ConsumeStop()\n"
    "        {\n"
    "            if (!_stopRequested) return false;\n"
    "            _stopRequested = false;\n"
    "            return true;\n"
    "        }\n"
    "\n"
    "        // Called by every acting op. Raises the ring and banner, and keeps\n"
    "        // them up until Axon has been quiet for a couple of seconds.\n"
    "        internal static void MarkActive()\n"
    "        {\n"
    "            if (!Enabled || _chrome == null) return;\n"
    "            try\n"
    "            {\n"
    "                _chrome.BeginInvoke((MethodInvoker)delegate\n"
    "                {\n"
    "                    try\n"
    "                    {\n"
    "                        _chrome.ShowChrome();\n"
    "                        if (_idleTimer != null) { _idleTimer.Stop(); _idleTimer.Start(); }\n"
    "                    }\n"
    "                    catch { }\n"
    "                });\n"
    "            }\n"
    "            catch { }\n"
    "        }", 'fields')

sub("                try { SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE); } catch { }\n"
    "                _ready = true;\n"
    "                Application.Run(_form);",
    "                try { SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE); } catch { }\n"
    "\n"
    "                _chrome = new ChromeForm(ClaudeColour);\n"
    "                _chrome.CreateControl();\n"
    "                _chromeHwnd = _chrome.Handle;\n"
    "                try { SetWindowDisplayAffinity(_chromeHwnd, WDA_EXCLUDEFROMCAPTURE); } catch { }\n"
    "\n"
    "                _idleTimer = new System.Windows.Forms.Timer();\n"
    "                _idleTimer.Interval = 2200;\n"
    "                _idleTimer.Tick += delegate { _idleTimer.Stop(); try { _chrome.HideChrome(); } catch { } };\n"
    "\n"
    "                _ready = true;\n"
    "                Application.Run(_form);", 'run')

CHROME = '''        // The ring is one layered window spanning the virtual screen, hollow in
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

                _stop = new Button();
                _stop.Text = "Stop";
                _stop.FlatStyle = FlatStyle.Flat;
                _stop.BackColor = Color.White;
                _stop.ForeColor = Color.FromArgb(30, 30, 30);
                _stop.FlatAppearance.BorderSize = 0;
                _stop.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
                _stop.Size = new Size(58, 24);
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

            static Size BannerSize() { return new Size(300, 40); }

            internal void ShowChrome()
            {
                Bounds = SystemInformation.VirtualScreen;
                Size sz = BannerSize();
                int x = (Width - sz.Width) / 2;
                _stop.Location = new Point(x + sz.Width - _stop.Width - 10, 8 + (sz.Height - _stop.Height) / 2);
                if (!Visible) Show();
                TopMost = true;
                Invalidate();
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

                using (Font f = new Font("Segoe UI", 9f, FontStyle.Regular))
                using (SolidBrush fg = new SolidBrush(Color.White))
                    g.DrawString("Claude is using your computer", f, fg, banner.X + 14, banner.Y + 11);
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

        sealed class TraceForm : Form'''

sub("        sealed class TraceForm : Form", CHROME, 'chrome')

io.open(p, 'w', encoding='utf-8').write(s)
print('ring + banner + stop written')
