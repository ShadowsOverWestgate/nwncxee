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
/// <para><b>Mode 0 is a transcription.</b> <see cref="EmitterSim.FillEngine"/> reads exactly the fields the
/// pre-CX-120 code read, in the same order, and <see cref="EmitterSim.StoreEngine"/> writes back exactly the
/// ones it wrote. The one consolidation is the transform: <c>Part::GetWorld</c> and <c>Gob::GetPosition</c> are
/// called once here instead of up to three times inside the step, which cannot change a value because neither
/// moves within a frame.</para></summary>
internal struct Emitter
{
    /// <summary>The engine <c>PartEmitter*</c>, or zero in DB mode.</summary>
    internal IntPtr Em;
    /// <summary>The database part index, or -1 in engine mode. The discriminant.</summary>
    internal int Part;
    /// <summary>The owning database object, in DB mode.</summary>
    internal DbId Obj;
    /// <summary>The database's chunk-spawn scene, in DB mode.</summary>
    internal DbId Scene;

    /// <summary>True when this snapshot came from the scene database.</summary>
    internal bool Db => Part >= 0;

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
    internal int EngineCount;

    /// <summary>The simulation's own state.</summary>
    internal EmitMut M;
}

internal static unsafe partial class EmitterSim
{
    /// <summary>Flag bits 3 and 9 of <see cref="EngineLayout.EmitterFlags"/>: <c>MdlNodeEmitter::IsTinted</c>
    /// @0x4aafc0 is <c>flags &gt;&gt; 3 &amp; 1</c> and <c>IsSplat</c> @0x4ab070 is <c>flags &gt;&gt; 9 &amp;
    /// 1</c> -- read off the word with the other nine rather than through two more calls.</summary>
    private const int FTinted = 1 << 3, FSplat = 1 << 9;

