using System;
using System.Text;
using Sowx.Framework;

namespace Sowx.Services.Nameplate;

/// <summary>
/// Every native read the overhead plate/bubble needs, in one place. No UI types, no GL: a service
/// or table calls in here and gets managed values back. Offsets are the ones verified in
/// <c>docs/re/nameplate.md</c> and re-confirmed by disassembly where the contract was thin.
/// <para>The standing rule from #33/#138/#145 applies to every pointer below: a <c>CNWCObject*</c> is
/// BORROWED. Nothing here stores one; callers hold object <b>ids</b> and re-resolve through
/// <see cref="GameObject"/> every frame, treating null as the despawn signal.</para>
/// </summary>
public static unsafe class NameplateNative
{
    // ---- object layout (CNWCObject, the base subobject -- what SetPopUpBubbleText's `this` is) ----
    // Offsets that differ on Windows are `IsWindows() ? win : linux` (#420, decomp-diffed against the same
    // build); a plain const is identical on both.
    private static readonly bool Win = OperatingSystem.IsWindows();
    /// <summary>uint object id.</summary>
    private const int ObjId = 0x08;
    /// <summary>Object type byte: 5 creature, 6 item, 9 placeable, 0xa door.</summary>
    private const int ObjType = 0x0c;
    /// <summary>World position x/y/z (three floats).</summary>
    private const int ObjPosX = 0x38;
    /// <summary>CGuiInGamePopUpBubble* (nullable).</summary>
    private const int ObjBubble = 0xb8;
    /// <summary>1 => the server has retired this object: no plate and no bubble.</summary>
    private const int ObjRetired = 0x104;

    /// <summary><c>ObjectTextBubbleOverride</c> -- the module's own plate text. Mode int at <c>+0x124</c>,
    /// then a <c>CExoString</c>: <c>char*</c> at <c>+0x128</c> and a <b>dword</b> length at <c>+0x130</c>
    /// (<c>mov 0x130(%rbx),%edx</c>). 1 = replace, 2 = prepend, 3 = append, anything else = leave alone.
    /// <para>CNWCObject-relative for all three plated classes: creature and placeable read them at these
    /// offsets verbatim (disasm @0x71ab8e, @0x7ea9b8), and <c>CNWCItem</c>'s <c>+0x16c/0x170/0x178</c> is the
    /// same triple through its base subobject at <c>+0x48</c> -- the <c>lea 0x48(%rbx)</c> that feeds
    /// <c>SetPopUpBubbleText</c> at @0x7cebec. Every pointer this file is handed is already that base.</para></summary>
    private const int ObjOverrideMode = 0x124, ObjOverrideText = 0x128, ObjOverrideLen = 0x130;

    // ---- per-class "the feedback plate is currently shown for me" flag ----
    /// <summary>CNWCCreature + 0x468, set at the top of its ShowFeedbackBubbleText (disasm @0x71a81e).</summary>
    private static readonly int CreaturePlated = Win ? 0x448 : 0x468;
    /// <summary>CNWCPlaceable + 0x218 (disasm @0x7ea8b8).</summary>
    private static readonly int PlaceablePlated = Win ? 0x1f8 : 0x218;
    /// <summary>CNWCItem is multiply inherited: the flag is at CNWCItem+0x2cc (disasm @0x7ceaa2) and the
    /// CNWCObject base sits at CNWCItem+0x48 (the <c>lea 0x48(%rbx)</c> feeding SetPopUpBubbleText at
    /// 0x7cebec, matching the <c>_ZThn72_</c> thunk's <c>sub $0x48,%rdi</c>). So from a CNWCObject* the
    /// flag is 0x2cc - 0x48. MSVC hands the override the subobject pointer directly (its body does the
    /// <c>-0x48</c>), so the Windows value is already subobject-relative.</summary>
    private static readonly int ItemPlated = Win ? 0x264 : 0x2cc - 0x48;

