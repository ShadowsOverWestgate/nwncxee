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

Why a second copy of a composition is never tolerable: a `CxDraw` record layout that lives in C#
was, until ADR 0012, also hand-copied into shipped `cx_*.shd`. When the record grew in #120 the
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
    "managed/Alloy/AlloyRenderer.Shaders.cs",
    "managed/Alloy/Pass/AlloyLights.cs",
    "managed/Alloy/Pass/LightmapBake.cs",           # #674: ChartBufDecl, which the lit stage composes
    "managed/Alloy/Pass/LightmapBake.Shaders.cs",
    "managed/Alloy/Pass/CasterPass.cs",
    "managed/Alloy/Pass/GrassField.cs",
    "managed/Alloy/Pass/PostChain.cs",
    "managed/Alloy/Pass/OitPass.cs",
    "managed/Alloy/Pass/PostStack.cs",
    "managed/Alloy/Pass/SmaaLib.cs",   # before SmaaStage: the harvest folds references in list order
    "managed/Alloy/Pass/SmaaStage.cs",
    "managed/Alloy/Pass/SsaoPass.cs",
    "managed/Alloy/Pass/Halos.cs",
    "managed/Alloy/Pass/Weather.Draw.cs",
    "managed/Alloy/Pass/Weather.Surface.cs",
    "managed/Alloy/Pass/FrameSnapshot.cs",
    "managed/Alloy/Pass/MotionVectors.cs",
    "managed/Alloy/Pass/TaaStage.cs",
    "managed/Alloy/Pass/ToonStage.cs",
    "managed/Alloy/Pass/FbPostStage.cs",
    "managed/Alloy/AlloyRenderer.Motion.cs",
    "managed/Alloy/Highlight.cs",
    "managed/Alloy/Pass/IdTarget.cs",
]
# ShaderResource.Tokens' three sizing tokens. Any legal value compiles the same shapes.
TOKENS = {"$CAP$": "128", "$SORT$": "32", "$GROUP$": "8", "$SRGB$": "2.2", "$BAND$": "45.0, 0.0667", "$AA$": "3",
          # #352: the hosted post chain built with sharpen + vibrance on (the two hosted option bits), high quality
          "$POSTMASK$": "9", "$SQM$": "2"}
STAGES = "managed/Alloy/Core/ShaderStages.cs"
# ...and an expression-bodied `static string X => A + B;` alias (Weather.Draw.cs), which is a composition too.
DECL = re.compile(r"\s*(?:private|internal|public)?\s*(?:static)?\s*(?:const|readonly) string (\w+) = (.*)$"
                  r"|\s*(?:private|internal|public)\s+static string (\w+) => (.*)$")
PIECE = re.compile(r'"""\n(.*?)\n\s*"""|([A-Za-z_]\w*)', re.S)
RESREF = re.compile(r'(\w+)\s*=\s*"(cx_\w+)"')
# `internal static string Source(string resref) => resref switch { ... };` -- the shape ShaderStages
# guarantees. One arm per resref: a qualified resref constant, then a plain `+` chain of constants.
SWITCH = re.compile(r"string Source\(string resref\) => resref switch\s*\{(.*?)\n\s*\};", re.S)
ARM = re.compile(r"^\s*([\w.]+)\s*=>\s*([^,]+),\s*$", re.M)


