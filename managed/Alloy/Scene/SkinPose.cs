using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>#262: a skinned part's bone matrices, solved from the engine's ANIMATION STATE rather than
/// produced as a side effect of re-entering <c>PartSkin::Draw</c>.
/// <para>This is a line-for-line port of the engine's own solve -- <c>RenderSkinned</c> @0x482c70,
/// <c>GatherBones</c> @0x482b10 and the matrix half of <c>ExecuteSkinGPU</c> @0x482800 -- reading the same
/// fields at a moment Alloy chooses. Everything it touches (<c>Part+0x10</c>/<c>+0x1c</c>, the child array,
/// the parent back-pointer, the <c>MdlNodeSkin</c> bind arrays) is state the engine's animation step wrote;
/// nothing here draws, and no engine function is called at all. The output is byte-identical to what the
/// engine would have left in <c>GLRender::m_bones</c>, which is what lets the vertex stages stay unchanged
/// (see <see cref="SymbolMap.GlRenderBones"/> for that layout).</para>
/// <para>The engine ran this from inside the draw purely because that was where it needed the answer. Alloy
/// runs it in the frame preamble instead, which is the whole point of #262: reading a pose and re-entering a
/// draw function to produce one are different things, and only the second was vanilla-shaped.</para></summary>
internal static unsafe class SkinPose
{
    /// <summary>Bone-tree nodes one solve may gather. <c>GatherBones</c> walks the WHOLE part tree of the
    /// creature the skinmesh hangs on, not just the 64 bones the mesh addresses, so this bounds the tree and
    /// not the bone map. A humanoid gathers well under a hundred; the ceiling is counted, never silent.</summary>
    private const int MaxGathered = 512;
    /// <summary>Guard for BOTH walks -- the DFS down and the parent chain up. A part tree is a tree by
    /// construction (<c>Part+0x48</c> is the one back-pointer and <c>Part+0x38</c> the one child array), so
    /// this can only bite on a corrupt one -- which is exactly the case where an unbounded walk is a stack
    /// overflow or a hung render thread instead of a refusal. The engine's own loops are unbounded; being
    /// faithful to that is not worth a hang, and <c>IdentityOk</c> only clears one level.</summary>
    private const int MaxDepth = 64;

    private static readonly float[] _gPos = new float[MaxGathered * 3];
    private static readonly float[] _gRot = new float[MaxGathered * 4];
    private static int _gCount, _gDepth;
    private static bool _gFull;

    /// <summary>Solves refused for want of prepared bone data, and tree walks that hit
    /// <see cref="MaxGathered"/>. Both mean the caller must hand the part back to the engine.</summary>
    internal static long NoBind, Overflow;

