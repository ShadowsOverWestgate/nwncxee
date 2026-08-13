# Ported skills

Copied from `sow-codebase/.agents/skills/`. They carry hard-won NWN engine
knowledge that applies to the client too -- but they were written for the
**server** (`nwserver`), so adapt, don't obey literally:

- **sow-hooking** -- mindset + symbol discovery transfer directly (the client
  binary is unstripped, ~40k symbols). BUT the mechanism does not: the server
  hooks via `NativeHookRegistry`/`HookPoint`, which does not exist here. The
  client extender hooks with **funchook**. Read it for *what/where/whether* to
  hook and Before/After/SkipOriginal semantics, not the C# hook-registration API.
- **sow-threading** -- applies as-is in spirit. The client's hot thread is the
  render/main loop; a GC pause there stutters frames. NativeAOT GC runs on
  whatever thread the hook fired on -- keep hot hooks allocation-free.
- **nwn-anvil-conventions** -- relevant for reading engine structs by raw
  pointer and native memory safety. The Anvil/NwModule/service-binding parts are
  server-only; the CNWS* layout and pointer-safety discipline are what carry over.

`integration-testing` and `nui-building` were left behind -- server-only.
