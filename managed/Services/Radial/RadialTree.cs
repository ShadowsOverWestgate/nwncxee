using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
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
internal static unsafe partial class RadialTree
{
    public const int SlotCount = 8;
    private const int ResRefLen = 16;
    private const int MaxLabelLines = 16;

    private static readonly IntPtr[] _vptr = new IntPtr[(int)RootKind.ChatWindow + 1];
    private static RadialCreateSubMenuFn _createSubMenu;
    private static RadialRecursiveAutoDeleteFn _recursiveAutoDelete;
    private static StringGobGetTextLineCountFn _getTextLineCount;
    private static StringGobGetTextLineFn _getTextLine;
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
        _createSubMenu = (RadialCreateSubMenuFn)SymbolMap.Resolve(SymbolMap.RadialCreateSubMenu, log);
        _recursiveAutoDelete = (RadialRecursiveAutoDeleteFn)SymbolMap.Resolve(SymbolMap.RadialRecursiveAutoDelete, log);
        _getTextLineCount = (StringGobGetTextLineCountFn)SymbolMap.Resolve(SymbolMap.StringGobGetTextLineCount, log);
        _getTextLine = (StringGobGetTextLineFn)SymbolMap.Resolve(SymbolMap.StringGobGetTextLine, log);
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
    public static (int X, int Y) Position(IntPtr menu)
    {
        var m = new CRadialMenu(menu);
        return (m.MenuX, m.MenuY);
    }

    /// <summary>The root's target object id where the kind carries one (creature/item/placeable/door at
    /// <see cref="EngineLayout.RadialTargetId"/>, the cast menu at <see cref="EngineLayout.SpellMenuTargetId"/>);
    /// 0 for the rest.</summary>
    public static uint TargetId(IntPtr menu, RootKind kind) => kind switch
    {
        RootKind.Creature or RootKind.Item or RootKind.Placeable or RootKind.Door => new CRadialMenu(menu).TargetId,
        RootKind.Spell => new CClientSpellMenu(menu).TargetId,
        _ => 0,
    };

    public static RadialSlot ReadSlot(IntPtr menu, int i)
    {
        var m = new CRadialMenu(menu);
        int stride = EngineLayout.RadialCallbackStride;
        IntPtr entry = menu + EngineLayout.RadialEntry + i * stride;
        IntPtr creator = menu + EngineLayout.RadialCreator + i * stride;
        bool hasEntry = *(IntPtr*)entry != entry;
        bool hasCreator = *(IntPtr*)creator != creator;
        IntPtr child = m.SubMenu[i].Ptr;
        SlotKind kind = hasCreator || child != IntPtr.Zero ? SlotKind.Creator : hasEntry ? SlotKind.Entry : SlotKind.Empty;
        IntPtr obj = hasEntry ? *(IntPtr*)entry : IntPtr.Zero;
        IntPtr fn = hasEntry ? *(IntPtr*)(entry + 8) : IntPtr.Zero;
        IntPtr adj = hasEntry && !OperatingSystem.IsWindows() ? *(IntPtr*)(entry + 16) : IntPtr.Zero;
        var icon = new CGuiIcon(m.Icon[i]);
        return new RadialSlot(i, kind,
            ReadLabel(m.Text[i]),
            ReadIcon(icon),
            icon.IsNull ? "" : ReadAur(icon.CountLabel),
            (m.Flags[i] & 1) != 0,
            m.Param[i],
            obj, fn, adj, child);
    }

    private static string ReadLabel(IntPtr bubble) =>
        bubble == IntPtr.Zero ? "" : ReadAur(new CGuiTextBubble(bubble).AurString);

    /// <summary>Rejoin a <c>CAurString</c>'s wrapped lines (the bubble label and the icon's count label are both
    /// <c>NewCAurString</c> gobs): <c>StringGob::WrapLines</c> @0x494d80 keeps the break space (and any newline) on
    /// the first piece, so plain concatenation restores the text SetText was given.</summary>
    private static string ReadAur(IntPtr aur)
    {
        if (aur == IntPtr.Zero || _getTextLineCount == null || _getTextLine == null) return "";
        // The reader's `this` is a this-ADJUSTMENT, not a field: the object base on Linux, the sub-object on MSVC.
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

    private static string ReadIcon(CGuiIcon icon)
    {
        if (icon.IsNull) return "";
        Span<byte> p = icon.Resref;
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
        var m = new CRadialMenu(menu);
        CRadialMenu child = m.SubMenu[i];
        if (!child.IsNull) child.Parent = menu;
        return child.Ptr;
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
        var m = new CRadialMenu(menu);
        long param = m.Param[i];
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
}
