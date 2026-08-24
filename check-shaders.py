#!/usr/bin/env python3
"""Compile the owned pipeline's embedded GLSL with glslangValidator, offline.

The runtime failure is quiet: a lit stage that does not compile only says "clustered lit program
unavailable -- lit parts revert to engine drawing" in the log and the world keeps rendering, vanilla.
This turns that into a build-time error. It composes the same stages `OwnedDrawService` and
`CasterPass` do -- the
lit fragment stage is head + LocalShadowLib + FragmentLib + SunShadowLib + MaterialLib +
KeyFogFs + main -- and expands the
`ShaderResource.Tokens` sizing tokens with stand-in values, since only the shapes matter here.

    ./check-shaders.py                     # exit 0 = every owned program compiles
    ./check-shaders.py ../sow-topdata      # ...and the sowx_*.shd overrides in that repo
    ./check-shaders.py --emit ../sow-topdata   # regenerate those overrides from the embedded stages

The override pass matters more than it sounds: a shipped `sowx_*.shd` REPLACES the embedded stage at
runtime, so it is what actually runs, and it is a hand-held copy of a `SowxDraw` record layout that
lives in C#. When the record grew in #120 the stale copies read every draw out of a neighbour's slot
and the world went black. The extender now refuses a stage whose record disagrees, and this catches
the same drift one step earlier.

Needs glslangValidator (Arch: `glslang`); the material pass also needs spirv-opt/spirv-dis
(Arch: `spirv-tools`), and skips itself with a notice where they are absent.
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile
import textwrap

ROOT = os.path.dirname(os.path.abspath(__file__))
SOURCES = [
    "managed/Services/Render/OwnedDrawService.cs",
    "managed/Services/Render/OwnedLights.cs",
    "managed/Services/Shadow/CasterPass.cs",
    "managed/Services/Render/GrassField.cs",
    "managed/Services/Render/PostChain.cs",
    "managed/Services/Render/WaterPass.cs",
]
# ShaderResource.Tokens' three sizing tokens. Any legal value compiles the same shapes.
TOKENS = {"$CAP$": "128", "$GROUP$": "8", "$SRGB$": "2.2", "$BAND$": "45.0, 0.0667"}
DECL = re.compile(r"\s*(?:private|internal|public)?\s*(?:static)?\s*const string (\w+) = (.*)$")
PIECE = re.compile(r'"""\n(.*?)\n\s*"""|([A-Za-z_]\w*)', re.S)


def harvest():
    """Every `const string` raw-literal in the render sources, with concatenated constants inlined."""
    lines = []
    for path in SOURCES:
        lines += open(os.path.join(ROOT, path)).read().split("\n")
    consts, i = {}, 0
    while i < len(lines):
        m = DECL.match(lines[i])
        i += 1
        if not m:
            continue
        expr = [m.group(2)]
        fenced = expr[0].count('"""') % 2 == 1
        while fenced or not expr[-1].rstrip().endswith(";"):
            expr.append(lines[i])
            if lines[i].count('"""') % 2 == 1:
                fenced = not fenced
            i += 1
        text = "\n".join(expr)
        if '"""' not in text:
            continue
        out = []
        for p in PIECE.finditer(text):
            if p.group(1) is not None:
                out.append(textwrap.dedent(p.group(1)) + "\n")
            elif p.group(2) in consts:
                out.append(consts[p.group(2)])
        consts[m.group(1)] = "".join(out)
    return consts


def check_overrides(repo):
    """Compile the sowx_*.shd in a content repo and check their record layout against the C# one."""
    d = os.path.join(repo, "assets", "shaders", "sowx")
    if not os.path.isdir(d):
        print(f"no {d} -- skipping the override pass")
        return 0
    want = open(os.path.join(ROOT, SOURCES[0])).read()
    floats = int(re.search(r"const int DrawFloats = (\d+)", want).group(1))
    vec4s = (floats - 16) // 4
    bad = 0
    for name in sorted(os.listdir(d)):
        if not name.endswith(".shd"):
            continue
        src = open(os.path.join(d, name)).read()
        decl = re.search(r"struct SowxDraw.*?\}", src, re.S)
        if decl and decl.group(0).count("vec4") != vec4s:
            print(f"{name}: FAILED -- declares {decl.group(0).count('vec4')} vec4s, the CPU stages {vec4s}")
            bad = 1
        elif decl:
            print(f"{name}: record ok ({vec4s} vec4s)")
    return bad