def harvest():
    """Every `const string` raw-literal in the render sources, with concatenated constants inlined."""
    lines = []
    for path in SOURCES:
        lines += open(os.path.join(ROOT, path)).read().split("\n")
    decls, i = [], 0
    while i < len(lines):
        m = DECL.match(lines[i])
        i += 1
        if not m:
            continue
        name, expr = (m.group(1), [m.group(2)]) if m.group(1) else (m.group(3), [m.group(4)])
        fenced = expr[0].count('"""') % 2 == 1
        while fenced or not expr[-1].rstrip().endswith(";"):
            expr.append(lines[i])
            if lines[i].count('"""') % 2 == 1:
                fenced = not fenced
            i += 1
        decls.append((name, "\n".join(expr)))
    # Two passes: an alias (`A = B + C;`, no literal of its own -- GrassVs is `static readonly`, declared
    # ABOVE the pieces it names) resolves only once every piece is known, so fold to a fixpoint rather
    # than in declaration order. A const string that is not shader text -- a config key, a log line --
    # names no known constant and folds to nothing, so it still drops out.
    consts = {}
    for _ in range(4):
        for name, text in decls:
            out = []
            for p in PIECE.finditer(text):
                if p.group(1) is not None:
                    out.append(textwrap.dedent(p.group(1)) + "\n")
                elif p.group(2) in consts:
                    out.append(consts[p.group(2)])
            if out:
                consts[name] = "".join(out)
    return consts


# #346, the vanilla bridge: a stage can carry `// $VANILLA <resref>` … `// $VANILLA-END`, and at runtime
# the engine's own copy of that file replaces the region. The fallback inside it is a STUB -- the feature
# off -- so compiling that proves only that the world still renders. What is worth checking offline is the
# real content file against Alloy's environment, which is why this splices from a content checkout when it
# can find one. Set CX_CONTENT_SHD to point elsewhere; missing is not an error, it is the stub's case.
CONTENT_SHD = os.environ.get("CX_CONTENT_SHD",
                             os.path.expanduser("~/repos/sow-topdata/assets/shaders"))
# #352: the ENGINE's own includes a content file reaches (inc_random, inc_framebuffer, inc_postpr_debug),
# which the runtime resolves off the resource chain and a content checkout never carries. An extracted
# ovr.bif tree; missing means those includes drop, as an absent chain file would.
STOCK_SHD = os.environ.get("CX_STOCK_SHD", os.path.expanduser("~/nwnexplorer/shaders"))
VANILLA = re.compile(r"^([ \t]*)// \$VANILLA(-HOST)? (\w+)\n(.*?)^[ \t]*// \$VANILLA-END[ \t]*$",
                     re.S | re.M)
# Mirrors VanillaShader.Skip: includes the stage's own environment stands in for, never followed.
SKIP = re.compile(r"^[ \t]*// \$VANILLA-SKIP (.*)$", re.M)


# Mirrors VanillaShader.Supplied: the engine's uniform/varying interface, which the renderer's own
# environment block stands in for and which must never be pulled into an Alloy stage.
SUPPLIED = {"inc_common", "inc_uniforms"}
# ...and VanillaShader.Substituted: the one include a HOSTED stage gets a stand-in for (#350). The text is
# the C# constant, read out of the harvest so the two cannot drift.
SUBSTITUTED = {"inc_standard": "VanillaStandardFs"}


def find_shd(name):
    for base in (CONTENT_SHD, STOCK_SHD):
        if not os.path.isdir(base):
            continue
        for root, _, files in os.walk(base):
            if name + ".shd" in files:
                return os.path.join(root, name + ".shd")
    return None


def expand(text, done, consts, depth=0):
    """A content file's own #includes, resolved out of the checkout -- the C# side resolves them through
    the resource chain, and a cx_* hook file is composed OF them. One visit each PER STAGE (`done` is
    shared across a stage's regions, as VanillaShader.Splice shares its list): the files carry #ifndef
    guards, so a second inclusion is already a no-op in GLSL. An include the checkout does not answer for is
    dropped, which is how the ones the renderer's environment supplies (inc_uniforms) stay out."""
    out = []
    for line in text.split("\n"):
        t = line.lstrip()
        if not t.startswith("#include"):
            out.append(line)
            continue
        m = re.search(r'"([^"]+)"', t)
        if not m or m.group(1) in done or m.group(1) in SUPPLIED or depth >= 4:
            continue
        done.add(m.group(1))
        if m.group(1) in SUBSTITUTED:
            out += expand(consts[SUBSTITUTED[m.group(1)]], done, consts, depth + 1)
            continue
        hit = find_shd(m.group(1))
        if hit:
            out += expand(open(hit).read(), done, consts, depth + 1)
    return out


