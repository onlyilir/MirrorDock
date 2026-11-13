using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using M = MirrorDock.H264Decoder;

namespace MirrorDock
{
    // Writes the phone's H.264 stream (and AAC sound) into a standard MP4 file. No re-encoding of the video.
    sealed class Mp4Writer
    {
        readonly FileStream fs;
        readonly long mdatStart;
        readonly byte[] sps, pps;
        readonly int width, height;
        readonly List<long> vOffsets = new List<long>(), aOffsets = new List<long>();
        readonly List<int> vSizes = new List<int>(), aSizes = new List<int>();
        readonly List<long> vPts = new List<long>();
        readonly List<int> keyIndex = new List<int>();
        long firstPts = -1;
        DateTime lastFrameAt, closedAt;
        public readonly string Path;
        public bool HasAudio;
        bool closed;

        public Mp4Writer(string path, byte[] config, int w, int h)
        {
            Path = path;
            width = w; height = h;
            foreach (byte[] nal in Nals(config, config.Length))
            {
                int t = nal[0] & 0x1F;
                if (t == 7) sps = nal; else if (t == 8) pps = nal;
            }
            if (sps == null || pps == null) throw new Exception("Missing video setup data (SPS/PPS).");
            fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            var ftyp = Box("ftyp", b => { b.Tag("isom"); b.U32(512); b.Tag("isom"); b.Tag("iso2"); b.Tag("avc1"); b.Tag("mp41"); });
            fs.Write(ftyp, 0, ftyp.Length);
            mdatStart = fs.Position;
            var head = new byte[16];
            head[3] = 1; Encoding.ASCII.GetBytes("mdat").CopyTo(head, 4);
            fs.Write(head, 0, 16); // 64-bit size, patched on close
        }

        public bool WaitingForKeyframe { get { return vSizes.Count == 0; } }

        // One Annex-B access unit from the encoder.
        public void AddVideo(byte[] data, int len, long ptsUs, bool key)
        {
            if (closed) return;
            if (vSizes.Count == 0 && !key) return;
            if (firstPts < 0) firstPts = ptsUs;
            long off = fs.Position;
            int size = 0;
            var lenBuf = new byte[4];
            foreach (byte[] nal in Nals(data, len))
            {
                int t = nal[0] & 0x1F;
                if (t == 7 || t == 8 || t == 9) continue;   // SPS/PPS live in avcC; drop access unit delimiters
                lenBuf[0] = (byte)(nal.Length >> 24); lenBuf[1] = (byte)(nal.Length >> 16); lenBuf[2] = (byte)(nal.Length >> 8); lenBuf[3] = (byte)nal.Length;
                fs.Write(lenBuf, 0, 4);
                fs.Write(nal, 0, nal.Length);
                size += 4 + nal.Length;
            }
            if (size == 0) return;
            if (key) keyIndex.Add(vSizes.Count + 1);
            vOffsets.Add(off); vSizes.Add(size); vPts.Add(ptsUs - firstPts);
            lastFrameAt = DateTime.UtcNow;
        }

        // One raw AAC frame (1024 samples at 48 kHz).
        public void AddAudio(byte[] aac, int len)
        {
            if (closed || vSizes.Count == 0) return;   // start audio together with the first video frame
            HasAudio = true;
            aOffsets.Add(fs.Position); aSizes.Add(len);
            fs.Write(aac, 0, len);
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            closedAt = DateTime.UtcNow;
            long end = fs.Position;
            fs.Position = mdatStart + 8;
            long mdatSize = end - mdatStart;
            var b8 = new byte[8];
            for (int i = 0; i < 8; i++) b8[i] = (byte)(mdatSize >> (56 - 8 * i));
            fs.Write(b8, 0, 8);
            fs.Position = end;
            byte[] moov = BuildMoov();
            fs.Write(moov, 0, moov.Length);
            fs.Dispose();
            if (vSizes.Count == 0) { try { File.Delete(Path); } catch { } }
        }

        public int FrameCount { get { return vSizes.Count; } }

        // ------------------------------------------------------------ boxes
        const int VScale = 90000, AScale = 48000;

