using System;
using System.Numerics;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #286 (M5 strand 2, ADR 0018): shadow caster selection in compute, in <see cref="CullPass"/>'s shape.
/// Until this existed <see cref="CasterPass"/> walked every live caster on the CPU once per VIEW -- 196 views
/// over 7950 casters, 12,46ms before a single depth draw issued -- and the walk did per-view what is a
/// per-FRAME fact for all but the cull itself.
/// <para>What replaces it: one staging pass per FRAME (the records, their batch keys, the VAO and mask-texture
/// resolves, the bone bases), then per view a handful of uniforms, one dispatch that tests each caster's
/// bounding sphere against that view and compacts the survivors into an indirect command buffer, and one
/// <c>glMultiDrawElementsIndirectCount</c> per batch. No count ever comes back to the CPU.</para>
/// <para>The record is <see cref="CasterPass"/>'s own <c>SowxCaster</c>, read through the same
/// <c>CasterBufDecl</c> the draw stages read it through -- #93 put <c>bounds</c> in the record precisely so
/// this dispatch could test it by record slot without a reformat. <c>baseInstance</c> still carries the
/// record index, which is what makes compaction free: it renumbers <c>gl_DrawID</c> and nothing else.</para>
/// <para>The cull ITSELF is ported semantics, not a re-derivation: the cone test circumscribes the 90deg face
/// pyramid and doubles the radius allowance, exactly as <c>CasterPass.FaceCulls</c> does, and the slope is
/// interpolated from that same constant so the two cannot drift. A caster wrongly REJECTED is a deleted
/// shadow; one wrongly admitted costs a draw. <c>/shadow gather 0</c> is the A/B back to the CPU walk.</para>
/// <para>Stands down whole when 4.6 indirect-count or compute is missing, or when the program fails to build;
/// standing down means the CPU walk, unchanged.</para>
/// </summary>
internal static unsafe class CasterGather
{
    /// <summary>SSBO bindings private to this dispatch. 0..3 are <see cref="AlloyLights"/>, 5/6
    /// <see cref="CasterPass"/>'s records and bones, 10/11 <see cref="Particles"/>, 12..16
    /// <see cref="CullPass"/>. The per-draw records are read at <see cref="CasterPass.BindCaster"/>, the same
    /// binding the depth stages draw them from.</summary>
    internal const uint BindSrc = 17, BindCmd = 18, BindCount = 19;

    /// <summary>Uints per source record, and their order -- index count, first index, base vertex, record
    /// slot, batch, batch output base. <see cref="CullPass.SrcUints"/>'s layout, deliberately: one shape for
    /// both gathers, and the batch's output base is what turns an <c>atomicAdd</c> into an output index.</summary>
    internal const int SrcUints = 6;
    /// <summary>Uints per <c>DrawElementsIndirectCommand</c>. Fixed by GL.</summary>
    internal const int CmdUints = 5, CmdBytes = CmdUints * 4;
    private const int GroupSize = 64;
    /// <summary>Command/count buffer pairs rotated per VIEW. A frame issues ~200 views into these, and the
    /// hazard is a write-after-read: view K's indirect draws source their commands (and their count) from one
    /// pair while view K+Rotation's dispatch shader-writes it. The per-view barrier does not close that on its
    /// own -- it is issued AFTER its own dispatch, so at a rotation of 1 nothing at all separates the read
    /// from the rewrite.
    /// <para>The rotation depth is what closes it. MemoryBarrier "defines a barrier ordering the memory
    /// transactions issued prior to the command relative to those issued after" -- symmetric, and a
    /// write-after-read needs only that ordering, not visibility. Reusing a pair R views later leaves R-1
    /// barriers standing in command order between the last read of it and the rewrite, each naming
    /// COMMAND_BARRIER_BIT -- whose buffer set covers the PARAMETER_BUFFER binding too, per
    /// ARB_indirect_parameters, which is how the count side is included -- and BUFFER_UPDATE_BARRIER_BIT, for
    /// the per-view zeroing. So 2 is the proven floor, pinned by <c>CasterPass.SelfCheck</c>, and 3 is that
    /// floor with one spare.</para>
    /// <para>Not a fence: <c>GpuRing</c> is the CPU walk's instrument because that path writes through a
    /// persistent COHERENT mapping -- a memcpy the command stream never sees, which no barrier can order.
    /// Nothing here is mapped; every write is in-stream. A per-view <c>ClientWaitSync</c> would also be
    /// exactly the per-view CPU cost this pass exists to remove. And not a barrier BEFORE each dispatch,
    /// which would make a rotation of 1 safe only by serialising the dispatch against the previous view's
    /// draws -- the overlap the rotation is here to keep.</para></summary>
    internal const int Rotation = 3;

