using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MirrorDock
{
    // One live mirror of one phone, using MirrorDock's own phone engine.
    class MirrorSession
    {
        public string Serial, Name;
        public Options Opts;
        public MirrorWindow Win;
        public ToolbarForm Bar;
        public int DevW, DevH;
        public DateTime Started;
        public string LastError;
        public volatile bool Stopping;
        public bool H264 { get; private set; }
        public bool AudioOn { get; private set; }

        public event Action<MirrorSession, string> Line;
        public event Action<MirrorSession, bool> Ended;        // bool = dropped unexpectedly
        public event Action<MirrorSession, string> PhoneClipboard;
        public event Action<MirrorSession> RecordingChanged;

        Process engine;
        TcpClient videoSock, controlSock, audioSock;
        NetworkStream ctrl;
        readonly object ctrlLock = new object();
        int port;
        string socketName;
        bool touchesChanged, stayOnChanged, ended, rotated;
        AudioPlayer player;

        // recording
        readonly object recLock = new object();
        Mp4Writer recorder;
        AacEncoder aac;
        Mp4Writer aacFor;
        byte[] lastConfig;
        int frameW, frameH;
        public string RecordingPath { get { lock (recLock) return recorder == null ? null : recorder.Path; } }

        const string RemoteJar = "/data/local/tmp/mirrordock.jar";

        public static string ShotsDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MirrorDock"); }
        }
        public static string VideosDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MirrorDock"); }
        }

        public bool Running { get { return !ended && Started != DateTime.MinValue; } }

        // ------------------------------------------------------------ start
        public async Task Start(bool forceJpeg)
        {
            Started = DateTime.Now;
            string jar = ExtractEngine();
            Say("Sending engine to phone...");
            ProcResult r = await Adb.Run("-s " + Serial + " push \"" + jar + "\" " + RemoteJar, 30000);
            if (r.Code != 0) throw new Exception(Friendly(r.All));

            socketName = "mirrordock_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            port = 27200 + new Random().Next(0, 700);
            r = await Adb.Run("-s " + Serial + " forward tcp:" + port + " localabstract:" + socketName);
            if (r.Code != 0) throw new Exception(Friendly(r.All));

            if (Opts.ShowTouches && !Opts.ViewOnly) { await Adb.Run("-s " + Serial + " shell settings put system show_touches 1"); touchesChanged = true; }
            if (Opts.StayAwake && !Opts.ViewOnly) { await Adb.Run("-s " + Serial + " shell svc power stayon true"); stayOnChanged = true; }

            AudioOn = Opts.Audio;
            string codec = forceJpeg || Opts.Codec == "jpeg" ? "jpeg" : "h264";
            string args = "-s " + Serial + " shell CLASSPATH=" + RemoteJar + " app_process / com.mirrordock.Server " +
                          socketName + " " + Opts.MaxSize + " " + Opts.Quality + " " + Opts.MaxFps + " " + codec + " " +
                          Opts.BitRate + " " + (AudioOn ? "1" : "0");
            engine = Process.Start(Adb.Hidden(Adb.AdbPath, args));
            engine.EnableRaisingEvents = true;
            engine.OutputDataReceived += (s, e) => EngineLine(e.Data);
            engine.ErrorDataReceived += (s, e) => EngineLine(e.Data);
            engine.BeginOutputReadLine();
            engine.BeginErrorReadLine();

            await Task.Run(() => ConnectSockets());
            Say("Connected (" + (H264 ? "H.264" : "JPEG") + (AudioOn ? ", sound on" : "") + ").");
        }

        static string ExtractEngine()
        {
            Directory.CreateDirectory(AppSettings.Dir);
            string path = Path.Combine(AppSettings.Dir, "mirrordock-server.jar");
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("mirrordock-server.jar"))
            {
                if (s == null) throw new Exception("The phone engine is missing from this build.");
                var bytes = new byte[s.Length];
                s.Read(bytes, 0, bytes.Length);
                bool same = File.Exists(path) && new FileInfo(path).Length == bytes.Length;
                if (same) { byte[] old = File.ReadAllBytes(path); for (int i = 0; i < old.Length && same; i++) same = old[i] == bytes[i]; }
                if (!same) File.WriteAllBytes(path, bytes);
            }
            return path;
        }

        void ConnectSockets()
        {
            // adb accepts the TCP connection before the engine listens, so retry until the hello arrives.
            for (int i = 0; i < 60; i++)
            {
                if (Stopping) throw new Exception("Stopped.");
                if (engine.HasExited) throw new Exception(LastError ?? "The phone engine stopped right away. Click Show log for details.");
                try
                {
                    var v = new TcpClient { NoDelay = true };
                    v.Connect("127.0.0.1", port);
                    v.ReceiveTimeout = 1500;
                    var hello = new byte[5];
                    int got = 0;
                    var st = v.GetStream();
                    while (got < 5) { int n = st.Read(hello, got, 5 - got); if (n <= 0) break; got += n; }
                    if (got == 5 && Encoding.ASCII.GetString(hello, 0, 4) == "MDK2")
                    {
                        v.ReceiveTimeout = 0;
                        H264 = hello[4] == 1;
                        videoSock = v;
                        controlSock = new TcpClient { NoDelay = true };
                        controlSock.Connect("127.0.0.1", port);
                        ctrl = controlSock.GetStream();
                        if (AudioOn)
                        {
                            audioSock = new TcpClient { NoDelay = true };
                            audioSock.Connect("127.0.0.1", port);
                        }
                        StartBackground(ReadControl, "control");
                        if (AudioOn) StartBackground(ReadAudio, "audio");
                        return;
                    }
                    v.Close();
                }
                catch (IOException) { }
                catch (SocketException) { }
                Thread.Sleep(150);
            }
            throw new Exception(LastError ?? "The phone engine didn't answer. Click Show log for details.");
        }

        static void StartBackground(ThreadStart fn, string name)
        {
            var t = new Thread(fn) { IsBackground = true, Name = name };
            t.Start();
        }

        // ------------------------------------------------------------ video
        // Runs on its own thread (started by MirrorWindow). present() gets a decoded frame and returns a bitmap to reuse.
        public void ReadVideo(Func<Bitmap, Bitmap> present)
        {
            H264Decoder dec = null;
            Bitmap spare = null;
            string failure = null;
            try
            {
                var st = new BufferedStream(videoSock.GetStream(), 1 << 16);
                var head = new byte[29];
                byte[] data = new byte[1 << 20];
                byte[] pendingConfig = null;
                if (H264) dec = new H264Decoder();
                while (!Stopping)
                {
                    ReadFully(st, head, 29);
                    int len = BE(head, 0);
                    DevW = BE(head, 4); DevH = BE(head, 8);
                    int fw = BE(head, 12), fh = BE(head, 16);
                    long pts = ((long)BE(head, 20) << 32) | (uint)BE(head, 24);
                    int flags = head[28];
                    if (len <= 0 || len > 64000000) throw new IOException("Bad frame");
                    if (data.Length < len) data = new byte[len * 2];
                    ReadFully(st, data, len);

                    if (!H264)
                    {
                        Bitmap jpg;
                        using (var ms = new MemoryStream(data, 0, len))
                        using (var img = Image.FromStream(ms, false, false)) jpg = new Bitmap(img);
                        Bitmap back = present(jpg);
                        if (back != null) back.Dispose();
                        continue;
                    }

                    if ((flags & 1) != 0)
                    {
                        pendingConfig = new byte[len];
                        Buffer.BlockCopy(data, 0, pendingConfig, 0, len);
                        OnConfig(pendingConfig, fw, fh);
                        continue;
                    }
                    byte[] feed = data; int feedLen = len;
                    if (pendingConfig != null)
                    {   // give the decoder SPS/PPS together with the first frame after them
                        feed = new byte[pendingConfig.Length + len];
                        Buffer.BlockCopy(pendingConfig, 0, feed, 0, pendingConfig.Length);
                        Buffer.BlockCopy(data, 0, feed, pendingConfig.Length, len);
                        feedLen = feed.Length;
                        pendingConfig = null;
                    }
                    Record(data, len, pts, (flags & 2) != 0);
                    Bitmap frame = dec.Decode(feed, feedLen, fw, fh, spare);
                    if (frame != null) spare = present(frame);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                failure = ex.Message;
                Say("ERROR: video: " + ex);
            }
            finally
            {
                if (dec != null) dec.Dispose();
                if (spare != null) spare.Dispose();
            }
            if (failure != null && H264 && !Stopping)
            {
                LastError = "Windows couldn't decode H.264 video here (" + failure + "). Switching to compatibility mode.";
                NeedsJpeg = true;
            }
            Finish(!Stopping);
        }

        public bool NeedsJpeg;

        static void ReadFully(Stream s, byte[] b, int n)
        {
            int got = 0;
            while (got < n) { int r = s.Read(b, got, n - got); if (r <= 0) throw new IOException("closed"); got += r; }
        }

        static int BE(byte[] b, int o) { return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]; }

        // ------------------------------------------------------------ recording
        void OnConfig(byte[] config, int w, int h)
        {
            lock (recLock)
            {
                bool sizeChanged = lastConfig != null && (w != frameW || h != frameH);
                lastConfig = config; frameW = w; frameH = h;
                if (recorder != null && sizeChanged)
                {   // phone rotated: finish this file and continue in a new one
                    string next = NewRecordingPath();
                    CloseRecorder();
                    OpenRecorder(next);
                }
            }
        }

        void Record(byte[] data, int len, long pts, bool key)
        {
            lock (recLock) { if (recorder != null) recorder.AddVideo(data, len, pts, key); }
        }

        public bool CanRecord { get { return H264; } }
        public bool Recording { get { lock (recLock) return recorder != null; } }

        public string ToggleRecording()
        {
            if (!H264) throw new Exception("Recording needs H.264 video. Turn off compatibility mode.");
            string done = null;
            lock (recLock)
            {
                if (recorder != null) { done = recorder.Path; CloseRecorder(); }
                else
                {
                    if (lastConfig == null) throw new Exception("Video hasn't started yet. Try again in a second.");
                    OpenRecorder(NewRecordingPath());
                }
            }
            if (RecordingChanged != null) RecordingChanged(this);
            return done;
        }

        string NewRecordingPath()
        {
            Directory.CreateDirectory(VideosDir);
            return Path.Combine(VideosDir, AppSettings.SafeName(Name).Replace(' ', '_') + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4");
        }

        void OpenRecorder(string path)
        {
            recorder = new Mp4Writer(path, lastConfig, frameW, frameH);
            Send(new byte[] { 8 }); // ask the phone for a keyframe so the file starts right away
            Say("Recording to " + path);
        }

        void CloseRecorder()
        {
            if (recorder == null) return;
            try { recorder.Close(); Say("Saved " + recorder.Path + " (" + recorder.FrameCount + " frames" + (recorder.HasAudio ? ", with sound" : "") + ")"); }
            catch (Exception ex) { Say("ERROR: couldn't finish recording: " + ex.Message); }
            recorder = null;
        }

        // ------------------------------------------------------------ audio + clipboard from phone
        void ReadAudio()
        {
            try
            {
                var st = audioSock.GetStream();
                var hello = new byte[5];
                ReadFully(st, hello, 5);
                if (hello[4] != 1) { Say("Sound isn't available on this phone (needs Android 11+)."); return; }
                try { player = new AudioPlayer(); } catch (Exception ex) { Say("ERROR: " + ex.Message); }
                var lenBuf = new byte[4];
                var buf = new byte[65536];
                while (!Stopping)
                {
                    ReadFully(st, lenBuf, 4);
                    int n = BE(lenBuf, 0);
                    if (n <= 0 || n > buf.Length) throw new IOException("bad audio");
                    ReadFully(st, buf, n);
                    if (player != null && !Muted) player.Play(buf, n);
                    lock (recLock)
                    {
                        // The AAC encoder is created, used and released only on this thread (COM objects dislike thread hopping).
                        if (recorder != aacFor)
                        {
                            if (aac != null) { aac.Dispose(); aac = null; }
                            aacFor = recorder;
                            if (recorder != null)
                            {
                                try { aac = new AacEncoder(); }
                                catch (Exception ex) { Say("Recording without sound: " + ex.Message); }
                            }
                        }
                        if (recorder != null && aac != null && !recorder.WaitingForKeyframe)
                        {
                            Mp4Writer rw = recorder;
                            aac.Encode(buf, n, (frame, len) => rw.AddAudio(frame, len));
                        }
                    }
                }
            }
            catch (Exception ex) { if (!Stopping && !(ex is IOException) && !(ex is ObjectDisposedException)) Say("ERROR: sound: " + ex.Message); }
            finally
            {
                if (player != null) { player.Dispose(); player = null; }
                if (aac != null) { aac.Dispose(); aac = null; }
            }
        }

        public bool Muted;

        void ReadControl()
        {
            try
            {
                var st = controlSock.GetStream();
                var lenBuf = new byte[4];
                var type = new byte[1];
                while (!Stopping)
                {
                    ReadFully(st, type, 1);
                    if (type[0] != 1) break;
                    ReadFully(st, lenBuf, 4);
                    int n = BE(lenBuf, 0);
                    if (n < 0 || n > 8000000) break;
                    var b = new byte[n];
                    ReadFully(st, b, n);
                    string text = Encoding.UTF8.GetString(b);
                    lastFromPhone = text;
                    if (PhoneClipboard != null) PhoneClipboard(this, text);
                }
            }
            catch (Exception) { }
        }

        string lastFromPhone;

        // ------------------------------------------------------------ input to phone
        void Send(byte[] msg)
        {
            if (ctrl == null || Opts.ViewOnly) return;
            lock (ctrlLock)
            {
                try { ctrl.Write(msg, 0, msg.Length); } catch { }
            }
        }

        static void PutInt(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        public void Touch(int action, int x, int y)
        {
            var b = new byte[10];
            b[0] = 1; b[1] = (byte)action; PutInt(b, 2, x); PutInt(b, 6, y);
            Send(b);
        }

        public void Pinch(int action, int x, int y)
        {
            var b = new byte[18];
            b[0] = 7; b[1] = (byte)action; PutInt(b, 2, x); PutInt(b, 6, y); PutInt(b, 10, DevW / 2); PutInt(b, 14, DevH / 2);
            Send(b);
        }

        public void KeyEvent(bool down, int code, int meta)
        {
            var b = new byte[10];
            b[0] = 2; b[1] = (byte)(down ? 0 : 1); PutInt(b, 2, code); PutInt(b, 6, meta);
            Send(b);
        }

        public void Key(int code) { KeyEvent(true, code, 0); KeyEvent(false, code, 0); }

        public void Text(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            byte[] t = Encoding.UTF8.GetBytes(s);
            if (t.Length > 60000) { SetPhoneClipboard(s, true); return; }
            var b = new byte[3 + t.Length];
            b[0] = 3; b[1] = (byte)(t.Length >> 8); b[2] = (byte)t.Length;
            Buffer.BlockCopy(t, 0, b, 3, t.Length);
            Send(b);
        }

        // Puts text on the phone's clipboard (and pastes it if asked).
        public void SetPhoneClipboard(string s, bool paste)
        {
            if (s == null) return;
            if (!paste && s == lastFromPhone) return;  // came from the phone in the first place
            lastFromPhone = s;
            byte[] t = Encoding.UTF8.GetBytes(s);
            var b = new byte[6 + t.Length];
            b[0] = 6; PutInt(b, 1, t.Length);
            Buffer.BlockCopy(t, 0, b, 5, t.Length);
            b[5 + t.Length] = (byte)(paste ? 1 : 0);
            Send(b);
        }

        public void Scroll(int x, int y, float h, float v)
        {
            var b = new byte[17];
            b[0] = 4; PutInt(b, 1, x); PutInt(b, 5, y);
            PutInt(b, 9, BitConverter.ToInt32(BitConverter.GetBytes(h), 0)); PutInt(b, 13, BitConverter.ToInt32(BitConverter.GetBytes(v), 0));
            Send(b);
        }

        public void ScreenPower(bool on) { Send(new byte[] { 5, (byte)(on ? 2 : 0) }); }

        public void Notifications() { Adb.Run("-s " + Serial + " shell cmd statusbar expand-notifications"); }

        public async void Rotate()
        {
            rotated = !rotated;
            await Adb.Run("-s " + Serial + " shell settings put system accelerometer_rotation 0");
            await Adb.Run("-s " + Serial + " shell settings put system user_rotation " + (rotated ? "1" : "0"));
        }

        // ------------------------------------------------------------ stop
        public void Stop()
        {
            if (Stopping) return;
            Stopping = true;
            if (Opts.PowerOffOnClose && !Opts.ViewOnly) Key(26);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(150);
                Close();
                Finish(false);
            });
        }

        void Close()
        {
            lock (recLock) CloseRecorder();
            try { if (controlSock != null) controlSock.Close(); } catch { }
            try { if (videoSock != null) videoSock.Close(); } catch { }
            try { if (audioSock != null) audioSock.Close(); } catch { }
            try { if (engine != null && !engine.WaitForExit(1500)) engine.Kill(); } catch { }
        }

        void Finish(bool dropped)
        {
            lock (this) { if (ended) return; ended = true; }
            Close();
            if (port != 0) Adb.Run("-s " + Serial + " forward --remove tcp:" + port);
            if (touchesChanged) Adb.Run("-s " + Serial + " shell settings put system show_touches 0");
            if (stayOnChanged) Adb.Run("-s " + Serial + " shell svc power stayon false");
            if (rotated) Adb.Run("-s " + Serial + " shell settings put system user_rotation 0");
            if (dropped && LastError == null) LastError = "The phone disconnected.";
            if (RecordingChanged != null) RecordingChanged(this);
            if (Ended != null) Ended(this, dropped);
        }

        // ------------------------------------------------------------ log
        void EngineLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.TrimEnd();
            if (text.StartsWith("ERROR") && !text.Contains("sound") && !text.Contains("screen off")) LastError = Friendly(text);
            Say(text);
        }

        void Say(string text) { if (Line != null) Line(this, text); }

        public static string Friendly(string line)
        {
            string l = line.ToLowerInvariant();
            if (l.Contains("unauthorized"))
                return "The phone hasn't allowed this PC yet. Unlock it and tap Allow on the USB debugging popup.";
            if (l.Contains("device") && l.Contains("not found"))
                return "Phone not found. Check the cable, or reconnect over Wi-Fi.";
            if (l.Contains("offline"))
                return "The phone is offline. Unplug it and plug it back in.";
            if (l.Contains("securityexception") || l.Contains("permission denial"))
                return "The phone blocked screen capture. Some phones need 'USB debugging (Security settings)' turned on in Developer options.";
            if (l.Contains("input injection unavailable"))
                return "The phone won't accept taps from the PC. View-only mirroring still works.";
            if (l.Contains("h.264 encoder failed"))
                return "The phone's video encoder failed. Try a lower resolution, or turn on compatibility mode.";
            if (l.StartsWith("error"))
                return line.Substring(line.IndexOf(':') + 1).Trim();
            return line.Trim();
        }
    }
}
