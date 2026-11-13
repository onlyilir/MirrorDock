using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MirrorDock
{
    // Decodes H.264 with the decoder built into Windows (Media Foundation), then converts NV12 to a Bitmap.
    sealed class H264Decoder : IDisposable
    {
        IMFTransform mft;
        int outW, outH, stride;     // decoded (padded) size and row pitch
        int matrix;                 // 1 = BT.709, else BT.601
        bool outputSet;
        long frameIndex;

        internal static bool started;
        static readonly Guid CLSID_H264Decoder = new Guid("62CE7E72-4C71-4d20-B15D-452831A87D9D");
        static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        static readonly Guid MF_MT_YUV_MATRIX = new Guid("3e23d450-2c75-4d25-a00e-b91670d12327");
        static readonly Guid MF_MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        static readonly Guid MF_LOW_LATENCY = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        static readonly Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        static readonly Guid MFVideoFormat_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");

        const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
        const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
        const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
        const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;

        public H264Decoder()
        {
            if (!started) { Check(MFStartup(0x20070, 0)); started = true; }
            mft = (IMFTransform)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_H264Decoder));
            IMFAttributes attrs;
            if (mft.GetAttributes(out attrs) == 0 && attrs != null) { attrs.SetUINT32(MF_LOW_LATENCY, 1); Marshal.ReleaseComObject(attrs); }

            IMFMediaType input;
            Check(MFCreateMediaType(out input));
            input.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            input.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
            input.SetUINT32(MF_MT_INTERLACE_MODE, 2); // progressive
            Check(mft.SetInputType(0, input, 0));
            Marshal.ReleaseComObject(input);
            SetOutputType();
            mft.ProcessMessage(0x10000000, IntPtr.Zero); // MFT_MESSAGE_NOTIFY_BEGIN_STREAMING
            mft.ProcessMessage(0x10000003, IntPtr.Zero); // MFT_MESSAGE_NOTIFY_START_OF_STREAM
        }

        void SetOutputType()
        {
            for (int i = 0; ; i++)
            {
                IMFMediaType t;
                int hr = mft.GetOutputAvailableType(0, i, out t);
                if (hr != 0) break;
                Guid sub;
                t.GetGUID(MF_MT_SUBTYPE, out sub);
                if (sub == MFVideoFormat_NV12)
                {
                    Check(mft.SetOutputType(0, t, 0));
                    long size;
                    if (t.GetUINT64(MF_MT_FRAME_SIZE, out size) == 0) { outW = (int)(size >> 32); outH = (int)(size & 0xFFFFFFFF); }
                    int st;
                    stride = t.GetUINT32(MF_MT_DEFAULT_STRIDE, out st) == 0 ? st : outW;
                    int m;
                    matrix = t.GetUINT32(MF_MT_YUV_MATRIX, out m) == 0 ? m : (outH >= 720 ? 1 : 2);
                    Marshal.ReleaseComObject(t);
                    outputSet = true;
                    return;
                }
                Marshal.ReleaseComObject(t);
            }
            outputSet = false;
        }

        // Feeds one access unit (Annex-B). Returns a frame when one is ready, drawn into 'reuse' if it fits.
        public Bitmap Decode(byte[] data, int length, int visibleW, int visibleH, Bitmap reuse)
        {
            IMFSample sample = MakeSample(data, length);
            sample.SetSampleTime(frameIndex * 166666);
            frameIndex++;
            int hr = mft.ProcessInput(0, sample, 0);
            Marshal.ReleaseComObject(sample);
            Bitmap result = null;
            if (hr == MF_E_NOTACCEPTING) { result = Drain(visibleW, visibleH, reuse); hr = mft.ProcessInput(0, MakeSample(data, length), 0); }
            if (hr != 0 && hr != MF_E_NOTACCEPTING) Check(hr);
            Bitmap more = Drain(visibleW, visibleH, result ?? reuse);
            return more ?? result;
        }

        Bitmap Drain(int vw, int vh, Bitmap reuse)
        {
            Bitmap last = null;
            while (true)
            {
                MFT_OUTPUT_STREAM_INFO info;
                Check(mft.GetOutputStreamInfo(0, out info));
                IMFSample outSample = null;
                IntPtr outPtr = IntPtr.Zero;
                if ((info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) == 0)
                {
                    Check(MFCreateSample(out outSample));
                    IMFMediaBuffer buf;
                    Check(MFCreateMemoryBuffer(Math.Max(info.cbSize, outW * outH * 3 / 2 + 4096), out buf));
                    outSample.AddBuffer(buf);
                    Marshal.ReleaseComObject(buf);
                    outPtr = Marshal.GetIUnknownForObject(outSample);
                }
                var od = new MFT_OUTPUT_DATA_BUFFER[1];
                od[0].pSample = outPtr;
                int status;
                int hr = mft.ProcessOutput(0, 1, od, out status);
                if (od[0].pEvents != IntPtr.Zero) Marshal.Release(od[0].pEvents);
                try
                {
                    if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return last;
                    if (hr == MF_E_TRANSFORM_STREAM_CHANGE) { SetOutputType(); continue; }
                    Check(hr);
                    IntPtr produced = od[0].pSample;
                    var s = (IMFSample)Marshal.GetObjectForIUnknown(produced);
                    last = ToBitmap(s, vw, vh, last ?? reuse);
                    Marshal.ReleaseComObject(s);
                    if (outPtr == IntPtr.Zero && produced != IntPtr.Zero) Marshal.Release(produced);
                }
                finally
                {
                    if (outPtr != IntPtr.Zero) Marshal.Release(outPtr);
                    if (outSample != null) Marshal.ReleaseComObject(outSample);
                }
            }
        }

        unsafe Bitmap ToBitmap(IMFSample s, int vw, int vh, Bitmap reuse)
        {
            if (!outputSet || outW == 0) return reuse;
            vw = Math.Min(vw, outW); vh = Math.Min(vh, outH);
            IMFMediaBuffer buf;
            Check(s.ConvertToContiguousBuffer(out buf));
            try
            {
                IntPtr p; int max, cur;
                Check(buf.Lock(out p, out max, out cur));
                try
                {
                    Bitmap bmp = reuse != null && reuse.Width == vw && reuse.Height == vh ? reuse : new Bitmap(vw, vh, PixelFormat.Format32bppRgb);
                    BitmapData bd = bmp.LockBits(new Rectangle(0, 0, vw, vh), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    try { Nv12ToRgb((byte*)p, stride, outH, (byte*)bd.Scan0, bd.Stride, vw, vh, matrix == 1); }
                    finally { bmp.UnlockBits(bd); }
                    return bmp;
                }
                finally { buf.Unlock(); }
            }
            finally { Marshal.ReleaseComObject(buf); }
        }

        static int[] yTab, rV, gU, gV, bU;
        static bool tabs709;

        static void BuildTables(bool bt709)
        {
            if (yTab != null && tabs709 == bt709) return;
            tabs709 = bt709;
            yTab = new int[256]; rV = new int[256]; gU = new int[256]; gV = new int[256]; bU = new int[256];
            double kr = bt709 ? 1.5748 : 1.402, kgu = bt709 ? 0.1873 : 0.344136, kgv = bt709 ? 0.4681 : 0.714136, kb = bt709 ? 1.8556 : 1.772;
            for (int i = 0; i < 256; i++)
            {
                yTab[i] = (int)(1.164383 * (i - 16) * 1024);
                double c = (i - 128) * 1.138393; // limited-range chroma scale (224 -> 255)
                rV[i] = (int)(kr * c * 1024); gU[i] = (int)(kgu * c * 1024); gV[i] = (int)(kgv * c * 1024); bU[i] = (int)(kb * c * 1024);
            }
        }

        static unsafe void Nv12ToRgb(byte* src, int srcStride, int paddedH, byte* dst, int dstStride, int w, int h, bool bt709)
        {
            BuildTables(bt709);
            byte* uvPlane = src + srcStride * paddedH;
            fixed (int* yt = yTab, rv = rV, gu = gU, gv = gV, bu = bU)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* yRow = src + y * srcStride;
                    byte* uv = uvPlane + (y >> 1) * srcStride;
                    uint* o = (uint*)(dst + y * dstStride);
                    for (int x = 0; x < w; x += 2)
                    {
                        int u = uv[x], v = uv[x + 1];
                        int r0 = rv[v], g0 = gu[u] + gv[v], b0 = bu[u];
                        int Y = yt[yRow[x]];
                        o[x] = Pack(Y + r0, Y - g0, Y + b0);
                        if (x + 1 < w) { Y = yt[yRow[x + 1]]; o[x + 1] = Pack(Y + r0, Y - g0, Y + b0); }
                    }
                }
            }
        }

        static uint Pack(int r, int g, int b)
        {
            r >>= 10; g >>= 10; b >>= 10;
            if ((uint)r > 255) r = r < 0 ? 0 : 255;
            if ((uint)g > 255) g = g < 0 ? 0 : 255;
            if ((uint)b > 255) b = b < 0 ? 0 : 255;
            return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }

        internal static IMFSample MakeSample(byte[] data, int length)
        {
            IMFSample s;
            Check(MFCreateSample(out s));
            IMFMediaBuffer buf;
            Check(MFCreateMemoryBuffer(length, out buf));
            IntPtr p; int max, cur;
            Check(buf.Lock(out p, out max, out cur));
            Marshal.Copy(data, 0, p, length);
            buf.Unlock();
            buf.SetCurrentLength(length);
            s.AddBuffer(buf);
            Marshal.ReleaseComObject(buf);
            return s;
        }

        internal static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

        public void Dispose()
        {
            if (mft != null) { try { Marshal.ReleaseComObject(mft); } catch { } mft = null; }
        }

        // ------------------------------------------------------------ Media Foundation interop
        [DllImport("mfplat.dll")] internal static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] internal static extern int MFCreateMediaType(out IMFMediaType t);
        [DllImport("mfplat.dll")] internal static extern int MFCreateSample(out IMFSample s);
        [DllImport("mfplat.dll")] internal static extern int MFCreateMemoryBuffer(int max, out IMFMediaBuffer b);

        [StructLayout(LayoutKind.Sequential)]
        internal struct MFT_OUTPUT_STREAM_INFO { public int dwFlags; public int cbSize; public int cbAlignment; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MFT_OUTPUT_DATA_BUFFER { public int dwStreamID; public IntPtr pSample; public int dwStatus; public IntPtr pEvents; }

        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFAttributes
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare();
            [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out int v);
            [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out long v);
            void GetDouble();
            [PreserveSig] int GetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out Guid v);
            void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
            void SetItem(); void DeleteItem(); void DeleteAllItems();
            [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int v);
            [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, long v);
            void SetDouble();
            [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid v);
            void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFMediaType
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare();
            [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out int v);
            [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out long v);
            void GetDouble();
            [PreserveSig] int GetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out Guid v);
            void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
            void SetItem(); void DeleteItem(); void DeleteAllItems();
            [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int v);
            [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, long v);
            void SetDouble();
            [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid v);
            void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
            void GetMajorType(); void IsCompressedFormat(); void IsEqual(); void GetRepresentation(); void FreeRepresentation();
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFSample
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64(); void GetDouble(); void GetGUID();
            void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
            void SetItem(); void DeleteItem(); void DeleteAllItems(); void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID();
            void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
            void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime();
            [PreserveSig] int SetSampleTime(long t);
            void GetSampleDuration(); void SetSampleDuration(); void GetBufferCount(); void GetBufferByIndex();
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer b);
            [PreserveSig] int AddBuffer(IMFMediaBuffer b);
            void RemoveBufferByIndex(); void RemoveAllBuffers(); void GetTotalLength(); void CopyToBuffer();
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr p, out int max, out int cur);
            [PreserveSig] int Unlock();
            void GetCurrentLength();
            [PreserveSig] int SetCurrentLength(int len);
            void GetMaxLength();
        }

        [ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFTransform
        {
            void GetStreamLimits(); void GetStreamCount(); void GetStreamIDs(); void GetInputStreamInfo();
            [PreserveSig] int GetOutputStreamInfo(int id, out MFT_OUTPUT_STREAM_INFO info);
            [PreserveSig] int GetAttributes(out IMFAttributes a);
            void GetInputStreamAttributes(); void GetOutputStreamAttributes(); void DeleteInputStream(); void AddInputStreams();
            void GetInputAvailableType();
            [PreserveSig] int GetOutputAvailableType(int id, int index, out IMFMediaType t);
            [PreserveSig] int SetInputType(int id, IMFMediaType t, int flags);
            [PreserveSig] int SetOutputType(int id, IMFMediaType t, int flags);
            void GetInputCurrentType(); void GetOutputCurrentType(); void GetInputStatus(); void GetOutputStatus(); void SetOutputBounds(); void ProcessEvent();
            [PreserveSig] int ProcessMessage(int msg, IntPtr param);
            [PreserveSig] int ProcessInput(int id, IMFSample s, int flags);
            [PreserveSig] int ProcessOutput(int flags, int count, [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] MFT_OUTPUT_DATA_BUFFER[] buffers, out int status);
        }
    }
}
