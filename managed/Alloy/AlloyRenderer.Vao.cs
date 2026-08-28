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
    /// Alloy's own copies of them. Both, because ownership is per class this ticket: see
    /// <see cref="VaoName"/>. The owned one is 0 whenever any one of this VDO's buffers has no copy, so a
    /// refusal costs that VDO its ownership and nothing else.</para></summary>
    private static bool EnsureVao(int v)
    {
        if (_vVao46[v] != 0 && !_vStale[v]) return true;
        uint* old = stackalloc uint[2] { _vVao46[v], _vVaoOwn[v] };
        for (int i = 0; i < 2; i++) if (old[i] != 0) Gl.DeleteVertexArrays(1, old + i);
        if (_vVaoOwn[v] != 0) _vaoOwned--;   // the slot being torn down stops counting BEFORE it is rebuilt
        _vVao46[v] = BuildVao(v, owned: false);
        _vVaoOwn[v] = OwnedBuffers.Armed ? BuildVao(v, owned: true) : 0;
        _vStale[v] = false;
        // The live-verify signal for #212. That copies were MADE is the VRAM line's business; this says how
        // many VDO slots the static-opaque class actually draws from ours -- the difference between owned
        // geometry working and owned geometry being allocated and never bound.
        if (_vVaoOwn[v] != 0) _vaoOwned++;
        // A slot with no ENGINE VAO is not drawable at all: binding 0 pulls no attributes and the multidraw
        // reads whatever the default VAO holds. Refused rather than reported built.
        return _vVao46[v] != 0;
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
        if (owned && !(Rebase(ref pos) && Rebase(ref uv)
                       && (!_vLit[v] || Rebase(ref nrm))
                       && (!skin || (Rebase(ref bi) && Rebase(ref bw))))) return 0;
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

    /// <summary>Point one harvested stream at Alloy's copy of its buffer. Offsets and strides are untouched:
    /// the copy is byte-identical, so every item offset the VDO tables hand the draw still lands.</summary>
    private static bool Rebase(ref VertexStream s)
    {
        uint own = OwnedBuffers.Draw(s.Buf);
        if (own == 0) return false;
        s.Buf = own;
        return true;
    }

    /// <summary>#212: which VAO a batch of this class binds. Static opaque pulls from Alloy's own buffers
    /// where a copy exists; every other class still pulls from the engine's pools, unchanged -- the ticket's
    /// proving slice. Widening it is deleting the class test.</summary>
    private static uint VaoName(int v, int cls)
        => cls == (int)AlloyClass.Opaque && _vVaoOwn[v] != 0 ? _vVaoOwn[v] : _vVao46[v];
}
