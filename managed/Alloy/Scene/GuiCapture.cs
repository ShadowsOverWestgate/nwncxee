using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// CX-95/CX-96: the engine side of Alloy's GUI path. <c>Scene::RenderOnlyGobs</c> @0x4682e0 -- the draw of every
/// scene without a tile BSP: each panel, the menus, the minimap and compass, the cursor overlay, character-creation
/// previews -- is replaced by the same loop over the same gobs, run with the two draw funnels in CAPTURE mode. Every
/// engine draw below it (<c>Gob::Render</c>, <c>StringGob::Render</c> -&gt; <c>CAurFont::TextOut</c>, skins, emitters,
/// the icons inline in text) lands here as a record of the geometry and of the <c>GLRender</c> state caches it would
/// have run under, and nothing reaches GL; <see cref="GuiPass"/> issues the records from the 4.6 context.
/// <para>The engine still walks, animates and lays out its own GUI -- tier 2 moves the pixels, not the scene
/// (<c>docs/research/total-render-ownership.md</c> §3). GL-free: a VDO's VAO slot is harvested through
/// <see cref="AlloyRenderer.VdoSlot"/>, which lives with the rest of the VAO code.</para>
/// </summary>
internal static unsafe class GuiCapture
{
    /// <summary>One GUI scene as drawn: its viewport (<c>GLRender::m_viewport</c> as <c>Camera::RenderScene</c> left it),
    /// its run of draws, and the depth treatment <c>Camera::RenderScene</c> @0x4a7920 gives the view: every GUI view
    /// shares ONE depth buffer, cleared (<c>Clear(2)</c>, depth+stencil, unscissored) only at the first GUI view after a
    /// non-GUI one, and split by depth RANGE -- the five overlay scenes in [0, 0.02], every other panel in [0.021, 0.1].</summary>
    internal struct Layer { public int X, Y, W, H, SX, SY, SW, SH, First, Count; public float Near, Far; public bool Clear; }

    /// <summary>One captured draw. <see cref="Slot"/> is a VAO-cache slot (<see cref="Stream"/> false) or the
    /// stream format the flush builds a VAO for; <see cref="Offset"/> is an element BYTE offset when
    /// <see cref="Indexed"/>, else the first vertex.</summary>
    internal struct Draw
    {
        public bool Stream, Indexed, Blend, DepthTest, DepthWrite, Cull;
        public int Slot, Count, BaseVertex, Stride, Tex;
        public uint Prim, Format, Src, Dst;
        public long Offset;
    }

    /// <summary>Floats per draw record, which is <see cref="GuiPass"/>'s SSBO stride (16 vec4): mvp, mv, the texture
    /// 2x2, globalColor, ambient, diffuse, emissive, (flags, bone base, 0, 0), padding.</summary>
    internal const int RecFloats = 64;
    /// <summary>Flag bits in a record's misc word.</summary>
    internal const int FlagTex = 1, FlagVColor = 2, FlagLit = 4, FlagAlphaTest = 8, FlagSkin = 16;
    /// <summary>ponytail: fixed staging. A busy screen is a few hundred draws; past any ceiling the rest is NOT
    /// DRAWN and said once, loudly. Grow on the log line, never silently.</summary>
    internal const int MaxDraws = 16384, MaxLayers = 1024, MaxSkins = 64;
    internal const int VtxBytes = 8 << 20, IdxMax = 1 << 20;
    /// <summary>One <c>GLRender::m_bones</c> palette: 64 mat4.</summary>
    internal const int BoneFloats = AlloyRenderer.BoneFloats;

    /// <summary>The <c>AuroraEnableEnums</c> bits the capture reads (<c>GLRender::Enable</c> @0x4cae40's switch).</summary>
    private const int CapBlend = 0, CapAlphaTest = 1, CapFog = 2, CapCull = 3, CapLighting = 5, CapDepthTest = 0xb;   // not-an-offset: engine cap enum
    /// <summary><c>AuroraEnableEnums</c> 4 = GL_TEXTURE_2D in <c>m_boundTextures</c>' target field (0xe is a cube).</summary>
    private const int TargetTex2D = 4;
    private const uint NoItem = 0xffffffff;   // not-an-offset: the funnel's "use the item's own range" sentinel
    private const int ModeProj = 0, ModeView = 1, ModeModel = 2, ModeTexture = 3;

