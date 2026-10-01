using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>AN1's one runnable proof: the owned walk visits the same parts, in the same order, as a recursive
/// reference walk of the same synthetic tree, and it leaves an override's subtree to the override.</summary>
internal static unsafe partial class PartWalkCheck
{
    /// <summary>Synthetic parts one check builds. Small on purpose -- the ordering property does not need a
    /// big tree, and <see cref="Deep"/> tests the ceiling with its own chain.</summary>
    private const int MaxParts = 80;
    /// <summary>The walk's own depth ceiling, so the deep chain can be built past it.</summary>
    private const int WalkDepth = 64;

    private static readonly IntPtr[] _parts = new IntPtr[MaxParts];
    private static int _n;

    /// <summary>The dispatch witnesses, in the order the walk called them.</summary>
    private static readonly IntPtr[] _seen = new IntPtr[MaxParts];
    private static int _seenN;
    /// <summary>...and the same, from the recursive reference.</summary>
    private static readonly IntPtr[] _want = new IntPtr[MaxParts];
    private static int _wantN, _refVisits;

    [UnmanagedCallersOnly]
    private static void OnDispatch(IntPtr part, float dt)
    {
        if (_seenN < MaxParts) _seen[_seenN] = part;
        _seenN++;
    }

    private static IntPtr NewPart(IntPtr vtbl)
    {
        IntPtr p = (IntPtr)NativeMemory.AllocZeroed(256);
        *(IntPtr*)p = vtbl;
        if (_n < MaxParts) _parts[_n] = p;
        _n++;
        return p;
    }

    private static void Adopt(IntPtr parent, IntPtr* kids, int n)
    {
        *(IntPtr*)(parent + EngineLayout.PartChildren) = (IntPtr)kids;
        *(int*)(parent + EngineLayout.PartChildCount) = n;
    }

    /// <summary>The recursive reference: the engine's own shape, written as a recursion so the iterative walk
    /// has something independent to agree with.</summary>
    private static void Reference(IntPtr part, IntPtr plain, IntPtr emitter, int depth)
    {
        _refVisits++;
        IntPtr slot = *(IntPtr*)(*(IntPtr*)part + EngineLayout.PartAnimateSlot);
        if (slot != plain && slot != emitter)
        {
            if (_wantN < MaxParts) _want[_wantN] = part;
            _wantN++;
            return;
        }
        if (depth >= WalkDepth) return;
        IntPtr* kids = (IntPtr*)*(IntPtr*)(part + EngineLayout.PartChildren);
        int n = *(int*)(part + EngineLayout.PartChildCount);
        for (int i = 0; i < n; i++)
            if (kids != null && kids[i] != IntPtr.Zero) Reference(kids[i], plain, emitter, depth + 1);
    }

