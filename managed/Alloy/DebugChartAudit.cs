using System;
using System.Numerics;
using CX.Graphics;

namespace CX.Alloy;

// [DEBUG-cx25] throwaway: the garbage-mesh-after-module-hop audit. Delete this file and its one call in Frame.cs.

internal static unsafe partial class MeshChart
{
    internal static int DebugEpoch => _epoch;
    internal static int DebugUsedVerts => _vUsed;
    internal static int DebugUsedIndices => _iUsed;
    internal static int DebugUploadedVerts => _vUploaded;
    internal static int DebugUploadedIndices => _iUploaded;

    internal static bool DebugEntry(int slot, int item, out ulong sig, out int bse, out int nv, out Vector3 bmin, out Vector3 bmax,
                                    out nint off0, out int cnt0, out nint off1, out int cnt1, out nint off2, out int cnt2)
    {
        sig = 0; bse = nv = cnt0 = cnt1 = cnt2 = 0; off0 = off1 = off2 = 0; bmin = bmax = default;
        if (!_table.TryGetValue(KeyOf(slot, item), out Entry e) || !e.Done || e.Refused != Refusal.None) return false;
        sig = e.Sig; bse = e.Base; nv = e.NV; bmin = e.Bmin; bmax = e.Bmax;
        off0 = e.Off0; cnt0 = e.Cnt0; off1 = e.Off1; cnt1 = e.Cnt1; off2 = e.Off2; cnt2 = e.Cnt2;
        return true;
    }

    internal static void DebugMirrorBounds(int bse, int nv, out Vector3 mn, out Vector3 mx)
    {
        mn = new Vector3(float.MaxValue); mx = new Vector3(float.MinValue);
        for (int i = 0; i < nv; i++)
        {
            int o = (bse + i) * Stride;
            var p = new Vector3(_vMirror[o], _vMirror[o + 1], _vMirror[o + 2]);
            mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
        }
    }

    /// <summary>First index in the run at or past <paramref name="nv"/>, or -1.</summary>
    internal static int DebugBadIndex(nint off, int cnt, int nv)
    {
        int from = (int)((long)off / 2);
        if (cnt == 0) return -1;
        if (from < 0 || from + cnt > _iUsed) return int.MaxValue;
        for (int i = 0; i < cnt; i++) if (_iMirror[from + i] >= nv) return _iMirror[from + i];
        return -1;
    }

    private static float[] _gpuV = Array.Empty<float>();
    private static ushort[] _gpuI = Array.Empty<ushort>();
    internal static bool DebugReadback()
    {
        if (_vbo == 0 || _ebo == 0 || Gl.GetBufferSubData == null || _vUsed == 0) return false;
        if (_gpuV.Length < _vUsed * Stride) _gpuV = new float[_vUsed * Stride];
        if (_gpuI.Length < _iUsed) _gpuI = new ushort[_iUsed];
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, _vbo);
        fixed (float* p = _gpuV) Gl.GetBufferSubData(Gl.COPY_READ_BUFFER, IntPtr.Zero, (IntPtr)((long)_vUsed * Stride * 4), p);
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, _ebo);
        fixed (ushort* p = _gpuI) Gl.GetBufferSubData(Gl.COPY_READ_BUFFER, IntPtr.Zero, (IntPtr)((long)_iUsed * 2), p);
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, 0);
        return true;
    }

    internal static bool DebugGpuMatches(int bse, int nv, nint off, int cnt)
    {
        for (long k = (long)bse * Stride; k < (long)(bse + nv) * Stride; k++) if (_gpuV[k] != _vMirror[k]) return false;
        int from = (int)((long)off / 2);
        for (int i = 0; i < cnt; i++) if (_gpuI[from + i] != _iMirror[from + i]) return false;
        return true;
    }
}

public sealed unsafe partial class AlloyRenderer
{
    private static int _dbgFrame;

