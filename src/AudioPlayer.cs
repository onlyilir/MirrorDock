using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MirrorDock
{
    // Plays raw PCM (48 kHz, stereo, 16-bit) from the phone with the Windows waveOut API. Keeps latency low by dropping
    // audio when too much is queued.
    sealed class AudioPlayer : IDisposable
    {
        IntPtr wave;
        readonly Queue<Block> queued = new Queue<Block>();
        const int MaxQueuedMs = 160;
        int queuedBytes;
        public float Volume = 1f;

        class Block { public IntPtr hdr, data; public int len; }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX { public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public short nBlockAlign, wBitsPerSample, cbSize; }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR { public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public int dwFlags, dwLoops; public IntPtr lpNext, reserved; }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX f, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        static readonly int HdrSize = Marshal.SizeOf(typeof(WAVEHDR));
        const int WHDR_DONE = 1;
        const int BytesPerMs = 48000 * 4 / 1000;

        public AudioPlayer()
        {
            var f = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = 48000, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = 48000 * 4 };
            int r = waveOutOpen(out wave, -1, ref f, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) throw new Exception("Couldn't open the PC's speakers (waveOut error " + r + ").");
        }

        public void Play(byte[] pcm, int len)
        {
            if (wave == IntPtr.Zero) return;
            Recycle();
            if (queuedBytes > MaxQueuedMs * BytesPerMs) return; // running behind: drop to stay in sync with the picture
            if (Volume < 0.999f) ApplyVolume(pcm, len);
            var b = new Block { len = len };
            b.data = Marshal.AllocHGlobal(len);
            Marshal.Copy(pcm, 0, b.data, len);
            b.hdr = Marshal.AllocHGlobal(HdrSize);
            var h = new WAVEHDR { lpData = b.data, dwBufferLength = len };
            Marshal.StructureToPtr(h, b.hdr, false);
            waveOutPrepareHeader(wave, b.hdr, HdrSize);
            waveOutWrite(wave, b.hdr, HdrSize);
            queued.Enqueue(b);
            queuedBytes += len;
        }

        void ApplyVolume(byte[] pcm, int len)
        {
            for (int i = 0; i + 1 < len; i += 2)
            {
                int s = (short)(pcm[i] | (pcm[i + 1] << 8));
                s = (int)(s * Volume);
                pcm[i] = (byte)s; pcm[i + 1] = (byte)(s >> 8);
            }
        }

        void Recycle()
        {
            while (queued.Count > 0)
            {
                Block b = queued.Peek();
                var h = (WAVEHDR)Marshal.PtrToStructure(b.hdr, typeof(WAVEHDR));
                if ((h.dwFlags & WHDR_DONE) == 0) break;
                queued.Dequeue();
                Free(b);
            }
        }

        void Free(Block b)
        {
            waveOutUnprepareHeader(wave, b.hdr, HdrSize);
            Marshal.FreeHGlobal(b.hdr);
            Marshal.FreeHGlobal(b.data);
            queuedBytes -= b.len;
        }

        public void Dispose()
        {
            if (wave == IntPtr.Zero) return;
            waveOutReset(wave);
            while (queued.Count > 0) Free(queued.Dequeue());
            waveOutClose(wave);
            wave = IntPtr.Zero;
        }
    }
}
