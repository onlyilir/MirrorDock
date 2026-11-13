package android.content;

// Compile-time stand-in for a hidden framework interface. Not shipped: the phone's real class is used at runtime.
public interface IOnPrimaryClipChangedListener extends android.os.IInterface {
    void dispatchPrimaryClipChanged() throws android.os.RemoteException;

    abstract class Stub extends android.os.Binder implements IOnPrimaryClipChangedListener {
        public Stub() { }
        @Override public android.os.IBinder asBinder() { return this; }
    }
}
