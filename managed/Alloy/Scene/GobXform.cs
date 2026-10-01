using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-103: <b>Alloy composes every world matrix.</b> The engine's only writer of a gob part's
/// <c>VertexPrimitive+0x18</c> is <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880, reached only for gobs in
/// <c>gobbucket</c> -- so a gob the engine never placed held the IDENTITY its <c>VertexPrimitive</c> ctor left
/// and drew at the area's (0,0) corner, and the whole bounded chase (#330) existed to bound the per-frame
/// re-read of a field only the engine could write. Both are gone: the matrix is ours.
///
/// <para><b>The composition, term for term.</b> The engine builds it on the model matrix stack --
/// <c>ApplyMatrix(m)</c> @0x4db450 is <c>top := m x top</c> (row-vector; <c>operator*</c> @0x6b54c0 computes
/// <c>rhs x lhs</c>) and <c>aurMatrixStack::Scale</c> @0x4db060 scales the top's ROWS, i.e. <c>top := S x
/// top</c>. The gob loop pushes <c>T(gobPos, gobQuat)</c> then <c>Scale(gobScale)</c>, and each part of the
/// tree pushes <c>T(part+0x10, part+0x1c)</c> then <c>Scale(part+0x2c)</c>, so a leaf's matrix is
/// <c>S_leaf T_leaf ... S_root T_root S_gob T_gob</c>. Every scale in that chain is UNIFORM, so the product is
/// a similarity transform and equals <c>Scale(s) x Rot(q) x Translate(p)</c> for the s, q and p the engine's
/// own <c>Part::GetWorld</c> @0x437930 accumulates up the <c>part+0x48</c> parent chain. That is what
/// <see cref="Compose"/> does, which is also why <c>SkyBox</c> and <c>HighlightGlow</c> -- both live-verified,
/// one bottom-up and one top-down -- agree with it.</para>
///
/// <para><b>A gobless part is its own local, exactly as vanilla.</b> Both engine writers say so: the
/// mesh-bucket loop of <c>Scene::AddPartsToDrawBuckets</c> writes <c>Matrix4x4(part+0x10, part+0x1c)</c> with
/// no parent and no scale, and <c>Part::GetWorld</c>'s <c>part+0x50 == 0</c> branch returns the same pair. So
/// tile geometry composes once and never again -- nothing in the client animates a gobless part's local.</para>
///
/// <para><b>What moves.</b> A gob is recomposed when a cheap read says its placement can have changed (raw
/// position, raw orientation, the scale base, a live <c>AnimRun</c>, an attachment, a look-at, the LOD dirty
/// byte) or when <see cref="Mark"/> named it -- the seam CX-106's animation runtime and CX-107's behaviour
/// solvers call for every gob they wrote. A staggered full recompose (<c>alloy.xformrefresh</c> frames) is the
/// net under all of that for a writer nobody predicted -- a game-layer <c>SetPartLocalPosition</c> through the
/// <c>CAurObject</c> interface is virtual and invisible to the call graph. While both owned runtimes drive, the
/// net stretches by <see cref="OwnedStretch"/>: every known writer then reports itself. Static placements and
/// ALL tile geometry cost nothing per frame.</para>
///
/// <para><b>The one remaining read of the engine's matrix</b> is <c>alloy.xformcheck</c>, off by default: it
/// composes AND reads <c>vp+0x18</c> and counts the disagreements on the [alloy] line. That is what lets a
/// live session verify this port; nothing else in Alloy touches the field.</para></summary>
internal static unsafe partial class GobXform
{
    /// <summary>Runaway guard on the <c>part+0x48</c> climb and on the attach chain. A part tree is wide and
    /// shallow; a cycle is not a shape the engine builds.</summary>
    private const int MaxDepth = 64, MaxAttach = 8;

    /// <summary>How much longer the unpredicted-writer net runs while the owned animation runtime and the owned
    /// behaviour solvers both drive and <see cref="Mark"/> what they move: 8 frames becomes 64.</summary>
    private const int OwnedStretch = 8;

