using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>What the procedural pose layer (<c>CX.Services.Anim.PoseService</c>) reaches in DB mode: the two
/// calls around a creature's queue advance, a world read that sees its own writes, the run columns and the
/// look-at state. The layer is the same code that corrects an engine <c>Gob</c> in mode 0; only its rig
/// access changes.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>The pose layer's calls around one creature's queue advance: <see cref="PoseBefore"/> puts back
    /// the values it corrected last frame and paces the runs, <see cref="PoseAfter"/> corrects the fresh pose.
    /// Each answers whether it wrote a pose column. Null until the layer arms.</summary>
    internal static delegate*<DbId, float, bool> PoseBefore, PoseAfter;

    /// <summary>A part's world transform from the LOCAL columns up its chain and the composed object matrix --
    /// not <see cref="SceneDb.PartWorld"/>, which is stale the moment the layer writes a local. The caller
    /// composes the object first (the layer's step does).</summary>
    internal static void PoseWorld(int part, out Vector3 pos, out Quaternion rot)
    {
        Matrix4x4 m = Build(SceneDb.PartScale[part], SceneDb.PartRot[part], SceneDb.PartPos[part]);
        int up = SceneDb.PartParent[part];
        for (int hop = 0; up >= 0 && hop < MaxDepth; hop++)
        {
            m *= Build(SceneDb.PartScale[up], SceneDb.PartRot[up], SceneDb.PartPos[up]);
            up = SceneDb.PartParent[up];
        }
        DbId obj = SceneDb.PartObj[part];
        if (SceneDb.Objects.Valid(obj)) m *= SceneDb.ObjWorld[obj.Index];
        pos = new Vector3(m.M41, m.M42, m.M43);
        rot = RotOf(m);
    }

    /// <summary>Runs on <paramref name="obj"/>'s queue; <see cref="RunSlot"/> 0 is the NEWEST.</summary>
    internal static int RunCount(DbId obj)
        => SceneDb.Objects.Valid(obj) && _block[obj.Index] >= 0 ? _runN[obj.Index] : 0;

    /// <summary>The pool slot of run <paramref name="k"/> (0 = newest) of <paramref name="obj"/>.</summary>
    internal static int RunSlot(DbId obj, int k) => Slot(obj.Index, k);

    /// <summary>A run's columns, by pool slot: rate, time, blend weight, fade time, flags and animation.</summary>
    internal static ref float RunRate(int s) => ref _rRate[s];
    /// <inheritdoc cref="RunRate"/>
    internal static ref float RunTime(int s) => ref _rTime[s];
    /// <inheritdoc cref="RunRate"/>
    internal static ref float RunBlend(int s) => ref _rBlend[s];
    /// <inheritdoc cref="RunRate"/>
    internal static ref float RunFade(int s) => ref _rFade[s];
    /// <inheritdoc cref="RunRate"/>
    internal static int RunFlagsOf(int s) => _rFlags[s];
    /// <inheritdoc cref="RunRate"/>
    internal static AnimDef? RunAnim(int s) => _rAnim[s];

    /// <summary>Is a look-at running (not fading back) on <paramref name="obj"/>?</summary>
    internal static bool LookingAt(DbId obj)
        => SceneDb.Objects.Valid(obj) && _lookAt[obj.Index].Active && !_lookAt[obj.Index].Ending;

    /// <summary>The layer's two calls inside <see cref="StepObject"/>, for a creature only.</summary>
    private static bool PoseLayer(int i) => PoseBefore != null && (SceneDb.ObjFlags[i] & ObjFlag.Creature) != 0;
}