    internal static readonly Layer[] Layers = new Layer[MaxLayers];
    internal static readonly Draw[] Draws = new Draw[MaxDraws];
    internal static readonly float[] Records = new float[MaxDraws * RecFloats];
    internal static readonly float[] Bones = new float[MaxSkins * BoneFloats];
    internal static readonly byte[] Vtx = new byte[VtxBytes];
    internal static readonly ushort[] Idx = new ushort[IdxMax];
    internal static int LayerN, DrawN, SkinN, VtxN, IdxN;

    /// <summary>A GUI scene's draws are being captured: the funnels record instead of drawing.</summary>
    internal static bool Open;
    /// <summary>A VDO's VAO could not be harvested this frame. In a module the world's enrollment re-arms the VAO drip;
    /// outside one <see cref="GuiPass"/> does, off this flag.</summary>
    internal static bool SlotMissed;

    private static ExtenderLog? _log;
    private static SceneRenderOnlyGobsFn _orig;
    private static RiEnableFn _enable;
    private static RiDisableFn _disable;
    private static RiSetColorvFn _setColor;
    private static GlDrawResetFn _drawReset;
    private static uint* _caps, _texStages, _primTable;
    private static int* _src, _dst, _viewport, _bound;
    private static float* _color, _bones;
    private static byte* _depthMask, _material, _stacks;
    private static IntPtr* _custom;
    private static readonly Dictionary<IntPtr, int> _slots = new(512);

    /// <summary>Refusals since boot, by reason: each is a hole in the GUI, so the first of each is an Always line and
    /// the totals are on the status line.</summary>
    private static long _noSlot, _noHost, _badFormat, _overflow, _toEngine, _arrays, _layers, _draws;
    private static int _said;

    internal static bool Installed => _orig != null;

