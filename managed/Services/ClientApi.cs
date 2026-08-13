using Sowx.Framework;

namespace Sowx.Services;

// Small shared bridge to the client's message object, so services don't each
// re-resolve the send plumbing. The live CNWCMessage hangs off the app manager:
//   msg = CClientExoApp::GetNWCMessage(*g_pAppManager)
public static unsafe class ClientApi
{
    private static readonly IntPtr AppMgr = Native.Sym(SymbolMap.AppManager);
    private static readonly delegate* unmanaged<IntPtr, IntPtr> GetMsg =
        (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.GetNWCMessage);
    private static readonly delegate* unmanaged<IntPtr, byte, byte, byte*, uint, void> SendMsg =
        (delegate* unmanaged<IntPtr, byte, byte, byte*, uint, void>)Native.Sym(SymbolMap.SendPlayerToServerMessage);

    // A live CNWCMessage* captured from a hook that is handed one (the inbound message hook's `self`,
    // or the login hook's `this`). Preferred over resolving GetNWCMessage(*g_pAppManager): the hook
    // pointer is exactly the object the engine itself uses, so there is no symbol/offset guesswork.
    // The g_pAppManager path stays as a fallback for the (unlikely) case nothing has populated this.
    public static IntPtr LiveMessage;

    public static IntPtr Message()
    {
        if (LiveMessage != IntPtr.Zero) return LiveMessage;
        if (AppMgr == IntPtr.Zero || GetMsg == null) return IntPtr.Zero;
        IntPtr app = *(IntPtr*)AppMgr;
        return app == IntPtr.Zero ? IntPtr.Zero : GetMsg(app);
    }

    public static void Send(byte major, byte minor, byte* payload, uint len)
    {
        IntPtr msg = Message();
        if (msg != IntPtr.Zero && SendMsg != null) SendMsg(msg, major, minor, payload, len);
    }

    // Convenience: send an ASCII payload.
    // CNWCMessage::SendPlayerToServerMessage reserves the first 3 bytes of the buffer for its own
    // ['p'][major][minor] header (decomp-verified) and transmits the whole `len`. So we reserve 3
    // bytes up front, put the ASCII at offset 3, and pass 3 + payload as the length. Handing it the
    // raw payload lets the engine overwrite the first 3 bytes and send a truncated, minor-less runt
    // the server cannot route (this is why armed keypresses never reached the server).
    public static void SendAscii(byte major, byte minor, string payload)
    {
        byte[] ascii = System.Text.Encoding.ASCII.GetBytes(payload);
        byte[] buf = new byte[3 + ascii.Length];
        System.Array.Copy(ascii, 0, buf, 3, ascii.Length);
        fixed (byte* p = buf) Send(major, minor, p, (uint)buf.Length);
    }
}