    // ---- creature ----
    /// <summary>Reaction byte: 1 friend, 2 enemy, else neutral.</summary>
    private static readonly int CreatureReaction = Win ? 0x388 : 0x3a8;
    /// <summary>CNWCCreatureStats*.</summary>
    private static readonly int CreatureStats = Win ? 0x3b0 : 0x3d0;
    /// <summary>Area id; hidden when it differs from the player's area.</summary>
    private static readonly int CreatureArea = Win ? 0x458 : 0x478;

    // ---- CNWCCreatureStats ----
    private const int StatsFirstName = 0x20, StatsFirstLen = 0x28;
    private const int StatsLastName = 0x30, StatsLastLen = 0x38;
    private const int StatsOverride = 0x40, StatsOverrideLen = 0x48;
    /// <summary>Damage-level byte, 0..5 -- a server-sent enum, not a client HP division.</summary>
    private const int StatsDamage = 0x8e;

    // ---- CGuiInGamePopUpBubble ----
    /// <summary>The projected screen anchor (x at +0x154, y at +0x158), whole-pixel snapped. Under the
    /// mode-0 suppression the engine refreshes it every tick for every bubble-owning object.
    /// <para>NOT the space a <c>Window</c> draws in: see <see cref="AnchorRaw"/>.</para></summary>
    private static readonly int BubbleAnchorX = Win ? 0x14c : 0x154, BubbleAnchorY = Win ? 0x150 : 0x158;
    /// <summary>1 while live (chat/feedback) text owns the bubble. Nothing clears it under mode 0, and
    /// MouseOver refuses to build a plate while it is 1 -- so we clear it ourselves (hook 3).</summary>
    private static readonly int BubbleLiveText = Win ? 0x144 : 0x14c;

    // ---- module / camera ----
    /// <summary>CNWCModule + 0xf8 = the player's current CNWCArea*.</summary>
    private const int ModuleArea = 0xf8;
    /// <summary>CNWCArea + 0x118 = its area id.</summary>
    private const int AreaId = 0x118;
    /// <summary>Camera + 0xb0 = position x/y/z. This is exactly what <c>Camera::GetPosition()</c> reads
    /// (disasm @0x4813f1); a field read avoids its two-XMM return ABI, which a delegate* cannot express.</summary>
    private const int CameraPos = 0xb0;
    /// <summary>Camera + 0xbc = the orientation quaternion, stored (w, x, y, z) -- the four floats
    /// <c>Camera::SetOrientation</c> writes (disasm @0x4a3939) and the ones <c>MapToScreen</c> conjugates at
    /// <c>lea 0xbc(%rbx)</c> (@0x4a3ed5). Same field Alloy's <c>FrameRead</c> re-orders into Numerics.</summary>
    private const int CameraRot = 0xbc;
    /// <summary>Camera + 0x4d4..0x4e0 = the camera's viewport rect {x, y, w, h} as floats -- the four values
    /// <c>Camera::SetViewPort(float,float,float,float)</c> stores (disasm @0x4a38a0, which clamps w and h up
    /// to 1.0). <c>MapToScreen</c> loads them at its top (@0x4a3d83) and, when either extent is exactly 0,
    /// overwrites all four from <c>RenderInterface::GetViewPort</c> instead (@0x4a3eb8).</summary>
    private static readonly int CameraViewport = Win ? 0x4d8 : 0x4d4;

    /// <summary>CClientExoApp + 8 = CClientExoAppInternal.</summary>
    private const int AppInternal = 0x8;
    /// <summary>CClientExoAppInternal + 0x180: bit 0 = the "show mouse-over feedback" option,
    /// bit 1 = highlight-all (Tab) currently held. The engine tests the WHOLE word, which is why Tab
    /// plates even with the option off.</summary>
    private const int InternalFeedbackWord = 0x180;