    /// <summary>Resolve the state caches and install the <c>RenderOnlyGobs</c> hook. False = something did not resolve
    /// (logged here); <see cref="GuiPass"/> then never arms and the engine keeps drawing its GUI.</summary>
    internal static bool Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _caps = (uint*)Native.Sym(SymbolMap.GlEnabledStates);
        _texStages = (uint*)Native.Sym(SymbolMap.GlEnabledTextureStages);
        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        _src = (int*)Native.Sym(SymbolMap.GlSrcBlendMode);
        _dst = (int*)Native.Sym(SymbolMap.GlDstBlendMode);
        _viewport = (int*)Native.Sym(SymbolMap.GlViewport);
        _bound = (int*)Native.Sym(SymbolMap.GlBoundTextures);
        _color = (float*)Native.Sym(SymbolMap.GlGlobalColor);
        _bones = (float*)Native.Sym(SymbolMap.GlRenderBones);
        _depthMask = (byte*)Native.Sym(SymbolMap.GlDepthMask);
        _material = (byte*)Native.Sym(SymbolMap.GlMaterial);
        _stacks = (byte*)Native.Sym(SymbolMap.MAurMtxStack);
        _custom = (IntPtr*)Native.Sym(SymbolMap.GlCurrentCustomProgram);
        _enable = EngineFn.RiEnable; _disable = EngineFn.RiDisable; _setColor = EngineFn.RiSetColorv;
        _drawReset = EngineFn.GlDrawReset;
        string? missing = _caps == null ? SymbolMap.GlEnabledStates : _texStages == null ? SymbolMap.GlEnabledTextureStages
            : _primTable == null ? SymbolMap.PrimTypeTranslationTable : _src == null ? SymbolMap.GlSrcBlendMode
            : _dst == null ? SymbolMap.GlDstBlendMode : _viewport == null ? SymbolMap.GlViewport
            : _bound == null ? SymbolMap.GlBoundTextures : _color == null ? SymbolMap.GlGlobalColor
            : _bones == null ? SymbolMap.GlRenderBones : _depthMask == null ? SymbolMap.GlDepthMask
            : _material == null ? SymbolMap.GlMaterial : _stacks == null ? SymbolMap.MAurMtxStack
            : _custom == null ? SymbolMap.GlCurrentCustomProgram : _enable == null ? SymbolMap.RiEnable
            : _disable == null ? SymbolMap.RiDisable : _setColor == null ? SymbolMap.RiSetColorv
            : _drawReset == null ? SymbolMap.GlDrawReset : null;
        if (missing != null)
        {
            log.Always($"GuiCapture: {missing} unresolved -- the GUI stays the ENGINE's draw (CX-95 not armed)");
            return false;
        }
        _orig = hooks.InstallSceneRenderOnlyGobs(&OnRenderOnlyGobs);
        if (_orig == null) log.Always("GuiCapture: Scene::RenderOnlyGobs did not hook -- the GUI stays the ENGINE's draw");
        return _orig != null;
    }

    /// <summary>The replacement: <c>RenderOnlyGobs</c>' own state preamble and gob loop, with the funnels capturing.
    /// Its Tracy scope and <c>LightManager::GetNearestLights</c> per lit gob are dropped: the first is instrumentation,
    /// the second feeds engine light uniforms no captured draw reads (<see cref="GuiPass"/> lights on its own terms).</summary>
    [UnmanagedCallersOnly]
    private static void OnRenderOnlyGobs(IntPtr scene)
    {
        if (Open || !GuiPass.Capturing || scene == IntPtr.Zero) { _orig(scene); return; }
        if (LayerN >= MaxLayers) { Overflow("layers"); return; }
        ref Layer l = ref Layers[LayerN++];
        l.X = l.SX = _viewport[0]; l.Y = l.SY = _viewport[1]; l.W = l.SW = _viewport[2]; l.H = l.SH = _viewport[3];
        l.First = DrawN; l.Count = 0;
        var sc = new Scene(scene);
        byte* name = (byte*)Unsafe.AsPointer(ref sc.Name);
        DepthOf(name, ref _latch, out l.Near, out l.Far, out l.Clear);
        _disable(CapAlphaTest);
        _disable(CapFog);
        float* white = stackalloc float[4] { 1f, 1f, 1f, 1f };
        _setColor(white);
        if (IsLitScene(name)) _enable(CapLighting); else _disable(CapLighting);
        IntPtr* gobs = (IntPtr*)sc.Gobs;
        int n = sc.GobCount;
        Open = true;
        for (int i = 0; i < n && gobs != null; i++)
            if (gobs[i] != IntPtr.Zero) new Gob(gobs[i]).Render(0);
        Open = false;
        // The LAST layer, not `l`: a custom-shader draw flushes mid-scene and reopens the scene as layer 0.
        ref Layer cur = ref Layers[LayerN - 1];
        cur.Count = DrawN - cur.First;
        _layers++;
    }

    /// <summary>The engine's clear latch (<c>DAT_019aa3a4</c>): set by the first GUI view's clear, dropped by a non-GUI
    /// view. Dropped at every present too -- <c>Update</c>'s own <c>Clear(3)</c> opens each frame with a clear depth.</summary>
    private static bool _latch;
    internal static void NewFrame() => _latch = false;

    /// <summary><c>Camera::RenderScene</c>'s depth decision for one view, by the scene's name: case-sensitive PREFIX
    /// compares (<c>repz cmpsb</c> over the literal's length, 8 for <c>"sceneGUI"</c>). ponytail: the engine also treats
    /// an ortho camera with a non-GUI name as GUI; the capture has the scene, not the camera, and no such scene is known.</summary>
    internal static void DepthOf(byte* name, ref bool latch, out float near, out float far, out bool clear)
    {
        if (!Prefix(name, "sceneGUI"u8)) { near = 0f; far = 1f; clear = false; latch = false; return; }
        clear = !latch;
        latch = true;
        bool front = Prefix(name, "sceneGUI_MouseOverlay"u8) || Prefix(name, "sceneGUI_Overlay"u8)
                  || Prefix(name, "sceneGUI_pnl_vidopts"u8) || Prefix(name, "sceneGUI_PNL_CONNECTSTAT"u8)
                  || Prefix(name, "sceneGUI_PNL_UTIL_OK"u8);
        near = front ? 0f : 0.021f;
        far = front ? 0.02f : 0.1f;
    }

    private static bool Prefix(byte* s, ReadOnlySpan<byte> want)
    {
        for (int i = 0; i < want.Length; i++) if (s[i] != want[i]) return false;
        return true;
    }

    /// <summary><c>RenderOnlyGobs</c>' two strcasecmp's: the character-preview model view and the compass are the only
    /// GUI scenes it lights.</summary>
    internal static bool IsLitScene(byte* name)
        => NameIs(name, "sceneGUI_guiModelView"u8) || NameIs(name, "sceneGUI_Viewport_Compass"u8);

    private static bool NameIs(byte* s, ReadOnlySpan<byte> want)
    {
        for (int i = 0; i < want.Length; i++)
            if ((s[i] | 0x20) != (want[i] | 0x20)) return false;
        return s[want.Length] == 0;
    }

    /// <summary>A <c>GLRender::RenderVertexData</c> call inside a GUI scene. The index range is resolved exactly as the
    /// funnel resolves it (@0x4d0570: mode 1 from the item tables unless overridden, mode 2 from the arguments); a
    /// non-indexed VDO is refused, because the VAO cache only builds over an element buffer.</summary>
    internal static bool Vdo(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        if (*_custom != IntPtr.Zero) { ToEngine(); return false; }
        CaptureVdo(vdo, item, prim, size, offset);
        _drawReset();
        return true;
    }

    private static void CaptureVdo(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        var v = new VdoRef(vdo);
        int mode = v.Mode;
        if (mode != 1 && mode != 2) { Refuse(ref _arrays, "a non-indexed VDO"); return; }
        if (item >= (uint)v.VertCount || (mode == 1 && item >= (uint)v.ElemCount)) { Refuse(ref _arrays, "an item past its VDO's tables"); return; }
        int baseVertex = *(int*)(v.VertItems + item * 8);
        long off; int count;
        if (mode == 1)
        {
            off = (offset == NoItem ? *(int*)(v.ElemItems + item * 8) : offset) * 2L;
            count = size == NoItem ? *(int*)(v.ElemItems + item * 8 + 4) : (int)size;
        }
        else
        {
            off = offset == NoItem ? 0 : offset * 2L;
            count = size == NoItem ? 0 : (int)size;
        }
        if (count <= 0) return;
        int slot = Slot(vdo);
        if (slot < 0) { SlotMissed = true; Refuse(ref _noSlot, "a VDO with no harvestable VAO"); return; }
        if (!Room()) return;
        ref Draw d = ref Draws[DrawN];
        d.Stream = false; d.Indexed = true; d.Slot = slot; d.Prim = _primTable[prim];
        d.Count = count; d.Offset = off; d.BaseVertex = baseVertex; d.Stride = 0; d.Format = 0;
        int flags = Snapshot(ref d);
        if (AlloyRenderer.GuiSlotSkinned(slot)) flags = Skin(flags);
        Seal(flags);
    }

    /// <summary>A <c>GLRender::RenderVertexStreamData</c> call inside a GUI scene -- text, particles, anything built on
    /// the CPU. The bytes are copied out of the pool's host shadow NOW: the stream is rewritten by the next draw that
    /// uses it, long before <see cref="GuiPass"/> flushes.</summary>
    internal static bool Stream(IntPtr vsdo, uint prim, uint size, uint offset)
    {
        if (*_custom != IntPtr.Zero) { ToEngine(); return false; }
        CaptureStream(vsdo, prim, size, offset);
        _drawReset();
        return true;
    }

    /// <summary>A draw under a material's CUSTOM program (content shaders -- a server's load screen repositions its bar
    /// and backdrop in its vertex stage): the engine's own funnel draws it, with the content's program and every uniform
    /// it declares, exactly as vanilla. What was captured before it is flushed first and the scene reopens after it, so
    /// the order -- and on Linux the shared depth buffer -- is the engine's.</summary>
    private static void ToEngine()
    {
        _toEngine++;
        if (LayerN == 0) return;
        Layer cont = Layers[LayerN - 1];
        Layers[LayerN - 1].Count = DrawN - cont.First;
        GuiPass.FlushNow();
        cont.First = DrawN; cont.Count = 0; cont.Clear = false;
        Layers[LayerN++] = cont;
    }

    private static void CaptureStream(IntPtr vsdo, uint prim, uint size, uint offset)
    {
        var s = new VertexStreamDataObject(vsdo);
        int stride = s.VertexStride;
        uint fmt = s.Format;
        if (!GuiStream.Layout(fmt, stride, out _))
        {
            if (_badFormat == 0) _log?.Always($"GuiCapture: stream format 0x{fmt:x} / stride {stride} is NOT DRAWN in a GUI scene (first sighting; totals on /alloy)");
            _badFormat++;
            return;
        }
        byte* vhost = OwnedBuffers.PoolHost(s.VertToken);
        if (vhost == null) { Refuse(ref _noHost, "a stream with no host shadow"); return; }
        if (size == 0 || !Room()) return;
        ushort* idx = null;
        if (s.Indexed != 0)
        {
            ushort* ehost = (ushort*)OwnedBuffers.PoolHost(s.ElemToken);
            if (ehost == null) { Refuse(ref _noHost, "an indexed stream with no element shadow"); return; }
            idx = ehost + offset;
        }
        ref Draw d = ref Draws[DrawN];
        if (!Stage(ref d, vhost, stride, fmt, _primTable[prim], idx, (int)size, (int)offset)) return;
        int flags = Snapshot(ref d);
        if ((fmt & GuiStream.FmtColor) != 0) flags |= FlagVColor;
        Seal(flags);
    }

    /// <summary>Copy one run into staging and point <paramref name="d"/> at it. Indexed: the referenced vertex span is
    /// copied and the indices rebased onto it; otherwise <paramref name="first"/>/<paramref name="count"/> vertices. Every
    /// run starts on a multiple of its stride, so a draw addresses it by base or first vertex. False = over a ceiling.</summary>
    private static bool Stage(ref Draw d, byte* vtx, int stride, uint fmt, uint glPrim, ushort* idx, int count, int first)
    {
        int lo = first, n = count;
        if (idx != null)
        {
            lo = int.MaxValue; int hi = -1;
            for (int i = 0; i < count; i++) { int x = idx[i]; if (x < lo) lo = x; if (x > hi) hi = x; }
            n = hi - lo + 1;
            if (IdxN + count > IdxMax) { Overflow("indices"); return false; }
        }
        int at = (VtxN + stride - 1) / stride * stride;
        long bytes = (long)n * stride;
        if (at + bytes > VtxBytes) { Overflow("vertex bytes"); return false; }
        fixed (byte* dst = &Vtx[at]) Buffer.MemoryCopy(vtx + (long)lo * stride, dst, bytes, bytes);
        VtxN = at + (int)bytes;
        d.Stream = true; d.Stride = stride; d.Format = fmt; d.Prim = glPrim; d.Slot = 0; d.Count = count;
        if (idx != null)
        {
            fixed (ushort* dst = &Idx[IdxN]) for (int i = 0; i < count; i++) dst[i] = (ushort)(idx[i] - lo);
            d.Indexed = true; d.Offset = IdxN * 2L; d.BaseVertex = at / stride;
            IdxN += count;
        }
        else { d.Indexed = false; d.Offset = at / stride; d.BaseVertex = 0; }
        return true;
    }

    /// <summary>CX-101: open a layer for draws that come through no engine funnel (nuklear's), with its own scissor. No
    /// depth clear, the full depth range: what follows is depth-test-off 2D.</summary>
    internal static bool BeginRawLayer(int x, int y, int w, int h, int sx, int sy, int sw, int sh)
    {
        if (LayerN >= MaxLayers) { Overflow("layers"); return false; }
        ref Layer l = ref Layers[LayerN++];
        l.X = x; l.Y = y; l.W = w; l.H = h; l.SX = sx; l.SY = sy; l.SW = sw; l.SH = sh;
        l.First = DrawN; l.Count = 0; l.Near = 0f; l.Far = 1f; l.Clear = false;
        return true;
    }

    /// <summary>CX-101: one CPU-built draw into the open raw layer -- unlit, blended with (<paramref name="src"/>,
    /// <paramref name="dst"/>), no depth, no cull, white global colour, identity texture matrix.</summary>
    internal static void Raw(byte* vtx, int stride, uint fmt, ushort* idx, int count, int first, uint glPrim,
                             in Matrix4x4 mvp, int tex, uint src, uint dst)
    {
        if (count <= 0 || !Room() || LayerN == 0) return;
        ref Draw d = ref Draws[DrawN];
        if (!Stage(ref d, vtx, stride, fmt, glPrim, idx, count, first)) return;
        d.Blend = true; d.Src = src; d.Dst = dst; d.DepthTest = false; d.DepthWrite = false; d.Cull = false; d.Tex = tex;
        fixed (float* r = &Records[DrawN * RecFloats])
        {
            *(Matrix4x4*)r = mvp;
            *(Matrix4x4*)(r + 16) = Matrix4x4.Identity;
            r[32] = 1f; r[33] = 0f; r[34] = 0f; r[35] = 1f;
            r[36] = 1f; r[37] = 1f; r[38] = 1f; r[39] = 1f;
        }
        Seal((tex != 0 ? FlagTex : 0) | ((fmt & GuiStream.FmtColor) != 0 ? FlagVColor : 0));
        Layers[LayerN - 1].Count = DrawN - Layers[LayerN - 1].First;
    }

    /// <summary>Everything the engine's program would have been fed, read off <c>GLRender</c>'s caches into the draw
    /// and its record: the three matrix stacks and the texture stack, stage 0's texture, the blend/depth/cull caps,
    /// the global colour and (lit) the material. Returns the record's flag bits so far.</summary>
    private static int Snapshot(ref Draw d)
    {
        uint caps = *_caps;
        d.Blend = (caps & (1u << CapBlend)) != 0;
        d.DepthTest = (caps & (1u << CapDepthTest)) != 0;
        d.Cull = (caps & (1u << CapCull)) != 0;
        d.DepthWrite = *_depthMask != 0;
        d.Src = Issue.BlendGl(*_src);
        d.Dst = Issue.BlendGl(*_dst);
        d.Tex = (*_texStages & 1) != 0 && _bound[0] == TargetTex2D ? _bound[1] : 0;

        var st = new aurMatrixStack((IntPtr)_stacks);
        Matrix4x4 mv = st[ModeModel].Top * st[ModeView].Top;
        Matrix4x4 mvp = mv * st[ModeProj].Top;
        ref Matrix4x4 tm = ref st[ModeTexture].Top;
        fixed (float* r = &Records[DrawN * RecFloats])
        {
            *(Matrix4x4*)r = mvp;
            *(Matrix4x4*)(r + 16) = mv;
            r[32] = tm.M11; r[33] = tm.M21; r[34] = tm.M12; r[35] = tm.M22;
            for (int i = 0; i < 4; i++) r[36 + i] = _color[i];
            bool lit = (caps & (1u << CapLighting)) != 0;
            var m = new GlMaterial((IntPtr)_material);
            *(Vector4*)(r + 40) = lit ? m.Ambient : Vector4.Zero;
            *(Vector4*)(r + 44) = lit ? m.Diffuse : Vector4.Zero;
            *(Vector4*)(r + 48) = lit ? m.Emissive : Vector4.Zero;
            return (d.Tex != 0 ? FlagTex : 0) | (lit ? FlagLit : 0) | ((caps & (1u << CapAlphaTest)) != 0 ? FlagAlphaTest : 0);
        }
    }

    /// <summary>A skinned VDO: the engine's <c>RenderSkinned</c> has just solved this part's palette into
    /// <c>GLRender::m_bones</c> (its layout is <see cref="SymbolMap.GlRenderBones"/>'s), so it is copied beside the record.</summary>
    private static int Skin(int flags)
    {
        if (SkinN >= MaxSkins) { Overflow("skinned draws"); return flags; }
        fixed (float* dst = &Bones[SkinN * BoneFloats])
            Buffer.MemoryCopy(_bones, dst, BoneFloats * sizeof(float), BoneFloats * sizeof(float));
        Records[DrawN * RecFloats + 53] = SkinN * BoneFloats / 4;
        SkinN++;
        return flags | FlagSkin;
    }

    private static void Seal(int flags)
    {
        Records[DrawN * RecFloats + 52] = flags;
        DrawN++;
        _draws++;
    }

    private static bool Room()
    {
        if (DrawN < MaxDraws) return true;
        Overflow("draws");
        return false;
    }

    /// <summary>The VAO-cache slot for a VDO, remembered across frames and re-validated on every use: a world edge can
    /// retire the slot and a recycled address changes its signature (<see cref="AlloyRenderer.SlotHolds"/>).</summary>
    private static int Slot(IntPtr vdo)
    {
        if (_slots.TryGetValue(vdo, out int s) && AlloyRenderer.SlotHolds(s, vdo)) return s;
        s = AlloyRenderer.VdoSlot(vdo);
        if (s >= 0) _slots[vdo] = s; else _slots.Remove(vdo);
        return s;
    }

    private static void Refuse(ref long counter, string what)
    {
        counter++;
        if (counter == 1 && _said++ < 16)
            _log?.Always($"GuiCapture: {what} is NOT DRAWN in a GUI scene (first sighting; totals on /alloy)");
    }

    private static void Overflow(string what)
    {
        _overflow++;
        if (_overflow == 1)
            _log?.Always($"GuiCapture: a frame's GUI ran past the {what} staging ceiling -- the rest of it is NOT DRAWN (raise the cap)");
    }

    /// <summary>Consumed by a flush: the next scene starts a fresh list.</summary>
    internal static void Reset() { LayerN = 0; DrawN = 0; SkinN = 0; VtxN = 0; IdxN = 0; }

    /// <summary>The cache holds VDO pointers; an area or module edge may free any of them.</summary>
    internal static void Forget() => _slots.Clear();

    internal static string Stats()
        => $"{_layers} scenes, {_draws} draws captured" +
           (_noSlot + _noHost + _badFormat + _arrays + _overflow == 0 ? "" :
            $"; NOT DRAWN: {_noSlot} no-vao, {_noHost} no-host, {_badFormat} format, {_arrays} non-indexed, {_overflow} over-cap") +
           (_toEngine == 0 ? "" : $"; {_toEngine} custom-shader draws by the engine's own program");

    /// <summary>Off-device: the lit-scene test is the engine's case-insensitive compare, whole-string.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        byte* a = stackalloc byte[32];
        void Put(string s) { for (int i = 0; i < s.Length; i++) a[i] = (byte)s[i]; a[s.Length] = 0; }
        Put("sceneGUI_guiModelView"); check.Expect(IsLitScene(a), "the model view is lit");
        Put("SCENEGUI_VIEWPORT_COMPASS"); check.Expect(IsLitScene(a), "the compass is lit, compared case-insensitively");
        Put("sceneGUI_guiModelViewX"); check.Expect(!IsLitScene(a), "a longer name is not the model view");
        Put("sceneGUI_minimap"); check.Expect(!IsLitScene(a), "any other panel is unlit");
        bool latch = false;
        DepthOf(a, ref latch, out float n0, out float f0, out bool c0);
        check.Expect(c0 && n0 == 0.021f && f0 == 0.1f, "the first panel of a frame clears and takes the panel range");
        Put("sceneGUI_MouseOverlay"); DepthOf(a, ref latch, out float n1, out float f1, out bool c1);
        check.Expect(!c1 && n1 == 0f && f1 == 0.02f, "the cursor overlay shares the depth buffer, in front of every panel");
        Put("sceneGUI_OverlayX"); DepthOf(a, ref latch, out _, out float f2, out _);
        check.Expect(f2 == 0.02f, "the overlay names are prefix compares, as repz cmpsb over the literal is");
        Put("sceneGUI_mouseoverlay"); DepthOf(a, ref latch, out _, out float f3, out _);
        check.Expect(f3 == 0.1f, "...and case-sensitive");
        Put("sceneArea"); DepthOf(a, ref latch, out float n4, out float f4, out bool c4);
        check.Expect(!c4 && n4 == 0f && f4 == 1f && !latch, "a non-GUI view takes the full range and drops the latch");
        Put("sceneGUI_minimap"); DepthOf(a, ref latch, out _, out _, out bool c5);
        check.Expect(c5, "...so the next GUI view clears again");
        log.Info(check.Ok ? "GuiCapture.SelfCheck: passed" : "GuiCapture.SelfCheck: FAILED");
    }
}

