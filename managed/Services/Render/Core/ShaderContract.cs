using System;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Services.Render;

using static OwnedDrawService;

/// <summary>#173: the owned pass's shader-contract predicates, lifted clear of the render service. Every one
/// is pure string-to-bool over the embedded GLSL -- no GL, no frame state, no engine pointer -- so this is
/// callable and checkable off-device by anything, which is exactly what a contract suite should be.
/// <para>These are assertions over the embedded contract: the per-draw SSBO binding and stride against
/// <see cref="OwnedDrawService.DrawBufDecl"/>'s literal GLSL, the alpha cutout spelling, the per-draw alpha
/// reaching the framebuffer, the clip expression under its invariant, the bindless rewrite -- each of which
/// fails SILENTLY as a misrender rather than an error, which is why the suite gates arming instead of only
/// logging.</para></summary>
internal static class ShaderContract
{
    /// <summary>A vertex stage whose depth the prepass may re-test: it computes <see cref="ClipExpr"/> and
    /// declares it invariant. The embedded stages satisfy this by construction (SelfCheck asserts it); a
    /// content override has to say so in its own text.</summary>
    public static bool DepthSafe(string vs)
        => vs.Contains(ClipExpr) && vs.Contains("invariant gl_Position;");

    /// <summary>Does this stage's text declare the per-draw record the CPU is staging? The layout is a
    /// contract between <see cref="OwnedDrawService.DrawFloats"/> and whatever GLSL actually compiled -- and a content
    /// override (#92) is a copy of that GLSL held somewhere else, so it goes stale the moment the record
    /// grows. #120 grew it from four vec4s to six: a stage still declaring four reads every draw's model
    /// matrix and material colours out of its NEIGHBOUR's record, which renders as a black, streaked world
    /// with nothing at all in the log. <see cref="SelfCheck"/> cannot catch it -- it only sees the embedded
    /// text. Counted rather than matched character-for-character, so an override may still reformat or
    /// rename the fields.</summary>
    public static bool RecordSafe(string src) => RecordSafe(src, "SowxDraw", (OwnedDrawService.DrawFloats - 16) / 4);

    /// <summary>#161: the same count, for any per-draw record a C# stride stages. The caster pass has its own
    /// (<c>SowxCaster</c>, <see cref="CasterPass"/>) and it fails exactly the same way -- an override
    /// declaring the wrong number of trailing vec4s reads every caster out of its neighbour's slot, which
    /// bakes shadows in the wrong places with nothing in the log.
    /// <para><paramref name="vec4s"/> is the count AFTER the leading <c>mat4</c>, i.e.
    /// <c>(strideFloats - 16) / 4</c>.</para></summary>
    public static bool RecordSafe(string src, string structName, int vec4s)
    {
        int at = src.IndexOf("struct " + structName, StringComparison.Ordinal);
        if (at < 0) return true;                        // a stage that never declares it cannot disagree
        int end = src.IndexOf('}', at);
        string decl = end < 0 ? src[at..] : src[at..end];
        return decl.Split("vec4").Length - 1 == vec4s;
    }

    /// <summary>#126: does this stage pair carry the cutout and the per-draw alpha? A content override (#92)
    /// replaces the stage wholesale, and one written before #126 links CLEANLY without them --
    /// <see cref="RecordSafe"/> passes (the record did not grow), <see cref="DepthSafe"/> passes (the clip
    /// expression did not change), and <c>glUniform1f</c> on the missing uniform's -1 location is a legal
    /// no-op. What ships is then a world with no alpha discard and a constant 1.0 alpha: grass and fences
    /// render as opaque quads and windows as walls, silently. So it is checked on the source that actually
    /// compiled, and a stage that lacks it is rebuilt from the embedded text.
    /// <para>Named terms rather than the whole line, so an override may still restyle everything around
    /// them -- what it may not do is drop them.</para></summary>
    public static bool CutoutSafe(string vs, string fs)
        => fs.Contains("uAlphaCut") && (fs.Contains("vAlpha") ? vs.Contains("vAlpha") : fs.Contains("rot.w"));