# --- #122 material synthesis -------------------------------------------------
# The stock generators, transcribed from inc_material.shd's SetupSpecularity in the OLD generation
# (data/base_shaders.bif) -- the one the installed client loads. This is the REFERENCE side of the
# comparison: it is written from the engine's shader, not from ours, so a case where the two agree
# says the owned synthesis matches stock, and a case where they deliberately differ says so in place.
SPEC_MIN, ROUGH_MAX, ROUGH_MIN = 0.04, 0.6, 0.125          # fSpecularityMin, fGeneratedRoughness{Max,Min}
ENV_SPEC_MOD, ENV_SPEC_MAX, ENV_ROUGH_MOD = 8.0, 0.98, 3.0  # fLegacyEnvMap{Specularity,Roughness}*
SLOT_SPEC, SLOT_ROUGH, SLOT_HEIGHT, SLOT_ENV = 2, 4, 8, 64   # env moved up past #123's flow-map bit


def stock_pbr(declared, slots, env_level, spec_texel, rough_texel):
    """inc_material.shd SetupSpecularity: (fSpecularity, fRoughness, fMetallicness)."""
    mix = lambda a, b, t: a * (1.0 - t) + b * t
    sm = bool(slots & SLOT_SPEC)
    spec = declared[0]
    if spec <= 0.0:
        if sm:
            spec = spec_texel
        elif env_level > 0.0:
            spec = mix(SPEC_MIN, ENV_SPEC_MAX, min(env_level * ENV_SPEC_MOD, 1.0))
        else:
            spec = SPEC_MIN
    rough = declared[1]
    if rough <= 0.0:
        if slots & SLOT_ROUGH:
            rough = rough_texel          # a bound roughness map is taken whole -- no env curve on top
        else:
            if sm:
                rough = mix(ROUGH_MIN, ROUGH_MAX, (1.0 - spec) ** 2)
                if env_level > 0.0:
                    rough = min(rough, mix(ROUGH_MAX, ROUGH_MIN, min(env_level * ENV_ROUGH_MOD, 1.0)))
            elif env_level > 0.0:
                rough = mix(ROUGH_MAX, ROUGH_MIN, min(env_level * ENV_ROUGH_MOD, 1.0))
            else:
                rough = ROUGH_MAX
    metal = declared[2]
    if metal <= 0.0:
        # fMetallicnessModifier * fSpecularity - fMetallicnessModifier * fGeneratedMetallicnessOffset
        metal = min(max((3.0 * spec - 0.6) if sm else (10.0 * spec - 0.4), 0.0), 1.0)
    return spec, rough, metal


def stock_spec_color(base, metal, slots):
    """The generated (not declared) branch of SetupSpecularity's specular colour, GAMMA_CORRECTION == 1."""
    mix = lambda a, b, t: a * (1.0 - t) + b * t
    if slots & SLOT_HEIGHT:
        return mix(1.0, base, metal)
    return mix(min(1.0, base / 0.2176), base, metal)     # ApplyColorSpace(0.5) at gamma 2.2


