using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Engine;

namespace CX.Alloy;

/// <summary>CX-120: the mutable half of one emitter -- every field the simulation WRITES and reads back on the
/// next step. In engine mode it mirrors <c>PartEmitter</c>'s own fields and is stored back to them at the end
/// of the step; in DB mode there is no engine object to store it in, so it lives in the simulation's own slot.
/// <para>The basis is three vectors rather than nine floats because that is what every reader of it wants: the
/// engine's <c>basis[9]</c> is column-major X, Y, Z axes and nothing indexes it as a matrix.</para></summary>
internal struct EmitMut
{
    /// <summary>The emitter transform the particles are currently expressed in -- refreshed from
    /// <see cref="Emitter.WorldPos"/> by the step, which is what the engine's three <c>Part::GetWorld</c> calls
    /// in <c>AnimateParticles</c> @0x4ac660 wrote.</summary>
    internal Vector3 Pos;
    /// <inheritdoc cref="Pos"/>
    internal Quat Orient;
    /// <summary>The frame the particles were last expressed against (<c>inherit</c> rebases THROUGH it).</summary>
    internal Vector3 PrevPos;
    /// <inheritdoc cref="PrevPos"/>
    internal Quat PrevRot;
    /// <summary>The OWNER's transform as the previous step left it (<c>inherit_local</c>/<c>inherit_part</c>
    /// measure their own motion against it). A different record from <see cref="PrevPos"/>.</summary>
    internal Vector3 PrevWorldPos;
    /// <inheritdoc cref="PrevWorldPos"/>
    internal Quat PrevWorldRot;
    /// <summary>The spawn basis: X, Y and Z axes of <see cref="Orient"/>.</summary>
    internal Vector3 Bx, By, Bz;
    /// <summary>p2p: the fraction of a chain's spacing the last spawn did not consume.</summary>
    internal Vector3 P2pLeftover;
    /// <summary>Fractional births carried between frames.</summary>
    internal float SpawnAcc;
    /// <summary>The explosion's fired latch, or the lightning's seconds-since-fractal clock.</summary>
    internal float ModeClock;
    /// <summary>Has the inherit basis been seeded, and is the emitter birthing?</summary>
    internal byte Seeded, Birthing;
    /// <summary>Lightning: the emitter-to-target delta and the emitter position the last fractal was built at.
    /// </summary>
    internal Vector3 BoltLastDelta, BoltLastPos;
}

/// <summary>CX-120: <b>one emitter, as a value.</b> Every parameter <see cref="EmitterSim"/> and
/// <see cref="Particles"/> read, snapshotted once per emitter per frame from EITHER the engine's
/// <c>PartEmitter</c>/<c>MdlNodeEmitter</c> views (mode 0) or the scene database's
/// <see cref="EmitterDef"/> + <see cref="DbEmitterChannels"/> + part columns (DB mode).
/// <para><b>Why a snapshot and not a facade.</b> The movers read the emitter's position, its previous frame and
/// its basis once PER PARTICLE; a two-source property would branch there. A snapshot pays for the branch once
/// per emitter and leaves the inner loops reading plain fields -- which is also what makes the two sources
/// provably interchangeable (<c>EmitterSim.SelfCheck</c> runs the same step over both).</para>
/// <para>CX-188: there is one source left. <c>FillEngine</c>, the transcription of what the pre-CX-120 code
/// read off the engine's <c>PartEmitter</c>/<c>MdlNodeEmitter</c>, had one caller -- the parity self-check --
/// so no production snapshot has come from an engine emitter since the seam took the scene graph.</para></summary>
internal struct Emitter
{
    /// <summary>The database part index, or -1 for an unfilled snapshot.</summary>
    internal int Part;
    /// <summary>The owning database object, in DB mode.</summary>
    internal DbId Obj;
    /// <summary>The database's chunk-spawn scene, in DB mode.</summary>
    internal DbId Scene;