    /// <summary>Virtual slot of <c>GetPortrait(unsigned char)</c> (MSVC orders the table differently).</summary>
    private static readonly int GetPortraitVtblOff = Win ? 0x1d0 : 0x1d8;
    /// <summary>CNWCModule + 0xe8 = the live <c>Camera*</c> -- what <c>CClientExoApp::GetModuleCamera()</c>
    /// returns (disasm @0x63a930); read directly because MSVC inlined that accessor out of existence.</summary>
    private const int ModuleCameraOff = 0xe8;
    /// <summary>Portrait size byte: the suffix appended to the base resref. Verified by decompiling
    /// <c>CNWPortrait::GetPortrait(unsigned char)</c> (nm <c>0x81e910</c>), whose suffix table is
    /// <c>{ "", "T", "S", "M", "L", "H" }</c> indexed by the byte -- so 1 is TINY (a 16x25 image), which is
    /// what the engine's own bubble asks for and why our portraits were blurry. 3 is MEDIUM (64x100).
    /// <para>Upsizing is safe without a fallback of ours: that same function walks a ladder of its own --
    /// it asks <c>CExoResMan::Exists</c> for the requested suffix and then for +1, -1, +2, -2, +3, -3, +4, -4
    /// before giving up, so a creature with no <c>_m</c> resolves to <c>_l</c> or <c>_s</c> on its own.</para></summary>
    private const byte PortraitSizePlate = 3;

    /// <summary>CResRef is 17 bytes (char[16] + a length byte); a 16-byte buffer smashes the stack canary.</summary>
    private const int ResRefScratch = 32;

    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    private static readonly delegate* unmanaged<IntPtr, uint, IntPtr> _getGameObject =
        (delegate* unmanaged<IntPtr, uint, IntPtr>)Native.Sym(SymbolMap.GetGameObject);
    private static readonly delegate* unmanaged<IntPtr, IntPtr> _getModule =
        (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.GetModule);
    private static readonly delegate* unmanaged<float*, void> _getViewPort =
        (delegate* unmanaged<float*, void>)Native.Sym(SymbolMap.GetViewPort);

    /// <summary>Whether the engine chain we need resolved at all. False headless (run-demo) and under a
    /// client that does not export these -- every accessor below then answers its neutral value.</summary>
    public static bool Ready => _appMgr != IntPtr.Zero && _getGameObject != null;

    /// <summary>The live CClientExoApp*, or Zero. Two loads: the symbol holds a CAppManager*, whose first
    /// field is the CClientExoApp* (the chat/dialog services walk the same chain).</summary>
    private static IntPtr ExoApp()
    {
        if (_appMgr == IntPtr.Zero) return IntPtr.Zero;
        IntPtr mgr = *(IntPtr*)_appMgr;
        return mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
    }

    /// <summary>A live CNWCObject* for <paramref name="id"/>, or Zero when the object is gone. Zero is the
    /// despawn signal, not an error. Hot: one chain walk and one engine call, no allocation.</summary>
    public static IntPtr GameObject(uint id)
    {
        if (_getGameObject == null) return IntPtr.Zero;
        IntPtr app = ExoApp();
        return app == IntPtr.Zero ? IntPtr.Zero : _getGameObject(app, id);
    }

    public static uint IdOf(IntPtr obj) => obj == IntPtr.Zero ? 0u : *(uint*)(obj + ObjId);
    public static byte TypeOf(IntPtr obj) => obj == IntPtr.Zero ? (byte)0 : *(byte*)(obj + ObjType);

    /// <summary>Object type bytes we care about.</summary>
    public const byte TypeCreature = 5, TypeItem = 6, TypePlaceable = 9, TypeDoor = 0xa;

    /// <summary>The server has retired this object -- it gets neither element (acceptance §9).</summary>
    public static bool Retired(IntPtr obj) => obj != IntPtr.Zero && *(int*)(obj + ObjRetired) == 1;

    public static IntPtr Bubble(IntPtr obj) => obj == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(obj + ObjBubble);