    /// <summary>Solve <paramref name="part"/>'s pose into <paramref name="dst"/>, which must have room for
    /// <see cref="AlloyRenderer.BoneFloats"/> floats (64 mat4). False = no pose; the part has to be handed
    /// back rather than drawn in its bind pose -- and since #264 that means NOT DRAWN at all.
    /// <para>A skinmesh whose <c>MdlNodeSkin</c> has not been prepared is the case #284 had to close:
    /// <c>PartSkin::PostProcess</c> @0x483120 builds the bind arrays and the bone map lazily, and its only
    /// caller is <c>RenderSkinned</c> @0x482c70 -- an ENGINE DRAW of the part, which #264 deleted. Nothing
    /// re-drives it, so an unprepared node stayed unprepared for the session and its creature was a permanent
    /// hole. The refusal is still the honest answer for this frame; what changed is that the part is
    /// <see cref="NoteUnprepared"/>ed here and <see cref="DripPrepares"/> runs the repair on an Alloy edge,
    /// so the next frame solves.</para></summary>
    internal static bool Solve(IntPtr part, float* dst)
    {
        IntPtr node = *(IntPtr*)(part + AlloyRenderer.PartMdlNode);
        // No node is not a repairable state -- PostProcess dereferences it unguarded -- so it is never noted.
        if (node == IntPtr.Zero) { NoBind++; return false; }
        int bones = *(short*)(node + EngineLayout.SkinBoneCount);
        IntPtr bindRot = *(IntPtr*)(node + EngineLayout.SkinBindRot);
        IntPtr bindPos = *(IntPtr*)(node + EngineLayout.SkinBindPos);
        if (bones <= 0 || bindRot == IntPtr.Zero || bindPos == IntPtr.Zero)
        { NoBind++; NoteUnprepared(part); return false; }

        // RenderSkinned's first act: the inverse of the root -> this-part chain, accumulated from the LEAF
        // upward, which is what makes the gathered transforms land in the skinmesh's own space.
        float* p = (float*)(part + EngineLayout.PartPos);
        float* q = (float*)(part + EngineLayout.PartRot);
        QConj(q, out float aw, out float ax, out float ay, out float az);
        QRot(aw, ax, ay, az, -p[0], -p[1], -p[2], out float px, out float py, out float pz);
        IntPtr root = part;
        int rise = 0;
        for (IntPtr up = *(IntPtr*)(part + EngineLayout.PartParent); up != IntPtr.Zero;
             up = *(IntPtr*)(up + EngineLayout.PartParent))
        {
            if (++rise > MaxDepth) { Overflow++; return false; }
            root = up;
            float* up_ = (float*)(up + EngineLayout.PartPos);
            QConj((float*)(up + EngineLayout.PartRot), out float cw, out float cx, out float cy, out float cz);
            QRot(cw, cx, cy, cz, -up_[0], -up_[1], -up_[2], out float tx, out float ty, out float tz);
            QRot(aw, ax, ay, az, tx, ty, tz, out float rx, out float ry, out float rz);
            px += rx; py += ry; pz += rz;
            QMul(aw, ax, ay, az, cw, cx, cy, cz, out aw, out ax, out ay, out az);
        }

        _gCount = 0; _gDepth = 0; _gFull = false;
        Gather(root, px, py, pz, aw, ax, ay, az);
        if (_gFull) { Overflow++; return false; }

        // ExecuteSkinGPU's loop. The bone map is indexed by BONE SLOT and holds an index into the gathered
        // lists; -1 (and anything past what was gathered) is an unused slot. The lists are mutated in place
        // exactly as the engine mutates them -- one gathered index is addressed by at most one slot, so the
        // difference is unobservable, but replicating it removes the question.
        short* map = (short*)(node + EngineLayout.SkinBoneMap);
        bool sawZero = false;
        fixed (float* gp = _gPos)
        fixed (float* gr = _gRot)
        for (int b = 0; b < AlloyRenderer.BoneCount; b++)
        {
            float* m = dst + b * 16;
            int g = map[b];
            if (g < 0 || g >= _gCount) { Identity(m); continue; }
            // The engine's own terminator: unused slots read 0, so the SECOND zero ends the walk. Alloy
            // fills the rest with identity rather than leaving the previous part's matrices there.
            if (g == 0 && sawZero)
            {
                for (int k = b; k < AlloyRenderer.BoneCount; k++) Identity(dst + k * 16);
                break;
            }
            if (g == 0) sawZero = true;
            float* gq = gr + g * 4;
            float* gpv = gp + g * 3;
            float* bq = (float*)(bindRot + g * 16);
            float* bp = (float*)(bindPos + g * 12);
            QRot(gq[0], gq[1], gq[2], gq[3], bp[0], bp[1], bp[2], out float ox, out float oy, out float oz);
            gpv[0] += ox; gpv[1] += oy; gpv[2] += oz;
            QMul(gq[0], gq[1], gq[2], gq[3], bq[0], bq[1], bq[2], bq[3],
                 out float mw, out float mx, out float my, out float mz);
            gq[0] = mw; gq[1] = mx; gq[2] = my; gq[3] = mz;
            Compose(mw, mx, my, mz, gpv[0], gpv[1], gpv[2], m);
        }
        return true;
    }