    /// <summary>Fill a snapshot from the ENGINE's views. A literal transcription of what the pre-CX-120
    /// simulation read, in its order; <paramref name="e"/>'s mutable half comes from the engine's own fields,
    /// so an emitter the engine's walk stepped last frame continues from where it left off.
    /// <para>False means the emitter has no model node, which is the one case the old <c>Open</c>/<c>Advance</c>
    /// pair bailed on.</para></summary>
    internal static bool FillEngine(IntPtr em, ref Emitter e)
    {
        var ev = new PartEmitter(em);
        var part = new CX.Engine.Part(em);
        var node = new MdlNodeEmitter(part.Node.Ptr);
        if (node.IsNull) return false;
        CX.Engine.Gob gob = part.Gob;

        e = default;
        e.Em = em;
        e.Part = -1;

        e.Flags = node.Flags;
        e.RenderOrder = node.RenderOrder;
        e.Chunk = node.ChunkModel[0] != 0;
        e.Loop = node.Loop != 0;
        e.NoCull = node.NoCull != 0;
        e.Splat = (e.Flags & FSplat) != 0;
        e.Tinted = (e.Flags & FTinted) != 0;
        e.ConeCap = node.ConeCap;
        e.WindStrength = node.WindStrength;
        e.WindFalloff = node.WindFalloff;
        e.GridX = node.GridX;
        e.GridY = node.GridY;
        e.Mode = ModeOf(node.Update);

        e.Type = ev.Type;
        e.Blend = ev.Blend;
        e.LifeInterp = ev.LifeInterp;
        e.BirthRate = ev.BirthRate;
        e.Life = ev.Life;
        e.Velocity = ev.Velocity;
        e.RandVel = ev.RandVel;
        e.Spread = ev.Spread;
        e.Spin = ev.Spin;
        e.Gravity = ev.Gravity;
        e.Bounce = ev.Bounce;
        e.CombineTime = ev.CombineTime;
        e.P2pDrag = ev.P2pDrag;
        e.P2pGrav = ev.P2pGrav;
        e.Bezier2 = ev.Bezier2;
        e.Bezier3 = ev.Bezier3;
        e.DeadSpace = ev.DeadSpace;
        e.SpawnX = ev.SpawnX;
        e.SpawnY = ev.SpawnY;
        e.BlurLength = ev.BlurLength;
        e.BoltDelay = ev.BoltDelay;
        e.BoltRadius = ev.BoltRadius;
        e.ZigZag = ev.ZigZag;
        e.AlphaStart = ev.AlphaStart;
        e.AlphaEnd = ev.AlphaEnd;
        e.Fps = ev.Fps;
        e.FrameStart = ev.FrameStart;
        e.FrameEnd = ev.FrameEnd;
        e.SizeStartX = ev.SizeStartX;
        e.SizeEndX = ev.SizeEndX;
        e.SizeStartY = ev.SizeStartY;
        e.SizeEndY = ev.SizeEndY;
        e.ViewRadius = ev.ViewRadius;
        e.ColorStart = ev.ColorStart;
        e.ColorEnd = ev.ColorEnd;

        // The transform, once. The engine's Part::GetWorld is pure within a frame, so folding its three call
        // sites into one cannot change a value -- only the count.
        if (_getWorld != null)
        {
            Vector3 wp;
            Quat wq;
            _getWorld(em, &wp, (float*)&wq);
            e.WorldPos = wp; e.WorldRot = wq;
        }
        else
        {
            // Off-device only. Install refuses to arm without Part::GetWorld, so a live frame never gets
            // here; the self-check's synthetic emitter has no tree to walk and states its own transform.
            e.WorldPos = ev.Pos; e.WorldRot = ev.Orient;
        }
        e.HasOwner = !gob.IsNull;
        if (e.HasOwner)
        {
            e.OwnerPos = gob.Position;
            e.OwnerRot = gob.Rot;
            e.ForceAnimated = gob.IsForceAnimated() != 0;
            e.Scale = gob.GetAdjustedScale();
        }
        else
        {
            e.OwnerPos = part.Pos;
            e.Scale = 1f;
        }
        e.PartScale = part.Scale;

        CX.Engine.Gob target = ev.P2pTarget;
        e.HasTarget = !target.IsNull;
        if (e.HasTarget)
        {
            AurXform.V3 t = AurXform.Position(target.Ptr);
            e.TargetPos = new Vector3(t.X, t.Y, t.Z);
            e.TargetRot = AurXform.Orientation(target.Ptr);
        }
        e.Detonate = ev.Detonate != 0;
        e.EngineCount = ev.Count;

        e.M.Pos = ev.Pos;
        e.M.Orient = ev.Orient;
        e.M.PrevPos = ev.PrevPos;
        e.M.PrevRot = ev.PrevRot;
        e.M.PrevWorldPos = ev.PrevWorldPos;
        e.M.PrevWorldRot = ev.PrevWorldRot;
        Span<float> b = ev.Basis;
        e.M.Bx = new Vector3(b[0], b[1], b[2]);
        e.M.By = new Vector3(b[3], b[4], b[5]);
        e.M.Bz = new Vector3(b[6], b[7], b[8]);
        e.M.P2pLeftover = ev.P2pLeftover;
        e.M.SpawnAcc = ev.SpawnAcc;
        e.M.ModeClock = ev.ModeClock;
        e.M.Seeded = ev.Seeded;
        e.M.Birthing = ev.Birthing;
        var bolt = new LightningEmitter(em);
        e.M.BoltLastDelta = bolt.LastDelta;
        e.M.BoltLastPos = bolt.LastPos;
        return true;
    }

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

        e = default;
        e.Em = IntPtr.Zero;
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

