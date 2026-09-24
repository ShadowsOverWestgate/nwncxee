using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Nameplate;
using CX.Ui;

namespace CX.Services.Radial;

/// <summary>What a <c>CGuiIcon</c> is at runtime, by vptr (docs/research/radial-menu-reimplementation.md §5).
/// <see cref="Plain"/> is <c>CGuiIcon</c> itself and every subclass not listed (<c>CSpellIcon</c>): one texture
/// by resref. The other three are what the item root's centre icon can be.</summary>
public enum IconKind { Plain, Layered, Composite, Armor }

/// <summary>
/// The item root's composite icon (CX-693): the engine draws an item's <c>CGuiIcon</c> as a model of one to six
/// stacked textured quads (<c>GUI_ICON_%dX%dL</c> / <c>..LC</c> / <c>GUI_ICON_ARMR%dL</c>, each layer a child of the
/// previous at +0.01 toward the viewer), so this draws the same layers, bottom to top, as
/// <see cref="TextRig.DrawIconTid"/> quads. A plain layer's texture is the resref's (<c>AurTextureGet</c>, as
/// <see cref="TextRig.DrawIcon"/>); a PLT layer's is the one <c>Material::ReplaceTexturePLT</c> @0x4c7a10 keyed under
/// (resref, palette scheme), found again per frame by the engine's own <c>AurTextureFind</c> hash -- never created
/// here, never cached (a <c>CAurTexture*</c> is borrowed, <see cref="AurTex"/>).
/// <para>The handle the dropdown carries is the item's OBJECT ID, not its <c>CGuiIcon*</c>: <c>CNWCItem::UpdateIcons</c>
/// @0x7cbc50 deletes and rebuilds that icon on every item update the server sends, which can land while the menu
/// is open, so <see cref="Draw"/> re-resolves id -&gt; <c>CNWCItem</c> -&gt; <see cref="EngineLayout.ItemGuiIcon"/> every
/// frame and an item that is gone (picked up, destroyed) simply draws nothing. The icon's textures live until the
/// area edge, where the menu closes anyway; a texture the engine has not built yet (tid 0 until the next
/// <c>Scene::Render</c>'s <c>AurTextureBuildAndStoreAll</c>) is skipped silently and appears a frame later.</para>
/// <para>Hot path (<see cref="Draw"/>): no allocation -- names are marshalled on the stack, schemes widened on the
/// stack. The one exception is a single one-shot log line per open under <c>logs=1</c>.</para>
/// </summary>
internal static unsafe class RadialIcons
{
    /// <summary>The armour model's ceiling, and the widest scheme the engine keeps (<c>CAurTextureBasic::SetScheme</c>
    /// @0x485f90 holds 15 slots; items pass 10). A scheme longer than that is treated as no layers, not truncated.</summary>
    public const int MaxLayers = EngineLayout.ArmorIconLayers;
    private const int MaxScheme = 15;
    private const int ResRefLen = 16;
    /// <summary>CGameObject type byte for an item (<c>CClientExoAppInternal::GetItemByGameObjectID</c> @0x63f3a0).</summary>
    private const byte GameObjTypeItem = 6;

    private static delegate* unmanaged<byte*, byte*, int*, int, byte, IntPtr> _find;
    private static delegate* unmanaged<byte*, IntPtr, byte*, int*, int, byte, IntPtr> _get;
    private static delegate* unmanaged<IntPtr, uint, IntPtr> _getGameObject;
    private static delegate* unmanaged<IntPtr, uint, IntPtr, void> _getObjectName;
    private static delegate* unmanaged<IntPtr, void> _exoDtor;
    private static IntPtr _appMgr, _vtLayered, _vtComposite, _vtArmor;
    private static bool _ready, _initTried, _firstDrawLogged;
    private static ExtenderLog? _log;