    /// <summary>
    /// The engine's own projected anchor for this object's bubble panel, EXACTLY as stored. False when there
    /// is no bubble (nothing to anchor to yet).
    /// <para>Space, settled by disassembling <c>Camera::MapToScreen(Vector, bool)</c> @0x4a3d60:
    /// <c>UpdatePopUpBubble</c> calls it with the bool <b>false</b> (<c>xor %esi,%esi</c> @0x7e8520), which
    /// takes the branch at <c>je 0x4a4008</c> and skips the normalise-and-flip tail entirely. What is stored
    /// is therefore the raw GL window transform: <c>planelineintersection</c> resolves to
    /// <c>t = -(d + n·p0)/(n·dir) = -1/P.z</c> against the plane <c>z = -1</c>, so eye space is -Z-forward
    /// (the convention Alloy's <c>ViewForward = (0,0,-1)</c> already runs on), and the tail is
    /// <c>vp.y + h/2 + Y'·(h/2)/tan(fov/2)</c> -- i.e. <c>(ndc.y + 1)/2 · h + vp.y</c>, character for
    /// character the GL window transform. So: <b>pixels, BOTTOM-left origin, y UP</b>, in the CAMERA's
    /// viewport rect and divided by the GUI scale. Corroborating: the bool-true tail computes
    /// <c>1 - y/h</c> (@0x4a3ff9), which only makes sense flipping a bottom-up pixel into a top-down
    /// normalised coordinate.</para>
    /// <para><see cref="PlateLayout.ToDrawSpace"/> is what turns this into the top-left, y-down pixels a
    /// <c>Window</c> draws in; nothing else may consume these two floats directly.</para>
    /// </summary>
    public static bool AnchorRaw(IntPtr obj, out float x, out float y)
    {
        x = 0f; y = 0f;
        IntPtr b = Bubble(obj);
        if (b == IntPtr.Zero) return false;
        x = *(float*)(b + BubbleAnchorX);
        y = *(float*)(b + BubbleAnchorY);
        return true;
    }

    /// <summary>Clear the bubble's "live text owns me" latch. Load-bearing: under the mode-0 suppression
    /// nothing else ever clears it, and <c>MouseOver</c> refuses to build a plate while it is 1 -- without
    /// this a creature's plate dies permanently the first time it speaks.</summary>
    public static void ClearLiveTextLatch(IntPtr obj)
    {
        IntPtr b = Bubble(obj);
        if (b != IntPtr.Zero) *(int*)(b + BubbleLiveText) = 0;
    }

    /// <summary>Is the engine currently showing its feedback plate for this object? The per-class flag set at
    /// the top of <c>ShowFeedbackBubbleText</c> and cleared in <c>MouseOff</c> -- the pollable "hovered or
    /// Tab-highlighted" state, which is why acceptance §1/§8 need no key tracking of our own.</summary>
    public static bool Plated(IntPtr obj, byte type)
    {
        if (obj == IntPtr.Zero) return false;
        int off = type switch
        {
            TypeCreature => CreaturePlated,
            TypePlaceable => PlaceablePlated,
            TypeItem => ItemPlated,
            _ => 0,
        };
        return off != 0 && *(int*)(obj + off) != 0;
    }

    /// <summary>The object's <c>ObjectTextBubbleOverride</c> mode: 1 replace, 2 prepend, 3 append, 0 (or any
    /// other value) none. HOT -- one field read, no allocation.</summary>
    public static int OverrideMode(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return 0;
        int m = *(int*)(obj + ObjOverrideMode);
        return m is 1 or 2 or 3 ? m : 0;
    }

    /// <summary>Does the object's override text still equal <paramref name="cached"/>? HOT: compares the
    /// engine's bytes against the cached string in place, so a steady plate allocates nothing and a server
    /// that flips "Sneaking" on mid-hover is caught on the next frame. Latin1, matching <see cref="RawStr"/>.
    /// <para>A length compare alone would not do: <c>CExoString</c> assignment can land a different payload of
    /// the same length in the same block, and status labels are all near-identical lengths.</para></summary>
    public static bool OverrideTextIs(IntPtr obj, string cached)
    {
        if (obj == IntPtr.Zero) return cached.Length == 0;
        byte* p = *(byte**)(obj + ObjOverrideText);
        int len = *(int*)(obj + ObjOverrideLen);
        if (p == null || len <= 0 || len > MaxOverrideLen) return cached.Length == 0;
        if (cached.Length != len) return false;
        for (int i = 0; i < len; i++) if (cached[i] != (char)p[i]) return false;
        return true;
    }

