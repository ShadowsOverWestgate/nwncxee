using CX.Engine;
using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>CX-120 seam-feed: <b>the light rig, fed from the scene database.</b> The DB-mode counterpart of
/// <c>SceneInputs.WalkLights</c>: same output, same <c>FrameInputs</c> calls, same arithmetic -- a different
/// source. Without it a DB frame draws in ambient alone, which is the single most visible hole in the mode.
/// <para><b>Why a sibling rather than a branch.</b> The engine walk's loop dereferences a <c>PartLight*</c>
/// about twenty ways and calls the engine's own <c>GetLightAdjustedRadius</c> through it. A DB light has no
/// such pointer, so threading one through would be twenty branches inside ninety lines of engine semantics.
/// The arithmetic BELOW the source, which is the part that must not drift, is ported here explicitly and is
/// what the self-check pins.</para>
/// <para><b>The identity.</b> <c>FrameInputs.RecordAreaLight</c> takes an id it STORES and compares and never
/// dereferences (the flicker table and the bake's fingerprint key on it), so a DB light hands over its part
/// index as that id. It is stable for the light's life and unique across the table, which is all the id ever
/// had to be -- and unlike the engine's pointer it is not recycled by the allocator.</para></summary>
internal static class DbLights
{
    /// <summary>A light at or past this range is the engine's DIRECTIONAL pair (the area's sun and ambient),
    /// not a local light: the same test the engine walk makes, through the same layout constant.</summary>
    private static float DirectionalRange => EngineLayout.DirectionalRange;

    private static int _walked, _admitted, _skipped;

    /// <summary>Gather this scene's lights into <c>FrameInputs</c>. The DB-mode replacement for
    /// <c>SceneInputs.WalkLights</c>; called once per world frame, on the game thread, before the draw.</summary>
    internal static void Walk(DbId scene, float radiusMult, CNWCArea area)
    {
        if (Unchanged(scene, radiusMult, area)) { _reused++; return; }
        int parts = DbInputs.LightCount, lits = SceneDb.Lights.High;
        FrameInputs.BeginAreaLights(parts + lits);
        FrameInputs.ResetLocalLights(parts + lits);
        _walked = 0; _admitted = 0; _skipped = 0; _static = 0; _areaAmb = 0; _areaDif = 0; _tile = 0; _deduped = 0;
        // Cleared per walk: an area pair that is no longer in the scene must stop lighting it. The SUN is
        // deliberately NOT cleared -- see AreaTerm.
        FrameInputs.HasAreaAmbient = false; FrameInputs.HasAreaDiffuse = false;
        // The bake's own walk opens with ours: it closes a STATIC set over the lights it is told about, and a
        // walk that never began leaves every light dynamic and every tile unbaked.
        LightmapBake.BeginWalk(area);

        // The TILE lights, from SceneDb.Lights -- the authoritative set. A tile's light node is not a part in
        // most tile trees, so the part-registered enumeration below saw only the few that happened to have
        // one (4 of 22 live) and the rest of the area simply never lit.
        for (int lit = 1; lit < lits; lit++)
        {
            if (!DbInputs.LitLight(lit, out DbLight l)) continue;
            // Same scene filter the object lights get: a GUI scene's 3D view must not be lit by the world's
            // tile lamps, and the table is one table for every scene.
            if (!SceneDb.Objects.Valid(l.Obj) || SceneDb.ObjScene[l.Obj.Index].Value != scene.Value) continue;
            Admit(in l, scene, radiusMult, area, tile: true);
        }

        // ...and the OBJECT lights, from the part registry. A tile light that DID become a part is in both
        // enumerations; it carries its Lit index, so it is dropped here rather than counted twice.
        for (int k = 0; k < parts; k++)
        {
            if (!DbInputs.Light(k, out DbLight l)) continue;
            if (l.Lit >= 0) { _deduped++; continue; }
            if (SceneDb.PartObj[l.Part].IsNull) { _skipped++; continue; }
            if (SceneDb.ObjScene[SceneDb.PartObj[l.Part].Index].Value != scene.Value) continue;
            Admit(in l, scene, radiusMult, area, tile: false);
        }

        // The same tail the engine walk closes with: a light that left the rig has to fade rather than vanish,
        // and the bake's static set is only a set once it is closed.
        SceneInputs.FinishWalk();
    }