    /// <summary>The gob registry: open-addressed on the <c>Gob*</c>, rebuilt on an area edge. Sized well past
    /// the gob count of any shipped area (a Scene's <c>+0x138</c> is low thousands); an overflow sets
    /// <see cref="_all"/>, which recomposes everything and is correct at the old cost.</summary>
    private const int Bits = 14, Cap = 1 << Bits, Mask = Cap - 1;
    private static readonly IntPtr[] _key = new IntPtr[Cap];
    /// <summary>Per gob: the CHEAP change-detection inputs, as raw field loads -- position (3), orientation
    /// (4), the scale base <see cref="EngineLayout.GobScaleBase"/> (1).</summary>
    private static readonly float[] _raw = new float[Cap * 8];
    /// <summary>Per gob: its ACTIVE root pointer (<c>gob+0x90</c>), compared beside <see cref="_raw"/>. Two
    /// jobs. It is a second LOD-swap detector, and -- the reason it is here -- it closes the one window a
    /// registry keyed on a <c>Gob*</c> has: a gob freed mid-area and a NEW gob allocated at the same address
    /// inherit the entry, and a new gob whose raw placement happens to equal the dead one's would have read
    /// "not moved" for that frame. A recycled gob's tree comes from a fresh <c>CreateInstanceTree</c>, so this
    /// pointer differs.</summary>
    private static readonly IntPtr[] _rawTree = new IntPtr[Cap];
    /// <summary>Per gob: this frame's composed root term -- scale (1), orientation (4), position (3) -- and the
    /// frame it was taken on. Memoised so a creature's twenty slots pay the three engine getters ONCE.</summary>
    private static readonly float[] _term = new float[Cap * 8];
    private static readonly int[] _termAt = new int[Cap];
    private static readonly int[] _movedAt = new int[Cap];
    /// <summary>Per gob: the mark generation <see cref="Mark"/> last stamped. A SEPARATE stamp from
    /// <see cref="_movedAt"/> because a mark lands wherever its runtime runs -- CX-106's animation and CX-107's
    /// behaviour solvers write poses during the game UPDATE, which is ahead of <see cref="Tick"/>, so a mark
    /// keyed on the transform frame would be bumped away before it was read. <see cref="Tick"/> consumes this
    /// generation and then retires it.</summary>
    private static readonly int[] _markAt = new int[Cap];
    private static int _markSeq = 1;
    private static readonly int[] _seen = new int[Cap];
    /// <summary>Per gob: the frame the engine last filed it in <c>gobbucket</c>, i.e. the last frame its
    /// <c>vp+0x18</c> is fresh. Stamped only under <c>alloy.xformcheck</c>, which compares against nothing
    /// else.</summary>
    private static readonly int[] _engineAt = new int[Cap];
    /// <summary><c>gobbucket</c>, resolved on the first checked frame: a .bss symbol answers only once boot is
    /// done.</summary>
    private static EngineList _gobBucket;
    private static bool _gobBucketTried;
    /// <summary>The registry's generation (a stamp older than this reads as an empty slot, so an area edge costs
    /// no clear), its live entry count, and the frame number the per-gob stamps are taken against.</summary>
    private static int _gen = 1, _count, _frame;
    /// <summary>Recompose everything: the registry overflowed, or no registry has been built yet.</summary>
    private static bool _all = true;

    private static bool _ready;
    private static ExtenderLog? _log;

    /// <summary>Can Alloy compose at all? The three gob getters are <c>EngineLayout</c> vtable slots (covered
    /// by <c>EngineLayout.Require</c>); this is the one free symbol the composition needs.</summary>
    internal static bool Ready => _ready;

    internal static void Resolve(ExtenderLog log)
    {
        _log = log;
        _ready = EngineFn.LerpFloatLerped != null;
        if (!_ready)
            log.Always("GobXform: " + SymbolMap.LerpFloatLerped + " unresolved -- Alloy cannot compose an "
                       + "attached gob's scale, so the owned transform stands down and every world matrix is "
                       + "read off the engine again (alloy.ownxform reads 0 on /alloy)");
    }

    /// <summary>Alloy composes the matrices (<c>alloy.ownxform</c>, and the symbol resolved).</summary>
    internal static bool Armed => _ready && Renderer.OwnXform.V;

    /// <summary>The area changed: every gob address in the registry is about to be recycled.</summary>
    internal static void AreaEdge() { _gen++; _count = 0; _tombs = 0; _all = true; }