    // ---- the node's own declarations ----
    internal int Flags;
    internal short RenderOrder;
    internal bool Chunk, Loop, NoCull, Splat, Tinted;
    /// <summary>DB mode only: the <c>chunkName</c> resref a chunk emitter spawns objects of.</summary>
    internal string? ChunkName;
    internal float ConeCap, WindStrength, WindFalloff;
    internal int GridX, GridY;
    /// <summary>The update mode, as <c>MdlNodeEmitter::InternalCreateInstance</c> @0x4af5c0 picks it.</summary>
    internal int Mode;

    // ---- the parameters (engine fields written by the model's controllers) ----
    internal int Type, Blend;
    internal short LifeInterp;
    internal float BirthRate, Life, Velocity, RandVel, Spread, Spin, Gravity, Bounce,
                   CombineTime, P2pDrag, P2pGrav, Bezier2, Bezier3, DeadSpace,
                   SpawnX, SpawnY, BlurLength, BoltDelay, BoltRadius, ZigZag,
                   AlphaStart, AlphaEnd, Fps, FrameStart, FrameEnd,
                   SizeStartX, SizeEndX, SizeStartY, SizeEndY, ViewRadius;
    internal Vector3 ColorStart, ColorEnd;

    // ---- this frame's placement and gates ----
    /// <summary>This frame's world transform, from <c>Part::GetWorld</c> or <see cref="SceneDb.PartWorld"/>.
    /// The step copies it into <see cref="EmitMut.Pos"/> where the engine called the vcall.</summary>
    internal Vector3 WorldPos;
    /// <inheritdoc cref="WorldPos"/>
    internal Quat WorldRot;
    /// <summary>Does this emitter have an owner (a <c>Gob</c> / a database object) at all?</summary>
    internal bool HasOwner;
    internal Vector3 OwnerPos;
    internal Quat OwnerRot;
    /// <summary>Is the owner forcing animation while the scene is not animating?</summary>
    internal bool ForceAnimated;
    /// <summary>The owner's adjusted scale, which every spawn extent is measured in. One without an owner is
    /// unscaled.</summary>
    internal float Scale;
    /// <summary>The emitter part's own scale -- the lightning zigzag's amplitude is measured in it.</summary>
    internal float PartScale;
    /// <summary>Is the emitter's tile mid-fade? <c>AnimateParticles</c>' and <c>RenderEmitter</c>'s shared
    /// opening exit: a fading tile's emitter neither steps nor draws.</summary>
    internal bool Faded;
    /// <summary>Does the p2p target exist, and where is it?</summary>
    internal bool HasTarget;
    internal Vector3 TargetPos;
    internal Quat TargetRot;
    /// <summary>The detonation REQUEST, cleared by the explosion step.</summary>
    internal bool Detonate;
    /// <summary>Engine mode: how many particles the engine's own list holds (the chunk census).</summary>

    /// <summary>The simulation's own state.</summary>
    internal EmitMut M;
}

internal static unsafe partial class EmitterSim
{
    /// <summary>Flag bits 3 and 9 of <see cref="EngineLayout.EmitterFlags"/>: <c>MdlNodeEmitter::IsTinted</c>
    /// @0x4aafc0 is <c>flags &gt;&gt; 3 &amp; 1</c> and <c>IsSplat</c> @0x4ab070 is <c>flags &gt;&gt; 9 &amp;
    /// 1</c> -- read off the word with the other nine rather than through two more calls.</summary>
    private const int FTinted = 1 << 3, FSplat = 1 << 9;

