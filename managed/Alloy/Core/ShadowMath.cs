using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// Pure light-space matrix math for the shadow-map pipeline (#65). No engine/GL deps -- exercised
/// headless by <see cref="SelfCheck"/>. Built with System.Numerics (row-vector, row-major, RH). NWN's
/// world is Z-up; the GL-facing layout/handedness reconciliation happens where these feed a shader
/// (slice 3, empirical against the depth-map debug view -- research doc §6.5).
/// </summary>
public static class ShadowMath
{
    /// <summary>
    /// Light view-projection for a directional light travelling along unit <paramref name="lightDir"/>,
    /// fitted to the world-space sphere (<paramref name="center"/>, <paramref name="radius"/>).
    /// <paramref name="upstreamMargin"/> extends the frustum *toward the light* so casters standing between
    /// the light and the fitted sphere still fall inside the near plane (a tall object just outside the fit is
    /// the classic near-clip miss). The eye steps back by <c>radius + upstreamMargin</c>, near sits at the eye
    /// (0), far reaches the sphere's far tangent (<c>2*radius + upstreamMargin</c>). Maps world points into
    /// light clip space -- the transform the depth pass renders with and the receiver samples.
    /// </summary>
    public static Matrix4x4 DirectionalLightViewProj(Vector3 lightDir, Vector3 center, float radius, float upstreamMargin, int mapSize = 0)
    {
        Vector3 dir = Normalize(lightDir);
        // Orthonormal light basis (right, up, dir). Snap the centre to whole shadow-texel steps in the
        // right/up plane so the map doesn't crawl/shimmer as the (moving) centre slides sub-texel -- the
        // classic CSM texel snap. mapSize<=0 disables it (the pure-math SelfCheck cases).
        Vector3 up = PerpendicularUp(dir);
        Vector3 right = Normalize(Vector3.Cross(up, dir));
        up = Vector3.Cross(dir, right);
        if (mapSize > 0)
        {
            float texel = 2f * radius / mapSize;
            float cr = MathF.Round(Vector3.Dot(center, right) / texel) * texel;
            float cu = MathF.Round(Vector3.Dot(center, up) / texel) * texel;
            // #306: the along-light component snaps too. It never affected which texel a caster lands on,
            // but leaving it continuous made the matrix a function of the RAW focus -- so no two walking
            // frames ever produced the same map, and the cascade static half could never be reused. Snapped,
            // the whole matrix is quantised: a sub-texel focus move reproduces it bit-identical, which is
            // what lets CasterPass key a cascade's static bake on the matrix itself. Depth shifts by at most
            // one texel step, identically for caster and receiver, so the comparison is unmoved.
            float cd = MathF.Round(Vector3.Dot(center, dir) / texel) * texel;
            center = right * cr + up * cu + dir * cd;
        }
        Vector3 eye = center - dir * (radius + upstreamMargin);
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, center, up);
        Matrix4x4 proj = Matrix4x4.CreateOrthographic(2f * radius, 2f * radius, 0f, 2f * radius + upstreamMargin);
        return view * proj * D3DToGlClipZ;
    }

    /// <summary>
    /// Half-extent of sun cascade <paramref name="i"/> of <paramref name="count"/>, out to
    /// <paramref name="maxDist"/> world units (#94). The cascades are CONCENTRIC on the shadow focus rather
    /// than fitted to camera-frustum corners: NWN's chase camera always looks at the focus, so a disc around
    /// it is very nearly the visible region, and a fit that does not depend on camera orientation cannot
    /// re-shape as the camera orbits -- which is what keeps the texel snap locked to the world and the shadow
    /// edges still (a crawling map centre reads as "banding").
    /// <para>Measured, not assumed (#99): the chase camera sits 12-25u BEHIND the PC, so a frustum-split
    /// cascade 0 covers the ground between camera and player while concentric centres it on the player.
    /// Across fov 45-65deg the split fit is 1.5-3x COARSER at the player's feet at every focus depth past
    /// ~8u, tight AABB included; <c>CasterPass.FitReadout</c> prints the live pair that would re-open it. What
    /// <see cref="SphereInCameraFrustum"/> refuses is narrower still: the CAPTURED QUATERNION at present time,
    /// which belongs to whichever pass set the view last, a UI/ortho one as often as the scene's.</para>
    /// <para>Radii halve inward, so cascade 0 is <c>maxDist / 2^(count-1)</c> -- a 15u half-extent at the
    /// shipped 4 x 120, i.e. ~0.015 world units per texel across a 2048 map. Halving is what makes the texel
    /// density ratio between neighbours constant, so no one cascade is the blurry one.</para>
    /// </summary>
    public static float CascadeRadius(int i, int count, float maxDist)
    {
        if (count < 1) count = 1;
        if (i < 0) i = 0; else if (i >= count) i = count - 1;
        return maxDist / (1 << (count - 1 - i));
    }

    /// <summary>
    /// One face of a point light's depth cube (slice 6): a 90&deg; perspective from <paramref name="lightPos"/>
    /// along cube face <paramref name="face"/> (0..5 = +X,-X,+Y,-Y,+Z,-Z), fitted to [<paramref name="near"/>,
    /// <paramref name="far"/>]. Uses the standard GL cube-map face look/up basis, so the rendered face round-trips
    /// with a <c>samplerCube</c> sampled by <c>fragWorld - lightPos</c> (the basis is world-coordinate-agnostic --
    /// render and sample use the same raw vector, so NWN's Z-up world needs no special-casing). Same convention as
    /// <see cref="DirectionalLightViewProj"/>: RH lookAt * D3D [0,1] perspective * GL clip-z remap.
    /// </summary>
    public static Matrix4x4 PointLightFaceViewProj(Vector3 lightPos, int face, float near, float far)
    {
        (Vector3 fwd, Vector3 up) = CubeFace(face);
        Matrix4x4 view = Matrix4x4.CreateLookAt(lightPos, lightPos + fwd, up);
        // EXACTLY 90deg, and that is a correctness constraint, not a default. A cube sampler maps a direction
        // to a face texel by the exact 90deg rule; a face rendered even 1.5% wider puts the same world point
        // on a different texel, so every shadow lands slightly off its caster. The interim system rendered
        // over-scanned faces on purpose (a seam border) and paid for it with a matching uv shrink in the
        // receiver -- which only works because it sampled a 2D array by hand. GL_TEXTURE_CUBE_MAP_SEAMLESS
        // solves the same seam problem in the filter, with no geometry to compensate for.
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, near, far);
        return view * proj * D3DToGlClipZ;
    }

    /// <summary>Inverse of the point-light face projection's stored depth: a device depth <paramref name="sd"/>
    /// (sampled from the cube, [0,1]) -&gt; the view-space distance along that face's major axis. <c>sd=0</c> maps to
    /// <paramref name="near"/>, <c>sd=1</c> to <paramref name="far"/>. The receiver uses the SAME formula to compare
    /// against the fragment's major-axis distance (<c>max(|dx|,|dy|,|dz|)</c> of <c>fragWorld-lightPos</c>) in linear,
    /// precision-flat space -- raw device depth is nearly all ~1.0 for a perspective frustum and compares badly.</summary>
    public static float LinearizeFaceDepth(float sd, float near, float far)
        => near * far / (far - sd * (far - near));

    /// <summary>The VIEW half of <see cref="PointLightFaceViewProj"/> alone -- what #121's probe capture
    /// hands the cluster build, which needs the face's view without its projection. Shares the one face
    /// basis rather than restating it: a duplicated up vector would mis-bin exactly one face's lights.</summary>
    public static Matrix4x4 CubeFaceView(Vector3 eye, int face)
    {
        (Vector3 fwd, Vector3 up) = CubeFace(face);
        return Matrix4x4.CreateLookAt(eye, eye + fwd, up);
    }

    /// <summary>Look direction of cube face <paramref name="face"/> -- the caster cull's "is this in front of
    /// the face" axis. Exposed rather than duplicated at the call site: a face axis that disagreed with the
    /// matrix it culls for would drop exactly the casters that face actually sees.</summary>
    public static Vector3 CubeFaceForward(int face) => CubeFace(face).fwd;

    /// <summary>Standard GL cube-map face basis (look direction, up), faces 0..5 = +X,-X,+Y,-Y,+Z,-Z. These are
    /// the fixed conventions a <c>samplerCube</c> expects, so rendering with them makes depth sampling round-trip.</summary>
    private static (Vector3 fwd, Vector3 up) CubeFace(int face) => face switch
    {
        0 => (new Vector3(1f, 0f, 0f),  new Vector3(0f, -1f, 0f)),
        1 => (new Vector3(-1f, 0f, 0f), new Vector3(0f, -1f, 0f)),
        2 => (new Vector3(0f, 1f, 0f),  new Vector3(0f, 0f, 1f)),
        3 => (new Vector3(0f, -1f, 0f), new Vector3(0f, 0f, -1f)),
        4 => (new Vector3(0f, 0f, 1f),  new Vector3(0f, -1f, 0f)),
        _ => (new Vector3(0f, 0f, -1f), new Vector3(0f, -1f, 0f)),
    };

    /// <summary>Sphere-vs-camera-frustum visibility (conservative: per-plane tests never cull a visible
    /// sphere, may keep a corner-outside one). The camera basis is built from a LOOK-AT point (NWN's chase
    /// camera has no roll; world is Z-up) -- deliberately NOT from the captured view quaternion, whose
    /// component order/convention was never live-verified and mis-culled everything when wrong.
    /// <paramref name="fovy"/> is degrees as SetPerspective delivers it (values &lt;= 3.2 = radians,
    /// defensively). A degenerate look-at (at the camera) keeps the sphere.</summary>
    /// <summary>Radius slack every caller of <see cref="SphereInCameraFrustum"/> applies: covers the
    /// mirrored reflection-subframe camera and one frame of camera-turn latency without re-introducing pop.
    /// One constant on purpose (#322 review) -- the light cull and the cube-bake cull must give the same
    /// answer to "is this light visible", or a light one keeps is starved by the other.</summary>
    public const float FrustumCullSlack = 1.25f;

    public static bool SphereInCameraFrustum(Vector3 p, float r, Vector3 camPos, Vector3 lookAt,
                                             float fovy, float aspect, float near, float far)
    {
        Vector3 fwd = lookAt - camPos;
        if (fwd.LengthSquared() < 1e-8f) return true;
        CameraBasis(fwd, fovy, aspect, out fwd, out Vector3 right, out Vector3 up, out float tanX, out float tanY);
        Vector3 d = p - camPos;
        Vector3 v = new(Vector3.Dot(d, right), Vector3.Dot(d, up), 0f);
        float depth = Vector3.Dot(d, fwd);
        if (depth + r < near || depth - r > far) return false;
        if ((MathF.Abs(v.X) - depth * tanX) / MathF.Sqrt(1f + tanX * tanX) > r) return false;
        if ((MathF.Abs(v.Y) - depth * tanY) / MathF.Sqrt(1f + tanY * tanY) > r) return false;
        return true;
    }

    /// <summary>The roll-free camera basis (world is Z-up) and the half-fov tangents, shared by every
    /// camera-frustum test here. <paramref name="fovy"/> is degrees as SetPerspective delivers it (values
    /// &lt;= 3.2 = radians, defensively).</summary>
    private static void CameraBasis(Vector3 camFwd, float fovy, float aspect,
                                    out Vector3 fwd, out Vector3 right, out Vector3 up, out float tanX, out float tanY)
    {
        fwd = Vector3.Normalize(camFwd);
        right = Vector3.Cross(fwd, new Vector3(0f, 0f, 1f));
        // straight-down camera: pick any horizontal right axis (roll-free by construction)
        right = right.LengthSquared() < 1e-6f ? new Vector3(1f, 0f, 0f) : Vector3.Normalize(right);
        up = Vector3.Cross(right, fwd);
        float halfY = fovy * (fovy > 3.2f ? MathF.PI / 360f : 0.5f);
        tanY = MathF.Tan(halfY);
        tanX = tanY * aspect;
    }

    /// <summary>#488: the camera frustum as six inward-facing planes (xyz = unit normal, w = offset, so
    /// <c>dot(n, p) + w</c> is the signed distance) from the same roll-free basis
    /// <see cref="SphereInCameraFrustum"/> builds, each pushed OUT by <paramref name="slack"/> world units.
    /// Order: near, far, left, right, bottom, top. A degenerate forward writes a frustum that keeps
    /// everything (all w = +inf).</summary>
    public static void ReceiverPlanes(Vector3 camPos, Vector3 camFwd, float fovy, float aspect, float near,
                                      float far, float slack, Span<float> planes)
    {
        if (camFwd.LengthSquared() < 1e-8f)
        {
            for (int i = 0; i < 6; i++) { planes[i * 4] = 0f; planes[i * 4 + 1] = 0f; planes[i * 4 + 2] = 1f; planes[i * 4 + 3] = float.PositiveInfinity; }
            return;
        }
        CameraBasis(camFwd, fovy, aspect, out Vector3 fwd, out Vector3 right, out Vector3 up, out float tanX, out float tanY);
        Put(planes, 0, fwd, -Vector3.Dot(fwd, camPos) - near + slack);
        Put(planes, 1, -fwd, Vector3.Dot(fwd, camPos) + far + slack);
        // side planes pass through the eye: n = normalize(fwd * tan -/+ axis), w = -dot(n, eye)
        Vector3 nl = Vector3.Normalize(fwd * tanX + right), nr = Vector3.Normalize(fwd * tanX - right);
        Vector3 nb = Vector3.Normalize(fwd * tanY + up), nt = Vector3.Normalize(fwd * tanY - up);
        Put(planes, 2, nl, slack - Vector3.Dot(nl, camPos)); Put(planes, 3, nr, slack - Vector3.Dot(nr, camPos));
        Put(planes, 4, nb, slack - Vector3.Dot(nb, camPos)); Put(planes, 5, nt, slack - Vector3.Dot(nt, camPos));
    }

    private static void Put(Span<float> planes, int i, Vector3 n, float w)
    { planes[i * 4] = n.X; planes[i * 4 + 1] = n.Y; planes[i * 4 + 2] = n.Z; planes[i * 4 + 3] = w; }

    /// <summary>#488: can a caster's shadow reach the receiver frustum? The C# twin of the gather
    /// dispatch's test, term for term: the shadow volume is bounded by the convex hull of two spheres --
    /// the caster's own at <paramref name="p0"/>/<paramref name="r0"/> and the extrusion's end at
    /// <paramref name="p1"/>/<paramref name="r1"/> -- and that hull misses the frustum only if BOTH spheres
    /// sit wholly outside ONE plane. Conservative: a caster this rejects casts on no visible pixel; one it
    /// keeps may still not.</summary>
    public static bool ShadowReaches(ReadOnlySpan<float> planes, Vector3 p0, float r0, Vector3 p1, float r1)
    {
        for (int i = 0; i < 6; i++)
        {
            Vector3 n = new(planes[i * 4], planes[i * 4 + 1], planes[i * 4 + 2]);
            float w = planes[i * 4 + 3];
            if (Vector3.Dot(n, p0) + w < -r0 && Vector3.Dot(n, p1) + w < -r1) return false;
        }
        return true;
    }

    /// <summary>#488: the sun extrusion -- the caster swept along the light to the fit's far extent
    /// (<paramref name="along"/> from <paramref name="focus"/>), radius unchanged.</summary>
    public static bool SunShadowReaches(ReadOnlySpan<float> planes, Vector3 c, float r, Vector3 dir, Vector3 focus, float along)
    {
        float t = Vector3.Dot(c - focus, dir);
        return ShadowReaches(planes, c, r, c + dir * MathF.Max(0f, along - t), r);
    }

    /// <summary>#488: the local-light extrusion -- the tangent cone from the light through the caster, out
    /// to the light's reach. The cone is wider than the caster at the caster's own depth
    /// (<c>dist * tan(a) &gt; r</c>), so both end radii are inflated by <c>dist / (dist - r)</c>; a light
    /// inside the caster keeps it.</summary>
    public static bool CubeShadowReaches(ReadOnlySpan<float> planes, Vector3 c, float r, Vector3 light, float reach)
    {
        Vector3 d = c - light;
        float dist = d.Length();
        if (dist <= r) return true;
        float k = 1f / (dist - r);
        return ShadowReaches(planes, c, r * dist * k, light + d * (reach / dist), r * reach * k);
    }

    /// <summary>A ground-ish focus point for the shadow map: march from the camera along its forward until it
    /// has descended <paramref name="dist"/> world units toward the ground, so the map centres on what the
    /// player is looking at (the visible ground), not on the elevated camera position. Camera forward is the
    /// view-space -Z axis (GL convention, pinned by the SelfCheck) rotated into world by the camera quaternion
    /// (which SetViewTransform treats as camera->world). <paramref name="dist"/>&lt;=0 returns the camera pos
    /// unchanged (feature off).</summary>
    public static Vector3 CameraGroundFocus(Vector3 camPos, Quaternion camRot, float dist)
    {
        if (dist <= 0f) return camPos;
        Vector3 fwd = Vector3.Transform(ViewForward, camRot);
        return camPos + fwd * dist;
    }

    /// <summary>View-space forward (into the screen) = -Z (OpenGL). Rotated by the camera quaternion it gives
    /// the world direction the camera looks along.</summary>
    private static readonly Vector3 ViewForward = new(0f, 0f, -1f);

    /// <summary>System.Numerics' CreateOrthographic maps clip-z to the Direct3D range [0,1]; OpenGL wants NDC
    /// z in [-1,1]. This post-multiply (z' = 2z - w) converts it, so the depth buffer uses its full range and
    /// the debug view shows a real gradient. Identity on x/y/w, so it doesn't touch the projection otherwise.</summary>
    private static readonly Matrix4x4 D3DToGlClipZ = new(
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 2f, 0f,
        0f, 0f, -1f, 1f);

    /// <summary>World point -> light NDC (x,y in [-1,1] across the map; z the stored depth). For sampling/tests.</summary>
    public static Vector3 WorldToLightNdc(Matrix4x4 lightViewProj, Vector3 world)
    {
        Vector4 c = Vector4.Transform(new Vector4(world, 1f), lightViewProj);
        return c.W != 0f ? new Vector3(c.X, c.Y, c.Z) / c.W : new Vector3(c.X, c.Y, c.Z);
    }

    private static Vector3 Normalize(Vector3 v)
    {
        float len = v.Length();
        return len > 1e-6f ? v / len : new Vector3(0f, 0f, -1f);   // degenerate dir -> straight down (-Z, NWN is Z-up)
    }

    /// <summary>An up vector not parallel to the light, so LookAt doesn't degenerate. Prefers world up (+Z,
    /// NWN Z-up); falls back to +X when the light is near-vertical.</summary>
    private static Vector3 PerpendicularUp(Vector3 dir) =>
        MathF.Abs(dir.Z) < 0.99f ? new Vector3(0f, 0f, 1f) : new Vector3(1f, 0f, 0f);

    /// <summary>Headless check of the light-space transform (light along -Y, unit sphere at origin, margin 1):
    /// centre maps to the NDC origin, the sphere rim to |x|==1, and a point deeper along the light stores a
    /// greater depth.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        Matrix4x4 vp = DirectionalLightViewProj(new Vector3(0f, -1f, 0f), Vector3.Zero, 1f, 1f);
        Vector3 c = WorldToLightNdc(vp, Vector3.Zero);
        check.Expect(MathF.Abs(c.X) < 1e-3f && MathF.Abs(c.Y) < 1e-3f, "centre -> NDC x/y origin");
        Vector3 rim = WorldToLightNdc(vp, new Vector3(1f, 0f, 0f));
        check.Expect(MathF.Abs(MathF.Abs(rim.X) - 1f) < 1e-3f, "rim -> NDC |x|==1");
        float dCentre = WorldToLightNdc(vp, Vector3.Zero).Z;
        float dDeep = WorldToLightNdc(vp, new Vector3(0f, -0.5f, 0f)).Z;
        check.Expect(dDeep > dCentre, "deeper-along-light -> greater depth");

        // Texel snap: two centres closer than one texel apart must produce the SAME map (identical
        // alignment) -- that is what stops the shadow edges crawling as the camera slides the centre.
        float radius = 40f; int map = 1024; float texel = 2f * radius / map;
        Vector3 dir = new(0f, 0f, -1f);
        Matrix4x4 a = DirectionalLightViewProj(dir, new Vector3(100f, 200f, 5f), radius, radius, map);
        Matrix4x4 b = DirectionalLightViewProj(dir, new Vector3(100f + texel * 0.3f, 200f - texel * 0.2f, 5f), radius, radius, map);
        check.Expect(a.Equals(b), "texel snap: sub-texel centre shift -> identical map");
        Matrix4x4 far = DirectionalLightViewProj(dir, new Vector3(100f + texel * 3f, 200f, 5f), radius, radius, map);
        check.Expect(!far.Equals(a), "texel snap: multi-texel shift -> different map");
        // #306: a sub-texel shift ALONG the light must reproduce the map too -- the along-light snap is
        // what makes the matrix a usable static-bake key while the focus walks.
        Matrix4x4 al = DirectionalLightViewProj(dir, new Vector3(100f, 200f, 5f - texel * 0.4f), radius, radius, map);
        check.Expect(a.Equals(al), "texel snap: sub-texel along-light shift -> identical map");

        // Sun cascades (#94): radii strictly increase, the outermost IS the max shadow distance, and every
        // cascade is contained in the next -- containment is what lets the receiver pick the FIRST cascade
        // whose NDC box holds the fragment and be sure the rest would too.
        const float maxDist = 120f; const int cascades = 4;
        for (int i = 1; i < cascades; i++)
            check.Expect(CascadeRadius(i, cascades, maxDist) > CascadeRadius(i - 1, cascades, maxDist), $"cascade {i} radius grows");
        check.Expect(CascadeRadius(cascades - 1, cascades, maxDist) == maxDist, "outermost cascade == max distance");
        check.Expect(CascadeRadius(0, 1, maxDist) == maxDist, "single cascade == max distance");
        Vector3 sun = Normalize(new Vector3(0.4f, 0.3f, -0.86f));
        Vector3 anchor = new(112f, -34f, 6f);
        for (int i = 0; i + 1 < cascades; i++)
        {
            float ri = CascadeRadius(i, cascades, maxDist);
            Matrix4x4 inner = DirectionalLightViewProj(sun, anchor, ri, ri);
            Matrix4x4 outer = DirectionalLightViewProj(sun, anchor, CascadeRadius(i + 1, cascades, maxDist),
                                                       CascadeRadius(i + 1, cascades, maxDist));
            // a point on cascade i's rim: inside i (|x| ~ 1) and comfortably inside i+1
            Vector3 edge = anchor + Vector3.Cross(PerpendicularUp(sun), sun) * (ri * 0.95f);
            Vector3 inNdc = WorldToLightNdc(inner, edge), outNdc = WorldToLightNdc(outer, edge);
            check.Expect(MathF.Abs(inNdc.X) <= 1f && MathF.Abs(inNdc.Y) <= 1f, $"cascade {i}: rim inside its own box");
            check.Expect(MathF.Abs(outNdc.X) < 1f && MathF.Abs(outNdc.Y) < 1f, $"cascade {i}: rim inside cascade {i + 1}");
        }
        // A caster far UP-SUN of the innermost cascade still shades the fragment at its centre, and
        // innermost-first selection means cascade 0 is the only one that fragment ever asks -- so the
        // up-sun margin has to be the max shadow distance for EVERY cascade, not each one's own radius.
        // Scaled per cascade, this point falls outside the near plane and its shadow silently vanishes.
        float r0 = CascadeRadius(0, cascades, maxDist);
        Matrix4x4 c0 = DirectionalLightViewProj(sun, anchor, r0, maxDist);
        Vector3 upSun = WorldToLightNdc(c0, anchor - sun * (maxDist * 0.9f));
        check.Expect(MathF.Abs(upSun.Z) <= 1f, "cascade 0: a caster 0.9 * maxDist up-sun is still inside the fit");

        // Camera forward convention: identity rotation looks along -Z (straight down, NWN Z-up), so the
        // focus descends below the camera; dist<=0 is a no-op.
        Vector3 focus = CameraGroundFocus(new Vector3(0f, 0f, 100f), Quaternion.Identity, 30f);
        check.Expect(MathF.Abs(focus.Z - 70f) < 1e-3f && focus.X == 0f && focus.Y == 0f, "camera focus: identity -> straight down");
        check.Expect(CameraGroundFocus(new Vector3(1f, 2f, 3f), Quaternion.Identity, 0f) == new Vector3(1f, 2f, 3f), "camera focus: dist<=0 no-op");

        // Frustum cull (look-at basis; straight-down camera exercises the degenerate-right branch).
        Vector3 dn = new(0f, 0f, -10f);
        check.Expect(SphereInCameraFrustum(new Vector3(0f, 0f, -10f), 1f, Vector3.Zero, dn, 45f, 1f, 0.1f, 45f), "frustum: on-axis kept");
        check.Expect(!SphereInCameraFrustum(new Vector3(0f, 0f, 10f), 1f, Vector3.Zero, dn, 45f, 1f, 0.1f, 45f), "frustum: behind culled");
        check.Expect(!SphereInCameraFrustum(new Vector3(100f, 0f, -10f), 1f, Vector3.Zero, dn, 45f, 1f, 0.1f, 45f), "frustum: far off-side culled");
        check.Expect(SphereInCameraFrustum(new Vector3(12f, 0f, -10f), 10f, Vector3.Zero, dn, 45f, 1f, 0.1f, 45f), "frustum: off-side big sphere kept");
        check.Expect(!SphereInCameraFrustum(new Vector3(0f, 0f, -60f), 1f, Vector3.Zero, dn, 45f, 1f, 0.1f, 45f), "frustum: beyond far culled");
        // Oblique chase-camera angle: light beside the player is in view, light far behind the camera is not.
        Vector3 cp = new(0f, -8f, 6f), tgt = new(0f, 0f, 1f);
        check.Expect(SphereInCameraFrustum(new Vector3(2f, 2f, 1f), 1f, cp, tgt, 45f, 1.8f, 0.1f, 45f), "frustum: near-target kept");
        check.Expect(!SphereInCameraFrustum(new Vector3(0f, -30f, 6f), 2f, cp, tgt, 45f, 1.8f, 0.1f, 45f), "frustum: behind camera culled");

        // Point-light cube face (slice 6): +X face from the origin, near 0.5 far 10. A point straight along
        // +X maps to the NDC centre; nudging it off-axis moves it off-centre; and a farther point stores a
        // greater depth. (Face-selection round-trip with samplerCube is verified live, not here.)
        Matrix4x4 fx = PointLightFaceViewProj(Vector3.Zero, 0, 0.5f, 10f);
        Vector3 onAxis = WorldToLightNdc(fx, new Vector3(5f, 0f, 0f));
        check.Expect(MathF.Abs(onAxis.X) < 1e-3f && MathF.Abs(onAxis.Y) < 1e-3f, "cube +X: on-axis -> NDC centre");
        // The face MUST be exactly 90deg: a cube sampler maps a direction to a texel by that rule, so a
        // point on the face's 45deg diagonal has to land exactly on the NDC edge. A wider fit renders
        // every caster off the texel the receiver will read it back from -- an offset shadow, not a
        // missing one, which is the failure mode nobody spots by looking.
        Vector3 corner = WorldToLightNdc(fx, new Vector3(5f, 5f, 5f));
        check.Expect(MathF.Abs(MathF.Abs(corner.X) - 1f) < 1e-3f && MathF.Abs(MathF.Abs(corner.Y) - 1f) < 1e-3f,
              "cube +X: 45deg diagonal -> exactly the NDC edge (face is 90deg)");
        Vector3 offAxis = WorldToLightNdc(fx, new Vector3(5f, 2f, 0f));
        check.Expect(MathF.Abs(offAxis.Y) > 0.1f, "cube +X: off-axis -> off-centre");
        float dNear = WorldToLightNdc(fx, new Vector3(2f, 0f, 0f)).Z;
        float dFar = WorldToLightNdc(fx, new Vector3(8f, 0f, 0f)).Z;
        check.Expect(dFar > dNear, "cube +X: farther-along-axis -> greater depth");

        // Weld the render matrix to the receiver's linearize formula: project an on-axis point at a known
        // distance, take the stored device depth sd=(ndc.z+1)/2, and assert LinearizeFaceDepth recovers it.
        // A convention slip between render and receiver would fail here headless instead of live.
        Matrix4x4 fx2 = PointLightFaceViewProj(Vector3.Zero, 0, 0.5f, 20f);
        foreach (float dist in new[] { 2f, 7f, 15f })
        {
            float sd = (WorldToLightNdc(fx2, new Vector3(dist, 0f, 0f)).Z + 1f) * 0.5f;
            check.Expect(MathF.Abs(ShadowMath.LinearizeFaceDepth(sd, 0.5f, 20f) - dist) < 0.05f, $"cube linearize d={dist}");
        }

        // Scene-camera latch (#99). The colour pass recovers the eye and look direction by inverting the
        // engine's view matrix; this pins that convention headless. A slip here does not crash, it reports
        // a camera somewhere else entirely -- which is the failure that put #93's cull at (0, 0, 766).
        Vector3 eye = new(10f, -20f, 8f), at = new(10f, 0f, 2f);
        Matrix4x4 camView = Matrix4x4.CreateLookAt(eye, at, new Vector3(0f, 0f, 1f));
        check.Expect(Matrix4x4.Invert(camView, out Matrix4x4 camInv), "scene cam: view matrix inverts");
        check.Expect((camInv.Translation - eye).Length() < 1e-3f, "scene cam: inverse translation IS the eye");
        Vector3 fwd = Vector3.Normalize(Vector3.TransformNormal(ViewForward, camInv));
        check.Expect(Vector3.Dot(fwd, Vector3.Normalize(at - eye)) > 0.999f, "scene cam: view -Z -> the look direction");
        // ...and the depth the period readout prints is the distance down that axis to the focus.
        check.Expect(MathF.Abs(Vector3.Dot(at - eye, fwd) - (at - eye).Length()) < 1e-3f,
              "scene cam: on-axis focus depth == its distance");

        // #257: the same forward, from the camera QUATERNION instead of the inverted view matrix. The
        // two derivations have to agree, because the frame constants now take the quaternion route and
        // every shadow fit in this file was validated against the matrix one.
        Quaternion look = Quaternion.CreateFromRotationMatrix(camInv);
        check.Expect(Vector3.Dot(FrameRead.ViewForward(look), fwd) > 0.999f,
              "scene cam: the camera quaternion gives the same forward as the inverted view");

        // #488: the receiver cull. Camera at the origin looking +X, 90deg fov, square, far 100: the
        // frustum is |y|,|z| <= x. Sun straight down (-Z).
        Span<float> pl = stackalloc float[24];
        ReceiverPlanes(Vector3.Zero, Vector3.UnitX, 90f, 1f, 0.1f, 100f, 0f, pl);
        Vector3 down = new(0f, 0f, -1f);
        check.Expect(ShadowReaches(pl, new Vector3(50f, 0f, 0f), 1f, new Vector3(50f, 0f, 0f), 1f), "receiver: a caster inside the frustum reaches");
        check.Expect(!SunShadowReaches(pl, new Vector3(-20f, 0f, 0f), 1f, down, Vector3.Zero, 200f), "receiver: a caster behind the camera with a downward sun cannot reach");
        check.Expect(SunShadowReaches(pl, new Vector3(50f, 0f, 80f), 1f, down, Vector3.Zero, 200f), "receiver: a caster above the frustum shades down into it");
        check.Expect(!SunShadowReaches(pl, new Vector3(50f, 0f, 80f), 1f, down, new Vector3(50f, 0f, 85f), 20f), "receiver: ...unless the fit's extent ends before it gets there");
        check.Expect(!SunShadowReaches(pl, new Vector3(50f, 0f, -80f), 1f, down, Vector3.Zero, 200f), "receiver: a caster below the frustum shading further down cannot reach");
        check.Expect(SunShadowReaches(pl, new Vector3(50f, 0f, -80f), 1f, new Vector3(0f, 0f, 1f), Vector3.Zero, 200f), "receiver: ...but shading up it does");
        // cube: light beside the frustum, caster between it and the frustum -> the cone widens in
        check.Expect(CubeShadowReaches(pl, new Vector3(50f, 70f, 0f), 1f, new Vector3(50f, 80f, 0f), 60f), "receiver: a local-light cone widening toward the frustum reaches");
        check.Expect(!CubeShadowReaches(pl, new Vector3(50f, 90f, 0f), 1f, new Vector3(50f, 80f, 0f), 60f), "receiver: ...one pointing away does not");
        check.Expect(!CubeShadowReaches(pl, new Vector3(50f, 70f, 0f), 1f, new Vector3(50f, 80f, 0f), 15f), "receiver: ...nor one whose reach ends short");
        check.Expect(CubeShadowReaches(pl, new Vector3(50f, 70f, 0f), 5f, new Vector3(50f, 72f, 0f), 60f), "receiver: a light inside the caster keeps it");
        ReceiverPlanes(Vector3.Zero, Vector3.UnitX, 90f, 1f, 0.1f, 100f, 30f, pl);
        check.Expect(ShadowReaches(pl, new Vector3(-20f, 0f, 0f), 1f, new Vector3(-20f, 0f, 0f), 1f), "receiver: slack pushes every plane out");
        ReceiverPlanes(Vector3.Zero, Vector3.Zero, 90f, 1f, 0.1f, 100f, 0f, pl);
        check.Expect(ShadowReaches(pl, new Vector3(-500f, 0f, 0f), 1f, new Vector3(-500f, 0f, 0f), 1f), "receiver: a degenerate camera keeps everything");

        log.Info(check.Ok ? "ShadowMath.SelfCheck: passed" : "ShadowMath.SelfCheck: FAILED");
    }
}
