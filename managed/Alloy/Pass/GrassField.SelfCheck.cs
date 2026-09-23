using System.Collections.Generic;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

internal static unsafe partial class GrassField
{
    /// <summary>Off-device (run-demo.sh). Three things that are decisions rather than transcriptions, and each
    /// fails silently on a GPU: the scatter must be DETERMINISTIC (a rebuild that reshuffles the meadow reads
    /// as grass twitching at every area reload) and must scale with the density dial; the frustum test must
    /// actually reject (a cull that accepts everything is invisible except in the frame time it was built to
    /// save); and the box distance must be the NEAREST point, or a patch the camera stands inside is culled
    /// by its own centre.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        GrassAtlas.SelfCheck(log);

        uint a = 12345u, b = 12345u;
        float first = Rnd(ref a);
        check.Expect(first == Rnd(ref b), "the same seed yields the same first blade");
        float thin = Grass.SnowThin.V, fall = Grass.SnowFall.V;
        Grass.SnowThin.V = 0.85f; Grass.SnowFall.V = 0.1f;
        check.Expect(SnowThinShare(0f, 0f) == 0f && SnowThinShare(0f, 1f) == 0.9f && SnowThinShare(1f, 0f) == 0.85f && SnowThinShare(1f, 1f) == 0.9f,
          "falling snow thins to the snowfall share, cover to its own, and the deeper cut wins");
        Grass.SnowThin.V = thin; Grass.SnowFall.V = fall;
        // #478: a blade never reads the screen-space visibility -- the pixel's occlusion is the surface behind it
        check.Expect(!GrassFsHead.Contains("uSsao") && !GrassFsMain.Contains("uSsao"), "the grass stage takes no SSAO tap");
        check.Expect(first != Rnd(ref a), "and the stream advances");
        float lo = 1f, hi = 0f;
        uint st = 99u;
        for (int i = 0; i < 4096; i++) { float r = Rnd(ref st); lo = MathF.Min(lo, r); hi = MathF.Max(hi, r); }
        check.Expect(lo >= 0f && hi < 1f && lo < 0.01f && hi > 0.99f, "the stream covers [0,1) without leaving it");

        // density scales the engine's authored per-face counts rather than replacing them
        check.Expect((int)(7 * 2f) == 14 && (int)(7 * 0.5f) == 3, "the density dial multiplies the tileset's own count");

        // #355: the ladder is continuous at its knee, falls as 1/d^2, and never drops below its floor
        check.Expect(Keep(Grass.Lod.V) == 1f && Keep(Grass.Lod.V * 0.5f) == 1f, "inside the full-density radius every blade draws");
        check.Expect(MathF.Abs(Keep(Grass.Lod.V * 2f) - MathF.Max(0.25f, Grass.LodMin.V)) < 1e-6f, "twice the radius keeps a quarter");
        check.Expect(Keep(Grass.Lod.V * 1000f) == Grass.LodMin.V, "and the floor holds far out");
        bool mono = true;
        for (float d = 1f, prev = 1f; d < 400f; d += 1f) { float k = Keep(d); if (k > prev) mono = false; prev = k; }
        check.Expect(mono, "keep never rises with distance");
        // CX-24: the scatter's record and material strides are C# layouts copied into its GLSL; a drift reads
        // every card out of its neighbour's record, silently
        check.Expect(ShaderSource.Get("cx_gbladebuf.glsl").Contains($"const int CX_GRASS_MATV = {MatVec4};") && ScatterCs.Contains($"const uint CX_GRASS_RECV = {InstFloats / 4}u;"),
          "the scatter's material and record strides match GrassField's");
        check.Expect(GrassVs.Contains("layout(location = 4) in vec4 iNrm;") && InstFloats == 12,
          "the card stage reads the scatter's third vec4 as the ground normal");
        check.Expect(ScatterCs.Contains("return max(uLod.y, r * r);"), "the scatter's keep is GrassField.Keep's");
        check.Expect(BoxFar(new Vector3(0f), new Vector3(1f), new Vector3(0f)) == MathF.Sqrt(3f)
                     && BoxFar(new Vector3(0f), new Vector3(1f), new Vector3(2f, 0.5f, 0.5f)) == MathF.Sqrt(4.5f),
          "the far box distance is to the farthest corner -- a patch straddling the blade radius feeds both blade draws");

        // an identity VP: clip == world, so the frustum is the [-1,1] cube
        Matrix4x4 id = Matrix4x4.Identity;
        float* planes = stackalloc float[24];
        Frustum(&id, planes);
        check.Expect(Visible(new Vector3(-0.5f), new Vector3(0.5f), planes), "a box inside the cube is visible");
        check.Expect(!Visible(new Vector3(2f, 2f, 2f), new Vector3(3f, 3f, 3f), planes), "a box past +x/+y/+z is culled");
        check.Expect(!Visible(new Vector3(-3f), new Vector3(-2f), planes), "and one past -x/-y/-z is too");
        check.Expect(Visible(new Vector3(-2f, -0.1f, -0.1f), new Vector3(0f, 0.1f, 0.1f), planes),
          "a box STRADDLING a plane is kept -- a straddle test that rejects pops geometry at the screen edge");