    /// <summary>The override text verbatim -- NOT trimmed. The leading space / leading LF the server sends is
    /// the splice itself (<c>sow-codebase</c> <c>TextBubbleService.ComposeOverride</c> emits its own
    /// separators), so trimming it would glue "Poisoned" onto the injury line without its space and drop the
    /// break before "Sneaking". COLD -- allocates, called only on the change edge.</summary>
    public static string OverrideText(IntPtr obj)
        => obj == IntPtr.Zero ? "" : RawStr(obj, ObjOverrideText, ObjOverrideLen, MaxOverrideLen);

    /// <summary>Longest override we will copy out. Past this the pointer is not what we think it is.</summary>
    private const int MaxOverrideLen = 1024;

    public static void Position(IntPtr obj, out float x, out float y, out float z)
    {
        x = 0f; y = 0f; z = 0f;
        if (obj == IntPtr.Zero) return;
        x = *(float*)(obj + ObjPosX);
        y = *(float*)(obj + ObjPosX + 4);
        z = *(float*)(obj + ObjPosX + 8);
    }

    /// <summary>The live game camera's world position. NOT the Alloy <c>CameraPos</c> global, which at
    /// present time is the UI frame's camera at (0,0,742). False when there is no module camera.</summary>
    public static bool CameraPosition(out float x, out float y, out float z)
    {
        x = 0f; y = 0f; z = 0f;
        IntPtr cam = ModuleCamera();
        if (cam == IntPtr.Zero) return false;
        x = *(float*)(cam + CameraPos);
        y = *(float*)(cam + CameraPos + 4);
        z = *(float*)(cam + CameraPos + 8);
        return true;
    }

    /// <summary>The live game camera's forward axis in world space: the camera quaternion applied to
    /// <c>(0,0,-1)</c>. Used to reject objects BEHIND the camera, which the engine's own projection does not:
    /// <c>planelineintersection</c> tests its denominator only for equality with zero (@0x5b403f), never its
    /// sign, so a point behind the camera projects to a mirrored on-screen anchor. False when there is no
    /// module camera.</summary>
    public static bool CameraForward(out float x, out float y, out float z)
    {
        x = 0f; y = 0f; z = 0f;
        IntPtr cam = ModuleCamera();
        if (cam == IntPtr.Zero) return false;
        float* q = (float*)(cam + CameraRot);
        float qw = q[0], qx = q[1], qy = q[2], qz = q[3];
        // -(third column of the rotation matrix) -- the quaternion applied to (0,0,-1).
        x = -2f * (qx * qz + qw * qy);
        y = -2f * (qy * qz - qw * qx);
        z = -(1f - 2f * (qx * qx + qy * qy));
        return true;
    }

    /// <summary>The viewport rect <c>Camera::MapToScreen</c> projected into: the camera's own
    /// <c>+0x4d4..0x4e0</c>, or -- when either extent is exactly zero, which is the test at @0x4a3db4/@0x4a3de0
    /// -- the GL viewport, exactly as the engine's own fallback does. False when there is no camera and no
    /// resolvable <c>GetViewPort</c>, in which case the caller must assume the draw viewport.</summary>
    public static bool ViewportRect(out float x, out float y, out float w, out float h)
    {
        x = 0f; y = 0f; w = 0f; h = 0f;
        if (CameraViewportRect(out x, out y, out w, out h) && w != 0f && h != 0f) return true;
        return GlViewportRect(out x, out y, out w, out h);
    }

