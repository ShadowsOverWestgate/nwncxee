# 0012 — Shaders ship embedded, never as content

Status: superseded by [0024](0024-hak-shipped-alloy-overrides.md) (2026-09-13) -- the ban on hak-shipped overrides is lifted; the embedded defaults, the dump and the guards stay.
Originally: accepted (2026-08-24 grilling). Supersedes the shipping half of #92; constrains ADR 0009's
Alloy pipeline and everything downstream of it.

## Context

Every Alloy stage exists twice: as a `const string` raw literal in `managed/Alloy/*.cs`,
and as a `sowx_*.shd` shipped in `sow-topdata`'s hak. The override wins at runtime, so the shipped
copy is the one that runs.

The two copies travel on channels with very different speeds. Content is one tag push away from
every player automatically (depot upload, NWSync emit, channel flip). The extender is a manual
download with no auto-updater and, at the time of writing, no anonymously readable release URL.
Nothing anywhere compares an extender version to a content version, because no content version
exists.

That asymmetry is not theoretical. Measured on 2026-08-24: **all eleven shipped `.shd` were stale**,
three of them missing the #152 shade-band LOD and its call sites, while `check-shaders.py` printed
`record ok` for every file and exited 0, and every runtime guard (`RecordSafe`, `DepthSafe`,
`CutoutSafe`, `SkinSafe`, `LinearSafe`) passed. The guards were built for the #120/#132 layout-drift
class; a *feature-loss* drift is invisible to all of them. The symptom is not a black world — it is
correct pixels at the wrong cost, silently, with no log line.

The duplication is also the multiplier. The shared libraries (`FragmentLib` at 233 lines,
`LocalShadowLib`, `SunShadowLib`, `KeyFogFs`, `WaveLib`) are flattened into three files each, so one
edit to one helper staled three overrides at once — roughly 38% of the shipped tree is redundant copy.

Worse, the channel taxes the renderer's design. `AoBit = 1 << 7` is deliberately *not* `1 << AoSlot`
purely so a hak shipped against an older build does not misread slot 64 as env-mapped
(`OwnedDrawService.cs:199-202`). New uniforms are added so a stale override degrades to location -1
(`OwnedLights.cs:504-510`). Those constraints exist only because the channel exists.

## Decision

**The extender ships every shader embedded. No content repo ever carries a copy.**

- A shader is *code*, not data. `cx_ownlit_fs` declares the `SowxDraw` struct whose layout lives in
  a C# file, reads uniform blocks the C# fills, and honours a clip expression the depth prepass
  depends on character-for-character. It ships with the renderer it agrees with, or the agreement is
  unenforceable.
- **The override read path stays, and is first-class.** Sowx ships defaults and lets a user replace
  them; that is a core property of the tool, not a delivery mechanism. `ShaderResource.Override` and
  the five `*Safe` guards are therefore load-bearing and stay.
- **The two roles are now distinct.** *Shipping* a shader is forbidden; *overriding* one is
  supported. Nothing in a hak, NWSync repo, or content release may carry a `sowx_*` resref.
- **The dump is how a customizer starts.** A `/dumpshaders` chat command writes every overridable
  resref to `<user directory>/cx_shader_dump/`, plus the bindless variant. Because it dumps what is
  compiled in, the starting point is current by construction — the drift class is structurally
  impossible rather than detected, in the same spirit as #127's grass punch-through.

## Consequences

- The seventeen overridable resrefs become the written-down contract, replacing
  `check-shaders.py`'s `override_sources` table as the authority. Six of them
  (`cx_post_vs`, `cx_bloomdn_fs`, `cx_bloomup_fs`, `cx_resolve_fs`, `cx_cast_vs`,
  `cx_cast_fs`) were overridable at runtime but never shipped, never dumped and never guarded;
  they join the contract and gain guard coverage.
- `/dumpshaders` replaces `SOWX_DUMP_SHADERS` (ADR 0013). The headless path it served — the only
  offline compile check on the `BindlessFs` rewrite, which `check-shaders.py` cannot reproduce
  because it is a C# function rather than a constant — moves to a launcher flag or config line.
- Dumped files keep their load-bearing warnings, trimmed to the constraint plus a pointer. A dumped
  file travels away from this repo, so the warning cannot live only in the repo. The reasoning moves
  to a single markdown document.
- Composition lives in C#. `check-shaders.py` may keep its override half, but the C# table is
  authoritative and the two must be bound — an assertion that they agree, or a harvest that reads
  the C# one. Two hand-maintained copies of a composition table is the shape this ADR deletes.
- The design taxes above can be paid off. `AoBit`'s displacement and the degrade-to--1 discipline
  guard against a *shipped* stale copy; with none shipped, an override is a user's own doing and
  fails loudly at the guards instead. Removing them is not urgent and is out of scope here.
- Existing `sowx_*.shd` in `sow-topdata` are deleted, along with the `is_sowx` special case in its
  `validate-shaders.py` and that directory's README. Handled manually, outside the rework spec.