    /// <summary>Itanium vptr = <c>_ZTV</c> + 0x10; MSVC vptr = the <c>vftable</c> label itself.</summary>
    private static readonly int VtableToVptr = OperatingSystem.IsWindows() ? 0 : 0x10;

    /// <summary>Resolve once; false stands the composite icon down (the dropdown then shows no header icon), and the
    /// stand-down line prints once, not per right-click. Called lazily by <see cref="HeaderItem"/> so the owner needs
    /// no init call.</summary>
    public static bool Init(ExtenderLog log)
    {
        if (_initTried) return _ready;
        _initTried = true;
        _log = log;
        if (!SymbolMap.Require(log, "RadialIcons", SymbolMap.AurTextureFind, SymbolMap.AurTextureGet, SymbolMap.GetGameObject,
                               SymbolMap.AppManager, SymbolMap.VtCLayeredIcon, SymbolMap.VtCCompositeIcon, SymbolMap.VtCArmorIcon)) return false;
        if (!EngineLayout.Require(log, "RadialIcons", "CGuiIcon", "CLayeredIcon", "CCompositeIcon", "CArmorIcon", "CNWCItem", "CGameObject")) return false;
        _find = (delegate* unmanaged<byte*, byte*, int*, int, byte, IntPtr>)SymbolMap.Resolve(SymbolMap.AurTextureFind, log);
        _get = (delegate* unmanaged<byte*, IntPtr, byte*, int*, int, byte, IntPtr>)SymbolMap.Resolve(SymbolMap.AurTextureGet, log);
        _getGameObject = (delegate* unmanaged<IntPtr, uint, IntPtr>)SymbolMap.Resolve(SymbolMap.GetGameObject, log);
        _getObjectName = (delegate* unmanaged<IntPtr, uint, IntPtr, void>)SymbolMap.Resolve(SymbolMap.GetObjectName, log);
        _exoDtor = (delegate* unmanaged<IntPtr, void>)SymbolMap.Resolve(SymbolMap.ExoStringDtor, log);
        _appMgr = SymbolMap.Resolve(SymbolMap.AppManager, log);
        _vtLayered = SymbolMap.Resolve(SymbolMap.VtCLayeredIcon, log) + VtableToVptr;
        _vtComposite = SymbolMap.Resolve(SymbolMap.VtCCompositeIcon, log) + VtableToVptr;
        _vtArmor = SymbolMap.Resolve(SymbolMap.VtCArmorIcon, log) + VtableToVptr;
        _ready = _find != null && _get != null && _getGameObject != null && _appMgr != IntPtr.Zero;
        return _ready;
    }

    public static IconKind Classify(IntPtr icon)
    {
        if (icon == IntPtr.Zero) return IconKind.Plain;
        IntPtr vptr = *(IntPtr*)icon;
        if (vptr == _vtLayered) return IconKind.Layered;
        if (vptr == _vtComposite) return IconKind.Composite;
        if (vptr == _vtArmor) return IconKind.Armor;
        return IconKind.Plain;
    }

    /// <summary>The dropdown's header row for an item root: the item's name and its own icon by handle, or
    /// <c>default</c> (null label) when the root is not an item, the item is gone, or the unit is stood down. Cold
    /// (once per open); logs the <c>[radial] icon</c> line under <c>logs=1</c>. The owner inserts it at row 0 of the
    /// item root and treats <c>Activate</c> on it as a no-op.</summary>
    public static CascadeItem HeaderItem(IntPtr menu, RootKind kind, ExtenderLog log)
    {
        if (kind != RootKind.Item || !Init(log)) return default;
        uint id = RadialTree.TargetId(menu, kind);
        IntPtr icon = ItemIcon((IntPtr)id);
        if (icon == IntPtr.Zero) return default;
        _firstDrawLogged = false;
        if (ExtenderLog.Enabled)
        {
            Span<int> tids = stackalloc int[MaxLayers];
            int n = Layers(icon, tids);
            log.Info($"[radial] icon slot=c kind={Classify(icon)} tid={(n > 0 ? tids[0] : 0)} layers={n} icon=0x{(long)icon:x} (0 = built on the next frame)");
        }
        return new CascadeItem(ObjectName(id)) { IconHandle = (IntPtr)id };
    }