    /// <summary>The camera's own viewport fields, unfiltered -- zero extents and all. Separated from
    /// <see cref="ViewportRect"/> so <c>/nameplate probe</c> can say WHICH of the two sources the conversion
    /// actually used; a silent fallback is exactly the ambiguity the probe exists to remove.</summary>
    public static bool CameraViewportRect(out float x, out float y, out float w, out float h)
    {
        x = 0f; y = 0f; w = 0f; h = 0f;
        IntPtr cam = ModuleCamera();
        if (cam == IntPtr.Zero) return false;
        float* v = (float*)(cam + CameraViewport);
        x = v[0]; y = v[1]; w = v[2]; h = v[3];
        return true;
    }

    /// <summary>The GL viewport as the engine last set it (<c>GLRender::m_viewport</c>, widened to floats).
    /// Reported by <c>/nameplate probe</c> and used as <see cref="ViewportRect"/>'s fallback.</summary>
    public static bool GlViewportRect(out float x, out float y, out float w, out float h)
    {
        x = 0f; y = 0f; w = 0f; h = 0f;
        if (_getViewPort == null) return false;
        float* buf = stackalloc float[4];
        _getViewPort(buf);
        x = buf[0]; y = buf[1]; w = buf[2]; h = buf[3];
        return w > 0f && h > 0f;
    }

    /// <summary>The live <c>Camera*</c> off the module, or Zero.</summary>
    private static IntPtr ModuleCamera()
    {
        if (_getModule == null) return IntPtr.Zero;
        IntPtr app = ExoApp();
        IntPtr module = app == IntPtr.Zero ? IntPtr.Zero : _getModule(app);
        return module == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(module + ModuleCameraOff);
    }

    /// <summary>The player's current area id, or 0 when there is no module/area.</summary>
    public static uint PlayerAreaId()
    {
        if (_getModule == null) return 0u;
        IntPtr app = ExoApp();
        if (app == IntPtr.Zero) return 0u;
        IntPtr module = _getModule(app);
        if (module == IntPtr.Zero) return 0u;
        IntPtr area = *(IntPtr*)(module + ModuleArea);
        return area == IntPtr.Zero ? 0u : *(uint*)(area + AreaId);
    }

    /// <summary>A creature's area id (0 for anything else). Only the creature path is area-gated in vanilla;
    /// placeables and items rely on being unloaded instead.</summary>
    public static uint CreatureAreaId(IntPtr obj, byte type)
        => obj != IntPtr.Zero && type == TypeCreature ? *(uint*)(obj + CreatureArea) : 0u;

    /// <summary>The "show mouse-over feedback" option word: bit 0 = the option, bit 1 = Tab held. Read for
    /// the <c>/nameplate</c> report only -- the plate trigger itself is the engine's, which already tests
    /// this whole word.</summary>
    public static int FeedbackWord()
    {
        IntPtr app = ExoApp();
        if (app == IntPtr.Zero) return 0;
        IntPtr intern = *(IntPtr*)(app + AppInternal);
        return intern == IntPtr.Zero ? 0 : *(int*)(intern + InternalFeedbackWord);
    }

    /// <summary>Damage-level index 0..5 for a creature (5 = dead), or -1 when it has no stats.</summary>
    public static int DamageLevel(IntPtr obj, byte type)
    {
        if (obj == IntPtr.Zero || type != TypeCreature) return -1;
        IntPtr stats = *(IntPtr*)(obj + CreatureStats);
        if (stats == IntPtr.Zero) return -1;
        int lvl = *(byte*)(stats + StatsDamage);
        return lvl > 5 ? 5 : lvl;
    }

    /// <summary>Reaction byte for the name colour: 1 friend, 2 enemy, anything else neutral.</summary>
    public static byte Reaction(IntPtr obj, byte type)
        => obj != IntPtr.Zero && type == TypeCreature ? *(byte*)(obj + CreatureReaction) : (byte)0;

