using System;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>#244: the CONTENT half of the mesh census. Hashing a part's actual vertices is what separates
/// "the same mesh" from "the same shape of mesh" -- the signature key (primitive, texture, index count)
/// cannot tell fifty copies of one placeable model from fifty terrain patches that share a topology and a
/// texture while their vertex positions differ, and on tileset content the second is the likelier of the two.
/// <para>GL, so it lives outside <c>Alloy/Scene/</c> (build.sh enforces that directory holds none) even
/// though the walk that feeds it is enrollment's.</para>
/// <para><b>Off by default, and a readback by nature.</b> The engine's host-side pool shadow is not reachable
/// from here -- <c>RenderInterface::GetPoolBuffer</c> answers with a GL name, not a pointer -- so the only
/// way to the bytes is to map the buffer and read it back, which is the stall that got #144 reverted. It is
/// paid per VDO rather than per part (six maps for a whole area, not seven thousand readbacks) and only on
/// an enrollment, but it is still a diagnostic and is armed by <c>/draw meshhash 1</c> rather than run
/// because a census happened to be due.</para></summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>#244: is the content pass armed? Off by default -- see the type summary for why a readback
    /// is not something a census runs just because one was due. <c>/draw meshhash 1</c>.</summary>
    private static bool _meshHash;

    /// <summary>#244: is the de-duplicated DRAW armed? Separate from <see cref="_meshHash"/>, which only
    /// builds the registry -- this is the switch that makes a wrong entry visible, so it bisects on its
    /// own.</summary>
    private static bool _dedupDraw;
    /// <summary>#244: milliseconds the last enrollment spent hashing and verifying, and the peak. This is the
    /// number that decides whether de-duplication can ship: the registry has to be REBUILT every enrollment
    /// (slots reshuffle, and the transform is only a proof for the parts that formed it), so its cost is
    /// paid on the same frames ADR 0017 already records as a compound spike -- Enroll, Shadow and Shim
    /// together. A win in the colour pass bought with a hitch on the enrollment frame is not a win.</summary>
    private static float _ddMs, _ddMsPeak;
    /// <summary>#244: draws this period that sourced their geometry from a representative.</summary>
    private static int _dedupDrawn;

    /// <summary>#244: this VDO slot's mapped position / UV / normal bytes, or null. Held across the whole
    /// verify rather than per VDO because a repeated shape can span buffers, and a pass that could only
    /// compare within one would under-report exactly where the engine spread a tileset over several.</summary>
    private static readonly byte*[] _mapPos = new byte*[MaxVdos],
                                    _mapUv = new byte*[MaxVdos],
                                    _mapNrm = new byte*[MaxVdos];
    /// <summary>#244: the buffer names currently mapped, so a stream trio sharing one interleaved buffer maps
    /// it once -- mapping an already-mapped buffer fails, and the second stream would silently read null.
    /// </summary>
    private static readonly uint[] _mapName = new uint[MaxVdos * 3];
    private static readonly IntPtr[] _mapPtr = new IntPtr[MaxVdos * 3];
    private static int _mapCount;

    /// <summary>#244 phase A: map every owned VDO's vertex streams, derive and verify a placement for each
    /// repeated part, then unmap. Same armed dial and the same cold cadence as the hash it follows.</summary>
    private static void VerifyDedupPass(int n)
    {
        if (!_meshHash || n <= 0 || Gl.MapBufferRange == null || Gl.UnmapBuffer == null) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _mapCount = 0;
        for (int v = 0; v < _vdoCount; v++)
        {
            _mapPos[v] = _mapUv[v] = _mapNrm[v] = null;
            if (_vVdo[v] == IntPtr.Zero) continue;
            _mapPos[v] = MapStream(_vPos[v]);
            _mapUv[v] = MapStream(_vUv[v]);
            _mapNrm[v] = MapStream(_vNrm[v]);
        }
        VerifyDedup(n);
        for (int m = 0; m < _mapCount; m++)
        {
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _mapName[m]);
            Gl.UnmapBuffer(Gl.ARRAY_BUFFER);
        }
        Gl.BindBuffer(Gl.ARRAY_BUFFER, 0);
        _mapCount = 0;
        _ddMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency);
        if (_ddMs > _ddMsPeak) _ddMsPeak = _ddMs;
    }

    /// <summary>Map one stream's buffer read-only, reusing the pointer when another stream already mapped the
    /// same name (interleaved formats share a buffer). Null for a stream this cannot read, which the callers
    /// treat as "not testable" rather than as a failure.</summary>
    private static byte* MapStream(in VertexStream st)
    {
        if (!st.Present || st.Stride <= 0 || st.Type != Gl.FLOAT) return null;
        for (int m = 0; m < _mapCount; m++) if (_mapName[m] == st.Buf) return (byte*)_mapPtr[m];
        if (_mapCount >= _mapName.Length) return null;
        Gl.BindBuffer(Gl.ARRAY_BUFFER, st.Buf);
        int size = 0;
        Gl.GetBufferParameteriv(Gl.ARRAY_BUFFER, Gl.BUFFER_SIZE, &size);
        if (size <= 0) return null;
        void* p = Gl.MapBufferRange(Gl.ARRAY_BUFFER, IntPtr.Zero, (IntPtr)size, Gl.MAP_READ_BIT);
        if (p == null) return null;
        _mapName[_mapCount] = st.Buf; _mapPtr[_mapCount] = (IntPtr)p; _mapCount++;
        return (byte*)p;
    }

    /// <summary>Hash the position prefix of every censused part, keyed by the VDO slot the staging walk
    /// recorded. One map per VDO, parts scanned inside it, so the readback cost is the buffer count and not
    /// the part count. False when the pass is disarmed or the entry points are missing, which leaves every
    /// hash zero and the shape key on its signature alone.
    /// <para>A part whose range will not fit the buffer keeps hash 0 rather than being dropped. Zero groups
    /// with zero, so such parts read as one shared mesh -- an OVER-report, which is the safe direction for a
    /// screen, and <see cref="_msHashed"/> against the censused count is what says how much of the figure is
    /// that rather than real sharing.</para></summary>
    private static bool HashVertices(int n)
    {
        if (!_meshHash || n <= 0) return false;
        if (Gl.MapBufferRange == null || Gl.UnmapBuffer == null || Gl.GetBufferParameteriv == null)
            return false;
        Array.Clear(_msHash, 0, n);
        _msHashed = 0;
        for (int v = 0; v < _vdoCount; v++)
        {
            if (_vVdo[v] == IntPtr.Zero) continue;
            VertexStream ps = _vPos[v];
            // FLOAT positions only. A packed or normalised position stream would hash its raw bytes just as
            // well, but the stride/size arithmetic below is written for the float3 case the harvest accepts,
            // and a stream this does not understand is better skipped than silently hashed wrong.
            if (!ps.Present || ps.Stride <= 0 || ps.Type != Gl.FLOAT || ps.Size < 3) continue;
            bool any = false;
            for (int k = 0; k < n && !any; k++) any = _msVao[k] == v;
            if (!any) continue;
            Gl.BindBuffer(Gl.ARRAY_BUFFER, ps.Buf);
            int size = 0;
            Gl.GetBufferParameteriv(Gl.ARRAY_BUFFER, Gl.BUFFER_SIZE, &size);
            if (size <= 0) continue;
            byte* p = (byte*)Gl.MapBufferRange(Gl.ARRAY_BUFFER, IntPtr.Zero, (IntPtr)size, Gl.MAP_READ_BIT);
            if (p == null) continue;
            for (int k = 0; k < n; k++)
            {
                if (_msVao[k] != v || _msVerts[k] <= 0) continue;
                int verts = Math.Min(MsHashVerts, _msVerts[k]);
                long off = ps.Off + (long)_msBase[k] * ps.Stride;
                if (off < 0 || off + (long)verts * ps.Stride > size) continue;
                _msHash[k] = HashPrefix(p + off, ps.Stride, verts);
                _msRigid[k] = HashRigid(p + off, ps.Stride, verts);
                _msFull[k] = HashFull(p + off, ps.Stride, _msVerts[k]);
                _msHashed++;
            }
            Gl.UnmapBuffer(Gl.ARRAY_BUFFER);
        }
        Gl.BindBuffer(Gl.ARRAY_BUFFER, 0);
        return true;
    }

    /// <summary>#244: the same prefix, hashed so that PLACEMENT drops out -- the distances from the prefix's
    /// centroid to each of its vertices, sorted and quantised. Invariant under rotation and translation, so
    /// two placements of one model agree even where the engine baked their placement into their vertices.
    /// <para>This is the key the exact hash cannot be: ~92% of an area's parts are world-space, and for those
    /// the exact hash reports every placement as a distinct mesh <b>however many times the model repeats</b>.
    /// It measures what is collapsible today; this measures what REPEATS.</para>
    /// <para>Not invariant under scale -- a scaled copy cannot share vertices with its original, so counting
    /// it as the same mesh would report a collapse that is not available.</para>
    /// <para><b>It IS invariant under mirroring, and that is a known over-report.</b> A reflection preserves
    /// every centroid distance, so a mirrored copy hashes identically to its original while sharing no
    /// vertex arrangement a rotation can reach -- and NWN tilesets mirror tiles routinely. Such a pair fails
    /// the derivation's determinant test and drops out there; the count this key reports is therefore an
    /// upper bound on what can actually be collapsed, and <see cref="_dedupMirror"/> is how much of the gap
    /// is this.</para>
    /// <para>Quantised at <see cref="RigidQuantum"/>. A distance landing on a quantum boundary splits a pair
    /// that should have matched, so this UNDER-reports rather than over-reports -- the safe direction for a
    /// number that would justify work. ponytail: if the two hashes ever disagree by more than the object-space
    /// share, the boundary rate is the first suspect, and the fix is a second hash at a half-quantum offset.
    /// </para></summary>
    private static uint HashRigid(byte* p, int stride, int verts)
    {
        const uint Basis = 2166136261, Prime = 16777619;
        float* d = stackalloc float[MsHashVerts];
        float cx = 0f, cy = 0f, cz = 0f;
        for (int i = 0; i < verts; i++)
        {
            float* v = (float*)(p + (long)i * stride);
            cx += v[0]; cy += v[1]; cz += v[2];
        }
        cx /= verts; cy /= verts; cz /= verts;
        for (int i = 0; i < verts; i++)
        {
            float* v = (float*)(p + (long)i * stride);
            float ax = v[0] - cx, ay = v[1] - cy, az = v[2] - cz;
            d[i] = MathF.Sqrt(ax * ax + ay * ay + az * az);
        }
        // Sorted, because vertex ORDER is not part of the shape: the same model can be emitted with its
        // vertices in a different order and is still the same geometry. Insertion sort -- verts is 16.
        for (int i = 1; i < verts; i++)
        {
            float x = d[i];
            int j = i - 1;
            while (j >= 0 && d[j] > x) { d[j + 1] = d[j]; j--; }
            d[j + 1] = x;
        }
        uint h = Basis;
        for (int i = 0; i < verts; i++)
        {
            uint q = (uint)(int)MathF.Round(d[i] / RigidQuantum);
            for (int b = 0; b < 4; b++) { h ^= (byte)(q >> (b * 8)); h *= Prime; }
        }
        return h == 0 ? Prime : h;
    }

    /// <summary>#244: an order-INSENSITIVE hash of the whole mesh's centroid distances -- a commutative sum
    /// rather than a sorted list, so it needs no scratch proportional to the vertex count and no sort. Used
    /// only to tell a failed correspondence from a failed match: two copies agreeing here while disagreeing
    /// vertex-for-vertex are the same geometry emitted in a different vertex order.</summary>
    private static uint HashFull(byte* p, int stride, int verts)
    {
        const uint Prime = 16777619;
        if (verts < 3) return 0;
        float cx = 0f, cy = 0f, cz = 0f;
        for (int i = 0; i < verts; i++)
        {
            float* v = (float*)(p + (long)i * stride);
            cx += v[0]; cy += v[1]; cz += v[2];
        }
        cx /= verts; cy /= verts; cz /= verts;
        uint acc = 0;
        for (int i = 0; i < verts; i++)
        {
            float* v = (float*)(p + (long)i * stride);
            float ax = v[0] - cx, ay = v[1] - cy, az = v[2] - cz;
            uint q = (uint)(int)MathF.Round(MathF.Sqrt(ax * ax + ay * ay + az * az) / RigidQuantum);
            // Mixed before it is added, so that the SUM is not simply the total distance -- which two
            // genuinely different meshes hit far too easily.
            acc += (q ^ (q >> 13)) * Prime;
        }
        return acc == 0 ? Prime : acc;
    }

    /// <summary>FNV-1a over the xyz of <paramref name="verts"/> vertices, walking the stream's stride. Only
    /// the position triple of each vertex, never the padding between them: a stride can carry a neighbouring
    /// attribute whose bytes differ between two placements of the same model.</summary>
    private static uint HashPrefix(byte* p, int stride, int verts)
    {
        const uint Basis = 2166136261, Prime = 16777619;
        uint h = Basis;
        for (int i = 0; i < verts; i++)
        {
            byte* q = p + (long)i * stride;
            for (int b = 0; b < 3 * sizeof(float); b++) { h ^= q[b]; h *= Prime; }
        }
        // Never 0: zero is the "not hashed" value, and a real mesh landing on it would join the unhashed
        // parts as one bogus shared group.
        return h == 0 ? Prime : h;
    }
}