    /// <summary>Per frame, from the owner's icon pass: every layer of the item's icon as it stands now, bottom to top.
    /// Zero-alloc; a vanished item, a stood-down unit or a not-yet-built texture draws nothing.</summary>
    public static void Draw(IntPtr handle, float x, float y, float size, float alpha)
    {
        if (!_ready || handle == IntPtr.Zero || !AurTex.Ready) return;
        IntPtr icon = ItemIcon(handle);
        if (icon == IntPtr.Zero) return;
        Span<int> tids = stackalloc int[MaxLayers];
        int n = Layers(icon, tids);
        for (int i = 0; i < n; i++) TextRig.DrawIconTid(tids[i], x, y, size, size, alpha);
        if (!_firstDrawLogged && n > 0 && tids[0] != 0 && ExtenderLog.Enabled)
        {
            _firstDrawLogged = true;
            _log?.Info($"[radial] icon first draw kind={Classify(icon)} layers={n} tids={tids[0]},{(n > 1 ? tids[1] : 0)},{(n > 2 ? tids[2] : 0)}");
        }
    }

    /// <summary>The icon's layers as live GL texture ids, bottom to top; returns the layer count (an existing layer
    /// whose texture is not built yet reports 0 and still counts). Plain: the resref. Layered: the resref as a PLT
    /// under the icon's scheme. Composite: three plain resrefs. Armour: up to six PLT resrefs, an empty slot
    /// skipped; the per-layer scheme table, when present, is indexed by the compacted layer exactly as
    /// <c>CArmorIcon::LoadArmorLayeredIconModel</c> @0x58d7c0 indexes it -- and since the engine only made a texture
    /// for a layer that exists as a PLT, a find miss IS its skip, so the compacted index advances on a hit only.</summary>
    internal static int Layers(IntPtr icon, Span<int> tids)
    {
        byte* p = (byte*)icon;
        switch (Classify(icon))
        {
            case IconKind.Layered:
            {
                var layered = new CLayeredIcon(icon);
                tids[0] = TidPlt(p + EngineLayout.GuiIconResRef, (ushort*)layered.Scheme, layered.SchemeCount);
                return 1;
            }
            case IconKind.Composite:
                tids[0] = TidPlain(p + EngineLayout.GuiIconResRef);
                tids[1] = TidPlain(p + EngineLayout.CompositeIconResRef2);
                tids[2] = TidPlain(p + EngineLayout.CompositeIconResRef3);
                return 3;
            case IconKind.Armor:
            {
                var armor = new CArmorIcon(icon);
                int count = armor.SchemeCount;
                ushort* shared = (ushort*)armor.Scheme;
                ushort* perLayer = (ushort*)armor.LayerSchemes;
                int n = 0;
                for (int slot = 0; slot < EngineLayout.ArmorIconLayers; slot++)
                {
                    byte* name = p + EngineLayout.ArmorIconResRefs + slot * EngineLayout.ArmorIconResRefStride;
                    if (name[0] == 0) continue;
                    ushort* scheme = perLayer != null ? perLayer + n * count : shared;
                    IntPtr tex = FindPlt(name, scheme, count);
                    if (tex == IntPtr.Zero) continue;
                    tids[n++] = AurTex.Tid(tex);
                }
                return n;
            }
            default:
                tids[0] = TidPlain(p + EngineLayout.GuiIconResRef);
                return 1;
        }
    }