    /// <summary>The creature's display name off its stats block: the override wins outright, else
    /// "first last", else whichever half is non-empty, else "". COLD -- it allocates a string, so it is
    /// resolved once per plate candidate, never per frame.</summary>
    public static string CreatureName(IntPtr obj, byte type)
    {
        if (obj == IntPtr.Zero || type != TypeCreature) return "";
        IntPtr stats = *(IntPtr*)(obj + CreatureStats);
        if (stats == IntPtr.Zero) return "";
        string over = Str(stats, StatsOverride, StatsOverrideLen);
        if (over.Length != 0) return over;
        string first = Str(stats, StatsFirstName, StatsFirstLen);
        string last = Str(stats, StatsLastName, StatsLastLen);
        if (first.Length == 0) return last;
        if (last.Length == 0) return first;
        return first + " " + last;
    }

    /// <summary>Longest string we will copy out of an engine char*/len pair. A larger length means the
    /// pointer is not what we think it is, so we return "" rather than walk it.</summary>
    private const int MaxNameLen = 256;

    private static string Str(IntPtr owner, int ptrOff, int lenOff)
        => RawStr(owner, ptrOff, lenOff, MaxNameLen).Trim();

    /// <summary>An engine <c>char* / len</c> pair copied out verbatim -- no trim. <see cref="Str"/> is the
    /// trimming form; anything whose leading or trailing whitespace is load-bearing must use this.</summary>
    private static string RawStr(IntPtr owner, int ptrOff, int lenOff, int maxLen)
    {
        byte* p = *(byte**)(owner + ptrOff);
        int len = *(int*)(owner + lenOff);
        if (p == null || len <= 0 || len > maxLen) return "";
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append((char)p[i]);   // Latin1: char == source byte
        return sb.ToString();
    }

    private static readonly delegate* unmanaged<IntPtr, uint, IntPtr, void> _getObjectName =
        (delegate* unmanaged<IntPtr, uint, IntPtr, void>)Native.Sym(SymbolMap.GetObjectName);
    private static readonly delegate* unmanaged<IntPtr, void> _exoDtor =
        (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.ExoStringDtor);

    /// <summary>The engine's display name for any object id -- what a placeable, door or item has instead of
    /// creature stats. <c>GetObjectName</c> writes into a CALLER-owned CExoString that must be zeroed first
    /// (the engine frees any non-null buf) and freed after, or it leaks a heap block per call. COLD.</summary>
    public static string ObjectName(uint id)
    {
        if (_getObjectName == null || _exoDtor == null) return "";
        IntPtr app = ExoApp();
        if (app == IntPtr.Zero) return "";
        byte* exo = stackalloc byte[16];
        for (int i = 0; i < 16; i++) exo[i] = 0;
        _getObjectName(app, id, (IntPtr)exo);
        string name = ReadExo((IntPtr)exo);
        _exoDtor((IntPtr)exo);
        return name.Trim();
    }

    /// <summary>Decode a by-value <c>CExoString</c> {char* buf@0; uint len@8} the way the chat tap does.
    /// COLD -- runs on the message thread inside the bubble-text hook.</summary>
    public static string ReadExo(IntPtr exo)
    {
        if (exo == IntPtr.Zero) return "";
        byte* buf = *(byte**)exo;
        uint len = *(uint*)(exo + 8);
        if (buf == null || len == 0 || len > 4096) return "";
        var sb = new StringBuilder((int)len);
        for (uint i = 0; i < len; i++) sb.Append((char)buf[i]);
        return sb.ToString();
    }

    /// <summary>The object's plate portrait resref via the GetPortrait virtual, at
    /// <see cref="PortraitSizePlate"/>. "" when the object has no portrait. COLD.</summary>
    public static string Portrait(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return "";
        IntPtr vtbl = *(IntPtr*)obj;
        if (vtbl == IntPtr.Zero) return "";
        var getPortrait = (delegate* unmanaged<IntPtr, IntPtr, byte, void>)*(IntPtr*)(vtbl + GetPortraitVtblOff);
        if (getPortrait == null) return "";
        byte* buf = stackalloc byte[ResRefScratch];
        for (int i = 0; i < ResRefScratch; i++) buf[i] = 0;
        // sret: Itanium puts the hidden buffer FIRST, Win64 puts it after `this`.
        if (Win) getPortrait(obj, (IntPtr)buf, PortraitSizePlate); else getPortrait((IntPtr)buf, obj, PortraitSizePlate);
        int n = 0; while (n < 16 && buf[n] != 0) n++;
        return n == 0 ? "" : Encoding.ASCII.GetString(buf, n);
    }

