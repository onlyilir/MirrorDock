package com.mirrordock;

import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.os.Build;

import java.io.DataOutputStream;
import java.io.IOException;

/** Captures what the phone is playing (Android 11+) and streams raw PCM, 48 kHz stereo 16-bit. */
final class AudioCapture {
    static final int RATE = 48000;

    static void startThread(final DataOutputStream out) {
        Thread t = new Thread(new Runnable() {
            @Override public void run() { loop(out); }
        }, "audio");
        t.setDaemon(true);
        t.start();
    }

    static void loop(DataOutputStream out) {
        AudioRecord rec = null;
        try {
            try {
                rec = create();
                rec.startRecording();
            } catch (Throwable t) {
                Server.log("ERROR: sound capture unavailable: " + t);
                out.writeBytes("MDA1");
                out.writeByte(0);
                out.flush();
                return;
            }
            out.writeBytes("MDA1");
            out.writeByte(1);
            out.flush();
            Server.log("Sound capture started");
            byte[] buf = new byte[RATE * 4 / 100];   // 10 ms
            while (Server.running) {
                int n = rec.read(buf, 0, buf.length);
                if (n < 0) { Server.log("ERROR: sound read failed (" + n + ")"); break; }
                if (n == 0) continue;
                out.writeInt(n);
                out.write(buf, 0, n);
                out.flush();
            }
        } catch (IOException e) {
            // PC went away
        } finally {
            if (rec != null) { try { rec.stop(); } catch (Exception ignored) { } rec.release(); }
        }
    }

    static AudioRecord create() throws Exception {
        if (Build.VERSION.SDK_INT < 30) throw new Exception("sound needs Android 11 or newer");
        AudioFormat fmt = new AudioFormat.Builder()
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                .setSampleRate(RATE)
                .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                .build();
        int min = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT);
        AudioRecord.Builder b = new AudioRecord.Builder()
                .setAudioSource(MediaRecorder.AudioSource.REMOTE_SUBMIX)
                .setAudioFormat(fmt)
                .setBufferSizeInBytes(Math.max(min * 4, RATE * 4 / 5));
        if (Build.VERSION.SDK_INT >= 31) b.setContext(FakeContext.get());
        AudioRecord r = b.build();
        if (r.getState() != AudioRecord.STATE_INITIALIZED) { r.release(); throw new Exception("AudioRecord did not initialize"); }
        return r;
    }
}
