using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

using static CX.Alloy.AlloyRenderer;

namespace CX.Alloy;

/// <summary>#232's second mode, CX-82's default: the OWNED highlight. Instead of replaying the engine's
/// <c>Scene::DoHighlightGob</c> glow (three additive quads per part, cut from its bounding box), redraw the
/// highlighted object flat: a 50%-alpha fill over its visible surface plus a thick, smooth silhouette
/// outline. <c>alloy.highlight</c> (Renderer → Selection highlight) picks it. Whatever the outline cannot cover --
/// an unclaimed source channel, a root with no Alloy draw records -- shows no highlight, and says so once.
/// <para>Runs right after <see cref="PostChain"/>'s composite: the target framebuffer holds the finished
/// DISPLAY-space frame and <see cref="PostChain.DepthTex"/> holds the frame's depth. The pass is
/// SCREEN-SPACE and, whatever the highlight count, exactly two draws deep: (1) ALL highlighted objects'
/// Alloy draw records rasterise one RGBA MASK (rgb = that root's highlight colour, a = coverage),
/// depth-tested against the frame's own depth under a small polygon offset -- the offset is what makes
/// this robust where a depth-EQUALITY fill speckles; (2) ONE fullscreen triangle blends the result over
/// the frame, fill from the mask (bilinear edge = antialiased) and outline from a ring-tap dilation of it
/// (a coverage ramp = thick AND smooth). Per-frame cost is therefore one geometry re-draw of the
/// highlighted set plus one fixed 24-tap fullscreen pass, not a pass per object.</para>
/// <para>An ADR 0014 module, not a partial (CX-699): it reaches the renderer's tables and state through
/// <c>using static</c>, a compile-time alias that emits the same static load the same-class read did.</para></summary>
internal static unsafe class Highlight
{

    /// <summary>Ceilings: highlighted ROOT gobs per frame, and (roots + attached children) in total.
    /// Roots beyond the cap take the glow instead of silently vanishing.
    /// ponytail: the per-draw match below is a linear scan over these targets; if a census ever shows
    /// hundreds of simultaneous highlights mattering, swap it for a fixed-size open-addressed table.</summary>
    private const int MaxHlRoots = 128, MaxHlTargets = 256;

    /// <summary>#336: the highlight CHANNELS, as <c>CNWCAnimBase</c> lays them out. A hilite is written into
    /// one of five slots -- active flag at +0x9c[ch], colour at +0x60[ch] (12 bytes), "also add a glow gob"
    /// at +0xb0[ch] -- and <c>RefreshHilite</c> takes the LOWEST active slot as the winner. The gob the
    /// winner is published against sits at +0xc8.
    /// <para>The channel is the SOURCE, which the Scene highlight list does not carry: Tab and mouseover
    /// arrive on 2 (<c>HiliteAreaObjects</c> @0x7119f0), while every VFX that highlights arrives on 3 --
    /// <c>CNWCVisualEffectOnObject::ApplySelfIllumination</c>, <c>::ApplyAlpha</c> and
    /// <c>::UpdateSelfIlluminationPulse</c> all call <c>Hilite(colour, 3, 1)</c>. Lowest-wins is why a
    /// Tab over a glowing creature shows the Tab colour.</para></summary>
    private const int HiliteChannels = 5;

    /// <summary>Channels the owned outline claims, as a bitmask. 3 is the VFX channel and is NOT claimed:
    /// a spell glow drawn as a crisp outline reads as a Tab highlight, which is exactly the bug. 4 is the
    /// tab-target lock (CX-636), a Tab highlight in all but source, so it is. <c>/alloy hlchan</c>.</summary>
    internal static int HlChannels => Renderer.HlChan.V;
    internal const int DefHlChannels = 0b10111;

    /// <summary>Gobs whose winning channel the outline claims, refreshed from the engine's own verdict at
    /// every <c>RefreshHilite</c>. Small and churn-free: an entry per highlighted object, not per part.</summary>
    private static readonly IntPtr[] _ownedHl = new IntPtr[MaxHlRoots];
    private static int _ownedHlN;