    /// <summary>Live dial behind <c>/shadow gather</c>: 1 = compute selection, 0 = <see cref="CasterPass"/>'s
    /// CPU per-view walk. Two states rather than three -- the third bisect step the colour path's
    /// <c>/draw cull</c> spells is already spelled here by <c>noshadows=0</c>, which hands the whole shadow
    /// question back to the engine's stencil.</summary>
    internal static int Mode = 1;

    private static ExtenderLog? _log;
    private static uint _prog;
    private static bool _failed;
    private static int _uDrawN, _uPoint, _uFocus, _uDir, _uReach, _uAlong, _uLodR;

    private static uint _srcBuf, _recBuf;
    private static readonly uint[] _cmdBuf = new uint[Rotation], _cntBuf = new uint[Rotation];
    private static int _cmdCap, _cntCap, _at;
    private static int _drawN, _groups;
    private static uint[] _zero = new uint[1024];

    /// <summary>Views dispatched and batches submitted this period -- the gather's half of the caster pass's
    /// period line. There is no survivor count here on purpose: reading one back is the thing this pass
    /// exists to not do.</summary>
    internal static long Views, Batches;

    internal static void Init(ExtenderLog log) => _log = log;

    /// <summary>The GL names went with the context; nothing is deleted (that would be a call into a dead
    /// context) and everything rebuilds on the next frame. See docs/owned-resource-lifetimes.md.</summary>
    internal static void ContextLost()
    {
        _prog = 0; _failed = false;
        _srcBuf = 0; _recBuf = 0; _cmdCap = 0; _cntCap = 0; _at = 0;
        Array.Clear(_cmdBuf); Array.Clear(_cntBuf);
        _drawN = 0; _groups = 0;
    }

    /// <summary>Everything the dispatch needs from GL, checked once and latched: a missing entry point is a
    /// permanent property of the driver and the gather then simply never arms.</summary>
    private static bool Capable()
        => Gl.DispatchCompute != null && Gl.MemoryBarrier != null && Gl.BindBufferBase != null
           && Gl.MultiDrawElementsIndirectCount != null && Gl.BufferSubData != null && Gl.BufferData != null;

    /// <summary>Build the dispatch program. Not an overridable stage (ADR 0012's table is what a content
    /// author may restyle): an override here could only delete shadows, and there is no look to restyle.</summary>
    internal static bool Ensure()
    {
        if (_prog != 0) return true;
        if (_failed || Mode == 0 || !Capable()) return false;
        _prog = GlProgram.Compute(GatherCs, _log, "CasterGather caster cull");
        if (_prog == 0) { _failed = true; return false; }
        _uDrawN = GlProgram.Loc(_prog, "uDrawN");
        _uPoint = GlProgram.Loc(_prog, "uPoint");
        _uFocus = GlProgram.Loc(_prog, "uFocus");
        _uDir = GlProgram.Loc(_prog, "uDir");
        _uReach = GlProgram.Loc(_prog, "uReach");
        _uAlong = GlProgram.Loc(_prog, "uAlong");
        _uLodR = GlProgram.Loc(_prog, "uLodR");
        _log?.Info("CasterGather: armed -- shadow caster selection in compute, " +
                   "glMultiDrawElementsIndirectCount submission (/shadow gather 0 for the CPU walk)");
        return true;
    }

    /// <summary>Is the gather driving this frame's views? False means every view stages on the CPU exactly as
    /// it did before.</summary>
    internal static bool Active => Mode != 0 && _prog != 0 && _drawN > 0;

    /// <summary>Forget the frame's staging. Called before every staging attempt, so a frame that stages
    /// nothing (the knob went off, a collect came back empty, an upload was refused) falls back to the CPU
    /// walk instead of baking the PREVIOUS frame's records into this frame's views.</summary>
    internal static void Stand() => _drawN = 0;

