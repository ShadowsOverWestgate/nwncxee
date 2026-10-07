using System;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>CX-120 seam-anim: the bone-slot claim DB mode needs. The palette must live in the renderer's own
/// bone stage, because that is the block <c>Issue.UploadBones</c> sends and <c>feat.y</c> indexes -- so the
/// pose step claims a slot here and writes into it, and <c>SceneDb.PartBoneBase</c> carries the slot number
/// the feed's record then names.
/// <para>A DB slot is marked with an owner of <see cref="DbBoneOwner"/>, which is below zero, so the engine
/// path's two reclaimers both leave it alone by construction: <c>FreeBoneSlot</c> returns on a negative owner
/// and <c>PoseSkinned</c>'s orphan sweep tests <c>_boneOwner[s] &gt;= 0</c>. The slot is therefore sticky for
/// as long as the part holds it -- which is also what #308 needs, since the shadow bake pairs the previous
/// build's <c>feat.y</c> with this frame's block.</para>
/// <para>NEW FILE, not an edit to another stream's: seam-feed owns <c>AlloyRenderer.Db.cs</c>.</para></summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The owner marker a DB-mode bone slot carries.</summary>
    private const int DbBoneOwner = -2;

    /// <summary>Claim a bone slot for a DB part, or -1 when the stage cannot grow (the caller counts it and the
    /// part draws rigid, which is its rest pose).</summary>
    internal static int DbAllocBone()
    {
        int s = AllocBoneSlot(DbBoneOwner);
        if (s >= 0)
        {
            EnsureDbStage(s + 1);
            _dbLive.Add(s);
            _dbFresh.Add(s);
        }
        return s;
    }

    /// <summary>
    /// Where slot <paramref name="slot"/>'s 64 matrices live: a stage of this file's OWN, not the pair
    /// <c>PoseSkinned</c> ping-pongs.
    /// <para>That matters because the two run on different clocks. A DB palette is solved on the GAME thread,
    /// at the end of the object's animate step; <c>PoseSkinned</c> swaps <c>_boneStage</c> with
    /// <c>_bonePrevStage</c> once per frame, afterwards. Writing straight into <c>_boneStage</c> therefore put
    /// half the palettes in the buffer that was about to become the PREVIOUS frame's, and
    /// <c>Issue.UploadBones</c> sent the other one -- alternating one- and two-frame-old poses, and
    /// arbitrarily old ones for a creature that had settled. The palettes live here instead and
    /// <see cref="DbBonePublish"/> copies them into whichever buffer the frame is about to upload.</para>
    /// </summary>
    internal static float* DbBoneStage(int slot)
    {
        EnsureDbStage(slot + 1);
        return _dbStage + (long)slot * BoneFloats;
    }

    private static float* _dbStage, _dbPrev;
    private static int _dbStageSlots;
    private static readonly System.Collections.Generic.HashSet<int> _dbLive = new();
    /// <summary>Slots claimed but not yet published, whose motion-vector history is their own first pose.</summary>
    private static readonly System.Collections.Generic.HashSet<int> _dbFresh = new();

    /// <summary>Room for <paramref name="want"/> slots in the DB stage, doubling as the engine path's does.</summary>
    private static void EnsureDbStage(int want)
    {
        if (want <= _dbStageSlots) return;
        int cap = _dbStageSlots == 0 ? 16 : _dbStageSlots;
        while (cap < want) cap *= 2;
        nuint bytes = (nuint)cap * BoneFloats * sizeof(float);
        _dbStage = (float*)NativeMemory.Realloc(_dbStage, bytes);
        _dbPrev = (float*)NativeMemory.Realloc(_dbPrev, bytes);
        _dbStageSlots = cap;
    }

    /// <summary>
    /// Copy every live DB palette into the frame's upload buffer, and last frame's into the history one.
    /// Called from <c>PoseSkinned</c> immediately after its swap, which is the one moment both buffers mean
    /// what their names say.
    /// </summary>
    internal static void DbBonePublish()
    {
        if (_dbStage == null || _dbLive.Count == 0) return;
        foreach (int slot in _dbLive)
        {
            if (slot < 0 || slot >= _boneTop || !EnsureBoneSlots(slot + 1)) continue;
            long off = (long)slot * BoneFloats;
            var now = new ReadOnlySpan<float>(_dbStage + off, BoneFloats);
            var prev = new Span<float>(_dbPrev + off, BoneFloats);
            // CX-26's rule, which the engine path follows for the same reason: a FRESH slot's history is its
            // own pose, or a creature's first frame smears from whatever the slot held before it.
            if (_dbFresh.Remove(slot)) now.CopyTo(prev);
            prev.CopyTo(new Span<float>(_bonePrevStage + off, BoneFloats));
            now.CopyTo(new Span<float>(_boneStage + off, BoneFloats));
            now.CopyTo(prev);
        }
    }

    /// <summary>Give a DB bone slot back. Does what <c>FreeBoneSlot</c> does, which refuses a negative owner on
    /// purpose: a DB slot is only ever released from here.</summary>
    internal static void DbFreeBone(int slot)
    {
        if (slot < 0 || slot >= _boneTop || _boneOwner[slot] != DbBoneOwner) return;
        _dbLive.Remove(slot);
        _dbFresh.Remove(slot);
        _boneOwner[slot] = -1;
        if (_boneFreeN < _boneFree.Length) _boneFree[_boneFreeN++] = slot;
    }
}
