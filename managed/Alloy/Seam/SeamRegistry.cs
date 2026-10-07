using System;
using System.Collections.Generic;

namespace CX.Alloy.Seam;

/// <summary>One vtable CX builds and hands the game layer. The engine's concrete class, not the abstract
/// interface: the pointer the game holds is the concrete object's, so the table it calls through carries the
/// concrete slot count (<c>Gob</c> 94 on Linux / 93 on Windows, not <c>CAurObject</c>'s 80).
/// <see cref="CAurString"/> and <see cref="Camera"/>'s secondary <c>Gob</c> table are reached through a
/// second vptr inside the same shell.</summary>
public enum SeamTable : byte
{
    /// <summary>`Gob` -- what a `CAurObject*` points at. 94 Linux slots (0-79 the `CAurObject` interface,
    /// 80-93 `Gob`-only), 93 Windows. Also Camera's secondary vptr at +8.</summary>
    Gob = 0,
    /// <summary>`Scene` -- what a `CAurScene*` points at. 88 Linux slots (0-50 the interface, 51-87 Scene-only), 87 Windows.</summary>
    Scene = 1,
    /// <summary>`Camera` primary -- what a `CAurCamera*` points at. 41 Linux slots (0-32 the interface,
    /// 33-40 Camera-only), 33 Windows: seven of the eight live only in Camera's `Gob` table on Windows.</summary>
    Camera = 2,
    /// <summary>`StringGob` primary -- a text object's `Gob` vptr at +0. 143 Linux slots (0-93 `Gob`'s,
    /// 94-142 StringGob's own), 97 Windows: 45 of the 49 live only in the `CAurString` table there.</summary>
    StringGob = 3,
    /// <summary>`CAurString` -- the sub-object vptr at +0x4c0 of a text shell. 52 Linux slots, 51 Windows.</summary>
    CAurString = 4,
    /// <summary>Camera's secondary `Gob` table, the vptr at +8 of a camera shell: `Gob`'s 94/93 slots with
    /// Camera's nine overrides patched in. Inherits every <see cref="Gob"/> registration; a camera-specific
    /// slot is registered here and changes no plain object.</summary>
    CameraGob = 5,
}

/// <summary>A CX-120 stream that owns some of the seam's vtable slots or engine-function replacements.
/// Discovered by reflection at boot (like <c>IExtenderService</c>), so a stream adds a class implementing this
/// and needs no edit to <see cref="SeamService"/>. <see cref="Register"/> runs on the boot thread, in type-name
/// order, BEFORE any vtable is built and before any hook is installed -- so it may only record, never call the
/// engine.</summary>
public interface ISeamProvider
{
    /// <summary>Record this stream's slot implementations and engine-function replacements.</summary>
    void Register(SeamRegistry r);
}

/// <summary>CX-120's one registration surface: every seam slot implementation and every engine function the
/// seam replaces is recorded here by its owning stream's <see cref="ISeamProvider"/>, and
/// <see cref="SeamService"/> turns the record into per-OS vtables and one funchook batch. Boot-only; nothing
/// here is touched again after <c>SeamService.Initialize</c> returns.</summary>
public sealed unsafe class SeamRegistry
{
    /// <summary>A recorded replacement: the engine symbol the seam answers instead of the engine, in DB mode.</summary>
    internal readonly record struct Replacement(string Symbol, IntPtr Handler, int Priority, string Owner);

    private readonly IntPtr[] _impl = new IntPtr[SeamVtables.LogicalCount];
    private readonly string?[] _implOwner = new string?[SeamVtables.LogicalCount];
    private readonly List<Replacement> _replacements = new();
    private readonly List<Action> _afterBuild = new();

    /// <summary>The provider currently registering, for the refusal lines.</summary>
    internal string Owner = "?";

    internal IntPtr[] Impls => _impl;
    internal string?[] ImplOwners => _implOwner;
    internal IReadOnlyList<Replacement> Replacements => _replacements;
    internal IReadOnlyList<Action> AfterBuild => _afterBuild;
    /// <summary>Every refusal this registration pass produced, logged by <see cref="SeamService"/>.</summary>
    internal readonly List<string> Refusals = new();

