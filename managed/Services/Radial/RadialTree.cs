using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Radial;

/// <summary>Which root built the engine tree, by vptr (docs/research/radial-menu-reimplementation.md §3).
/// <see cref="Plain"/> is a bare <c>CRadialMenu</c> -- the tile/ground root and every creator-made child.</summary>
public enum RootKind { Unknown, Plain, Creature, Spell, RadialSpell, Trap, Item, Placeable, Door, Quickbar, ChatWindow }

/// <summary>What a slot holds: nothing, a leaf (entry callback), or a submenu (creator callback / child).</summary>
public enum SlotKind { Empty, Entry, Creator }

/// <summary>One of the eight slots of an engine <c>CRadialMenu</c>, read cold. <see cref="Obj"/>/<see cref="Fn"/>/
/// <see cref="Adj"/> are the entry callback's member-fn triple (Adj is 0 on Windows, whose pairs have no adjust
/// word); <see cref="Child"/> is <c>m_pSubMenu[i]</c>, null until materialized; <see cref="Count"/> is the icon's
/// uses-left label ("" when the creator set none).</summary>
public readonly record struct RadialSlot(int Index, SlotKind Kind, string Label, string Icon, string Count, bool Disabled, long Param,
    IntPtr Obj, IntPtr Fn, IntPtr Adj, IntPtr Child)
{
    /// <summary>A slot the user can descend into: it has a creator, or a child already exists.</summary>
    public bool IsSubMenu => Kind == SlotKind.Creator || Child != IntPtr.Zero;
}

/// <summary>
/// Static reader/driver over an engine <c>CRadialMenu*</c> (CX-689): classify the root, read its eight slots,
/// materialize a child through the engine's own <c>CreateSubMenu</c>, invoke a leaf with the platform's call
/// shape, tear the tree down. GL-free and allocation-tolerant (every caller is a cold right-click path). Offsets
/// come from <see cref="EngineLayout"/>; the label read-back walks <c>m_pText[i]</c> (a <c>CGuiTextBubble</c>) to
/// its <c>CAurString*</c> (ctor @0x588690 stores <c>NewCAurString(-1)</c> at +0xf8; the Windows ctor @rva 0x1fd310
/// at +0xf0), which is the <c>StringGob</c> sub-object at +0x4c0 on both builds, and reads the wrapped lines back
/// through the exported <c>StringGob::GetTextLineCount/GetTextLine</c> with the <c>this</c> each build's export
/// wants (<see cref="EngineLayout.StringGobReaderAdjust"/>: the object base on Linux, the sub-object on MSVC). A
/// <c>CGuiTextBubble::SetText</c> tap was the alternative; read-back needs no hook and no per-bubble bookkeeping.
/// </summary>
internal static unsafe class RadialTree
{
    public const int SlotCount = 8;
    private const int ResRefLen = 16;
    private const int MaxLabelLines = 16;

    private static readonly IntPtr[] _vptr = new IntPtr[(int)RootKind.ChatWindow + 1];
    private static delegate* unmanaged<IntPtr, int, int> _createSubMenu;
    private static delegate* unmanaged<IntPtr, int> _recursiveAutoDelete;
    private static delegate* unmanaged<IntPtr, int> _getTextLineCount;
    private static delegate* unmanaged<IntPtr, int, IntPtr> _getTextLine;
    private static IntPtr* _eventObject;
    private static int* _ctorCounter;
    private static int* _menuDepth;

    /// <summary>Itanium vptr = <c>_ZTV</c> + 0x10 (two words: offset-to-top, typeinfo); MSVC vptr = the <c>vftable</c>
    /// label itself.</summary>
    private static readonly int VtableToVptr = OperatingSystem.IsWindows() ? 0 : 0x10;