    /// <summary><c>CNWCAnimBase::RefreshHilite</c>, after the original. The engine has just recomputed the
    /// winning channel and published (or withdrawn) the gob, so this reads the result instead of predicting
    /// it -- no duplicated priority rule to drift.</summary>
    [UnmanagedCallersOnly]
    internal static void OnRefreshHilite(IntPtr animBase)
    {
        _origRefreshHilite(animBase);
        if (animBase == IntPtr.Zero) return;
        var anim = new CNWCAnimBase(animBase);
        IntPtr gob = anim.Gob.Ptr;
        // #456: the trace goes AHEAD of the early return, not after it. Sited below, "no [hldiag] lines at
        // all" reads as "the hook never fired" when it may equally be "fired, published no gob" -- and on
        // Windows those two have completely different causes. The engine's own body takes the same branch
        // on a null here (win rva 0x966170: `mov rcx,[rcx+0xc8]` / `test rcx,rcx` / `je`), so a null is a
        // legitimate state and the only way to tell them apart is to count the calls.
        if (HlDiag) _log?.Info($"[hldiag] enter animBase={animBase:x} gob={gob:x}");
        if (gob == IntPtr.Zero) return;
        int win = -1;
        Span<int> active = anim.HiliteActive, glow = anim.HiliteGlow;
        for (int c = 0; c < HiliteChannels && win < 0; c++)
            if (active[c] != 0) win = c;
        // Claimed only when the winner is ours AND the engine would publish a gob for it at all.
        bool claim = win >= 0 && (HlChannels & (1 << win)) != 0 && glow[win] != 0;
        int at = -1;
        for (int i = 0; i < _ownedHlN; i++) if (_ownedHl[i] == gob) { at = i; break; }
        if (claim)
        {
            if (at < 0 && _ownedHlN < _ownedHl.Length) _ownedHl[_ownedHlN++] = gob;
        }
        else if (at >= 0) _ownedHl[at] = _ownedHl[--_ownedHlN];
        if (HlDiag)
            _log?.Info($"[hldiag] gob={gob:x} channel={win} glow={(win >= 0 ? glow[win] : 0)}"
                       + $" -> {(claim ? "OWNED outline" : "engine glow")}");
    }

    internal static RefreshHiliteFn _origRefreshHilite;

    /// <summary>Did the source hook claim this gob? A gob the hook never saw is not ours: the engine can put
    /// a gob in the highlight list by a path that never reaches <c>RefreshHilite</c>, and the safe verdict
    /// for an unrecognised source is the glow, not an outline.</summary>
    private static bool OwnedHighlight(IntPtr gob)
    {
        for (int i = 0; i < _ownedHlN; i++) if (_ownedHl[i] == gob) return true;
        return false;
    }

    /// <inheritdoc cref="OwnedHighlight"/>
    /// <remarks>DB mode: the pointers the source hook collected are the facade SHELLS the game holds
    /// (<c>CNWCAnimBase+0xc8</c> is the shell there, not an engine <c>Gob</c>), so the claim is tested by
    /// resolving each back to its record rather than by comparing pointers to a handle.</remarks>
    private static bool OwnedHighlightDb(DbId obj)
    {
        for (int i = 0; i < _ownedHlN; i++) if (Facade.ObjectId(_ownedHl[i]) == obj) return true;
        return false;
    }

    /// <summary>The highlighted root's attached children, in the shape <c>Gob::GetAttachedGobs</c> hands back
    /// in engine mode. The database has no child list -- attachment is a field ON THE CHILD -- so this is one
    /// walk of the object table per highlighted root, which is cold (it runs only while something is
    /// highlighted) and allocates nothing.</summary>
    private static int DbAttached(DbId root, byte rootIndex, IntPtr* targets, byte* targetRoot, int nT)
    {
        for (int o = 1; o < SceneDb.Objects.High && nT < MaxHlTargets; o++)
        {
            if (!SceneDb.Objects.LiveAt(o) || SceneDb.ObjAttachTo[o] != root) continue;
            if ((SceneDb.ObjSeamFlags[o] & (uint)SeamFlag.PropagateHighlight) == 0) continue;
            targetRoot[nT] = rootIndex;
            targets[nT++] = (IntPtr)SceneDb.Objects.IdAt(o).Value;
        }
        return nT;
    }