        byte[] BuildMoov()
        {
            int n = vSizes.Count;
            var vDur = new int[n];
            for (int i = 0; i < n; i++)
            {
                // The last frame stays on screen until Stop was pressed (a still phone screen sends no new frames).
                long d = i + 1 < n ? vPts[i + 1] - vPts[i] : Math.Max(33333, (long)(closedAt - lastFrameAt).TotalMilliseconds * 1000);
                if (d <= 0) d = 1000;
                vDur[i] = (int)(d * VScale / 1000000);
                if (vDur[i] <= 0) vDur[i] = 1;
            }
            long vTotal = 0; foreach (int d in vDur) vTotal += d;
            long aTotal = (long)aSizes.Count * 1024;
            long movieMs = Math.Max(vTotal * 1000 / VScale, aTotal * 1000 / AScale);

            return Box("moov", m =>
            {
                m.Raw(Box("mvhd", b =>
                {
                    b.U32(0); b.U32(0); b.U32(0); b.U32(1000); b.U32((uint)movieMs);
                    b.U32(0x00010000); b.U16(0x0100); b.Zero(10); Matrix(b); b.Zero(24); b.U32(HasAudio ? 3u : 2u);
                }));
                m.Raw(Track(1, true, vTotal * 1000 / VScale, VScale, vTotal, VideoEntry(), Stts(vDur), keyIndex, vSizes, vOffsets));
                if (HasAudio)
                {
                    var aDur = new int[aSizes.Count];
                    for (int i = 0; i < aDur.Length; i++) aDur[i] = 1024;
                    m.Raw(Track(2, false, aTotal * 1000 / AScale, AScale, aTotal, AudioEntry(), Stts(aDur), null, aSizes, aOffsets));
                }
            });
        }

        byte[] Track(int id, bool video, long durMs, int scale, long dur, byte[] entry, byte[] stts, List<int> keys, List<int> sizes, List<long> offsets)
        {
            return Box("trak", t =>
            {
                t.Raw(Box("tkhd", b =>
                {
                    b.U32(3); b.U32(0); b.U32(0); b.U32((uint)id); b.U32(0); b.U32((uint)durMs); b.Zero(8);
                    b.U16(0); b.U16(0); b.U16(video ? (ushort)0 : (ushort)0x0100); b.U16(0); Matrix(b);
                    b.U32(video ? (uint)width << 16 : 0); b.U32(video ? (uint)height << 16 : 0);
                }));
                t.Raw(Box("mdia", md =>
                {
                    md.Raw(Box("mdhd", b => { b.U32(0); b.U32(0); b.U32(0); b.U32((uint)scale); b.U32((uint)dur); b.U16(0x55C4); b.U16(0); }));
                    md.Raw(Box("hdlr", b => { b.U32(0); b.U32(0); b.Tag(video ? "vide" : "soun"); b.Zero(12); b.Bytes(Encoding.ASCII.GetBytes(video ? "VideoHandler\0" : "SoundHandler\0")); }));
                    md.Raw(Box("minf", mi =>
                    {
                        if (video) mi.Raw(Box("vmhd", b => { b.U32(1); b.Zero(8); }));
                        else mi.Raw(Box("smhd", b => { b.U32(0); b.U32(0); }));
                        mi.Raw(Box("dinf", di => di.Raw(Box("dref", b => { b.U32(0); b.U32(1); b.Raw(Box("url ", u => u.U32(1))); }))));
                        mi.Raw(Box("stbl", st =>
                        {
                            st.Raw(Box("stsd", b => { b.U32(0); b.U32(1); b.Raw(entry); }));
                            st.Raw(stts);
                            if (keys != null) st.Raw(Box("stss", b => { b.U32(0); b.U32((uint)keys.Count); foreach (int k in keys) b.U32((uint)k); }));
                            st.Raw(Box("stsc", b => { b.U32(0); b.U32(1); b.U32(1); b.U32(1); b.U32(1); }));
                            st.Raw(Box("stsz", b => { b.U32(0); b.U32(0); b.U32((uint)sizes.Count); foreach (int s in sizes) b.U32((uint)s); }));
                            st.Raw(Box("co64", b => { b.U32(0); b.U32((uint)offsets.Count); foreach (long o in offsets) b.U64((ulong)o); }));
                        }));
                    }));
                }));
            });
        }

