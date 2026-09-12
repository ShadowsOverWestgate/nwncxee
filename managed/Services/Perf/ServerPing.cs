using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Sowx.Framework;

namespace Sowx.Services.Perf;

/// <summary>
/// Round-trip time to the connected server, shown by the <c>/fps</c> overlay. The client keeps no live
/// latency figure -- its only ping machinery (<c>CNetLayerInternal::StartPing</c>) drives the server-browser
/// list and is indexed by enumerate slot, not by the connection -- so we measure it ourselves with the
/// engine's own connectionless probe: an 11-byte <c>BNLM</c> datagram, which every server echoes back as
/// <c>BNLR</c> regardless of connection state (verified against a live server). Our own UDP socket on a
/// background thread, so nothing touches the engine's netlayer or the frame.
/// <para>ponytail: through the master-server relay the client's server address is the relay's, so the number
/// is then the hop to the relay. Accepted; split it if relayed play ever matters.</para>
/// </summary>
internal static unsafe class ServerPing
{
    private const int PeriodMs = 1000, TimeoutMs = 2000;
    /// <summary>"BNLM" + our UDP port (u16, the receiver ignores it) + a tag byte + a tag u32, echoed back.</summary>
    private const int ProbeLen = 11;

    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    private static readonly delegate* unmanaged<IntPtr, IntPtr> _getNetLayer =
        (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.GetNetLayer);
    /// <summary>CNetLayer::GetServerNetworkAddress() returns by value: (CExoString* out, CNetLayer* this).
    /// <para>Linux order. Unreachable on Windows today -- <c>GetNetLayer</c> is a declared hole, so the gate
    /// below returns before this is called -- but whoever anchors it must swap the arguments: Win64 puts the
    /// sret pointer AFTER <c>this</c> (#420), and the callee, not the caller, destroys the string.</para></summary>
    private static readonly delegate* unmanaged<IntPtr, IntPtr, void> _serverAddress =
        (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.GetServerNetworkAddress);
    private static readonly delegate* unmanaged<IntPtr, void> _exoDtor =
        (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.ExoStringDtor);

    /// <summary>Server address packed as <c>(ipv4 &lt;&lt; 16) | port</c> -- one atomic long. 0 = no server.</summary>
    private static long _endpoint;
    private static Thread? _thread;

    /// <summary>Last measured round trip in ms, or -1 for "no number" (not connected, or no reply).</summary>
    internal static int LastMs = -1;

