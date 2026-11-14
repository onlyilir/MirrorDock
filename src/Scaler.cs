using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MirrorDock
{
    // Resizes video frames on the video thread, so the window only has to copy a ready-sized picture.
    // Smooth bilinear filtering; big shrinks first halve the picture (2x2 average) so fine text doesn't shimmer.
    // GDI+'s own scaling took 17-60 ms per frame on the UI thread, which made the window lag.
    sealed class Scaler : IDisposable
    {
        // Two scratch buffers in unmanaged memory (they never move, so raw pointers into them stay valid).
        IntPtr bufA = IntPtr.Zero, bufB = IntPtr.Zero;
        int sizeA, sizeB;
        int[] x0 = new int[0], fx = new int[0];

        // Returns 'reuse' (or a new bitmap) holding 'src' resized to 'size'. Never returns 'src' itself.
        public Bitmap Resize(Bitmap src, Size size, Bitmap reuse)
        {
            int dw = Math.Max(1, size.Width), dh = Math.Max(1, size.Height);
            Bitmap dst = reuse != null && reuse.Width == dw && reuse.Height == dh ? reuse : null;
            if (dst == null) { if (reuse != null) reuse.Dispose(); dst = new Bitmap(dw, dh, PixelFormat.Format32bppPArgb); }
            int sw = src.Width, sh = src.Height;
            BitmapData sd = src.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            BitmapData dd = dst.LockBits(new Rectangle(0, 0, dw, dh), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { Run(sd.Scan0, sw, sh, sd.Stride, dd.Scan0, dw, dh, dd.Stride); }
            finally { src.UnlockBits(sd); dst.UnlockBits(dd); }
            return dst;
        }

        unsafe void Run(IntPtr srcPtr, int sw, int sh, int sStride, IntPtr dstPtr, int dw, int dh, int dStride)
        {
            byte* s = (byte*)srcPtr;
            if (sw == dw && sh == dh)
            {
                for (int y = 0; y < sh; y++) Copy(s + y * sStride, (byte*)dstPtr + y * dStride, sw * 4);
                return;
            }
            bool useA = true;
            while (sw >= dw * 2 && sh >= dh * 2 && sw >= 4 && sh >= 4)
            {
                int hw = sw / 2, hh = sh / 2;
                byte* o = (byte*)Buffer(useA, hw * hh * 4);
                Halve(s, sw, sh, sStride, o);
                s = o; sw = hw; sh = hh; sStride = hw * 4;
                useA = !useA;
            }
            Bilinear(s, sw, sh, sStride, (byte*)dstPtr, dw, dh, dStride);
        }

        IntPtr Buffer(bool a, int bytes)
        {
            if (a)
            {
                if (sizeA < bytes) { if (bufA != IntPtr.Zero) Marshal.FreeHGlobal(bufA); bufA = Marshal.AllocHGlobal(bytes); sizeA = bytes; }
                return bufA;
            }
            if (sizeB < bytes) { if (bufB != IntPtr.Zero) Marshal.FreeHGlobal(bufB); bufB = Marshal.AllocHGlobal(bytes); sizeB = bytes; }
            return bufB;
        }

        static unsafe void Copy(byte* a, byte* b, int n)
        {
            long* la = (long*)a, lb = (long*)b;
            int words = n / 8;
            for (int i = 0; i < words; i++) lb[i] = la[i];
            for (int i = words * 8; i < n; i++) b[i] = a[i];
        }

        // 2x2 box average into a tightly packed buffer of (sw/2)x(sh/2).
        static unsafe void Halve(byte* s, int sw, int sh, int sStride, byte* o)
        {
            int hw = sw / 2, hh = sh / 2;
            for (int y = 0; y < hh; y++)
            {
                uint* r0 = (uint*)(s + (2 * y) * sStride), r1 = (uint*)(s + (2 * y + 1) * sStride);
                uint* d = (uint*)(o + y * hw * 4);
                for (int x = 0; x < hw; x++)
                {
                    uint a = r0[2 * x], b = r0[2 * x + 1], c = r1[2 * x], e = r1[2 * x + 1];
                    uint rb = (((a & 0xFF00FF) + (b & 0xFF00FF) + (c & 0xFF00FF) + (e & 0xFF00FF) + 0x020002) >> 2) & 0xFF00FF;
                    uint g = (((a & 0x00FF00) + (b & 0x00FF00) + (c & 0x00FF00) + (e & 0x00FF00) + 0x000200) >> 2) & 0x00FF00;
                    d[x] = 0xFF000000u | rb | g;
                }
            }
        }

        unsafe void Bilinear(byte* s, int sw, int sh, int sStride, byte* d, int dw, int dh, int dStride)
        {
            if (x0.Length < dw) { x0 = new int[dw]; fx = new int[dw]; }
            double kx = (double)sw / dw, ky = (double)sh / dh;
            for (int x = 0; x < dw; x++)
            {
                double gx = (x + 0.5) * kx - 0.5;
                if (gx < 0) gx = 0;
                int ix = (int)gx;
                if (ix >= sw - 1) { ix = Math.Max(0, sw - 2); gx = sw - 1; }
                x0[x] = ix;
                fx[x] = sw == 1 ? 0 : Math.Min(256, (int)((gx - ix) * 256));
            }
            fixed (int* xs = x0, fs = fx)
            {
                for (int y = 0; y < dh; y++)
                {
                    double gy = (y + 0.5) * ky - 0.5;
                    if (gy < 0) gy = 0;
                    int iy = (int)gy;
                    if (iy >= sh - 1) { iy = Math.Max(0, sh - 2); gy = sh - 1; }
                    uint fy = sh == 1 ? 0u : (uint)Math.Min(256, (int)((gy - iy) * 256));
                    uint* r0 = (uint*)(s + iy * sStride);
                    uint* r1 = sh == 1 ? r0 : (uint*)(s + (iy + 1) * sStride);
                    uint* o = (uint*)(d + y * dStride);
                    for (int x = 0; x < dw; x++)
                    {
                        int i = xs[x];
                        int j = sw == 1 ? i : i + 1;
                        uint f = (uint)fs[x];
                        o[x] = Lerp(Lerp(r0[i], r0[j], f), Lerp(r1[i], r1[j], f), fy);
                    }
                }
            }
        }

        static uint Lerp(uint a, uint b, uint f)
        {
            uint nf = 256 - f;
            uint rb = (((a & 0xFF00FF) * nf + (b & 0xFF00FF) * f) >> 8) & 0xFF00FF;
            uint g = (((a & 0x00FF00) * nf + (b & 0x00FF00) * f) >> 8) & 0x00FF00;
            return 0xFF000000u | rb | g;
        }

        public void Dispose()
        {
            if (bufA != IntPtr.Zero) { Marshal.FreeHGlobal(bufA); bufA = IntPtr.Zero; sizeA = 0; }
            if (bufB != IntPtr.Zero) { Marshal.FreeHGlobal(bufB); bufB = IntPtr.Zero; sizeB = 0; }
        }
    }
}
