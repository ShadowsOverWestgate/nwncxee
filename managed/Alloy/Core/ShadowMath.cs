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
public static partial class ShadowMath
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
}
