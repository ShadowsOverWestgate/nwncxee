using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>CX-120 seam-feed: <b>where an emitter's animated tracks land.</b> seam-anim samples every
/// controller the pose columns do not carry and hands it to <c>DbAnim.ChannelSink</c>; with no sink installed
/// those tracks are sampled by nobody and counted as such. This is the sink for emitter parts.
/// <para><b>Sparse on purpose.</b> An emitter declares about thirty-five animatable channels
/// (<see cref="Ctl"/>'s <c>Em*</c> block, file ids 80..472) and a real one animates two or three. A dense
/// column over every emitter would be megabytes to hold a handful of floats, so a block is allocated on the
/// first write an emitter receives and freed when its part dies -- an emitter that animates nothing costs
/// one int.</para>
/// <para><b>Blend targets, not samples</b>, the same contract the light columns keep: a controller writes
/// <c>lerp(column, key, weight)</c>, so a crossfade between two animations crossfades the emitter's rates
/// too. The block is seeded from the node's rest keys on its first write, because nothing else knows when an
/// emitter starts being animated.</para>
/// <para>The particle simulation does not read this yet -- <c>EmitterSim</c> is still engine-keyed. This is
/// the half that has to exist before it can: the values are sampled, blended and queryable, and the
/// "sampled by NOBODY" counter stops counting them.</para></summary>
internal static class DbEmitterChannels
{
    /// <summary>Emitters that may animate at once. An area's worth of animated emitters is a handful; past
    /// this the write is refused and counted, never silently dropped.</summary>
    internal const int MaxBlocks = 256;

    /// <summary>The controller ids this sink accepts, in order. Index into a block is the position here.</summary>
    private static readonly int[] _ids =
    {
        Ctl.EmAlphaEnd, Ctl.EmAlphaStart, Ctl.EmBirthRate, Ctl.EmBounceCo, Ctl.EmColorEnd,
        Ctl.EmColorStart, Ctl.EmCombineTime, Ctl.EmDrag, Ctl.EmFps, Ctl.EmFrameEnd,
        Ctl.EmFrameStart, Ctl.EmGrav, Ctl.EmLifeExp, Ctl.EmMass, Ctl.EmP2PBezier2,
        Ctl.EmP2PBezier3, Ctl.EmParticleRot, Ctl.EmRandVel, Ctl.EmSizeStart, Ctl.EmSizeEnd,
        Ctl.EmSizeStartY, Ctl.EmSizeEndY, Ctl.EmSpread, Ctl.EmThreshold, Ctl.EmVelocity,
        Ctl.EmXSize, Ctl.EmYSize, Ctl.EmBlurLength, Ctl.EmLightningDelay, Ctl.EmLightningRadius,
        Ctl.EmLightningScale, Ctl.EmDetonate, Ctl.EmAlphaMid, Ctl.EmColorMid,
    };

    internal static int Channels => _ids.Length;

    /// <summary>Controller id -> channel index, or -1. Built once from <see cref="_ids"/>; the ids are sparse
    /// over 80..472, so a direct table is 473 bytes and beats a search per sampled key.</summary>
    private static readonly short[] _index = BuildIndex();

    private static short[] BuildIndex()
    {
        int max = 0;
        foreach (int id in _ids) if (id > max) max = id;
        var t = new short[max + 1];
        Array.Fill(t, (short)-1);
        for (int k = 0; k < _ids.Length; k++) t[_ids[k]] = (short)k;
        return t;
    }

    /// <summary>Channel index for a controller id, or -1 when it is not an emitter channel.</summary>
    internal static int ChannelOf(int type)
        => (uint)type < (uint)_index.Length ? _index[type] : -1;

    private static readonly Vector4[] _value = new Vector4[MaxBlocks * 34];
    private static readonly int[] _blockOf = new int[SceneDb.MaxParts];
    private static readonly int[] _partOf = new int[MaxBlocks];
    private static int _blockN;
    private static long _writes, _refused, _unknown;

    internal static int Blocks => _blockN;

    /// <summary>The sink seam-anim calls. Hot: a table lookup, a block index and a lerp.</summary>
    internal static void Write(int part, int type, Vector4 value, float weight)
    {
        int ch = ChannelOf(type);
        if (ch < 0) { _unknown++; return; }
        int b = BlockFor(part);
        if (b < 0) { _refused++; return; }
        int at = b * Channels + ch;
        _value[at] = weight >= 1f ? value : Vector4.Lerp(_value[at], value, weight);
        _writes++;
        SceneDb.TouchPart(part, Dirty.Material);
    }