    /// <summary>CX-102 seam (<see cref="GobMotion.Mark"/>): a runtime of ours wrote this gob's pose or
    /// placement this frame. Allocation-free, one probe; safe to call before <see cref="Tick"/>, because a
    /// mark for a gob the registry has not seen inserts it.</summary>
    internal static void Mark(IntPtr gob)
    {
        int h = Index(gob, true);
        // Only a null gob now (an insert recycles rather than refuses); kept as the safe answer to a row the
        // registry could not give out at all.
        if (h < 0) { _all = true; return; }
        _markAt[h] = _markSeq;
        // ...and this frame too, for a mark that lands AFTER Tick has already decided the frame.
        _movedAt[h] = _frame;
        _marks++;
    }

    /// <summary>Once per world frame, at the frame anchor, engine context: decide which gobs moved. One pass
    /// over the area's own gob list (<c>Scene+0x130</c>, the list <see cref="SceneWalk"/> enumerates), eight
    /// float compares and four byte/int loads per gob, no allocation and no engine call -- the three getters
    /// are paid per MOVED gob, in <see cref="RootTerm"/>.</summary>
    internal static void Tick(IntPtr scene)
    {
        _frame++;
        if (!Armed || scene == IntPtr.Zero) { _all = true; return; }
        var sc = new Scene(scene);
        IntPtr* gobs = (IntPtr*)sc.Gobs;
        int n = sc.GobCount;
        if (gobs == null || n < 0) { _all = true; return; }
        int refresh = Math.Max(1, Renderer.XformRefresh.V);
        if (AnimRuntime.Owned && BehaviorSolvers.Driving) refresh *= OwnedStretch;
        _all = false;
        _moved = 0;
        for (int k = 0; k < n; k++)
        {
            IntPtr gob = gobs[k];
            if (gob == IntPtr.Zero) continue;
            int h = Index(gob, true);
            // Defensive: an insert now recycles the registry rather than refusing one, and the gob is not null,
            // so this cannot be reached. It stays because the SAFE answer to a missing row is to recompose
            // everything, and the loop must still reach its end either way -- the mark generation is retired at
            // the bottom and the remaining gobs are counted.
            if (h < 0) { _all = true; continue; }
            var g = new Gob(gob);
            bool moved = _markAt[h] == _markSeq || _movedAt[h] == _frame;   // Mark() already named it
            // The cheap inputs. A placement write lands in exactly these fields, and a raw compare is the
            // whole detection for a walking creature, a swinging door and a lifted placeable.
            Vector3 pos = g.Position;
            Quat rot = g.Rot;
            float bas = g.ScaleBase;
            int o = h * 8;
            IntPtr tree = g.PartTree.Ptr;
            if (!moved)
                moved = _raw[o] != pos.X || _raw[o + 1] != pos.Y || _raw[o + 2] != pos.Z
                        || _raw[o + 3] != rot.W || _raw[o + 4] != rot.X
                        || _raw[o + 5] != rot.Y || _raw[o + 6] != rot.Z
                        || _raw[o + 7] != bas || _rawTree[h] != tree;
            // ...and the four "this gob's part LOCALS may have been rewritten" facts. An AnimRun is the
            // engine's animation state (Scene::Animate walks it), an attachment means the parent's tree is
            // copied into ours (SynchronizeHierarchies, gob+0x1b8), a look-at aims the head nodes, and the
            // LOD dirty byte means SetLODModel just rotated the subtree under us.
            if (!moved)
                moved = g.AnimRunCount != 0 || !g.Attach.IsNull || !g.LookAt.IsNull || g.GeomDirty != 0;
            // The net under all of that: every gob recomposed at least once every `refresh` frames, staggered.
            if (!moved && ((h + _frame) % refresh) == 0) { moved = true; _refreshed++; }
            if (moved)
            {
                _raw[o] = pos.X; _raw[o + 1] = pos.Y; _raw[o + 2] = pos.Z;
                _raw[o + 3] = rot.W; _raw[o + 4] = rot.X; _raw[o + 5] = rot.Y; _raw[o + 6] = rot.Z;
                _raw[o + 7] = bas; _rawTree[h] = tree;
                _movedAt[h] = _frame;
                _moved++;
                // The root term is taken HERE, on the main thread, because the staging walk that consumes it
                // runs on StagePool workers (alloy.threads) -- a memo written from one of those would be a
                // torn read on another. A slot forced to recompose without its gob having moved (a stale
                // record epoch, a slot re-entering the view) computes the term locally instead and caches
                // nothing, which is why RootTerm never writes.
                Term(h, gob);
            }
        }
        _gobs = n;
        if (Renderer.XformCheck.V) StampEngineBucket();
        // Every mark this frame has been consumed; the next Tick must not see them again.
        _markSeq++;
    }

