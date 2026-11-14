using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MirrorDock
{
    // The window that shows the phone screen and turns mouse/keyboard into phone input.
    // The button bar is part of this window (docked on the right), and resizing keeps the phone's shape.
    class MirrorWindow : Form
    {
        readonly MirrorSession session;
        readonly MainForm main;
        readonly object frameLock = new object();
        Bitmap frame;
        int paintPending;
        bool sized, mouseDown, pinching;
        Rectangle shown;
        int lastFw, lastFh;
        volatile int clientW, clientH, nativeW, nativeH;   // read by the video thread
        ToolbarPanel bar;
        int frames; DateTime fpsStart = DateTime.Now; string fpsText = "";
        string overlay; DateTime overlayUntil;

        const int KC_BACK = 4, KC_ENTER = 66, KC_DEL = 67, KC_FWD_DEL = 112, KC_TAB = 61,
            KC_UP = 19, KC_DOWN = 20, KC_LEFT = 21, KC_RIGHT = 22, KC_MOVE_HOME = 122, KC_MOVE_END = 123,
            KC_PAGE_UP = 92, KC_PAGE_DOWN = 93, META_CTRL = 0x1000 | 0x2000, META_SHIFT = 0x1 | 0x40;

        public MirrorWindow(MainForm main, MirrorSession s)
        {
            this.main = main;
            session = s;
            Text = s.Name + "  -  MirrorDock";
            Icon = Theme.AppIcon();
            BackColor = Color.Black;
            DoubleBuffered = true;
            KeyPreview = true;
            AllowDrop = true;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

            Rectangle saved = main.SavedWindow(s.Serial + (s.IsExtraScreen ? "#extra" : ""));
            if (saved.Width > 100 && Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(saved)))
            {
                Bounds = saved;
                sized = true;
            }
            else
            {
                Size = new Size(420 + (s.Opts.Toolbar ? ToolbarPanel.BarWidth : 0), 820);
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
            }
            if (s.Opts.OnTop) TopMost = true;
            if (s.Opts.Borderless) FormBorderStyle = FormBorderStyle.None;
            HandleCreated += (o, e) => Native.DarkTitleBar(Handle);
            Shown += (o, e) => { if (s.Opts.Fullscreen) ToggleFullscreen(); };
            Activated += (o, e) => main.ActiveSession = s;   // game controllers follow the mirror you use
            Deactivate += (o, e) => ReleaseKeys();
            UpdateArea();
        }

        int BarW { get { return bar != null && bar.Visible ? bar.Width : 0; } }
        Rectangle Area { get { return new Rectangle(0, 0, Math.Max(1, ClientSize.Width - BarW), ClientSize.Height); } }
        void UpdateArea() { Rectangle a = Area; clientW = a.Width; clientH = a.Height; }

        public void StartReading()
        {
            if (session.Opts.Toolbar)
            {
                bar = new ToolbarPanel(main, this, session);
                Controls.Add(bar);
                bar.Visible = !fullscreen;
                UpdateArea();
            }
            var t = new Thread(() => session.ReadVideo(Present, TargetFor)) { IsBackground = true, Name = "video " + session.Serial };
            t.Start();
        }

        // Video thread: the size this frame will be shown at (fits the picture area, keeps the phone's shape).
        Size TargetFor(int fw, int fh)
        {
            nativeW = fw; nativeH = fh;
            int cw = clientW, ch = clientH;
            if (cw <= 0 || ch <= 0 || fw <= 0 || fh <= 0) return new Size(Math.Max(1, fw), Math.Max(1, fh));
            double s = Math.Min((double)cw / fw, (double)ch / fh);
            return new Size(Math.Max(1, (int)(fw * s)), Math.Max(1, (int)(fh * s)));
        }

        // Video thread: swap in a new frame, give back the old one for reuse.
        Bitmap Present(Bitmap bmp)
        {
            Bitmap old;
            lock (frameLock) { old = frame; frame = bmp; }
            frames++;
            if (Interlocked.Exchange(ref paintPending, 1) == 0)
            {
                try { BeginInvoke((Action)(() => { paintPending = 0; FitIfNeeded(); Invalidate(Area); })); } catch { }
            }
            return old;
        }

        public void ShowOverlay(string text)
        {
            overlay = text;
            overlayUntil = DateTime.Now.AddSeconds(2.5);
            Invalidate(Area);
            var t = new System.Windows.Forms.Timer { Interval = 2600 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Invalidate(Area); };
            t.Start();
        }

        void FitIfNeeded()
        {
            int fw = nativeW, fh = nativeH;
            if (fw <= 0) return;
            bool first = lastFw == 0;
            bool rotated = (!first && (fw > fh) != (lastFw > lastFh)) || (first && sized);  // a saved size still gets the phone's shape
            lastFw = fw; lastFh = fh;
            if (WindowState != FormWindowState.Normal || fullscreen || (sized && !rotated)) return;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            double scale;
            Rectangle a = Area;
            if (sized && rotated)
            {   // keep roughly the same size, just turn it
                scale = Math.Min((double)Math.Max(a.Width, a.Height) / Math.Max(fw, fh), Math.Min(wa.Height * 0.9 / fh, (wa.Width * 0.9 - BarW) / fw));
            }
            else scale = Math.Min(Math.Min(wa.Height * 0.88 / fh, (wa.Width * 0.85 - BarW) / fw), 1.5);
            sized = true;
            Size inner = new Size((int)(fw * scale) + BarW, (int)(fh * scale));
            Size chrome = Size - ClientSize;
            Size = inner + chrome;
            Left = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
            Top = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
        }

        // Keep the phone's shape while the window is being resized (no black bars).
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0214 && !fullscreen && nativeW > 0 && FormBorderStyle != FormBorderStyle.None)   // WM_SIZING
            {
                var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                Size chrome = Size - ClientSize;
                double aspect = (double)nativeW / nativeH;
                int edge = m.WParam.ToInt32();
                int cw = r.Right - r.Left - chrome.Width - BarW, ch = r.Bottom - r.Top - chrome.Height;
                if (edge == 1 || edge == 2)          // left/right edge: width leads
                    r.Bottom = r.Top + chrome.Height + Math.Max(80, (int)Math.Round(cw / aspect));
                else if (edge == 3 || edge == 6)     // top/bottom edge: height leads
                    r.Right = r.Left + chrome.Width + BarW + Math.Max(60, (int)Math.Round(ch * aspect));
                else                                  // corners: follow the bigger change
                {
                    int byW = (int)Math.Round(cw / aspect), byH = (int)Math.Round(ch * aspect);
                    if (Math.Abs(byW - ch) > Math.Abs(byH - cw))
                    {
                        if (edge == 4 || edge == 5) r.Top = r.Bottom - chrome.Height - Math.Max(80, byW);   // top corners
                        else r.Bottom = r.Top + chrome.Height + Math.Max(80, byW);
                    }
                    else
                    {
                        if (edge == 4 || edge == 7) r.Left = r.Right - chrome.Width - BarW - Math.Max(60, byH);   // left corners
                        else r.Right = r.Left + chrome.Width + BarW + Math.Max(60, byH);
                    }
                }
                Marshal.StructureToPtr(r, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle a = Area;
            lock (frameLock)
            {
                if (frame == null)
                {
                    using (var black = new SolidBrush(Color.Black)) g.FillRectangle(black, a);
                    TextRenderer.DrawText(g, "Connecting to " + session.Name + "...", Theme.Header, a, Color.FromArgb(150, 158, 172),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    return;
                }
                double scale = Math.Min((double)a.Width / frame.Width, (double)a.Height / frame.Height);
                int w = (int)(frame.Width * scale), h = (int)(frame.Height * scale);
                if (Math.Abs(w - frame.Width) <= 1 && Math.Abs(h - frame.Height) <= 1) { w = frame.Width; h = frame.Height; }   // rounding only
                shown = new Rectangle((a.Width - w) / 2, (a.Height - h) / 2, w, h);
                using (var black = new SolidBrush(Color.Black))
                {
                    g.FillRectangle(black, 0, 0, a.Width, shown.Top);
                    g.FillRectangle(black, 0, shown.Bottom, a.Width, a.Height - shown.Bottom);
                    g.FillRectangle(black, 0, 0, shown.Left, a.Height);
                    g.FillRectangle(black, shown.Right, 0, a.Width - shown.Right, a.Height);
                }
                g.CompositingMode = CompositingMode.SourceCopy;
                if (frame.Width == w && frame.Height == h) g.DrawImageUnscaled(frame, shown.X, shown.Y);   // already sized: a plain copy
                else
                {   // only for a moment while resizing, until the next frame arrives at the new size
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(frame, shown);
                }
                g.CompositingMode = CompositingMode.SourceOver;
            }
            if (overlay != null && DateTime.Now < overlayUntil)
            {
                Size sz = TextRenderer.MeasureText(overlay, Theme.UiBold);
                var r = new Rectangle((a.Width - sz.Width) / 2 - 14, a.Height - sz.Height - 46, sz.Width + 28, sz.Height + 16);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Rounded(r, 10))
                using (var br = new SolidBrush(Color.FromArgb(225, 31, 35, 43))) g.FillPath(br, path);
                TextRenderer.DrawText(g, overlay, Theme.UiBold, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            if (session.Recording)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var br = new SolidBrush(Color.FromArgb(229, 72, 77))) g.FillEllipse(br, 12, 12, 12, 12);
            }
            if ((DateTime.Now - fpsStart).TotalSeconds >= 1)
            {
                fpsText = frames + " fps";
                frames = 0; fpsStart = DateTime.Now;
            }
        }

        public string Fps { get { return fpsText; } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateArea();
            Invalidate(Area);
        }

        // ------------------------------------------------------------ mouse -> touch
        bool ToDevice(Point p, out int x, out int y)
        {
            x = y = 0;
            if (shown.Width <= 0 || session.DevW <= 0) return false;
            double fx = (p.X - shown.Left) / (double)shown.Width, fy = (p.Y - shown.Top) / (double)shown.Height;
            fx = Math.Max(0, Math.Min(0.9999, fx)); fy = Math.Max(0, Math.Min(0.9999, fy));
            x = (int)(fx * session.DevW); y = (int)(fy * session.DevH);
            return p.X >= shown.Left && p.X < shown.Right && p.Y >= shown.Top && p.Y < shown.Bottom;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int x, y;
            if (e.Button == MouseButtons.Left && ToDevice(e.Location, out x, out y))
            {
                mouseDown = true; Capture = true;
                pinching = (ModifierKeys & Keys.Control) != 0;
                if (pinching) session.Pinch(0, x, y); else session.Touch(0, x, y);
            }
            else if (e.Button == MouseButtons.Right) session.Key(KC_BACK);
            else if (e.Button == MouseButtons.Middle) session.Key(3);
            else if (e.Button == MouseButtons.XButton1) session.Key(187);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int x, y;
            if (!mouseDown) return;
            ToDevice(e.Location, out x, out y);
            if (pinching) session.Pinch(2, x, y); else session.Touch(2, x, y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int x, y;
            if (mouseDown && e.Button == MouseButtons.Left)
            {
                mouseDown = false; Capture = false;
                ToDevice(e.Location, out x, out y);
                if (pinching) session.Pinch(1, x, y); else session.Touch(1, x, y);
                pinching = false;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int x, y;
            if (ToDevice(e.Location, out x, out y))
            {
                bool shift = (ModifierKeys & Keys.Shift) != 0;
                float amount = e.Delta / 120f;
                session.Scroll(x, y, shift ? -amount : 0, shift ? 0 : amount);
            }
        }

        // ------------------------------------------------------------ drag & drop: APK = install, anything else = copy to Downloads
        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) main.SendFiles(session, files);
        }

        // ------------------------------------------------------------ keyboard
        // Typing mode (default): text and a few special keys. Real keyboard mode: every key goes to the phone's
        // virtual keyboard with real press/release, which games and keyboard shortcuts need.
        readonly List<byte> realKeys = new List<byte>();
        byte realMods;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0, alt = (keyData & Keys.Alt) != 0;
            if (keyData == Keys.F11) { ToggleFullscreen(); return true; }
            if (keyData == Keys.F12) { main.ToggleRecording(session); return true; }
            if (session.RealKeyboard && (msg.Msg == 0x100 || msg.Msg == 0x104)) { RealKey((int)k, true, ((long)msg.LParam & 0x01000000) != 0); return true; }
            if (ctrl && k == Keys.V) { if (Clipboard.ContainsText()) session.SetPhoneClipboard(Clipboard.GetText(), true); return true; }
            int code = SpecialKey(k);
            if (ctrl && k >= Keys.A && k <= Keys.Z) code = 29 + (k - Keys.A);
            if (code != 0 && !alt)
            {
                int meta = (ctrl ? META_CTRL : 0) | (shift ? META_SHIFT : 0);
                session.KeyEvent(true, code, meta);
                session.KeyEvent(false, code, meta);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (session.RealKeyboard) { RealKey((int)e.KeyCode, false, false); e.Handled = true; }
        }

        void RealKey(int vk, bool down, bool extended)
        {
            if (vk == 0x7A || vk == 0x7B) return;   // F11 / F12 stay MirrorDock shortcuts
            byte mod = InputCapture.ModifierBit(vk);
            if (mod != 0) { if (down) realMods |= mod; else realMods &= (byte)~mod; }
            else
            {
                byte usage = InputCapture.Usage(vk, extended);
                if (usage == 0) return;
                if (down) { if (!realKeys.Contains(usage)) realKeys.Add(usage); }
                else realKeys.Remove(usage);
            }
            session.InputReport(MirrorSession.KeyboardSlot, InputCapture.KeyboardReport(realMods, realKeys));
        }

        void ReleaseKeys()
        {
            if (realKeys.Count == 0 && realMods == 0) return;
            realKeys.Clear(); realMods = 0;
            if (session.Running) session.InputReport(MirrorSession.KeyboardSlot, new byte[8]);
        }

        static int SpecialKey(Keys k)
        {
            switch (k)
            {
                case Keys.Enter: return KC_ENTER;
                case Keys.Back: return KC_DEL;
                case Keys.Delete: return KC_FWD_DEL;
                case Keys.Tab: return KC_TAB;
                case Keys.Escape: return KC_BACK;
                case Keys.Up: return KC_UP;
                case Keys.Down: return KC_DOWN;
                case Keys.Left: return KC_LEFT;
                case Keys.Right: return KC_RIGHT;
                case Keys.Home: return KC_MOVE_HOME;
                case Keys.End: return KC_MOVE_END;
                case Keys.PageUp: return KC_PAGE_UP;
                case Keys.PageDown: return KC_PAGE_DOWN;
                case Keys.VolumeUp: return 24;
                case Keys.VolumeDown: return 25;
                default: return 0;
            }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (!session.RealKeyboard && e.KeyChar >= ' ' && !char.IsControl(e.KeyChar)) session.Text(e.KeyChar.ToString());
            e.Handled = true;
        }

        bool fullscreen;
        FormBorderStyle oldBorder;
        Rectangle oldBounds;
        public void ToggleFullscreen()
        {
            fullscreen = !fullscreen;
            if (fullscreen)
            {
                oldBorder = FormBorderStyle; oldBounds = Bounds;
                FormBorderStyle = FormBorderStyle.None;
                if (bar != null) bar.Visible = false;
                Bounds = Screen.FromControl(this).Bounds;
            }
            else
            {
                FormBorderStyle = oldBorder;
                if (bar != null) bar.Visible = true;
                Bounds = oldBounds;
            }
            UpdateArea();
            Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (WindowState == FormWindowState.Normal && !fullscreen) main.SaveWindow(session.Serial + (session.IsExtraScreen ? "#extra" : ""), Bounds);
            ReleaseKeys();
            session.Stop();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) lock (frameLock) { if (frame != null) { frame.Dispose(); frame = null; } }
            base.Dispose(disposing);
        }
    }
}