    /// <summary>This part's animated value for <paramref name="type"/>, or <paramref name="rest"/> when no
    /// animation has written it. The same gate shape the light columns keep: a block that does not exist
    /// means "unwritten", so a real value of zero is not mistaken for one.</summary>
    internal static Vector4 Value(int part, int type, Vector4 rest)
    {
        int ch = ChannelOf(type);
        if (ch < 0 || (uint)part >= SceneDb.MaxParts) return rest;
        int b = _blockOf[part] - 1;
        return b < 0 ? rest : _value[b * Channels + ch];
    }

    private static int BlockFor(int part)
    {
        if ((uint)part >= SceneDb.MaxParts) return -1;
        int b = _blockOf[part] - 1;
        if (b >= 0) return b;
        if (_blockN >= MaxBlocks) return -1;
        b = _blockN++;
        _blockOf[part] = b + 1;
        _partOf[b] = part;
        Seed(part, b);
        return b;
    }

    /// <summary>Seed a fresh block from the node's own rest keys, so the first blended write lerps from the
    /// value the emitter actually had rather than from zero.</summary>
    private static void Seed(int part, int b)
    {
        ModelDef? m = SceneDb.PartModel[part];
        NodeDef? nd = m != null && (uint)SceneDb.PartNode[part] < (uint)m.Nodes.Length
                    ? m.Nodes[SceneDb.PartNode[part]] : null;
        for (int ch = 0; ch < Channels; ch++)
        {
            ControllerTrack? c = nd?.Controller(_ids[ch]);
            _value[b * Channels + ch] = c == null || c.Values.Length == 0 ? default
                : new Vector4(c.Values[0],
                              c.Columns > 1 && c.Values.Length > 1 ? c.Values[1] : 0f,
                              c.Columns > 2 && c.Values.Length > 2 ? c.Values[2] : 0f,
                              c.Columns > 3 && c.Values.Length > 3 ? c.Values[3] : 0f);
        }
    }

    /// <summary>This part died or was re-tenanted: drop its block, so a recycled part index cannot inherit
    /// another emitter's animation. Swap-with-last, like every other table here.</summary>
    internal static void Forget(int part)
    {
        if ((uint)part >= SceneDb.MaxParts) return;
        int b = _blockOf[part] - 1;
        if (b < 0) return;
        _blockOf[part] = 0;
        int last = --_blockN;
        if (b != last)
        {
            Array.Copy(_value, last * Channels, _value, b * Channels, Channels);
            _partOf[b] = _partOf[last];
            _blockOf[_partOf[b]] = b + 1;
        }
    }

    internal static void Reset()
    {
        for (int b = 0; b < _blockN; b++) _blockOf[_partOf[b]] = 0;
        _blockN = 0; _writes = 0; _refused = 0; _unknown = 0;
    }

    /// <summary>Install the sink. Called at boot beside the feed's other lifetime wiring.</summary>
    internal static void Attach()
    {
        DbAnim.ChannelSink = Write;
    }

    internal static string Stats()
        => $"dbemit: {_blockN} animated, {_writes} writes"
         + (_refused > 0 ? $" REFUSED {_refused} (past {MaxBlocks} blocks)" : "")
         + (_unknown > 0 ? $", {_unknown} non-emitter channel(s)" : "");

    /// <summary>Off-device: the sparse mapping, the blend, the rest-key seed and the recycle. An emitter
    /// whose block leaked to a reused part index would animate with a stranger's rates, which reads as a
    /// visual effect behaving differently each time it plays.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbEmitterChannels.SelfCheck");
        Reset();

        check.Expect(ChannelOf(Ctl.EmBirthRate) >= 0, "an emitter channel maps");
        check.Expect(ChannelOf(Ctl.LightColor) < 0, "a light channel is not an emitter channel");
        check.Expect(ChannelOf(Ctl.Position) < 0, "...nor is a geometry one");
        check.Expect(ChannelOf(int.MaxValue) < 0, "...nor is an id past the table");

