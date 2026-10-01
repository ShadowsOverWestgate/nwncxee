using System;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// AN1: the owned <c>Part::Animate</c> walk. Replaces the engine's per-gob part-tree recursion with one
/// iterative pass that calls out only where a subclass actually does something.
/// </summary>
/// <remarks>
/// <para>Addresses below are <b>nm</b>, as elsewhere in this namespace; the read-only decompile's are Ghidra's,
/// which are these plus 0x100000.</para>
/// <para><b>Why there was nothing to run.</b> <c>Part::Animate</c> @0x49ad00 is, with Tracy off, exactly
/// <c>for each child: child-&gt;vtbl[+0xd8](dt)</c> -- one indirect call per part, re-reading
/// <c>Part+0x38</c>/<c>+0x40</c> each iteration, writing no field and marking nothing (objdump
/// 0x49ad53..0x49ad90; Ghidra drops the <c>dt</c> float and drowns the rest in the Tracy zone). So the whole
/// per-gob tree walk existed to find the parts whose Animate is NOT that function. A sweep of every vtable in
/// the binary for a Part-interface slot finds exactly three functions in it:
/// <list type="bullet">
/// <item><c>Part::Animate</c> -- <c>Part</c>, <c>PartAABB</c>, <c>PartAnimMesh</c>, <c>PartBeam</c>,
/// <c>PartCamera</c>, <c>PartLight</c>, <c>PartProjection</c>, <c>PartReference</c>, <c>PartShadow</c>,
/// <c>PartSkin</c>, <c>PartTriMesh</c>, <c>PartTrigger</c> and Gob's own <c>PartAABB</c> sub-vtable.</item>
/// <item><c>PartEmitter::Animate</c> @0x4b3310 -- <c>PartEmitter</c> and its four subclasses. A pure
/// tail-jump into <c>Part::Animate</c>, so it is a no-op override and this walk treats it as plain.</item>
/// <item><c>PartDanglyMesh::Animate</c> @0x49bbb0 -- the dangly solve, which calls <c>Part::Animate</c> on its
/// own children as its last act.</item>
/// </list>
/// <para><b>Equivalence.</b> A part whose slot is <see cref="SymbolMap.PartAnimate"/> or
/// <see cref="SymbolMap.PartEmitterAnimate"/> has its children walked here; any other slot is DISPATCHED and
/// its subtree left alone, because an override reaches its own children itself -- which is the engine's own
/// arrangement, and the honest answer for an override this build does not describe. The visit order is the
/// engine's preorder and <see cref="SelfCheck"/> pins it against a recursive reference walk.</para>
/// <para><b>It bypasses any hook on the two plain slots.</b> Walking a plain part's children here instead
/// of calling its Animate means a funchook patch on <c>Part::Animate</c> or <c>PartEmitter::Animate</c> would
/// not be reached. Neither is hooked today -- <c>SymbolMap.PartAnimate</c> is resolved as a call target by
/// <see cref="DanglyPhysics"/>'s tail and as an identity here, and nothing installs on either -- and a future
/// hook on one of them belongs in this walk instead, which is where a reader will look.</para>
/// <para><b>Safe under a reshape.</b> A dispatched override can run game code, so the child array and count
/// are re-read on every step, exactly as the engine re-reads them. No tree is cached, so there is nothing to
/// invalidate: the walk is the only reader of the tree's shape and it always reads it live.</para>
/// <para>Allocation-free: the explicit stack is two static arrays, and its ceiling is counted rather than
/// being a hang (the engine's own recursion has no guard at all).</para>
/// </remarks>
internal static unsafe class PartWalk
{
    /// <summary>Tree depth this walk descends. A part tree is a tree by construction and a humanoid is a
    /// handful of levels deep; past this the subtree is left unwalked and counted, which is the refusal
    /// <see cref="SkinPose"/> already makes for the same reason.</summary>
    private const int MaxDepth = 64;

    private static readonly IntPtr[] _stkPart = new IntPtr[MaxDepth];
    private static readonly int[] _stkIdx = new int[MaxDepth];

    private static IntPtr _fnPlain, _fnEmitter, _fnDangly;
    private static ExtenderLog? _log;
    private static bool _warned;

    /// <summary>AN0/AN1: parts visited, subtrees handed to an override, and how many of those overrides were
    /// neither the dangly solve nor the emitter no-op. <see cref="Overflow"/> is the depth ceiling.</summary>
    internal static int Visits, Dispatches, Unknown, Overflow;

    /// <summary>Resolve the three Animate identities. False = the caller must keep calling the engine's
    /// <c>Part::Animate</c>, because without them every part would read as an unknown override.</summary>
    internal static bool Attach(ExtenderLog log)
    {
        _log = log;
        _fnPlain = Native.Sym(SymbolMap.PartAnimate);
        _fnEmitter = Native.Sym(SymbolMap.PartEmitterAnimate);
        _fnDangly = Native.Sym(SymbolMap.PartDanglyAnimate);
        return _fnPlain != IntPtr.Zero && _fnEmitter != IntPtr.Zero && _fnDangly != IntPtr.Zero;
    }

    /// <summary>Off-device only: the three identities as the self-check's synthetic vtables carry them.</summary>
    internal static void SelfCheckAttach(IntPtr plain, IntPtr emitter, IntPtr dangly)
    { _fnPlain = plain; _fnEmitter = emitter; _fnDangly = dangly; _warned = true; }
    /// <inheritdoc cref="SelfCheckAttach"/>
    internal static void SelfCheckDetach() { _fnPlain = _fnEmitter = _fnDangly = IntPtr.Zero; _warned = false; }

    /// <summary>Whether this pointer is one of the two Animate slots that do nothing but recurse.</summary>
    private static bool Plain(IntPtr slot) => slot == _fnPlain || slot == _fnEmitter;

    /// <summary>Animate <paramref name="root"/>'s tree, as <c>root-&gt;Animate(dt)</c> would.</summary>
    internal static void Walk(IntPtr root, float dt)
    {
        IntPtr cur = root;
        int i = 0, sp = 0;
        while (true)
        {
            if (i == 0)
            {
                Visits++;
                IntPtr slot = *(IntPtr*)(*(IntPtr*)cur + EngineLayout.PartAnimateSlot);
                if (!Plain(slot))
                {
                    Dispatches++;
                    if (slot != _fnDangly)
                    {
                        Unknown++;
                        if (!_warned)
                        {
                            _warned = true;
                            _log?.Always($"PartWalk: an UNKNOWN Part::Animate override at 0x{slot:x} -- its "
                                       + "subtree is handed to it whole, as the engine does; counted on the "
                                       + "animrt clause from here");
                        }
                    }
                    ((PartAnimateFn)slot)(cur, dt);
                    goto up;
                }
            }
            {
                var p = new Part(cur);
                if (i < p.ChildCount)
                {
                    // Re-read both each step: a dispatched override runs game code that can reshape the tree
                    // under us, and the engine's own loop re-reads them for exactly that reason.
                    IntPtr* kids = (IntPtr*)p.Children;
                    IntPtr kid = kids == null ? IntPtr.Zero : kids[i];
                    i++;
                    if (kid == IntPtr.Zero) continue;
                    if (sp >= MaxDepth) { Overflow++; continue; }
                    _stkPart[sp] = cur;
                    _stkIdx[sp] = i;
                    sp++;
                    cur = kid;
                    i = 0;
                    continue;
                }
            }
        up:
            if (sp == 0) return;
            sp--;
            cur = _stkPart[sp];
            i = _stkIdx[sp];
        }
    }
}
