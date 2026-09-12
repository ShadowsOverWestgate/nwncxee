using System;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>Per-face normals welded into smooth ones, once per newly enrolled gob part (#144 retry).
/// NWN heads and many placeables ship every triangle with its own three vertices and a flat normal, so a
/// terminator on them is a solid wedge no wrap width can soften (<c>/draw viz 7</c> shows the wedges).
/// The weld runs on the engine's permanent host shadow -- the bytes #260's upload funnel already pushes --
/// so there is no GPU readback; the one GL call is a sub-data upload of the touched vertex span.
/// <para>Crease-preserving: a vertex takes the area-weighted sum of every face at its bitwise-equal
/// position whose normal is within <see cref="Angle"/> degrees of its own; faces past the threshold are
/// left out, so a box corner stays a box corner. Idempotent, so a shared range welded twice is unchanged.</para>
/// <para>Windows: no new symbol or offset -- every read is a GL query or a pool/VDO row already resolved on
/// both platforms (#447 anchors the pool table on each). A null host is still counted refused, not skipped.</para>
/// <para>ponytail: idempotence is exact only while a fold's face membership is stable; a shared range welded
/// twice near the threshold can drift by one face. Track welded ranges by (vdo,item) if it ever shows.</para></summary>
internal static unsafe class NormalWeld
{
    /// <summary>Crease threshold in degrees; 0 disables. <c>/draw weld</c>. Applies to parts enrolled after
    /// the change, so a re-tune shows on the next area load or relogin.</summary>
    internal static float Angle = 60f;

    /// <summary>Corner ceiling per part: ushort indices bound a part to 65536 distinct vertices, and a gob
    /// part's index count sits far under this. A part over it is left alone and counted.</summary>
    private const int MaxCorners = 1 << 17;
    private const int Slots = 1 << 18;   // open addressing at <= 50% load
    private const int MaxVerts = 1 << 16;

    private static long[] _key = null!;
    private static float[] _kx = null!, _ky = null!, _kz = null!;
    private static int[] _slotGen = null!, _head = null!, _next = null!, _cornerTri = null!, _vertGen = null!;
    private static float[] _fn = null!;
    private static int _gen;
    private static ExtenderLog? _log;
    private static int _logLeft = 12;

    internal static int Parts, Welded, Kept, Refused;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _key = new long[Slots]; _kx = new float[Slots]; _ky = new float[Slots]; _kz = new float[Slots];
        _slotGen = new int[Slots]; _head = new int[Slots];
        _next = new int[MaxCorners]; _cornerTri = new int[MaxCorners];
        _fn = new float[MaxCorners];   // 3 floats per tri, tris <= MaxCorners/3
        _vertGen = new int[MaxVerts];
    }

    /// <summary>Weld one part's range. <paramref name="elemHost"/>/<paramref name="vertHost"/> are the
    /// pools' host shadows; the vertex span touched is re-uploaded to <paramref name="nrm"/>'s buffer.</summary>
    internal static void Run(byte* elemHost, byte* vertHost, long first, int count, int baseVertex,
                             in VertexStream pos, in VertexStream nrm)
    {
        if (Angle <= 0f || _key == null || count < 3 || count > MaxCorners) { Refused++; return; }
        if (pos.Type != Gl.FLOAT || pos.Size != 3 || pos.Buf != nrm.Buf) { Refused++; return; }
        ushort* idx = (ushort*)(elemHost + first * 2);
        int minI = int.MaxValue, maxI = -1;
        for (int i = 0; i < count; i++) { int v = idx[i]; if (v < minI) minI = v; if (v > maxI) maxI = v; }
        if (maxI - minI >= MaxVerts) { Refused++; return; }
        int gen = ++_gen;
        int tris = count / 3;
        // pass 1: face normals (area-weighted) + cluster corners by exact position
        for (int t = 0; t < tris; t++)
        {
            float* p0 = P(vertHost, pos, baseVertex + idx[t * 3]);
            float* p1 = P(vertHost, pos, baseVertex + idx[t * 3 + 1]);
            float* p2 = P(vertHost, pos, baseVertex + idx[t * 3 + 2]);
            float ax = p1[0] - p0[0], ay = p1[1] - p0[1], az = p1[2] - p0[2];
            float bx = p2[0] - p0[0], by = p2[1] - p0[1], bz = p2[2] - p0[2];
            float fx = ay * bz - az * by, fy = az * bx - ax * bz, fz = ax * by - ay * bx;
            // winding-indifferent: sign the face by its own corners' authored normals, not by a guess at
            // the engine's winding -- a mesh that winds the other way would otherwise weld nothing, silently
            float* n0 = P(vertHost, nrm, baseVertex + idx[t * 3]);
            float* n1 = P(vertHost, nrm, baseVertex + idx[t * 3 + 1]);
            float* n2 = P(vertHost, nrm, baseVertex + idx[t * 3 + 2]);
            if (fx * (n0[0] + n1[0] + n2[0]) + fy * (n0[1] + n1[1] + n2[1]) + fz * (n0[2] + n1[2] + n2[2]) < 0f)
            { fx = -fx; fy = -fy; fz = -fz; }
            _fn[t * 3] = fx; _fn[t * 3 + 1] = fy; _fn[t * 3 + 2] = fz;
            for (int c = 0; c < 3; c++)
            {
                int corner = t * 3 + c;
                float* p = c == 0 ? p0 : c == 1 ? p1 : p2;
                int s = Slot(p, gen);
                _cornerTri[corner] = t; _next[corner] = _head[s]; _head[s] = corner;
            }
        }
        // pass 2: each vertex sums the faces at its position within the crease threshold of its own normal
        float cosT = MathF.Cos(Angle * MathF.PI / 180f);
        int welded = 0, kept = 0;
        for (int i = 0; i < count; i++)
        {
            int v = idx[i];
            if (_vertGen[v - minI] == gen) continue;
            _vertGen[v - minI] = gen;
            float* p = P(vertHost, pos, baseVertex + v);
            float* n = P(vertHost, nrm, baseVertex + v);
            float nl = MathF.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            if (nl < 1e-8f) { kept++; continue; }
            float sx = 0, sy = 0, sz = 0; int faces = 0;
            for (int c = _head[Slot(p, gen)]; c >= 0; c = _next[c])
            {
                int t = _cornerTri[c] * 3;
                float fx = _fn[t], fy = _fn[t + 1], fz = _fn[t + 2];
                float fl = MathF.Sqrt(fx * fx + fy * fy + fz * fz);
                if (fl < 1e-12f) continue;
                if ((fx * n[0] + fy * n[1] + fz * n[2]) / (fl * nl) < cosT) continue;
                sx += fx; sy += fy; sz += fz; faces++;
            }
            float sl = MathF.Sqrt(sx * sx + sy * sy + sz * sz);
            if (faces < 2 || sl < 1e-12f) { kept++; continue; }
            n[0] = sx / sl; n[1] = sy / sl; n[2] = sz / sl; welded++;
        }
        Parts++; Welded += welded; Kept += kept;
        if (welded == 0) return;
        long off = nrm.Off + (long)(baseVertex + minI) * nrm.Stride;
        long span = (long)(maxI - minI) * nrm.Stride + nrm.Size * 4;
        Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, nrm.Buf);
        Gl.BufferSubData(Gl.COPY_WRITE_BUFFER, (IntPtr)off, (IntPtr)span, vertHost + off);
        Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, 0);
        if (_logLeft-- > 0)
            _log?.Info($"NormalWeld: {welded} welded / {kept} kept of {maxI - minI + 1} verts ({tris} tris, {Angle:F0} deg)");
    }

    private static float* P(byte* host, in VertexStream s, int v) => (float*)(host + s.Off + (long)v * s.Stride);

    /// <summary>Slot for a bitwise-equal position; a fresh one is claimed with an empty corner chain.</summary>
    private static int Slot(float* p, int gen)
    {
        int bx = BitConverter.SingleToInt32Bits(p[0]), by = BitConverter.SingleToInt32Bits(p[1]), bz = BitConverter.SingleToInt32Bits(p[2]);
        long k = ((long)bx * 0x9E3779B1L) ^ ((long)by * 0x85EBCA77L) ^ ((long)bz * 0xC2B2AE3DL);
        int s = (int)(k ^ (k >> 29)) & (Slots - 1);
        while (true)
        {
            if (_slotGen[s] != gen)
            { _slotGen[s] = gen; _key[s] = k; _kx[s] = p[0]; _ky[s] = p[1]; _kz[s] = p[2]; _head[s] = -1; return s; }
            if (_key[s] == k && _kx[s] == p[0] && _ky[s] == p[1] && _kz[s] == p[2]) return s;
            s = (s + 1) & (Slots - 1);
        }
    }

    /// <summary>Off-device: two flat-shaded triangles meeting at a 20-degree fold (six vertices, two per shared
    /// position) weld to one blended normal on the shared edge, leave the unshared corners alone, and a
    /// second pass changes nothing.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        if (_key == null) Attach(log);
        // pos(3) + nrm(3) interleaved, stride 24; quad in XY tilted: tri A normal +Z, tri B normal rotated 20deg
        float[] verts = {
            0,0,0, 0,0,1,  1,0,0, 0,0,1,  0,1,0, 0,0,1,          // tri A: flat +Z
            1,0,0, 0,-0.342f,0.94f,  1,1,0.364f, 0,-0.342f,0.94f,  0,1,0, 0,-0.342f,0.94f };  // tri B: tilted 20deg
        ushort[] idx = { 0, 1, 2, 3, 4, 5 };
        var pos = new VertexStream { Buf = 1, Size = 3, Type = Gl.FLOAT, Stride = 24, Off = 0 };
        var nrm = new VertexStream { Buf = 1, Size = 3, Type = Gl.FLOAT, Stride = 24, Off = 12 };
        bool ok = true;
        fixed (float* v = verts) fixed (ushort* e = idx)
        {
            var bind = Gl.BindBuffer; var sub = Gl.BufferSubData;
            Gl.BindBuffer = &NoBind; Gl.BufferSubData = &NoSub;
            float save = Angle; int logLeft = _logLeft;
            Angle = 60f; Run((byte*)e, (byte*)v, 0, 6, 0, pos, nrm);
            // shared verts (1,0,0) and (0,1,0) now carry the same blended normal on both triangles
            ok &= MathF.Abs(verts[1 * 6 + 5] - verts[3 * 6 + 5]) < 1e-6f && verts[1 * 6 + 4] < -0.1f;
            ok &= verts[0 * 6 + 5] == 1f;                                   // unshared corner untouched
            float once = verts[1 * 6 + 5];
            Run((byte*)e, (byte*)v, 0, 6, 0, pos, nrm);                     // idempotent on the welded result
            ok &= verts[1 * 6 + 5] == once;
            Angle = save; Gl.BindBuffer = bind; Gl.BufferSubData = sub;
            _logLeft = logLeft; Parts = Welded = Kept = Refused = 0;
        }
        log.Info(ok ? "NormalWeld.SelfCheck: passed" : "NormalWeld.SelfCheck FAIL");
        return ok;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly] private static void NoBind(uint a, uint b) { }
    [System.Runtime.InteropServices.UnmanagedCallersOnly] private static void NoSub(uint a, IntPtr b, IntPtr c, void* d) { }
}