    /// <summary>Fill a snapshot from the scene DATABASE: the parsed model's <see cref="EmitterDef"/> for what
    /// the file declares, the node's REST keys blended with whatever an animation has written
    /// (<see cref="DbEmitterChannels"/>) for every animatable parameter, and the part's own columns for the
    /// placement and the gates.
    /// <para>Three engine field names mean something else in the file, and this is the only place that
    /// translation happens: the engine's <c>coneCap</c> is the MDL's <c>deadspace</c>, its
    /// <c>windStrength</c>/<c>windFalloff</c> are <c>blastRadius</c>/<c>blastLength</c>, and its
    /// <c>deadSpace</c> (the p2p arrival radius) is the <c>threshold</c> CONTROLLER. Measured on
    /// <c>MdlNodeEmitter::InternalParseField</c> @0x4ab... (node+0x9c, +0xa0, +0xa4) and on the controller
    /// id map (<c>MdlBinary.EngineControllerId</c>: file 188 -&gt; <c>PartEmitter+0xd4</c>).</para>
    /// <para>The mutable half is left at the fresh world transform, which is what <see cref="Particles"/>
    /// wants; the simulation overwrites it from its own slot before it steps.</para></summary>
    internal static bool FillDb(int part, ref Emitter e)
    {
        ModelDef? m = SceneDb.PartModel[part];
        int n = SceneDb.PartNode[part];
        NodeDef? nd = m != null && (uint)n < (uint)m.Nodes.Length ? m.Nodes[n] : null;
        EmitterDef? d = nd?.Emitter;
        if (nd == null || d == null) return false;
        DbId obj = SceneDb.PartObj[part];
        if (!SceneDb.Objects.Valid(obj)) return false;
        int o = obj.Index;
        Vector4[] rest = RestOf(nd);
        int b = DbEmitterChannels.BlockOf(part);

        e = default;
        e.Part = part;
        e.Obj = obj;
        e.Scene = SceneDb.ObjScene[o];

        e.Flags = (int)d.Flags;
        e.RenderOrder = (short)d.RenderOrder;
        e.Chunk = d.ChunkName.Length > 0;
        e.ChunkName = d.ChunkName;
        e.Loop = d.Loop;
        e.NoCull = d.TwoSidedTex;
        e.Splat = (e.Flags & FSplat) != 0;
        e.Tinted = (e.Flags & FTinted) != 0;
        e.ConeCap = d.DeadSpace;
        e.WindStrength = d.BlastRadius;
        e.WindFalloff = d.BlastLength;
        e.GridX = d.XGrid;
        e.GridY = d.YGrid;
        e.Mode = ModeOf(d.Update);
        e.Type = RenderTypeOf(d.Render);
        e.Blend = BlendOf(d.Blend);
        // PartEmitter::Initialize @0x4aaaa0 stores 1 here and nothing in the client ever stores 0, so the
        // engine's index-domain interpolation is dead code on this build. Matched rather than invented.
        e.LifeInterp = 1;

        e.AlphaEnd = Ch1(rest, b, ChAlphaEnd);
        e.AlphaStart = Ch1(rest, b, ChAlphaStart);
        e.BirthRate = Ch1(rest, b, ChBirthRate);
        e.Bounce = Ch1(rest, b, ChBounceCo);
        e.ColorEnd = Ch3(rest, b, ChColorEnd);
        e.ColorStart = Ch3(rest, b, ChColorStart);
        e.CombineTime = Ch1(rest, b, ChCombineTime);
        e.P2pDrag = Ch1(rest, b, ChDrag);
        e.Fps = Ch1(rest, b, ChFps);
        e.FrameEnd = Ch1(rest, b, ChFrameEnd);
        e.FrameStart = Ch1(rest, b, ChFrameStart);
        e.P2pGrav = Ch1(rest, b, ChGrav);
        e.Life = Ch1(rest, b, ChLifeExp);
        e.Gravity = Ch1(rest, b, ChMass);
        e.Bezier2 = Ch1(rest, b, ChP2PBezier2);
        e.Bezier3 = Ch1(rest, b, ChP2PBezier3);
        e.Spin = Ch1(rest, b, ChParticleRot);
        e.RandVel = Ch1(rest, b, ChRandVel);
        e.SizeStartX = Ch1(rest, b, ChSizeStart);
        e.SizeEndX = Ch1(rest, b, ChSizeEnd);
        e.SizeStartY = Ch1(rest, b, ChSizeStartY);
        e.SizeEndY = Ch1(rest, b, ChSizeEndY);
        e.Spread = Ch1(rest, b, ChSpread);
        e.DeadSpace = Ch1(rest, b, ChThreshold);
        e.Velocity = Ch1(rest, b, ChVelocity);
        e.SpawnX = Ch1(rest, b, ChXSize);
        e.SpawnY = Ch1(rest, b, ChYSize);
        e.BlurLength = Ch1(rest, b, ChBlurLength);
        e.BoltDelay = Ch1(rest, b, ChLightningDelay);
        e.BoltRadius = Ch1(rest, b, ChLightningRadius);
        e.ZigZag = Ch1(rest, b, ChLightningScale);
        e.Detonate = Ch1(rest, b, ChDetonate) != 0f;

        Matrix4x4 w = SceneDb.PartWorld[part];
        e.WorldPos = new Vector3(w.M41, w.M42, w.M43);
        e.WorldRot = RotationOf(in w);
        e.ViewRadius = ViewRadiusOf(e.Gravity, e.Life, e.Velocity, e.RandVel,
                                    e.SizeStartX, e.SizeEndX, e.SizeStartY, e.SizeEndY,
                                    Ch1(rest, b, ChSizeMid), Ch1(rest, b, ChSizeMidY));

        e.HasOwner = true;
        e.OwnerPos = SceneDb.ObjWorld[o].Translation;
        e.OwnerRot = SceneDb.ObjRot[o];
        e.ForceAnimated = false;
        e.Scale = ObjectBridge.ObjectAdjustedScale?.Invoke(obj) ?? SceneDb.ObjScale[o];
        if (e.Scale <= 0f) e.Scale = 1f;
        e.PartScale = SceneDb.PartScale[part] > 0f ? SceneDb.PartScale[part] : 1f;
        // The engine's exit is "my tile is mid-fade": PartFade carries exactly that in DB mode (the tiles
        // stream writes fade x reveal per part), so a tile being unloaded takes its flames with it.
        e.Faded = SceneDb.PartFade[part] < 1f;
        TargetOf(part, o, ref e);

        e.M.Pos = e.WorldPos;
        e.M.Orient = e.WorldRot;
        return true;
    }