    /// <summary>Is this frame's rig the one <see cref="FrameInputs"/> already holds? The walk rebuilds the
    /// whole rig -- the area-light arrays, the local-light set, the bake's static-set walk and the departure
    /// fade -- and in a settled area none of its inputs moved, so it was rebuilding an identical answer every
    /// world frame (seam-anim's perf audit).
    /// <para>The test is a SIGNATURE over the inputs the rig is a function of, not a dirty flag the writers
    /// have to remember to set: a missed writer would freeze an area's lighting, and that is a fault nobody
    /// would read as a bug. The registry's own revision short-circuits the scan when a light was added or
    /// dropped. The scan itself is one pass over the light records with no allocation and no bake work, which
    /// is the cheap half of what it replaces.</para>
    /// <para><c>ponytail:</c> the ceiling is <see cref="ResyncFrames"/> -- the rig is rebuilt anyway that
    /// often, so anything the signature does not cover costs at most that many frames of staleness rather
    /// than the life of the area. Narrow it by adding the term to the signature, not by lowering the
    /// number.</para></summary>
    private static bool Unchanged(DbId scene, float radiusMult, CNWCArea area)
    {
        if (++_sinceResync >= ResyncFrames) { _sinceResync = 0; return false; }
        ulong sig = Signature(scene, radiusMult, area);
        if (sig == _sig && _sigValid) return true;
        _sig = sig; _sigValid = true;
        return false;
    }

    /// <summary>Frames between unconditional rebuilds. Half a second at 60 fps.</summary>
    private const int ResyncFrames = 30;
    private static int _sinceResync;
    private static ulong _sig;
    private static bool _sigValid;
    /// <summary>Frames the rig was reused rather than rebuilt.</summary>
    internal static long Reused => _reused;
    private static long _reused;

