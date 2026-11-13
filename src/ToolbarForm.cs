using System;
using System.Drawing;
using System.Windows.Forms;

namespace MirrorDock
{
    // A slim button strip that sticks to the side of a mirror window. Buttons talk straight to the phone engine.
    class ToolbarForm : Form
    {
        readonly MirrorSession session;
        readonly MainForm main;
        readonly Timer follow = new Timer();
        readonly ToolTip tips = new ToolTip();
        Label fpsLabel;
        bool screenOff;
        Button last, recBtn, muteBtn;

        void UpdateRecButton()
        {
            if (recBtn == null) return;
            bool on = session.Recording;
            recBtn.ForeColor = on ? Theme.Red : Theme.Text;
            tips.SetToolTip(recBtn, on ? "Stop recording  (F12)" : "Record to Videos\\MirrorDock  (F12)");
        }

        public ToolbarForm(MainForm main, MirrorSession s)
        {
            this.main = main;
            session = s;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Card;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            SuspendLayout();

            int y = 6;
            // Glyphs come from the Segoe MDL2 Assets font that ships with Windows 10/11.
            y = Add("", "Back  (right-click / Esc)", y, () => session.Key(4));
            y = Add("", "Home  (middle-click)", y, () => session.Key(3));
            y = Add("", "Recent apps", y, () => session.Key(187));
            y = Gap(y);
            y = Add("", "Volume up", y, () => session.Key(24));
            y = Add("", "Volume down", y, () => session.Key(25));
            y = Add("", "Power / lock", y, () => session.Key(26));
            y = Add("", "Rotate phone", y, () => session.Rotate());
            y = Add("", "Notifications", y, () => session.Notifications());
            y = Gap(y);
            y = Add("", "Screenshot to Pictures\\MirrorDock", y, () => main.TakeScreenshot(session.Serial, session.Name));
            if (s.CanRecord)
            {
                y = Add("", "Record to Videos\\MirrorDock  (F12)", y, () => main.ToggleRecording(session));
                recBtn = last;
                s.RecordingChanged += ss => { try { BeginInvoke((Action)UpdateRecButton); } catch { } };
            }
            if (s.AudioOn)
            {
                y = Add("", "Mute / unmute phone sound on the PC", y, () =>
                {
                    session.Muted = !session.Muted;
                    muteBtn.Text = session.Muted ? "" : "";
                });
                muteBtn = last;
            }
            y = Add("", "Paste PC clipboard on phone  (Ctrl+V)", y, () => { if (Clipboard.ContainsText()) session.SetPhoneClipboard(Clipboard.GetText(), true); });
            y = Add("", "Install an APK...", y, () => main.InstallApk(session.Serial, this));
            y = Add("", "Phone screen off/on (keeps mirroring)", y, () => { screenOff = !screenOff; session.ScreenPower(!screenOff); });
            y = Add("", "Fullscreen  (F11)", y, () => { if (session.Win != null) session.Win.ToggleFullscreen(); });
            y = Gap(y);
            fpsLabel = Theme.MakeLabel("", Theme.Small, Theme.Sub);
            fpsLabel.AutoSize = false; fpsLabel.TextAlign = ContentAlignment.MiddleCenter;
            fpsLabel.Bounds = new Rectangle(0, y, 44, 18);
            tips.SetToolTip(fpsLabel, "Frames per second right now (only counts when the screen changes)");
            Controls.Add(fpsLabel);
            y += 20;
            y = Add("", "Stop mirroring", y, () => session.Stop());
            ClientSize = new Size(44, y + 4);
            ResumeLayout(false);

            follow.Interval = 100;
            follow.Tick += (o, e) => Follow();
            FormClosed += (o, e) => follow.Stop();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: no Alt+Tab entry
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        int Add(string glyph, string tip, int y, Action click)
        {
            var b = new Button();
            b.Text = glyph;
            b.Font = Theme.Glyph;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.Hover;
            b.FlatAppearance.MouseDownBackColor = Theme.Accent;
            b.ForeColor = Theme.Text;
            b.BackColor = Theme.Card;
            b.Bounds = new Rectangle(4, y, 36, 34);
            b.TabStop = false;
            b.Cursor = Cursors.Hand;
            b.Click += (s, e) => { try { click(); } catch (Exception ex) { main.Status(ex.Message, true); } };
            tips.SetToolTip(b, tip);
            Controls.Add(b);
            last = b;
            return y + 36;
        }

        int Gap(int y)
        {
            var line = new Panel();
            line.BackColor = Theme.Border;
            line.Bounds = new Rectangle(8, y + 3, 28, 1);
            Controls.Add(line);
            return y + 8;
        }

        public void StartFollowing() { follow.Start(); Follow(); }

        void Follow()
        {
            Form w = session.Win;
            if (w == null || w.IsDisposed || !w.Visible || w.WindowState == FormWindowState.Minimized) { if (Visible) Hide(); return; }
            if (Owner != w) Owner = w;
            fpsLabel.Text = session.Win.Fps;

            Native.RECT r = Native.VisibleRect(w.Handle);
            Rectangle win = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            Screen scr = Screen.FromRectangle(win);
            if (win.Width >= scr.Bounds.Width && win.Height >= scr.Bounds.Height) { if (Visible) Hide(); return; } // fullscreen

            Rectangle wa = scr.WorkingArea;
            int x = win.Right + 4;
            if (x + Width > wa.Right) x = win.Left - Width - 4;
            if (x < wa.Left) x = win.Right - Width - 4;
            int y = Math.Max(wa.Top, Math.Min(win.Top, wa.Bottom - Height));
            if (Left != x || Top != y) Location = new Point(x, y);
            if (!Visible) Show();
        }
    }
}