    /// <summary>[DEBUG-cx25] every 600 frames while charts draw: which stage of a charted mesh is wrong.</summary>
    internal static void DebugChartAudit()
    {
        if (++_dbgFrame % 600 != 0 || !MeshChart.Armed || _log == null) return;
        bool gpu = MeshChart.DebugReadback();
        int epoch = MeshChart.DebugEpoch;
        int n = 0, sigBad = 0, liveBoundsBad = 0, mirrorBad = 0, idxBad = 0, gpuBad = 0, memoBad = 0, noLive = 0, shown = 0;
        for (int i = 0; i < _ownCount; i++)
        {
            if (_oTri[i] == IntPtr.Zero) continue;
            int v = _oVao[i], item = _oItem[i];
            if (!MeshChart.DebugEntry(v, item, out ulong sig, out int bse, out int nv, out Vector3 bmin, out Vector3 bmax,
                                      out nint o0, out int c0, out nint o1, out int c1, out nint o2, out int c2)) continue;
            n++;
            string why = "";
            bool live = LiveRange(v, item, out ulong liveSig, out Vector3 lmin, out Vector3 lmax);
            if (!live) noLive++;
            else
            {
                if (liveSig != sig) { sigBad++; why += " sig"; }
                if (Vector3.Distance(lmin, bmin) > 1e-2f || Vector3.Distance(lmax, bmax) > 1e-2f) { liveBoundsBad++; why += " live-bounds"; }
            }
            MeshChart.DebugMirrorBounds(bse, nv, out Vector3 mmin, out Vector3 mmax);
            if (Vector3.Distance(mmin, bmin) > 1e-2f || Vector3.Distance(mmax, bmax) > 1e-2f) { mirrorBad++; why += " mirror"; }
            int bad = MeshChart.DebugBadIndex(o0, c0, nv);
            if (bad < 0) bad = MeshChart.DebugBadIndex(o1, c1, nv);
            if (bad < 0) bad = MeshChart.DebugBadIndex(o2, c2, nv);
            if (bad >= 0) { idxBad++; why += $" index({bad}>={nv})"; }
            if (gpu && bad < 0 && !(MeshChart.DebugGpuMatches(bse, nv, o0, c0) && MeshChart.DebugGpuMatches(bse, 0, o1, c1) && MeshChart.DebugGpuMatches(bse, 0, o2, c2)))
            { gpuBad++; why += " gpu"; }
            ref var m = ref _oChart[i];
            if (m.Hit && m.Epoch == epoch && MeshChart.Range(v, item, m.Lod, out int rb, out nint ro, out int rc, out _)
                && (rb != m.Base || ro != m.Off || rc != m.Count)) { memoBad++; why += " memo"; }
            if (why.Length > 0 && shown++ < 6)
                _log.Info($"[DEBUG-cx25] slot {i} vdo {v} item {item} base {bse} nv {nv} off0 {o0} cnt0 {c0}:{why} | chart bounds {bmin}..{bmax} mirror {mmin}..{mmax} live {(live ? $"{lmin}..{lmax}" : "?")} sig {sig:x16} live {liveSig:x16}");
        }
        _log.Info($"[DEBUG-cx25] audit: {n} charted parts, BAD sig {sigBad} live-bounds {liveBoundsBad} mirror {mirrorBad} index {idxBad} gpu {gpuBad}{(gpu ? "" : " (no readback)")} memo {memoBad}, no-live {noLive}; arena v {MeshChart.DebugUsedVerts}/{MeshChart.DebugUploadedVerts} up, i {MeshChart.DebugUsedIndices}/{MeshChart.DebugUploadedIndices} up, epoch {epoch}");
    }

    /// <summary>[DEBUG-cx25] the live (slot, item) range: its RangeSig and position bounds, read as OfferChart reads it.</summary>
    private static bool LiveRange(int s, int item, out ulong sig, out Vector3 mn, out Vector3 mx)
    {
        sig = 0; mn = new Vector3(float.MaxValue); mx = new Vector3(float.MinValue);
        IntPtr vdoPtr = _vVdo[s];
        if (vdoPtr == IntPtr.Zero) return false;
        var vdo = new VdoRef(vdoPtr);
        long elem = vdo.ElemItems, vert = vdo.VertItems;
        if (elem == 0 || vert == 0 || item < 0 || item >= vdo.ElemCount || item >= vdo.VertCount) return false;
        int count = *(int*)(elem + item * 8 + 4);
        long first = (uint)*(int*)(elem + item * 8);
        int baseVertex = *(int*)(vert + item * 8);
        byte* elemHost = OwnedBuffers.PoolHost(vdo.ElemToken);
        byte* posHost = OwnedBuffers.PoolHostByName(_vPos[s].Buf, vdo.VertToken, vdo.DynToken);
        if (elemHost == null || posHost == null || count < 3) return false;
        sig = RangeSig(elemHost, first, count, posHost, in _vPos[s], baseVertex);
        ushort* src = (ushort*)(elemHost + first * 2);
        for (int k = 0; k < count; k++)
        {
            float* p = (float*)(posHost + _vPos[s].Off + (long)(baseVertex + src[k]) * _vPos[s].Stride);
            var q = new Vector3(p[0], p[1], p[2]);
            mn = Vector3.Min(mn, q); mx = Vector3.Max(mx, q);
        }
        return true;
    }
}