    /// <summary>Re-read the server address and start the prober on first call. Render thread, on the overlay's
    /// re-bake cadence -- the engine call heap-allocates a CExoString, so it must NOT run per frame.</summary>
    internal static void Refresh()
    {
        Interlocked.Exchange(ref _endpoint, ReadEndpoint());
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "sowx-ping" };
        _thread.Start();
    }

    /// <summary>*g_pAppManager -&gt; CClientExoApp -&gt; GetNetLayer() -&gt; GetServerNetworkAddress().</summary>
    private static long ReadEndpoint()
    {
        if (_appMgr == IntPtr.Zero || _getNetLayer == null || _serverAddress == null || _exoDtor == null) return 0;
        IntPtr mgr = *(IntPtr*)_appMgr; if (mgr == IntPtr.Zero) return 0;
        IntPtr app = *(IntPtr*)mgr;     if (app == IntPtr.Zero) return 0;
        IntPtr net = _getNetLayer(app); if (net == IntPtr.Zero) return 0;

        byte* exo = stackalloc byte[16];
        for (int i = 0; i < 16; i++) exo[i] = 0;   // the dtor below frees whatever buffer lands here
        _serverAddress((IntPtr)exo, net);
        long ep = Parse(*(byte**)exo, (int)*(uint*)(exo + 8));
        _exoDtor((IntPtr)exo);
        return ep;
    }

    /// <summary>Parse "a.b.c.d:port" -- the decompile leaves the halves' order unreadable, so the dotted half
    /// is taken as the address whichever side it is on. 0 = unparseable (main menu returns an empty string).</summary>
    private static long Parse(byte* s, int len)
    {
        if (s == null || len <= 0 || len > 64) return 0;
        int colon = -1;
        for (int i = 0; i < len; i++) if (s[i] == ':') { colon = i; break; }
        if (colon < 0) return 0;
        bool ipFirst = false;
        for (int i = 0; i < colon; i++) if (s[i] == '.') { ipFirst = true; break; }
        int ipAt = ipFirst ? 0 : colon + 1, ipEnd = ipFirst ? colon : len;
        int portAt = ipFirst ? colon + 1 : 0, portEnd = ipFirst ? len : colon;

        uint ip = 0; int octets = 0, v = -1;
        for (int i = ipAt; i <= ipEnd; i++)
        {
            if (i == ipEnd || s[i] == '.') { if (v < 0 || v > 255) return 0; ip = (ip << 8) | (uint)v; octets++; v = -1; }
            else if (s[i] >= '0' && s[i] <= '9') v = (v < 0 ? 0 : v) * 10 + (s[i] - '0');
            else return 0;
        }
        if (octets != 4) return 0;

        int port = 0;
        for (int i = portAt; i < portEnd; i++)
        {
            if (s[i] < '0' || s[i] > '9') return 0;
            port = port * 10 + (s[i] - '0');
            if (port > 65535) return 0;
        }
        return port == 0 ? 0 : ((long)ip << 16) | (uint)port;
    }

    /// <summary>Probe once a second for as long as the client is in a game. Background thread.</summary>
    private static void Loop()
    {
        Affinity.ClearThis();
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        { ReceiveTimeout = TimeoutMs };
        byte[] tx = new byte[ProbeLen], rx = new byte[64];
        tx[0] = (byte)'B'; tx[1] = (byte)'N'; tx[2] = (byte)'L'; tx[3] = (byte)'M';
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        var sw = new Stopwatch();
        long known = 0; IPEndPoint? server = null; uint tag = 0;

        while (true)
        {
            long ep = Interlocked.Read(ref _endpoint);
            if (ep == 0 || !GameGate.InGame)
            {
                LastMs = -1; known = 0; server = null;
                Thread.Sleep(PeriodMs); continue;
            }
            if (ep != known)
            {
                known = ep;
                uint ip = (uint)(ep >> 16);
                server = new IPEndPoint(
                    new IPAddress(new[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip }),
                    (int)(ep & 0xffff));
            }

            tag++;
            tx[6] = (byte)tag;
            BitConverter.TryWriteBytes(tx.AsSpan(7), tag);
            sw.Restart();
            try
            {
                sock.SendTo(tx, server!);
                while (true)
                {
                    int n = sock.ReceiveFrom(rx, ref any);
                    if (n >= ProbeLen && rx[0] == 'B' && rx[1] == 'N' && rx[2] == 'L' && rx[3] == 'R'
                        && BitConverter.ToUInt32(rx, 7) == tag) { LastMs = (int)sw.ElapsedMilliseconds; break; }
                }
            }
            catch (SocketException) { LastMs = -1; }
            Thread.Sleep(PeriodMs);
        }
    }

    /// <summary>Off-device check for the one piece of real logic here: the address parse.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        static long P(string s)
        {
            byte[] b = System.Text.Encoding.ASCII.GetBytes(s);
            fixed (byte* p = b) return Parse(p, b.Length);
        }
        long want = (10L << 40 | 1L << 32 | 2L << 24 | 3L << 16) | 5121;
        bool ok = P("10.1.2.3:5121") == want && P("5121:10.1.2.3") == want
               && P("") == 0 && P("nope") == 0 && P("10.1.2:5121") == 0
               && P("10.1.2.300:5121") == 0 && P("10.1.2.3:99999") == 0;
        if (!ok) log.Info("ServerPing.SelfCheck FAIL: address parse");
    }
}
