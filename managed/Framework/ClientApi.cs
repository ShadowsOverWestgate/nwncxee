namespace Sowx.Framework;

/// <summary>
/// Small shared bridge to the client's message object, so services don't each re-resolve
/// the send plumbing. Lives in the framework layer: cross-cutting plumbing every feature calls,
/// not a feature of its own.
/// </summary>
public static unsafe class ClientApi
{
    private static readonly IntPtr AppMgr = Native.Sym(SymbolMap.AppManager);
    private static readonly delegate* unmanaged<IntPtr, IntPtr> GetMsg =
        (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.GetNWCMessage);
    private static readonly delegate* unmanaged<IntPtr, byte, byte, byte*, uint, void> SendMsg =
        (delegate* unmanaged<IntPtr, byte, byte, byte*, uint, void>)Native.Sym(SymbolMap.SendPlayerToServerMessage);

    /// <summary>
    /// A live CNWCMessage* captured from a hook that is handed one (inbound `self` / login `this`).
    /// Preferred over resolving GetNWCMessage(*g_pAppManager) -- exactly the engine's own object, no
    /// symbol/offset guesswork. The g_pAppManager path (which segfaulted on this build) is fallback only.
    /// </summary>
    public static IntPtr LiveMessage;

    /// <summary>A live CClientExoApp* captured from the focus hooks (their `this`). A process-lifetime
    /// singleton, so one capture holds; lets services walk to the live in-game GUI without the flaky
    /// g_pAppManager global.</summary>
    public static IntPtr LiveExoApp;

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

    /// <summary>
    /// Send an ASCII payload. Footgun: SendPlayerToServerMessage reserves the first 3 bytes for its
    /// own ['p'][major][minor] header and transmits the whole len, so we reserve 3 bytes up front and
    /// put the ASCII at offset 3 -- handing it the raw payload sends a minor-less runt the server drops.
    /// </summary>
    public static void SendAscii(byte major, byte minor, string payload)
    {
        byte[] ascii = System.Text.Encoding.ASCII.GetBytes(payload);
        byte[] buf = new byte[3 + ascii.Length];
        System.Array.Copy(ascii, 0, buf, 3, ascii.Length);
        fixed (byte* p = buf) Send(major, minor, p, (uint)buf.Length);
    }
}