    /// <summary>Does this slot's matrix have to be recomposed this frame? False is the whole win: a static
    /// placement and every tile part answer it, and their records are served from themselves.</summary>
    internal static bool Moved(IntPtr gob)
    {
        if (_all) return true;
        if (gob == IntPtr.Zero) return false;   // a gobless part's local is written by nobody
        int h = Index(gob, false);
        return h < 0 || _movedAt[h] == _frame;
    }

    /// <summary>The world matrix of <paramref name="tri"/>, composed. <paramref name="gob"/> is the part's own
    /// <c>part+0x50</c> as the ownership table recorded it (never re-read here: the route gate has proved the
    /// PART live, not the gob, and this is the one place the gob is dereferenced -- behind that same
    /// verdict).</summary>
    internal static Matrix4x4 Compose(IntPtr tri, IntPtr gob)
    {
        System.Threading.Interlocked.Increment(ref _composed);
        if (gob == IntPtr.Zero)
        {
            // Part::GetWorld's own gobless branch, and the mesh-bucket loop's: the local pair IS the world
            // one, with no parent climb and no scale. Matching it exactly is what keeps tile geometry vanilla.
            var leaf = new Part(tri);
            return Build(1f, leaf.Rot, leaf.Pos);
        }
        Chain(tri, out float s, out Quaternion q, out Vector3 p);
        RootTerm(gob, out float gs, out Quaternion gq, out Vector3 gp);
        return Build(s * gs, gq * q, Vector3.Transform(p * gs, gq) + gp);
    }

    /// <summary><c>Part::GetWorld</c>'s parent climb, to the root and not past it: the accumulated uniform
    /// scale, orientation and offset of <paramref name="tri"/> in its GOB's space. Pure -- the self-check
    /// drives it over synthetic parts.</summary>
    internal static void Chain(IntPtr tri, out float s, out Quaternion q, out Vector3 p)
        => ChainHops(tri, out s, out q, out p);

    /// <summary>...and the hop count it stopped at, so <see cref="MaxDepth"/> is an OBSERVABLE rather than a
    /// promise: the self-check drives a cycle through this and asserts it came back at the guard. A parent
    /// cycle is not a shape the engine builds, but a truncated walk and a hung frame fail very differently.
    /// </summary>
    internal static int ChainHops(IntPtr tri, out float s, out Quaternion q, out Vector3 p)
    {
        s = 1f; q = Quaternion.Identity; p = Vector3.Zero;
        int hops = 0;
        for (var part = new Part(tri); !part.IsNull && hops < MaxDepth; part = part.Parent, hops++)
        {
            float ps = part.Scale;
            Quaternion pq = part.Rot;
            p = Vector3.Transform(p * ps, pq) + part.Pos;
            q = pq * q;
            s *= ps;
        }
        return hops;
    }

    /// <summary>A similarity transform as a matrix, in the engine's own convention: row-vector, translation in
    /// the fourth row (<c>MakeMatrix4x4</c> @0x6b8ce0 writes <c>m[12..14]</c> and matches Numerics'
    /// <c>CreateFromQuaternion</c> term for term).</summary>
    internal static Matrix4x4 Build(float s, Quaternion q, Vector3 p)
        => Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(p);

    /// <summary>The uniform scale a world matrix carries: its first row's length, since every scale in the
    /// chain is uniform and the rows are <c>s</c> times a rotation's. What a local radius is multiplied by to
    /// bound the part the matrix actually draws.</summary>
    internal static float ScaleOf(in Matrix4x4 m) => new Vector3(m.M11, m.M12, m.M13).Length();