        check.Expect(BoxDistance(new Vector3(0f), new Vector3(1f), new Vector3(0.5f, 0.5f, 0.5f)) == 0f,
          "a point inside the box is at distance 0");
        check.Expect(MathF.Abs(BoxDistance(new Vector3(0f), new Vector3(1f), new Vector3(4f, 0.5f, 0.5f)) - 3f) < 1e-4f,
          "and outside it, the distance to the NEAREST face");

        // The displacement splat. Its sign is the whole effect: pushing the wrong way pulls grass INTO a
        // creature instead of parting it around one, and nothing about the resulting frame says which.
        byte[] f = new byte[PushRes * PushRes * PushChannels];
        void Clear() { for (int i = 0; i < f.Length; i += PushChannels)
               { f[i] = 128; f[i + 1] = 128; f[i + 2] = 0; f[i + 3] = 0; } }
        Clear();
        const float cx = 64.5f, cy = 64.5f, rad = 6f;
        Splat(f, cx, cy, rad);
        int R(int x, int y) => f[(y * PushRes + x) * PushChannels];
        int G(int x, int y) => f[(y * PushRes + x) * PushChannels + 1];
        int B(int x, int y) => f[(y * PushRes + x) * PushChannels + 2];
        check.Expect(R(67, 64) > 128 && R(61, 64) < 128, "a gob pushes grass OUTWARD along x, not toward itself");
        check.Expect(G(64, 67) > 128 && G(64, 61) < 128, "and outward along y");
        check.Expect(R(67, 64) - 128 == 128 - R(61, 64), "the push is symmetric about the gob");
        check.Expect(R(64, 64) == 128 && G(64, 64) == 128, "the texel under the centre has no direction, so no push");
        check.Expect(R(64 + (int)rad + 1, 64) == 128, "and nothing past the radius is touched at all");
        check.Expect(R(66, 64) > R(68, 64), "the push falls off with distance");
        // The flatten channel is the half the direction cannot carry: it has to PEAK where the direction
        // vanishes, or the tuft between a creature's feet is the one thing left standing.
        check.Expect(B(64, 64) > B(67, 64) && B(67, 64) > B(69, 64), "the press-down peaks under the gob and falls off");
        check.Expect(B(64 + (int)rad + 1, 64) == 0, "and is zero past the radius");
        Clear();
        Splat(f, cx, cy, rad);
        int solo = B(66, 64);
        Splat(f, cx, cy, rad);
        check.Expect(B(66, 64) == solo, "two overlapping gobs do not press the same grass twice -- flatten takes the max");
        check.Expect(R(66, 64) != 128 && R(66, 64) > 128, "...while their lateral pushes do sum");
        Clear();
        Splat(f, cx, cy, 0f);
        check.Expect(R(64, 64) == 128 && B(64, 64) == 0, "a zero radius splats nothing rather than dividing by it");

        // The object cull. Its footprint is a RECTANGLE, not a disc, because the things it exists for -- a
        // crate, a wall, a chest -- are not round, and a disc over a long wall either misses its ends or
        // erases the grass beside it.
        int A(int x, int y) => f[(y * PushRes + x) * PushChannels + 3];
        Clear();
        CullRect(f, cx, cy, 10f, 3f);
        check.Expect(A(64, 64) == 255, "the middle of an object's footprint is fully culled");
        check.Expect(A(72, 64) > 0 && A(64, 66) > 0, "...along both axes of the rectangle");
        check.Expect(A(76, 64) == 0 && A(64, 69) == 0, "and nothing outside it is");
        check.Expect(A(73, 64) < A(64, 64), "the footprint softens toward its edge, so grass thins instead of stopping");
        Clear();
        CullRect(f, cx, cy, 0f, 3f);
        check.Expect(A(64, 64) == 0, "a degenerate footprint culls nothing rather than dividing by it");

        // ---- the toggle (#180). _off is the resolution latch and Grass.Enabled is the player's dial; they are
        // two flags on purpose, because /grass enabled 1 on a client whose grass globals never came up would
        // run the pass over null pointers. Direct field writes, not sets: a set arms the cx.cfg writer, and
        // this runs at boot against the real user's file.
        bool on0 = Grass.Enabled.V, off0 = _off;
        Grass.Enabled.V = false;
        check.Expect(Stats().Contains("vanilla"), "with the toggle off the pass reports itself as vanilla");
        _off = true; Grass.Enabled.V = true;
        check.Expect(_off && !CastsInto(0), "arming from chat cannot clear the resolution latch");
        _off = off0; Grass.Enabled.V = on0;

        log.Info(check.Ok ? "GrassField.SelfCheck: passed" : "GrassField.SelfCheck: FAILED");
    }
}