    /// <summary>Upload one frame's staging: the source records (six uints each) and the per-draw
    /// <c>SowxCaster</c> block every view of the frame draws from. Orphan-then-fill rather than a fence ring:
    /// the whole block is a few hundred KB and it is written once per frame, not once per view.</summary>
    internal static bool Begin(int drawN, int groups, uint* src, float* recs, int recFloats)
    {
        _drawN = 0;
        if (drawN <= 0 || groups <= 0 || !Ensure()) return false;
        if (groups > _zero.Length)
        {
            int cap = _zero.Length;
            while (cap < groups) cap *= 2;
            _zero = new uint[cap];
        }

        if (_srcBuf == 0) { uint b; Gl.GenBuffers(1, &b); _srcBuf = b; }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _srcBuf);
        var srcSize = (IntPtr)((long)drawN * SrcUints * sizeof(uint));
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, srcSize, null, Gl.DYNAMIC_DRAW);
        Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, srcSize, src);

        if (_recBuf == 0) { uint b; Gl.GenBuffers(1, &b); _recBuf = b; }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _recBuf);
        var recSize = (IntPtr)((long)drawN * recFloats * sizeof(float));
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, recSize, null, Gl.DYNAMIC_DRAW);
        Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, recSize, recs);
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);

        for (int i = 0; i < Rotation; i++)
        {
            Ensure(ref _cmdBuf[i], (long)drawN * CmdBytes, drawN > _cmdCap);
            Ensure(ref _cntBuf[i], (long)groups * sizeof(uint), groups > _cntCap);
        }
        if (drawN > _cmdCap) _cmdCap = drawN;
        if (groups > _cntCap) _cntCap = groups;
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        _drawN = drawN; _groups = groups;
        return true;
    }

    private static void Ensure(ref uint buf, long bytes, bool grow)
    {
        if (buf == 0) { uint b; Gl.GenBuffers(1, &b); buf = b; grow = true; }
        if (!grow) return;
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, buf);
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)Math.Max(bytes, 4), null, Gl.DYNAMIC_DRAW);
    }

    /// <summary>Select this view's casters. <paramref name="point"/> picks the cube face's cone test over the
    /// cascade fit's axis test, exactly as the CPU walk's own branch does; <paramref name="lodR"/> is this
    /// view's sub-texel radius, and a record whose <c>aux.y</c> marks it a creature is exempt from it.
    /// <para>Binds the per-draw records for the draws that follow, so the caller does not.</para></summary>
    internal static void Dispatch(Vector3 focus, Vector3 dir, float reach, float along, float lodR, bool point)
    {
        _at = (_at + 1) % Rotation;
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _cntBuf[_at]);
        fixed (uint* z = _zero)
            Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, (IntPtr)((long)_groups * sizeof(uint)), z);
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);

        Gl.UseProgram(_prog);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSrc, _srcBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, CasterPass.BindCaster, _recBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCmd, _cmdBuf[_at]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCount, _cntBuf[_at]);
        Gl.Uniform1i(_uDrawN, _drawN);
        Gl.Uniform1i(_uPoint, point ? 1 : 0);
        float* v = stackalloc float[3];
        v[0] = focus.X; v[1] = focus.Y; v[2] = focus.Z;
        Gl.Uniform3fv(_uFocus, 1, v);
        v[0] = dir.X; v[1] = dir.Y; v[2] = dir.Z;
        Gl.Uniform3fv(_uDir, 1, v);
        Gl.Uniform1f(_uReach, reach);
        Gl.Uniform1f(_uAlong, along);
        Gl.Uniform1f(_uLodR, lodR);
        Gl.DispatchCompute((uint)((_drawN + GroupSize - 1) / GroupSize), 1, 1);
        // The commands are consumed as indirect state and the counts as parameter-buffer state; neither is in
        // the shader-storage class, so SHADER_STORAGE alone leaves both undefined.
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.COMMAND_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _cmdBuf[_at]);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, _cntBuf[_at]);
        Views++;
    }

    /// <summary>One batch, out of the compacted list the dispatch just wrote. <paramref name="start"/> is the
    /// batch's own output base (the region the dispatch compacted into), <paramref name="batch"/> indexes the
    /// parameter buffer, and <paramref name="max"/> is the batch's staged length -- the ceiling GL clamps the
    /// count to.</summary>
    internal static void Draw(uint mode, int batch, int start, int max)
    {
        Gl.MultiDrawElementsIndirectCount(mode, CasterPass.GlUnsignedShort,
                                          (void*)((long)start * CmdBytes),
                                          (IntPtr)((long)batch * sizeof(uint)), max, CmdBytes);
        Batches++;
    }

    /// <summary>Hand the indirect bindings back, so nothing later in the frame -- a CPU-walk view under a
    /// mid-session <c>/shadow gather 0</c>, or the colour pass's own submission -- finds them bound.</summary>
    internal static void End()
    {
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, 0);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, 0);
    }

    /// <summary>The cone slope, as the shader spells it. Interpolated from <see cref="CasterPass.FaceConeSlope"/>
    /// so the GPU test cannot drift from the CPU one it is a port of, and pinned by
    /// <c>CasterPass.SelfCheck</c>.</summary>
    private static readonly string Slope =
        CasterPass.FaceConeSlope.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The cull. One thread per staged draw; a survivor appends into its batch's own output region
    /// with an <c>atomicAdd</c>, so every batch keeps its boundaries and only its interior order moves (depth
    /// draws are order-independent).
    /// <para>Both tests are <c>CasterPass.Submit</c>'s, term for term. The cone test is conservative BY
    /// CONSTRUCTION -- it circumscribes the 90deg face pyramid (corners at <c>|perp| = sqrt2 * t</c>) and
    /// allows twice the caster radius -- and that direction is the whole point: admitting a caster the face
    /// cannot see costs a depth draw, rejecting one it can see deletes a shadow.</para></summary>
    private static readonly string GatherCs = ("""
        #version 460 core
        layout(local_size_x = 64) in;   // GroupSize

        """ + CasterPass.CasterBufDecl + """
        layout(std430, binding = 17) readonly buffer SowxGatherSrc  { uint uSrc[]; };
        layout(std430, binding = 18) writeonly buffer SowxGatherCmd { uint uCmd[]; };
        layout(std430, binding = 19) buffer SowxGatherCount         { uint uCnt[]; };
        uniform int uDrawN;
        uniform int uPoint;
        uniform vec3 uFocus;
        uniform vec3 uDir;
        uniform float uReach;
        uniform float uAlong;
        uniform float uLodR;

        bool SowxFaceCulls(float dSq, float t, float r)
        {
            if (t < -r) return true;
            float lim = t * @SLOPE@ + 2.0 * r;
            return lim < 0.0 || dSq - t * t > lim * lim;
        }

        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            if (i >= uint(uDrawN)) return;
            uint b = i * 6u;
            int slot = int(uSrc[b + 3u]);
            SowxCaster c = uCaster[slot];
            float r = c.bounds.w;
            vec3 d = c.bounds.xyz - uFocus;
            float t = dot(d, uDir);
            bool offAxis, offEnds;
            if (uPoint != 0)
            {
                float dSq = dot(d, d);
                offAxis = dSq > (uReach + r) * (uReach + r);
                offEnds = SowxFaceCulls(dSq, t, r);
            }
            else
            {
                vec3 perp = d - t * uDir;
                offAxis = dot(perp, perp) > (uReach + r) * (uReach + r);
                offEnds = abs(t) > uAlong + r;
            }
            if (offAxis || offEnds) return;
            if (r < uLodR && c.aux.y == 0.0) return;   // aux.y != 0: a creature, exempt from the sub-texel cull
            uint o = uSrc[b + 5u] + atomicAdd(uCnt[uSrc[b + 4u]], 1u);
            uint q = o * 5u;
            uCmd[q] = uSrc[b];             // count
            uCmd[q + 1u] = 1u;             // instanceCount
            uCmd[q + 2u] = uSrc[b + 1u];   // firstIndex, in INDICES
            uCmd[q + 3u] = uSrc[b + 2u];   // baseVertex
            uCmd[q + 4u] = uint(slot);     // baseInstance IS the per-draw record index
        }
        """).Replace("@SLOPE@", Slope);

    /// <summary>What <c>CasterPass.SelfCheck</c> pins about the dispatch: the cull it runs is the CPU walk's,
    /// not a re-derivation. Text rather than arithmetic because GLSL cannot be run headless -- but every term
    /// that would silently delete a shadow if it drifted is named here, and the slope is interpolated from
    /// the C# constant rather than typed twice.</summary>
    internal static bool SourceSafe()
        => GatherCs.Contains("if (t < -r) return true;")
           && GatherCs.Contains($"float lim = t * {Slope} + 2.0 * r;")
           && GatherCs.Contains("return lim < 0.0 || dSq - t * t > lim * lim;")
           && GatherCs.Contains("offEnds = abs(t) > uAlong + r;")
           && GatherCs.Contains("offAxis = dot(perp, perp) > (uReach + r) * (uReach + r);")
           && GatherCs.Contains("if (r < uLodR && c.aux.y == 0.0) return;")
           && GatherCs.Contains("uCmd[q + 4u] = uint(slot);")
           && GatherCs.Contains($"binding = {CasterPass.BindCaster}")
           && GatherCs.Contains($"binding = {BindSrc}") && GatherCs.Contains($"binding = {BindCmd}")
           && GatherCs.Contains($"binding = {BindCount}");
}