# (label, GLSL expression yielding one float, expected value). Every expression must fold to a
# constant -- no texture fetch, no uniform.
def material_cases():
    def pbr(declared, slots, env, st=0.0, rt=0.0):
        return f"SowxSynthPbr(vec3({declared[0]}, {declared[1]}, {declared[2]}), {slots}, {env}, {st}, {rt})"

    cases = []
    for label, declared, slots, env, st, rt in [
        ("bare material (no maps, no env)", (0.0, 0.0, 0.0), 0, 0.0, 0.0, 0.0),
        ("declared beats the floor", (0.7, 0.3, 0.5), 0, 0.0, 0.0, 0.0),
        ("specular map, nothing declared", (0.0, 0.0, 0.0), SLOT_SPEC, 0.0, 0.25, 0.0),
        ("declared beats a specular map", (0.7, 0.0, 0.0), SLOT_SPEC, 0.0, 0.25, 0.0),
        ("roughness map beats the env curve", (0.0, 0.0, 0.0), SLOT_ROUGH | SLOT_ENV, 0.5, 0.0, 0.9),
        ("declared beats a roughness map", (0.0, 0.42, 0.0), SLOT_ROUGH, 0.0, 0.0, 0.9),
        ("legacy env-mapped, no maps", (0.0, 0.0, 0.0), SLOT_ENV, 0.4, 0.0, 0.0),
        ("legacy env-mapped, faintly shiny", (0.0, 0.0, 0.0), SLOT_ENV, 0.05, 0.0, 0.0),
        ("env + specular map", (0.0, 0.0, 0.0), SLOT_ENV | SLOT_SPEC, 0.3, 0.6, 0.0),
        ("declared metallicness beats both curves", (0.0, 0.0, 0.8), SLOT_SPEC, 0.0, 0.9, 0.0),
    ]:
        want = stock_pbr(declared, slots, env, st, rt)
        for k, comp in enumerate("xyz"):
            cases.append((f"{label} [{'spec rough metal'.split()[k]}]",
                          f"{pbr(declared, slots, env, st, rt)}.{comp}", want[k]))
    for label, base, metal, slots in [
        ("generated specular colour, dark albedo", 0.1, 0.0, 0),
        ("generated specular colour, metal", 0.4, 0.75, 0),
        ("height-mapped takes white as its base", 0.1, 0.25, SLOT_HEIGHT),
    ]:
        cases.append((label,
                      f"SowxSpecColor(vec4(0.0), vec3({base}), {metal}, {slots}).r",
                      stock_spec_color(base, metal, slots)))
    # ...and the declared colour wins outright: 3.0 only if all three channels come back as
    # CustomSpecularColor itself. Compared per channel because a vec3 `==` lowers to OpAll, which is the one
    # step spirv-opt will not fold.
    got = "SowxSpecColor(vec4(0.3, 0.6, 0.9, 1.0), vec3(0.1), 0.0, 0)"
    want = "SowxLinear(vec3(0.3, 0.6, 0.9))"
    cases.append(("declared CustomSpecularColor wins",
                  " + ".join(f"float({got}.{ch} == {want}.{ch})" for ch in "rgb"), 3.0))
    return cases


def fold(prelude, expr, tmp):
    """Compile a fragment stage whose only output is `expr` and read the constant it folds to."""
    src = (prelude + "\nlayout(location = 0) out vec4 sowxProbe;\n"
           "void main() { sowxProbe = vec4(" + expr + "); }\n")
    frag, spv = os.path.join(tmp, "fold.frag"), os.path.join(tmp, "fold.spv")
    open(frag, "w").write(src)
    for cmd in (["glslangValidator", "-G", frag, "-o", spv], ["spirv-opt", "-O", spv, "-o", spv],
                ["spirv-dis", spv]):
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode:
            return None, (r.stdout + r.stderr)
    dis = r.stdout
    consts = dict(re.findall(r"(%\S+) = OpConstant %float ([-\d.e+]+)", dis))
    comp = dict(re.findall(r"(%\S+) = OpConstantComposite %v4float (%\S+)", dis))
    m = re.search(r"OpStore %sowxProbe (%\S+)", dis)
    if not m:
        return None, "no store to the probe output -- did the expression get optimised away?"
    v = m.group(1)
    if v in comp:
        v = comp[v]
    if v not in consts:
        return None, f"{v} did not fold to a constant (a uniform or texture fetch leaked into the expression)"
    return float(consts[v]), None