    /// <summary>The p2p target, the engine's own rule: <c>SetEmitterTarget</c> @0x4aa7d0 walks the emitter
    /// part's CHILDREN, takes the first reference node among them and makes that node's instance
    /// (<c>PartReference+0x68</c>, <see cref="SceneDb.PartRefObj"/>) the target. A beam's instance rides its
    /// victim, which is what puts the far end of a bolt on the thing it was cast at.
    /// <para>A part's subtree is contiguous in pre-order, so the walk stops at the first part whose parent is
    /// outside it. No allocation, and an emitter has a handful of children.</para></summary>
    internal static int DbTargets;

    private static void TargetOf(int part, int o, ref Emitter e)
    {
        e.HasTarget = false;
        int end = SceneDb.ObjPartFirst[o] + SceneDb.ObjPartCount[o];
        for (int c = part + 1; c < end && SceneDb.PartParent[c] >= part; c++)
        {
            if (SceneDb.PartParent[c] != part || SceneDb.PartKindOf[c] != PartKind.Reference) continue;
            DbId t = SceneDb.PartRefObj[c];
            if (!SceneDb.Objects.Valid(t)) continue;
            Matrix4x4 tw = SceneDb.ObjWorld[t.Index];
            e.TargetPos = tw.Translation;
            e.TargetRot = RotationOf(in tw);
            e.HasTarget = true;
            DbTargets++;
            return;
        }
    }

    /// <summary>CX-188: <b>just enough of the fill to answer the cull</b> -- the emitter's world position and
    /// its view radius, and the update mode the dump budget keys on. Ten channel reads against the full fill's
    /// thirty-odd plus a target walk, an owner transform and an engine scale call.
    /// <para>The culled emitter needs nothing else: it has always <c>continue</c>d before <c>Advance</c>, so an
    /// off-screen emitter is not stepped today either -- filling it was waste, not caution. The verdict is bit
    /// identical to the full fill's, because the position and the radius come from the same columns through
    /// the same <see cref="ViewRadiusOf"/>.</para>
    /// <para>False for the same two reasons <see cref="FillDb"/> is false -- no emitter declaration on the
    /// node, or a dead owner -- so the walk's <c>no-def</c> count keeps its meaning.</para></summary>
    internal static bool CullDb(int part, out Vector3 pos, out float radius, out int mode)
    {
        pos = default; radius = 0f; mode = ModeSingle;
        ModelDef? m = SceneDb.PartModel[part];
        int n = SceneDb.PartNode[part];
        NodeDef? nd = m != null && (uint)n < (uint)m.Nodes.Length ? m.Nodes[n] : null;
        EmitterDef? d = nd?.Emitter;
        if (nd == null || d == null) return false;
        if (!SceneDb.Objects.Valid(SceneDb.PartObj[part])) return false;
        Vector4[] rest = RestOf(nd);
        int b = DbEmitterChannels.BlockOf(part);
        Matrix4x4 w = SceneDb.PartWorld[part];
        pos = new Vector3(w.M41, w.M42, w.M43);
        radius = ViewRadiusOf(Ch1(rest, b, ChMass), Ch1(rest, b, ChLifeExp), Ch1(rest, b, ChVelocity),
                              Ch1(rest, b, ChRandVel), Ch1(rest, b, ChSizeStart), Ch1(rest, b, ChSizeEnd),
                              Ch1(rest, b, ChSizeStartY), Ch1(rest, b, ChSizeEndY),
                              Ch1(rest, b, ChSizeMid), Ch1(rest, b, ChSizeMidY));
        mode = ModeOf(d.Update);
        return true;
    }