    /// <summary>The gob's own term, memoised for this frame: the adjusted scale times every ancestor's
    /// visual-transform scale lerp, and the adjusted orientation and position. The three getters fold the
    /// gob's <c>LerpFloat</c> visual transform in, which is why they are CALLED rather than transcribed
    /// (<c>LerpFloat::Lerped</c> integrates against the engine's own <c>CWorldTimer</c>).</summary>
    private static void RootTerm(IntPtr gob, out float scale, out Quaternion rot, out Vector3 pos)
    {
        int h = Index(gob, false);
        if (h >= 0 && _termAt[h] == _frame)
        {
            int c = h * 8;
            scale = _term[c];
            rot = new Quaternion(_term[c + 1], _term[c + 2], _term[c + 3], _term[c + 4]);
            pos = new Vector3(_term[c + 5], _term[c + 6], _term[c + 7]);
            return;
        }
        Read(gob, out scale, out rot, out pos);
    }

    /// <summary>The gob's three getters, and nothing else. All three are read-only on this client -- each is
    /// arithmetic over <c>LerpFloat::Lerped</c> @0x5f8fc0, which is const in fact as well as in signature
    /// (<c>Progress</c> @0x5f8d30 writes only locals; the writing <c>RefreshInternally</c> is NOT on its path)
    /// -- so a stage worker may call them.</summary>
    private static void Read(IntPtr gob, out float scale, out Quaternion rot, out Vector3 pos)
    {
        var g = new Gob(gob);
        scale = g.GetAdjustedScale() * AttachScale(gob);
        rot = AurXform.AdjustedOrientation(gob);
        AurXform.V3 v = AurXform.AdjustedPosition(gob);
        pos = new Vector3(v.X, v.Y, v.Z);
        System.Threading.Interlocked.Increment(ref _terms);
    }

    /// <summary>Main thread only, from <see cref="Tick"/>: memoise this gob's root term for the frame.</summary>
    private static void Term(int h, IntPtr gob)
    {
        Read(gob, out float scale, out Quaternion rot, out Vector3 pos);
        int w = h * 8;
        _term[w] = scale;
        _term[w + 1] = rot.X; _term[w + 2] = rot.Y; _term[w + 3] = rot.Z; _term[w + 4] = rot.W;
        _term[w + 5] = pos.X; _term[w + 6] = pos.Y; _term[w + 7] = pos.Z;
        _termAt[h] = _frame;
    }

    /// <summary>The one placement term <c>Part::GetWorld</c> does not carry: the gob loop of
    /// <c>Scene::AddPartsToDrawBuckets</c> @0x4751d0 climbs the attach chain and multiplies each ancestor's
    /// <c>LerpFloat(ancestor+0x1c0).Lerped()</c> -- the ancestor's visual-transform SCALE, not its
    /// <c>GetAdjustedScale</c> -- into the pushed scale. 1.0 for every gob without an attachment, which is
    /// everything but held items, heads and worn pieces.
    /// <para>The engine reaches the ancestor through the behaviour's own virtual (slot 0x38, whose result
    /// carries a <c>Part*</c> at +0x30 whose <c>part+0x50</c> is the gob); this takes
    /// <see cref="EngineLayout.BehaviorTarget"/>, the same climb <c>RunGob</c> and <c>CreaturePart</c> already
    /// make. Unverified that the two never disagree; a divergence would be a wrong SCALE on an attached
    /// item, which <c>alloy.xformcheck</c> reports.</para></summary>
    private static float AttachScale(IntPtr gob)
    {
        float s = 1f;
        for (int hop = 0; hop < MaxAttach; hop++)
        {
            CAurBehavior b = new Gob(gob).Attach;
            if (b.IsNull) break;
            IntPtr parent = b.Target.Ptr;
            if (parent == IntPtr.Zero) break;
            s *= new Gob(parent).ScaleLerp.LerpFloatLerped();
            gob = parent;
        }
        return s;
    }