    /// <summary>Claim slot <paramref name="linuxSlot"/> of <paramref name="table"/> for
    /// <paramref name="impl"/>, the address of an <c>[UnmanagedCallersOnly]</c> static whose parameter list is
    /// the slot's ABI on THIS OS (see <c>CX-183</c> section 4 for the 17 by-value
    /// returns, which differ per OS and need a per-OS declaration).
    /// <para>The slot index is always the LINUX index -- the canonical identity in
    /// <c>tools/seam/aurora-seam-vtables.tsv</c>. The builder places it at the Windows index there when it
    /// builds the Windows table, and drops it from a Windows table whose Windows home is a different vftable
    /// of the same class (MSVC does not duplicate a secondary-base override into the primary vtable).</para>
    /// <para>One implementation can serve several tables: slots that share an implementation symbol share one
    /// logical id, so registering <c>(StringGob, 94)</c> also fills <c>(CAurString, 3)</c>. Registering both
    /// with the same pointer is accepted; registering them with different pointers is refused.</para></summary>
    public void Slot(SeamTable table, int linuxSlot, void* impl)
    {
        int id = SeamVtables.Logical(table, linuxSlot);
        if (id < 0) { Refusals.Add($"{Owner}: {table}+{linuxSlot} is not a slot of that table"); return; }
        if (impl == null) { Refusals.Add($"{Owner}: {table}+{linuxSlot} ({SeamVtables.Name(id)}) registered a null implementation"); return; }
        IntPtr p = (IntPtr)impl;
        if (_impl[id] != IntPtr.Zero && _impl[id] != p)
        {
            Refusals.Add($"{Owner}: {table}+{linuxSlot} ({SeamVtables.Name(id)}) already claimed by {_implOwner[id]}");
            return;
        }
        _impl[id] = p;
        _implOwner[id] ??= Owner;
    }

    /// <summary>Claim every slot of <paramref name="table"/> from <paramref name="first"/> to
    /// <paramref name="last"/> inclusive for one implementation -- the form a stream uses for a run it answers
    /// uniformly (a shared refusal, a shared forward).</summary>
    public void SlotRange(SeamTable table, int first, int last, void* impl)
    {
        for (int s = first; s <= last; s++) Slot(table, s, impl);
    }

    /// <summary>Replace engine function <paramref name="symbol"/> with <paramref name="handler"/> for the
    /// session, in DB mode only. <see cref="SeamService"/> installs it through <c>HookService</c> in one batch
    /// after every provider has registered; the handler never calls the original (the engine's scene graph
    /// does not exist in DB mode), so no trampoline is handed back. A symbol two streams both claim is a
    /// refusal, not a silent last-wins -- use <paramref name="priority"/> and <see cref="ChainEngine"/> when
    /// sharing one is deliberate.</summary>
    public void ReplaceEngine(string symbol, void* handler)
    {
        foreach (Replacement r in _replacements)
            if (r.Symbol == symbol && r.Priority == int.MinValue)
            { Refusals.Add($"{Owner}: {symbol} already replaced by {r.Owner}"); return; }
        _replacements.Add(new Replacement(symbol, (IntPtr)handler, int.MinValue, Owner));
    }

    /// <summary>Share <paramref name="symbol"/> with another handler at <paramref name="priority"/>
    /// (<c>HookService.Chain</c>, #88): the seam's stacked form, for a symbol a non-seam service also hooks.</summary>
    public void ChainEngine(string symbol, void* handler, int priority)
        => _replacements.Add(new Replacement(symbol, (IntPtr)handler, priority, Owner));

    /// <summary>Run <paramref name="a"/> once, after every table is built and before the hooks commit -- where
    /// a stream caches a built table's address or resolves an engine pointer it needs at the first call.</summary>
    public void AfterTablesBuilt(Action a) => _afterBuild.Add(a);
}
