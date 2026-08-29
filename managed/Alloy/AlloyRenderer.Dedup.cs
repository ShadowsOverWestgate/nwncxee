using System;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>#244 phase A: recovering the PLACEMENT of a repeated mesh, and proving the recovery exact.
/// <para>The census established that an area is backed by ~3x fewer distinct shapes than it has parts, and
/// that ~95% of the repetition is world-space -- the engine baked each placement into its own copy of the
/// vertices. Collapsing those to one copy means putting the placement back into a matrix, and this is the
/// half that decides whether that is possible at all: given two copies of one mesh, find the rigid transform
/// between them, then check it reproduces EVERY vertex, normal and texture coordinate of the copy.</para>
/// <para><b>The verifier is the deliverable, not the derivation.</b> A transform that is nearly right is the
/// worst outcome available here: the colour pass and the depth prepass would compute <c>uVP * model * pos</c>
/// from different arithmetic for a de-duped part and its un-de-duped neighbour, and #119's LEQUAL match turns
/// a sub-ULP disagreement into silent dropped fragments -- while a tileset seam between one de-duped tile and
/// one rejected one is #221's white lines again. So a part is de-duped only where the reproduction is
/// EXACT, rejection is always available, and every rejection is counted by reason rather than shrugged off.
/// </para>
/// <para>Correspondence is assumed to be vertex ORDER (two copies of one model are emitted the same way) and
/// then verified like everything else -- a copy whose vertices were reordered simply fails and is rejected.
/// </para></summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>#244: the reproduction tolerance, RELATIVE to the magnitude of the coordinate being checked.
    /// <para>It was absolute (1e-5 world units) and that was a mistake worth naming: an NWN area spans a few
    /// hundred units, and float32's ULP at 320 is already ~3,8e-5 -- four times the bar. Demanding agreement
    /// finer than a float can represent made genuinely congruent copies of DISTANT geometry fail by
    /// construction, and they were being counted as different meshes. A tolerance has to be expressible in
    /// the arithmetic it judges.</para>
    /// <para><see cref="RelEpsilon"/> is a few ULP of float32; <see cref="AbsEpsilon"/> is the floor near the
    /// origin, where relative tolerance would collapse to nothing. This is still a tight bar -- it is the
    /// bar float32 can actually meet -- and #119's depth match is why it stays tight rather than becoming a
    /// convenient one.</para></summary>
    private const float RelEpsilon = 8f * 1.1920929e-7f, AbsEpsilon = 1e-5f;

    /// <summary>The tolerance to judge a coordinate of this magnitude by. Both sides are passed so a
    /// comparison near zero on one side but not the other is judged by the larger.</summary>
    private static float Tol(float a, float b)
        => MathF.Max(AbsEpsilon, RelEpsilon * MathF.Max(MathF.Abs(a), MathF.Abs(b)));
    /// <summary>#244: how close a rotation entry must sit to 0 or +/-1 to be snapped to it. A tile's
    /// orientation is a quarter turn, so its rotation matrix is a signed permutation and every entry should
    /// land on one of the three; snapping makes <c>R * v</c> an exact sign flip and a copy rather than a
    /// multiply-add chain, which is what lets the reproduction be exact at all. A rotation that will not
    /// snap is left alone and simply fails the verify.</summary>
    private const float SnapTolerance = 1e-4f;
    /// <summary>#244: how close two rigid invariants (a centroid distance, an anchor separation) must agree
    /// for a copy vertex to be tried as an anchor match. Looser than the reproduction tolerance on purpose --
    /// it only has to narrow the candidate set, and the whole-mesh congruence test is what actually
    /// decides.</summary>
    private const float MatchTolerance = 1e-3f;
    /// <summary>#244: the anchor pairs an unordered match will try before giving up, and the vertex count
    /// past which it does not start. A symmetric mesh -- a disc, a regular ring -- makes every vertex an
    /// anchor candidate, and the congruence test is quadratic, so an uncapped search would spend the
    /// enrollment on the shapes where any fit would have worked. ponytail: both are budget, not correctness;
    /// raise them if the reorderable count stops falling before the cap is reached.</summary>
    private const int MatchTrials = 24, MaxMatchVerts = 1024;
    /// <summary>#244: how many distinct meshes one rigid-key run may turn out to contain. The key screens on
    /// a 16-vertex prefix, so a run is a bucket of candidates rather than a mesh; this bounds the clustering
    /// that separates them. A run needing more than this reports its overflow as unmatched rather than
    /// silently keeping fewer meshes than the content has -- a cap must never look like a finding.</summary>
    private const int MaxReps = 16;

    /// <summary>Outcomes of <see cref="TryMatch"/>. Non-negative values are matches (and index nothing --
    /// they are just distinguishable); negative values are the two ways it can fail, kept apart because a
    /// UV/normal difference is permanent while a failed fit may just be the wrong representative.</summary>
    private const int MatchDirect = 0, MatchMirrored = 1, MatchReordered = 2, MatchNone = -1, MatchAttr = -2;

    /// <summary>Phase A outcomes, per repeated part. The first two are SUCCESSES -- a placement was found
    /// that reproduces the copy exactly, directly or through a reflection -- and the rest are the reasons a
    /// candidate was turned down.
    /// <para><c>_dedupOrder</c> is the one that decides how much is left on the table: those copies agree on
    /// their whole-mesh distance multiset but not vertex for vertex, which means the geometry repeats and only
    /// the CORRESPONDENCE is unknown. Recoverable by matching vertices instead of assuming index order.
    /// <c>_dedupResidual</c> is what remains after that, and is the honest ceiling -- shapes that collided in
    /// the prefix screen and are simply not the same mesh.</para></summary>
    private static int _dedupExact, _dedupMirror, _dedupOrder, _dedupResidual, _dedupAttr, _dedupUnusable;
    /// <summary>#244: copies proved congruent only after dropping the index-order assumption -- verified
    /// against every vertex, UV and normal by <see cref="Congruent"/>, so they are collapsible on exactly the
    /// same terms as a direct fit. Counted apart to show what the matcher is worth; <c>_dedupOrder</c> is
    /// what it still could not close.</summary>
    private static int _dedupReordered;
    /// <summary>#244: why the unordered matcher gave up, so "unmatched" is a diagnosis rather than a shrug.
    /// Too big and trials-exhausted are BUDGET (the caps), no-candidate means no copy vertex carried the
    /// canonical's anchor invariants at all, and incongruent means pairs were fitted and the whole-mesh test
    /// turned them down -- only the last is evidence the copies are genuinely different geometry.</summary>
    private static int _mfTooBig, _mfNoAnchor, _mfNoCandidate, _mfTrials, _mfIncongruent;
    /// <summary>#244: representatives kept -- the number of meshes that would actually have to be STORED for
    /// the repeated population. Members over representatives is the de-duplication factor, and unlike the
    /// rigid-key ratio it is a proven one: every member counted against a representative was verified
    /// vertex, UV and normal.</summary>
    private static int _dedupReps;

    /// <summary>Derive and verify a placement for every repeated part, given the census's rigid grouping and
    /// the mapped position/UV/normal streams. Runs inside <see cref="HashVertices"/>'s maps, so no buffer is
    /// re-fetched; the population is the same one the census keyed.</summary>
    private static void VerifyDedup(int n)
    {
        _dedupExact = _dedupMirror = _dedupOrder = _dedupResidual = _dedupAttr = _dedupUnusable = 0;
        _dedupReordered = 0;
        _mfTooBig = _mfNoAnchor = _mfNoCandidate = _mfTrials = _mfIncongruent = 0;
        _dedupReps = 0;
        _ddLive = _ddGated = 0;
        // Rebuilt from scratch: after an enrollment a slot is a different part, so an entry that survived
        // would point a stranger's draw at a stranger's geometry.
        for (int k = 0; k < _ownCount; k++) _ddRep[k] = -1;
        for (int a = 0; a < n; )
        {
            int b = a;
            while (b < n && _msRigidKey[b] == _msRigidKey[a]) b++;
            if (b - a > 1)
            {
                // The canonical copy is the first member that has usable streams; the rest are measured
                // against it. Which one is canonical does not matter for the count -- the relation is
                // symmetric -- and phase B will pick by residency rather than by census order.
                // #244: a run of the rigid key is a bucket of CANDIDATES, not a mesh. The key screens on a
                // 16-vertex prefix, so one run routinely holds several genuinely different shapes -- and
                // measuring every member against a single canonical then reports the other shapes'
                // members as incongruent, which is a property of the comparison, not of the content.
                // So the run is CLUSTERED: a part that matches no current representative becomes one.
                // <para>This is phase B's storage plan falling out of phase A's verifier -- the
                // representative set IS the set of meshes that would have to be kept, and the ratio of
                // members to representatives IS the de-duplication factor.</para>
                int* reps = stackalloc int[MaxReps];
                int nrep = 0;
                float* r = stackalloc float[9];
                float* t = stackalloc float[3];
                for (int k = a; k < b; k++)
                {
                    int i = _msIdx[k];
                    if (!Usable(i)) { _dedupUnusable++; continue; }
                    int outcome = MatchNone, hit = -1;
                    for (int q = 0; q < nrep && hit < 0; q++)
                    {
                        if (_msVerts[i] != _msVerts[reps[q]]) continue;
                        int o = TryMatch(reps[q], i, r, t);
                        if (o >= 0) { outcome = o; hit = q; }
                        else if (o == MatchAttr) outcome = MatchAttr;
                    }
                    if (hit >= 0)
                    {
                        if (outcome == MatchDirect) _dedupExact++;
                        else if (outcome == MatchMirrored) _dedupMirror++;
                        else _dedupReordered++;
                        Register(reps[hit], i, r, t);
                        continue;
                    }
                    // No representative fitted. A new one is not a failure -- it is a mesh this run turned
                    // out to contain -- until the cap, past which the part is reported as unmatched so the
                    // cap can never masquerade as content.
                    if (nrep < MaxReps) { reps[nrep++] = i; _dedupReps++; continue; }
                    if (outcome == MatchAttr) _dedupAttr++; else Unmatched(reps[0], i);
                }
            }
            a = b;
        }
    }

    /// <summary>#244 phase B(1): record that this part can draw <paramref name="rep"/>'s geometry under
    /// <paramref name="r"/>/<paramref name="t"/> -- unless a gate says it must not, whatever the geometry
    /// proved. The verify answers "is this the same mesh"; these answer "is sharing it safe", and they are
    /// different questions.
    /// <para><b>Animated sources.</b> Dangly and morph-animated meshes move VERTICES under a still matrix,
    /// and both commit through <c>CommitItemDynamicData</c> into <see cref="OwnedBuffers.DirtySince"/>. A
    /// part sharing an animating mesh would wave in unison with every sibling, so either side being dirty
    /// disqualifies the pair. A skinned part is refused for the same reason a frame earlier -- its vertices
    /// are posed per frame.</para>
    /// <b>Primitive mode.</b> The rigid key carries the element count but not the mode, and the same
    /// vertices drawn as a strip and as a list are not the same draw.</summary>
    private static void Register(int rep, int i, float* r, float* t)
    {
        // #244: BOTH indices are census entries here, and the registry is keyed by ownership SLOT -- the two
        // diverge wherever the census skipped a part. Everything reached through the ownership table
        // (_oBone, _oPrim, and the registry itself) goes through _msSlot; everything in the _ms* arrays does
        // not. Crossing them silently is the one mistake this comment exists to stop repeating.
        int si = _msSlot[i], sr = _msSlot[rep];
        if (_oBone[si] >= 0 || _oBone[sr] >= 0
            || _oPrim[si] != _oPrim[sr] || _msCount[i] != _msCount[rep]
            || OwnedBuffers.DirtySince(_vPos[_msVao[i]].Buf)
            || OwnedBuffers.DirtySince(_vPos[_msVao[rep]].Buf))
        { _ddGated++; return; }
        _ddRep[si] = sr;
        // Row-major 3x4: the rotation with the translation as a fourth column, which is the shape the record
        // stage wants. The fourth ROW is implicit (0,0,0,1) -- a placement is affine and never projective.
        long o = (long)si * 12;
        for (int row = 0; row < 3; row++)
        {
            _ddMat[o + row * 4] = r[row * 3];
            _ddMat[o + row * 4 + 1] = r[row * 3 + 1];
            _ddMat[o + row * 4 + 2] = r[row * 3 + 2];
            _ddMat[o + row * 4 + 3] = t[row];
        }
        _ddLive++;
    }

    /// <summary>Does this part have every stream the verify reads, on a slot whose buffers were mapped?
    /// A part missing one is not a failed candidate, it is one that was never testable -- counted apart so
    /// the reject reasons stay about geometry.</summary>
    private static bool Usable(int i)
    {
        int v = _msVao[i];
        if (v < 0 || v >= MaxVdos || _msVerts[i] < 3) return false;
        return _mapPos[v] != null && _mapUv[v] != null;
    }

    /// <summary>Derive the transform between two copies and account for the result. Positions go through the
    /// full matrix, normals through its rotation alone, and UVs must match bitwise -- two identical quads
    /// with different texture coordinates are different meshes, and the position hash cannot see that.
    /// </summary>
    private static int TryMatch(int c, int i, float* r, float* t)
    {
        if (!Derive(c, i, r, t, mirror: false)) return MatchNone;
        bool mirrored = false;
        if (!Reproduces(c, i, r, t))
        {
            // ...and if the rotation does not fit, try the REFLECTION of it before giving up. Both frames
            // below are built right-handed (their third axis is a cross product), so a mirrored copy cannot
            // be expressed by the first attempt at all -- it fits the two directions the frame was built
            // from and misses everything off their plane. Retrying with the third axis negated is what tells
            // "mirrored" apart from "not recoverable", and the two have different futures: a reflected copy
            // can still share vertices if the store carries the flip, at the price of reversed winding.
            if (!Derive(c, i, r, t, mirror: true) || !Reproduces(c, i, r, t))
            {
                // Neither ordering-assuming derivation fitted. Before calling it a different mesh, try the
                // match that does NOT assume the two copies list their vertices alike -- which is ~90% of
                // what phase A first turned down, and is fully shareable because the copy's ordering ceases
                // to exist once it draws the canonical's buffer.
                if (!MatchUnordered(c, i, r, t)) return MatchNone;
                return MatchReordered;
            }
            mirrored = true;
        }
        int v = _msVao[c];
        VertexStream ps = _vPos[v], us = _vUv[v], ns = _vNrm[v];
        int verts = _msVerts[c];
        // UVs bitwise: they are not transformed, so any difference at all means the two copies cannot share
        // a vertex buffer however well their positions agree.
        byte* uc = _mapUv[v] + us.Off + (long)_msBase[c] * us.Stride;
        byte* ui = _mapUv[_msVao[i]] + us.Off + (long)_msBase[i] * us.Stride;
        for (int k = 0; k < verts; k++)
        {
            float* a = (float*)(uc + (long)k * us.Stride);
            float* b = (float*)(ui + (long)k * us.Stride);
            if (a[0] != b[0] || a[1] != b[1]) return MatchAttr;
        }
        // ...and the normals through the rotation alone. A lit part whose normals do not survive the
        // transform would shade wrong while drawing in exactly the right place, which is the failure least
        // likely to be noticed and most likely to be blamed on lighting.
        if (ns.Present && _mapNrm[v] != null && _mapNrm[_msVao[i]] != null)
        {
            byte* nc = _mapNrm[v] + ns.Off + (long)_msBase[c] * ns.Stride;
            byte* ni = _mapNrm[_msVao[i]] + ns.Off + (long)_msBase[i] * ns.Stride;
            for (int k = 0; k < verts; k++)
            {
                float* a = (float*)(nc + (long)k * ns.Stride);
                float* b = (float*)(ni + (long)k * ns.Stride);
                float x = r[0] * a[0] + r[1] * a[1] + r[2] * a[2];
                float y = r[3] * a[0] + r[4] * a[1] + r[5] * a[2];
                float z = r[6] * a[0] + r[7] * a[1] + r[8] * a[2];
                // Unit-length normals, so the absolute floor is the right bar -- a relative term would
                // only ever shrink it.
                if (MathF.Abs(x - b[0]) > AbsEpsilon || MathF.Abs(y - b[1]) > AbsEpsilon
                    || MathF.Abs(z - b[2]) > AbsEpsilon) return MatchAttr;
            }
        }
        return mirrored ? MatchMirrored : MatchDirect;
    }

    /// <summary>Build the rotation and translation taking copy <paramref name="c"/> onto copy
    /// <paramref name="i"/>, from two vertex pairs and the meshes' centroids. Orthonormal frames rather than
    /// a least-squares fit: the transform being looked for is a placement, so it is exactly recoverable from
    /// two independent directions, and a fit would return a nearly-right answer where this returns none.
    /// <para>Entries are then SNAPPED to 0 / +/-1 where they are within <see cref="SnapTolerance"/>. A quarter
    /// turn's matrix is a signed permutation, and snapping it makes the reproduction a sign flip instead of a
    /// three-term dot product -- which is how the verify above can demand near-exactness and get it.</para>
    /// </summary>
    private static bool Derive(int c, int i, float* r, float* t, bool mirror)
    {
        int v = _msVao[c];
        // #244: EACH side reads through its OWN stream. The rigid key drops the VDO on purpose -- a model
        // repeated across buffers is still one model -- so a pair straddling two VDOs is ordinary, and those
        // VDOs need not share a vertex format. Reading the copy at the canonical's stride and offset walks
        // the wrong addresses and fails every comparison, whatever the geometry does.
        VertexStream ps = _vPos[v], qs = _vPos[_msVao[i]];
        byte* pc = _mapPos[v] + ps.Off + (long)_msBase[c] * ps.Stride;
        byte* pi = _mapPos[_msVao[i]] + qs.Off + (long)_msBase[i] * qs.Stride;
        int verts = _msVerts[c];
        float* cc = stackalloc float[3];
        float* ci = stackalloc float[3];
        Centroid(pc, ps.Stride, verts, cc);
        Centroid(pi, qs.Stride, verts, ci);
        // Two directions off the centroid that are independent enough to span a frame. Scanned rather than
        // assumed: a mesh's first vertices can be collinear with its centre (a fan, a strip), and a frame
        // built from those is degenerate in a way the snap would happily hide.
        float* a1 = stackalloc float[3]; float* a2 = stackalloc float[3];
        float* b1 = stackalloc float[3]; float* b2 = stackalloc float[3];
        int k1 = -1, k2 = -1;
        for (int k = 0; k < verts && k2 < 0; k++)
        {
            float* a = (float*)(pc + (long)k * ps.Stride);
            float dx = a[0] - cc[0], dy = a[1] - cc[1], dz = a[2] - cc[2];
            if (dx * dx + dy * dy + dz * dz < 1e-8f) continue;
            if (k1 < 0) { k1 = k; a1[0] = dx; a1[1] = dy; a1[2] = dz; continue; }
            float cx = a1[1] * dz - a1[2] * dy, cy = a1[2] * dx - a1[0] * dz, cz = a1[0] * dy - a1[1] * dx;
            if (cx * cx + cy * cy + cz * cz < 1e-8f) continue;
            k2 = k; a2[0] = dx; a2[1] = dy; a2[2] = dz;
        }
        if (k2 < 0) return false;
        float* bp1 = (float*)(pi + (long)k1 * qs.Stride);
        float* bp2 = (float*)(pi + (long)k2 * qs.Stride);
        for (int j = 0; j < 3; j++) { b1[j] = bp1[j] - ci[j]; b2[j] = bp2[j] - ci[j]; }
        float* fa = stackalloc float[9];
        float* fb = stackalloc float[9];
        if (!Frame(a1, a2, fa) || !Frame(b1, b2, fb)) return false;
        // The reflected candidate: negate the target frame's third axis, which is the only axis the two
        // source directions do not pin. R then carries determinant -1 and maps the copy onto its mirror.
        if (mirror) { fb[2] = -fb[2]; fb[5] = -fb[5]; fb[8] = -fb[8]; }
        // R = Fb * Fa^T -- both frames are orthonormal, so the transpose is the inverse.
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                float sum = 0f;
                for (int m = 0; m < 3; m++) sum += fb[m * 3 + row] * fa[m * 3 + col];
                r[row * 3 + col] = Snap(sum);
            }
        // Translation off a single ANCHOR VERTEX, not the centroid. A centroid is a sum over every vertex,
        // so its error grows with the mesh; an anchor is one exact coordinate on each side, and the
        // reproduction is only ever as good as the translation it is built on.
        AnchorT(pc, ps.Stride, k1, pi, qs.Stride, k1, r, t);
        return true;
    }

    /// <summary>t = b - R a, from one vertex on each side.</summary>
    private static void AnchorT(byte* pc, int sc, int kc, byte* pi, int si, int ki, float* r, float* t)
    {
        float* a = (float*)(pc + (long)kc * sc);
        float* b = (float*)(pi + (long)ki * si);
        for (int j = 0; j < 3; j++)
            t[j] = b[j] - (r[j * 3] * a[0] + r[j * 3 + 1] * a[1] + r[j * 3 + 2] * a[2]);
    }

    /// <summary>Neither the rotation nor its reflection fitted. Split the two cases that look identical from
    /// here and are worlds apart in what they cost to recover: if the two copies agree on their WHOLE-MESH
    /// distance multiset, the geometry does repeat and only the vertex correspondence is unknown -- this
    /// pass assumes index order, and a copy emitted in another order fails it while being perfectly
    /// shareable. If they disagree, the pair only ever collided in the 16-vertex prefix screen and is not
    /// the same mesh at all.</summary>
    private static void Unmatched(int c, int i)
    {
        if (_msFull[c] == 0 || _msFull[c] != _msFull[i]) { _dedupResidual++; return; }
        _dedupOrder++;
    }

    /// <summary>#244: derive a placement WITHOUT assuming the two copies list their vertices in the same
    /// order. Index-order correspondence accounts for ~90% of what phase A first turned down, and those
    /// copies are congruent -- only the ordering differs, which no runtime path ever sees: a de-duped part
    /// draws the CANONICAL vertices and the CANONICAL indices under the derived transform, so the copy's own
    /// ordering stops existing the moment it is collapsed. Correspondence is needed to prove congruence and
    /// to derive the matrix, and nowhere else.
    /// <para>Two anchors are chosen on the canonical -- the vertex furthest from the centroid, then the one
    /// most independent of it -- and matched against copy vertices by RIGID INVARIANTS: distance to the
    /// centroid, and for the second anchor also distance to the first. Those hold under any placement, so
    /// the candidate set is small; each pair is tried and the first that reproduces the whole mesh wins.
    /// </para>
    /// <para>Trials are capped (<see cref="MatchTrials"/>): a mesh whose vertices are all equidistant --
    /// a disc, a regular ring -- has every vertex as a candidate, and an uncapped search would be quadratic
    /// in the candidate set for a shape that is symmetric enough that any fit works anyway.</para></summary>
    private static bool MatchUnordered(int c, int i, float* r, float* t)
    {
        VertexStream ps = _vPos[_msVao[c]], qs = _vPos[_msVao[i]];
        byte* pc = _mapPos[_msVao[c]] + ps.Off + (long)_msBase[c] * ps.Stride;
        byte* pi = _mapPos[_msVao[i]] + qs.Off + (long)_msBase[i] * qs.Stride;
        int verts = _msVerts[c];
        if (verts > MaxMatchVerts) { _mfTooBig++; return false; }
        float* cc = stackalloc float[3];
        float* ci = stackalloc float[3];
        Centroid(pc, ps.Stride, verts, cc);
        Centroid(pi, qs.Stride, verts, ci);
        // anchor 1: furthest from the centroid, which is the vertex least likely to have a twin at the same
        // radius and so the one with the smallest candidate set.
        int k1 = -1; float best = 0f;
        for (int k = 0; k < verts; k++)
        {
            float d = Dist(pc, ps.Stride, k, cc);
            if (d > best) { best = d; k1 = k; }
        }
        if (k1 < 0 || best < 1e-4f) { _mfNoAnchor++; return false; }
        // anchor 2: most independent of anchor 1, so the frame the pair spans is well conditioned.
        float* a1 = stackalloc float[3];
        Rel(pc, ps.Stride, k1, cc, a1);
        int k2 = -1; float bestc = 0f;
        float* tmp = stackalloc float[3];
        for (int k = 0; k < verts; k++)
        {
            if (k == k1) continue;
            Rel(pc, ps.Stride, k, cc, tmp);
            float cx = a1[1] * tmp[2] - a1[2] * tmp[1];
            float cy = a1[2] * tmp[0] - a1[0] * tmp[2];
            float cz = a1[0] * tmp[1] - a1[1] * tmp[0];
            float m = cx * cx + cy * cy + cz * cz;
            if (m > bestc) { bestc = m; k2 = k; }
        }
        if (k2 < 0 || bestc < 1e-8f) { _mfNoAnchor++; return false; }
        float d1 = best, d2 = Dist(pc, ps.Stride, k2, cc), d12 = DistVV(pc, ps.Stride, k1, k2);
        int trials = 0;
        for (int b1 = 0; b1 < verts; b1++)
        {
            if (MathF.Abs(Dist(pi, qs.Stride, b1, ci) - d1) > MatchTolerance) continue;
            for (int b2 = 0; b2 < verts; b2++)
            {
                if (b2 == b1) continue;
                if (MathF.Abs(Dist(pi, qs.Stride, b2, ci) - d2) > MatchTolerance) continue;
                // ...and the distance BETWEEN the two anchors, which is what pins the pair rather than
                // merely putting each on the right sphere.
                if (MathF.Abs(DistVV(pi, qs.Stride, b1, b2) - d12) > MatchTolerance) continue;
                if (++trials > MatchTrials) { _mfTrials++; return false; }
                if (!FrameFit(pc, pi, ps.Stride, qs.Stride, cc, ci, k1, k2, b1, b2, r, t)) continue;
                if (Congruent(c, i, r, t)) return true;
            }
        }
        if (trials == 0) _mfNoCandidate++; else _mfIncongruent++;
        return false;
    }

    /// <summary>Build the transform taking the canonical's two anchors onto the copy's candidate pair.</summary>
    private static bool FrameFit(byte* pc, byte* pi, int strideC, int strideI, float* cc, float* ci,
                                 int k1, int k2, int b1, int b2, float* r, float* t)
    {
        float* a1 = stackalloc float[3]; float* a2 = stackalloc float[3];
        float* v1 = stackalloc float[3]; float* v2 = stackalloc float[3];
        Rel(pc, strideC, k1, cc, a1); Rel(pc, strideC, k2, cc, a2);
        Rel(pi, strideI, b1, ci, v1); Rel(pi, strideI, b2, ci, v2);
        float* fa = stackalloc float[9];
        float* fb = stackalloc float[9];
        if (!Frame(a1, a2, fa) || !Frame(v1, v2, fb)) return false;
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                float sum = 0f;
                for (int m = 0; m < 3; m++) sum += fb[m * 3 + row] * fa[m * 3 + col];
                r[row * 3 + col] = Snap(sum);
            }
        AnchorT(pc, strideC, k1, pi, strideI, b1, r, t);
        return true;
    }

    /// <summary>Every canonical vertex, transformed, lands on SOME copy vertex -- and the copy vertex it
    /// lands on carries the same UV and normal. Order-free, so it is the congruence test the ordered
    /// <see cref="Reproduces"/> cannot be. Quadratic in the vertex count, which is why the population is
    /// capped at <see cref="MaxMatchVerts"/> and this runs on the enrollment cadence behind a dial.
    /// <para>Attributes are checked at the MATCHED vertex, not the same index: a de-duped part will draw the
    /// canonical's UVs and normals, so what has to hold is that the copy agreed with them wherever the two
    /// meshes coincide.</para></summary>
    private static bool Congruent(int c, int i, float* r, float* t)
    {
        int vc = _msVao[c], vi = _msVao[i];
        VertexStream ps = _vPos[vc], us = _vUv[vc], ns = _vNrm[vc];
        VertexStream qs = _vPos[vi], vs = _vUv[vi], ms = _vNrm[vi];
        byte* pc = _mapPos[vc] + ps.Off + (long)_msBase[c] * ps.Stride;
        byte* pi = _mapPos[vi] + qs.Off + (long)_msBase[i] * qs.Stride;
        byte* uc = _mapUv[vc] + us.Off + (long)_msBase[c] * us.Stride;
        byte* ui = _mapUv[vi] + vs.Off + (long)_msBase[i] * vs.Stride;
        bool nrm = ns.Present && ms.Present && _mapNrm[vc] != null && _mapNrm[vi] != null;
        byte* nc = nrm ? _mapNrm[vc] + ns.Off + (long)_msBase[c] * ns.Stride : null;
        byte* ni = nrm ? _mapNrm[vi] + ms.Off + (long)_msBase[i] * ms.Stride : null;
        int verts = _msVerts[c];
        for (int k = 0; k < verts; k++)
        {
            float* a = (float*)(pc + (long)k * ps.Stride);
            float x = r[0] * a[0] + r[1] * a[1] + r[2] * a[2] + t[0];
            float y = r[3] * a[0] + r[4] * a[1] + r[5] * a[2] + t[1];
            float z = r[6] * a[0] + r[7] * a[1] + r[8] * a[2] + t[2];
            int hit = -1;
            for (int m = 0; m < verts && hit < 0; m++)
            {
                float* b = (float*)(pi + (long)m * qs.Stride);
                if (MathF.Abs(x - b[0]) <= Tol(x, b[0]) && MathF.Abs(y - b[1]) <= Tol(y, b[1])
                    && MathF.Abs(z - b[2]) <= Tol(z, b[2])) hit = m;
            }
            if (hit < 0) return false;
            float* ua = (float*)(uc + (long)k * us.Stride);
            float* ub = (float*)(ui + (long)hit * vs.Stride);
            if (ua[0] != ub[0] || ua[1] != ub[1]) return false;
            if (nrm)
            {
                float* na = (float*)(nc + (long)k * ns.Stride);
                float* nb = (float*)(ni + (long)hit * ms.Stride);
                float nx = r[0] * na[0] + r[1] * na[1] + r[2] * na[2];
                float ny = r[3] * na[0] + r[4] * na[1] + r[5] * na[2];
                float nz = r[6] * na[0] + r[7] * na[1] + r[8] * na[2];
                // Normals are unit-length, so the absolute floor is the right bar for them -- the relative
                // term would only ever shrink it.
                if (MathF.Abs(nx - nb[0]) > AbsEpsilon || MathF.Abs(ny - nb[1]) > AbsEpsilon
                    || MathF.Abs(nz - nb[2]) > AbsEpsilon) return false;
            }
        }
        return true;
    }

    private static float Dist(byte* p, int stride, int k, float* c)
    {
        float* v = (float*)(p + (long)k * stride);
        float dx = v[0] - c[0], dy = v[1] - c[1], dz = v[2] - c[2];
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static float DistVV(byte* p, int stride, int k, int m)
    {
        float* a = (float*)(p + (long)k * stride);
        float* b = (float*)(p + (long)m * stride);
        float dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static void Rel(byte* p, int stride, int k, float* c, float* o)
    {
        float* v = (float*)(p + (long)k * stride);
        o[0] = v[0] - c[0]; o[1] = v[1] - c[1]; o[2] = v[2] - c[2];
    }

    /// <summary>Does this transform put every vertex of the canonical copy onto the corresponding vertex of
    /// the other, within <see cref="Tol"/>? The whole mesh, not the 16-vertex prefix the rigid hash
    /// screened on -- prefix-agreeing, later-diverging meshes are precisely what this exists to reject.
    /// </summary>
    private static bool Reproduces(int c, int i, float* r, float* t)
    {
        VertexStream ps = _vPos[_msVao[c]], qs = _vPos[_msVao[i]];
        byte* pc = _mapPos[_msVao[c]] + ps.Off + (long)_msBase[c] * ps.Stride;
        byte* pi = _mapPos[_msVao[i]] + qs.Off + (long)_msBase[i] * qs.Stride;
        for (int k = 0; k < _msVerts[c]; k++)
        {
            float* a = (float*)(pc + (long)k * ps.Stride);
            float* b = (float*)(pi + (long)k * qs.Stride);
            float x = r[0] * a[0] + r[1] * a[1] + r[2] * a[2] + t[0];
            float y = r[3] * a[0] + r[4] * a[1] + r[5] * a[2] + t[1];
            float z = r[6] * a[0] + r[7] * a[1] + r[8] * a[2] + t[2];
            if (MathF.Abs(x - b[0]) > Tol(x, b[0]) || MathF.Abs(y - b[1]) > Tol(y, b[1])
                || MathF.Abs(z - b[2]) > Tol(z, b[2])) return false;
        }
        return true;
    }

    /// <summary>Gram-Schmidt an orthonormal frame from two independent directions, columns in
    /// <paramref name="f"/>. False when the pair turns out to be degenerate after all.</summary>
    private static bool Frame(float* u, float* w, float* f)
    {
        float* e0 = stackalloc float[3];
        float* e1 = stackalloc float[3];
        float* e2 = stackalloc float[3];
        if (!Normalize(u, e0)) return false;
        float d = e0[0] * w[0] + e0[1] * w[1] + e0[2] * w[2];
        float* tmp = stackalloc float[3] { w[0] - d * e0[0], w[1] - d * e0[1], w[2] - d * e0[2] };
        if (!Normalize(tmp, e1)) return false;
        e2[0] = e0[1] * e1[2] - e0[2] * e1[1];
        e2[1] = e0[2] * e1[0] - e0[0] * e1[2];
        e2[2] = e0[0] * e1[1] - e0[1] * e1[0];
        for (int j = 0; j < 3; j++) { f[j * 3] = e0[j]; f[j * 3 + 1] = e1[j]; f[j * 3 + 2] = e2[j]; }
        return true;
    }

    private static bool Normalize(float* v, float* o)
    {
        float len = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        if (len < 1e-6f) return false;
        o[0] = v[0] / len; o[1] = v[1] / len; o[2] = v[2] / len;
        return true;
    }

    private static void Centroid(byte* p, int stride, int verts, float* o)
    {
        o[0] = o[1] = o[2] = 0f;
        for (int k = 0; k < verts; k++)
        {
            float* v = (float*)(p + (long)k * stride);
            o[0] += v[0]; o[1] += v[1]; o[2] += v[2];
        }
        o[0] /= verts; o[1] /= verts; o[2] /= verts;
    }

    private static float Snap(float x)
    {
        if (MathF.Abs(x) < SnapTolerance) return 0f;
        if (MathF.Abs(x - 1f) < SnapTolerance) return 1f;
        if (MathF.Abs(x + 1f) < SnapTolerance) return -1f;
        return x;
    }

}