    /// <summary>#284: parts whose <c>MdlNodeSkin</c> is unprepared, waiting for their one repair. Entry index
    /// and the part pointer it recorded, because BOTH are needed at the drain: the index carries the
    /// one-attempt ledger and the pointer is what <c>PartTable.EntryLive</c> proves the record still means.
    /// <para>ADR 0015: a <c>Part*</c> is BORROWED and its death notice is <c>Part::~Part</c> (#145/#221),
    /// which is what leaves the tombstone <c>EntryLive</c> reads. Nothing here dereferences the pointer; the
    /// liveness proof is at the drain, immediately before the call.</para></summary>
    private static readonly int[] _prepQ = new int[PrepQueue];
    private static readonly IntPtr[] _prepPart = new IntPtr[PrepQueue];
    private static int _prepN;
    private static long _prepCalls, _prepWon, _prepNoData, _prepDead, _prepDropped;
    /// <summary>How many unprepared skinmeshes may wait at once, and how many repairs a frame may pay for.
    /// The budget is the stated cost: at most two <c>PartSkin::PostProcess</c> calls per frame, on frames
    /// where anything is queued at all, and never the same record twice.
    /// <para>Two rather than the texture drip's four because the call is heavier: it rebuilds the node's
    /// vertex indices and tangent basis and takes two <c>RenderInterface</c> pools. Vanilla paid all of it
    /// in one frame, on the first draw of every new skinmesh, so a couple a frame is not a new cost.</para></summary>
    private const int PrepQueue = 64, PrepBudget = 2;

    /// <summary>Note a part whose bind arrays are missing, for <see cref="DripPrepares"/> to repair. Cold --
    /// it runs only on a refusal, which in a settled area is zero -- and allocation-free: the queue and the
    /// ledger are both pre-sized. One attempt per RECORD, latched in <c>PartTable._pPoseTried</c>, which is
    /// also what makes a queue this small enough (an entry cannot re-enter it).</summary>
    private static void NoteUnprepared(IntPtr part)
    {
        int e = PartTable.Find(part);
        if (!PartTable.EntryLive(e, part) || !PartTable.Skinned(e) || PartTable._pPoseTried[e]) return;
        if (_prepN >= PrepQueue) { _prepDropped++; return; }
        for (int i = 0; i < _prepN; i++) if (_prepQ[i] == e) return;
        _prepQ[_prepN] = e; _prepPart[_prepN] = part; _prepN++;
    }

    /// <summary>#284's driver: spend at most <see cref="PrepBudget"/> engine skin builds, on the frame anchor
    /// where <c>OwnedTextures.DripRebuilds</c> already runs -- once a frame, before the Alloy bracket, with
    /// the ENGINE's context current. The context is load-bearing for the same reason it is there:
    /// <c>PostProcess</c> ends in <c>RenderInterface::RequestPool</c>/<c>LockPool</c>/<c>UnlockPool</c>, and
    /// running the engine's own pool traffic inside Alloy's 4.6 bracket would leave the engine's bind cache
    /// naming objects in the other context.
    /// <para>Ahead of the owned draws, so a node repaired here is posed the same frame; and the upload goes
    /// through <c>UnlockPool</c>, which is #260's funnel, so the geometry lands in owned residency by
    /// itself.</para></summary>
    internal static void DripPrepares()
    {
        for (int spent = 0; spent < PrepBudget && _prepN > 0; spent++)
        {
            _prepN--;
            int e = _prepQ[_prepN];
            IntPtr part = _prepPart[_prepN];
            _prepPart[_prepN] = IntPtr.Zero;
            PartTable._pPoseTried[e] = true;
            Prepare(e, part);
        }
    }