    /// <summary>#137: does this VERTEX stage carry the skinning branch? Same shape, same reason, as
    /// <see cref="CutoutSafe"/> -- and the same trap. A <c>sowx_*_vs</c> written before #137 links
    /// perfectly: <see cref="RecordSafe"/> passes (the record did not grow, the bone base rides in
    /// <c>feat.y</c>), <see cref="DepthSafe"/> passes (the clip expression did not change), and the missing
    /// bone attributes are simply disabled. What ships is then a skinned draw taking
    /// <c>!SowxDisplaces(8)</c> -- the literal clip expression -- and rendering the robe in its BIND POSE at
    /// the node matrix, silently.
    /// <para>Not academic, and not only a developer's problem: on a server the HAK wins over a local
    /// override directory, so a HAK that has not caught up with the extender is exactly this case on live
    /// content while a singleplayer test looks right. Checked only while the class is armed -- an override
    /// predating #137 is perfectly correct when nothing skins.</para></summary>
    public static bool SkinSafe(string vs, bool skinning) => !skinning || vs.Contains("SOWX_FEAT_SKIN");

    /// <summary>#132: does this FRAGMENT stage write LINEAR? Same shape and same trap as
    /// <see cref="CutoutSafe"/> and <see cref="SkinSafe"/>, and the one with the widest blast radius yet: a
    /// <c>sowx_*_fs</c> written before #132 ends in <c>SowxRevert(...)</c>, links perfectly (the record did
    /// not grow, the clip expression did not change, <c>uSoftClip</c> at -1 is a legal no-op) and writes
    /// display-space values into a target the resolve then encodes a SECOND time. The frame is washed out,
    /// end to end, and nothing says so.
    /// <para>On a server the HAK wins over a local override directory, so a HAK that has not caught up with
    /// the extender is exactly this case on live content while a singleplayer test looks right -- which is
    /// why the answer is to rebuild from the embedded text rather than to log and carry on.</para></summary>
    public static bool LinearSafe(string fs) => !fs.Contains("SowxRevert") && !fs.Contains("SowxSoftClip");

    /// <summary>#132's guard from the other end, for the ONE stage that encodes: the resolve. Every other
    /// owned fragment stage must not encode (<see cref="LinearSafe"/>); this one must, and must not decode
    /// first. A pre-#132 resolve override opens with <c>SowxLinear</c> on a target that is already linear and
    /// comes out a gamma dark; one that dropped the encode leaves the whole frame linear and comes out washed
    /// out. Both link cleanly and say nothing, so both rebuild from the embedded text.
    /// <para>Named terms, like every other guard here -- an override may restyle the tonemap and the dither
    /// around them, but not the colour-space decision the rest of the pipeline is built on.</para></summary>
    public static bool ResolveSafe(string fs) => !fs.Contains("SowxLinear") && fs.Contains("SowxEncode");

