using System;
using System.Numerics;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #243 (ADR 0017 step 3): Alloy's own visibility. Until this existed the only visibility input the colour
/// path had was <c>PartTable.InBucketsRecently</c> -- "did the engine's bucket walk touch this part in the
/// last frame or two" -- which is a property of ENROLMENT CADENCE, not of what the camera can see. That is
/// why #236's pop-in was tuned with a window instead of answered.
/// <para>What replaces it: a compute dispatch over the per-draw list that tests each draw's world bounding
/// sphere against this pass's frustum (and, for the colour pass, against a hi-Z pyramid), compacts the
/// survivors into an indirect command buffer and writes a per-batch count. Submission is
/// <c>glMultiDrawElementsIndirectCount</c>, so the count never comes back to the CPU.</para>
/// <para><b>Same-frame, not reprojected.</b> The pyramid is built from #119's depth prepass INSIDE the
/// frame that consumes it: dispatch A (frustum only) feeds the prepass, the prepass depth becomes the
/// pyramid, dispatch B (frustum + hi-Z) feeds the colour pass. Reprojection was rejected because it buys
/// nothing here -- the prepass depth is already paid for, and a pyramid one frame stale is exactly the
/// silhouette-popping artefact the ticket names. What the prepass leaves out (cutouts, discarding classes
/// it drops, everything the engine still draws) only leaves the pyramid holding the FARTHER depth that is
/// actually behind them, and a farther max culls less. The direction that matters, and that #243's first
/// live run got wrong: the test is <c>objectNear &gt; maxSceneDepth</c>, so anything that LOWERS the max
/// culls MORE. Over-covering the sampled region is safe; under-covering it is the bug.</para>
/// <para>#287 adds LOD selection to the same dispatch (<see cref="LodPx"/>) and deletes the per-pass CPU
/// rebuild of its source list: the batch walk stages the six-uint record where it already stages the draw's
/// count, offset and base, and this class uploads it. What is left on the CPU is the batch CUT alone, whose
/// keys are engine pointers read during revalidation and cannot be asked of the GPU.</para>
/// <para><b>Two kinds of batch (#290).</b> Below <c>compactBelow</c> a batch COMPACTS: an <c>atomicAdd</c>
/// appends survivors in whatever order the hardware hands them out, which is sound for the opaque range
/// because its draws are order-independent. At and above it a batch is SORTED: the first dispatch measures
/// each survivor's squared distance from the eye, and a second dispatch gives it its slot by counting how
/// many survivors of the same batch are farther away. See <see cref="SortCs"/>.</para>
/// <para>That is #287's deferred ordering question, answered on the GPU: nothing about WHICH transparent
/// draws exist or what order they composite in comes back to the CPU. The key is measured from the bounding
/// sphere already resident in the SSBO, the compare is on the float key rather than a quantised one (so two
/// coplanar surfaces cannot tie into the wrong order), the tie-break is the staged index (so equal-distance
/// draws hold their texture-grouped enrollment order frame to frame), and the per-draw alpha multiplier
/// cannot come unpaired from its index because it rides the RECORD, which the permutation names through
/// <c>uOut</c> rather than reorders.</para>
/// <para>Cutout batches blend and ride the COMPACTING side -- overlapping cutouts can therefore composite in
/// a different order from frame to frame. Named rather than hidden: they depth-write with a discard, so the
/// visible consequence is at most a shimmer where two graded edges overlap, and <c>/draw cull 0</c> is the
/// A/B that proves it either way.</para>
/// <para>Stands down whole when 4.6 indirect-count or compute is missing, when the programs fail to build,
/// or when the knob is off -- and standing down means the caller's existing <c>BaseVertex</c> submission,
/// which is unchanged. A cull that cannot run must never mean an empty world.</para>
/// </summary>
internal static unsafe class CullPass
{
    /// <summary>SSBO bindings. 0..3 are <see cref="AlloyLights"/>, 4 the per-draw records, 5/6
    /// <see cref="CasterPass"/>, 7 the Alloy bones, 8 the bindless handles, 9 the per-draw index array,
    /// 10/11 <see cref="Particles"/>. Everything here is private to the dispatch except
    /// <see cref="AlloyRenderer.BindDrawIdx"/>, which the compacted slot buffer takes at DRAW time.</summary>
    internal const uint BindSrc = 12, BindSphere = 13, BindCmd = 14, BindOutSlot = 15, BindCount = 16,
                        BindKey = 17;

    /// <summary>Uints per source record: index count, first index, base vertex, record slot, batch, and the
    /// batch's output base. Six rather than five because the batch's start offset is what turns an
    /// <c>atomicAdd</c> into an output index without a second buffer to look it up in.
    /// <para>#287: STAGED BY THE CALLER, at the one site that already writes the same draw's
    /// <c>_dCount/_dOff/_dBase/_dSlot</c>. This class used to rebuild it in a second walk of its own every
    /// pass, which was the CPU half of "which draws exist" the ticket names; the batch and its output base
    /// are known at that store (the cut has just placed the draw), so the rebuild folds into it at no
    /// loop.</para></summary>
    internal const int SrcUints = 6;
    /// <summary>Uints per <c>DrawElementsIndirectCommand</c>: count, instanceCount, firstIndex, baseVertex,
    /// baseInstance. Fixed by GL, and the stride the draw calls pass.</summary>
    internal const int CmdUints = 5;

    /// <summary>Compute workgroup size; the dispatch is 1D over draws.</summary>
    private const int GroupSize = 64;

    /// <summary>The two dispatch phases, and the two output sets they fill.</summary>
    internal const int PhasePrepass = 0, PhaseColour = 1;

    private static ExtenderLog? _log;
    private static uint _prog, _progHiZ, _progSort;
    private static bool _failed;
    private static int _uDrawN, _uPlanes, _uHiZ, _uVP, _uPyr, _uPyrSize, _uMaxLod;
    private static int _uLodPx, _uLodScale, _uCompactBelow, _uEye;
    private static int _sDrawN, _sCompactBelow;
    private static int _hSrc, _hLevel;

    private static uint _srcBuf, _sphBuf, _keyBuf;
    private static readonly uint[] _cmdBuf = new uint[2];
    private static readonly uint[] _slotBuf = new uint[2];
    private static readonly uint[] _cntBuf = new uint[2];
    private static int _cmdCap, _cntCap, _sphCap;

    private static uint _pyrTex, _pyrFbo, _pyrVao;
    private static int _pyrW, _pyrH, _pyrLevels;
    /// <summary>The pass generation the pyramid was last BUILT for. Not "a pyramid exists": a declined build
    /// (no depth texture, a zero viewport) would otherwise leave the previous frame's pyramid armed, which
    /// is the reprojected cull this pass exists to not be.</summary>
    private static int _pyrGen = -1;

    private static uint[] _zero = new uint[1024];

    /// <summary>#243 (AC5): what the last readable dispatch actually kept. Copied out of the count buffer
    /// three frames late through a small ring, so the period line can report culled-vs-drawn without a
    /// readback anywhere near the submission path.</summary>
    private static readonly uint[] _readBuf = new uint[3];
    /// <summary>How many count words each ring slot actually holds. Without it a frame whose batch cut came
    /// out shorter reads the tail of a copy three frames older as if it were counts -- which is exactly the
    /// "keeps 7474870/frame" nonsense the first live run printed.</summary>
    private static readonly int[] _readN = new int[3];
    private static readonly int[] _readDrawN = new int[3];
    private static uint[] _counts = new uint[1024];
    private static int _readSeq, _readCap;
    internal static long CulledDraws, KeptDraws, CullFrames;

