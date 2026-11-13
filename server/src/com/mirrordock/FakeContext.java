package com.mirrordock;

import android.annotation.TargetApi;
import android.content.AttributionSource;
import android.content.Context;
import android.content.ContextWrapper;
import android.os.Process;

/** A Context that says it is the adb shell package, so permission checks match the shell user we run as. */
final class FakeContext extends ContextWrapper {
    static final String PACKAGE = "com.android.shell";
    private static FakeContext instance;

    static synchronized FakeContext get() {
        if (instance == null) instance = new FakeContext();
        return instance;
    }

    private FakeContext() {
        super(Workarounds.systemContext);
    }

    @Override public String getPackageName() { return PACKAGE; }
    @Override public String getOpPackageName() { return PACKAGE; }
    @Override public Context getApplicationContext() { return this; }

    @TargetApi(31)
    @Override public AttributionSource getAttributionSource() {
        return new AttributionSource.Builder(Process.SHELL_UID).setPackageName(PACKAGE).build();
    }

    @Override public int getDeviceId() { return 0; }
}