        ModelDef m = EmitterModel();
        DbId obj = SceneDb.Objects.Alloc();
        DbFeed.InitObject(obj.Index);
        DbId h = SceneDb.Parts.Alloc();
        int p = h.Index;
        DbFeed.InitPart(p);
        SceneDb.PartObj[p] = obj;
        SceneDb.PartModel[p] = m;
        SceneDb.PartNode[p] = 1;

        // Unwritten reads the caller's rest value, not zero.
        check.Expect(Value(p, Ctl.EmBirthRate, new Vector4(9f, 0f, 0f, 0f)).X == 9f,
                     "an unwritten channel reads the caller's rest value");
        check.Expect(Blocks == 0, "...and allocates no block for asking");

        // A full-weight write lands exactly; the block seeds from the node's rest keys.
        Write(p, Ctl.EmBirthRate, new Vector4(20f, 0f, 0f, 0f), 1f);
        check.Expect(Blocks == 1, "the first write allocates one block");
        check.Expect(Value(p, Ctl.EmBirthRate, default).X == 20f, "a full-weight write lands exactly");
        // The seed is the node's own rest key (5), so a half-weight write from it lands halfway.
        check.Expect(Value(p, Ctl.EmVelocity, default).X == 5f,
                     "an untouched channel of an allocated block still reads its REST key, not zero");
        Write(p, Ctl.EmVelocity, new Vector4(15f, 0f, 0f, 0f), 0.5f);
        check.Expect(MathF.Abs(Value(p, Ctl.EmVelocity, default).X - 10f) < 1e-4f,
                     "a half-weight write blends from the value the emitter had (5 -> 15 at 0.5 = 10)");

        // A non-emitter channel is counted, not applied.
        long before = _unknown;
        Write(p, Ctl.LightColor, Vector4.One, 1f);
        check.Expect(_unknown == before + 1, "a non-emitter channel is counted rather than applied");

        // The recycle: a dead part's block must not reach its successor.
        Forget(p);
        check.Expect(Blocks == 0, "a destroyed emitter drops its block");
        check.Expect(Value(p, Ctl.EmBirthRate, new Vector4(7f, 0f, 0f, 0f)).X == 7f,
                     "...and the part index reads unwritten again, not the dead emitter's rate");

        // Two blocks, drop the FIRST: the swap-with-last must keep the survivor addressable.
        DbId h2 = SceneDb.Parts.Alloc();
        int q = h2.Index;
        DbFeed.InitPart(q);
        SceneDb.PartObj[q] = obj; SceneDb.PartModel[q] = m; SceneDb.PartNode[q] = 1;
        Write(p, Ctl.EmBirthRate, new Vector4(1f, 0f, 0f, 0f), 1f);
        Write(q, Ctl.EmBirthRate, new Vector4(2f, 0f, 0f, 0f), 1f);
        check.Expect(Blocks == 2, "two animated emitters, two blocks");
        Forget(p);
        check.Expect(Blocks == 1 && Value(q, Ctl.EmBirthRate, default).X == 2f,
                     "dropping the first block leaves the second addressable and unchanged");

        Forget(q);
        SceneDb.Parts.Free(h); SceneDb.Parts.Free(h2); SceneDb.Objects.Free(obj);
        SceneDb.Parts.TakeDirty(); SceneDb.Objects.TakeDirty();
        Reset();

        log.Info(check.Ok ? "DbEmitterChannels.SelfCheck: passed"
                          : "DbEmitterChannels.SelfCheck: FAILED -- an emitter would animate with the wrong "
                          + "rates, or with a dead emitter's");
        return check.Ok;
    }

    private static ModelDef EmitterModel()
    {
        var m = new ModelDef { Name = "cx_emitcheck", ContentHash = 0xE817E8 };
        var root = new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy };
        var emit = new NodeDef
        {
            Name = "puff", Index = 1, Parent = 0, Kind = NodeKind.Emitter,
            Emitter = new EmitterDef { Update = "fountain", Render = "normal" },
            Controllers = new[]
            {
                new ControllerTrack { Type = Ctl.EmVelocity, Columns = 1, Times = new[] { 0f }, Values = new[] { 5f } },
            },
        };
        m.Nodes = new[] { root, emit };
        m.NodeByName["root"] = 0; m.NodeByName["puff"] = 1;
        return m;
    }
}