    /// <summary>Live dial behind <c>/draw cull</c>, and deliberately three-state rather than two: culling is
    /// the one system whose bug is invisible geometry, and "something is missing" has two possible authors
    /// here. <b>0</b> stands the whole pass down -- both submissions back on <c>BaseVertex</c> and, since
    /// #264 deleted the engine walk, EVERY owned part drawn every frame with no cull anywhere, which is slow
    /// and correct and is exactly what makes it the A/B for "is the cull eating this?"; <b>1</b> is the
    /// frustum test alone; <b>2</b> adds the hi-Z occlusion test. Reporting a hole means saying which of the
    /// three it survives.</summary>
    internal static int Mode = ModeHiZ;
    internal const int ModeOff = 0, ModeFrustum = 1, ModeHiZ = 2;

    /// <summary>#287: LOD selection, in pixels. A draw whose bounding sphere projects to a screen radius
    /// smaller than this is dropped by the same dispatch that culls it -- the classic distance thinning NWN
    /// never had. 0 disables it; <c>/draw lod</c> is the dial and <c>/draw cull 0</c> stands it down with
    /// everything else, because it IS the cull shader.
    /// <para>#152 shipped this on the CPU and 73ea59b deleted it, measured: <c>LOD skipped 0 sub-pixel
    /// draws/frame</c> in every period of the live log, because the tile exemption it needed (a small TILE
    /// part is a patch of wall, and culling it is a hole in a surface) excluded most of what a distant
    /// camera sees. There is no exemption here. On the GPU the test is free, the hole it can leave is by
    /// construction under <see cref="LodPx"/> pixels wide, and the exempt population that remains is the one
    /// the cull already exempts -- <see cref="AlloyRenderer.NeverCull"/> bounds.</para></summary>
    internal static float LodPx = DefLodPx;
    /// <summary>The boot threshold, and #152's own: one pixel of projected radius.</summary>
    internal const float DefLodPx = 1.0f;

    /// <summary>#336: pull the far plane in to the fog's opaque distance. <c>SowxFog</c> clamps its lerp, so
    /// a fragment past <c>FogEnd</c> is EXACTLY the fog colour -- it is drawn, lit, prepassed and shadowed to
    /// produce a pixel the fog would have painted anyway. Measured: camfar 215 against a fog end of 125, so
    /// two thirds of the ground area in the frustum was past it. <c>/draw fogcull</c>.</summary>
    internal static bool FogCull = DefFogCull;
    internal const bool DefFogCull = true;

    /// <summary>Pixels per unit of world radius at unit view depth -- <c>proj.M22 * viewportHeight / 2</c>,
    /// so <c>radius * this / depth</c> is the sphere's screen radius. Zero for a pass whose projection is not
    /// a perspective one, and the LOD test stands down rather than dividing by a scale it cannot trust.
    /// </summary>
    private static float _lodScale;

    /// <summary>#290: this pass's eye, the sort key's one input. The same quantity
    /// <c>AlloyRenderer.EyeDist2</c> measures from on the CPU, so the two orders agree and
    /// <c>/draw cull 0</c> is a like-for-like A/B rather than a different sort.</summary>
    private static Vector3 _eye;

    /// <summary>The pass can actually run: the programs built and every entry point it needs exists.</summary>
    internal static bool Ready => _prog != 0;

    /// <summary>#243: the extent of the staged sphere CENTRES, and the eye they were culled against. A
    /// diagnostic with one job: the bounds are derived from <c>PartTriMesh</c>'s bbox, which is world-space
    /// for a world-space part and object-space for a matrix-class one, and that reading has never been
    /// verified for TILES. If it is wrong for them every tile's sphere collapses toward one point, and this
    /// line says so -- a centre spread far smaller than the area is the signature.</summary>
    internal static float SpreadX, SpreadY, SpreadZ, SpreadMaxR;
    private static int _drawN, _groups, _phase = -1;

    /// <summary>#290: the first SORTED batch -- everything below it compacts in the first dispatch, and
    /// everything at or above it is placed by the second. Kept because the dispatch needs it as a uniform
    /// and <see cref="SortCs"/> needs it to know which draws are its.</summary>
    private static int _compactBelow;


    /// <summary>#287: the diagnostic above, measured by the staging walk that already touches every one of
    /// these spheres. It used to be a second walk here; the extent is the same extent either way.</summary>
    internal static void SetSpread(float x, float y, float z, float maxR)
    { SpreadX = x; SpreadY = y; SpreadZ = z; SpreadMaxR = maxR; }

    internal static void Init(ExtenderLog log) => _log = log;

    /// <summary>The GL names are gone with the context; nothing is deleted (that would be a call into a
    /// dead context) and everything rebuilds on the next pass. See docs/owned-resource-lifetimes.md.</summary>
    internal static void ContextLost()
    {
        _prog = 0; _progHiZ = 0; _progSort = 0; _failed = false;
        _srcBuf = 0; _sphBuf = 0; _keyBuf = 0; _cmdCap = 0; _cntCap = 0; _sphCap = 0;
        Array.Clear(_cmdBuf); Array.Clear(_slotBuf); Array.Clear(_cntBuf);
        Array.Clear(_readBuf); Array.Clear(_readN); Array.Clear(_readDrawN); _readCap = 0;
        _pyrTex = 0; _pyrFbo = 0; _pyrVao = 0; _pyrW = 0; _pyrH = 0; _pyrLevels = 0; _pyrGen = -1;
        _phase = -1; _compactBelow = 0;
    }

    /// <summary>Everything the dispatch needs from GL. #264 promoted this to a BOOT term: with the engine's
    /// bucket walk deleted there is no other visibility test in the frame, so a driver that cannot run the
    /// dispatch cannot run Alloy at all -- see <see cref="AlloyGate"/>.</summary>
    internal static bool Capable()
        => Gl.DispatchCompute != null && Gl.MemoryBarrier != null && Gl.BindBufferBase != null
           && Gl.MultiDrawElementsIndirectCount != null && Gl.BufferSubData != null
           && Gl.GenFramebuffers != null && Gl.FramebufferTexture2D != null && Gl.TexStorage2D != null
           && Gl.CopyBufferSubData != null && Gl.GetBufferSubData != null;

    /// <summary>Build the two programs. Neither is an overridable stage (ADR 0012's table is the set a
    /// content author can restyle): an override here could only make the world invisible, and there is no
    /// look to restyle.</summary>
    private static bool Ensure()
    {
        if (_prog != 0) return true;
        if (_failed || !Capable()) return false;
        _prog = GlProgram.Compute(CullCs, _log, "CullPass frustum/hi-Z cull");
        _progSort = GlProgram.Compute(SortCs, _log, "CullPass back-to-front rank sort");
        _progHiZ = GlProgram.Graphics(PostChain.FullscreenVs, HiZFs, _log, "CullPass hi-Z pyramid");
        if (_prog == 0 || _progHiZ == 0 || _progSort == 0)
        {
            _failed = true; _prog = 0;
            // #264: hard, not a downgrade. The frame has no other visibility test, and the boot verdict has
            // already frozen on Alloy by the time a program can fail to build.
            AlloyGate.Fail("the cull compute program did not build -- Alloy has no visibility test");
            return false;
        }
        _uDrawN = GlProgram.Loc(_prog, "uDrawN");
        _uPlanes = GlProgram.Loc(_prog, "uPlanes");
        _uHiZ = GlProgram.Loc(_prog, "uHiZ");
        _uVP = GlProgram.Loc(_prog, "uVP");
        _uPyr = GlProgram.Loc(_prog, "uPyramid");
        _uPyrSize = GlProgram.Loc(_prog, "uPyrSize");
        _uMaxLod = GlProgram.Loc(_prog, "uMaxLod");
        _uLodPx = GlProgram.Loc(_prog, "uLodPx");
        _uLodScale = GlProgram.Loc(_prog, "uLodScale");
        _uCompactBelow = GlProgram.Loc(_prog, "uCompactBelow");
        _uEye = GlProgram.Loc(_prog, "uEye");
        _sDrawN = GlProgram.Loc(_progSort, "uDrawN");
        _sCompactBelow = GlProgram.Loc(_progSort, "uCompactBelow");
        _hSrc = GlProgram.Loc(_progHiZ, "uSrc");
        _hLevel = GlProgram.Loc(_progHiZ, "uLevel");
        if (_pyrVao == 0) { uint v; Gl.GenVertexArrays(1, &v); _pyrVao = v; }
        _log?.Info("CullPass: armed -- GPU frustum + hi-Z cull, GPU back-to-front sort of the blending "
                   + "ranges, glMultiDrawElementsIndirectCount submission");
        return true;
    }

