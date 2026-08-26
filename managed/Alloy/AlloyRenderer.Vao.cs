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

    /// <summary>Build the 4.6-side VAO over the SHARED engine buffers using the harvested formats (VAOs don't
    /// share across contexts; buffers do -- #70). ONLY position, UV and (where the stream exists) normal are
    /// bound, at OUR locations: replicating every enabled engine attrib crashed the driver's vertex-pull JIT
    /// when one carried a client-pointer binding (#81), and neither Alloy program consumes anything else.</summary>
    private static bool EnsureVao(int v)
    {
        if (_vVao46[v] != 0 && !_vStale[v]) return true;
        if (_vVao46[v] != 0) { uint old = _vVao46[v]; Gl.DeleteVertexArrays(1, &old); _vVao46[v] = 0; }
        uint vao; Gl.GenVertexArrays(1, &vao);
        Gl.BindVertexArray(vao);
        VertexArrays.Bind(LocPos, in _vPos[v]);
        VertexArrays.Bind(LocUv, in _vUv[v]);
        if (_vLit[v]) VertexArrays.Bind(LocNrm, in _vNrm[v]);
        // #137: and the bone streams, on a skinned VDO. Same "one stream and no more" rule -- these two are
        // the only additions, and both are consumed by the skinning branch the Alloy stages now carry.
        if (_vSkin[v] && _skinning)
        {
            VertexArrays.Bind(LocBoneIdx, in _vBi[v]);
            VertexArrays.Bind(LocBoneW, in _vBw[v]);
        }
        Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _vElemBuf[v]);
        Gl.BindVertexArray(0);
        _vVao46[v] = vao; _vStale[v] = false;
        return true;
    }
}