MAIN_FN = re.compile(r"\bvoid\s+main\s*\(\s*(?:void)?\s*\)")
# Mirrors VanillaShader.StubFn: a function DEFINITION in a stub, anchored on its opening brace.
STUB_FN = re.compile(r"^\s*(?:\w+\s+)+(\w+)\s*\([^)]*\)\s*\{", re.M)
OWN_DEFINE = re.compile(r"^[ \t]*#[ \t]*define[ \t]+(\w+)", re.M)


def host(stock, resref, done, consts):
    """Mirrors VanillaShader.HostText (#350): a content FRAGMENT STAGE rewritten to run as a function of the
    lit program. main() renamed, gl_FragColor redirected into FragmentColor, the stage's OWN #defines scoped
    to it (undefined before and after, then the environment's macros re-issued), and its declaration of a
    name the environment supplies dropped. The wrapper reads the MATERIAL_*_VALUE terms back."""
    text = "\n".join(expand(stock, done, consts))
    if not MAIN_FN.search(text):
        return None
    text = MAIN_FN.sub(f"void CxHostMain_{resref}()", text, count=1)
    text = re.sub(r"\bgl_FragColor\b", "FragmentColor", text)
    env = OWN_DEFINE.findall(consts["VanillaEnvDefs"])
    text = re.sub(r"^[ \t]*uniform\b[^;\n]*\b(?:" + "|".join(env) + r")[ \t]*;[^\n]*\n?", "", text, flags=re.M)
    own = list(dict.fromkeys(OWN_DEFINE.findall(stock)))
    undef = "".join(f"#undef {n}\n" for n in own)
    return (undef + text + "\n"
            + f"void CxHost_{resref}()\n{{\n    CxHostMain_{resref}();\n"
            + "    fSpecularity = MATERIAL_SPECULARITY_VALUE;\n    fRoughness = MATERIAL_ROUGHNESS_VALUE;\n"
            + "    fMetallicness = MATERIAL_METALLICNESS_VALUE;\n    SpecularColor = MATERIAL_SPECULAR_COLOR_VALUE;\n}\n"
            + undef + consts["VanillaEnvDefs"])


def splice(src, seen, consts):
    """Replace each marked region with the content repo's copy of that .shd, where one exists."""
    done = set()
    for m in SKIP.finditer(src):
        done.update(m.group(1).split())
    def one(m):
        indent, hosted, resref, stub = m.group(1), m.group(2), m.group(3), m.group(4)
        hit = find_shd(resref)
        if hit is None:
            seen.setdefault(resref, None)
            return m.group(0)
        seen[resref] = hit
        if resref in done:      # an earlier region already included it: its functions exist
            return indent + f"// $VANILLA{hosted or ''} {resref}"
        done.add(resref)
        stock = open(hit).read()
        body = host(stock, resref, done, consts) if hosted else "\n".join(expand(stock, done, consts))
        # Mirrors VanillaShader.Validate: every function the stub defines must appear in the spliced text,
        # or the runtime compiles the stub for this region -- so the check does the same rather than
        # accepting text the runtime would reject.
        missing = [n for n in STUB_FN.findall(stub) if not re.search(r"\b" + n + r"\s*\(", body or "")]
        if body is None or missing:
            print(f"vanilla bridge: {resref}.shd " + ("has no main() to host" if body is None
                  else f"does not define {', '.join(missing)}") + " -- the stub stands")
            return m.group(0)
        return (indent + f"// $VANILLA{hosted or ''} {resref}\n"
                + "\n".join(indent + l if l.strip() else "" for l in body.split("\n")))
    return VANILLA.sub(one, src) if os.path.isdir(CONTENT_SHD) else src


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
            sys.exit(f"{STAGES}: switch arm `{left}` is not a known cx_* resref constant")
        pieces = []
        for term in expr.split("+"):
            leaf = term.strip().rsplit(".", 1)[-1]
            if leaf not in consts:
                sys.exit(f"{STAGES}: `{ref}` names shader constant {leaf}, which no render source declares")
            pieces.append(consts[leaf])
        out[ref] = "".join(pieces)
    seen = {}
    for ref in out:
        out[ref] = splice(out[ref], seen, consts)
    for resref, path in sorted(seen.items()):
        print(f"vanilla bridge: {resref}.shd "
              + (f"spliced from {os.path.relpath(path, os.path.expanduser('~'))}" if path
                 else f"NOT FOUND under {CONTENT_SHD} -- compiled the stub, which checks nothing"))
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
        return f"CxSynthPbr(vec3({declared[0]}, {declared[1]}, {declared[2]}), {slots}, {env}, {st}, {rt})"

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
                      f"CxSpecColor(vec4(0.0), vec3({base}), {metal}, {slots}).r",
                      stock_spec_color(base, metal, slots)))
    # ...and the declared colour wins outright: 3.0 only if all three channels come back as
    # CustomSpecularColor itself. Compared per channel because a vec3 `==` lowers to OpAll, which is the one
    # step spirv-opt will not fold.
    got = "CxSpecColor(vec4(0.3, 0.6, 0.9, 1.0), vec3(0.1), 0.0, 0)"
    want = "CxLinear(vec3(0.3, 0.6, 0.9))"
    cases.append(("declared CustomSpecularColor wins",
                  " + ".join(f"float({got}.{ch} == {want}.{ch})" for ch in "rgb"), 3.0))
    return cases