    /// <summary><c>PartEmitter::CalculateRadius</c> @0x4ac350, in its non-p2p arm: the distance a particle can
    /// travel over its own lifetime under its launch speed and its gravity, plus the half-diagonal of the
    /// largest quad the emitter can reach. The engine caches it in <c>PartEmitter+0x214</c>; DB mode has no
    /// such field, so it is derived here from the same parameters.
    /// <para>TWO terms of the engine's are dropped, both of which can only make the radius LARGER -- so the
    /// owned cull is marginally tighter than vanilla's rather than looser. One is the live-particle extension
    /// (it re-walks every particle and takes the furthest, which would make the radius a per-frame quantity);
    /// the other is a vector read off <c>PartEmitter+0x200</c> whose field this stream could not name. A p2p
    /// emitter's radius is the distance to its target, which DB mode has no target edge for at all.</para>
    /// </summary>
    private static float ViewRadiusOf(float mass, float life, float vel, float randVel,
                                      float sizeStartX, float sizeEndX, float sizeStartY, float sizeEndY,
                                      float sizeMid, float sizeMidY)
    {
        float accel = MathF.Abs(mass * Gravity);
        float l = MathF.Max(life, 0f);
        float travel = accel * 0.5f * l * l + (vel + randVel) * l;
        float hx = MathF.Max(sizeMid, MathF.Max(sizeEndX, sizeStartX)) * 0.5f;
        float hy = MathF.Max(sizeMidY, MathF.Max(sizeEndY, sizeStartY)) * 0.5f;
        if (hy <= 0f) hy = hx;
        return travel + MathF.Sqrt(hx * hx + hy * hy);
    }

    // ---- the parameter cache (CX-188) ------------------------------------------------------------------

    /// <summary>The controller ids the DB fill reads, one per <c>Ch*</c> ordinal below -- the ordinal IS the
    /// index into this table and into <see cref="NodeDef.EmRest"/>. Keep the two in step; the self-check
    /// asserts every pair.</summary>
    private static readonly int[] _chIds =
    {
        Ctl.EmAlphaEnd, Ctl.EmAlphaStart, Ctl.EmBirthRate, Ctl.EmBounceCo, Ctl.EmColorEnd, Ctl.EmColorStart,
        Ctl.EmCombineTime, Ctl.EmDrag, Ctl.EmFps, Ctl.EmFrameEnd, Ctl.EmFrameStart, Ctl.EmGrav,
        Ctl.EmLifeExp, Ctl.EmMass, Ctl.EmP2PBezier2, Ctl.EmP2PBezier3, Ctl.EmParticleRot, Ctl.EmRandVel,
        Ctl.EmSizeStart, Ctl.EmSizeEnd, Ctl.EmSizeStartY, Ctl.EmSizeEndY, Ctl.EmSpread, Ctl.EmThreshold,
        Ctl.EmVelocity, Ctl.EmXSize, Ctl.EmYSize, Ctl.EmBlurLength, Ctl.EmLightningDelay,
        Ctl.EmLightningRadius, Ctl.EmLightningScale, Ctl.EmDetonate, Ctl.EmSizeMid, Ctl.EmSizeMidY,
    };

