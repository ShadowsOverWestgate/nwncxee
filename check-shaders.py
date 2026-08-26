#!/usr/bin/env python3
"""Compile the owned pipeline's embedded GLSL with glslangValidator, offline.

    ./check-shaders.py      # exit 0 = every owned program compiles

The runtime failure is quiet: a lit stage that does not compile only says "clustered lit program
unavailable -- lit parts revert to engine drawing" in the log and the world keeps rendering, vanilla.
This turns that into a build-time error.

Composition is not written down here. `ShaderStages.Source` in C# is the authoritative table (ADR
0012) and this script parses its switch arms, so a stage the extender compiles is the stage checked
here by construction. Only program *linkage* -- which stages link together, including the depth-only
ones that have no resref -- lives below. `ShaderResource.Tokens`' sizing tokens are expanded with
stand-in values, since only the shapes matter here.

Why a second copy of a composition is never tolerable: a `SowxDraw` record layout that lives in C#
was, until ADR 0012, also hand-copied into shipped `sowx_*.shd`. When the record grew in #120 the
stale copies read every draw out of a neighbour's slot and the world went black. Nothing ships a copy
now, and nothing here restates one either.

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
    # #171: the owned pass's GLSL lives in the partial, not the service. Must stay FIRST --
    # harvest() inlines only constants it has already seen, and the files below name these.
    # The service's own file is deliberately absent: it holds no shader text now. Keep it that way --
    # a const string added THERE and concatenated into one here would splice in as empty and be
    # validated as if it shipped. (A constant a Source arm names directly still hard-errors.)
    "managed/Services/Render/OwnedDrawService.Shaders.cs",
    "managed/Services/Render/Pass/OwnedLights.cs",
    "managed/Services/Render/Pass/CasterPass.cs",
    "managed/Services/Render/Pass/GrassField.cs",
    "managed/Services/Render/Pass/PostChain.cs",
    "managed/Services/Render/Pass/FrameSnapshot.cs",
]
# ShaderResource.Tokens' three sizing tokens. Any legal value compiles the same shapes.
TOKENS = {"$CAP$": "128", "$GROUP$": "8", "$SRGB$": "2.2", "$BAND$": "45.0, 0.0667"}
STAGES = "managed/Services/Render/Core/ShaderStages.cs"
DECL = re.compile(r"\s*(?:private|internal|public)?\s*(?:static)?\s*const string (\w+) = (.*)$")
PIECE = re.compile(r'"""\n(.*?)\n\s*"""|([A-Za-z_]\w*)', re.S)
RESREF = re.compile(r'(\w+)\s*=\s*"(sowx_\w+)"')
# `internal static string Source(string resref) => resref switch { ... };` -- the shape ShaderStages
# guarantees. One arm per resref: a qualified resref constant, then a plain `+` chain of constants.
SWITCH = re.compile(r"string Source\(string resref\) => resref switch\s*\{(.*?)\n\s*\};", re.S)
ARM = re.compile(r"^\s*([\w.]+)\s*=>\s*([^,]+),\s*$", re.M)


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


def composition(consts):
    """Resref -> composed source, read out of `ShaderStages.Source`'s switch arms. C# owns this table;
    a name that does not resolve is a hard error, because silently dropping a piece is the drift this
    binding exists to prevent."""
    refs = {}
    for path in SOURCES:
        for name, ref in RESREF.findall(open(os.path.join(ROOT, path)).read()):
            refs[name] = ref
    body = SWITCH.search(open(os.path.join(ROOT, STAGES)).read())
    if not body:
        sys.exit(f"{STAGES}: no `Source(string resref) => resref switch` found -- did the table move?")
    out = {}
    for left, expr in ARM.findall(body.group(1)):
        if left == "_":
            continue
        ref = refs.get(left.rsplit(".", 1)[-1])
        if ref is None:
            sys.exit(f"{STAGES}: switch arm `{left}` is not a known sowx_* resref constant")
        pieces = []
        for term in expr.split("+"):
            leaf = term.strip().rsplit(".", 1)[-1]
            if leaf not in consts:
                sys.exit(f"{STAGES}: `{ref}` names shader constant {leaf}, which no render source declares")
            pieces.append(consts[leaf])
        out[ref] = "".join(pieces)
    if not out:
        sys.exit(f"{STAGES}: the composition table parsed to nothing")
    return out


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


def check_material(consts, stages):
    """#122: the synthesis chain folded off-device and compared against the stock generators."""
    missing = [t for t in ("glslangValidator", "spirv-opt", "spirv-dis") if shutil.which(t) is None]
    if missing:
        print(f"material synthesis: SKIPPED -- no {', '.join(missing)} (Arch: spirv-tools)")
        return 0
    # The real lit stage minus its main, so the folds see whatever C# actually splices into the chain.
    prelude, main = stages["sowx_ownlit_fs"], consts["OwnedLitFsMain"]
    if not prelude.endswith(main):
        sys.exit("sowx_ownlit_fs no longer ends with OwnedLitFsMain -- the material prelude broke")
    prelude = prelude[: -len(main)]
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


# Program linkage: which stages link together. Overridable stages are named by resref and come out of the
# C# table; the depth-only stages have no resref, so they are the one place this file names shader text
# directly. #147: water is the only class that tessellates -- its vertex stage must NOT project, the
# evaluation stage does, after displacing.
def programs(c, s):
    return {
        "fullbright": (s["sowx_own_vs"], s["sowx_own_fs"]),
        "depth prepass": (c["DepthVs"], c["DepthFs"]),
        "keyhole depth prepass": (c["DepthKeyVs"], c["DepthKeyFs"]),
        "caster depth": (s["sowx_cast_vs"], s["sowx_cast_fs"]),
        "caster depth (alpha-tested)": (c["CastMaskVs"], c["CastMaskFs"]),
        "clustered lit": (s["sowx_ownlit_vs"], s["sowx_ownlit_fs"]),
        "post bloom downsample": (s["sowx_post_vs"], s["sowx_bloomdn_fs"]),
        "post bloom upsample": (s["sowx_post_vs"], s["sowx_bloomup_fs"]),
        "post resolve": (s["sowx_post_vs"], s["sowx_resolve_fs"]),
        "owned water": (s["sowx_water_vs"], s["sowx_water_tcs"], s["sowx_water_tes"], s["sowx_water_fs"]),
        "instanced grass": (s["sowx_grass_vs"], s["sowx_grass_fs"]),
        "clustered light build": (s["sowx_cluster_cs"],),
    }


# Stage count -> the file extensions glslangValidator infers each stage from.
EXTS = {1: ("comp",), 2: ("vert", "frag"), 4: ("vert", "tesc", "tese", "frag")}


def main():
    c = harvest()
    s = composition(c)
    try:
        progs = programs(c, s)
    except KeyError as e:
        sys.exit(f"could not find shader constant {e} -- did a stage get renamed?")

    bad = 0
    with tempfile.TemporaryDirectory() as tmp:
        for name, stages in progs.items():
            paths = []
            for src, ext in zip(stages, EXTS[len(stages)]):
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
    print(f"{len(progs)} programs, {len(s)} overridable stages composed from ShaderStages.Source")
    bad |= check_material(c, s)
    return bad


if __name__ == "__main__":
    sys.exit(main())