    /// <summary>Is the cull driving this pass's opaque submission? Asked by the two issue sites, and the
    /// ONLY thing that may switch them off the <c>BaseVertex</c> path.</summary>
    internal static bool Active => Mode > ModeOff && _phase >= 0;

    /// <summary>Upload the pass's draw list and the per-record bounding spheres. Returns false when
    /// the cull is not running this pass, and the caller then submits exactly as before.
    /// <para>#290: <paramref name="groups"/> is now EVERY batch of the pass, and
    /// <paramref name="compactBelow"/> is where the compacting half ends -- the opaque batch count. Batches
    /// at or above it are SORTED back-to-front by the second dispatch (see the class summary), which the
    /// and additive ranges are placed by the sort dispatch instead of the atomic.</para>
    /// <para>#287: <paramref name="src"/> is <see cref="SrcUints"/> uints per draw, staged by the batch walk
    /// itself -- see <see cref="SrcUints"/>. Nothing per-draw is computed here any more; this is an upload
    /// and a set of buffer sizes.</para>
    /// <para>#303: the spheres are no longer a parameter -- <see cref="UploadSpheres"/> carries them, outside
    /// the Mode gate, because the shadow gather culls against the same buffer whatever <c>/draw cull</c>
    /// says.</para></summary>
    internal static bool Begin(int groups, int drawN, uint* src,
                              float lodScale, int compactBelow, Vector3 eye)
    {
        _phase = -1;
        if (Mode <= ModeOff || drawN <= 0 || groups <= 0 || _sphBuf == 0 || !Ensure()) return false;
        // The per-batch count array is indexed by batch; the zero-fill in Dispatch is what resets it. GROWN
        // rather than capped: a refusal here would be a refusal AFTER the staging walk had already dropped
        // the engine's visibility gate on the strength of Ready, and the frame would then draw the whole
        // area unculled -- the cap has to not exist rather than be generous.
        if (groups > _zero.Length)
        {
            int cap = _zero.Length;
            while (cap < groups) cap *= 2;
            _zero = new uint[cap]; _counts = new uint[cap];
        }

        _drawN = drawN; _groups = groups; _lodScale = lodScale;
        _compactBelow = Math.Clamp(compactBelow, 0, groups);
        _eye = eye;

        if (_srcBuf == 0) { uint b; Gl.GenBuffers(1, &b); _srcBuf = b; }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _srcBuf);
        var srcSize = (IntPtr)((long)drawN * SrcUints * sizeof(uint));
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, srcSize, null, Gl.DYNAMIC_DRAW);
        Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, srcSize, src);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSrc, _srcBuf);

        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSphere, _sphBuf);

        for (int i = 0; i < 2; i++)
        {
            Ensure(ref _cmdBuf[i], (long)drawN * CmdUints * sizeof(uint), drawN > _cmdCap);
            Ensure(ref _slotBuf[i], (long)drawN * sizeof(uint), drawN > _cmdCap);
            Ensure(ref _cntBuf[i], (long)groups * sizeof(uint), groups > _cntCap);
        }
        // #290: the sort's scratch -- one key per draw, negative for a draw the cull dropped. One buffer
        // rather than a key array beside a keep array: distances are never negative, so the sign IS the
        // verdict, and the rank scan reads one word per neighbour instead of two.
        Ensure(ref _keyBuf, (long)drawN * sizeof(float), drawN > _cmdCap);
        if (drawN > _cmdCap) _cmdCap = drawN;
        if (groups > _cntCap) _cntCap = groups;
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        _phase = -2;   // staged; the first Dispatch publishes a phase
        return true;
    }

    /// <summary>#303: the per-ownership-slot <c>SowxCullSphere</c> upload, hoisted out of <see cref="Begin"/>
    /// and out of the Mode gate: the shadow gather culls against this same buffer, so it must stay current
    /// under <c>/draw cull 0</c> and during a probe capture alike. The spheres are indexed by RECORD slot,
    /// exactly as the records are, and go up whole -- four floats against the record's forty-four, changing
    /// on the same edges, so a dirty list here would be #242's machinery again for a fortieth of the
    /// traffic.</summary>
    internal static void UploadSpheres(float* spheres, int sphereSlots)
    {
        if (sphereSlots <= 0 || Gl.BufferSubData == null) return;
        if (_sphBuf == 0) { uint b; Gl.GenBuffers(1, &b); _sphBuf = b; }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _sphBuf);
        var sphSize = (IntPtr)((long)sphereSlots * 4 * sizeof(float));
        if (sphereSlots > _sphCap) { Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, sphSize, null, Gl.DYNAMIC_DRAW); _sphCap = sphereSlots; }
        Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, sphSize, spheres);
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
    }

    /// <summary>The shared sphere buffer's GL name, for the shadow gather's dispatch. 0 until the first
    /// colour build uploads it, and the gather stands down for that frame.</summary>
    internal static uint SphereBuf => _sphBuf;

    private static void Ensure(ref uint buf, long bytes, bool grow)
    {
        if (buf == 0) { uint b; Gl.GenBuffers(1, &b); buf = b; grow = true; }
        if (!grow) return;
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, buf);
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)Math.Max(bytes, 4), null, Gl.DYNAMIC_DRAW);
    }

    /// <summary>Run one dispatch into output set <paramref name="phase"/>. <paramref name="hiZ"/> false is
    /// the frustum-only pass the depth prepass consumes; true adds the occlusion test against the pyramid
    /// the prepass just filled.</summary>
    internal static void Dispatch(int phase, Matrix4x4* vp, bool hiZ, int gen)
    {
        if (_phase == -1 || _drawN <= 0) return;
        // Zero the counters. glClearBufferData is not in the loader, and a BufferSubData of a few hundred
        // bytes off a static zero array costs nothing next to the dispatch it precedes.
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _cntBuf[phase]);
        fixed (uint* z = _zero)
            Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, (IntPtr)((long)_groups * sizeof(uint)), z);
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);

        Gl.UseProgram(_prog);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSrc, _srcBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSphere, _sphBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCmd, _cmdBuf[phase]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindOutSlot, _slotBuf[phase]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCount, _cntBuf[phase]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindKey, _keyBuf);
        Gl.Uniform1i(_uDrawN, _drawN);
        Gl.UniformMatrix4fv(_uVP, 1, 0, (float*)vp);
        float* planes = stackalloc float[24];
        Planes((float*)vp, planes, _eye);
        Gl.Uniform4fv(_uPlanes, 6, planes);
        Gl.Uniform1f(_uLodPx, MathF.Max(LodPx, 0f));
        Gl.Uniform1f(_uLodScale, _lodScale);
        Gl.Uniform1i(_uCompactBelow, _compactBelow);
        fixed (Vector3* e = &_eye) Gl.Uniform3fv(_uEye, 1, (float*)e);
        bool useHiZ = hiZ && Mode >= ModeHiZ && _pyrTex != 0 && _pyrLevels > 0 && _pyrGen == gen;
        Gl.Uniform1i(_uHiZ, useHiZ ? 1 : 0);
        if (useHiZ)
        {
            Gl.Uniform1i(_uPyr, (int)PyramidUnit);
            Gl.Uniform2f(_uPyrSize, _pyrW, _pyrH);
            Gl.Uniform1f(_uMaxLod, _pyrLevels - 1);
            Gl.ActiveTexture(Gl.TEXTURE0 + PyramidUnit);
            Gl.BindTexture(Gl.TEXTURE_2D, _pyrTex);
            Gl.ActiveTexture(Gl.TEXTURE0);
        }
        uint groups1D = (uint)((_drawN + GroupSize - 1) / GroupSize);
        Gl.DispatchCompute(groups1D, 1, 1);
        // #290: the sort is a SECOND dispatch, and the barrier between them is load-bearing -- a draw ranks
        // itself against keys every other invocation of the first dispatch wrote, so none of them may still
        // be in flight. One pass cannot do both: a rank needs the whole batch's verdicts decided.
        if (_compactBelow < _groups)
        {
            Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT);
            Gl.UseProgram(_progSort);
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSrc, _srcBuf);
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCmd, _cmdBuf[phase]);
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindOutSlot, _slotBuf[phase]);
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindKey, _keyBuf);
            Gl.Uniform1i(_sDrawN, _drawN);
            Gl.Uniform1i(_sCompactBelow, _compactBelow);
            Gl.DispatchCompute(groups1D, 1, 1);
        }
        // Both consumers are outside the shader-storage class: the commands are read as indirect state and
        // the count as parameter-buffer state. SHADER_STORAGE alone leaves both undefined.
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.COMMAND_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        // The pyramid's unit is shared with the probe's cube (TextureSlots.HiZ): leave nothing of ours on it.
        if (useHiZ)
        {
            Gl.ActiveTexture(Gl.TEXTURE0 + PyramidUnit);
            Gl.BindTexture(Gl.TEXTURE_2D, 0);
            Gl.ActiveTexture(Gl.TEXTURE0);
        }
        _phase = phase;
        if (phase == PhaseColour) Sample();
    }

    /// <summary>Texture unit the pyramid takes for the dispatch -- see <see cref="TextureSlots.HiZ"/> for
    /// why it is shared rather than its own.</summary>
    private const uint PyramidUnit = TextureSlots.HiZ;

    /// <summary>Bind the compacted list of output set <paramref name="phase"/> for submission: the indirect
    /// commands, the parameter buffer the count comes from, and the per-draw slot array the vertex stages
    /// already read at <see cref="AlloyRenderer.BindDrawIdx"/>. The stages are untouched -- the compacted
    /// array holds the same record slots in a different order, so <c>uDraw[uDrawIdx[uDrawBase + gl_DrawID]]</c>
    /// resolves exactly as it did (and #119's clip expression never moves).</summary>
    internal static void BindForDraw(int phase)
    {
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _cmdBuf[phase]);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, _cntBuf[phase]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, AlloyRenderer.BindDrawIdx, _slotBuf[phase]);
    }

    /// <summary>...and hand the bindings back, so a later pass on the <c>BaseVertex</c> path (the blended
    /// and water ranges) does not find an indirect buffer bound under it.</summary>
    internal static void UnbindForDraw()
    {
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, 0);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, 0);
    }

    /// <summary>One batch, submitted from the compacted list. <paramref name="start"/> is the batch's
    /// output base -- the same <c>_gStart[g]</c> the record lookup uses as <c>uDrawBase</c>, because the
    /// cull compacts each batch into its OWN source region rather than into one global run -- and
    /// <paramref name="batch"/> indexes the parameter buffer, whose word <c>g</c> is that batch's survivor
    /// count. <paramref name="max"/> is the batch's staged length, the ceiling GL clamps the count to.</summary>
    internal static void Draw(uint mode, int batch, int start, int max)
        => Gl.MultiDrawElementsIndirectCount(mode, AlloyRenderer.GlUnsignedShort,
                                             (void*)((long)start * CmdUints * sizeof(uint)),
                                             (IntPtr)((long)batch * sizeof(uint)),
                                             max, CmdUints * sizeof(uint));

    /// <summary>Build the hi-Z pyramid from the depth the prepass just wrote. Level 0 is a 2x2 MAX reduction
    /// of the depth buffer (so the pyramid is half resolution and every level after it chains from the one
    /// above), and MAX is what makes the test conservative: an object is culled only when its NEAREST depth
    /// is behind the FARTHEST scene depth anywhere in the region it covers.
    /// <para>Restores the framebuffer, viewport and program the caller had -- the colour bracket is mid-pass
    /// and everything it set is still live.</para></summary>
    internal static void BuildHiZ(uint depthTex, int w, int h, int* viewport, uint restoreFbo, int gen)
    {
        _pyrGen = -1;
        if (_phase == -1 || _progHiZ == 0 || depthTex == 0 || w <= 0 || h <= 0) return;
        if (!EnsurePyramid(w / 2, h / 2)) return;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _pyrFbo);
        // NOT a PassState: this runs INSIDE the colour bracket, between the prepass and the opaque pass, and
        // the bracket's own state (depth test on, depth writes on, sRGB as the target declared it) has to be
        // exactly what the opaque pass finds. Only the two things a full-screen reduction cannot tolerate
        // are touched, and both are put back below.
        Gl.Disable(Gl.DEPTH_TEST);
        Gl.DepthMask(0);
        Gl.BindVertexArray(_pyrVao);
        Gl.UseProgram(_progHiZ);
        Gl.Uniform1i(_hSrc, (int)PyramidUnit);
        Gl.ActiveTexture(Gl.TEXTURE0 + PyramidUnit);
        for (int i = 0; i < _pyrLevels; i++)
        {
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _pyrTex, i);
            Gl.Viewport(0, 0, Math.Max(_pyrW >> i, 1), Math.Max(_pyrH >> i, 1));
            if (i == 0) { Gl.BindTexture(Gl.TEXTURE_2D, depthTex); Gl.Uniform1i(_hLevel, 0); }
            else
            {
                // Reading level i-1 of the texture level i is attached to: legal only while BASE/MAX_LEVEL
                // exclude the attachment, which is exactly what the clamp is for (PostChain's bloom chain
                // takes the same trade for the same reason).
                Gl.BindTexture(Gl.TEXTURE_2D, _pyrTex);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_BASE_LEVEL, i - 1);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, i - 1);
                Gl.Uniform1i(_hLevel, 0);
            }
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        }
        // The dispatch samples arbitrary LODs, so the clamp has to come back off.
        Gl.BindTexture(Gl.TEXTURE_2D, _pyrTex);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_BASE_LEVEL, 0);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, _pyrLevels - 1);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, restoreFbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.DepthMask(1);
        _pyrGen = gen;
    }

    private static bool EnsurePyramid(int w, int h)
    {
        w = Math.Max(w, 1); h = Math.Max(h, 1);
        if (_pyrTex != 0 && w == _pyrW && h == _pyrH) return true;
        if (_pyrTex != 0) { uint t = _pyrTex; Gl.DeleteTextures(1, &t); _pyrTex = 0; }
        int levels = 1;
        while ((w >> levels) > 0 || (h >> levels) > 0) levels++;
        uint tex; Gl.GenTextures(1, &tex);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
        Gl.TexStorage2D(Gl.TEXTURE_2D, levels, Gl.R32F, w, h);
        // NEAREST all the way: an interpolated max is not a max, and a filtered pyramid would cull against
        // depths that are in no texel of it.
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST_MIPMAP_NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        if (_pyrFbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _pyrFbo = f; }
        _pyrTex = tex; _pyrW = w; _pyrH = h; _pyrLevels = levels;
        _log?.Info($"CullPass: hi-Z pyramid {w}x{h}, {levels} levels (R32F, max reduction)");
        return true;
    }

    /// <summary>#243 (AC5): the culled-vs-drawn figure, taken three frames behind the dispatch that produced
    /// it. A <c>glCopyBufferSubData</c> into a small ring is a GPU-side copy; the read that follows names a
    /// buffer the GPU finished with two frames ago, so it is not the readback the ticket forbids -- nothing
    /// on the submission path waits for it.</summary>
    private static void Sample()
    {
        if (_groups <= 0) return;
        int w = _readSeq % 3, r = (_readSeq + 1) % 3;
        _readSeq++;
        var bytes = (IntPtr)((long)_groups * sizeof(uint));
        Ensure(ref _readBuf[w], (long)_counts.Length * sizeof(uint),
               _readBuf[w] == 0 || _counts.Length > _readCap);
        _readCap = _counts.Length;
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, _cntBuf[PhaseColour]);
        Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, _readBuf[w]);
        Gl.CopyBufferSubData(Gl.COPY_READ_BUFFER, Gl.COPY_WRITE_BUFFER, IntPtr.Zero, IntPtr.Zero, bytes);
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, 0);
        Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, 0);
        _readN[w] = _groups; _readDrawN[w] = _drawN;
        if (_readBuf[r] == 0 || _readN[r] <= 0) return;
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, _readBuf[r]);
        fixed (uint* c = _counts)
            Gl.GetBufferSubData(Gl.COPY_READ_BUFFER, IntPtr.Zero, (IntPtr)((long)_readN[r] * sizeof(uint)), c);
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, 0);
        long kept = 0;
        for (int i = 0; i < _readN[r]; i++) kept += _counts[i];
        // Against the draw count of the frame that WROTE those counts, not this frame's.
        KeptDraws += Math.Min(kept, _readDrawN[r]);
        CulledDraws += Math.Max(_readDrawN[r] - kept, 0);
        CullFrames++;
    }

    internal static void ResetStats() { CulledDraws = 0; KeptDraws = 0; CullFrames = 0; }

    /// <summary>The six frustum planes of a row-major <c>view*proj</c>, Gribb-Hartmann, normalised so the
    /// plane distance is in world units and the sphere test is a plain compare. Row-major here means the
    /// combination is <c>clip = v * VP</c>, so the planes come off the COLUMNS -- the transpose of the form
    /// the paper writes for column-vector convention.</summary>
    internal static void Planes(float* m, float* p, Vector3 eye)
    {
        // m[r*4+c] is row r, column c of the row-vector matrix: clip.c = sum_r v[r] * m[r*4+c].
        for (int i = 0; i < 6; i++)
        {
            int c = i >> 1;
            float s = (i & 1) == 0 ? 1f : -1f;
            float x = m[0 * 4 + 3] + s * m[0 * 4 + c];
            float y = m[1 * 4 + 3] + s * m[1 * 4 + c];
            float z = m[2 * 4 + 3] + s * m[2 * 4 + c];
            float w = m[3 * 4 + 3] + s * m[3 * 4 + c];
            float len = MathF.Sqrt(x * x + y * y + z * z);
            if (len > 0f) { x /= len; y /= len; z /= len; w /= len; }
            p[i * 4 + 0] = x; p[i * 4 + 1] = y; p[i * 4 + 2] = z; p[i * 4 + 3] = w;
        }
        if (!FogCull || !FrameInputs.FogOn || FrameInputs.FogEnd <= 0f) return;
        // Plane 5 is the far one (i>>1 == 2, w - z). Normalised, its signed distance at the eye IS the far
        // distance, so subtracting the excess slides it in along its own normal to the fog end exactly.
        // The eye is the one THIS vp was built from, so a mirrored subframe measures from its own camera.
        float far = p[5 * 4 + 0] * eye.X + p[5 * 4 + 1] * eye.Y + p[5 * 4 + 2] * eye.Z + p[5 * 4 + 3];
        if (far > FrameInputs.FogEnd) p[5 * 4 + 3] -= far - FrameInputs.FogEnd;
    }

    /// <summary>Headless check of the two pieces that are arithmetic rather than GL: the plane extraction
    /// and the compacted list itself, against the CPU-staged one the <c>BaseVertex</c> path would submit.
    /// Both fail silently in the worst possible way -- a wrong plane culls the world, a wrong compaction
    /// draws a neighbour's geometry -- and neither needs a context.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"CullPass.SelfCheck FAIL: {w}"); } }

        // A right-handed look-down-(-Z) camera in the row-vector convention the renderer uses.
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 0), new Vector3(0, 0, -10), Vector3.UnitY);
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 1.0f, 1f, 100f);
        Matrix4x4 vp = view * proj;
        float* planes = stackalloc float[24];
        Planes((float*)&vp, planes, Vector3.Zero);
        bool Inside(Vector3 pt, float r)
        {
            for (int i = 0; i < 6; i++)
                if (planes[i * 4] * pt.X + planes[i * 4 + 1] * pt.Y + planes[i * 4 + 2] * pt.Z
                    + planes[i * 4 + 3] < -r) return false;
            return true;
        }
        E(Inside(new Vector3(0, 0, -10), 1f), "a point straight ahead is inside the frustum");
        E(!Inside(new Vector3(0, 0, 10), 1f), "a point BEHIND the camera is outside it");
        E(!Inside(new Vector3(0, 0, -500), 1f), "a point past the far plane is outside it");
        E(!Inside(new Vector3(200, 0, -10), 1f), "a point far off to the side is outside it");
        E(Inside(new Vector3(200, 0, -10), 400f), "...and is back inside once its radius reaches the camera");

        // #336: the fog clamp slides plane 5 in, and nothing else. A wrong sign here culls the whole world
        // or culls nothing, and neither shows up until someone walks into a foggy area.
        bool fogWas = FrameInputs.FogOn; float endWas = FrameInputs.FogEnd; Vector3 camWas = FrameInputs.CameraPos;
        FrameInputs.FogOn = true; FrameInputs.FogEnd = 50f; FrameInputs.CameraPos = Vector3.Zero;
        Planes((float*)&vp, planes, Vector3.Zero);
        E(Inside(new Vector3(0, 0, -40), 1f), "fog cull: a point inside the fog end survives");
        E(!Inside(new Vector3(0, 0, -60), 1f), "fog cull: a point past the fog end is culled");
        E(Inside(new Vector3(0, 0, -60), 15f), "...and a sphere reaching back inside it survives");
        FrameInputs.FogOn = fogWas; FrameInputs.FogEnd = endWas; FrameInputs.CameraPos = camWas;
        Planes((float*)&vp, planes, Vector3.Zero);
        E(!Inside(new Vector3(0, 0, -500), 1f), "fog off: the far plane is the engine's again");

        // The pack, against what the shader reads out of it. Not a GL call in sight: this is a memory layout
        // shared with GLSL, and every field landing one slot over is a draw reading its neighbour's range.
        uint* src = stackalloc uint[2 * SrcUints];
        Pack(src, 30, 200, 7, 11, 0, 0);
        Pack(src + SrcUints, 60, 400, 9, 13, 0, 0);
        E(src[0] == 30 && src[1] == 100 && src[2] == 7 && src[3] == 11 && src[4] == 0 && src[5] == 0,
          "draw 0 packs count/firstIndex/baseVertex/slot/batch/start in that order");
        E(src[SrcUints + 1] == 200, "...and the byte offset becomes an ELEMENT index (two-byte indices)");
        E(CmdUints * sizeof(uint) == 20, "the indirect command stride is GL's 20 bytes");

        ok &= Parity(log, vp, planes);
        log.Info(ok ? "CullPass.SelfCheck: passed" : "CullPass.SelfCheck: FAILED -- see above");
    }

    /// <summary>#287: one draw's source record, written where the batch walk already writes the same draw's
    /// count/offset/base. The one place the layout <see cref="CullCs"/> reads back is spelled out.</summary>
    internal static void Pack(uint* r, int idxCount, nint byteOff, int baseVertex, int slot,
                             int batch, int batchStart)
    {
        r[0] = (uint)idxCount;
        // The indirect command's firstIndex is in ELEMENTS; the staged offset is pre-multiplied to the byte
        // offset MultiDrawElementsBaseVertex takes. Two-byte indices, so the halving is exact.
        r[1] = (uint)((long)byteOff / 2);
        r[2] = (uint)baseVertex;
        r[3] = (uint)slot;
        r[4] = (uint)batch;
        r[5] = (uint)batchStart;
    }

    /// <summary>#287 (AC5's off-device half): does the compute-produced draw list agree with the CPU-staged
    /// one? A line-by-line port of <see cref="CullCs"/>'s <c>main</c>, run one invocation at a time -- which
    /// is what a sequential <c>atomicAdd</c> is -- over a synthetic pass, compared against the list the
    /// <c>BaseVertex</c> path would submit from the same staging.
    /// <para>Two standards, because #290 gave the pass two kinds of batch. For a COMPACTING batch it is set
    /// equality: the order inside it is the atomic's, not the staging walk's, and on real hardware it is not
    /// even deterministic -- so what is asserted is what the submission actually depends on, that every
    /// survivor lands inside its own batch's output region, carries its own record slot, and its command is
    /// its source record field for field. For an ORDERED batch it is SEQUENCE equality, which is the whole
    /// point of one: every draw sits at its exact staged index and a culled one holds its place as a
    /// zero-instance command. Class order is preserved either way, because each batch owns its own output
    /// region.</para>
    /// <para>The port is deliberately adjacent to the GLSL it mirrors. If one changes and the other does not,
    /// that is what this goes red on.</para></summary>
    private static bool Parity(ExtenderLog log, Matrix4x4 vp, float* planes)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"CullPass.Parity FAIL: {w}"); } }

        // Three batches, all inside the frustum the caller built: straight ahead at 10 units. Batch 2 is
        // the SORTED one -- #290's half, where the back-to-front order is produced rather than inherited.
        const int drawN = 10, groups = 3, compactBelow = 2;
        int* gStart = stackalloc int[groups] { 0, 3, 6 };
        int* gLen = stackalloc int[groups] { 3, 3, 4 };
        uint* src = stackalloc uint[drawN * SrcUints];
        float* sph = stackalloc float[drawN * 4];
        float* key = stackalloc float[drawN];
        var eye = Vector3.Zero;
        int n = 0;
        for (int g = 0; g < groups; g++)
            for (int d = 0; d < gLen[g]; d++, n++)
            {
                Pack(src + (long)n * SrcUints, 30 + n, 2 * n, n, n, g, gStart[g]);
                float* s = sph + (long)n * 4;
                s[0] = 0f; s[1] = 0f; s[2] = -10f; s[3] = 1f;
            }
        // ...and the three populations a cull has to get right: outside the frustum, exempt, sub-pixel.
        sph[1 * 4 + 0] = 500f;                                   // draw 1: far off to the side
        sph[4 * 4 + 3] = AlloyRenderer.NeverCull;                // draw 4: unknown bounds, never culled
        sph[5 * 4 + 3] = 0.001f;                                 // draw 5: a speck, for the LOD test
        // The sorted batch: near, far, equally far (the tie), and one the frustum drops.
        sph[6 * 4 + 2] = -10f;
        sph[7 * 4 + 2] = -30f;
        sph[8 * 4 + 2] = -30f;
        sph[9 * 4 + 0] = 500f;

        uint* cmd = stackalloc uint[drawN * CmdUints];
        int* outSlot = stackalloc int[drawN];
        uint* cnt = stackalloc uint[groups];

        // Everything visible: the compaction must reproduce the staged list exactly, batch for batch.
        Emulate(src, sph, drawN, groups, planes, vp, 0f, 0f, compactBelow, eye, key, cmd, outSlot, cnt);
        E(cnt[0] == 2 && cnt[1] == 3, "only the out-of-frustum draw is dropped when nothing else can be");
        E(Agrees(src, drawN, groups, gStart, cmd, outSlot, cnt, log),
          "the compacted list agrees with the CPU-staged one draw for draw");

        // #290 (AC1 + AC5), the sorted half. Not "the same set": the submitted order has to BE the
        // back-to-front one, produced from keys the dispatch measured, with the culled draw compacted out.
        E(cnt[2] == 3, "the sorted batch's count is its survivors, not its staged length");
        E(outSlot[6] == 7 && outSlot[7] == 8 && outSlot[8] == 6,
          "the sorted batch submits farthest first, and an exact tie keeps the lower staged index");
        E(Sorted(sph, outSlot, gStart[2], (int)cnt[2], eye, log),
          "...which is to say every neighbour in it is no nearer than the one before");
        E(Agrees(src, drawN, groups, gStart, cmd, outSlot, cnt, log),
          "...and each of its commands is its own staged draw's range");
        // A rank is order-independent by construction; this is what pins that it really is.
        Emulate(src, sph, drawN, groups, planes, vp, 0f, 0f, compactBelow, eye, key, cmd, outSlot, cnt,
                scramble: true);
        E(cnt[2] == 3 && outSlot[6] == 7 && outSlot[7] == 8 && outSlot[8] == 6,
          "the sort is the same when the atomic hands its draws out backwards");

        // LOD: a speck goes, an exempt record stays, and the ordinary draws are untouched. uLodScale is
        // proj.M22 * viewportH / 2 -- 1.0 * 1080 / 2 for the caller's 90-degree projection.
        Emulate(src, sph, drawN, groups, planes, vp, 1f, 540f, compactBelow, eye, key, cmd, outSlot, cnt);
        E(cnt[1] == 2 && outSlot[gStart[1]] == 3 && outSlot[gStart[1] + 1] == 4,
          "a sub-pixel draw is LOD-culled and the ones that are not are the ones that survive");
        E(Agrees(src, drawN, groups, gStart, cmd, outSlot, cnt, log),
          "...and what survives it is still the staged record, field for field");
        Emulate(src, sph, drawN, groups, planes, vp, 1f, 0f, compactBelow, eye, key, cmd, outSlot, cnt);
        E(cnt[1] == 3, "a pass with no perspective scale runs no LOD test rather than culling everything");
        Emulate(src, sph, drawN, groups, planes, vp, 1e6f, 540f, compactBelow, eye, key, cmd, outSlot, cnt);
        E(cnt[1] == 1 && outSlot[gStart[1]] == 4,
          "an absurd LOD threshold still cannot touch a NeverCull record");
        E(cnt[2] == 3, "...nor anything in a SORTED batch, which runs no LOD test at all");
        return ok;
    }

    /// <summary>The port. <c>i</c> is <c>gl_GlobalInvocationID.x</c> and the counter is the atomic; every
    /// other line is <see cref="CullCs"/>'s and <see cref="SortCs"/>'s, in order -- two loops because there
    /// are two dispatches, and the barrier between them is what the loop boundary stands for.
    /// <para><paramref name="scramble"/> runs the invocations backwards. A sequential port hands the atomic
    /// out in staged order, which is the one thing real hardware never promises; the sorted range must come
    /// out identical either way, and that is asserted rather than assumed.</para></summary>
    private static void Emulate(uint* src, float* sph, int drawN, int groups, float* planes, Matrix4x4 vp,
                                float lodPx, float lodScale, int compactBelow, Vector3 eye, float* key,
                                uint* cmd, int* outSlot, uint* cnt, bool scramble = false)
    {
        for (int g = 0; g < groups; g++) cnt[g] = 0;
        for (int q = 0; q < drawN; q++)
        {
            int i = scramble ? drawN - 1 - q : q;
            uint* b = src + (long)i * SrcUints;
            bool sorted = b[4] >= (uint)compactBelow;
            int slot = (int)b[3];
            float* s = sph + (long)slot * 4;
            var c = new Vector3(s[0], s[1], s[2]);
            float r = s[3];
            bool keep = true;
            if (r < 1e29f)
            {
                for (int p = 0; p < 6 && keep; p++)
                    keep = !(planes[p * 4] * c.X + planes[p * 4 + 1] * c.Y + planes[p * 4 + 2] * c.Z
                             + planes[p * 4 + 3] < -r);
                if (keep && !sorted && lodPx > 0f && lodScale > 0f)
                {
                    float w = Vector4.Transform(new Vector4(c, 1f), vp).W;
                    if (w > 0f && r * lodScale < lodPx * w) keep = false;
                }
            }
            if (sorted)
            {
                Vector3 d = c - eye;
                key[i] = keep ? Vector3.Dot(d, d) : -1f;
                if (keep) cnt[b[4]]++;
                continue;
            }
            if (!keep) continue;
            uint o = b[5] + cnt[b[4]]++;
            uint k = o * 5u;
            cmd[k] = b[0];
            cmd[k + 1u] = 1u;
            cmd[k + 2u] = b[1];
            cmd[k + 3u] = b[2];
            cmd[k + 4u] = 0u;
            outSlot[o] = slot;
        }
        // ...and the second dispatch: the rank sort.
        for (int q = 0; q < drawN; q++)
        {
            int i = scramble ? drawN - 1 - q : q;
            uint* b = src + (long)i * SrcUints;
            uint batch = b[4];
            if (batch < (uint)compactBelow) continue;
            float k = key[i];
            if (k < 0f) continue;
            uint bas = b[5];
            uint rank = 0;
            for (uint j = bas; j < (uint)drawN; j++)
            {
                if (src[(long)j * SrcUints + 4] != batch) break;
                if (j == (uint)i) continue;
                float kj = key[j];
                if (kj < 0f) continue;
                if (kj > k || (kj == k && j < (uint)i)) rank++;
            }
            uint o = bas + rank;
            uint cw = o * 5u;
            cmd[cw] = b[0];
            cmd[cw + 1u] = 1u;
            cmd[cw + 2u] = b[1];
            cmd[cw + 3u] = b[2];
            cmd[cw + 4u] = 0u;
            outSlot[o] = (int)b[3];
        }
    }

    /// <summary>#290 (AC5): the submitted run really is back-to-front. Read off the SPHERES rather than the
    /// keys, so a key the emulator computed wrongly cannot agree with itself.</summary>
    private static bool Sorted(float* sph, int* outSlot, int start, int count, Vector3 eye, ExtenderLog log)
    {
        float prev = float.MaxValue;
        for (int o = start; o < start + count; o++)
        {
            float* s = sph + (long)outSlot[o] * 4;
            Vector3 d = new Vector3(s[0], s[1], s[2]) - eye;
            float k = Vector3.Dot(d, d);
            if (k > prev)
            { log.Info($"CullPass.Parity FAIL: output {o} is farther than the one before it"); return false; }
            prev = k;
        }
        return true;
    }

    /// <summary>Every survivor of <paramref name="cmd"/> is a staged draw of its own batch, once, with its
    /// own record slot -- and it landed inside that batch's output region. The two ways a compaction can be
    /// wrong that the draw call cannot survive: a draw written into someone else's region, and a command
    /// paired with the wrong record slot.</summary>
    private static bool Agrees(uint* src, int drawN, int groups, int* gStart, uint* cmd,
                              int* outSlot, uint* cnt, ExtenderLog log)
    {
        bool ok = true;
        for (int g = 0; g < groups; g++)
            for (int o = gStart[g]; o < gStart[g] + (int)cnt[g]; o++)
            {
                int found = -1;
                for (int d = 0; d < drawN; d++)
                    if ((int)src[(long)d * SrcUints + 3] == outSlot[o] && (int)src[(long)d * SrcUints + 4] == g)
                        found = d;
                if (found < 0)
                { log.Info($"CullPass.Parity FAIL: output {o} names a slot batch {g} never staged"); ok = false; continue; }
                uint* b = src + (long)found * SrcUints;
                uint* k = cmd + (long)o * CmdUints;
                if (k[0] != b[0] || k[1] != 1u || k[2] != b[1] || k[3] != b[2] || k[4] != 0u)
                { log.Info($"CullPass.Parity FAIL: output {o}'s command is not its staged draw's range"); ok = false; }
            }
        return ok;
    }

    /// <summary>The cull itself. One thread per draw; a survivor appends into its batch's own region with an
    /// atomicAdd, so the compacted list keeps every batch's boundaries and only its interior order moves.
    /// <para>A radius of infinity is "do not cull me" -- the CPU writes it for anything whose vertices move
    /// under a matrix that does not (skinned, dangly, a displacing feature), and for a degenerate bbox.
    /// Unknown bounds mean visible, always: the failure mode of this pass is invisible geometry.</para></summary>
    private const string CullCs = """
        #version 460 core
        layout(local_size_x = 64) in;   // GroupSize
        layout(std430, binding = 12) readonly buffer SowxCullSrc     { uint uSrc[]; };
        layout(std430, binding = 13) readonly buffer SowxCullSphere  { vec4 uSph[]; };
        layout(std430, binding = 14) writeonly buffer SowxCullCmd    { uint uCmd[]; };
        layout(std430, binding = 15) writeonly buffer SowxCullOutIdx { int  uOut[]; };
        layout(std430, binding = 16) buffer SowxCullCount            { uint uCnt[]; };
        layout(std430, binding = 17) buffer SowxCullKey              { float uKey[]; };
        uniform int uDrawN;
        uniform vec4 uPlanes[6];
        uniform mat4 uVP;
        uniform int uHiZ;
        uniform sampler2D uPyramid;
        uniform vec2 uPyrSize;
        uniform float uMaxLod;
        uniform float uLodPx;
        uniform float uLodScale;
        uniform int uCompactBelow;
        uniform vec3 uEye;

        // One pyramid tap, addressed through the LEVEL-0 grid rather than the level's own.
        // This is the fix for #243's first live bug. `textureLod(uv, L)` maps uv across the width of level
        // L, which is floor(w0 / 2^L) -- and for a non-power-of-two screen that is NOT w0 / 2^L. At level 8
        // of a 1272-wide pyramid the level is 4 texels representing 1024 source columns, so uv 0.9 (source
        // column 1145) samples the texel covering columns 768..1023: a completely different part of the
        // screen, with a completely unrelated max depth. Large screen boxes take the high LODs, and a
        // distant tile's bounding cube IS a large screen box -- which is why the artefact was distant
        // tileset geometry vanishing while near geometry (whose box leaves the screen and exits above)
        // stayed. Converting to a level-0 texel first and shifting keeps the correspondence exact.
        float SowxPyr(vec2 uv, int lod)
        {
            ivec2 lsz = max(ivec2(uPyrSize) >> lod, ivec2(1));
            ivec2 t = clamp(ivec2(uv * uPyrSize) >> lod, ivec2(0), lsz - ivec2(1));
            return texelFetch(uPyramid, t, lod).r;
        }

        // #287: LOD selection. The sphere's screen radius is r * uLodScale / depth, and the clip w of its
        // centre IS that depth under a perspective projection -- so no eye position and no view matrix reach
        // this shader. A scale of zero means the pass is not perspective and the test cannot be trusted.
        bool SowxSubPixel(vec3 c, float r)
        {
            if (uLodPx <= 0.0 || uLodScale <= 0.0) return false;
            float w = (uVP * vec4(c, 1.0)).w;
            return w > 0.0 && r * uLodScale < uLodPx * w;
        }

        bool SowxOccluded(vec3 c, float r)
        {
            vec2 lo = vec2(1e30), hi = vec2(-1e30);
            float near = 1e30;
            for (int i = 0; i < 8; i++)
            {
                vec3 o = vec3((i & 1) != 0 ? r : -r, (i & 2) != 0 ? r : -r, (i & 4) != 0 ? r : -r);
                vec4 cp = uVP * vec4(c + o, 1.0);
                // Straddling the eye plane: the screen box is meaningless there, so do not cull.
                if (cp.w <= 0.0) return false;
                vec3 ndc = cp.xyz / cp.w;
                lo = min(lo, ndc.xy); hi = max(hi, ndc.xy); near = min(near, ndc.z);
            }
            // Partly off screen: CLAMP_TO_EDGE would answer for a region the box does not cover, and that
            // region's max can be FARTHER than the box's -- the one direction that culls something visible.
            if (lo.x < -1.0 || lo.y < -1.0 || hi.x > 1.0 || hi.y > 1.0) return false;
            vec2 uv0 = lo * 0.5 + 0.5, uv1 = hi * 0.5 + 0.5;
            vec2 px = (uv1 - uv0) * uPyrSize;
            // A level whose neighbourhood covers the box. Rounding UP over-covers, which only ever raises
            // the max it compares against, so it errs toward drawing.
            int lod = int(clamp(ceil(log2(max(max(px.x, px.y), 1.0))), 0.0, uMaxLod));
            float d = max(max(SowxPyr(uv0, lod), SowxPyr(vec2(uv1.x, uv0.y), lod)),
                          max(SowxPyr(vec2(uv0.x, uv1.y), lod), SowxPyr(uv1, lod)));
            return near * 0.5 + 0.5 > d;
        }

        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            if (i >= uint(uDrawN)) return;
            uint b = i * 6u;
            uint batch = uSrc[b + 4u];
            // #290: a batch at or above uCompactBelow is SORTED -- this dispatch only decides its verdict
            // and measures its key, and the second dispatch places it. Everything below compacts here, as
            // it has since #243, because opaque draws have no order to preserve.
            bool sorted = batch >= uint(uCompactBelow);
            int slot = int(uSrc[b + 3u]);
            vec4 s = uSph[slot];
            bool keep = true;
            if (s.w < 1e29)
            {
                for (int p = 0; p < 6 && keep; p++)
                    if (dot(uPlanes[p].xyz, s.xyz) + uPlanes[p].w < -s.w) keep = false;
                // #290: LOD is for the OPAQUE range alone. A blended or additive draw is routinely far
                // larger on screen than its bounding radius -- a torch flame or a spell mote is a bloomed
                // sprite, and thinning it by projected radius would delete lights the frustum and the
                // pyramid both agree are visible.
                if (keep && !sorted && SowxSubPixel(s.xyz, s.w)) keep = false;
                if (keep && uHiZ != 0 && SowxOccluded(s.xyz, s.w)) keep = false;
            }
            if (sorted)
            {
                // The key, and the verdict carried in its sign: squared distance from this pass's eye,
                // which is exactly what the CPU's EyeDist2 measures, so the two orders agree.
                vec3 d = s.xyz - uEye;
                uKey[i] = keep ? dot(d, d) : -1.0;
                // The count is the batch's SURVIVOR count -- the sort compacts as well as orders.
                if (keep) atomicAdd(uCnt[batch], 1u);
                return;
            }
            if (!keep) return;
            uint o = uSrc[b + 5u] + atomicAdd(uCnt[batch], 1u);
            uint c = o * 5u;
            uCmd[c] = uSrc[b];        // count
            uCmd[c + 1u] = 1u;        // instanceCount
            uCmd[c + 2u] = uSrc[b + 1u];   // firstIndex
            uCmd[c + 3u] = uSrc[b + 2u];   // baseVertex
            uCmd[c + 4u] = 0u;        // baseInstance
            uOut[o] = slot;
        }
        """;

    /// <summary>#290: the back-to-front sort, as a RANK rather than a shuffle. One thread per draw; a
    /// survivor of a sorted batch counts how many survivors of the same batch are FARTHER than it, and that
    /// count is its offset from the batch's output base. No scratch list, no bucket table, and no key
    /// quantisation -- the compare is on the float key itself, so two coplanar surfaces cannot tie into the
    /// wrong order the way a radix over quantised depth lets them.
    /// <para>The tie-break is the STAGED index, which keeps equal-distance draws in the texture-grouped
    /// enrollment order the CPU sort also leaves them in -- stable, and stable the same way, so a still
    /// camera does not shuffle its transparents frame to frame.</para>
    /// <para>A batch's draws are contiguous in the source list (the cut walks it in order), so the scan runs
    /// from the batch's base until the batch field changes. ponytail: that is O(n^2) compares inside a
    /// batch, spread across n threads. The blending ranges are the small remainder of a scene by
    /// construction -- single-digit batches in a typical area -- and every alternative (bitonic, radix)
    /// needs more dispatches and a scratch list to beat it. If a scene ever lands enough transparents to
    /// show on the <c>[alloy]</c> line, a bitonic sort over these same keys drops in without changing
    /// anything around it.</para></summary>
    private const string SortCs = """
        #version 460 core
        layout(local_size_x = 64) in;   // GroupSize
        layout(std430, binding = 12) readonly buffer SowxCullSrc     { uint uSrc[]; };
        layout(std430, binding = 14) writeonly buffer SowxCullCmd    { uint uCmd[]; };
        layout(std430, binding = 15) writeonly buffer SowxCullOutIdx { int  uOut[]; };
        layout(std430, binding = 17) readonly buffer SowxCullKey     { float uKey[]; };
        uniform int uDrawN;
        uniform int uCompactBelow;

        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            if (i >= uint(uDrawN)) return;
            uint b = i * 6u;
            uint batch = uSrc[b + 4u];
            if (batch < uint(uCompactBelow)) return;
            float k = uKey[i];
            if (k < 0.0) return;              // the cull dropped it
            uint base = uSrc[b + 5u];
            uint rank = 0u;
            for (uint j = base; j < uint(uDrawN); j++)
            {
                if (uSrc[j * 6u + 4u] != batch) break;   // batches are contiguous in the source list
                if (j == i) continue;
                float kj = uKey[j];
                if (kj < 0.0) continue;
                // Farther first. An exact tie falls to the lower staged index, which is the CPU sort's own
                // stable order -- so the two agree draw for draw and /draw cull 0 is a like-for-like A/B.
                if (kj > k || (kj == k && j < i)) rank++;
            }
            uint o = base + rank;
            uint c = o * 5u;
            uCmd[c] = uSrc[b];
            uCmd[c + 1u] = 1u;
            uCmd[c + 2u] = uSrc[b + 1u];
            uCmd[c + 3u] = uSrc[b + 2u];
            uCmd[c + 4u] = 0u;
            uOut[o] = int(uSrc[b + 3u]);
        }
        """;

    /// <summary>One level of the pyramid: the MAX of the source region the destination texel covers.
    /// <para>A 3x3 tap, not a 2x2, and that is a correctness fix rather than a refinement. GL sizes mip
    /// <c>i</c> as <c>floor(size / 2^i)</c>, so a source level with an ODD dimension has a last row (or
    /// column) that no 2x2 destination texel reads. The test downstream is
    /// <c>objectNear &gt; maxSceneDepth =&gt; occluded</c>, so a max that is missing texels fires the cull
    /// MORE readily -- dropping them is the dangerous direction, not the safe one. The third tap folds the
    /// remainder in; where there is no remainder it merely over-covers by one texel, which raises the max
    /// and therefore only ever draws more.</para></summary>
    private const string HiZFs = """
        #version 460 core
        uniform sampler2D uSrc;
        uniform int uLevel;
        in vec2 vUV;
        out vec4 oCol;
        void main()
        {
            ivec2 lim = textureSize(uSrc, uLevel) - ivec2(1);
            ivec2 s = ivec2(gl_FragCoord.xy) * 2;
            float m = 0.0;
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                    m = max(m, texelFetch(uSrc, min(s + ivec2(x, y), lim), uLevel).r);
            oCol = vec4(m);
        }
        """;
}
