using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace MirrorDock
{
    // The window that shows the phone screen and turns mouse/keyboard into phone input.
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

            Rectangle saved = main.SavedWindow(s.Serial);
            if (saved.Width > 100 && Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(saved)))
            {
                Bounds = saved;
                sized = true;
            }
            else
            {
                Size = new Size(420, 820);
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
            }
            if (s.Opts.OnTop) TopMost = true;
            if (s.Opts.Borderless) FormBorderStyle = FormBorderStyle.None;
            HandleCreated += (o, e) => Native.DarkTitleBar(Handle);
            Shown += (o, e) => { if (s.Opts.Fullscreen) ToggleFullscreen(); };
            Activated += (o, e) => main.ActiveSession = s;   // game controllers follow the mirror you use
        }

        public void StartReading()
        {
            var t = new Thread(() => session.ReadVideo(Present)) { IsBackground = true, Name = "video " + session.Serial };
            t.Start();
        }

        // Video thread: swap in a new frame, give back the old one for reuse.
        Bitmap Present(Bitmap bmp)
        {
            Bitmap old;
            lock (frameLock) { old = frame; frame = bmp; }
            frames++;
            if (Interlocked.Exchange(ref paintPending, 1) == 0)
            {
                try { BeginInvoke((Action)(() => { paintPending = 0; FitIfNeeded(); Invalidate(); })); } catch { }
            }
            return old;
        }

        public void ShowOverlay(string text)
        {
            overlay = text;
            overlayUntil = DateTime.Now.AddSeconds(2.5);
            Invalidate();
            var t = new System.Windows.Forms.Timer { Interval = 2600 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Invalidate(); };
            t.Start();
        }

        void FitIfNeeded()
        {
            int fw, fh;
            lock (frameLock) { if (frame == null) return; fw = frame.Width; fh = frame.Height; }
            bool first = lastFw == 0;
            bool rotated = (!first && (fw > fh) != (lastFw > lastFh)) || (first && sized);  // a saved size still gets the phone's shape
            lastFw = fw; lastFh = fh;
            if (WindowState != FormWindowState.Normal || fullscreen || (sized && !rotated)) return;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            double scale;
            if (sized && rotated)
            {   // keep roughly the same size, just turn it
                scale = Math.Min((double)Math.Max(ClientSize.Width, ClientSize.Height) / Math.Max(fw, fh), Math.Min(wa.Height * 0.9 / fh, wa.Width * 0.9 / fw));
            }
            else scale = Math.Min(Math.Min(wa.Height * 0.88 / fh, wa.Width * 0.85 / fw), 1.5);
            sized = true;
            Size inner = new Size((int)(fw * scale), (int)(fh * scale));
            Size chrome = Size - ClientSize;
            Size = inner + chrome;
            Left = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
            Top = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            lock (frameLock)
            {
                if (frame == null)
                {
                    g.Clear(Color.Black);
                    TextRenderer.DrawText(g, "Connecting to " + session.Name + "...", Theme.Header, ClientRectangle, Theme.Sub,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    return;
                }
                double scale = Math.Min((double)ClientSize.Width / frame.Width, (double)ClientSize.Height / frame.Height);
                int w = (int)(frame.Width * scale), h = (int)(frame.Height * scale);
                shown = new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
                using (var black = new SolidBrush(Color.Black))
                {
                    g.FillRectangle(black, 0, 0, ClientSize.Width, shown.Top);
                    g.FillRectangle(black, 0, shown.Bottom, ClientSize.Width, ClientSize.Height - shown.Bottom);
                    g.FillRectangle(black, 0, 0, shown.Left, ClientSize.Height);
                    g.FillRectangle(black, shown.Right, 0, ClientSize.Width - shown.Right, ClientSize.Height);
                }
                g.InterpolationMode = scale < 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(frame, shown);
            }
            if (overlay != null && DateTime.Now < overlayUntil)
            {
                Size sz = TextRenderer.MeasureText(overlay, Theme.UiBold);
                var r = new Rectangle((ClientSize.Width - sz.Width) / 2 - 14, ClientSize.Height - sz.Height - 46, sz.Width + 28, sz.Height + 16);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Rounded(r, 10))
                using (var br = new SolidBrush(Color.FromArgb(225, 31, 35, 43))) g.FillPath(br, path);
                TextRenderer.DrawText(g, overlay, Theme.UiBold, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            if (session.Recording)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var br = new SolidBrush(Theme.Red)) g.FillEllipse(br, 12, 12, 12, 12);
            }
            if ((DateTime.Now - fpsStart).TotalSeconds >= 1)
            {
                fpsText = frames + " fps";
                frames = 0; fpsStart = DateTime.Now;
            }
        }

        public string Fps { get { return fpsText; } }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

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
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0, alt = (keyData & Keys.Alt) != 0;
            if (keyData == Keys.F11) { ToggleFullscreen(); return true; }
            if (keyData == Keys.F12) { main.ToggleRecording(session); return true; }
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
            if (e.KeyChar >= ' ' && !char.IsControl(e.KeyChar)) session.Text(e.KeyChar.ToString());
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
                Bounds = Screen.FromControl(this).Bounds;
            }
            else
            {
                FormBorderStyle = oldBorder;
                Bounds = oldBounds;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (WindowState == FormWindowState.Normal && !fullscreen) main.SaveWindow(session.Serial, Bounds);
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