    /// <summary><c>/alloy hldiag 1</c>: name the winning channel and the verdict every time a highlight
    /// changes. The source is the question now, so the colour is not worth printing.</summary>
    internal static bool HlDiag => Renderer.HlDiag.V;

    /// <summary>Outline thickness in pixels: the dilation radius the composite's ring taps sample at.</summary>
    private const float OutlinePx = 5f;

    /// <summary>Fill opacity over the highlighted object's visible surface.</summary>
    private const float FillAlpha = 0.10f;

    /// <summary>The mask rasterisation's depth bias: pulled toward the viewer so the re-draw passes LEQUAL
    /// against its own colour-pass depth everywhere, instead of speckling on exact-equality precision.</summary>
    private const float MaskOffsetFactor = -1f, MaskOffsetUnits = -2f;

    private static uint _progHl, _progHlComp, _hlMaskTex, _hlMaskFbo, _hlVao;
    private static int _hlVp, _hlBase, _hlColor, _hlCompMask, _hlCompFill, _hlCompRadius, _hlCompUnjitter;
    private static int _hlW, _hlH;
    private static uint _hlDepthAttached;
    private static bool _hlFailed, _hlLogged, _saidDbUnhit;

    internal static void HighlightContextLost()
    {
        _progHl = 0; _progHlComp = 0; _hlMaskTex = 0; _hlMaskFbo = 0; _hlVao = 0;
        _hlW = 0; _hlH = 0; _hlDepthAttached = 0;
    }

    /// <summary>The root's highlight colour at full coverage, over <see cref="DepthVs"/> -- the vertex
    /// stage is reused verbatim so the mask's biased LEQUAL test sits against exactly the positions the
    /// colour pass wrote depth from.</summary>
    private static string HighlightMaskFs => ShaderSource.Get("cx_hilitemask_f.glsl");

    /// <summary>The composite: fill where the mask has coverage (its bilinear edge antialiases the rim, and
    /// rgb rides the same fetch so per-object colours cost nothing), outline where a ring-tap dilation of
    /// the coverage lands OUTSIDE it, coloured by the taps' own coverage-weighted rgb. Coverage through
    /// smoothstep is what makes the outline read as one thick soft stroke rather than an aliased wireframe.</summary>
    private static string HighlightCompFs => ShaderSource.Get("cx_hilitecomp_f.glsl");

    private static bool EnsureHighlightPrograms()
    {
        if (_progHl != 0 && _progHlComp != 0) return true;
        if (_hlFailed) return false;
        if (Gl.GenFramebuffers == null || Gl.FramebufferTexture2D == null || Gl.PolygonOffset == null)
        { _hlFailed = true; return false; }
        uint mask = GlProgram.Graphics(DepthVs, HighlightMaskFs, _log, "AlloyRenderer highlight mask program");
        uint comp = GlProgram.Graphics(PostChain.FullscreenVs, HighlightCompFs, _log,
                                       "AlloyRenderer highlight composite program");
        if (mask == 0 || comp == 0) { _hlFailed = true; return false; }
        _progHl = mask;
        _hlVp = GlProgram.Loc(mask, "uVP");
        _hlBase = GlProgram.Loc(mask, "uDrawBase");
        _hlColor = GlProgram.Loc(mask, "uColor");
        _progHlComp = comp;
        _hlCompMask = GlProgram.Loc(comp, "uMask"); _hlCompUnjitter = GlProgram.Loc(comp, "uUnjitter");
        _hlCompFill = GlProgram.Loc(comp, "uFill");
        _hlCompRadius = GlProgram.Loc(comp, "uRadius");
        if (_hlVao == 0) { uint v; Gl.CreateVertexArrays(1, &v); _hlVao = v; }
        return true;
    }