/// <summary>CX-95: a <c>VertexStreamDataObject</c>'s interleaved layout, from its attribute flags. The order and sizes
/// are <c>GLRender::SetVertexStreamDataObject</c> @0x4d07a0's <c>VertexAttribPointer</c> chain; the four the GUI program
/// reads are located, the rest only occupy bytes. Pure, so it is checked off-device.</summary>
internal static class GuiStream
{
    internal const uint FmtColor = 0x1, FmtNormal = 0x2, FmtPos = 0x4, FmtUv0 = 0x800;   // not-an-offset: stream attribute flag bits
    /// <summary>CX-101: not an engine format -- nuklear's vertex (<c>{vec2 pos, vec2 uv, u8x4 colour}</c>, 20 bytes,
    /// <c>docs/research/nui-draw-in-alloy.md</c> §3), on a bit the engine's flags never use.</summary>
    internal const uint FmtNuklear = 0x80000000 | FmtColor;   // not-an-offset: a CX-private format tag

    /// <summary>Byte offsets within a vertex; -1 = absent. <see cref="PosN"/> is the position's component count.</summary>
    internal struct Offsets { public int Pos, PosN, Uv, Color, Normal; }

    /// <summary>(flag, bytes) in the engine's interleave order; bit 0x400 is never bound and takes no room.</summary>
    private static ReadOnlySpan<uint> Order => [0x1, 4, 0x2, 12, 0x4, 12, 0x8, 12, 0x10, 4, 0x20, 8, 0x40, 16,
                                                0x80, 4, 0x100, 12, 0x200, 4, 0x800, 8, 0x1000, 8, 0x2000, 8, 0x4000, 8];