def check_material(consts):
    """#122: the synthesis chain folded off-device and compared against the stock generators."""
    missing = [t for t in ("glslangValidator", "spirv-opt", "spirv-dis") if shutil.which(t) is None]
    if missing:
        print(f"material synthesis: SKIPPED -- no {', '.join(missing)} (Arch: spirv-tools)")
        return 0
    prelude = "".join(consts[k] for k in ("OwnedLitFsHead", "LocalShadowLib", "FragmentLib",
                                          "SunShadowLib", "MaterialLib", "KeyFogFs"))
    for tok, val in TOKENS.items():
        prelude = prelude.replace(tok, val)
    prelude = prelude.replace("out vec4 frag;", "")     # our own probe output replaces it
    # SPIR-V wants an explicit location on every user in/out; the GL path leaves them implicit.
    loc = [0]
    def locate(m):
        # generous stride: a mat4[] uniform consumes one location per column, and an overlap is an error
        loc[0] += 64
        return f"layout(location = {loc[0] - 64}) {m.group(0)}"
    prelude = re.sub(r"^(?:flat )?in \w+ \w+;", locate, prelude, flags=re.M)
    # ...and on every non-opaque uniform. Samplers are opaque and must NOT get one.
    prelude = re.sub(r"^uniform (?!\w*sampler)\w+ \w+(\[[^\]]*\])?;", locate, prelude, flags=re.M)
    bad = 0
    with tempfile.TemporaryDirectory() as tmp:
        for label, expr, want in material_cases():
            got, err = fold(prelude, expr, tmp)
            if err is not None:
                print(f"material: {label}: FAILED -- {err.strip().splitlines()[0]}")
                bad = 1
            elif abs(got - want) > 1e-5:
                print(f"material: {label}: FAILED -- got {got:.6f}, stock says {want:.6f}")
                bad = 1
    print(f"material synthesis: {'ok' if not bad else 'FAILED'} "
          f"({len(material_cases())} cases against inc_material.shd)")
    return bad


# Resref -> the composed source the extender compiles for it. This is the ONLY place the composition is
# written down for the override set, so an emitted .shd is the stage the extender actually runs rather than
# someone's copy of it. Programs absent here have no resref and are not overridable (the depth-only stages).
def override_sources(c):
    return {
        "sowx_own_vs": c["OwnedVs"],
        "sowx_own_fs": c["OwnedFs"],
        "sowx_ownlit_vs": c["OwnedLitVs"],
        "sowx_ownlit_fs": (c["OwnedLitFsHead"] + c["LocalShadowLib"] + c["FragmentLib"]
                           + c["SunShadowLib"] + c["MaterialLib"] + c["KeyFogFs"] + c["OwnedLitFsMain"]),
        # #129: water is overridable like every other stage that produces a pixel. Its vertex default is the
        # lit stage's text, under its own resref.
        "sowx_water_vs": c["VsHead"] + c["DrawBufDecl"] + c["VsMain"],
        "sowx_water_tcs": c["TcsHead"] + c["DrawBufDecl"] + c["NoiseLib"] + c["WaveLib"] + c["TcsMain"],
        "sowx_water_tes": c["TesHead"] + c["DrawBufDecl"] + c["NoiseLib"] + c["WaveLib"] + c["TesMain"],
        "sowx_water_fs": (c["FsHead"] + c["LocalShadowLib"] + c["FragmentLib"]
                          + c["SunShadowLib"] + c["KeyFogFs"] + c["WaveLib"] + c["FsMain"]),
        "sowx_grass_vs": c["GrassVs"],
        "sowx_grass_fs": (c["GrassFsHead"] + c["LocalShadowLib"] + c["FragmentLib"]
                          + c["SunShadowLib"] + c["KeyFogFs"] + c["GrassFsMain"]),
        "sowx_cluster_cs": c["BuildSrc"],
    }


def emit(repo):
    """Write every overridable stage into a content repo as <resref>.shd.

    The sizing tokens are deliberately NOT expanded: ShaderResource.Tokens expands them at compile time for
    an override exactly as it does for the embedded default, so an emitted file keeps the same $CAP$ spelling
    the extender uses and stays correct when those sizes change.
    """
    c = harvest()
    d = os.path.join(repo, "assets", "shaders", "sowx")
    os.makedirs(d, exist_ok=True)
    banner = (
        "// Copied from the sow-client-extender owned pipeline's embedded default (#92).\n"
        "// $CAP$ / $GROUP$ / $SRGB$ / $BAND$ are expanded by the extender on load -- leave them as tokens.\n"
        "// Regenerate with `./check-shaders.py --emit <this repo>` from sow-client-extender.\n")
    # #119: the depth prepass writes these depths and the colour pass re-tests them with LEQUAL, so a
    # vertex override that recomputes gl_Position any other way loses fragments to a ULP of drift. Carried
    # in the banner rather than left to whoever hand-wrote the file it replaces.
    invariant = (
        "// #119: `invariant gl_Position;` and the exact clip expression below are LOAD-BEARING -- the depth\n"
        "// prepass writes these depths and the colour pass re-tests them with LEQUAL. Rewrite either and lit\n"
        "// surfaces lose fragments to a ULP of drift (holes, no log line).\n")
    record = (
        "// The SowxDraw struct below is the extender's per-draw SSBO record, and this file is a COPY of a\n"
        "// layout that lives in C#. Drop a field and every draw reads its neighbour's record -- a black,\n"
        "// streaked world. The extender counts the vec4s and ignores a stage that disagrees, so a stale\n"
        "// copy costs one log line and the embedded look, not the frame.\n")
    for ref, src in override_sources(c).items():
        src = (banner + (invariant if "invariant gl_Position" in src else "")
               + (record if "struct SowxDraw" in src else "") + src)
        path = os.path.join(d, f"{ref}.shd")
        old = open(path).read() if os.path.exists(path) else None
        if old == src:
            print(f"{ref}.shd: unchanged")
            continue
        open(path, "w").write(src)
        print(f"{ref}.shd: {'updated' if old is not None else 'written'} ({len(src)} bytes)")
    return 0


