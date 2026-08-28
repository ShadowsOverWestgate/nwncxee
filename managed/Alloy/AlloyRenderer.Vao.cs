using System;
using Sowx.Graphics;
using static Sowx.Alloy.SceneLayout;

namespace Sowx.Alloy;

/// <summary>#206: the VAO half of enrollment, moved out of <c>Alloy/Scene/</c> so that directory holds no
/// GL. Harvesting an engine VDO's attribute formats is a query against the live GL context and building the
/// 4.6-side VAO over the shared buffers is resource creation -- both are submission-side work that the
/// ownership walk merely calls; what stays in Scene/ is the decision of WHICH parts to take.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Read the engine VAO's position, UV and normal attribute formats + buffer bindings at their
    /// FIXED engine locations (<see cref="EngPos"/>/<see cref="EngUv"/>/<see cref="EngNormal"/>, from
    /// GLRender::ConnectShaderData's bind table). A failed query (type 0) is refused rather than fed to
    /// VertexAttribPointer -- an enabled-but-unset array crashes the driver's vertex pull (#81). A VDO with
    /// no normal stream is still ownable, just never LIT (#83): lit parts on it hand themselves back to the
    /// engine rather than shade off a guessed normal.</summary>
    private static bool Harvest(IntPtr vdo, int s)
    {
        uint vao = new VdoRef(vdo).Vao;
        if (vao == 0) return false;
        int saved;
        Gl.GetIntegerv(Gl.VERTEX_ARRAY_BINDING, &saved);
        Gl.BindVertexArray(vao);
        int eb; Gl.GetIntegerv(Gl.ELEMENT_ARRAY_BUFFER_BINDING, &eb);
        _vElemBuf[s] = (uint)eb;
        VertexArrays.Read(EngPos, out _vPos[s]);
        VertexArrays.Read(EngUv, out _vUv[s]);
        VertexArrays.Read(EngNormal, out _vNrm[s]);
        // ...while the HARVESTED VAO is still bound: Enabled() queries the binding, not the slot, so asking
        // after the restore below would answer for whatever the engine had bound and refuse (or admit) the
        // wrong VDO -- in the admitting direction, exactly the bind-pose misrender this flag exists to stop.
        // #137: the bone streams' FORMATS, not just their presence -- the Alloy VAO binds them now. Both or
        // neither, for the reason on _vSkin.
        _vSkin[s] = false;
        if (_skinning)
        {
            VertexArrays.Read(EngBoneIdx, out _vBi[s]);
            VertexArrays.Read(EngBoneW, out _vBw[s]);
            _vSkin[s] = _vBi[s].Buf != 0 && _vBw[s].Buf != 0;
        }
        else _vSkin[s] = VertexArrays.Enabled(EngBoneIdx) && VertexArrays.Enabled(EngBoneW);
        Gl.BindVertexArray((uint)saved);
        _vLit[s] = _vNrm[s].Buf != 0 && _vNrm[s].Type == Gl.FLOAT && _vNrm[s].Size == 3;
        return _vElemBuf[s] != 0 && _vPos[s].Present && _vUv[s].Buf != 0;
    }

    /// <summary>Build the 4.6-side VAO(s) for this VDO slot using the harvested formats (VAOs don't share
    /// across contexts; buffers do -- #70). ONLY position, UV and (where the stream exists) normal are bound,
    /// at OUR locations: replicating every enabled engine attrib crashed the driver's vertex-pull JIT when
    /// one carried a client-pointer binding (#81), and neither Alloy program consumes anything else.
    /// <para>#212: TWO VAOs, identical but for which buffers they pull from -- the engine's pool buffers and
    /// Alloy's own copies of them. Both, because the owned one is 0 whenever any one of this VDO's buffers
    /// has no copy: a refusal costs that VDO its ownership and nothing else, and the engine-side VAO is what
    /// it falls back to. See <see cref="VaoName"/>.</para></summary>
    private static bool EnsureVao(int v)
    {
        if (_vVao46[v] != 0 && !_vStale[v]) return true;
        uint* old = stackalloc uint[2] { _vVao46[v], _vVaoOwn[v] };
        for (int i = 0; i < 2; i++) if (old[i] != 0) Gl.DeleteVertexArrays(1, old + i);
        _vVao46[v] = BuildVao(v, owned: false);
        _vVaoOwn[v] = OwnedBuffers.Armed ? BuildVao(v, owned: true) : 0;
        _vStale[v] = false;
        // A slot with no ENGINE VAO is not drawable at all: binding 0 pulls no attributes and the multidraw
        // reads whatever the default VAO holds. Refused rather than reported built.
        return _vVao46[v] != 0;
    }

    /// <summary>The live-verify signal: how many live VDO slots actually DRAW from Alloy's buffers. That
    /// copies were made is the VRAM line's business; this is the difference between owned geometry working
    /// and owned geometry being allocated and never bound.
    /// <para>Counted at the log rather than tracked, because <c>RevokeAll</c> retires slots without deleting
    /// their VAOs -- that waits for a bracket -- so a running tally would over-report every slot retired and
    /// not yet rebuilt.</para></summary>
    private static int OwnedVaos()
    {
        int n = 0;
        for (int v = 0; v < _vdoCount; v++) if (_vVdo[v] != IntPtr.Zero && _vVaoOwn[v] != 0) n++;
        return n;
    }

    /// <summary>One VAO over one set of buffers. <paramref name="owned"/> swaps every source buffer for
    /// Alloy's copy of it; a buffer with no copy returns 0 rather than a VAO half-pulling from each.</summary>
    private static uint BuildVao(int v, bool owned)
    {
        uint elem = owned ? OwnedBuffers.Draw(_vElemBuf[v]) : _vElemBuf[v];
        if (elem == 0) return 0;
        bool skin = _vSkin[v] && _skinning;
        var pos = _vPos[v]; var uv = _vUv[v]; var nrm = _vNrm[v];
        var bi = _vBi[v]; var bw = _vBw[v];
        if (owned && !(OwnedBuffers.Rebase(ref pos) && OwnedBuffers.Rebase(ref uv)
                       && (!_vLit[v] || OwnedBuffers.Rebase(ref nrm))
                       && (!skin || (OwnedBuffers.Rebase(ref bi) && OwnedBuffers.Rebase(ref bw))))) return 0;
        uint vao; Gl.GenVertexArrays(1, &vao);
        Gl.BindVertexArray(vao);
        VertexArrays.Bind(LocPos, in pos);
        VertexArrays.Bind(LocUv, in uv);
        if (_vLit[v]) VertexArrays.Bind(LocNrm, in nrm);
        // #137: and the bone streams, on a skinned VDO. Same "one stream and no more" rule -- these two are
        // the only additions, and both are consumed by the skinning branch the Alloy stages now carry.
        if (skin)
        {
            VertexArrays.Bind(LocBoneIdx, in bi);
            VertexArrays.Bind(LocBoneW, in bw);
        }
        Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, elem);
        Gl.BindVertexArray(0);
        return vao;
    }


    /// <summary>The sky dome's VAO. Same owned-or-engine fallback, but off the class mask: the dome is not
    /// an <see cref="AlloyClass"/>, and its own pass line reports it.</summary>
    internal static uint SkyVao(int v) => _vVaoOwn[v] != 0 ? _vVaoOwn[v] : _vVao46[v];

    /// <summary>#213's acceptance signal: the classes that have bound an OWNED VAO since the last period
    /// line, one bit per <see cref="AlloyClass"/>. The slot count cannot say this -- a class whose slots all
    /// refused, and a class nothing staged for, both read as "some slots owned" -- and the criterion is per
    /// class. Set from the submission loops, which is why it is a mask and not a count.</summary>
    private static int _ownedClsMask;

    /// <summary>Which classes drew from Alloy's buffers this period, cleared as it is read.</summary>
    internal static string OwnedClasses()
    {
        int m = _ownedClsMask;
        _ownedClsMask = 0;
        if (m == 0) return "none";
        string s = "";
        for (int c = 0; c < AlloyClasses.Count; c++)
            if ((m & (1 << c)) != 0) s += (s.Length == 0 ? "" : "+") + (AlloyClass)c;
        return s;
    }

    /// <summary>#213: which VAO this VDO slot binds -- Alloy's own buffers wherever a copy exists, whatever
    /// the class. #212's proving slice was static opaque alone; the class test that enforced it is gone, so
    /// cutout, blended, water, additive, the sky dome and the skinned parts all pull from the same copies.
    /// The remaining fallback is per-SLOT, not per-class: a VDO one of whose buffers refused to copy keeps
    /// drawing from the engine's pools rather than half from each.</summary>
    private static uint VaoName(int v, int cls)
    {
        if (_vVaoOwn[v] == 0) return _vVao46[v];
        _ownedClsMask |= 1 << cls;
        return _vVaoOwn[v];
    }
}