    /// <summary>Resolve every symbol once. False when a required one is missing (the caller has already gated on
    /// <see cref="SymbolMap.Require"/>, so this is belt and braces).</summary>
    public static bool Init(ExtenderLog log)
    {
        _createSubMenu = (delegate* unmanaged<IntPtr, int, int>)SymbolMap.Resolve(SymbolMap.RadialCreateSubMenu, log);
        _recursiveAutoDelete = (delegate* unmanaged<IntPtr, int>)SymbolMap.Resolve(SymbolMap.RadialRecursiveAutoDelete, log);
        _getTextLineCount = (delegate* unmanaged<IntPtr, int>)SymbolMap.Resolve(SymbolMap.StringGobGetTextLineCount, log);
        _getTextLine = (delegate* unmanaged<IntPtr, int, IntPtr>)SymbolMap.Resolve(SymbolMap.StringGobGetTextLine, log);
        _eventObject = (IntPtr*)SymbolMap.Resolve(SymbolMap.GuiCurrentEventObject, log);
        _menuDepth = (int*)SymbolMap.Resolve(SymbolMap.RadialMenuDepth, log);
        _ctorCounter = (int*)SymbolMap.Resolve(SymbolMap.RadialCtorCounter, log);
        Vt(RootKind.Plain, SymbolMap.VtCRadialMenu, log);
        Vt(RootKind.Creature, SymbolMap.VtCGuiCreatureMenu, log);
        Vt(RootKind.Spell, SymbolMap.VtCClientSpellMenu, log);
        Vt(RootKind.RadialSpell, SymbolMap.VtCRadialSpellMenu, log);
        Vt(RootKind.Trap, SymbolMap.VtCClientTrapMenu, log);
        Vt(RootKind.Item, SymbolMap.VtCGuiItemMenu, log);
        Vt(RootKind.Placeable, SymbolMap.VtCGuiPlaceableMenu, log);
        Vt(RootKind.Door, SymbolMap.VtCGuiDoorMenu, log);
        Vt(RootKind.Quickbar, SymbolMap.VtCGuiQuickbarMenu, log);
        Vt(RootKind.ChatWindow, SymbolMap.VtCGuiChatWindowMenu, log);
        return _createSubMenu != null && _recursiveAutoDelete != null && _getTextLineCount != null
            && _getTextLine != null && _eventObject != null && _menuDepth != null;
    }

    private static void Vt(RootKind kind, string symbol, ExtenderLog log)
    {
        IntPtr vt = SymbolMap.Resolve(symbol, log);
        _vptr[(int)kind] = vt == IntPtr.Zero ? IntPtr.Zero : vt + VtableToVptr;
    }

    /// <summary><c>CRadialMenu::static_nMenuDepth</c>: non-zero while an ENGINE radial is active (never under the
    /// CX scheme, whose roots skip ActivateInternal). -1 when unresolved.</summary>
    public static int MenuDepth => _menuDepth == null ? -1 : *_menuDepth;

    /// <summary><c>g_nCRadialMenu_ctorCounter</c>: monotonic (the ctor @0x583460 is its only writer -- no dtor
    /// decrements it), so a delta between two reads counts constructions, never live instances.</summary>
    public static int CtorCounter => _ctorCounter == null ? -1 : *_ctorCounter;

    /// <summary><c>CGuiObject::s_pCurrentEventObject</c> as it stands now.</summary>
    public static IntPtr EventObject => _eventObject == null ? IntPtr.Zero : *_eventObject;

    public static RootKind Classify(IntPtr menu)
    {
        if (menu == IntPtr.Zero) return RootKind.Unknown;
        IntPtr vptr = *(IntPtr*)menu;
        for (int k = 1; k < _vptr.Length; k++)
            if (_vptr[k] != IntPtr.Zero && _vptr[k] == vptr) return (RootKind)k;
        return RootKind.Unknown;
    }

    /// <summary>Menu position (<c>Activate</c> copies the mouse there; the keybind opener centres it first).</summary>
    public static (int X, int Y) Position(IntPtr menu) =>
        (*(int*)(menu + EngineLayout.RadialMenuX), *(int*)(menu + EngineLayout.RadialMenuY));

    /// <summary>The root's target object id where the kind carries one (creature/item/placeable/door at
    /// <see cref="EngineLayout.RadialTargetId"/>, the cast menu at <see cref="EngineLayout.SpellMenuTargetId"/>);
    /// 0 for the rest.</summary>
    public static uint TargetId(IntPtr menu, RootKind kind) => kind switch
    {
        RootKind.Creature or RootKind.Item or RootKind.Placeable or RootKind.Door => *(uint*)(menu + EngineLayout.RadialTargetId),
        RootKind.Spell => *(uint*)(menu + EngineLayout.SpellMenuTargetId),
        _ => 0,
    };