    internal static bool Order()
    {
        // Four vtables, one per Animate identity the walk distinguishes. Only the +Animate slot is filled;
        // nothing here is ever called through any other slot.
        IntPtr vPlain = (IntPtr)NativeMemory.AllocZeroed(1024);
        IntPtr vEmit = (IntPtr)NativeMemory.AllocZeroed(1024);
        IntPtr vDangly = (IntPtr)NativeMemory.AllocZeroed(1024);
        IntPtr kidArrays = (IntPtr)NativeMemory.AllocZeroed((nuint)(MaxParts * 8 * sizeof(IntPtr)));
        _n = 0;
        try
        {
            IntPtr plain = (IntPtr)0x1000, emitter = (IntPtr)0x2000;
            IntPtr dangly = (IntPtr)(delegate* unmanaged<IntPtr, float, void>)&OnDispatch;
            *(IntPtr*)(vPlain + EngineLayout.PartAnimateSlot) = plain;
            *(IntPtr*)(vEmit + EngineLayout.PartAnimateSlot) = emitter;
            *(IntPtr*)(vDangly + EngineLayout.PartAnimateSlot) = dangly;
            PartWalk.SelfCheckAttach(plain, emitter, dangly);

            // root -> [a(plain), b(emitter), c(dangly, with a child that must NOT be visited)]
            // a    -> [a0(plain), aD(dangly)]                 -- so aD witnesses a's place in the order
            // a0   -> [a0D(dangly)]
            // b    -> [bD(dangly)]                            -- an emitter's subtree IS walked
            IntPtr root = NewPart(vPlain), a = NewPart(vPlain), b = NewPart(vEmit), c = NewPart(vDangly);
            IntPtr a0 = NewPart(vPlain), aD = NewPart(vDangly), a0D = NewPart(vDangly), bD = NewPart(vDangly);
            IntPtr cKid = NewPart(vDangly);
            IntPtr* slab = (IntPtr*)kidArrays;
            slab[0] = a; slab[1] = b; slab[2] = c; Adopt(root, slab, 3);
            slab[8] = a0; slab[9] = aD; Adopt(a, slab + 8, 2);
            slab[16] = a0D; Adopt(a0, slab + 16, 1);
            slab[24] = bD; Adopt(b, slab + 24, 1);
            slab[32] = cKid; Adopt(c, slab + 32, 1);

            _seenN = 0; _wantN = 0; _refVisits = 0;
            PartWalk.Visits = 0; PartWalk.Dispatches = 0; PartWalk.Unknown = 0; PartWalk.Overflow = 0;
            Reference(root, plain, emitter, 0);
            PartWalk.Walk(root, 0.016f);

            bool ok = _seenN == _wantN && _wantN == 4 && PartWalk.Visits == _refVisits
                   && PartWalk.Dispatches == 4 && PartWalk.Unknown == 0 && PartWalk.Overflow == 0;
            for (int i = 0; ok && i < _wantN; i++) ok = _seen[i] == _want[i];
            // c is dangly, so its child is the override's business and the walk must not have counted it.
            ok = ok && PartWalk.Visits == 8;
            return ok;
        }
        finally
        {
            for (int i = 0; i < _n && i < MaxParts; i++) NativeMemory.Free((void*)_parts[i]);
            NativeMemory.Free((void*)vPlain); NativeMemory.Free((void*)vEmit); NativeMemory.Free((void*)vDangly);
            NativeMemory.Free((void*)kidArrays);
            PartWalk.SelfCheckDetach();
        }
    }

    /// <summary>A chain deeper than the walk descends must be refused and counted, not overflow the stack.</summary>
    internal static bool Deep()
    {
        IntPtr vPlain = (IntPtr)NativeMemory.AllocZeroed(1024);
        IntPtr kidArrays = (IntPtr)NativeMemory.AllocZeroed((nuint)(MaxParts * sizeof(IntPtr)));
        _n = 0;
        try
        {
            IntPtr plain = (IntPtr)0x1000;
            *(IntPtr*)(vPlain + EngineLayout.PartAnimateSlot) = plain;
            PartWalk.SelfCheckAttach(plain, (IntPtr)0x2000, (IntPtr)0x3000);
            IntPtr* slab = (IntPtr*)kidArrays;
            IntPtr root = NewPart(vPlain), prev = root;
            for (int i = 0; i < MaxParts - 1; i++)
            {
                IntPtr kid = NewPart(vPlain);
                slab[i] = kid;
                Adopt(prev, slab + i, 1);
                prev = kid;
            }
            PartWalk.Visits = 0; PartWalk.Overflow = 0;
            PartWalk.Walk(root, 0.016f);
            // A chain longer than the stack: the part at the ceiling has nowhere to push its descent, so it
            // is refused ONCE and everything below it is left unwalked. The visited run is the ceiling + the
            // root's own level.
            return PartWalk.Overflow == 1 && PartWalk.Visits == WalkDepth + 1;
        }
        finally
        {
            for (int i = 0; i < _n && i < MaxParts; i++) NativeMemory.Free((void*)_parts[i]);
            NativeMemory.Free((void*)vPlain);
            NativeMemory.Free((void*)kidArrays);
            PartWalk.SelfCheckDetach();
        }
    }
}