    /// <summary>One repair, guarded three ways. The record must still BE this part and still be a
    /// <c>PartSkin</c> (<c>PartTable.Skinned</c> is an exact vtable test taken at the walk) -- on any other
    /// part the same vtable slot is <c>Part::PostProcess</c> @0x436e10, which recurses the whole subtree. The
    /// node is re-read cold: a sibling part sharing the <c>MdlNodeSkin</c> may have prepared it already,
    /// which is a win and not a call. And a node with no temporary arrays left
    /// (<see cref="EngineLayout.SkinTempArrays"/>) can never be prepared by anything -- counted, not retried.
    /// <para>Called as the engine calls it: the part as both arguments (see
    /// <see cref="EngineLayout.PartPostProcessSlot"/> for the ABI). Side effect worth naming, because it is on
    /// a borrowed object: <c>PostProcess</c> opens with <c>Gob::Reset</c> @0x499bd0 on <c>part+0x50</c>, so
    /// repairing a live creature resets its gob -- exactly what the engine's own first draw did.</para></summary>
    private static void Prepare(int e, IntPtr part)
    {
        if (!PartTable.EntryLive(e, part) || !PartTable.Skinned(e)) { _prepDead++; return; }
        IntPtr node = *(IntPtr*)(part + AlloyRenderer.PartMdlNode);
        if (node == IntPtr.Zero) { _prepDead++; return; }
        if (*(short*)(node + EngineLayout.SkinBoneCount) > 0) { _prepWon++; return; }
        if (*(IntPtr*)(node + EngineLayout.SkinTempArrays) == IntPtr.Zero) { _prepNoData++; return; }
        _prepCalls++;
        IntPtr slot = *(IntPtr*)(*(IntPtr*)part + EngineLayout.PartPostProcessSlot);
        ((delegate* unmanaged<IntPtr, IntPtr, void>)slot)(part, part);
        if (*(short*)(node + EngineLayout.SkinBoneCount) > 0) _prepWon++;
    }

    /// <summary>The part table was wiped, so every queued index names a record that no longer exists. The
    /// one-attempt ledger is a column and goes with the wipe on its own; this only drops the queue.</summary>
    internal static void Forget() { Array.Clear(_prepPart); _prepN = 0; }

    /// <summary>#284's number for the period line: how many unprepared skinmeshes Alloy has repaired, how
    /// many now have a bind pose, and how many it will never fix. Session totals, like the texture drip's --
    /// a hole that is still a hole three periods later is the thing worth seeing.
    /// <para>A climbing repair count is creatures arriving, not a leak. A nonzero <c>no data</c> is the
    /// residual hole and is attributable: those nodes lost their temporary arrays before anything asked.</para></summary>
    internal static string PrepareNote()
        => _prepCalls == 0 && _prepN == 0 && _prepNoData == 0 && _prepDropped == 0 ? ""
         : $", unprepared skinmeshes: {_prepCalls} repaired ({_prepWon} now pose, {_prepDead} refused as "
         + $"freed, {_prepNoData} PERMANENT -- no bind data left), {_prepN} queued"
         + (_prepDropped == 0 ? "" : $", {_prepDropped} DROPPED (queue full)");