    /// <summary>Lay <paramref name="fmt"/> out. False = no position, or the flags do not add up to the stride the
    /// engine stored -- i.e. this table no longer describes the build, which is a refusal, not a guess.</summary>
    internal static bool Layout(uint fmt, int stride, out Offsets o)
    {
        if (fmt == FmtNuklear)
        {
            o = new Offsets { Pos = 0, PosN = 2, Uv = 8, Color = 16, Normal = -1 };
            return stride == 20;
        }
        o = new Offsets { Pos = -1, PosN = 3, Uv = -1, Color = -1, Normal = -1 };
        int at = 0;
        ReadOnlySpan<uint> t = Order;
        for (int i = 0; i < t.Length; i += 2)
        {
            if ((fmt & t[i]) == 0) continue;
            if (t[i] == FmtPos) o.Pos = at;
            else if (t[i] == FmtUv0) o.Uv = at;
            else if (t[i] == FmtColor) o.Color = at;
            else if (t[i] == FmtNormal) o.Normal = at;
            at += (int)t[i + 1];
        }
        return o.Pos >= 0 && at == stride && stride > 0;
    }

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GuiStream.SelfCheck");
        check.Expect(Layout(FmtPos | FmtUv0, 20, out var a) && a.Pos == 0 && a.Uv == 12 && a.Color < 0,
          "position + uv: 20 bytes, uv after position");
        check.Expect(Layout(FmtColor | FmtPos | FmtUv0, 24, out var b) && b.Color == 0 && b.Pos == 4 && b.Uv == 16,
          "colour leads the interleave, as the engine binds it at offset 0");
        check.Expect(Layout(FmtNormal | FmtPos, 24, out var c) && c.Normal == 0 && c.Pos == 12,
          "the normal comes before the position");
        check.Expect(!Layout(FmtPos | FmtUv0, 24, out _), "a stride the flags do not add up to is refused");
        check.Expect(!Layout(FmtUv0, 8, out _), "no position is refused");
        check.Expect(Layout(FmtPos | 0x400, 12, out _), "bit 0x400 takes no room");
        check.Expect(Layout(FmtNuklear, 20, out var n) && n.PosN == 2 && n.Uv == 8 && n.Color == 16,
          "nuklear's vertex: vec2 position, uv at 8, colour at 16");
        log.Info(check.Ok ? "GuiStream.SelfCheck: passed" : "GuiStream.SelfCheck: FAILED");
    }
}