    public static RadialSlot ReadSlot(IntPtr menu, int i)
    {
        int stride = EngineLayout.RadialCallbackStride;
        IntPtr entry = menu + EngineLayout.RadialEntry + i * stride;
        IntPtr creator = menu + EngineLayout.RadialCreator + i * stride;
        bool hasEntry = *(IntPtr*)entry != entry;
        bool hasCreator = *(IntPtr*)creator != creator;
        IntPtr child = *(IntPtr*)(menu + EngineLayout.RadialSubMenu + i * 8);
        SlotKind kind = hasCreator || child != IntPtr.Zero ? SlotKind.Creator : hasEntry ? SlotKind.Entry : SlotKind.Empty;
        IntPtr obj = hasEntry ? *(IntPtr*)entry : IntPtr.Zero;
        IntPtr fn = hasEntry ? *(IntPtr*)(entry + 8) : IntPtr.Zero;
        IntPtr adj = hasEntry && !OperatingSystem.IsWindows() ? *(IntPtr*)(entry + 16) : IntPtr.Zero;
        IntPtr icon = *(IntPtr*)(menu + EngineLayout.RadialIcon + i * 8);
        return new RadialSlot(i, kind,
            ReadLabel(*(IntPtr*)(menu + EngineLayout.RadialText + i * 8)),
            ReadIcon(icon),
            icon == IntPtr.Zero ? "" : ReadAur(*(IntPtr*)(icon + EngineLayout.GuiIconCountLabel)),
            (*(uint*)(menu + EngineLayout.RadialFlags + i * 4) & 1) != 0,
            *(long*)(menu + EngineLayout.RadialParam + i * 8),
            obj, fn, adj, child);
    }

    private static string ReadLabel(IntPtr bubble) =>
        bubble == IntPtr.Zero ? "" : ReadAur(*(IntPtr*)(bubble + EngineLayout.TextBubbleString));

    /// <summary>Rejoin a <c>CAurString</c>'s wrapped lines (the bubble label and the icon's count label are both
    /// <c>NewCAurString</c> gobs): <c>StringGob::WrapLines</c> @0x494d80 keeps the break space (and any newline) on
    /// the first piece, so plain concatenation restores the text SetText was given.</summary>
    private static string ReadAur(IntPtr aur)
    {
        if (aur == IntPtr.Zero || _getTextLineCount == null || _getTextLine == null) return "";
        IntPtr gob = aur - EngineLayout.StringGobReaderAdjust;
        int n = _getTextLineCount(gob);
        if (n <= 0) return "";
        if (n > MaxLabelLines) n = MaxLabelLines;
        var sb = new StringBuilder();
        for (int k = 0; k < n; k++) AppendCStr(sb, (byte*)_getTextLine(gob, k));
        return sb.ToString().Trim();
    }

    private static void AppendCStr(StringBuilder sb, byte* p)
    {
        if (p == null) return;
        for (int k = 0; k < 4096 && p[k] != 0; k++) sb.Append((char)p[k]);
    }