    private const int ChAlphaEnd = 0, ChAlphaStart = 1, ChBirthRate = 2, ChBounceCo = 3, ChColorEnd = 4,
                      ChColorStart = 5, ChCombineTime = 6, ChDrag = 7, ChFps = 8, ChFrameEnd = 9,
                      ChFrameStart = 10, ChGrav = 11, ChLifeExp = 12, ChMass = 13, ChP2PBezier2 = 14,
                      ChP2PBezier3 = 15, ChParticleRot = 16, ChRandVel = 17, ChSizeStart = 18,
                      ChSizeEnd = 19, ChSizeStartY = 20, ChSizeEndY = 21, ChSpread = 22, ChThreshold = 23,
                      ChVelocity = 24, ChXSize = 25, ChYSize = 26, ChBlurLength = 27,
                      ChLightningDelay = 28, ChLightningRadius = 29, ChLightningScale = 30,
                      ChDetonate = 31, ChSizeMid = 32, ChSizeMidY = 33, ChCount = 34;

    /// <summary>Ordinal -> the channel index <see cref="DbEmitterChannels"/> files that controller under, or -1
    /// for one it does not carry (<c>sizeMid</c> and <c>sizeMidY</c>, which no animation can write -- so they
    /// are always the node's rest key).</summary>
    private static readonly int[] _chBlock = BuildChBlock();

    /// <summary>Ordinals whose rest key is a THREE-column read. The two fillers build their rest values by
    /// different rules -- a 1-column track's rest is <c>Values[0]</c> and a colour's is zero unless the track
    /// states all three -- and losing that distinction would silently change a colour with a short track.
    /// </summary>
    private static bool Ch3Rest(int ord) => ord == ChColorEnd || ord == ChColorStart;

    private static int[] BuildChBlock()
    {
        var t = new int[ChCount];
        for (int ord = 0; ord < ChCount; ord++) t[ord] = DbEmitterChannels.ChannelOf(_chIds[ord]);
        return t;
    }

    /// <summary>CX-188: this NODE's rest keys, in <c>Ch*</c> ordinals, resolved on first sight and memoised on
    /// the node itself (<see cref="NodeDef.EmRest"/>). Every read used to call <see cref="NodeDef.Controller"/>,
    /// a linear search of the node's controller list, about thirty times per emitter per frame: 4,3% self of
    /// the main thread's on-CPU samples (<c>perf-0927</c>), under a <see cref="FillDb"/> that ran for every
    /// emitter in the area whether it was on screen or not.
    /// <para><b>Keyed by the node, so there is no invalidation edge.</b> The keys are a pure function of
    /// <see cref="NodeDef.Controllers"/>, which is immutable once parsed -- so a part whose model or node
    /// changes, a part index the database re-tenants and a re-parsed model all read a DIFFERENT
    /// <see cref="NodeDef"/> and therefore a different memo, with nothing to invalidate and no edge for a
    /// writer to miss. The memo dies with the model the cache drops.</para>
    /// <para><b>What an animation writes is never here.</b> <see cref="DbEmitterChannels"/>'s block is read
    /// live on every fill (<see cref="Val"/>), asked once per emitter rather than once per channel, so an
    /// emitter whose rate or size is keyed still ramps.</para>
    /// <para>One array per distinct emitter NODE, at first sight -- the accepted first-sight cost, like the
    /// model parse that produced the node. Four hundred torches of one model share one.</para></summary>
    private static Vector4[] RestOf(NodeDef nd)
    {
        Vector4[]? r = nd.EmRest;
        if (r != null) return r;
        var t = new Vector4[ChCount];
        for (int ord = 0; ord < ChCount; ord++)
        {
            ControllerTrack? c = nd.Controller(_chIds[ord]);
            t[ord] = Ch3Rest(ord)
                ? c != null && c.Values.Length >= 3
                      ? new Vector4(c.Values[0], c.Values[1], c.Values[2], 0f) : Vector4.Zero
                : new Vector4(c != null && c.Values.Length > 0 ? c.Values[0] : 0f, 0f, 0f, 0f);
        }
        // Published only once filled: the resolve is idempotent, so two threads racing cost one duplicate
        // array and no reader can see a half-written one.
        nd.EmRest = t;
        return t;
    }