    /// <summary>Everything the rig is a function of, folded into one value: the scene and the global radius
    /// multiplier, the area's palette pair (the two area terms come off it), and every light record's
    /// position, colour, reach, multiplier and enabled state.</summary>
    private static ulong Signature(DbId scene, float radiusMult, CNWCArea area)
    {
        ulong h = 1469598103934665603UL;
        Mix(ref h, scene.Value);
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(radiusMult));
        Mix(ref h, DbInputs.LightRev);
        if (!area.IsNull)
        {
            Mix(ref h, (uint)area.IsNight);
            Span<float> d = area.IsNight != 0 ? area.NightDiffuse : area.DayDiffuse;
            Span<float> a = area.IsNight != 0 ? area.NightAmbient : area.DayAmbient;
            for (int k = 0; k < 3; k++) { Mix(ref h, (uint)BitConverter.SingleToInt32Bits(d[k])); Mix(ref h, (uint)BitConverter.SingleToInt32Bits(a[k])); }
        }
        for (int k = 0; k < 3; k++)
        {
            Mix(ref h, (uint)BitConverter.SingleToInt32Bits(_sunAmb[k]));
            Mix(ref h, (uint)BitConverter.SingleToInt32Bits(_sunDif[k]));
        }
        int lits = SceneDb.Lights.High;
        for (int lit = 1; lit < lits; lit++)
            if (DbInputs.LitLight(lit, out DbLight l)) MixLight(ref h, in l);
        int parts = DbInputs.LightCount;
        for (int k = 0; k < parts; k++)
            if (DbInputs.Light(k, out DbLight l)) MixLight(ref h, in l);
        return h;
    }

    private static void MixLight(ref ulong h, in DbLight l)
    {
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Position.X));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Position.Y));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Position.Z));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Colour.X));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Colour.Y));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Colour.Z));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Radius));
        Mix(ref h, (uint)BitConverter.SingleToInt32Bits(l.Multiplier));
        Mix(ref h, l.Enabled ? 1u : 0u);
    }

    private static void Mix(ref ulong h, uint v) { h = (h ^ v) * 1099511628211UL; }

    /// <summary>One light into the frame's rig: the area-pair fork, the engine's admission tests, the
    /// adjusted radius/colour pair, and the bake's static verdict. Shared by both enumerations so a tile
    /// light and an object light cannot drift apart in how they are admitted.</summary>
    private static void Admit(in DbLight l, DbId scene, float radiusMult, CNWCArea area, bool tile)
    {
        _walked++;
        if (tile) _tile++;
        if (!l.Enabled) { _skipped++; return; }

        float range = l.Radius, inten = l.Multiplier;
        // The two index -1 lights, picked exactly as LightManager::UpdateLights @0x4c06c0 picks them: a light
        // reaching DirectionalRange or further is the AREA pair -- kind 0 the directional diffuse (and, when
        // it reaches like one, the sun), anything else the ambient -- and the last of each wins.
        if (range >= DirectionalRange) { AreaTerm(in l, area); return; }
        Vector3 rgb = l.Colour;
        if (range <= 0f || inten <= 0f || (rgb.X == 0f && rgb.Y == 0f && rgb.Z == 0f)) { _skipped++; return; }

        bool ambient = DbInputs.AmbientOnly(in l);
        Adjust(range, inten, radiusMult, ref rgb, out float adjR);
        Vector3 pos = l.Position;
        // The bake's verdict, asked the way the engine walk asks it -- except that the database, not a walk of
        // engine objects, says whether the light holds still. A static light is what a tile is LIT BY: mode 0
        // lights tiles from the baked pages and objects dynamically, which is the split that made DB tiles
        // black while objects beside them were fine.
        bool isStatic = LightmapBake.Record(l.Id, area, pos, adjR, rgb, ambient, l.Static, out int setIndex);
        FrameInputs.RecordAreaLight(l.Id, pos, adjR, rgb, ambient, isStatic, setIndex);
        _admitted++;
        if (isStatic) _static++;
        // A local (shadow-casting) light is a non-ambient one that asked to cast. A tile light says so in its
        // own record; an object light says so through its LightDef.
        bool casts = l.Lit >= 0 ? SceneDb.LitShadow[l.Lit] : l.Def is { Shadow: true };
        if (ambient || !casts) return;
        FrameInputs.RecordLocalLight(l.Id, pos, adjR, rgb);
    }

    /// <summary>Route one area light to the term its own kind drives, the way <c>FrameRead.ApplyAreaTerm</c>
    /// does for the engine path -- same curve, same sun rule, so the two modes light a surface identically.
    /// <para>The colour goes through the receiver's own gamma here (<c>AlloyLights.SrgbGamma</c>) because
    /// every consumer of <c>FrameInputs.Area*</c> expects it pre-compensated; handing it linear is a base
    /// term that looks washed out rather than merely wrong.</para></summary>
    private static void AreaTerm(in DbLight l, CNWCArea area)
    {
        bool isAmbient = DbInputs.AmbientOnly(in l);
        Vector3 c = Palette(area, isAmbient, l.Colour);
        var col = new Vector3(MathF.Pow(MathF.Max(c.X, 0f), AlloyLights.SrgbGamma),
                              MathF.Pow(MathF.Max(c.Y, 0f), AlloyLights.SrgbGamma),
                              MathF.Pow(MathF.Max(c.Z, 0f), AlloyLights.SrgbGamma));
        if (isAmbient)
        {
            FrameInputs.AreaAmbient = col;
            FrameInputs.HasAreaAmbient = true;
            _areaAmb++;
            return;
        }
        Vector3 toLight = l.Position;
        if (toLight.LengthSquared() <= 1e-8f) return;
        FrameInputs.AreaDiffuse = col;
        FrameInputs.AreaDiffuseDir = Vector3.Normalize(toLight);
        FrameInputs.HasAreaDiffuse = true;
        _areaDif++;
        // ...and the same light IS the sun when it reaches like one. +0xac points TO the light; the light
        // travels the other way, which is the direction the cascades render along. Set, never cleared: an
        // interior has no sun and the caster pass gates on FrameInputs.Interior for exactly that reason.
        if (l.Radius <= DirectionalRange) return;
        Vector3 travel = -Vector3.Normalize(toLight);
        if (travel.Z < FrameRead.DownwardZ) { FrameInputs.SunDir = travel; FrameInputs.HasSun = true; }
    }

    /// <summary>The area's own colour for the palette it is on and the term asked for, which is what mode 0's
    /// two directional lights carry and a DB light does not: the game writes both into the Aurora "sun" model
    /// by script (<c>CNWCArea::SetGlobalLightDiffuse</c> @0x802510 and <c>SetGlobalLightAmbient</c> @0x8022d0
    /// sprintf <c>controlpart sun gidy_sun_diff|gidy_sun_amb color</c>), so the model's own rest keys -- all
    /// the database has -- are the model's, not the area's.
    /// <para>What the game last sent through that text wins (<see cref="SunColour"/>): it is the day/night
    /// blend and any scripted palette. Until the first one arrives, the ARE pair <c>LoadArea</c> cached and
    /// <c>SetAsDay</c>/<c>SetAsNight</c> picked stands in.</para></summary>
    private static Vector3 Palette(CNWCArea area, bool ambient, Vector3 fallback)
    {
        if (ambient ? _sunAmbSet : _sunDifSet) return ambient ? _sunAmb : _sunDif;
        if (area.IsNull) return fallback;
        bool night = area.IsNight != 0;
        Span<float> c = ambient ? (night ? area.NightAmbient : area.DayAmbient)
                                : (night ? area.NightDiffuse : area.DayDiffuse);
        return c[0] == 0f && c[1] == 0f && c[2] == 0f ? fallback : new Vector3(c[0], c[1], c[2]);
    }

    private static int _areaAmb, _areaDif, _tile, _deduped, _static;

    /// <summary>The colour the game last set on the sun's ambient or diffuse part (<c>SunFns</c>, the seam's
    /// <c>controlpart</c>): the day/night blend and any scripted palette, which the ARE pair is only the
    /// load-time default of. Kept across areas -- the game sets it again on every day/night update.</summary>
    internal static void SunColour(bool ambient, Vector3 c)
    {
        if (ambient) { _sunAmb = c; _sunAmbSet = true; }
        else { _sunDif = c; _sunDifSet = true; }
    }

    private static Vector3 _sunAmb, _sunDif;
    private static bool _sunAmbSet, _sunDifSet;

    /// <summary>The engine's own lit radius and colour pair (<c>GetLightAdjustedRadius</c> @0x4c0c10 and
    /// <c>GetLightAdjustedColor</c> @0x4c0b60), ported: the radius is range x intensity times the global
    /// radius multiplier, boosted by the max colour channel when that exceeds 1, and the colour is then
    /// divided back down by the same max -- so an overbright light spends its surplus on REACH, not on hue.
    /// <para>This is the arithmetic that must not drift from the engine walk's, which is why it is one
    /// function with one self-check rather than a second copy of the comment block in that file. The
    /// overbright keep-back is Alloy's own (<c>AlloyLights.OverbrightK</c>), shared with it.</para></summary>
    internal static void Adjust(float range, float inten, float radiusMult, ref Vector3 rgb, out float adjR)
    {
        float maxc = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
        adjR = range * inten * radiusMult;
        if (maxc > 1f)
        {
            adjR *= maxc;
            float k = AlloyLights.OverbrightK(maxc);
            rgb = new Vector3(rgb.X * k, rgb.Y * k, rgb.Z * k);
        }
    }

    internal static string Stats()
        => $"dblights: {_reused} frames reused, {_walked} walked ({_tile} tile, {_deduped} deduped), {_admitted} admitted "
         + $"({_static} static), {_skipped} skipped, area amb/dif {_areaAmb}/{_areaDif}"
         + (FrameInputs.HasAreaAmbient ? "" : " NO AREA AMBIENT")
         + (FrameInputs.HasAreaDiffuse ? "" : " NO AREA DIFFUSE");

    /// <summary>Off-device: the arithmetic, and the admission. A light dropped here is an unlit area and a
    /// radius that drifted from the engine's is every lit sphere the wrong size -- #67's symptom, which read
    /// as "two lights can't overlay" and took a live A/B to find.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbLights.SelfCheck");

        // The area light arrives as console text (CNWCArea::SetGlobalLightDiffuse's own sprintf, %f format).
        check.Expect(CX.Alloy.Seam.SunFns.Parse("sun gidy_sun_diff color 0.250000 0.500000 1.000000"u8, out bool amb, out Vector3 sc)
                     == CX.Alloy.Seam.SunFns.Verdict.Colour && !amb && sc == new Vector3(0.25f, 0.5f, 1f),
                     "controlpart's sun diffuse line parses to its colour");
        check.Expect(CX.Alloy.Seam.SunFns.Parse("SUN gidy_sun_amb color 0.1 0.2 0.3"u8, out amb, out _)
                     == CX.Alloy.Seam.SunFns.Verdict.Colour && amb, "...the ambient part, case-blind as FindGob is");
        check.Expect(CX.Alloy.Seam.SunFns.Parse("lamp01 light color 1 1 1"u8, out _, out _)
                     == CX.Alloy.Seam.SunFns.Verdict.NoObject, "...and any other object answers as the engine's would");

        // A plain light: radius is range x intensity x the global multiplier, hue untouched.
        var rgb = new Vector3(0.5f, 0.6f, 0.7f);
        Adjust(10f, 2f, 1.5f, ref rgb, out float r);
        check.Expect(MathF.Abs(r - 30f) < 1e-4f, $"a plain light's radius is range x intensity x mult ({r})");
        check.Expect(rgb == new Vector3(0.5f, 0.6f, 0.7f), "...and its colour is untouched");

        // An overbright torch: the surplus goes to REACH and the hue is normalised back down.
        var torch = new Vector3(1.75f, 1.50f, 1.00f);
        Vector3 before = torch;
        Adjust(10f, 1f, 1f, ref torch, out float tr);
        check.Expect(MathF.Abs(tr - 17.5f) < 1e-3f,
                     $"an overbright light spends its surplus on reach ({tr}, expected range x maxc)");
        check.Expect(torch.X <= before.X && torch.Y <= before.Y && torch.Z <= before.Z,
                     "...and its colour comes back DOWN rather than tinting every lit surface");
        check.Expect(MathF.Abs(torch.X / torch.Y - before.X / before.Y) < 1e-3f,
                     "...keeping its hue exactly (the ratio between channels is unchanged)");

        // The admission rules, over a synthetic database. The departure FADE is off for the duration: the walk
        // closes with it (so a light that leaves is still emitted, shrinking, for Fadeout frames), and that is
        // mode 0's behaviour rather than something to re-prove here -- SceneInputs.FadeSelfCheck owns it.
        int fadeWas = Lighting.Fadeout.V;
        Lighting.Fadeout.V = 0;
        DbInputs.Reset();
        ModelDef m = LightModel();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = SceneDb.Objects.Alloc();
        DbFeed.InitObject(obj.Index);
        SceneDb.ObjScene[obj.Index] = scene;
        SceneDb.ObjVisible[obj.Index] = true;
        DbId h = SceneDb.Parts.Alloc();
        int p = h.Index;
        DbFeed.InitPart(p);
        SceneDb.PartObj[p] = obj;
        SceneDb.PartModel[p] = m;
        SceneDb.PartNode[p] = 1;
        SceneDb.PartEnabled[p] = true;
        SceneDb.PartWorld[p] = Matrix4x4.CreateTranslation(3f, 4f, 5f);
        DbInputs.PartCreated(p);

        Walk(scene, 1f, default);
        check.Expect(FrameInputs.LightCount == 1, $"a live light of this scene is admitted ({FrameInputs.LightCount})");
        check.Expect(FrameInputs.AreaLightId[0] == (IntPtr)(p + 1),
                     "...under its part index as an identity, which no allocator recycles");

        // ...and another scene's is not.
        DbId other = SceneDb.Scenes.Alloc();
        Walk(other, 1f, default);
        check.Expect(FrameInputs.LightCount == 0, "a light of another scene is not in this scene's rig");

        // An invisible object's light is off, which is what a door closing on a lit room looks like.
        SceneDb.ObjVisible[obj.Index] = false;
        Walk(scene, 1f, default);
        check.Expect(FrameInputs.LightCount == 0, "a light on an invisible object does not light");
        SceneDb.ObjVisible[obj.Index] = true;

        // A TILE light, which is the case the part registry never saw: the Lit table is enumerated directly,
        // so a light node that never became a part still lights its area.
        DbId lid = SceneDb.Lights.Alloc();
        int lk = lid.Index;
        SceneDb.LitObj[lk] = obj;
        SceneDb.LitPart[lk] = -1;
        SceneDb.LitPos[lk] = new Vector3(9f, 8f, 7f);
        SceneDb.LitColour[lk] = new Vector3(1f, 1f, 1f);
        SceneDb.LitRange[lk] = 6f;
        SceneDb.LitMultiplier[lk] = 1f;
        SceneDb.LitDynamic[lk] = false;
        SceneDb.LitShadow[lk] = true;
        Walk(scene, 1f, default);
        check.Expect(FrameInputs.LightCount == 2, $"a tile light with no part is admitted ({FrameInputs.LightCount})");
        int at = FrameInputs.AreaLightId[0] == DbInputs.LitId(lk) ? 0 : 1;
        check.Expect(FrameInputs.AreaLightId[at] == DbInputs.LitId(lk), "...under its own Lit record as an identity");
        // ...and LitDynamic=false is what the bake is TOLD. Whether it comes back static is the bake's answer,
        // and in DB mode it is "no" until the arena ranges are charted -- a static light with no page to be lit
        // from is an unlit one (LightmapBake.Produces).
        check.Expect(FrameInputs.AreaLightStatic[at] == LightmapBake.Produces,
                     $"LitDynamic=false reaches the bake as static, and the bake answers for its own ability to "
                   + $"produce a page (Produces={LightmapBake.Produces}, static={FrameInputs.AreaLightStatic[at]})");

        // ...and the same light reached through a part is ONE light, not two.
        SceneDb.LitPart[lk] = p;
        DbInputs.RegisterTileLight(p, lk);
        Walk(scene, 1f, default);
        check.Expect(FrameInputs.LightCount == 1, $"a tile light that also has a part is admitted once ({FrameInputs.LightCount})");
        SceneDb.Lights.Free(lid); SceneDb.Lights.TakeDirty();
        DbInputs.RegisterTileLight(p, -1);

        // A destroyed light leaves the rig in the same frame, with no sweep and no tombstone.
        DbInputs.PartDestroyed(p);
        Walk(scene, 1f, default);
        check.Expect(FrameInputs.LightCount == 0, "a destroyed light is out of the rig at once");

        SceneDb.Parts.Free(h); SceneDb.Objects.Free(obj);
        SceneDb.Scenes.Free(scene); SceneDb.Scenes.Free(other);
        SceneDb.Parts.TakeDirty(); SceneDb.Objects.TakeDirty(); SceneDb.Scenes.TakeDirty();
        DbInputs.Reset();
        Lighting.Fadeout.V = fadeWas;
        FrameInputs.BeginAreaLights(0);
        FrameInputs.ResetLocalLights(0);

        log.Info(check.Ok ? "DbLights.SelfCheck: passed"
                          : "DbLights.SelfCheck: FAILED -- a DB-mode area would be lit wrongly or not at all");
        return check.Ok;
    }

    private static ModelDef LightModel()
    {
        var m = new ModelDef { Name = "cx_lightcheck", ContentHash = 0x11D11D };
        var root = new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy };
        var light = new NodeDef
        {
            Name = "lamp", Index = 1, Parent = 0, Kind = NodeKind.Light,
            Light = new LightDef { Dynamic = true, Shadow = true },
            Controllers = new[]
            {
                new ControllerTrack { Type = Ctl.LightColor, Columns = 3, Times = new[] { 0f }, Values = new[] { 1f, 0.9f, 0.8f } },
                new ControllerTrack { Type = Ctl.LightRadius, Columns = 1, Times = new[] { 0f }, Values = new[] { 12f } },
                new ControllerTrack { Type = Ctl.LightMultiplier, Columns = 1, Times = new[] { 0f }, Values = new[] { 1f } },
            },
        };
        m.Nodes = new[] { root, light };
        m.NodeByName["root"] = 0; m.NodeByName["lamp"] = 1;
        return m;
    }
}