    /// <summary>Handle -&gt; the item's current <c>CGuiIcon*</c>: <c>GetGameObject</c>, the type byte, the
    /// <c>AsNWCItem</c> virtual, the field. Every step re-read; nothing held.</summary>
    private static IntPtr ItemIcon(IntPtr handle)
    {
        IntPtr mgr = *(IntPtr*)_appMgr;
        if (mgr == IntPtr.Zero) return IntPtr.Zero;
        IntPtr app = *(IntPtr*)mgr;
        if (app == IntPtr.Zero) return IntPtr.Zero;
        var obj = new CGameObject(_getGameObject(app, (uint)(long)handle));
        if (obj.IsNull || obj.Type != GameObjTypeItem) return IntPtr.Zero;
        IntPtr item = ((delegate* unmanaged<IntPtr, IntPtr>)obj.AsNWCItemFn)(obj.Ptr);
        return item == IntPtr.Zero ? IntPtr.Zero : new CNWCItem(item).GuiIcon;
    }

    /// <summary>A plain layer: <c>AurTextureGet(name, 0, 0, 0, 0, false)</c>, the resman's find-or-create by resref
    /// (what the engine's own <c>ReplaceTexture</c> and <see cref="TextRig.ResolveTid"/> do).</summary>
    private static int TidPlain(byte* resref)
    {
        byte* name = stackalloc byte[ResRefLen + 1];
        if (CName(resref, name) == 0) return 0;
        return AurTex.Tid(_get(name, IntPtr.Zero, null, null, 0, 0));
    }

    private static int TidPlt(byte* resref, ushort* scheme, int count) => AurTex.Tid(FindPlt(resref, scheme, count));

    /// <summary>A PLT layer: <c>AurTextureFind(name, name, scheme, count, false)</c> -- the hashed (pltName, scheme)
    /// lookup, scheme widened <c>ushort -&gt; int</c> as <c>Gob::ReplaceTexture</c> @0x43eb30 widens it. Find, never get:
    /// a get would mint a scheme-less duplicate every frame.</summary>
    private static IntPtr FindPlt(byte* resref, ushort* scheme, int count)
    {
        if (scheme == null || (uint)count > MaxScheme) return IntPtr.Zero;
        byte* name = stackalloc byte[ResRefLen + 1];
        if (CName(resref, name) == 0) return IntPtr.Zero;
        int* ints = stackalloc int[MaxScheme];
        for (int i = 0; i < count; i++) ints[i] = scheme[i];
        return _find(name, name, ints, count, 0);
    }

    /// <summary>Copy a 16-byte <c>CResRef</c> into a NUL-terminated stack buffer; returns its length.</summary>
    private static int CName(byte* resref, byte* dst)
    {
        int n = 0;
        while (n < ResRefLen && resref[n] != 0) { dst[n] = resref[n]; n++; }
        dst[n] = 0;
        return n;
    }

    /// <summary>The item's display name through <c>GetObjectName</c> (cold; the DialogService contract: zeroed
    /// CExoString in, its buffer freed by the CExoString dtor after the copy).</summary>
    private static string ObjectName(uint id)
    {
        if (_getObjectName == null || _exoDtor == null) return "";
        IntPtr mgr = *(IntPtr*)_appMgr; if (mgr == IntPtr.Zero) return "";
        IntPtr app = *(IntPtr*)mgr; if (app == IntPtr.Zero) return "";
        byte* exo = stackalloc byte[16];
        for (int i = 0; i < 16; i++) exo[i] = 0;
        _getObjectName(app, id, (IntPtr)exo);
        string name = Native.ReadExo((IntPtr)exo);
        _exoDtor((IntPtr)exo);
        return name.Trim();
    }

    // ---- self-check ----------------------------------------------------------------------------

    private static int _fakeFinds, _fakeGets, _fakeLastCount;
    private static readonly int[] _fakeLastScheme = new int[MaxScheme];
    private static readonly byte[] _fakeLastName = new byte[ResRefLen + 1];
    private static readonly byte[] _fakeMissName = new byte[ResRefLen + 1];
    private static IntPtr _fakeTex;