    /// <summary>This part's animated value for one controller, or the node's own rest key when no animation has
    /// written it. <paramref name="b"/> is <see cref="DbEmitterChannels.BlockOf"/>, asked once per fill.</summary>
    private static float Ch1(Vector4[] rest, int b, int ord) => Val(rest, b, ord).X;

    /// <inheritdoc cref="Ch1"/>
    private static Vector3 Ch3(Vector4[] rest, int b, int ord)
    {
        Vector4 v = Val(rest, b, ord);
        return new Vector3(v.X, v.Y, v.Z);
    }

    /// <summary><see cref="DbEmitterChannels.Value"/>'s own rule, with the two lookups it repeated per channel
    /// hoisted out: the block's value when this part is animated and the channel is one it carries, the rest
    /// key otherwise.</summary>
    private static Vector4 Val(Vector4[] rest, int b, int ord)
    {
        int ch = _chBlock[ord];
        return b >= 0 && ch >= 0 ? DbEmitterChannels.At(b, ch) : rest[ord];
    }

    /// <summary>The rotation half of a world matrix, with the scale divided out. The database stores a part's
    /// placement as a matrix; the simulation's spawn basis and the type-1 billboard want the quaternion the
    /// engine kept beside it.</summary>
    private static Quat RotationOf(in Matrix4x4 w)
    {
        Vector3 x = new(w.M11, w.M12, w.M13), y = new(w.M21, w.M22, w.M23), z = new(w.M31, w.M32, w.M33);
        float lx = x.Length(), ly = y.Length(), lz = z.Length();
        if (lx <= 0f || ly <= 0f || lz <= 0f) return new Quat { W = 1f };
        var r = new Matrix4x4(x.X / lx, x.Y / lx, x.Z / lx, 0f,
                              y.X / ly, y.Y / ly, y.Z / ly, 0f,
                              z.X / lz, z.Y / lz, z.Z / lz, 0f,
                              0f, 0f, 0f, 1f);
        return Quaternion.CreateFromRotationMatrix(r);
    }

    /// <summary>The update mode, from the model's own <c>update</c> literal -- the same four-way string compare
    /// <c>MdlNodeEmitter::InternalCreateInstance</c> @0x4af5c0 makes. Case-insensitive, because the ASCII
    /// corpus spells it both ways and the engine's own compare is <c>strcasecmp</c>.</summary>
    private static int ModeOf(string update)
        => Eq(update, "Fountain") ? ModeFountain
         : Eq(update, "Explosion") ? ModeExplosion
         : Eq(update, "Lightning") ? ModeLightning
         : ModeSingle;

    /// <summary>The render type, from the model's <c>render</c> literal -- <c>PartEmitter::Initialize</c>
    /// @0x4aaaa0's own seven-way ladder, in its order. Anything it does not recognise stays 0, exactly as the
    /// engine's fall-through does.</summary>
    private static int RenderTypeOf(string render)
        => Eq(render, "Normal") ? 0
         : Eq(render, "Billboard_to_World_Z") ? 2
         : Eq(render, "Billboard_to_Local_Z") ? 1
         : Eq(render, "Linked") ? 3
         : Eq(render, "Aligned_to_World_Z") ? 4
         : Eq(render, "Aligned_to_Particle_Dir") ? 5
         : Eq(render, "Motion_Blur") ? 6
         : 0;

    /// <summary>The blend mode, from the model's <c>blend</c> literal: <c>PartEmitter::Initialize</c> stores 0
    /// for <c>Normal</c>, 1 for <c>Punch-Through</c> and 2 for anything else (which is additive).</summary>
    private static int BlendOf(string blend)
        => Eq(blend, "Normal") ? 0 : Eq(blend, "Punch-Through") ? 1 : 2;

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The engine's own <c>update</c> literal, compared the same way.</summary>
    private static int ModeOf(Span<byte> update)
    {
        if (Is(update, "Fountain")) return ModeFountain;
        if (Is(update, "Explosion")) return ModeExplosion;
        if (Is(update, "Lightning")) return ModeLightning;
        return ModeSingle;
    }
}