        byte[] VideoEntry()
        {
            return Box("avc1", b =>
            {
                b.Zero(6); b.U16(1); b.Zero(16); b.U16((ushort)width); b.U16((ushort)height);
                b.U32(0x00480000); b.U32(0x00480000); b.U32(0); b.U16(1); b.Zero(32); b.U16(0x0018); b.U16(0xFFFF);
                b.Raw(Box("avcC", c =>
                {
                    c.U8(1); c.U8(sps[1]); c.U8(sps[2]); c.U8(sps[3]); c.U8(0xFF); c.U8(0xE1);
                    c.U16((ushort)sps.Length); c.Bytes(sps); c.U8(1); c.U16((ushort)pps.Length); c.Bytes(pps);
                }));
            });
        }

        static byte[] AudioEntry()
        {
            return Box("mp4a", b =>
            {
                b.Zero(6); b.U16(1); b.Zero(8); b.U16(2); b.U16(16); b.U16(0); b.U16(0); b.U32(48000u << 16);
                b.Raw(Box("esds", e =>
                {
                    e.U32(0);
                    e.U8(0x03); e.U8(25); e.U16(2); e.U8(0);                       // ES descriptor
                    e.U8(0x04); e.U8(17); e.U8(0x40); e.U8(0x15); e.U8(0); e.U16(0x1800); // AAC, audio stream, buffer 6144
                    e.U32(192000); e.U32(192000);
                    e.U8(0x05); e.U8(2); e.U8(0x11); e.U8(0x90);                   // AAC-LC, 48 kHz, stereo
                    e.U8(0x06); e.U8(1); e.U8(0x02);
                }));
            });
        }

        static byte[] Stts(int[] durations)
        {
            var runs = new List<int[]>();
            foreach (int d in durations)
            {
                if (runs.Count > 0 && runs[runs.Count - 1][1] == d) runs[runs.Count - 1][0]++;
                else runs.Add(new[] { 1, d });
            }
            return Box("stts", b => { b.U32(0); b.U32((uint)runs.Count); foreach (int[] r in runs) { b.U32((uint)r[0]); b.U32((uint)r[1]); } });
        }

        static void Matrix(BoxWriter b)
        {
            b.U32(0x00010000); b.U32(0); b.U32(0); b.U32(0); b.U32(0x00010000); b.U32(0); b.U32(0); b.U32(0); b.U32(0x40000000);
        }

        static byte[] Box(string type, Action<BoxWriter> body)
        {
            var w = new BoxWriter();
            body(w);
            byte[] content = w.ToArray();
            var outp = new byte[8 + content.Length];
            int size = outp.Length;
            outp[0] = (byte)(size >> 24); outp[1] = (byte)(size >> 16); outp[2] = (byte)(size >> 8); outp[3] = (byte)size;
            Encoding.ASCII.GetBytes(type).CopyTo(outp, 4);
            Buffer.BlockCopy(content, 0, outp, 8, content.Length);
            return outp;
        }

        // Splits Annex-B data into NAL units (without start codes).
        public static List<byte[]> Nals(byte[] d, int len)
        {
            var list = new List<byte[]>();
            int i = 0, start = -1;
            while (i + 2 < len)
            {
                if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
                {
                    if (start >= 0) list.Add(Slice(d, start, TrimZeros(d, start, i)));
                    i += 3; start = i;
                }
                else i++;
            }
            if (start >= 0 && start < len) list.Add(Slice(d, start, len));
            return list;
        }

        static int TrimZeros(byte[] d, int start, int end) { while (end > start && d[end - 1] == 0) end--; return end; }
        static byte[] Slice(byte[] d, int a, int b) { var r = new byte[b - a]; Buffer.BlockCopy(d, a, r, 0, b - a); return r; }
    }

    class BoxWriter
    {
        readonly MemoryStream ms = new MemoryStream();
        public void U8(int v) { ms.WriteByte((byte)v); }
        public void U16(int v) { U8(v >> 8); U8(v); }
        public void U32(uint v) { U8((int)(v >> 24)); U8((int)(v >> 16)); U8((int)(v >> 8)); U8((int)v); }
        public void U32(int v) { U32((uint)v); }
        public void U64(ulong v) { U32((uint)(v >> 32)); U32((uint)v); }
        public void Tag(string s) { Bytes(Encoding.ASCII.GetBytes(s)); }
        public void Zero(int n) { for (int i = 0; i < n; i++) ms.WriteByte(0); }
        public void Bytes(byte[] b) { ms.Write(b, 0, b.Length); }
        public void Raw(byte[] b) { Bytes(b); }
        public byte[] ToArray() { return ms.ToArray(); }
    }