    /// <summary>The RGBA8 mask target, viewport-sized, recreated on resize. The frame's depth texture is
    /// attached fresh whenever <see cref="PostChain"/> recreates it on its own schedule.</summary>
    private static bool EnsureHighlightMask(int w, int h)
    {
        if (_hlMaskTex == 0 || w != _hlW || h != _hlH)
        {
            if (_hlMaskFbo != 0) { uint f = _hlMaskFbo; Gl.DeleteFramebuffers(1, &f); _hlMaskFbo = 0; }
            uint tex = _hlMaskTex;
            RenderTarget.AllocColor(ref tex, false, w, h, Gl.LINEAR);
            uint fbo; Gl.GenFramebuffers(1, &fbo);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, tex, 0);
            _hlMaskTex = tex; _hlMaskFbo = fbo; _hlW = w; _hlH = h; _hlDepthAttached = 0;
        }
        else Gl.BindFramebuffer(Gl.FRAMEBUFFER, _hlMaskFbo);
        if (_hlDepthAttached != PostChain.DepthTex)
        {
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D,
                                    PostChain.DepthTex, 0);
            if (Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE)
            {
                _hlFailed = true;
                _log?.Info("AlloyRenderer: highlight mask framebuffer incomplete -- outline highlight disarmed");
                return false;
            }
            _hlDepthAttached = PostChain.DepthTex;
        }
        return true;
    }

    /// <summary>The pass. Called from the frame walk immediately after the post-process composite; the
    /// engine target is bound with the finished frame in it.
    /// <para>CX-82: it covers EVERY highlighted root, because nothing engine-side draws afterwards any more.
    /// A root whose source channel the outline claims and whose parts Alloy drew gets the mask outline; every
    /// other root -- an unclaimed VFX channel, or no Alloy records at all -- shows nothing.</para>
    /// <para>#391: the mask is drawn at the RENDER size (<paramref name="viewport"/>) because it depth-tests
    /// against the render-res frame depth; the composite over the finished frame runs at
    /// <paramref name="display"/>, sampling the mask bilinearly. The outline radius is in mask texels, so it
    /// widens by 1/scale on screen -- noted, not corrected.</para></summary>
    internal static void OutlineHighlightPass(int groups, Matrix4x4* vp, Matrix4x4* view, Matrix4x4* proj,
                                             float jx, float jy, int* viewport, int* display)
    {
        // The highlighted set is the scene database's: the seam records every AddHighlightGob in SceneDb.
        DbId dbScene = AlloyRenderer.DbPassScene;
        int nGobs = SceneFeed.HighlightCount(dbScene);
        if (nGobs == 0) return;
        if (PostChain.DepthTex == 0) return;
        if (nGobs > MaxHlRoots) nGobs = MaxHlRoots;

        // The target set for EVERY highlighted root at once: the gob plus its visible attached children
        // (weapons, shields -- exactly the set the engine glow covers), each carrying its root's index so
        // the mask draw picks the right colour and the hit lands on the right root. Read per frame off the
        // live Scene; nothing cached across frames (borrowed pointers).
        IntPtr* targets = stackalloc IntPtr[MaxHlTargets];
        byte* targetRoot = stackalloc byte[MaxHlTargets];
        byte* rootHit = stackalloc byte[MaxHlRoots];
        // A root KEY per entry (the DB record's handle value) and its colour.
        IntPtr* arr = stackalloc IntPtr[MaxHlRoots];
        float* cols = stackalloc float[MaxHlRoots * 3];
        for (int i = 0; i < nGobs; i++)
        {
            SceneFeed.HighlightAt(dbScene, i, out DbId o, out Vector3 c);
            arr[i] = (IntPtr)o.Value;
            cols[i * 3] = c.X; cols[i * 3 + 1] = c.Y; cols[i * 3 + 2] = c.Z;
        }
        int nT = 0;
        bool outline = Renderer.OutlineHighlight && EnsureHighlightPrograms();
        for (int i = 0; i < nGobs; i++)
        {
            rootHit[i] = 0;
            if (!outline || nT >= MaxHlTargets) continue;
            IntPtr gob = arr[i];
            // #336: the list is source-agnostic -- a spell's glow sits in it looking exactly like a Tab
            // highlight, and drawn as a crisp outline it reads as one. Only what the source hook claimed is
            // ours.
            if (!OwnedHighlightDb(new DbId((uint)gob))) continue;
            targetRoot[nT] = (byte)i;
            targets[nT++] = gob;
            nT = DbAttached(new DbId((uint)gob), (byte)i, targets, targetRoot, nT);
        }

        int prevFbo;
        Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &prevFbo);

        if (nT > 0 && EnsureHighlightMask(viewport[2], viewport[3]))
        {
            // Step 1: ONE visibility mask for the whole set. Biased LEQUAL against the frame's own depth --
            // visible surface takes its root's colour at full coverage, everything else stays the cleared zero.
            Gl.Viewport(0, 0, viewport[2], viewport[3]);
            Gl.ClearColor(0f, 0f, 0f, 0f);
            Gl.Clear(Gl.COLOR_BUFFER_BIT);
            Gl.UseProgram(_progHl);
            Gl.UniformMatrix4fv(_hlVp, 1, 0, (float*)vp);
            Gl.Enable(Gl.DEPTH_TEST);
            Gl.DepthFunc(Gl.LEQUAL);
            Gl.DepthMask(0);
            Gl.Disable(Gl.BLEND);
            Gl.PolygonOffset(MaskOffsetFactor, MaskOffsetUnits);
            DrawTargets(groups, targets, targetRoot, rootHit, nT, cols);
            Gl.Disable(Gl.POLYGON_OFFSET_FILL);
            Gl.DepthFunc(Gl.LEQUAL);   // #364: the cutout run leaves EQUAL bound; every later pass assumes LEQUAL
        }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)prevFbo);
        Gl.Viewport(display[0], display[1], display[2], display[3]);

        // A root the outline does not claim or cannot draw shows nothing at all -- said once, not dropped quietly.
        int hit = 0, unhit = 0;
        for (int i = 0; i < nGobs; i++)
            if (rootHit[i] != 0) hit++; else unhit++;
        if (unhit > 0 && !_saidDbUnhit)
        {
            _saidDbUnhit = true;
            _log?.Always($"AlloyRenderer: {unhit} highlighted object(s) got NO highlight this frame -- a root "
                       + "the outline does not claim or cannot draw shows nothing at all");
        }
        if (hit == 0) return;

        // Step 2: ONE fullscreen triangle over the finished frame -- fill + dilated outline, blended.
        Gl.UseProgram(_progHlComp);
        Gl.BindVertexArray(_hlVao);
        Gl.Disable(Gl.DEPTH_TEST);
        Gl.Enable(Gl.BLEND);
        Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, _hlMaskTex);
        Gl.Uniform1i(_hlCompMask, 0);
        Gl.Uniform1f(_hlCompFill, FillAlpha);
        Gl.Uniform1f(_hlCompRadius, OutlinePx);
        // NDC offset j is uv offset j/2; the mask holds clean + j, so clean reads at uv + j/2
        Gl.Uniform2f(_hlCompUnjitter, jx * 0.5f, jy * 0.5f);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindVertexArray(0);

        // Hand the state back the way Composite leaves it: depth on, mask on, blend off.
        Gl.Disable(Gl.BLEND);
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.DepthMask(1);

        if (_hlLogged) return;
        _hlLogged = true;
        _log?.Info($"AlloyRenderer: outline highlight LIVE -- {hit}/{nGobs} root gob(s) outlined "
                 + "(screen-space mask fill + dilated outline, one pass for the set); every other "
                 + "highlighted root takes the glow");
    }

    /// <summary>One walk over the staged batches, issuing a single-draw multidraw for every record whose
    /// owning gob is in the target set, with the root's colour bound and the root marked hit. Same
    /// per-batch VAO/cull discipline as the depth prepass.
    /// <para>#364: cutout batches are IN, under a different depth test, because a highlighted creature's
    /// hair, cloak and helmet trim are cutout and leaving them out is what made an object take its outline
    /// in pieces. The mask has no alpha to discard on -- but it does not need one. Cutout is the one class
    /// the prepass skips (a discard's prepass depth would be its solid silhouette), so it writes its own
    /// depth in the COLOUR pass, with the discard already applied. Drawn at <c>GL_EQUAL</c> with no bias,
    /// only the fragments that survived that discard match, and the mask gets the exact silhouette for
    /// free. It rests on <see cref="DepthVs"/> computing <c>gl_Position</c> character-for-character as the
    /// colour stages do under <c>invariant gl_Position</c> -- which <c>ShaderContract</c> asserts, and which
    /// the whole prepass already rests on.</para>
    /// <para>Still out, and both are silhouette holes rather than timing: DISPLACING batches, whose colour
    /// stage moves vertices this one does not, so nothing would match under either test; and everything past
    /// <see cref="_gBlendEnd"/> (water and additive), which writes no depth to match against.</para></summary>
    private static void DrawTargets(int groups, IntPtr* targets, byte* targetRoot, byte* rootHit, int nT,
                                    float* cols)
    {
        uint boundVao = 0;
        int boundCull = -1, boundColor = -1, boundCut = -1;
        // The blended class writes depth too now (Issue.TransparentPass depthWrite), so a translucent creature matches.
        int end = groups < _gBlendEnd ? groups : _gBlendEnd;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        for (int g = 0; g < end; g++)
        {
            if (ShaderFeatures.OutOfPrepass(_gFeat[g])) continue;
            int s = _gStart[g], len = _gLen[g];
            for (int j = s; j < s + len; j++)
            {
                // The slot's OWNER, in whichever shape this mode's target keys are: the engine's gob pointer,
                // or the DB record handle of the object the part belongs to.
                int slot = _dSlot[j];
                IntPtr gob = DbSlot(slot) ? (IntPtr)SceneDb.PartObj[_oDbPart[slot]].Value : _oGob[slot];
                if (gob == IntPtr.Zero) continue;
                // An alpha-0 or untextured part (a door's `sam` doorway sheen) is no silhouette to outline.
                if (DbSlot(slot) && (SceneDb.PartAlpha[_oDbPart[slot]] <= 0f || AlloyRenderer.DbUntextured(slot))) continue;
                int hit = -1;
                for (int k = 0; k < nT; k++)
                    if (targets[k] == gob) { hit = k; break; }
                if (hit < 0) continue;
                int root = targetRoot[hit];
                rootHit[root] = 1;
                if (root != boundColor)
                {
                    boundColor = root;
                    Gl.Uniform3f(_hlColor, cols[root * 3], cols[root * 3 + 1], cols[root * 3 + 2]);
                }
                // #364: the two depth regimes. Opaque took its depth from the prepass, so the mask is
                // biased toward the viewer and tested LEQUAL to clear exact-equality speckle; cutout wrote
                // its own, discard-applied, in the colour pass, so the mask tests EQUAL against it with no
                // bias at all -- the bias is what would let the alpha holes through.
                int wantCut = _gCls[g] == (byte)AlloyClass.Cutout ? 1 : 0;
                if (wantCut != boundCut)
                {
                    boundCut = wantCut;
                    if (wantCut != 0)
                    { Gl.Disable(Gl.POLYGON_OFFSET_FILL); Gl.DepthFunc(Gl.EQUAL); }
                    else
                    { Gl.Enable(Gl.POLYGON_OFFSET_FILL); Gl.DepthFunc(Gl.LEQUAL); }
                }
                BindBatchState(g, ref boundCull, ref boundVao);
                Gl.Uniform1i(_hlBase, j);
                Gl.MultiDrawElementsBaseVertex(_gMode[g], counts + j, GlUnsignedShort,
                                               (void**)(offs + j), 1, bases + j);
            }
        }
    }
}