    private static string ReadIcon(IntPtr icon)
    {
        if (icon == IntPtr.Zero) return "";
        byte* p = (byte*)(icon + EngineLayout.GuiIconResRef);
        int n = 0;
        while (n < ResRefLen && p[n] != 0) n++;
        if (n == 0) return "";
        var sb = new StringBuilder(n);
        for (int k = 0; k < n; k++) sb.Append((char)p[k]);
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary><c>CreateSubMenu(i)</c> through the engine: it brackets <c>s_pCurrentEventObject</c> itself. Returns the
    /// child (the same one on a repeat call), or null when the slot has no creator or the creator declined. Links the
    /// child's <c>m_pParent</c> as <c>HandleMenuAction</c> @0x585530 does on a descent, so the tree reads as vanilla's.</summary>
    public static IntPtr Materialize(IntPtr menu, int i)
    {
        if (_createSubMenu == null || (uint)i >= SlotCount) return IntPtr.Zero;
        _createSubMenu(menu, i);
        IntPtr child = *(IntPtr*)(menu + EngineLayout.RadialSubMenu + i * 8);
        if (child != IntPtr.Zero) *(IntPtr*)(child + EngineLayout.RadialParent) = menu;
        return child;
    }

    /// <summary>The cascade rows of one engine menu (CX-690): vanilla slot order with the empty slots left out, the
    /// icon resref for <c>TextRig.DrawIcon</c>, disabled + uses-left as the creator set them at this open, a submenu
    /// mark on creator slots, and the slot index as the row's tag (boxed once, cold). Reads fresh on every call --
    /// nothing is cached across opens (research §9.7).</summary>
    public static CascadeItem[] BuildItems(IntPtr menu)
    {
        var slots = new RadialSlot[SlotCount];
        int n = 0;
        for (int i = 0; i < SlotCount; i++)
        {
            RadialSlot s = ReadSlot(menu, i);
            if (s.Kind != SlotKind.Empty) slots[n++] = s;
        }
        var items = new CascadeItem[n];
        for (int k = 0; k < n; k++)
            items[k] = new CascadeItem(slots[k].Label, slots[k].Icon, slots[k].Disabled, slots[k].IsSubMenu, slots[k].Count, slots[k].Index);
        return items;
    }

    /// <summary>The leaf call exactly as <c>HandleMenuAction</c> @0x585530 makes it, under the
    /// <c>s_pCurrentEventObject = menu</c> bracket. Itanium: <c>this = obj + adj</c>, an odd <c>fn</c> is a vtable byte
    /// offset + 1 (<c>fn = *(vtable(this) + fn - 1)</c>); Win64 (@rva 0x1f9350): <c>fn(obj, param)</c>, no adjust, no
    /// thunk decode. False when the slot's entry is the unset sentinel (vanilla would call the no-op
    /// <c>HandleUninitializedCallback</c>).
    /// Windows: the call shape is transcribed from the decompiled body, self-checked here, not yet run under Proton.</summary>
    public static bool Invoke(IntPtr menu, int i)
    {
        if (_eventObject == null || (uint)i >= SlotCount) return false;
        IntPtr entry = menu + EngineLayout.RadialEntry + i * EngineLayout.RadialCallbackStride;
        IntPtr obj = *(IntPtr*)entry;
        if (obj == entry) return false;
        IntPtr fn = *(IntPtr*)(entry + 8);
        long param = *(long*)(menu + EngineLayout.RadialParam + i * 8);
        IntPtr self = obj;
        if (!OperatingSystem.IsWindows())
        {
            self = obj + *(IntPtr*)(entry + 16);
            if (((long)fn & 1) != 0) fn = *(IntPtr*)((long)*(IntPtr*)self + (long)fn - 1);
        }
        IntPtr saved = *_eventObject;
        *_eventObject = menu;
        ((delegate* unmanaged<IntPtr, long, void>)fn)(self, param);
        *_eventObject = saved;
        return true;
    }

    /// <summary>A handler invoked on an object CX built (a self stand-in, CX-694): <c>fn(obj, param)</c> under the
    /// event-object bracket, non-virtual on both platforms (the table names plain member functions).</summary>
    public static bool InvokeOn(IntPtr obj, IntPtr fn, long param)
    {
        if (_eventObject == null || obj == IntPtr.Zero || fn == IntPtr.Zero) return false;
        IntPtr saved = *_eventObject;
        *_eventObject = obj;
        ((delegate* unmanaged<IntPtr, long, void>)fn)(obj, param);
        *_eventObject = saved;
        return true;
    }

    /// <summary><c>RecursiveAutoDelete()</c> on the root: every materialized child, then the root itself (all roots
    /// set m_bAutoDelete). Returns the ctor counter before and after -- monotonic, so the pair only documents
    /// that teardown constructs nothing; constructions per open are the caller's delta between opens.</summary>
    public static (int Before, int After) Teardown(IntPtr root)
    {
        int before = CtorCounter;
        if (root != IntPtr.Zero && _recursiveAutoDelete != null) _recursiveAutoDelete(root);
        return (before, CtorCounter);
    }

    // ---- self-check ----------------------------------------------------------------------------

    private static IntPtr _fakeSelf;
    private static long _fakeParam;
    private static int _fakeCalls;
    private static IntPtr _fakeGob;

    [UnmanagedCallersOnly]
    private static void FakeHandler(IntPtr self, long param) { _fakeSelf = self; _fakeParam = param; _fakeCalls++; }

    /// <summary>The count-label gob (one line) beside the two-line label gob.</summary>
    private static IntPtr _fakeCountGob;

    [UnmanagedCallersOnly]
    private static int FakeLineCount(IntPtr gob) => gob == _fakeGob ? 2 : gob == _fakeCountGob ? 1 : 0;

    private static IntPtr _line0, _line1, _countLine;
    [UnmanagedCallersOnly]
    private static IntPtr FakeLine(IntPtr gob, int i) =>
        gob == _fakeCountGob ? (i == 0 ? _countLine : IntPtr.Zero) : gob != _fakeGob ? IntPtr.Zero : i == 0 ? _line0 : _line1;

    /// <summary>A stand-in <c>CreateSubMenu</c>: stores <see cref="_fakeChild"/> at <c>m_pSubMenu[i]</c> for slot 1 only.</summary>
    private static IntPtr _fakeChild;
    [UnmanagedCallersOnly]
    private static int FakeCreate(IntPtr menu, int i)
    {
        if (i != 1) return 0;
        *(IntPtr*)(menu + EngineLayout.RadialSubMenu + i * 8) = _fakeChild;
        return 1;
    }

    /// <summary>A fake root laid out per the resolved <see cref="EngineLayout"/> rows (so the same check runs on either
    /// platform's offsets), with a planted vptr, a text bubble + StringGob pair behind the exported readers (swapped
    /// for managed fakes, since the stub host resolves nothing), a CGuiIcon blob with a count label, sentinel and set
    /// callbacks, the event-object global redirected at a stack cell so the bracket's restore can be asserted, and a
    /// faked <c>CreateSubMenu</c> so Materialize's parent link and BuildItems' row mapping are covered. Teardown is an
    /// engine call with no fake -- plumbing only, live-unverified.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"RadialTree.SelfCheck FAIL: {w}"); } }

        const int BlobSize = 0x900, VtSize = 0x200, ObjAdj = 0x10;
        byte* menu = (byte*)NativeMemory.AllocZeroed(BlobSize);
        byte* fakeVt = (byte*)NativeMemory.AllocZeroed(VtSize);
        byte* handlerVt = (byte*)NativeMemory.AllocZeroed(VtSize);
        byte* handlerObj = (byte*)NativeMemory.AllocZeroed(0x40);
        byte* bubble = (byte*)NativeMemory.AllocZeroed(0x200);
        byte* gob = (byte*)NativeMemory.AllocZeroed(0x600);
        byte* icon = (byte*)NativeMemory.AllocZeroed(0x48);
        byte* line0 = (byte*)NativeMemory.AllocZeroed(32);
        byte* line1 = (byte*)NativeMemory.AllocZeroed(32);
        byte* countGob = (byte*)NativeMemory.AllocZeroed(0x600);
        byte* countLine = (byte*)NativeMemory.AllocZeroed(32);
        byte* child = (byte*)NativeMemory.AllocZeroed(BlobSize);
        IntPtr eventCell = IntPtr.Zero;

        var savedVptr = _vptr[(int)RootKind.Door];
        var savedCount = _getTextLineCount; var savedLine = _getTextLine; var savedEvent = _eventObject;
        var savedCreate = _createSubMenu;
        try
        {
            int stride = EngineLayout.RadialCallbackStride;
            bool win = OperatingSystem.IsWindows();
            for (int i = 0; i < SlotCount; i++)
            {
                IntPtr e = (IntPtr)(menu + EngineLayout.RadialEntry + i * stride);
                IntPtr c = (IntPtr)(menu + EngineLayout.RadialCreator + i * stride);
                *(IntPtr*)e = e;
                *(IntPtr*)c = c;
            }
            // vptr: Itanium points 0x10 into the _ZTV blob, MSVC at the vftable label -- plant the resolved form.
            *(IntPtr*)menu = (IntPtr)fakeVt + VtableToVptr;
            _vptr[(int)RootKind.Door] = (IntPtr)fakeVt + VtableToVptr;
            *(int*)(menu + EngineLayout.RadialMenuX) = 640;
            *(int*)(menu + EngineLayout.RadialMenuY) = 360;
            *(uint*)(menu + EngineLayout.RadialTargetId) = 0x7f01;

            // slot 0: a direct entry leaf, labelled + iconed, param 0x123456789.
            IntPtr e0 = (IntPtr)(menu + EngineLayout.RadialEntry);
            *(IntPtr*)e0 = (IntPtr)handlerObj;
            *(IntPtr*)(e0 + 8) = (IntPtr)(delegate* unmanaged<IntPtr, long, void>)&FakeHandler;
            *(long*)(menu + EngineLayout.RadialParam) = 0x123456789L;
            *(IntPtr*)(menu + EngineLayout.RadialText) = (IntPtr)bubble;
            *(IntPtr*)(menu + EngineLayout.RadialIcon) = (IntPtr)icon;
            "IR_CAST\0"u8.CopyTo(new Span<byte>(icon + EngineLayout.GuiIconResRef, ResRefLen));
            "Cast "u8.CopyTo(new Span<byte>(line0, 32));
            "Spell"u8.CopyTo(new Span<byte>(line1, 32));
            _line0 = (IntPtr)line0; _line1 = (IntPtr)line1; _fakeGob = (IntPtr)gob;
            *(IntPtr*)(bubble + EngineLayout.TextBubbleString) = (IntPtr)gob + EngineLayout.StringGobReaderAdjust;
            "3"u8.CopyTo(new Span<byte>(countLine, 32));
            _countLine = (IntPtr)countLine; _fakeCountGob = (IntPtr)countGob;
            *(IntPtr*)(icon + EngineLayout.GuiIconCountLabel) = (IntPtr)countGob + EngineLayout.StringGobReaderAdjust;
            _getTextLineCount = &FakeLineCount;
            _getTextLine = &FakeLine;
            _createSubMenu = &FakeCreate;
            _fakeChild = (IntPtr)child;

            // slot 1: a creator (submenu), disabled; slot 2: a materialized child with no creator.
            IntPtr c1 = (IntPtr)(menu + EngineLayout.RadialCreator + stride);
            *(IntPtr*)c1 = (IntPtr)handlerObj;
            *(uint*)(menu + EngineLayout.RadialFlags + 4) = 1;
            *(IntPtr*)(menu + EngineLayout.RadialSubMenu + 2 * 8) = (IntPtr)fakeVt;

            // slot 3 (Itanium only): a virtual entry -- odd fn = vtable byte offset 0x28 + 1, adj 0x10, so this =
            // obj + 0x10 and the vtable is read from there.
            IntPtr e3 = (IntPtr)(menu + EngineLayout.RadialEntry + 3 * stride);
            *(IntPtr*)e3 = (IntPtr)handlerObj;
            if (win)
            {
                *(IntPtr*)(e3 + 8) = (IntPtr)(delegate* unmanaged<IntPtr, long, void>)&FakeHandler;
            }
            else
            {
                *(IntPtr*)(e3 + 8) = (IntPtr)(0x28 + 1);
                *(IntPtr*)(e3 + 16) = (IntPtr)ObjAdj;
                *(IntPtr*)(handlerObj + ObjAdj) = (IntPtr)handlerVt;
                *(IntPtr*)(handlerVt + 0x28) = (IntPtr)(delegate* unmanaged<IntPtr, long, void>)&FakeHandler;
            }
            *(long*)(menu + EngineLayout.RadialParam + 3 * 8) = -7;

            E(Classify((IntPtr)menu) == RootKind.Door, "a planted vptr classifies as the kind it was planted for");
            *(IntPtr*)menu = (IntPtr)handlerVt;
            E(Classify((IntPtr)menu) == RootKind.Unknown, "an unknown vptr classifies as Unknown");
            *(IntPtr*)menu = (IntPtr)fakeVt + VtableToVptr;
            E(Position((IntPtr)menu) == (640, 360), "Position reads menu XY");
            E(TargetId((IntPtr)menu, RootKind.Door) == 0x7f01 && TargetId((IntPtr)menu, RootKind.Quickbar) == 0,
                "TargetId reads the subclass word for kinds that have one, 0 otherwise");

            RadialSlot s0 = ReadSlot((IntPtr)menu, 0);
            E(s0.Kind == SlotKind.Entry && !s0.IsSubMenu, $"slot 0 is an entry ({s0.Kind})");
            E(s0.Label == "Cast Spell", $"slot 0 label rejoins the wrapped lines (\"{s0.Label}\")");
            E(s0.Icon == "ir_cast", $"slot 0 icon is the lowercased resref (\"{s0.Icon}\")");
            E(s0.Count == "3", $"slot 0 count reads the icon's label gob (\"{s0.Count}\")");
            E(!s0.Disabled && s0.Param == 0x123456789L, "slot 0 enabled, param read");
            E(s0.Obj == (IntPtr)handlerObj && s0.Fn == (IntPtr)(delegate* unmanaged<IntPtr, long, void>)&FakeHandler
                && s0.Adj == IntPtr.Zero && s0.Child == IntPtr.Zero, "slot 0 triple read back");
            RadialSlot s1 = ReadSlot((IntPtr)menu, 1);
            E(s1.Kind == SlotKind.Creator && s1.IsSubMenu && s1.Disabled && s1.Label == "" && s1.Icon == "",
                $"slot 1 is a disabled creator with no label/icon ({s1.Kind} {s1.Disabled} \"{s1.Label}\")");
            RadialSlot s2 = ReadSlot((IntPtr)menu, 2);
            E(s2.Kind == SlotKind.Creator && s2.Child == (IntPtr)fakeVt, "slot 2: a materialized child alone makes a submenu");
            RadialSlot s4 = ReadSlot((IntPtr)menu, 4);
            E(s4.Kind == SlotKind.Empty && !s4.IsSubMenu && s4.Obj == IntPtr.Zero, "slot 4: both sentinels = empty");

            _eventObject = &eventCell;
            eventCell = (IntPtr)0x5150;
            _fakeCalls = 0;
            E(Invoke((IntPtr)menu, 0) && _fakeCalls == 1 && _fakeSelf == (IntPtr)handlerObj && _fakeParam == 0x123456789L,
                $"Invoke slot 0 calls fn(obj, param) ({_fakeCalls} {_fakeParam:x})");
            E(eventCell == (IntPtr)0x5150, "Invoke restores s_pCurrentEventObject");
            E(!Invoke((IntPtr)menu, 4) && _fakeCalls == 1, "Invoke on an unset entry does nothing");
            E(Invoke((IntPtr)menu, 3) && _fakeCalls == 2 && _fakeParam == -7
                && _fakeSelf == (win ? (IntPtr)handlerObj : (IntPtr)(handlerObj + ObjAdj)),
                win ? "Invoke slot 3 (Win64 pair)" : $"Invoke slot 3 decodes the virtual thunk: this = obj + adj ({(long)_fakeSelf - (long)handlerObj:x})");
            E(Materialize((IntPtr)menu, 9) == IntPtr.Zero && !Invoke((IntPtr)menu, -1), "out-of-range slots are refused");

            // BuildItems: slots 0 (entry), 1 (disabled creator), 2 (child), 3 (entry) -- empties 4..7 dropped, tags = slot.
            CascadeItem[] items = BuildItems((IntPtr)menu);
            E(items.Length == 4, $"BuildItems keeps the four filled slots ({items.Length})");
            E(items.Length == 4 && (int)items[0].Tag! == 0 && (int)items[1].Tag! == 1 && (int)items[2].Tag! == 2 && (int)items[3].Tag! == 3,
                "row tags are the vanilla slot indices, in order");
            E(items.Length == 4 && items[0].Label == "Cast Spell" && items[0].Icon == "ir_cast" && items[0].Count == "3" && !items[0].HasChildren && !items[0].Disabled,
                "row 0 carries label, icon, count, no mark");
            E(items.Length == 4 && items[1].HasChildren && items[1].Disabled && items[2].HasChildren && !items[3].HasChildren && items[3].Count == "",
                "creator + child rows carry the mark, the disabled flag follows the slot, no icon = no count");

            // Materialize through the faked CreateSubMenu: slot 1 yields the child with its parent linked; slot 3 declines.
            E(Materialize((IntPtr)menu, 1) == (IntPtr)child && *(IntPtr*)(child + EngineLayout.RadialParent) == (IntPtr)menu,
                "Materialize returns m_pSubMenu[i] and links the child's m_pParent");
            E(Materialize((IntPtr)menu, 3) == IntPtr.Zero, "a declined creator yields null");
        }
        finally
        {
            _vptr[(int)RootKind.Door] = savedVptr;
            _getTextLineCount = savedCount; _getTextLine = savedLine; _eventObject = savedEvent; _createSubMenu = savedCreate;
            _fakeGob = _fakeCountGob = _fakeChild = IntPtr.Zero; _line0 = _line1 = _countLine = IntPtr.Zero;
            NativeMemory.Free(menu); NativeMemory.Free(fakeVt); NativeMemory.Free(handlerVt); NativeMemory.Free(handlerObj);
            NativeMemory.Free(bubble); NativeMemory.Free(gob); NativeMemory.Free(icon); NativeMemory.Free(line0); NativeMemory.Free(line1);
            NativeMemory.Free(countGob); NativeMemory.Free(countLine); NativeMemory.Free(child);
        }
        log.Info(ok ? "RadialTree.SelfCheck: passed" : "RadialTree.SelfCheck: FAILED");
        RadialIcons.SelfCheck(log);
    }
}