def fold(prelude, expr, tmp):
    """Compile a fragment stage whose only output is `expr` and read the constant it folds to."""
    src = (prelude + "\nlayout(location = 0) out vec4 cxProbe;\n"
           "void main() { cxProbe = vec4(" + expr + "); }\n")
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
    m = re.search(r"OpStore %cxProbe (%\S+)", dis)
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
    prelude, main = stages["cx_ownlit_fs"], consts["OwnedLitFsMain"]
    if not prelude.endswith(main):
        sys.exit("cx_ownlit_fs no longer ends with OwnedLitFsMain -- the material prelude broke")
    prelude = prelude[: -len(main)]
    for tok, val in TOKENS.items():
        prelude = prelude.replace(tok, val)
    prelude = prelude.replace("out vec4 frag;", "")     # our own probe output replaces it
    # SPIR-V wants an explicit location on every user in/out; the GL path leaves them implicit.
    loc = [0]
    def locate(m):
        # An ARRAY uniform consumes one location per element column, so it gets a generous stride; a scalar
        # or vector consumes one, and a mat4 four. Striding everything by 64 ran past the implementation
        # limit once the lit stage started carrying the engine's own uniform names too (#237).
        stride = 64 if "[" in m.group(0) else 4
        loc[0] += stride
        return f"layout(location = {loc[0] - stride}) {m.group(0)}"
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
# directly. #339 took the tessellation out with the custom water shader: water is a plain vertex/fragment
# pair again, like every other class here.
def programs(c, s):
    return {
        "fullbright": (s["cx_own_vs"], s["cx_own_fs"]),
        "depth prepass": (c["DepthVs"], c["DepthFs"]),
        "keyhole depth prepass": (c["DepthKeyVs"], c["DepthKeyFs"]),
        "caster depth": (s["cx_cast_vs"], s["cx_cast_fs"]),
        "caster depth (alpha-tested)": (c["CastMaskVs"], c["CastMaskFs"]),
        "clustered lit": (s["cx_ownlit_vs"], s["cx_ownlit_fs"]),
        "clustered lit (OIT lists)": (s["cx_ownlit_vs"], s["cx_ownlit_fs"].replace("#version 460 core\n", "#version 460 core\n#define CX_OIT_LISTS 1\n", 1)),
        "post bloom downsample": (s["cx_post_vs"], s["cx_bloomdn_fs"]),
        "post bloom upsample": (s["cx_post_vs"], s["cx_bloomup_fs"]),
        "post resolve": (s["cx_post_vs"], s["cx_resolve_fs"]),
        "post stack gamma": (s["cx_post_vs"], s["cx_postgam_fs"]),
        "post stack toon": (s["cx_post_vs"], s["cx_toon_fs"]),
        "post stack fbpost (hosted content chain)": (s["cx_post_vs"], s["cx_fbpost_fs"]),
        "post stack aa edges": (s["cx_post_vs"], s["cx_smaaedge_fs"]),
        "post stack aa weights": (s["cx_post_vs"], s["cx_smaawgt_fs"]),
        "post stack aa blend": (s["cx_post_vs"], s["cx_smaablend_fs"]),
        "ssao visibility": (s["cx_post_vs"], s["cx_ssao_fs"]),
        "ssao blur": (s["cx_post_vs"], s["cx_ssaoblur_fs"]),
        "halos": (s["cx_halo_vs"], s["cx_halo_fs"]),
        "weather rain": (s["cx_weather_vs"], s["cx_rain_fs"]),
        "weather snow": (s["cx_weather_vs"], s["cx_snow_fs"]),
        "ssao temporal": (c["FullscreenVs"], c["TemporalFs"]),
        "motion vectors": (c["FullscreenVs"], c["CameraFs"]),
        "mover motion": (c["MotionVs"], c["MotionFs"]),
        "taa resolve": (c["FullscreenVs"], c["TaaFs"]),
        "post stack taa": (s["cx_post_vs"], s["cx_taa_fs"]),
        "highlight composite": (c["FullscreenVs"], c["HighlightCompFs"]),
        "owned water": (s["cx_water_vs"], s["cx_water_fs"]),
        "instanced grass": (s["cx_grass_vs"], s["cx_grass_fs"]),
        "grass depth prepass": (s["cx_grass_vs"], c["GrassDepthFs"]),
        "clustered light build": (s["cx_cluster_cs"],),
        "sky decode": (c["FullscreenVs"], c["SkyDecodeFs"]),
        "oit resolve": (c["FullscreenVs"], c["OitResolveFs"]),
        "oit clear": (c["FullscreenVs"], c["OitClearFs"]),
        "lightmap bake": (c["BakeVs"], c["BakeFs"]),
        "lightmap finish": (c["FullscreenVs"], c["FinishFs"]),
    }


