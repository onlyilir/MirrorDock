using System;
using System.Drawing;
using System.Windows.Forms;

namespace MirrorDock
{
    // The button bar, built into the right side of the mirror window (it used to be a separate window that chased it).
    class ToolbarPanel : Panel
    {
        public const int BarWidth = 52;
        readonly MirrorSession session;
        readonly MainForm main;
        readonly MirrorWindow win;
        readonly ToolTip tips = new ToolTip();
        readonly Label stats;
        readonly Timer tick = new Timer { Interval = 500 };
        IconButton rec, mute, keyboard, screenOff;
        int y = 8;

        public ToolbarPanel(MainForm main, MirrorWindow win, MirrorSession s)
        {
            this.main = main; this.win = win; session = s;
            Dock = DockStyle.Right;
            Width = BarWidth;
            BackColor = Theme.Card;
            AutoScroll = true;
            DoubleBuffered = true;

            Add("", "Back  (right-click, Esc)", () => session.Key(4));
            Add("", "Home  (middle-click)", () => session.Key(3));
            Add("", "Recent apps", () => session.Key(187));
            Gap();
            Add("", "Volume up", () => session.Key(24));
            Add("", "Volume down", () => session.Key(25));
            Add("", "Power / lock", () => session.Key(26));
            Add("", "Rotate phone", () => session.Rotate());
            Add("", "Notifications", () => session.Notifications());
            Gap();
            Add("", "Screenshot to Pictures\\MirrorDock", () => main.TakeScreenshot(session.Serial, session.Name));
            if (s.CanRecord || !s.IsExtraScreen)
            {
                rec = Add("", "Record to Videos\\MirrorDock  (F12)", () => main.ToggleRecording(session));
                rec.OnColor = Theme.Red;
                s.RecordingChanged += ss => { try { BeginInvoke((Action)(() => rec.Toggled = session.Recording)); } catch { } };
            }
            if (s.Opts.Audio && !s.IsExtraScreen)
                mute = Add("", "Mute / unmute phone sound on the PC", () =>
                {
                    session.Muted = !session.Muted;
                    mute.Text = session.Muted ? "" : "";
                    mute.Toggled = session.Muted;
                });
            keyboard = Add("", "Real keyboard: keys go to the phone like a plugged-in keyboard (games, shortcuts). Off = typing mode.", () =>
            {
                session.RealKeyboard = !session.RealKeyboard;
                keyboard.Toggled = session.RealKeyboard;
                win.ShowOverlay(session.RealKeyboard ? "Real keyboard on" : "Typing mode");
                win.Focus();
            });
            Add("", "Paste PC clipboard on phone  (Ctrl+V)", () => { if (Clipboard.ContainsText()) session.SetPhoneClipboard(Clipboard.GetText(), true); });
            Add("", "Install an APK...", () => main.InstallApk(session.Serial, win));
            if (!s.IsExtraScreen)
                screenOff = Add("", "Phone screen off/on (keeps mirroring)", () =>
                {
                    screenOff.Toggled = !screenOff.Toggled;
                    session.ScreenPower(!screenOff.Toggled);
                });
            Add("", "Fullscreen  (F11)", () => win.ToggleFullscreen());
            Gap();
            stats = new Label { AutoSize = false, Bounds = new Rectangle(0, y, BarWidth, 34), TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Small, ForeColor = Theme.Sub, BackColor = Theme.Card };
            tips.SetToolTip(stats, "Frames per second (only counts when the screen changes) and video quality. Quality drops by itself on a slow connection.");
            Controls.Add(stats);
            y += 38;
            var stop = Add("", "Stop mirroring", () => session.Stop());
            stop.OnColor = Theme.Red;
            tick.Tick += (o, e) => stats.Text = win.Fps + (session.H264 && session.CurrentMbps > 0 ? "\n" + session.CurrentMbps.ToString("0.#") + " Mbps" : "");
            tick.Start();
            Disposed += (o, e) => tick.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, 0, Height);
        }

        IconButton Add(string glyph, string tip, Action click)
        {
            var b = new IconButton(glyph) { Bounds = new Rectangle(4, y, BarWidth - 8, 38), TabStop = false };
            b.Click += (s, e) => { try { click(); } catch (Exception ex) { main.Status(ex.Message, true); } };
            tips.SetToolTip(b, tip);
            Controls.Add(b);
            y += 40;
            return b;
        }

        void Gap()
        {
            Controls.Add(new Panel { BackColor = Theme.Border, Bounds = new Rectangle(12, y + 3, BarWidth - 24, 1) });
            y += 8;
        }
    }
}