    /// <summary>GS2: the gob LEFT the scene the walk enumerates -- which <c>Gob::~Gob</c> announces, because it
    /// calls <c>Gob::SetScene(0)</c> before it destroys anything, and <see cref="SceneWalk"/>'s hook on that
    /// symbol is the notice. Release its row.
    /// <para><b>What this closes.</b> Without it the registry only ever grew: every spawn in a busy area is a
    /// fresh address, nothing was ever released, and a long session in one area filled it to
    /// <see cref="Cap"/>/2 -- at which point <see cref="Seed"/> recycles the whole registry (it used to refuse
    /// the insert and let the caller latch <see cref="_all"/> for the rest of the area, i.e. EVERY gob
    /// recomposed EVERY frame, reading as a slow frame with no cause). The other half is the recycled address
    /// itself: a new gob on a dead one's row would be compared against the dead one's cached placement, which
    /// <see cref="_rawTree"/> catches (a recycled gob's tree is a fresh <c>CreateInstanceTree</c>) and which
    /// now does not arise -- the row is gone and its replacement is seeded MOVED.</para>
    /// <para>A TOMBSTONE, not an empty row: the registry is open-addressed with linear probing, so a hole
    /// would cut the probe chain of every gob that hashed behind it. That is also why <see cref="_tombs"/> is
    /// inside the load factor -- a tombstone still costs a probe.</para>
    /// <para><b>Where the notice does NOT reach</b>, deliberately. <c>Gob::SetScene</c> bails on
    /// <c>old == scene</c>, so a gob destroyed while already out of a scene is silent; and nothing is delivered
    /// while <c>SceneWalk._scene</c> is Zero (no walk yet) or the walk is not <c>Ready</c>. In all three the
    /// registry has no ownership to serve, and the recycle above is the backstop. <c>PoseService</c>'s own
    /// pointer-keyed per-gob slots are out of scope on purpose: its probe window is BOUNDED and a slot untouched
    /// for 30 s is reclaimed in place (<c>PoseService.Claim</c>), so it can neither degrade into a full-table
    /// walk nor hold a dead row for longer than that.</para></summary>
    internal static void Forget(IntPtr gob)
    {
        if (gob == IntPtr.Zero) return;
        int h = Index(gob, false);
        if (h < 0) return;
        _key[h] = Tomb;
        _movedAt[h] = 0; _termAt[h] = 0; _markAt[h] = 0; _engineAt[h] = 0; _rawTree[h] = IntPtr.Zero;
        // Unconditional, to match the `_tombs++` beside it: the SUM is load-bearing now, so a clamp here would
        // hide a miscount as a registry that quietly never reaches its factor.
        _count--;
        _tombs++;
        _forgotten++;
    }

    /// <summary>A row <see cref="Forget"/> released. Never a real <c>Gob*</c>, and distinct from
    /// <c>IntPtr.Zero</c>, which <see cref="Index"/> refuses outright.</summary>
    private static readonly IntPtr Tomb = (IntPtr)(-1);
    /// <summary>Rows <see cref="Forget"/> released. Counted BESIDE <see cref="_count"/> and never instead of
    /// it: a tombstone is still a stamped row, so it still costs a probe and still has to sit inside the load
    /// factor <see cref="Seed"/> enforces.</summary>
    private static int _tombs;

    /// <summary>The registry slot for <paramref name="gob"/>, or -1 when absent (and <paramref name="insert"/>
    /// is clear) or the table is full. A slot stamped with an older registry generation reads empty, so an
    /// area edge costs no clear.</summary>
    private static int Index(IntPtr gob, bool insert)
    {
        if (gob == IntPtr.Zero) return -1;
        int h = (int)((ulong)gob >> 4) & Mask;
        int tomb = -1;
        for (int probe = 0; probe <= Mask; probe++, h = (h + 1) & Mask)
        {
            if (_seen[h] != _gen) return insert ? Seed(tomb >= 0 ? tomb : h, gob) : -1;
            // A released row is free to take, but the probe has to walk PAST it: a gob that hashed behind it
            // is reachable only through this chain, and stopping here would lose it.
            if (_key[h] == Tomb) { if (tomb < 0) tomb = h; continue; }
            if (_key[h] == gob) return h;
        }
        // Every row occupied and none of them ours: -1 asks Seed for the recycle, which is the same backstop
        // GobLod's Insert has (review: the two were asymmetric). Refusing here instead would latch
        // `_all` for the rest of the area, which is the failure the load factor exists to avoid.
        return insert ? Seed(tomb >= 0 ? tomb : -1, gob) : -1;
    }