    /// <summary><c>GatherBones</c>: preorder DFS of the part tree, accumulating each node's local transform
    /// onto the one handed down. Traversal ORDER is the index the bone map refers to, so the child filter
    /// matters -- a child whose <c>MdlNode</c> parent (<c>+0x60</c>) is not this part's node belongs to a
    /// different model glued into the same part tree, and admitting it shifts every index after it.</summary>
    private static void Gather(IntPtr part, float px, float py, float pz,
                               float qw, float qx, float qy, float qz)
    {
        if (_gFull) return;
        float* lp = (float*)(part + EngineLayout.PartPos);
        float* lq = (float*)(part + EngineLayout.PartRot);
        QRot(qw, qx, qy, qz, lp[0], lp[1], lp[2], out float rx, out float ry, out float rz);
        px += rx; py += ry; pz += rz;
        QMul(qw, qx, qy, qz, lq[0], lq[1], lq[2], lq[3], out qw, out qx, out qy, out qz);
        if (_gCount >= MaxGathered || _gDepth >= MaxDepth) { _gFull = true; return; }
        int i = _gCount++;
        _gPos[i * 3] = px; _gPos[i * 3 + 1] = py; _gPos[i * 3 + 2] = pz;
        _gRot[i * 4] = qw; _gRot[i * 4 + 1] = qx; _gRot[i * 4 + 2] = qy; _gRot[i * 4 + 3] = qz;

        IntPtr* kids = *(IntPtr**)(part + EngineLayout.PartChildren);
        int n = *(int*)(part + EngineLayout.PartChildCount);
        if (kids == null) return;
        IntPtr myNode = *(IntPtr*)(part + AlloyRenderer.PartMdlNode);
        _gDepth++;
        for (int k = 0; k < n && !_gFull; k++)
        {
            IntPtr kid = kids[k];
            if (kid == IntPtr.Zero) continue;
            IntPtr kn = *(IntPtr*)(kid + AlloyRenderer.PartMdlNode);
            if (kn == IntPtr.Zero || *(IntPtr*)(kn + EngineLayout.MdlNodeParent) != myNode) continue;
            Gather(kid, px, py, pz, qw, qx, qy, qz);
        }
        _gDepth--;
    }

    /// <summary>The sixteen floats that actually LAND in <c>GLRender::m_bones</c> -- a column-major affine
    /// [R|t], which is what GLSL reads a 64-byte run as, hence the memcpy the vertex stages inherit.
    /// <para>Derived from the pair of transposes rather than guessed. <c>ExecuteSkinGPU</c> builds the 3x3
    /// COLUMN-major (R00,R10,R20,R01,...), <c>Augment</c> @0x5b4ef0 writes it out as
    /// <c>out[j*4+i] = in[i*3+j]</c> and drops the translation into 3/7/11, then
    /// <c>GLRender::SetBoneMatrix</c> @0x4cbbb0 transposes the lot. What survives is R's COLUMNS in the
    /// first three runs of four and the translation in 12..14 -- so the off-diagonals below are R10/R20/R01
    /// and not R01/R02/R10, which is the one thing in this file a sign slip would silently invert.</para>
    /// <para><see cref="SelfCheck"/> pins it.</para></summary>
    private static void Compose(float w, float x, float y, float z, float tx, float ty, float tz, float* m)
    {
        float xx = x * x, yy = y * y, zz = z * z;
        m[0] = 1f - 2f * (yy + zz); m[1] = 2f * (x * y + w * z); m[2] = 2f * (x * z - y * w); m[3] = 0f;
        m[4] = 2f * (x * y - w * z); m[5] = 1f - 2f * (xx + zz); m[6] = 2f * (y * z + x * w); m[7] = 0f;
        m[8] = 2f * (x * z + y * w); m[9] = 2f * (y * z - x * w); m[10] = 1f - 2f * (xx + yy); m[11] = 0f;
        m[12] = tx; m[13] = ty; m[14] = tz; m[15] = 1f;
    }