def main():
    if len(sys.argv) > 2 and sys.argv[1] == "--emit":
        return emit(sys.argv[2])
    c = harvest()
    try:
        programs = {
            "fullbright": (c["OwnedVs"], c["OwnedFs"]),
            "depth prepass": (c["DepthVs"], c["DepthFs"]),
            "keyhole depth prepass": (c["DepthKeyVs"], c["DepthKeyFs"]),
            "caster depth": (c["CastVs"], c["CastFs"]),
            "caster depth (alpha-tested)": (c["CastMaskVs"], c["CastMaskFs"]),
            "clustered lit": (
                c["OwnedLitVs"],
                c["OwnedLitFsHead"] + c["LocalShadowLib"] + c["FragmentLib"]
                + c["SunShadowLib"] + c["MaterialLib"] + c["KeyFogFs"] + c["OwnedLitFsMain"],
            ),
            "post bloom downsample": (c["FullscreenVs"], c["BloomDownFs"]),
            "post bloom upsample": (c["FullscreenVs"], c["BloomUpFs"]),
            "post resolve": (c["FullscreenVs"], c["ResolveFs"]),
            # #147: four stages -- water is the only class that tessellates. Its vertex stage is its own
            # because it must NOT project; the evaluation stage does, after displacing.
            "owned water": (
                c["VsHead"] + c["DrawBufDecl"] + c["VsMain"],
                c["TcsHead"] + c["DrawBufDecl"] + c["NoiseLib"] + c["WaveLib"] + c["TcsMain"],
                c["TesHead"] + c["DrawBufDecl"] + c["NoiseLib"] + c["WaveLib"] + c["TesMain"],
                c["FsHead"] + c["LocalShadowLib"] + c["FragmentLib"]
                + c["SunShadowLib"] + c["KeyFogFs"] + c["WaveLib"] + c["FsMain"],
            ),
            "instanced grass": (
                c["GrassVs"],
                c["GrassFsHead"] + c["LocalShadowLib"] + c["FragmentLib"]
                + c["SunShadowLib"] + c["KeyFogFs"] + c["GrassFsMain"],
            ),
        }
    except KeyError as e:
        sys.exit(f"could not find shader constant {e} -- did a stage get renamed?")

    bad = 0
    with tempfile.TemporaryDirectory() as tmp:
        for name, stages in programs.items():
            paths = []
            # Two stages is (vert, frag); four is the tessellated water program.
            exts = ("vert", "frag") if len(stages) == 2 else ("vert", "tesc", "tese", "frag")
            for src, ext in zip(stages, exts):
                for tok, val in TOKENS.items():
                    src = src.replace(tok, val)
                p = os.path.join(tmp, f"{name.replace(' ', '_')}.{ext}")
                open(p, "w").write(src)
                paths.append(p)
            r = subprocess.run(["glslangValidator"] + paths, capture_output=True, text=True)
            print(f"{name}: {'ok' if r.returncode == 0 else 'FAILED'}")
            if r.returncode:
                bad = 1
                print(r.stdout, r.stderr)
    bad |= check_material(c)
    if len(sys.argv) > 1:
        bad |= check_overrides(sys.argv[1])
    return bad


if __name__ == "__main__":
    sys.exit(main())