    /// <summary>A fresh row for <paramref name="gob"/> in slot <paramref name="h"/>, recycling the whole
    /// registry first when it is past its load factor.
    /// <para><b>The load factor counts tombstones too</b> (review), because a stamp is never cleared and a
    /// tombstone is still a stamp. Counting only live rows would let a registry <see cref="Forget"/> kept
    /// supplying rows to report <c>730/8192</c> with all <see cref="Cap"/> rows stamped, <see cref="Index"/>
    /// probing every one of them, and the occupancy gate bounding nothing -- the failure GS2 exists to close,
    /// invisibly.</para>
    /// <para>Past it -- or at <paramref name="h"/> of -1, meaning the probe found no free row at all -- the
    /// registry is recycled exactly as <see cref="AreaEdge"/> recycles it. That costs TWO frames of recomposing
    /// everything, <see cref="_all"/> on this one and every row re-seeded MOVED on the next, and then O(1)
    /// probes again -- which is still strictly better than the old answer, which was to refuse the insert and
    /// let the caller latch <see cref="_all"/> for the rest of the area.</para></summary>
    private static int Seed(int h, IntPtr gob)
    {
        if (h < 0 || _count + _tombs >= Cap / 2)
        {
            _gen++; _count = 0; _tombs = 0; _all = true; _recycles++;
            h = (int)((ulong)gob >> 4) & Mask;
        }
        if (_seen[h] == _gen && _key[h] == Tomb) _tombs--;
        _seen[h] = _gen; _key[h] = gob; _termAt[h] = 0; _markAt[h] = 0;
        _rawTree[h] = IntPtr.Zero; _engineAt[h] = 0;
        // A fresh tenant has no cached raw placement, and comparing against the previous tenant's
        // would be the one shape of this bug that reads as a frozen creature: seed it MOVED.
        _movedAt[h] = _frame;
        for (int j = 0; j < 8; j++) _raw[h * 8 + j] = float.NaN;
        _count++;
        return h;
    }

    // ---- instruments ----
    private static long _composed, _terms, _marks, _refreshed;
    /// <summary>GS2: rows released by the leave notice, and registry recycles the load factor forced. Reported
    /// so a session can see the registry stop filling up -- the stamped occupancy against <see cref="Cap"/>/2
    /// is what decides whether <see cref="_all"/> latches.</summary>
    private static long _forgotten, _recycles;
    private static int _moved, _gobs;
    /// <summary>The validator's tally: routed matrix-class slots checked against <c>vp+0x18</c> and the
    /// disagreements, with the worst translation error seen. Only <c>alloy.xformcheck</c> fills them.</summary>
    internal static long CheckedN, CheckMiss, CheckStale;
    internal static float CheckWorst;
    /// <summary>The first miss's two translations and its worst element, recorded by whichever thread saw it
    /// and LOGGED on the period line. <see cref="Check"/> runs inside <c>StagePart</c>, i.e. on StagePool
    /// workers, so it may neither allocate (an interpolated string per miss, in the stage loop) nor race a
    /// bool latch on the log.</summary>
    private static float _missMineX, _missMineY, _missMineZ, _missTheirsX, _missTheirsY, _missTheirsZ, _missWorst;
    private static int _missCaught;

    /// <summary>The one remaining read of <c>VertexPrimitive+0x18</c>, behind <c>alloy.xformcheck</c>: does the
    /// composed matrix agree with the one the engine wrote? A miss is counted, the first one is logged with
    /// both translations, and the composed matrix is used either way -- the check observes, it never
    /// corrects.</summary>
    internal static void Check(IntPtr node, IntPtr gob, in Matrix4x4 mine)
    {
        if (node == IntPtr.Zero) return;
        // The engine writes a gob part's matrix only for a gob it filed this frame; any other gob's vp+0x18 is
        // however stale the engine's own cull left it, and comparing against that measures the engine.
        if (gob != IntPtr.Zero)
        {
            int h = Index(gob, false);
            if (h < 0 || _engineAt[h] != _frame) { System.Threading.Interlocked.Increment(ref CheckStale); return; }
        }
        Matrix4x4 theirs = new NodeRef(node).Model;
        System.Threading.Interlocked.Increment(ref CheckedN);
        // A matrix the engine never wrote is the identity its VertexPrimitive ctor left; counting that as a
        // disagreement would report the very fault CX-103 removed as a port bug.
        if (theirs.IsIdentity) return;
        float worst = 0f;
        fixed (Matrix4x4* a = &mine)
        {
            float* x = (float*)a, y = (float*)&theirs;
            for (int i = 0; i < 16; i++) worst = MathF.Max(worst, MathF.Abs(x[i] - y[i]));
        }
        if (worst <= CheckTol) return;
        System.Threading.Interlocked.Increment(ref CheckMiss);
        // Plain float stores under a claim: a torn worst-element is a diagnostic, and the claim is what keeps
        // the SIX numbers a coherent pair rather than two threads' halves.
        if (System.Threading.Interlocked.CompareExchange(ref _missCaught, 1, 0) == 0)
        {
            _missMineX = mine.M41; _missMineY = mine.M42; _missMineZ = mine.M43;
            _missTheirsX = theirs.M41; _missTheirsY = theirs.M42; _missTheirsZ = theirs.M43;
            _missWorst = worst;
        }
        if (worst > CheckWorst) CheckWorst = worst;
    }

