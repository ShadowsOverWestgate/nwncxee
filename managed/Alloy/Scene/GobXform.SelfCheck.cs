using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class GobXform
{
    /// <summary>Off-device: the composition math, pinned to the engine's own matrix stack, and the moved set's
    /// three rules. Synthetic parts in unmanaged scratch -- nothing here touches the client.
    ///
    /// <para><b>What is being proved.</b> The engine builds a part's world matrix by PUSHING onto the model
    /// matrix stack: <c>RenderInterface::Transform(pos, quat)</c> is <c>MakeMatrix4x4</c> @0x6b8ce0 then
    /// <c>aurMatrixStack::ApplyMatrix</c> @0x4db450, which is <c>top := m x top</c> (its <c>operator*</c>
    /// @0x6b54c0 computes <c>rhs x lhs</c> row-by-row), and <c>Scale(s)</c> @0x4db060 multiplies the top's ROWS,
    /// i.e. <c>top := S x top</c>. So a leaf under one parent under a gob carries
    /// <c>S_leaf T_leaf S_parent T_parent S_gob T_gob</c>. <see cref="Stack"/> spells exactly that, term for
    /// term, and the check is that <see cref="Compose"/>'s similarity-transform form
    /// (<see cref="Chain"/> + <see cref="Build"/>, the form <c>Part::GetWorld</c> @0x437930 accumulates)
    /// produces the same matrix. If the two ever diverge, every creature, door and held item is placed
    /// wrong -- and a live session sees it as geometry in the wrong place, with nothing to say why.</para></summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GobXform.SelfCheck");

        // A quaternion pair the arithmetic cannot accidentally satisfy: neither is a half-turn about an axis
        // the other shares, and neither is the identity.
        Quaternion qGob = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.7f, -0.3f, 0.2f));
        Quaternion qRoot = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-1.1f, 0.4f, 0.9f));
        Quaternion qLeaf = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.25f, 1.3f, -0.6f));
        Vector3 pGob = new(11f, -23f, 4.5f), pRoot = new(0.5f, 2f, -1.25f), pLeaf = new(-3f, 0.75f, 6f);
        float sGob = 1.5f, sRoot = 0.8f, sLeaf = 2.25f;

        // Two synthetic parts: a leaf whose part+0x48 names the root, and a root with no parent.
        int bytes = Math.Max(EngineLayout.PartParent, EngineLayout.PartScale) + 16;
        IntPtr blk = Marshal.AllocHGlobal(bytes * 2);
        try
        {
            new Span<byte>((void*)blk, bytes * 2).Clear();
            IntPtr root = blk, leaf = blk + bytes;
            var rv = new Part(root);
            rv.Pos = pRoot; rv.Rot = qRoot; rv.Scale = sRoot;
            var lv = new Part(leaf);
            lv.Pos = pLeaf; lv.Rot = qLeaf; lv.Scale = sLeaf; lv.Parent = new Part(root);

            // the engine's stack, pushed in its own order: gob first, then the tree from the root down
            Matrix4x4 want = Stack(Matrix4x4.Identity, pGob, qGob, sGob);
            want = Stack(want, pRoot, qRoot, sRoot);
            want = Stack(want, pLeaf, qLeaf, sLeaf);

            Chain(leaf, out float cs, out Quaternion cq, out Vector3 cp);
            Matrix4x4 got = Build(cs * sGob, qGob * cq, Vector3.Transform(cp * sGob, qGob) + pGob);
            check.Expect(Close(got, want, 1e-3f),
                         "the composed matrix equals the engine's own matrix-stack product, gob + two parts");
            check.Expect(MathF.Abs(ScaleOf(want) - sGob * sRoot * sLeaf) < 1e-3f,
                         "ScaleOf reads the whole scale chain off the engine's product, so a cull sphere scales with the gob");

            // ...and the root alone, which is the creature-body case (one part, one gob).
            Matrix4x4 wantRoot = Stack(Stack(Matrix4x4.Identity, pGob, qGob, sGob), pRoot, qRoot, sRoot);
            Chain(root, out cs, out cq, out cp);
            Matrix4x4 gotRoot = Build(cs * sGob, qGob * cq, Vector3.Transform(cp * sGob, qGob) + pGob);
            check.Expect(Close(gotRoot, wantRoot, 1e-3f), "...and for a single part under a gob");

            // A gobless part is its own local pair and nothing else -- the branch Part::GetWorld takes on
            // part+0x50 == 0, and what the mesh-bucket loop of Scene::AddPartsToDrawBuckets writes. Getting
            // this wrong would move every tile in the area, so it is checked against a hand-built matrix
            // rather than against the stack helper.
            Matrix4x4 tile = Compose(root, IntPtr.Zero);
            Matrix4x4 wantTile = Matrix4x4.CreateFromQuaternion(qRoot) * Matrix4x4.CreateTranslation(pRoot);
            check.Expect(Close(tile, wantTile, 1e-4f),
                         "a gobless part composes to its OWN local pos/quat, with no parent and no scale");
            check.Expect(MathF.Abs(tile.M41 - pRoot.X) < 1e-4f && MathF.Abs(tile.M42 - pRoot.Y) < 1e-4f
                         && MathF.Abs(tile.M43 - pRoot.Z) < 1e-4f,
                         "...and its translation is in the fourth ROW (the engine's row-vector convention)");

            // The parent climb is BOUNDED, and the hop count is what says so: a real tree stops when the
            // parent chain runs out, and a cycle stops at the guard. Asserting only that the call returned
            // would assert nothing -- a cycle would hang, not go red.
            check.Expect(ChainHops(leaf, out _, out _, out _) == 2, "a two-deep tree walks exactly two hops");
            lv.Parent = new Part(leaf);                 // one-node cycle
            check.Expect(ChainHops(leaf, out _, out _, out _) == MaxDepth,
                         "a parent cycle stops at the hop guard instead of hanging the walk");
            rv.Parent = new Part(leaf); lv.Parent = new Part(root);   // two-node cycle
            check.Expect(ChainHops(leaf, out _, out _, out _) == MaxDepth, "...and so does a two-node cycle");
            rv.Parent = default; lv.Parent = new Part(root);

            // The moved set. Unknown is MOVED (a gob the registry has not seen must not be served a stale
            // matrix), a marked gob is moved for that frame only, and a gobless slot never is.
            int savedFrame = _frame;
            bool savedAll = _all;
            AreaEdge();
            check.Expect(Moved((IntPtr)0x1000), "after an area edge every gob recomposes");
            _all = false;
            _frame = 100;
            check.Expect(Moved((IntPtr)0x2000), "a gob the registry has never seen recomposes");
            check.Expect(!Moved(IntPtr.Zero), "a gobless part never recomposes -- nothing writes its local");
            Mark((IntPtr)0x3000);
            check.Expect(Moved((IntPtr)0x3000), "a marked gob recomposes this frame");
            _frame = 101;
            check.Expect(!Moved((IntPtr)0x3000), "...and not the frame after, unless it is marked again");
            // The ORDERING that matters for CX-106/CX-107: their runtimes mark during the game update, which
            // is ahead of Tick, and Tick's first act is to bump the transform frame. A mark keyed on that
            // frame would be gone before it was read; the mark generation is what survives it.
            int savedSeq = _markSeq;
            Mark((IntPtr)0x4000);
            int mh = Index((IntPtr)0x4000, false);
            _frame++;                                   // what Tick does first
            check.Expect(mh >= 0 && _markAt[mh] == _markSeq,
                         "a mark made before Tick survives Tick's frame bump");
            _markSeq++;                                 // what Tick does once it has consumed them
            check.Expect(_markAt[mh] != _markSeq, "...and is retired once consumed");
            _markSeq = savedSeq;

            // GS2: the leave notice. Two rules, and both fail in a way only a long session shows. A released
            // row must be REUSABLE, or the notice buys nothing and the registry still fills to Cap/2 and
            // latches `_all` for the rest of the area; and a gob that hashed BEHIND the released one must
            // still be found, or its matrix silently stops being recomposed. `x` and `y` collide on one bucket
            // by construction -- the key is `ptr >> 4` masked to Bits.
            AreaEdge();
            _all = false;
            _frame = 200;
            IntPtr x = (IntPtr)0x70000, y = x + (Cap << 4);
            int hx = Index(x, true), hy = Index(y, true);
            check.Expect(hx >= 0 && hy == ((hx + 1) & Mask), "two gobs colliding on one bucket take adjacent rows");
            int wasCount = _count;
            Forget(x);
            check.Expect(Index(x, false) < 0 && _count == wasCount - 1,
                         "a gob that left the scene has no row, and the registry counts one fewer");
            check.Expect(Index(y, false) == hy, "...and the gob that hashed BEHIND it is still found");
            int tombsWas = _tombs, countWas = _count;
            check.Expect(Index(x, true) == hx && _movedAt[hx] == _frame && float.IsNaN(_raw[hx * 8]),
                         "a released row is reused, and its new tenant is seeded MOVED with no cached placement");
            // The counter rule, spelled: a reused tombstone is live AND no longer a tombstone. Taking the two as
            // alternatives (which GobLod's first cut did) drifts the stamped total down without bound, and a
            // stamped total that can go negative is a load factor that never fires again.
            check.Expect(_tombs == tombsWas - 1 && _count == countWas + 1,
                         "reusing a tombstone counts the row as live and drops the tombstone");

            // Review: the load factor counts the TOMBSTONES. Counting only live rows would let a registry
            // Forget kept supplying rows to read `730/8192` with every row stamped -- Index probing all of
            // them and the occupancy gate bounding nothing, which is the failure this notice exists to close,
            // invisibly. Past the factor the registry recycles, which costs TWO frames of recomposing
            // everything -- `_all` on the frame it happens, and every row re-Seeded MOVED on the next -- and
            // then O(1) probes again.
            // Tested as the RULE on the stamped total, and with the tombstones carrying it, rather than by
            // filling 8192 rows and releasing them again at every boot for the same assertion.
            AreaEdge();
            _all = false;
            _frame = 300;
            const int load = Cap / 2;
            _count = load - 1; _tombs = 0;
            int genWas = _gen;
            check.Expect(Index((IntPtr)0x400000, true) >= 0 && _gen == genWas && _count == load,
                         "an insert that lands ON the load factor is seated, not recycled");
            _count = load - 1; _tombs = 1;         // the same total, one of it a released row
            genWas = _gen;
            _all = false;
            check.Expect(Index((IntPtr)0x500000, true) >= 0 && _gen == genWas + 1 && _tombs == 0 && _count == 1,
                         "a registry at the factor is RECYCLED on the next insert rather than refusing it, and "
                         + "a tombstone counts towards it exactly as a live row does");
            check.Expect(_all, "...at the cost of recomposing everything on this frame, and again on the next "
                             + "as each row is re-Seeded MOVED");
            // ...and the backstop: no free row at all, which Index reports as -1 and Seed answers the same way.
            genWas = _gen;
            check.Expect(Seed(-1, (IntPtr)0x510000) >= 0 && _gen == genWas + 1 && _count == 1 && _tombs == 0,
                         "a registry with no free row is recycled on that alone");
            _forgotten = 0; _recycles = 0;

            AreaEdge();
            _frame = savedFrame; _all = savedAll;
        }
        finally { Marshal.FreeHGlobal(blk); }

        log.Info(check.Ok ? "GobXform.SelfCheck: passed"
                          : "GobXform.SelfCheck: FAILED -- the owned world matrix disagrees with the engine's "
                            + "matrix stack; every gob part would be placed wrong");
        return check.Ok;
    }

    /// <summary>One push of the engine's model matrix stack: <c>Transform(pos, quat)</c> then, when the scale is
    /// not 1, <c>Scale(s)</c> -- <c>top := S x (T x top)</c>.</summary>
    private static Matrix4x4 Stack(Matrix4x4 top, Vector3 pos, Quaternion rot, float s)
    {
        Matrix4x4 t = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);
        return Matrix4x4.CreateScale(s) * (t * top);
    }

    private static bool Close(in Matrix4x4 a, in Matrix4x4 b, float tol)
    {
        fixed (Matrix4x4* pa = &a, pb = &b)
        {
            float* x = (float*)pa, y = (float*)pb;
            for (int i = 0; i < 16; i++) if (MathF.Abs(x[i] - y[i]) > tol) return false;
        }
        return true;
    }
}
