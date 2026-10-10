# Update day: a new NWN:EE client build

Beamdog pushes client builds to Steam unannounced. The moment one lands, CX's rot gate
(`EngineLayout.KeyOk`, before discovery) sees an unknown build key and stands the
**whole** extender down: the game runs vanilla, the log (and on Windows a message box)
names the build it saw and the builds it knows. Nothing crashes and nothing draws
garbage. The work below is what gets CX back on the new build.

Everything version-bound is **data**: `managed/Framework/Builds/<name>.tsv`, one
complete file per measured build (format: `ClientBuilds.cs`). It holds the build keys,
every `EngineLayout` offset for both OSes and every `SymbolMap` `At()` RVA. The code
only declares what is read. So update day means writing a new file, not editing
`EngineLayout.cs` / `SymbolMap.cs`, and CX can accept the old and new builds side by
side.

## Before: keep the old binaries

`newbuild.py` diffs the old build against the new one, and Steam overwrites the old
one in place. Snapshot every build you ship a file for, while it is still installed:

    tools/update/snapshot.sh            # -> ~/nwn-builds/<name>/{linux-x86,win32}/ ($CX_BUILDS)

`8193.37-17` is snapshotted. To prove the tooling still works against today's tables:

    tools/update/newbuild.py ~/nwn-builds/8193.37-17 "<steam>/bin" --rehearse --name x

This must report 115/115 RVAs, every offset carried, and an empty worklist.

Once the new binary is imported into the Ghidra project, dump it too (about 15 min):
`tools/ghidra/decomp-all.sh $NWN_DECOMP/<new name>`.

## Day of

1. **Generate the build file** (about 5 s, no Ghidra):

       tools/update/newbuild.py ~/nwn-builds/<old> "<steam>/bin" --name <new, e.g. 8193.38-1> --write
       tools/update/snapshot.sh    # AFTER the file exists: the new build becomes a future OLD

   It writes the new keys and stamp, one `rva` line per `At()` symbol, one `lva` line per
   `SymbolMap.LinuxAnchored` static function (the same matcher, run on the two ELFs), and one `offset`
   line per row, copied from the old file where the row is proven unchanged. It also
   prints a `WORKLIST` of everything it could not prove. `diff` the new file against the
   old one to see exactly what moved.

2. **Work the list.** Each item is a line in the file waiting for a human:
   - `# TODO rva ...`: the matcher was not confident. It gives a candidate and a
     score. Confirm it in Ghidra (method: `docs/windows-symbols.md`, "Regenerating after a client patch"),
     then turn it into an `rva` line. `ClientBuilds.SelfCheck` fails until every `At()`
     symbol has one.
   - `# TODO lva ...`: the same, for a Linux static function nm has no name for (a nuklear
     internal such as `nk_scrollbar_behavior`). Find it by its callers in the new ELF, then write the
     `lva` line; `ClientBuilds.SelfCheck` fails until every `LinuxAnchored` symbol has one.
   - `# CHECK offset ...`, **changed**: a function vouching for that row changed. The
     line carries the old values, commented out, the functions that changed, and
     usually a `candidates:` list of displacements that gained what the old value lost.
     In the server backtest the right value was among the top 3 in 45 of 61 moved
     fields. Confirm it in the named function, correct the value if it moved, and
     uncomment the line. `ClientBuilds.SelfCheck` refuses the file while any row lacks
     its line.
   - `# CHECK offset ...`, **likely**: the row's canary functions changed, but its old
     displacement is still addressed as often as before. This is a hint, not a verdict:
     4 of 24 such rows were stale in the 36→37 server backtest. Glance at the named
     function, then uncomment the line.
   - `# CHECK offset ...`, **blind**: nothing vouches for this row. Give its doc an anchor
     (`Class::Method` in the `<summary>`, or the `//` comment above its group) so the
     next update can check it.
   - `linux symbol gone` / `windows export gone`: a function was renamed or
     re-signatured. This one is a code change: the hook's signature moved.
   - `named functions changed body`: not a blocker. These are hooked or called
     functions whose code changed, so read them for moved behaviour.

3. **Build and boot**: `./build.sh && ./run-demo.sh`, `./build-win.sh && ./run-proton.sh`.
   Check the log for `ClientBuilds.SelfCheck: passed -- ... running <new>`, no
   `standing down`, and no `layout: probe FAILED` (the Byte01/VtblSlot probes are the
   last net for a wrong offset).

4. **Ship.** The release accepts both builds, so players who haven't updated yet
   aren't broken. Drop an old build's file once nobody runs it.

## What the tools trust, and why

| Output | Evidence | Backtest (public dedicated-server builds, names hidden) |
|---|---|---|
| `rva`, how `name`/`delta`/`interval` (+ `xref` for code) | export anchors bracket the target; body similarity with relink-moved displacements masked, mutual best; if that is not confident, the callers vote | code, 400 targets per run, two seeds: 8193.37-13→37-17 400/400 random, 394–396/400 with whole classes hidden; 8193.36-12→37-17 322–328/400 random, 227–240/400 classes hidden. **0 confident-wrong in 3,200** |
| `rva`, how `rtti` | MSVC Complete Object Locator names the vtable's class | exact by construction |
| `rva`, how `xref` (data) | ≥2 distinct referrer functions (or one matched by name and byte-identical), ≥90% agreeing, each re-read at the same instruction in the new build, never at an address another function owns by name | 150 globals per run: 110–113 confident (hotfix), 80–82 (major). **0 confident-wrong in 1,200** |
| offset **carried** | every canary function byte-identical (union of both builds' relink masks) | **0 stale** on both pairs |
| offset **likely** | displacement count unchanged in the anchors | 4 stale of 24 on 36→37: why it's only a hint |

Rerun them after changing the matcher:

    tools/update/backtest.py OLD/nwserver.exe NEW/nwserver.exe [--classes CNWSCreature,...]
    tools/update/layout_backtest.py OLD/nwserver-linux NEW/nwserver-linux

The server zips (`https://nwn.beamdog.net/downloads/nwnee-dedicated-<ver>.zip`) are
public and ship every build, so they are the test corpus. The client itself only
comes through Steam.

## Not covered

- **Behaviour change under the same name.** A function can keep its name, its
  signature and every offset CX reads, yet do something new. The tool lists the named
  functions whose bodies changed; reading them is the human part.
- **Hosted engine shaders.** Alloy hosts stock programs (`docs/shader-overrides.md`).
  A changed stock shader shows up as a compile or link line in the boot log, not in
  the worklist.
