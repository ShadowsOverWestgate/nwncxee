using System;
using Sowx.Framework;
using Sowx.Graphics;
using static Sowx.Alloy.SceneLayout;

namespace Sowx.Alloy;

/// <summary>#206: the VAO half of enrollment, moved out of <c>Alloy/Scene/</c> so that directory holds no
/// GL. Harvesting an engine VDO's attribute formats is a query against the live GL context and building the
/// 4.6-side VAO over the shared buffers is resource creation -- both are submission-side work that the
/// ownership walk merely calls; what stays in Scene/ is the decision of WHICH parts to take.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>#284: the engine's lazy VAO builder, and the ARRAY_BUFFER shadow it early-outs against.
    /// Both may be null on a client whose symbols did not resolve -- the drip then simply does not run and
    /// the refusal is reported exactly as it was before.</summary>
    private static delegate* unmanaged<IntPtr, void> _setVdo;
    private static uint* _arrayBound;

    /// <summary>Resolve the two symbols the VAO drip drives the engine through.</summary>
    private static void ResolveVaoDrip(ExtenderLog log)
    {
        _setVdo = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.GlSetVertexDataObject);
        _arrayBound = (uint*)Native.Sym(SymbolMap.GlArrayBufferBound);
        if (_setVdo == null || _arrayBound == null)
            log.Info("AlloyRenderer: " +
                     (_setVdo == null ? "GLRender::SetVertexDataObject" : "GLRender::m_nArrayBufferBound") +
                     " unresolved -- #284's VAO drip stays off, so a VDO minted after the shim armed keeps " +
                     "no engine VAO and every part on it stays NOT DRAWN");
    }

    /// <summary>Read the engine VAO's position, UV and normal attribute formats + buffer bindings at their
    /// FIXED engine locations (<see cref="EngPos"/>/<see cref="EngUv"/>/<see cref="EngNormal"/>, from
    /// GLRender::ConnectShaderData's bind table). A failed query (type 0) is refused rather than fed to
    /// VertexAttribPointer -- an enabled-but-unset array crashes the driver's vertex pull (#81). A VDO with
    /// no normal stream is still ownable, just never LIT (#83): lit parts on it hand themselves back to the
    /// engine rather than shade off a guessed normal.</summary>
    private static bool Harvest(IntPtr vdo, int s)
    {
        uint vao = new VdoRef(vdo).Vao;
        if (vao == 0 && DripVao(vdo)) vao = new VdoRef(vdo).Vao;
        if (vao == 0) { HarvestFailed(vdo, 0, 0, false, 0); return false; }
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
        if (_vElemBuf[s] != 0 && _vPos[s].Present && _vUv[s].Buf != 0) return true;
        HarvestFailed(vdo, vao, _vElemBuf[s], _vPos[s].Present, _vUv[s].Buf);
        return false;
    }

    /// <summary>#284: RESIDENCY IS ALLOY'S TO DRIVE, part two. A <c>VertexDataObject</c>'s GL VAO is created
    /// LAZILY, by the engine's draw: <c>VertexDataObjectBase</c>'s constructor zeroes +0xc and the ONLY other
    /// writer of that field in the binary is <c>GLRender::SetVertexDataObject</c> @0x4cff30, whose
    /// create branch gens the name (<c>mov %edi,0xc(%r14)</c> @0x4d000c) and specifies every attribute over
    /// the pool buffers. <c>GLRender::RenderVertexData</c> calls it as its first act -- and #264 swallows
    /// that funnel inside the Alloy window. So a VDO the pool manager minted after the shim armed
    /// (<c>GetVertexDataObjectTokenByAttributes</c> @0x45bdc0 news one whenever no existing VDO matches the
    /// attribute pair with room left) never gets a VAO, <see cref="Harvest"/> refuses it, and since #285
    /// every part on it is a HOLE -- the missing heads, calves and cloaks.
    /// <para>So Alloy drives the creation, at the enrollment edge, calling the engine's own function rather
    /// than replicating its attribute setup: that mask-to-offset arithmetic is exactly the surface #81
    /// crashed on, and the engine is entitled to change it.</para>
    /// <para>ADR 0015: nothing is retained. The engine is handed a pointer it already owns, for the duration
    /// of one call, and the current-VDO cache is left holding null -- so there is no borrowed pointer here to
    /// state a death notice for.</para></summary>
    private static bool DripVao(IntPtr vdo)
    {
        if (_setVdo == null || _arrayBound == null) return false;
        if (!DripArm(vdo)) return false;
        var v = new VdoRef(vdo);
        // The residency gate, and the reason this is a re-armable drip and not a one-shot latch. The create
        // branch captures the pool's GL name into the VAO it builds, and the has-a-VAO branch never
        // re-specifies an attribute -- so dripping before the pools are named bakes buffer 0 in for ever and
        // converts a healable no-vao-slot into an unhealable no-pos. Deferred, and asked again next
        // enrollment. Indexed is in the same test because GetElementArrayBuffer @0x45b320 returns 0 while
        // +0x8 is clear, which sends BindBuffer's element path down its UNBIND branch mid-build.
        // BOTH vertex pools, not just the static one: the attribute loop runs twice, over +0x24 and +0x30.
        if (!v.Indexed || !OwnedBuffers.PoolHasName(v.ElemToken)
            || !PoolReady(v.VertToken) || !PoolReady(v.DynToken))
        { _dripDeferred++; return false; }
        // The engine's ARRAY_BUFFER shadow must be truthful before it binds an array buffer of its own, or
        // BindBuffer skips the bind and the attribute pointers below capture whatever Alloy left bound.
        int ab; Gl.GetIntegerv(Gl.ARRAY_BUFFER_BINDING, &ab); *_arrayBound = (uint)ab;
        int saved; Gl.GetIntegerv(Gl.VERTEX_ARRAY_BINDING, &saved);
        // Null FIRST, not merely after. SetVertexDataObject early-outs when its cached current VDO equals
        // the argument, and that cache can hold a freed pointer -- operator_new recycles the 0x68-byte
        // blocks -- so a recycled address would silently skip the build it was called for.
        _setVdo(IntPtr.Zero);
        _setVdo(vdo);
        _setVdo(IntPtr.Zero);
        Gl.BindVertexArray((uint)saved);
        if (new VdoRef(vdo).Vao != 0)
        {
            _dripMade++;
            // Bounded, and separate from the refusal report: _harvestSeen latches a VDO the FIRST time it is
            // refused and never speaks about it again, so without its own line the fix working looks exactly
            // like the fix never running.
            if (_dripLogLeft-- > 0)
                _log?.Info($"AlloyRenderer: [harvest] VDO {vdo:X} had no engine VAO -- drip built it "
                           + $"(mode={v.Mode} verts={v.VertCount} elems={v.ElemCount})");
            return true;
        }
        _dripRefused++;
        return false;
    }

    /// <summary>A vertex pool the engine's build will not stumble over: either resident, or ABSENT. Absent
    /// has to pass, in both directions -- the engine skips a 0xffffffff token, so dripping past one is safe,
    /// and refusing it would park a VDO with no dynamic stream in <c>deferred</c> for ever under a reason
    /// ("pools not resident") that was never true of it.</summary>
    private static bool PoolReady(uint token)
        => token == SceneLayout.VdoNoPool || OwnedBuffers.PoolHasName(token);

    /// <summary>Lines the drip may spend saying it worked. Matched to <see cref="HarvestReportCap"/>: the
    /// refusals it answers are capped there, so this cannot outlive them.</summary>
    private static int _dripLogLeft = HarvestReportCap;

    /// <summary>One drip attempt per VDO per enrollment. <see cref="Harvest"/> runs once per candidate PART,
    /// so a VDO carrying two hundred of them would otherwise re-drive the engine two hundred times in a
    /// single walk -- which is what <c>no-engine-vao 8831</c> on the live period line is a count of.
    /// <para>The re-arm is the enrollment edge itself, and it is bounded by that: the pools a deferral is
    /// waiting on become resident within a frame or two, and an enrollment is edge-triggered, not
    /// per-frame.</para></summary>
    private const int DripSlots = 16;
    private static readonly IntPtr[] _dripVdo = new IntPtr[DripSlots];
    private static int _dripN;
    internal static int _dripMade, _dripDeferred, _dripRefused;

    private static bool DripArm(IntPtr vdo)
    {
        for (int i = 0; i < _dripN; i++) if (_dripVdo[i] == vdo) return false;
        if (_dripN >= DripSlots) return false;
        _dripVdo[_dripN++] = vdo;
        return true;
    }

    /// <summary>A new enrollment re-asks every deferred VDO. Called from <c>Enroll</c>, which is the edge the
    /// drip is scoped to.</summary>
    private static void DripRearm() { _dripN = 0; _dripDeferred = 0; }

    /// <summary>#284's residual, on the period line. <c>made</c> and <c>refused</c> are LIFETIME
    /// populations of VDOs; <c>deferred</c> is per enrollment, because it is the residency race still open
    /// and a lifetime total of it could not distinguish "settled" from "never settles". A non-zero
    /// <c>refused</c> is what would kill the diagnosis -- the engine ran its own build and still left +0xc
    /// at zero.</summary>
    private static string DripNote()
        => _dripMade == 0 && _dripDeferred == 0 && _dripRefused == 0
           ? ""
           : $" [VAO drip: {_dripMade} made, {_dripRefused} refused, {_dripDeferred} deferred at the last enrollment]";

    /// <summary>Distinct VDOs whose harvest has already been reported. #285 made every refusal a hole, and
    /// <c>no-vao-slot</c> is the largest one on live content -- but "harvest failed" names four different
    /// faults and the period line cannot tell them apart. Bounded: one line per VDO, at most
    /// <see cref="HarvestReportCap"/> of them, so a persistent failure does not become a log flood.</summary>
    private static readonly System.Collections.Generic.HashSet<IntPtr> _harvestSeen = new();
    private const int HarvestReportCap = 24;
    internal static int HarvestNoVao, HarvestNoElem, HarvestNoPos, HarvestNoUv;

    /// <summary>Name which of Harvest's four conditions refused this VDO, once per distinct VDO. The counts
    /// are populations of VDOs, not of parts or frames.</summary>
    private static void HarvestFailed(IntPtr vdo, uint vao, uint elem, bool pos, uint uv)
    {
        if (vao == 0) HarvestNoVao++;
        else if (elem == 0) HarvestNoElem++;
        else if (!pos) HarvestNoPos++;
        else HarvestNoUv++;
        if (_harvestSeen.Count >= HarvestReportCap || !_harvestSeen.Add(vdo)) return;
        var v = new VdoRef(vdo);
        _log?.Info($"AlloyRenderer: [harvest] VDO {vdo:X} refused -- engine vao={vao} elembuf={elem} "
                   + $"pos={(pos ? 1 : 0)} uvbuf={uv} | mode={v.Mode} verts={v.VertCount} elems={v.ElemCount}");
    }

    /// <summary>Build the 4.6-side VAO(s) for this VDO slot using the harvested formats (VAOs don't share
    /// across contexts; buffers do -- #70). ONLY position, UV and (where the stream exists) normal are bound,
    /// at OUR locations: replicating every enabled engine attrib crashed the driver's vertex-pull JIT when
    /// one carried a client-pointer binding (#81), and neither Alloy program consumes anything else.
    /// <para>#260: ONE VAO. #212/#213 built a second one over Alloy's copies of the same buffers; there are
    /// no copies now -- Alloy uploads into the pool's own buffer -- so the two VAOs would be identical. What
    /// survives is the question of whether the pools behind it are Alloy's, asked once here and reported by
    /// <see cref="VaoName"/>.</para></summary>
    private static bool EnsureVao(int v)
    {
        if (_vVao46[v] != 0 && !_vStale[v]) return true;
        if (_vVao46[v] != 0) { uint old = _vVao46[v]; Gl.DeleteVertexArrays(1, &old); }
        _vVao46[v] = BuildVao(v);
        _vOwned[v] = _vVao46[v] != 0 && OwnedBuffers.Owns(_vElemBuf[v]) && OwnedBuffers.Owns(_vPos[v].Buf);
        _vStale[v] = false;
        // A slot with no ENGINE VAO is not drawable at all: binding 0 pulls no attributes and the multidraw
        // reads whatever the default VAO holds. Refused rather than reported built.
        return _vVao46[v] != 0;
    }

    /// <summary>The live-verify signal: how many live VDO slots draw from pools Alloy uploaded. That the
    /// pools are resident is the VRAM line's business; this is the difference between owned geometry working
    /// and owned geometry being uploaded and never bound.
    /// <para>Counted at the log rather than tracked, because <c>RevokeAll</c> retires slots without deleting
    /// their VAOs -- that waits for a bracket -- so a running tally would over-report every slot retired and
    /// not yet rebuilt.</para></summary>
    private static int OwnedVaos()
    {
        int n = 0;
        for (int v = 0; v < _vdoCount; v++) if (_vVdo[v] != IntPtr.Zero && _vOwned[v]) n++;
        return n;
    }

    /// <summary>One VAO over this slot's streams, wherever the engine's pool descriptors say they live.</summary>
    private static uint BuildVao(int v)
    {
        uint elem = _vElemBuf[v];
        if (elem == 0) return 0;
        bool skin = _vSkin[v] && _skinning;
        var pos = _vPos[v]; var uv = _vUv[v]; var nrm = _vNrm[v];
        var bi = _vBi[v]; var bw = _vBw[v];
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


    /// <summary>The sky dome's VAO. Off the class mask: the dome is not an <see cref="AlloyClass"/>, and its
    /// own pass line reports it.</summary>
    internal static uint SkyVao(int v) => _vVao46[v];

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

    /// <summary>The VAO this VDO slot binds, and the acceptance stamp that goes with it. #260 removed the
    /// choice -- there is one VAO, over the pool buffers Alloy itself uploads -- so what is left is the
    /// report: which classes drew from pools Alloy owns. A slot whose pools the ENGINE uploaded (everything
    /// resident before the first present) draws correctly and is simply not counted.</summary>
    private static uint VaoName(int v, int cls)
    {
        if (_vOwned[v]) _ownedClsMask |= 1 << cls;
        return _vVao46[v];
    }
}