    /// <summary>The one runnable check behind this file: a hand-worked pose, asserted at arm time the way
    /// <see cref="ShaderContract"/> asserts the stage text. It catches the class of bug decompiled maths is
    /// actually prone to -- a quaternion component order read off the wrong word, or a matrix written in the
    /// transposed layout -- before a live run turns it into a creature folded inside out.
    /// <para>The subject is a 90 degree turn about +Z at (1,2,3): it maps +X to +Y, so the first COLUMN of
    /// the composed matrix must be (0,1,0) and the fourth the translation. A transposed layout passes every
    /// diagonal test and fails this one.</para></summary>
    internal static bool SelfCheck()
    {
        const float R = 0.70710678f;
        float* m = stackalloc float[16];
        Compose(R, 0f, 0f, R, 1f, 2f, 3f, m);
        if (!Near(m[0], 0f) || !Near(m[1], 1f) || !Near(m[2], 0f) || !Near(m[3], 0f)) return false;
        if (!Near(m[4], -1f) || !Near(m[5], 0f) || !Near(m[6], 0f) || !Near(m[7], 0f)) return false;
        if (!Near(m[8], 0f) || !Near(m[9], 0f) || !Near(m[10], 1f) || !Near(m[11], 0f)) return false;
        if (!Near(m[12], 1f) || !Near(m[13], 2f) || !Near(m[14], 3f) || !Near(m[15], 1f)) return false;
        // ...and the rotate the composition is built out of, which must agree with that matrix's columns.
        QRot(R, 0f, 0f, R, 1f, 0f, 0f, out float ox, out float oy, out float oz);
        if (!Near(ox, 0f) || !Near(oy, 1f) || !Near(oz, 0f)) return false;
        // The identity quaternion is the engine's early-out, so it must return the vector's own bits.
        QRot(1f, 0f, 0f, 0f, 7f, 8f, 9f, out ox, out oy, out oz);
        if (ox != 7f || oy != 8f || oz != 9f) return false;
        // Hamilton, in the engine's association: a half turn about Z twice over is a full turn, i.e. -w.
        QMul(R, 0f, 0f, R, R, 0f, 0f, R, out float w, out float x, out float y, out float z);
        return Near(w, 0f) && Near(x, 0f) && Near(y, 0f) && Near(z, 1f);
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-5f;

    private static void Identity(float* m)
    {
        for (int i = 0; i < 16; i++) m[i] = 0f;
        m[0] = 1f; m[5] = 1f; m[10] = 1f; m[15] = 1f;
    }

    /// <summary><c>operator-(Quaternion const&amp;)</c> @0x5b5d50, which negates the FIRST word and no other.
    /// That is what pins the storage order to (w,x,y,z): negating w is the conjugate up to the sign of the
    /// whole quaternion, and q and -q are the same rotation.</summary>
    private static void QConj(float* q, out float w, out float x, out float y, out float z)
    { w = -q[0]; x = q[1]; y = q[2]; z = q[3]; }

    /// <summary><c>operator*(Quaternion const&amp;, Quaternion const&amp;)</c> @0x5b5c30 -- the Hamilton
    /// product, written in the engine's own association so the result is bit-identical.</summary>
    private static void QMul(float aw, float ax, float ay, float az, float bw, float bx, float by, float bz,
                             out float w, out float x, out float y, out float z)
    {
        float rw = ((aw * bw - ax * bx) - by * ay) - bz * az;
        float rx = (aw * bx + ax * bw + bz * ay) - by * az;
        float ry = (aw * by - bz * ax) + ay * bw + bx * az;
        float rz = ((aw * bz + ax * by) - ay * bx) + az * bw;
        w = rw; x = rx; y = ry; z = rz;
    }

    /// <summary><c>operator*(Quaternion const&amp;, Vector const&amp;)</c> @0x5b5e40 -- rotate, with the
    /// engine's three early-outs kept: they are not an optimisation here but the difference between
    /// returning the vector's exact bits and returning a rounded product of them.</summary>
    private static void QRot(float w, float x, float y, float z, float vx, float vy, float vz,
                             out float ox, out float oy, out float oz)
    {
        ox = vx; oy = vy; oz = vz;
        if (w == 1f) return;
        if (vx == 0f && vy == 0f && vz == 0f) return;
        if (x == 0f && y == 0f && z == 0f) return;
        float a = y * y + z * z, b = z * z + x * x, c = x * x + y * y;
        float d = x * y - w * z, e = w * z + x * y, f = y * z - x * w, g = x * z + y * w;
        float h = x * z - y * w, i = y * z + x * w;
        ox = (g + g) * vz + (1f - (a + a)) * vx + (d + d) * vy;
        oy = (e + e) * vx + (1f - (b + b)) * vy + (f + f) * vz;
        oz = (h + h) * vx + (i + i) * vy + (1f - (c + c)) * vz;
    }
}