    private static bool SameName(byte* a, byte[] b) { for (int i = 0; i <= ResRefLen; i++) { if (a[i] != b[i]) return false; if (a[i] == 0) return true; } return true; }

    [UnmanagedCallersOnly]
    private static IntPtr FakeFind(byte* name, byte* plt, int* scheme, int count, byte byName)
    {
        _fakeFinds++;
        for (int i = 0; i <= ResRefLen; i++) { _fakeLastName[i] = name[i]; if (name[i] == 0) break; }
        _fakeLastCount = count;
        for (int i = 0; i < count && i < MaxScheme; i++) _fakeLastScheme[i] = scheme[i];
        return byName != 0 || plt != name || SameName(name, _fakeMissName) ? IntPtr.Zero : _fakeTex;
    }

    [UnmanagedCallersOnly]
    private static IntPtr FakeGet(byte* name, IntPtr tex, byte* plt, int* scheme, int count, byte byName)
    {
        _fakeGets++;
        for (int i = 0; i <= ResRefLen; i++) { _fakeLastName[i] = name[i]; if (name[i] == 0) break; }
        return plt == null && scheme == null && count == 0 ? _fakeTex : IntPtr.Zero;
    }

    private static void Put(byte* dst, string s) { for (int i = 0; i < s.Length; i++) dst[i] = (byte)s[i]; dst[s.Length] = 0; }
    private static string LastName() { int n = 0; while (n < ResRefLen && _fakeLastName[n] != 0) n++; return System.Text.Encoding.ASCII.GetString(_fakeLastName, 0, n); }

    /// <summary>Off-device: the vptr classification and the layer walk over fake icon blobs laid out per the resolved
    /// <see cref="EngineLayout"/> rows, with the two resman calls swapped for managed fakes that record (name,
    /// scheme) -- plain = one get; layered = one hashed find with the widened scheme; composite = three gets in
    /// 01/02/03 order; armour = an empty slot skipped and a missing PLT (find miss) NOT advancing the per-layer
    /// scheme index. The GL draw and the item walk are engine calls with no fake -- live-unverified. Skipped when
    /// <see cref="AurTex"/> is already armed: every fake hit would then be a counted vtable refusal, and that
    /// counter is the maintainer's stale-holder signal on the <c>[owned]</c> line.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        if (AurTex.Ready) { log.Info("RadialIcons.SelfCheck: skipped (AurTex armed -- the fake walk would count refusals)"); return; }
        var check = new Checker(log);