    /// <summary>The vanilla decay for a bubble line: the server's fixed value when it sent one, else the
    /// engine's own <c>clamp(len * perChar * 0.15, 4, 12)</c> (min/max are hard-coded in
    /// <c>CNWCObject::SetPopUpBubbleText</c>, disasm @0x7e8104). Pure -- self-checked.</summary>
    public static float Decay(int textLen, float perChar, float fixedSec)
    {
        if (fixedSec > FixedDecayEpsilon) return fixedSec;
        float t = perChar * textLen * DecayPerChar;
        if (t < DecayMin) return DecayMin;
        return t > DecayMax ? DecayMax : t;
    }

    /// <summary><c>CGuiInGamePopUpBubble::SetText</c> takes the fixed value only when it exceeds this: the
    /// literal at <c>.rodata 0x1313280</c> (<c>9a99193e</c> = 0.15f), compared by
    /// <c>ucomiss 0x1313280,%xmm3; jbe &lt;length-formula&gt;</c> at <c>0x69fdf3</c>. Not <c>&gt; 0</c>.</summary>
    private const float FixedDecayEpsilon = 0.15f;
    private const float DecayPerChar = 0.15f;
    private const float DecayMin = 4f, DecayMax = 12f;

    /// <summary>Chat channels that never bubble (server message, 0x0c, 0x0f). The engine already filters
    /// them before <c>SetPopUpBubbleText</c>; this is here so the rule is testable off-device.</summary>
    public static bool ChannelBubbles(int channel) => channel != 5 && channel != 0x0c && channel != 0x0f;

    /// <summary>Off-device proof of the two pure engine rules this file owns: the decay formula (both
    /// branches and both clamp rails) and the no-bubble channel set.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"NameplateNative.SelfCheck FAIL: {w}"); } }

        E(Decay(10, 1f, 0f) == 4f, "a short line clamps to the 4s floor");
        E(Decay(1000, 1f, 0f) == 12f, "a long line clamps to the 12s ceiling");
        float mid = Decay(40, 1f, 0f);
        E(mid > 5.9f && mid < 6.1f, "40 chars at 1.0/char is 0.15*40 = 6s, inside the clamp");
        E(Decay(40, 0.75f, 0f) < mid, "a smaller per-char scale shortens the line");
        E(Decay(1000, 1f, 30f) == 30f, "a server-sent fixed value bypasses the length formula entirely");

        // The 0 < fixedSec <= 0.15 band: the engine's jbe rejects it, a `fixedSec > 0` rule would have taken it.
        E(Decay(10, 1f, 0.1f) == 4f, "a positive fixed value below 0.15 is NOT a fixed value (jbe @0x69fdf3)");
        E(Decay(10, 1f, 0.15f) == 4f, "exactly 0.15 is not a fixed value either (jbe, not jb)");
        E(Decay(10, 1f, 0.0001f) == 4f, "a hair above zero still falls through to the length formula");
        E(Decay(10, 1f, 0.16f) == 0.16f, "just past 0.15 IS a fixed value, however short");
        E(Decay(1000, 1f, -1f) == 12f, "a negative fixed value falls through to the formula");

        E(!ChannelBubbles(5) && !ChannelBubbles(0x0c) && !ChannelBubbles(0x0f), "channels 5, 0x0c and 0x0f never bubble");
        E(ChannelBubbles(1) && ChannelBubbles(3) && ChannelBubbles(4) && ChannelBubbles(0x0e), "every other handled channel bubbles");

        log.Info(ok ? "NameplateNative.SelfCheck: passed" : "NameplateNative.SelfCheck: FAILED");
    }
}