# Stage count -> the file extensions glslangValidator infers each stage from.
EXTS = {1: ("comp",), 2: ("vert", "frag"), 4: ("vert", "tesc", "tese", "frag")}


SIDE_EFFECTS = re.compile(r"\b(atomic\w*|imageStore|imageAtomic\w*|gl_FragDepth)\b|\blayout\([^)]*\)\s*(?!readonly)(?:coherent\s+|volatile\s+|restrict\s+)*buffer\b")


def check_early_z(progs):
    """A fragment-stage side effect (SSBO/image store or atomic, gl_FragDepth) costs the program its early-Z,
    so it may reach ONLY the OIT-lists twin: every other stage an opaque draw uses must preprocess clean.
    (The fullbright stage takes the block at RUNTIME through OitPass.WithLists -- a deliberate exception.)"""
    with tempfile.TemporaryDirectory() as tmp:
        def effects(name):
            src = progs[name][1]
            for tok, val in TOKENS.items():
                src = src.replace(tok, val)
            p = os.path.join(tmp, "ez.frag"); open(p, "w").write(src)
            r = subprocess.run(["glslangValidator", "-E", p], capture_output=True, text=True)
            return sorted({m.group(0).split("(")[0].strip() for m in SIDE_EFFECTS.finditer(r.stdout)})
        bad = 0
        for name, want in (("clustered lit", False), ("clustered lit (OIT lists)", True), ("sky decode", False), ("owned water", False)):
            got = effects(name)
            ok = bool(got) == want
            print(f"early-z: {name}: {'ok' if ok else 'FAILED'} ({'side effects: ' + ', '.join(got) if got else 'no side effects'})")
            bad |= not ok
        return bad


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
    bad |= check_early_z(progs)
    bad |= check_material(c, s)
    return bad


if __name__ == "__main__":
    sys.exit(main())