        var savedFind = _find; var savedGet = _get; bool savedReady = _ready;
        IntPtr sl = _vtLayered, sc = _vtComposite, sa = _vtArmor;
        byte* vt = (byte*)NativeMemory.AllocZeroed(0x100);
        byte* icon = (byte*)NativeMemory.AllocZeroed(0x100);
        ushort* scheme = (ushort*)NativeMemory.AllocZeroed(2 * MaxScheme * MaxLayers);
        ushort* perLayer = (ushort*)NativeMemory.AllocZeroed(2 * MaxScheme * MaxLayers);
        Span<int> tids = stackalloc int[MaxLayers];
        try
        {
            _find = &FakeFind; _get = &FakeGet; _ready = true;
            _fakeTex = (IntPtr)vt;   // any non-null: AurTex is unarmed headless, so Tid() answers 0 and only the walk is checked
            _vtLayered = (IntPtr)(vt + 0x10); _vtComposite = (IntPtr)(vt + 0x20); _vtArmor = (IntPtr)(vt + 0x30);
            fixed (byte* miss = _fakeMissName) Put(miss, "no_such_plt");

            *(IntPtr*)icon = (IntPtr)(vt + 0x40);
            Put(icon + EngineLayout.GuiIconResRef, "isk_pocket");
            check.Expect(Classify((IntPtr)icon) == IconKind.Plain, "an unlisted vptr is Plain");
            _fakeGets = _fakeFinds = 0;
            check.Expect(Layers((IntPtr)icon, tids) == 1 && _fakeGets == 1 && _fakeFinds == 0 && LastName() == "isk_pocket", $"plain = one get by resref ({LastName()})");

            *(IntPtr*)icon = _vtLayered;
            for (int i = 0; i < 10; i++) scheme[i] = (ushort)(i * 17);
            var layered = new CLayeredIcon((IntPtr)icon);
            layered.SchemeCount = 10;
            layered.Scheme = (IntPtr)scheme;
            _fakeGets = _fakeFinds = 0;
            check.Expect(Classify((IntPtr)icon) == IconKind.Layered && Layers((IntPtr)icon, tids) == 1 && _fakeFinds == 1 && _fakeGets == 0
              && _fakeLastCount == 10 && _fakeLastScheme[9] == 153 && LastName() == "isk_pocket", "layered = one hashed find, scheme widened");
            layered.SchemeCount = MaxScheme + 1;
            _fakeFinds = 0;
            check.Expect(Layers((IntPtr)icon, tids) == 1 && _fakeFinds == 0 && tids[0] == 0, "an over-long scheme is refused, not truncated");

            *(IntPtr*)icon = _vtComposite;
            Put(icon + EngineLayout.CompositeIconResRef2, "iwaxe_m_1");
            Put(icon + EngineLayout.CompositeIconResRef3, "iwaxe_t_1");
            _fakeGets = _fakeFinds = 0;
            check.Expect(Classify((IntPtr)icon) == IconKind.Composite && Layers((IntPtr)icon, tids) == 3 && _fakeGets == 3 && _fakeFinds == 0
              && LastName() == "iwaxe_t_1", "composite = three gets, 01 -> 03");

            NativeMemory.Clear(icon + 8, 0xf8);
            *(IntPtr*)icon = _vtArmor;
            Put(icon + EngineLayout.ArmorIconResRefs + 0 * EngineLayout.ArmorIconResRefStride, "arm_a");
            Put(icon + EngineLayout.ArmorIconResRefs + 2 * EngineLayout.ArmorIconResRefStride, "no_such_plt");
            Put(icon + EngineLayout.ArmorIconResRefs + 3 * EngineLayout.ArmorIconResRefStride, "arm_d");
            var armor = new CArmorIcon((IntPtr)icon);
            armor.SchemeCount = 10;
            armor.Scheme = (IntPtr)scheme;
            for (int i = 0; i < 30; i++) perLayer[i] = (ushort)(100 + i);
            armor.LayerSchemes = (IntPtr)perLayer;
            _fakeGets = _fakeFinds = 0;
            int n = Layers((IntPtr)icon, tids);
            check.Expect(Classify((IntPtr)icon) == IconKind.Armor && n == 2 && _fakeFinds == 3 && _fakeGets == 0,
              $"armour: empty slot skipped, three finds, two layers ({n}, finds={_fakeFinds})");
            check.Expect(LastName() == "arm_d" && _fakeLastScheme[0] == 110, $"the miss did not advance the per-layer scheme index (row {_fakeLastScheme[0] - 100})");
            armor.LayerSchemes = IntPtr.Zero;
            Layers((IntPtr)icon, tids);
            check.Expect(_fakeLastScheme[9] == 153, "no per-layer table: every layer takes the shared scheme");
        }
        finally
        {
            _find = savedFind; _get = savedGet; _ready = savedReady;
            _vtLayered = sl; _vtComposite = sc; _vtArmor = sa; _fakeTex = IntPtr.Zero;
            NativeMemory.Free(vt); NativeMemory.Free(icon); NativeMemory.Free(scheme); NativeMemory.Free(perLayer);
        }
        log.Info(check.Ok ? "RadialIcons.SelfCheck: passed" : "RadialIcons.SelfCheck: FAILED");
    }
}