        e.AlphaEnd = Ch1(part, nd, Ctl.EmAlphaEnd, 0f);
        e.AlphaStart = Ch1(part, nd, Ctl.EmAlphaStart, 0f);
        e.BirthRate = Ch1(part, nd, Ctl.EmBirthRate, 0f);
        e.Bounce = Ch1(part, nd, Ctl.EmBounceCo, 0f);
        e.ColorEnd = Ch3(part, nd, Ctl.EmColorEnd);
        e.ColorStart = Ch3(part, nd, Ctl.EmColorStart);
        e.CombineTime = Ch1(part, nd, Ctl.EmCombineTime, 0f);
        e.P2pDrag = Ch1(part, nd, Ctl.EmDrag, 0f);
        e.Fps = Ch1(part, nd, Ctl.EmFps, 0f);
        e.FrameEnd = Ch1(part, nd, Ctl.EmFrameEnd, 0f);
        e.FrameStart = Ch1(part, nd, Ctl.EmFrameStart, 0f);
        e.P2pGrav = Ch1(part, nd, Ctl.EmGrav, 0f);
        e.Life = Ch1(part, nd, Ctl.EmLifeExp, 0f);
        e.Gravity = Ch1(part, nd, Ctl.EmMass, 0f);
        e.Bezier2 = Ch1(part, nd, Ctl.EmP2PBezier2, 0f);
        e.Bezier3 = Ch1(part, nd, Ctl.EmP2PBezier3, 0f);
        e.Spin = Ch1(part, nd, Ctl.EmParticleRot, 0f);
        e.RandVel = Ch1(part, nd, Ctl.EmRandVel, 0f);
        e.SizeStartX = Ch1(part, nd, Ctl.EmSizeStart, 0f);
        e.SizeEndX = Ch1(part, nd, Ctl.EmSizeEnd, 0f);
        e.SizeStartY = Ch1(part, nd, Ctl.EmSizeStartY, 0f);
        e.SizeEndY = Ch1(part, nd, Ctl.EmSizeEndY, 0f);
        e.Spread = Ch1(part, nd, Ctl.EmSpread, 0f);
        e.DeadSpace = Ch1(part, nd, Ctl.EmThreshold, 0f);
        e.Velocity = Ch1(part, nd, Ctl.EmVelocity, 0f);
        e.SpawnX = Ch1(part, nd, Ctl.EmXSize, 0f);
        e.SpawnY = Ch1(part, nd, Ctl.EmYSize, 0f);
        e.BlurLength = Ch1(part, nd, Ctl.EmBlurLength, 0f);
        e.BoltDelay = Ch1(part, nd, Ctl.EmLightningDelay, 0f);
        e.BoltRadius = Ch1(part, nd, Ctl.EmLightningRadius, 0f);
        e.ZigZag = Ch1(part, nd, Ctl.EmLightningScale, 0f);
        e.Detonate = Ch1(part, nd, Ctl.EmDetonate, 0f) != 0f;

        Matrix4x4 w = SceneDb.PartWorld[part];
        e.WorldPos = new Vector3(w.M41, w.M42, w.M43);
        e.WorldRot = RotationOf(in w);
        e.ViewRadius = ViewRadiusOf(in e, Ch1(part, nd, Ctl.EmSizeMid, 0f), Ch1(part, nd, Ctl.EmSizeMidY, 0f));

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
        e.EngineCount = 0;

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
    private static float ViewRadiusOf(in Emitter e, float sizeMid, float sizeMidY)
    {
        float accel = MathF.Abs(e.Gravity * Gravity);
        float life = MathF.Max(e.Life, 0f);
        float travel = accel * 0.5f * life * life + (e.Velocity + e.RandVel) * life;
        float hx = MathF.Max(sizeMid, MathF.Max(e.SizeEndX, e.SizeStartX)) * 0.5f;
        float hy = MathF.Max(sizeMidY, MathF.Max(e.SizeEndY, e.SizeStartY)) * 0.5f;
        if (hy <= 0f) hy = hx;
        return travel + MathF.Sqrt(hx * hx + hy * hy);
    }

    /// <summary>This part's animated value for one controller, or the node's own rest key when no animation has
    /// written it. One table lookup and one array read per parameter.</summary>
    private static float Ch1(int part, NodeDef nd, int type, float def)
    {
        ControllerTrack? c = nd.Controller(type);
        float rest = c != null && c.Values.Length > 0 ? c.Values[0] : def;
        return DbEmitterChannels.Value(part, type, new Vector4(rest, 0f, 0f, 0f)).X;
    }

    /// <inheritdoc cref="Ch1"/>
    private static Vector3 Ch3(int part, NodeDef nd, int type)
    {
        ControllerTrack? c = nd.Controller(type);
        var rest = c != null && c.Values.Length >= 3
                 ? new Vector4(c.Values[0], c.Values[1], c.Values[2], 0f) : Vector4.Zero;
        Vector4 v = DbEmitterChannels.Value(part, type, rest);
        return new Vector3(v.X, v.Y, v.Z);
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