    /// <summary>Main thread, from <see cref="Tick"/>: stamp every gob the engine filed in <c>gobbucket</c> this
    /// frame. <c>ManageSceneBSP</c> refills it and <c>AddPartsTreeToDrawBuckets</c> writes those gobs' matrices
    /// before the frame anchor, so at this point the stamp names exactly the fresh <c>vp+0x18</c> set.</summary>
    private static void StampEngineBucket()
    {
        if (!_gobBucketTried)
        {
            _gobBucketTried = true;
            _gobBucket = new EngineList(Native.Sym(SymbolMap.GobBucket));
            if (_gobBucket.IsNull)
                _log?.Always("GobXform: gobbucket unresolved -- alloy.xformcheck has no fresh engine matrix to compare "
                           + "a gob part against, so it checks tile parts only");
        }
        if (_gobBucket.IsNull || _gobBucket.Items == IntPtr.Zero) return;
        int n = _gobBucket.Count;
        for (int i = 0; i < n; i++)
        {
            int h = Index(_gobBucket[i], false);
            if (h >= 0) _engineAt[h] = _frame;
        }
    }

    /// <summary>Main thread, once per period: log the first miss <see cref="Check"/> caught. Here rather than
    /// at the miss because the miss is seen on a stage worker inside the per-part loop.</summary>
    private static void LogFirstMiss()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _missCaught, 2, 1) != 1) return;
        _log?.Always($"GobXform: alloy.xformcheck MISS -- composed translation ({_missMineX:F3}, {_missMineY:F3}, "
                     + $"{_missMineZ:F3}) vs engine ({_missTheirsX:F3}, {_missTheirsY:F3}, {_missTheirsZ:F3}), worst "
                     + $"element {_missWorst:F4}; the owned composition and Scene::AddPartsTreeToDrawBuckets disagree");
    }

    /// <summary>A millimetre, in the units the engine's own matrix is built in. The two are computed by
    /// different arithmetic (a matrix stack of 4x4 products against a quaternion chain), so bit equality is
    /// not the bar -- a placement error is orders of magnitude larger than this.</summary>
    private const float CheckTol = 1e-3f;

    internal static string Period()
    {
        long frames = Math.Max(1, AlloyRenderer.Counters._frames);
        string s = $"xform {_gobs} gobs, {_moved} moved/frame, composed {_composed / frames}/frame "
                 + $"(root terms {_terms / frames}/frame, marks {_marks}, refresh {_refreshed / frames}/frame, "
                 // Review: STAMPED occupancy, live + tombstones, because that is what bounds a probe and what
                 // the load factor gates on.
                 + $"registry {_count}+{_tombs} tomb(s) of {Cap / 2}"
                 + (_forgotten == 0 ? "" : $", {_forgotten} row(s) released on a scene leave")
                 + (_recycles == 0 ? "" : $", {_recycles} registry recycle(s)") + ")"
                 + (_all ? " REGISTRY STOOD DOWN -- everything recomposed" : "");
        if (CheckedN != 0 || CheckStale != 0)
        {
            s += $"; XFORMCHECK {CheckMiss} of {CheckedN} disagree (worst {CheckWorst:F4}), "
               + $"{CheckStale} skipped (a gob the engine did not place this frame)";
            LogFirstMiss();
        }
        _composed = _terms = _refreshed = 0; _marks = 0; _forgotten = 0; _recycles = 0;
        return s;
    }

    internal static void ResetPeriod() { CheckedN = 0; CheckMiss = 0; CheckStale = 0; CheckWorst = 0f; }
}