    /// <summary>Headless check of the one #84 desync that fails SILENTLY: the per-draw SSBO's binding point
    /// and record stride live twice -- once as C# constants driving the staging writes and the upload, once as
    /// literal GLSL in <see cref="OwnedDrawService.DrawBufDecl"/>. Bind to the wrong point or stage the wrong stride and the
    /// shaders read zeros or a neighbour's record, which renders as black/misplaced geometry rather than an
    /// error. Verified against glslang reflection: model@0 rot@64 amb@80 dif@96 emi@112 pbr@128 spc@144,
    /// array stride 160.
    /// <para>False = do not arm. A check that only logs would let exactly the misrender it predicts ship, with
    /// one easily-missed line as the signal -- and because the iterator would still suppress the engine's own
    /// draws, the world goes black rather than merely unaccelerated. Standing down is the honest failure.</para></summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        // the record's field list, counted: four vec4s after the mat4 IS the 128-byte stride staged above
        bool ok = DrawBufDecl.Contains($"binding = {BindDraw}") && DrawFloats * sizeof(float) == 176
                  && DrawBufDecl.Split("vec4").Length - 1 == (DrawFloats - 16) / 4
                  && DrawBufDecl.Contains("vec4 emi") && DrawBufDecl.Contains("vec4 spc")
                  && DrawBufDecl.Contains("vec4 feat")
                  // #123: the feature ids are a contract between the ShaderFeature enum (staged into
                  // d.feat.x) and the GLSL that switches on it. Drift renders the wrong effect on the wrong
                  // content -- roofs swaying, plants dropping 500m -- with nothing in the log.
                  && FeatDecl.Contains($"SOWX_FEAT_WINDGRASS {(int)ShaderFeature.WindGrass}")
                  && FeatDecl.Contains($"SOWX_FEAT_WINDLEAF  {(int)ShaderFeature.WindLeaf}")
                  && FeatDecl.Contains($"SOWX_FEAT_WINDPLANT {(int)ShaderFeature.WindPlant}")
                  && FeatDecl.Contains($"SOWX_FEAT_ROOFKN    {(int)ShaderFeature.RoofKn}")
                  && FeatDecl.Contains($"SOWX_FEAT_ROOFKNM   {(int)ShaderFeature.RoofKnM}")
                  && FeatDecl.Contains($"SOWX_FEAT_FLOWMAP   {(int)ShaderFeature.Flowmap}")
                  && FeatDecl.Contains($"SOWX_FEAT_SKIN      {(int)ShaderFeature.Skin}")
                  // #137: the skinning half. The binding point and the two attribute locations live twice
                  // (C# consts, literal GLSL) and neither disagreement is visible -- a wrong binding reads an
                  // empty block and collapses the mesh to the origin, a wrong location reads whatever stream
                  // the engine left at 8/9. The weight guards are bounds protection on an SSBO index, not a
                  // micro-optimisation, so their absence is checked as well.
                  && BoneVs.Contains($"binding = {BindBones}")
                  && BoneVs.Contains($"layout(location = {LocBoneIdx}) in vec4 aBoneIdx;")
                  && BoneVs.Contains($"layout(location = {LocBoneW}) in vec4 aBoneWeight;")
                  && BoneVs.Contains("aBoneWeight.w > 0.0")
                  && BoneVs.Contains("int(d.feat.x) == SOWX_FEAT_SKIN ? int(d.feat.y) : -1")
                  && BoneFloats * sizeof(float) == 4096
                  // ...and both stages take the skinned arm BEFORE the displaced test, which is what keeps
                  // the literal clip expression below reachable for every part that is still in the prepass.
                  && OwnedVs.Contains("if (bb >= 0) gl_Position = uVP * d.model * pos;")
                  && OwnedLitVs.Contains("if (bb >= 0) gl_Position = uVP * d.model * pos;")
                  // ...which is also the token SkinSafe reads to spot a pre-#137 content override. It has to
                  // be in the COMPOSED stage, not merely in BoneVs, or the guard passes on text that never
                  // reached the program. And it must NOT be in the depth stage: that one is the prepass, and
                  // a skinning branch appearing there would mean the exclusion above had been undone.
                  && OwnedVs.Contains("SOWX_FEAT_SKIN") && OwnedLitVs.Contains("SOWX_FEAT_SKIN")
                  && !DepthVs.Contains("SOWX_FEAT_SKIN") && !DepthKeyVs.Contains("SOWX_FEAT_SKIN")
                  // The prepass exclusion is the OTHER half of that decision, and it is the half no shader
                  // text can assert: a skinned batch left in the prepass writes a bind-pose silhouette and
                  // LEQUAL then punches the cloak full of holes.
                  && ShaderFeatures.OutOfPrepass((int)ShaderFeature.Skin)
                  && !ShaderFeatures.Displaces((int)ShaderFeature.Skin)
                  && ShaderFeatures.OutOfPrepass((int)ShaderFeature.WindGrass)
                  && !ShaderFeatures.OutOfPrepass((int)ShaderFeature.Flowmap)
                  // ...and the CPU-side exclusions that go with them: no name here, no switch there.
                  && ShaderFeatures.VsNames.Length == 8
                  && ShaderFeatures.Displaces((int)ShaderFeature.WindGrass)
                  && ShaderFeatures.Displaces((int)ShaderFeature.RoofKnM)
                  && !ShaderFeatures.Displaces((int)ShaderFeature.Flowmap)
                  && !ShaderFeatures.Displaces((int)ShaderFeature.NoKeyhole)
                  && ShaderFeatures.NoKeyholing((int)ShaderFeature.NoKeyhole)
                  && ShaderFeatures.NoKeyholing((int)ShaderFeature.RoofKn)
                  && ShaderFeatures.NoKeyholing((int)ShaderFeature.Flowmap)
                  && !ShaderFeatures.NoKeyholing((int)ShaderFeature.RoofKnM)
                  && !ShaderFeatures.NoKeyholing((int)ShaderFeature.WindPlant)
                  // ...and the GLSL predicate picking the displaced clip expression mirrors Displaces(). If it
                  // ever says "any feature" again, nokeyhole and flowmap sit in the prepass computing a clip
                  // expression the prepass never compiled -- the #119 hole, with nothing in the log.
                  && FeatVs.Contains("bool SowxDisplaces(int feat) { return feat >= SOWX_FEAT_WINDGRASS "
                                     + "&& feat <= SOWX_FEAT_ROOFKNM; }")
                  && OwnedLitVs.Contains("if (!SowxDisplaces(int(d.feat.x))) " + ClipExpr)
                  && OwnedVs.Contains("if (!SowxDisplaces(int(d.feat.x))) " + ClipExpr)
                  // ...and the map slots must stay BELOW the probe's unit. A samplerCube and a sampler2D
                  // pointed at one image unit is INVALID_OPERATION: every lit batch is dropped, and the log
                  // says nothing at all. #123 grew the slots into exactly that collision once.
                  && PbrUnit0 + MapSlots <= EnvProbe.ProbeUnit
                  // #146: the AO map is one more slot on that same ladder, and its GLSL bit is a HAND-WRITTEN
                  // copy of AoSlot/EnvSlot. Drift there is silent twice over: an AO bit that reads as the env
                  // bit turns every AO material legacy-shiny, and a lost multiply just renders unoccluded --
                  // which is what shipped content looks like today, so nothing would look wrong.
                  && OwnedLitFsHead.Contains($"SOWX_SLOT_AO = {AoBit};")
                  && OwnedLitFsHead.Contains($"SOWX_SLOT_ENV = {EnvSlot};")
                  && OwnedLitFsMain.Contains("if ((slots & SOWX_SLOT_AO) != 0 && shadeLod < 1.0) occl *= mix(texture(uAoTex, uv).r, 1.0, shadeLod);")
                  && OwnedLitFsMain.Contains("* mix(0.15, 1.0, sunRaw) * occl")
                  && OwnedVs.Contains("uDrawBase + gl_DrawID") && OwnedLitVs.Contains("uDrawBase + gl_DrawID")
                  // #119: the prepass depth is only re-testable by the colour pass while all three stages
                  // compute the SAME clip expression and declare it invariant. Drift either and lit
                  // surfaces lose fragments to LEQUAL -- a hole, with nothing in the log.
                  && OwnedVs.Contains(ClipExpr) && OwnedLitVs.Contains(ClipExpr) && DepthVs.Contains(ClipExpr)
                  && OwnedVs.Contains("invariant gl_Position;") && OwnedLitVs.Contains("invariant gl_Position;")
                  && DepthVs.Contains("invariant gl_Position;")
                  // #124: ...and the keyhole depth stage is a FOURTH copy of the same contract. It writes the
                  // depth a dissolving batch is tested against, so drift here is the #119 hole again.
                  && DepthKeyVs.Contains(ClipExpr) && DepthKeyVs.Contains("invariant gl_Position;")
                  // #102/#132: the lit stage lights in LINEAR and now STAYS there -- the frame's one encode
                  // is PostChain's resolve. Both halves are silent when they drift: a stage that stopped
                  // linearising its texel renders washed out, one that re-encoded renders twice-encoded, and
                  // neither says anything. So assert both, including the absence.
                  && OwnedLitFsMain.Contains("SowxLinear(texel.rgb)")
                  && !OwnedLitFsMain.Contains("SowxRevert")
                  && !OwnedLights.FragmentLib.Contains("SowxRevert")
                  // #120: the lobe the whole ticket exists to restore, and the one-walk light function it
                  // rides in. A stage that lost either still compiles and still renders -- flat, exactly as
                  // it did before.
                  && OwnedLights.FragmentLib.Contains("float SowxSpecLobe(")
                  && OwnedLights.FragmentLib.Contains("float SowxSpecBase(")
                  && OwnedLitFsMain.Contains("sunSpec * SowxSpecLobe(N, V, uAreaDir")
                  // ...fed the RAW dot. #143's wrap is diffuse-only; a lobe on the wrapped dot lights the
                  // side of a glossy surface the sun cannot reach, and only at uWrapN > 0 -- invisible at
                  // the default.
                  && OwnedLitFsMain.Contains("vec3 sunSpec = uAreaDiffuse * (sunRaw * sunVis);")
                  // ...and the base modifier really is applied to the SUM, once. Folding it per light is
                  // silent and wrong by exactly the light count.
                  && OwnedLitFsMain.Contains("* (specColor * SowxSpecBase(")
                  && OwnedLights.FragmentLib.Contains("vec3 SowxClusteredLight(")
                  // #143: the wrapped-diffuse helper, and the sun actually going THROUGH it --
                  // a lit tail that keeps the raw dot is the punctual terminator back with no other sign.
                  && OwnedLights.FragmentLib.Contains("float SowxWrap(float ndl, float w)")
                  && OwnedLights.FragmentLib.Contains("max(s, uWrapN)")
                  && OwnedLitFsMain.Contains("SowxWrap(dot(N, uAreaDir), uWrapN)")
                  // ...while the ambient hemisphere keeps the RAW dot -- that line is inc_lighting.shd:679
                  // verbatim and wrapping it would drift the owned pass off the engine's own ambient.
                  && OwnedLitFsMain.Contains("mix(0.15, 1.0, sunRaw)")
                  // ...and the fullbright stage must DECODE, unconditionally: #132 made the target linear,
                  // so a stage that passed its display-space texel through would be wrong by a gamma
                  // wherever the engine is not drawing beside it.
                  && OwnedFs.Contains("vec3($SRGB$)") && !OwnedFs.Contains("uCapture")
                  // #121: the ambient-specular term, and the capture pairing that keeps the probe linear.
                  // A stage that lost the sample still renders (flat, as before #121); one that kept the
                  // sample but lost the capture branch samples the cube it is drawing into -- undefined --
                  // and stores display-space values a mip filter then darkens. Both are silent.
                  && OwnedLitFsMain.Contains("textureLod(uProbe, reflect(-V, N)")
                  && OwnedLitFsMain.Contains("uCapture == 0 && uProbeMips > 0.0")
                  && OwnedLitFsMain.Contains("if (uCapture != 0) { frag = vec4(lit, 1.0); return; }")
                  // ...and the weighting itself: the engine tints the light that is already there. Dropping
                  // the (ambient + diffuse) factor is what made every stone surface read as wet.
                  && OwnedLitFsMain.Contains("(ambient + diffuse) * env * F0 * uProbeGain")
                  // ...and the gate that keeps it off the parts the engine binds no env texture for at all.
                  && OwnedLitFsMain.Contains("uSceneEnv != 0 || (slots & SOWX_SLOT_ENV) != 0")
                  // #124/#132: the dissolve and the fog, and the order they apply in. Both are LINEAR now --
                  // #124 welded the engine's keyhole-then-encode-then-fog order and #132 unwelded it, so the
                  // whole tail is one linear expression and the encode is the resolve's. Swap the two and
                  // the world still renders, just with fog that dissolves and a border that does not darken;
                  // nothing says so in the log.
                  && KeyFogFs.Contains("float SowxKeyhole(") && KeyFogFs.Contains("vec3 SowxFog(")
                  && OwnedLitFsMain.Contains("SowxFog(lit * kh, vDepth)")
                  // #139: ...and the two terms whose loss is silent. A stage that dropped the wrap or the
                  // soft cube taps just renders harder -- nothing about either fails to compile or shows in
                  // the log.
                  && OwnedLights.FragmentLib.Contains("float s = uWrap <= 0.0 ? 0.0 : clamp(uWrap * uFalloff.y")
                  // #140: the engine's own range-normalised curve. A stage that kept the old absolute
                  // 1/d^n plateau still renders -- blinding at a metre, black at five -- and says nothing,
                  // so the shape is asserted rather than trusted to stay put.
                  && OwnedLights.FragmentLib.Contains("float att = uFalloff.x * fade / (at.x + at.y * f2);")
                  // #142: the near-field term, and the guard that makes an unset uHot mean "off". A stage
                  // that lost it renders every light flat at the gain the BODY wants -- which is the exact
                  // symptom the term exists to fix, and says nothing.
                  && OwnedLights.FragmentLib.Contains("att += uHot.x * fade * t * t / at.x;")
                  && OwnedLights.FragmentLib.Contains("max(1.0 - f2 / (uHot.y * uHot.y), 0.0)")
                  && OwnedLights.FragmentLib.Contains("float f2 = dd / rr;")
                  // ...and the fallback pair, so a program that never gets uAtten falls back instead of
                  // dividing every light by zero and rendering the world white.
                  && OwnedLights.FragmentLib.Contains("uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0)")
                  && OwnedLights.LocalShadowLib.Contains("if (m < uLocalSoft.y) return 1.0;")
                  // ...and every one of them must read "off" from the ZERO a program that never sets the
                  // uniform leaves behind, or a stale content override (#92) does something wrong rather
                  // than something old. This is the whole reason none of them rides in uFalloff's spare lane.
                  && OwnedLights.LocalShadowLib.Contains("if (uLocalSoft.x <= 0.0) return v;")
                  // ...and the dissolve has to reach the fragment stage at all: it rides in rot.z, which was
                  // a dead float, so a stage reading the wrong lane dissolves nothing and says nothing.
                  && OwnedLitFsMain.Contains("SowxKeyhole(d.rot.z, vWorld, vDepth)")
                  // ...and the FULLBRIGHT stage must have NEITHER. Every unlit fragment stage in
                  // base_shaders.bif compiles FOG 0 and KEYHOLING 0 -- fst/fst_sm/fsglu (textured) and
                  // fs/fstc (untextured) alike -- so an unlit part that fogged or dissolved here would be
                  // doing to a part something vanilla never does to it. Asserted rather than commented:
                  // both are one line to add back and neither would look obviously wrong.
                  && !OwnedFs.Contains("SowxKeyhole") && !OwnedFs.Contains("SowxFog")
                  // ...and the prepass punches the SAME aperture the colour pass will. Both stages call the
                  // one SowxKeyhole out of KeyFogFs, so they cannot drift by a constant -- but a stage that
                  // lost the call writes depth over fragments the colour pass then discards, and the room
                  // behind the wall goes dark with nothing in the log.
                  && DepthKeyFs.Contains("float SowxKeyhole(")
                  && DepthKeyFs.Contains("SowxKeyhole(uDraw[vDraw].rot.z, vWorld, vDepth)")
                  // #126: the cutout, in both colour stages -- and spelled the engine's way, so a stage that
                  // kept the uniform but lost the >= 0.0 guard would discard every fragment of the OPAQUE
                  // set (which passes -1.0) and render an empty world.
                  && OwnedFs.Contains("if (uAlphaCut >= 0.0 && t.a <= uAlphaCut) discard;")
                  && OwnedLitFsMain.Contains("if (uAlphaCut >= 0.0 && texel.a <= uAlphaCut) discard;")
                  // ...and the per-draw alpha reaching the framebuffer, which is the whole of the blended
                  // class: a stage writing a constant 1.0 there renders every transparent surface solid.
                  && OwnedFs.Contains("t.a * vAlpha") && OwnedVs.Contains("vAlpha = d.rot.w;")
                  && OwnedLitFsMain.Contains("texel.a * d.rot.w")
                  // ...and the embedded stages themselves satisfy the override guard, so a wrong guard fails
                  // here rather than rebuilding every program from the embedded text every session (which
                  // would look like the override silently not applying).
                  && CutoutSafe(OwnedVs, OwnedFs)
                  && CutoutSafe(OwnedLitVs, OwnedLitFsHead + OwnedLitFsMain)
                  // #132: ...and the same for the colour-space guard, over every stage it polices. A guard
                  // the embedded text itself trips rebuilds every program from that same text, every
                  // session -- an infinite "your override is stale" that no override caused.
                  && LinearSafe(OwnedFs) && LinearSafe(OwnedLitFsHead + OwnedLitFsMain)
                  // #153: the bindless rewrite must produce a complete swap on the embedded lit stage --
                  // extension + handle block in, every bound-sampler declaration out -- and must leave
                  // foreign text (no known decls) untouched rather than half-rewritten.
                  && BindlessFs(OwnedLitFsHead + MaterialLib).Contains("SowxTexH")
                  && BindlessFs(OwnedLitFsHead + MaterialLib).Contains("GL_ARB_bindless_texture")
                  && !BindlessFs(OwnedLitFsHead + MaterialLib).Contains("uniform sampler2D uTex;")
                  && !BindlessFs(OwnedLitFsHead + MaterialLib).Contains("uniform sampler2D uAoTex;")
                  && BindlessFs("void main() { }") == "void main() { }"
                  && LinearSafe(FrameSnapshot.Fs) && LinearSafe(GrassField.EmbeddedFs)
                  // #161: the record guard reaches every stage that declares the record -- on water that is
                  // the tessellation pair too -- and the embedded text passes it, for the same reason as
                  // above: a guard its own defaults trip rebuilds from them every session.
                  && RecordSafe(FrameSnapshot.Vs) && RecordSafe(FrameSnapshot.Tcs)
                  && RecordSafe(FrameSnapshot.Tes) && RecordSafe(FrameSnapshot.Fs)
                  // ...and it REFUSES a record that disagrees, in both layouts it now polices. A predicate
                  // that only ever returns true is precisely the silent failure it was written to prevent,
                  // and nothing else in this build would notice.
                  && !RecordSafe("struct SowxDraw { mat4 model; vec4 rot; };")
                  && !RecordSafe("struct SowxCaster { mat4 model; vec4 bounds; };", "SowxCaster", 2)
                  && RecordSafe("void main() { }")   // a stage that declares no record cannot disagree
                  // #161: the resolve's inverted colour-space guard, both directions. It is the frame's one
                  // encode, so it must hold on the embedded text and must catch a stage that decodes first
                  // (pre-#132) or performs no encode at all -- each of which washes out the whole frame.
                  && ResolveSafe(PostChain.ResolveFs)
                  && !ResolveSafe(PostChain.ResolveFs.Replace("SowxEncode", "SowxLinear"))
                  && !ResolveSafe(PostChain.ResolveFs.Replace("SowxEncode", "SowxPassthrough"))
                  // ...and the bloom chain runs in the same linear space every other owned stage writes.
                  && LinearSafe(PostChain.BloomDownFs) && LinearSafe(PostChain.BloomUpFs)
                  // ...and the four text-matching guards REFUSE, not just accept. A predicate that only ever
                  // returns true is the silent failure it was written to prevent, and the embedded text can
                  // never exercise the refusing direction on its own.
                  && !LinearSafe("void main() { frag = SowxRevert(c); }")
                  && !LinearSafe("void main() { frag = SowxSoftClip(c); }")
                  && !CutoutSafe(OwnedVs, OwnedFs.Replace("uAlphaCut", "uNothing"))
                  && !CutoutSafe(OwnedVs.Replace("vAlpha", "vNothing"), OwnedFs)
                  // SkinSafe's refusing direction is NOT assertable here: it short-circuits on _skinning,
                  // which Initialize sets AFTER this runs, so the predicate is unconditionally true at
                  // SelfCheck time. Its guard site is covered instead by the CutoutSafe pair beside it.
                  && !DepthSafe(OwnedVs.Replace("invariant gl_Position;", ""))
                  && !DepthSafe(OwnedVs.Replace(ClipExpr, "gl_Position = uVP * (d.model * vec4(aPos, 1.0));"))
                  // ...and the suppression list is the capture list. These two disagreeing is the one #126
                  // failure that is silent in both directions -- geometry dropped, or drawn twice.
                  && OwnershipCensus.OwnableBucket(0) && OwnershipCensus.OwnableBucket(1)
                  && OwnershipCensus.OwnableBucket(4) && OwnershipCensus.OwnableBucket(7)
                  && OwnershipCensus.OwnableBucket(8)
                  // #134: the dynamic gob pair joins, and joins TOGETHER -- the engine walks 2 then 3, and
                  // owning one without the other splits one instance's geometry across two renderers.
                  && OwnershipCensus.OwnableBucket(2) && OwnershipCensus.OwnableBucket(3)
                  // 5 (refraction glass) stays the engine's: it reads a framebuffer copy taken after the
                  // owned bracket has already resolved.
                  // #129: 6 is deliberately NOT asserted here. OwnableBucket evaluates it AS
                  // FrameSnapshot.Usable, so any check of the two against each other is a tautology -- it would
                  // read as cover and catch nothing. What actually governs the water draw is _waterArmed
                  // (Usable && lightingOn && the program), and what keeps suppression honest is _waterDrew,
                  // which IterSight tests directly. Both are structural, and neither is assertable here.
                  && !OwnershipCensus.OwnableBucket(5)
                  // #92: a resref past the CResRef limit can never name a resource, so an override
                  // dropped under it would go silently unread.
                  && ShdFullbrightVs.Length <= ShaderResource.ResRefBytes
                  && ShdFullbrightFs.Length <= ShaderResource.ResRefBytes
                  && ShdLitVs.Length <= ShaderResource.ResRefBytes
                  && ShdLitFs.Length <= ShaderResource.ResRefBytes
                  && ShdWaterVs.Length <= ShaderResource.ResRefBytes
                  && ShdWaterFs.Length <= ShaderResource.ResRefBytes
                  && ShdWaterTcs.Length <= ShaderResource.ResRefBytes
                  && ShdWaterTes.Length <= ShaderResource.ResRefBytes;
        log.Info(ok
            ? "ShaderContract.SelfCheck: passed"
            : $"ShaderContract.SelfCheck: FAILED -- per-draw SSBO contract (binding {BindDraw}, " +
              $"stride {DrawFloats * sizeof(float)}B) or the #102 linear/display-space pairing drifted; " +
              "standing down rather than drawing from the wrong records");
        return ok;
    }
}