    // PCM (48 kHz stereo s16) -> raw AAC-LC frames, using the AAC encoder built into Windows.
    sealed class AacEncoder : IDisposable
    {
        M.IMFTransform mft;
        static readonly Guid CLSID_AAC = new Guid("93AF0C51-2275-45d2-A35B-F2BA21CAED00");
        static readonly Guid MAJOR = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static readonly Guid SUB = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static readonly Guid Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        static readonly Guid PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");
        static readonly Guid AAC = new Guid("00001610-0000-0010-8000-00AA00389B71");
        static readonly Guid CH = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        static readonly Guid RATE = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        static readonly Guid BITS = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        static readonly Guid AVG = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        static readonly Guid ALIGN = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        static readonly Guid PAYLOAD = new Guid("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
        const int NEED_MORE = unchecked((int)0xC00D6D72);
        long time;

        public AacEncoder()
        {
            if (!M.started) { M.Check(M.MFStartup(0x20070, 0)); M.started = true; }
            mft = (M.IMFTransform)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_AAC));
            M.IMFMediaType o;
            M.Check(M.MFCreateMediaType(out o));
            o.SetGUID(MAJOR, Audio); o.SetGUID(SUB, AAC); o.SetUINT32(BITS, 16); o.SetUINT32(RATE, 48000);
            o.SetUINT32(CH, 2); o.SetUINT32(AVG, 24000); o.SetUINT32(PAYLOAD, 0);
            M.Check(mft.SetOutputType(0, o, 0));
            Marshal.ReleaseComObject(o);
            M.IMFMediaType i;
            M.Check(M.MFCreateMediaType(out i));
            i.SetGUID(MAJOR, Audio); i.SetGUID(SUB, PCM); i.SetUINT32(BITS, 16); i.SetUINT32(RATE, 48000);
            i.SetUINT32(CH, 2); i.SetUINT32(ALIGN, 4); i.SetUINT32(AVG, 192000);
            M.Check(mft.SetInputType(0, i, 0));
            Marshal.ReleaseComObject(i);
            mft.ProcessMessage(0x10000000, IntPtr.Zero);
            mft.ProcessMessage(0x10000003, IntPtr.Zero);
        }

        public void Encode(byte[] pcm, int len, Action<byte[], int> onFrame)
        {
            M.IMFSample s = M.MakeSample(pcm, len);
            s.SetSampleTime(time);
            time += (long)len / 4 * 10000000 / 48000;
            int hr = mft.ProcessInput(0, s, 0);
            Marshal.ReleaseComObject(s);
            if (hr < 0 && hr != unchecked((int)0xC00D36B5)) M.Check(hr);
            while (true)
            {
                M.MFT_OUTPUT_STREAM_INFO info;
                M.Check(mft.GetOutputStreamInfo(0, out info));
                M.IMFSample os;
                M.Check(M.MFCreateSample(out os));
                M.IMFMediaBuffer buf;
                M.Check(M.MFCreateMemoryBuffer(Math.Max(info.cbSize, 8192), out buf));
                os.AddBuffer(buf);
                IntPtr p = Marshal.GetIUnknownForObject(os);
                var od = new M.MFT_OUTPUT_DATA_BUFFER[1];
                od[0].pSample = p;
                int status;
                hr = mft.ProcessOutput(0, 1, od, out status);
                Marshal.Release(p);
                if (od[0].pEvents != IntPtr.Zero) Marshal.Release(od[0].pEvents);
                try
                {
                    if (hr == NEED_MORE) return;
                    M.Check(hr);
                    IntPtr data; int max, cur;
                    M.Check(buf.Lock(out data, out max, out cur));
                    var bytes = new byte[cur];
                    Marshal.Copy(data, bytes, 0, cur);
                    buf.Unlock();
                    if (cur > 0) onFrame(bytes, cur);
                }
                finally
                {
                    Marshal.ReleaseComObject(buf);
                    Marshal.ReleaseComObject(os);
                }
            }
        }

        public void Dispose() { if (mft != null) { try { Marshal.ReleaseComObject(mft); } catch { } mft = null; } }
    }
}
