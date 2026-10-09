using System;
using System.Threading;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>CX-120 / ADR 0028 section 5: Alloy's GL 4.6 context on its OWN thread, drawing frame N while the
/// game thread simulates N+1. Two snapshots ping-pong (<see cref="RenderSnapshot"/>); the publish waits only
/// if the renderer still holds N-1, so latency is bounded at one frame and a CPU-bound frame costs the LARGER
/// of the two threads' times instead of their sum.
///
/// <para><b>Why a second thread is possible at all.</b> A GL context is current on at most one thread. Today
/// <c>GlContextService</c> makes the 4.6 context current on the game thread inside an Enter/Leave bracket
/// around every Alloy pass and hands it back before the present -- two <c>glXMakeCurrent</c>/
/// <c>wglMakeCurrent</c> calls plus a fence pair per frame. With a render thread the 4.6 context is made
/// current ONCE, on this thread, and never released: the game thread keeps the engine's 3.3 context and never
/// calls <c>Enter</c>. So the bracket does not merely move -- it goes away, which is A1 in
/// <c>CX-128</c>.</para>
///
/// <para><b>What still reaches the ENGINE's context on the game thread</b> -- and therefore what this thread
/// must be ordered against, not merely run beside. Measured on this tree; each is why the knob defaults to 0
/// until the harness clears it:</para>
/// <list type="number">
/// <item>CX's own 2D UI. <c>UiHost.DrawFrame</c> draws every registered window (chat, the settings panel,
/// nameplates, combat text) with raw GL in the ENGINE context, and <c>UiHostService.HandBack</c> resyncs
/// <c>GLRender</c>'s shadow cache afterwards. Per frame. CX-188 ORDERED it rather than moved it: it runs on
/// the game thread inside the fence, and <see cref="UiOrderSettled"/> is the argument.</item>
/// <item>NUI -- <b>not on this list for GL, but on <see cref="GuiBuffersOwned"/>'s.</b>
/// <c>Pass/NuiFlush.cs</c> chains <c>nk_sdl_render</c> but does not call it: it converts nuklear's command
/// list and issues the quads through <c>GuiCapture</c>, in ALLOY's context ("drawn by Alloy;
/// nk_sdl_render is not called"), and only the nuklear TEXTURES are engine-created names, which the share
/// list reaches. So NUI reaches no engine GL -- but the conversion runs on the GAME thread, in the overlap
/// window, and writes the buffers the renderer is flushing. That is the one precondition the fence cannot
/// close.</item>
/// <item>The present itself. <c>ScreenOwner.Present</c> binds the engine's default framebuffer, calls
/// <c>SDL_GL_SwapWindow</c> and then <c>RenderInterface::ResetRenderStates</c> -- all in the engine context,
/// on the game thread, because that is the context whose back buffer carries the 2D above.</item>
/// <item>Movies. <c>MovieService</c> hands a movie frame back to the engine's own quad
/// (<c>Renderer.Movie 0</c> makes that every frame), and <c>ScreenOwner.EngineFrame</c> is the latch that says
/// so. On demand.</item>
/// <item>Snapshots. <c>AurSnapshot</c> and the engine's render-to-texture read back what the world frame drew,
/// through <c>glReadPixels</c> in the engine context. On demand.</item>
/// <item>The engine's shader compiles at boot and at area load (<c>area-load-is-shader-compile</c>): engine
/// context, game thread, and they BLOCK -- which is exactly why <c>GlContextService.SwapNow</c> exists.</item>
/// <item>Texture decode and upload. Alloy's own are share-listed and so are this thread's; what the engine
/// still loads through its own <c>CAurTexture</c> path happens on the game thread, in its context, at an area
/// load. CX-117's registry is what shrinks this one.</item>
/// </list>
/// <para><b>The blockers, found by review rather than by a run.</b> The list above is about the ENGINE's
/// context. The thread's own frame has a second set on Alloy's side, and they are the five preconditions
/// (<see cref="GuiBuffersOwned"/>, <see cref="TextDocsPublished"/>, <see cref="UiOrderSettled"/>,
/// <see cref="FrameInputsPublished"/>, <see cref="ScreenHandoffSettled"/>). Each gates the start, each is set
/// by whoever makes it true, and the refusal names EVERY unmet one -- so the knob refuses loudly instead of
/// producing a corruption nobody would trace back to a flag. CX-188 closed two of the five and corrected the
/// other three, whose descriptions had drifted from the code they describe.</para>
/// <para><b>THE FENCE</b>, which is the one mechanism everything else is stated against.
/// <c>RenderSnapshot.AwaitDrained</c> at the top of <c>SeamDraw.OnBoundary</c> blocks until the renderer has
/// released the frame it was handed. From the moment it returns until the next <c>Publish</c>, the render
/// thread is parked in <c>RenderSnapshot.Take</c> with nothing to draw, so framebuffer 0, the engine's GL
/// state caches and the engine's own context are the game thread's -- which is what every <c>PrePresent</c>
/// participant after <c>SeamDraw</c>, <c>UiHostService</c>'s hand-back and <c>ScreenOwner.Present</c> already
/// assume. It is not a new wait: <c>Publish</c> waited for exactly this at exactly this point, because
/// latency is bounded at one frame either way. What is new is that the gap is NAMED, so the present and the
/// 2D UI are ordered inside it instead of racing it.</para>
/// <para><b>What the fence does NOT cover, and is therefore what is left.</b> The window from the publish to
/// the NEXT frame's fence is the one in which the two threads genuinely overlap -- that overlap IS the
/// feature, it is where <c>max(game, render)</c> comes from -- and in it the game thread runs the engine's
/// whole main loop: the network receive, the resource manager, the AI update and <c>CGuiMan::UpdateAndRender</c>
/// with <c>nk_sdl_render</c> inside it. Two things the renderer touches are also touched there, and they are
/// <see cref="GuiBuffersOwned"/> and <see cref="ScreenHandoffSettled"/>.</para>
/// <para><b>What the pose columns do NOT need.</b> The obvious reading -- copy <c>SceneDb</c>'s pose columns
/// into the frame -- is an expensive answer to the wrong question. <see cref="RenderSnapshot.Fold"/> already
/// runs on the game thread inside <see cref="RenderSnapshot.Publish"/>, under the lock that IS the frame's
/// happens-before edge, and its job is already to turn columns into the renderer's own persistent records. The
/// columns are read off-thread only because <c>StageDbPart</c> re-reads them at DRAW time for the per-frame
/// half of the record (model, alpha, dissolve, the lighting folds). Moving those reads into the fold costs a
/// change-driven write per dirty part instead of a copy of every column every frame -- and it is the same
/// change that makes the stage loop stop touching the database at all.</para>
/// <para>Items 1-3 are the ordering problem, and they are why this thread does NOT present: the game thread
/// still does, from the engine context, inside the fence, after this thread has resolved the frame and
/// released it. Items 4-7 are on-demand or per-session, and <see cref="Pause"/>/<see cref="Unpause"/> is the
/// hook that parks this thread around one of them. <b>Nothing calls that pair yet</b> -- each entry gets its
/// call when the live harness shows it needs one, and saying so is better than a pause nobody can point at.
/// The fence does NOT cover them: a movie frame or a snapshot read-back happens in the engine's own loop,
/// i.e. in the overlap window, so each still owes the handshake <see cref="Pause"/> describes.</para>
///
/// <para><b>No silent fallback.</b> A fault inside the thread's bracket, or a refused
/// <c>MakeCurrent</c>, stands the thread down with a loud line and hands the work back to the
/// single-threaded snapshot path -- the same publish/consume protocol, drained on the game thread, so the
/// mode difference is which thread calls <see cref="DrawFrame"/> and nothing else.
/// <para>The stand-down is a HANDSHAKE, not a flag: the thread must release the context and say so before
/// <see cref="Live"/> goes false, or the game thread takes a context the renderer still holds and GLX answers
/// <c>BadAccess</c> -- which Xlib's default handler turns into process exit, not an error code.</para></summary>
internal static class RenderThread
{
    private static ExtenderLog? _log;
    private static Thread? _thread;
    private static volatile bool _run, _stoodDown, _paused;
    private static readonly object _pauseGate = new();
    private static long _frames, _faults;
    private static string _reason = "";

    /// <summary>Draw one published frame. Set by seam-feed (Alloy's frame entry over the DB); until it is,
    /// there is nothing to run and the thread refuses to start rather than spin.</summary>
    internal static Action<RenderSnapshot.Frame>? DrawFrame;

    /// <summary><b>Precondition: </b><c>GuiCapture</c>'s one static bank of buffers is not written by two
    /// threads in the same frame. <b>UNMET</b>, and the one thing the fence provably cannot fix.
    /// <para>Each precondition is a FACT about what the frame touches, not a capability, so none can be
    /// checked from here -- each is set by whoever makes it true, and the thread refuses to start while any is
    /// unset, naming EVERY unmet one (<see cref="Unmet"/>). They are separate bools rather than one
    /// <c>GuiOffThread</c> flag because they are met by different work at different times, and one flag means
    /// whoever finishes first arms the others' races. Setting one that is not actually true is the one way to
    /// turn this knob into a corruption.</para>
    /// <para><b>The measured producers in DB mode are two, on two threads.</b> <c>NuiFlush</c> converts
    /// nuklear's command list into <c>GuiCapture</c> quads at <c>nk_sdl_render</c>, which is inside
    /// <c>CGuiMan::UpdateAndRender</c> -- the GAME thread, in the overlap window. <c>DbGui</c> and
    /// <c>TextQuads.Issue</c> append the panel layers inside <c>SeamDraw.DrawSnapshot</c>, and
    /// <c>GuiCapture.Lead</c> + <c>GuiPass.FlushNow</c> reorder and then RESET the whole bank at the end of
    /// it -- the RENDER thread. So the game thread is filling a bank the renderer is rotating and clearing.
    /// <para>Two earlier readings of this were wrong and are corrected here. <c>UiHost</c>'s 2D windows are
    /// NOT a <c>GuiCapture</c> producer: they draw raw GL in the engine context through <c>TextRig</c>, which
    /// is why they are <see cref="UiOrderSettled"/>'s problem and not this one. And <c>NuiFlush</c> is not
    /// engine-mode-only: it installs whenever <c>RspShim.Armed</c>, which is a statement about hook installs
    /// and not about the scene mode -- <c>DrawSnapshot</c>'s own <c>int nui = GuiCapture.LayerN</c> exists
    /// precisely because NUI captured earlier in the same DB frame.</para></para>
    /// <para><b>The way out, and why it is not taken blind.</b> Ping-pong the NUI bank off the snapshot's
    /// frame: <c>GuiCapture</c>'s mutable state becomes a bank object, <c>NuiFlush</c>'s two entry points
    /// write "the NUI bank" while everything else writes the main one, the game thread flips the NUI bank at
    /// the publish, and the flush draws main-then-NUI (which is also what <c>Lead</c> does today, so the
    /// ordering argument does not change). The second bank only needs NUI's own budget -- 512 KiB of vertices
    /// -- not a clone of the 8 MiB pool, and with one bank it collapses to exactly today's single path. What
    /// stops it here is that it is a mechanical rewrite of all of <c>GuiCapture</c> and <c>GuiPass.Draw</c>
    /// whose failure mode is a black interface, on a branch that cannot be run; <c>reason-before-harness</c>
    /// says reason from the code, and the code says this one wants a live A/B behind it.</para></summary>
    internal static bool GuiBuffersOwned;

    /// <summary>Precondition: <c>TextObjects</c>/<c>TextQuads</c> document state is published to the frame
    /// rather than rebuilt under its reader. <c>TextQuads.Build</c> is game-thread-on-change and
    /// <c>TextQuads.Issue</c> runs in the per-view draw, walking <c>d.Quads</c> through UNCHECKED pointer
    /// arithmetic (<c>q + run.First * VertFloats</c>) against a <c>d.Runs</c>/<c>d.RunCount</c> the builder
    /// rewrites -- so a chat line arriving mid-draw is an out-of-bounds read, not a torn glyph.
    /// <para><b>MET</b>, and already met before CX-188 looked: <c>TextQuads.Install</c> sets it, because a
    /// build fills the slot that is not published and publishes it whole (<c>TextDoc.NextList</c> /
    /// <c>PublishQuads</c>), so the issuer takes the list in one reference read and never walks one under
    /// rewrite. The claim that nothing set any of these was stale.</para></summary>
    internal static bool TextDocsPublished;

    /// <summary>Precondition: the 2D UI's ordering against this thread is settled. <b>MET by CX-188</b>, set
    /// by <c>SeamDraw.Install</c> -- the file that builds the order is the one that may assert it.
    /// <para><b>What was wrong.</b> <c>SeamDraw.OnBoundary</c> returned on <c>Live</c> BEFORE its
    /// <c>finally</c>, so <c>UiHostService.DrawWindowsForDbFrame</c> never ran and chat, the settings panel,
    /// nameplates and combat text would not have been drawn at all -- masked only by the thread refusing to
    /// start (review #11). And <c>FrameRegistry</c>'s <c>PrePresent</c> participants (<c>GlContextService</c>
    /// 10, <c>LightmapBake.Hold</c> 20, <c>FrameTap</c> 30, <c>DebugStringService</c> 60) plus
    /// <c>UiHostService.HandBack</c> and <c>ScreenOwner.Present</c> all run engine-context GL on the game
    /// thread assuming the world frame is already resolved.</para>
    /// <para><b>What settles it.</b> The fence. <c>AwaitDrained</c> runs first in <c>OnBoundary</c>
    /// (<c>PrePresent</c> 9), so every participant after it, the hand-back and the present are inside the
    /// window in which the renderer is parked. The roll-call then runs in BOTH arms of the boundary, and the
    /// invariant both keep is the one that matters: <b>the 2D UI is drawn after the world frame it composites
    /// over.</b> What the knob changes is WHICH frame that is -- N on the single-threaded path, N-1 with the
    /// renderer pipelined, because a pipelined draw is not finished yet. That is the same one frame of latency
    /// the snapshot protocol is built around, and the alternative is no second thread.</para>
    /// <para>The UI stays on the GAME thread, in the ENGINE context, deliberately: it is raw GL against
    /// <c>GLRender</c>'s shadow caches, it reads <c>glGetIntegerv(GL_VIEWPORT)</c> off the engine's drawable
    /// (<c>TextRig.SampleViewport</c>), and <c>HandBack</c> resyncs those caches through engine calls. Moving
    /// it would hand the renderer the engine's context, which is the whole point of not doing that.</para>
    /// <para>Input the UI reads -- chat typing, panel state -- is therefore read on the thread that writes it,
    /// which is why that half needs no fence of its own.</para></summary>
    internal static bool UiOrderSettled;

    /// <summary>Precondition: the frame's INPUTS are this thread's, and nothing under the draw reads or
    /// writes ENGINE memory. <b>UNMET</b> -- CX-188 closed the named half and audited the rest, and the rest
    /// is bigger than this summary used to claim.
    /// <para><c>FrameInputs</c> itself needs no publishing in this mode, and that part of the old reading was
    /// right: the mode-0 writers (<c>FrameRead.Tick</c>, <c>SceneInputs.FrameStart</c>) hang off
    /// <c>RspShim</c>'s anchor and <c>RenderSinglePass</c> does not run in DB mode, so every DB-mode writer is
    /// inside the draw and the whole structure moves to whichever thread draws.
    /// <c>FrameInputs.Publish</c>/<c>Unpublish</c> have no caller because in this mode they have no job.</para>
    /// <para><b>CLOSED by CX-188</b>, all into <see cref="FrameHoist"/> -- read once on the game thread inside
    /// <c>RenderSnapshot.Publish</c> and handed across by value: <c>SceneInputs.LatchAtten</c> (three
    /// <c>GetGlobalShaderParamf</c> calls), <c>SceneInputs.CurrentArea</c> (<c>**g_pAppManager</c> -&gt;
    /// <c>GetModule</c> -&gt; <c>module+0xf8</c>, from four draw sites per view), the <c>CNWCArea</c> flags /
    /// extent / <c>isNight</c> / day-night palette, the engine scene shell's fog words,
    /// <c>LightmapBake.BeginWalk</c>'s area deref and placeable-light list walk, <c>LightmapBake.Record</c>'s
    /// dead area parameter, and <c>SceneFeed.RuntimeWind</c> -- which WROTE the engine's
    /// <c>ShaderExposedRuntimeData</c> block and now runs at the fold.</para>
    /// <para><b>STILL OPEN, in the order a reviewer should take them.</b> Writes and engine-list walks first,
    /// because those are correctness and not staleness:</para>
    /// <list type="number">
    /// <item><c>CameraService.SettleCollision</c>, reached every frame from <c>RouteParts</c> -&gt;
    /// <c>AlloyRenderer.Occlude</c>'s <c>OccludeFrame</c>: a read-modify-WRITE of <c>CNWCModule</c>'s camera
    /// flags. The worst single site in the chain -- a non-atomic RMW of game-owned engine state.</item>
    /// <item><c>DbKeyhole.Runtime</c>: writes five fields of the engine's <c>ShaderExposedRuntimeData</c>
    /// (player position, camera position, focus, distance, pitch) per world view.</item>
    /// <item><c>Particles</c>: reads AND writes the engine's own GL bind-state cache globals around its
    /// draw, which the game thread's 2D UI reads in the same window.</item>
    /// <item><c>GrassField</c>: walks the engine's <c>g_GrassInfo</c> list and calls <c>AurTextureGet</c> +
    /// <c>AurTextureGetTID</c> per live material EVERY frame -- an engine resman lookup whose create path can
    /// touch GL.</item>
    /// <item><c>AlloyRenderer.Target.ReadClear</c>: two <c>glGetFloatv</c> queries issued in the ENGINE's
    /// context, every frame, by its own comment "the only place the value can be had" -- and then overwritten
    /// from <c>ScreenOwner.ClearRgba</c>, so they may be removable outright.</item>
    /// <item><c>OwnedTextures.Drain</c>'s <c>CAurTexture</c> reads when its queues are non-empty.</item>
    /// </list>
    /// <para>...and then roughly forty small-value reads, which are mechanical once the pattern exists: the
    /// whole <c>ShaderExposedRuntimeData</c> block and the four <c>GLRender::m_globalShaderConstants*</c>
    /// tables (one struct copy and four table copies kill about twenty-six read sites between them, most of
    /// them in <c>UploadFeatureTerms</c>, which runs once per program switch per pass), the scalar engine
    /// globals (<c>DeltaT</c>, <c>ScreenWidth/Height</c>, <c>GrassIsEnabled</c>,
    /// <c>GrassFarRenderDistance</c>, <c>EnableBeams</c>, <c>ProjectionAlpha</c>, <c>g_nMSAA</c>,
    /// <c>EnableFBEffects</c>, <c>FbEffectsMask</c>, <c>ssao_intensity</c>, the three toon-edge globals,
    /// <c>enabletexturecontrollers</c>), and the <c>GetGlobalShaderParami/f</c> answers the post stack asks
    /// for six times a frame.</para>
    /// <para><b>Latent, and worth a line because "dead" is not "safe":</b> <c>CycleTextures.Tick</c>'s engine
    /// virtual call -- which MUTATES engine texture state -- is DEAD, confirmed independently by CX-188's
    /// build stream: <c>CycleTextures.Register</c> has no caller, so <c>Tick</c>, <c>Dropped</c>,
    /// <c>Count</c> and <c>Steps</c> are all inert and this thread cannot reach it. It stays on the list
    /// because re-arming <c>Register</c> would put an engine vcall back inside the draw;
    /// <c>AlloyRenderer.Frame</c>'s probe <c>BspRoot</c> read and <c>Route</c>'s keyhole-camera reads are dead
    /// only because <c>_enrollScene</c> and <c>AlloyCamera.World</c> are zero in DB mode;
    /// <c>AlloyCamera.EngineLatch</c> becomes a live per-frame engine read under <c>view.cameraloop 0</c>; and
    /// the <c>FrameDiff</c> / <c>CreatureParity</c> dumps walk <c>CNWCArea</c>, <c>CNWCCreature</c>,
    /// <c>Gob</c> and <c>Part</c> whenever their knobs are on.</para></summary>
    internal static bool FrameInputsPublished;

    /// <summary>Precondition: framebuffer 0 and the engine's GL state caches have ONE owner in the window the
    /// fence does not cover. <b>UNMET</b>, and the sibling of <see cref="GuiBuffersOwned"/> -- same root fact
    /// (the publish-to-next-fence overlap), different work to fix, so its own flag.
    /// <para>Under GLX both contexts name one drawable, so framebuffer 0 is one surface and the renderer's
    /// resolve lands in the buffer the game thread is about to present. Inside the fence that is exactly what
    /// is wanted. Outside it, the renderer is resolving frame N into framebuffer 0 while the game thread runs
    /// <c>CGuiMan::UpdateAndRender</c> -- and whether the engine issues any GL of its own against framebuffer
    /// 0 there is the one question a code read cannot close. <c>ScreenOwner.Owned</c> swallows the engine's
    /// default-framebuffer CLEARS and takes its present, and in DB mode its scene layer never runs, so the
    /// expected answer is "none" -- but <c>RspShim.StrayDraws</c> exists because that expectation has been
    /// wrong before, and the cursor, the load screen and a movie frame are all engine draws.</para>
    /// <para><b>The way out already exists and is Windows's.</b> <c>PresentTarget</c> routes Alloy's whole
    /// composite into a shared TEXTURE and has the engine context blit it into framebuffer 0 after the
    /// bracket, for the neighbouring reason that the two contexts there do not share a back buffer (#456).
    /// Making <c>Needed</c> true for <c>Live</c> as well gives the render thread a surface no engine draw can
    /// reach, at the documented price of one full-screen blit a frame; the blit then has to move out of
    /// <c>GuiPass.Draw</c> (which is the RENDER thread's) and into the game thread's half of the boundary,
    /// beside the roll-call. Not done here because it would turn a Linux path that has never run on
    /// (<c>Needed</c> is <c>OperatingSystem.IsWindows()</c> today, deliberately) and the knob cannot be
    /// exercised in this session.</para></summary>
    internal static bool ScreenHandoffSettled;

    /// <summary>Every unmet precondition as one refusal line, or null when all are met.</summary>
    private static string? Unmet()
    {
        string s = "";
        if (!GuiBuffersOwned)
            s += "GuiCapture's one static bank is written by both threads in the same frame -- NuiFlush "
               + "captures nuklear at nk_sdl_render on the GAME thread while DbGui/TextQuads.Issue append the "
               + "panel layers and GuiCapture.Lead + GuiPass.FlushNow rotate and RESET the bank on the render "
               + "thread. The fix is a NUI bank ping-ponged off the snapshot's frame; ";
        if (!TextDocsPublished)
            s += "TextObjects/TextQuads document state is rebuilt on the game thread while the draw walks it "
               + "through unchecked pointer arithmetic; ";
        if (!UiOrderSettled)
            s += "UiHost's 2D draw runs in the ENGINE context at the present, FrameRegistry's "
               + "Start/PrePresent/PostPresent contract assumes the world frame is already resolved, and "
               + "SeamDraw draws the CX windows only on the game thread's path; ";
        if (!FrameInputsPublished)
            s += "the draw still reads and WRITES engine memory: CameraService.SettleCollision "
               + "read-modify-writes CNWCModule's camera flags from RouteParts, DbKeyhole.Runtime writes five "
               + "ShaderExposedRuntimeData fields, Particles saves and restores the engine's GL bind-state "
               + "cache, GrassField walks g_GrassInfo and calls AurTextureGet per material per frame, "
               + "FrameSnapshot reads CurrentScene plus a CAurTexture, and AlloyRenderer.Target issues two "
               + "glGetFloatv queries in the ENGINE's context -- plus about forty small-value reads of the "
               + "runtime block and the global shader-constant tables. CX-188 hoisted the area walk, the "
               + "attenuation latch, the fog words and the wind write; the list above is what is left; ";
        if (!ScreenHandoffSettled)
            s += "framebuffer 0 has two writers outside the fence -- the renderer resolves frame N into it "
               + "while the game thread runs CGuiMan::UpdateAndRender. The fix is PresentTarget: composite "
               + "into the shared texture and blit it from the engine context in the game thread's half of "
               + "the boundary; ";
        return s.Length == 0 ? null : s.TrimEnd(' ', ';');
    }

    /// <summary>Off-device: the thread refuses to start while any precondition is unset, and the refusal names
    /// EVERY unmet one. A single flag would let whoever met one arm the others' races.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "RenderThread.SelfCheck");
        bool g = GuiBuffersOwned, t = TextDocsPublished, u = UiOrderSettled, f = FrameInputsPublished,
             sc = ScreenHandoffSettled;
        GuiBuffersOwned = TextDocsPublished = UiOrderSettled = FrameInputsPublished = ScreenHandoffSettled = false;
        string? all = Unmet();
        check.Expect(all != null, "with nothing met, the thread refuses");
        check.Expect(all != null && all.Contains("GuiCapture") && all.Contains("TextObjects")
                     && all.Contains("UiHost") && all.Contains("CameraService") && all.Contains("framebuffer 0"),
                     "the refusal names EVERY unmet precondition, not the first");
        // One at a time, each way round: a refusal that drops a precondition is how a race gets armed by
        // whoever met theirs first, and a flag wired to the wrong clause is the same bug wearing a name.
        string[] marker = ["GuiCapture", "TextObjects", "UiHost", "CameraService", "framebuffer 0"];
        for (int k = 0; k < marker.Length; k++)
        {
            GuiBuffersOwned = k != 0; TextDocsPublished = k != 1; UiOrderSettled = k != 2;
            FrameInputsPublished = k != 3; ScreenHandoffSettled = k != 4;
            string? one = Unmet();
            check.Expect(one != null && one.Contains(marker[k]),
                         $"four met still refuses, naming the fifth ({marker[k]})");
            for (int j = 0; j < marker.Length; j++)
                if (j != k)
                    check.Expect(one != null && !one.Contains(marker[j]),
                                 $"...and names ONLY it, not {marker[j]}");
        }
        GuiBuffersOwned = TextDocsPublished = UiOrderSettled = FrameInputsPublished = ScreenHandoffSettled = true;
        check.Expect(Unmet() == null, "all of them met is the only state that does not refuse");
        GuiBuffersOwned = g; TextDocsPublished = t; UiOrderSettled = u; FrameInputsPublished = f;
        ScreenHandoffSettled = sc;
        log.Info(check.Ok ? "RenderThread.SelfCheck: passed"
                          : "RenderThread.SelfCheck: FAILED -- the render thread's preconditions do not gate "
                            + "the start, so alloy.renderthread=1 could arm a race the refusal is meant to name");
        return check.Ok;
    }

    /// <summary>Is the render thread the one drawing? False = the single-threaded snapshot path, which is
    /// either the knob's choice or a completed stand-down. A stand-down IN FLIGHT still reads true: the game
    /// thread must not take the context back until the render thread has let go of it.</summary>
    internal static bool Live => _started && !_stoodDown;

    private static volatile bool _started;

    /// <summary>Called by the frame boundary once per frame: once a stand-down has been raised, block here
    /// until the render thread has dropped the context, then let the single-threaded path resume.
    /// Near-free otherwise -- and latched, not conditioned on the stand-down still being IN FLIGHT: a thread
    /// that finished its stand-down before this frame's boundary used to skip the settle altogether, which
    /// left <c>RenderSnapshot</c> stopping for the rest of the session (review #9).</summary>
    internal static void SettleIfStandingDown()
    {
        if (!_downRequested || _settled) return;
        _settled = true;
        AwaitDown();
    }

    private static volatile bool _settled;

    /// <summary>Why the thread is not running, for the period line and <c>/alloy</c>.</summary>
    internal static string Reason => _stoodDown ? _reason : _run ? "" : "alloy.renderthread=0";

    /// <summary>Try to start, once, at a DB frame that already has a 4.6 context. Called every frame from the
    /// boundary and near-free after the first: the context is created inside
    /// <c>GlContextService.OnFrame</c>, which runs AFTER this service's Install, so a start at boot would
    /// find it unavailable and stand the thread down for the session (that was the bug).</summary>
    internal static void StartWhenReady(ExtenderLog? log)
    {
        if (_tried || _stoodDown) return;
        if (!CX.Alloy.Renderer.RenderThread.Boot) { _tried = true; return; }
        if (!GlContextService.Available) return;   // not yet; ask again next frame
        _tried = true;
        Start(log ?? _log!);
    }

    private static bool _tried;

    /// <summary>Start the thread, if the knob asked for it and everything it needs is in hand. Game thread,
    /// with the 4.6 context NOT current.</summary>
    private static void Start(ExtenderLog log)
    {
        _log = log;
        if (!CX.Alloy.Renderer.RenderThread.Boot) return;
        if (!Renderer.On)
        {
            // Not a stand-down: mode 0 is a different renderer. Taking the 4.6 context to a second thread
            // there would break GlContextService's per-frame bracket, whose MakeCurrent would then be
            // refused on every frame -- a render thread without the scene database has nothing to draw from
            // and everything to break.
            log.Info("RenderThread: alloy.renderthread=1 but alloy.scenedb=0 -- the render thread is the "
                   + "scene database's frame and does not run over the engine-scene path. Not started.");
            return;
        }
        if (!GlContextService.Available)
        {
            DownBeforeStart("the GL 4.6 context is not available (alloy.gl46=0, or it failed) -- there is no context to "
               + "hold on a second thread");
            return;
        }
        if (DrawFrame == null)
        {
            DownBeforeStart("no frame entry was registered (seam-feed did not set RenderThread.DrawFrame)");
            return;
        }
        // THE preconditions -- see GuiBuffersOwned. None is checkable from here, so each is asserted as a
        // refusal rather than discovered as corruption, and the refusal names every unmet one at once.
        string? unmet = Unmet();
        if (unmet != null)
        {
            DownBeforeStart(unmet + ". Not a capability problem: this is game-thread state the draw still "
                          + "reads, and it has to stop being that first");
            return;
        }
        if (GlContextService.Inside)
        {
            DownBeforeStart("the 4.6 bracket was OPEN at boot -- the context cannot be taken to another thread while the "
               + "game thread holds it");
            return;
        }
        // Claim the context HERE, on the game thread, before the render thread exists: otherwise the game
        // thread's own bracket can open in the gap and the render thread stands down over a race it lost.
        if (!GlContextService.ReserveForOtherThread())
        {
            DownBeforeStart("the 4.6 context could not be reserved -- the game thread's bracket was open");
            return;
        }
        _run = true;
        _started = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "CX.Alloy.Render" };
        _thread.Start();
        log.Info("RenderThread: starting -- Alloy's 4.6 context moves to its own thread and the per-frame "
               + "Enter/Leave bracket stops running on the game thread (alloy.renderthread=1)");
    }

    /// <summary>Ask the thread to stand down. Loud, once, with the cause named -- but this only RAISES the
    /// request: <see cref="Live"/> stays true, and the game thread keeps handing frames across, until the
    /// render thread has actually dropped the context and said so (<see cref="_released"/>). Anything less is
    /// the race the review named: clearing <c>Live</c> first lets the game thread <c>MakeCurrent</c> a context
    /// the render thread still holds, which is a GLX <c>BadAccess</c> and Xlib's default handler exits the
    /// process.</summary>
    private static void RequestDown(string why)
    {
        if (_downRequested) return;
        _downRequested = true;
        _reason = why;
        _run = false;
        _log?.Always($"RenderThread: standing down -- {why}. Alloy will draw on the game thread from the same "
                   + "snapshot; the frame is correct and costs engine + Alloy instead of max(engine, Alloy).");
        // Both waits the thread can be in, or the stand-down never completes and SettleIfStandingDown blocks
        // the game thread for ever: the pause gate first (RenderSnapshot.Stop does not wake a PARKED thread),
        // then Take's.
        Unpause();
        RenderSnapshot.Stop();   // wake the thread out of Take()
    }

    /// <summary>The render thread has released the context and exited; the game thread may own it again. Only
    /// now does <see cref="Live"/> go false.</summary>
    private static void CompleteDown()
    {
        _stoodDown = true;
        lock (_downGate) { _released = true; Monitor.PulseAll(_downGate); }
    }

    /// <summary>Stand down from the GAME thread (a start that never got off the ground): there is no thread
    /// holding anything, so the two halves collapse.</summary>
    private static void DownBeforeStart(string why)
    {
        RequestDown(why);
        CompleteDown();
        RenderSnapshot.Resume();
    }

    /// <summary>Wait for the render thread to drop the context, then let the single-threaded path resume.
    /// Called on the GAME thread from the boundary when a stand-down is in flight.</summary>
    private static void AwaitDown()
    {
        lock (_downGate)
            while (!_released) Monitor.Wait(_downGate, 1000);
        RenderSnapshot.Resume();
    }

    private static readonly object _downGate = new();
    private static volatile bool _downRequested, _released;

    private static void Loop()
    {
        // FIRST, before any bracket this thread can open: its zones go to their OWN row. Added to the game
        // thread's they would make the period line's residual (frame time minus every top-level zone) read
        // negative on every frame this thread drew, which is the instrument failing exactly where the knob is
        // judged.
        FrameZones.UseRow(FrameZones.RowRender);
        if (!GlContextService.MakeCurrentHere())
        {
            GlContextService.CancelReservation();
            RequestDown("MakeCurrent(4.6 context) on the render thread was refused -- the driver will not hand "
                      + "the context to a second thread on this drawable");
            CompleteDown();
            return;
        }
        _log?.Info("RenderThread: LIVE -- the 4.6 context is current on this thread for the rest of the session");
        try
        {
            while (_run)
            {
                // BEFORE Take, not after. RenderSnapshot.Take does not clear _published -- only Release does
                // -- so a thread that parked between them held the frame, and the game thread's next
                // AwaitDrained then waited on a renderer that was itself waiting for an Unpause the blocked
                // thread was the only one that could reach. That is a frozen client, which leaves no core and
                // is the hardest failure in this codebase to diagnose (nativeaot-hang-forensics). Parked here
                // the thread owns nothing, and the only way it can acquire a frame is a publish -- which only
                // the paused game thread performs.
                WaitIfPaused();
                RenderSnapshot.Frame? f = RenderSnapshot.Take();
                if (f == null) break;
                try
                {
                    // The work that needs A context but not the pass chain -- #211's texture retires, the
                    // texture registry's drain, the arena pump, the palette and glyph-atlas builds -- used to
                    // ride the game thread's health bracket. That bracket never opens now, so it rides here,
                    // on the thread that holds the context, once per frame and before the draw.
                    GlContextService.RunInBracketHere();
                    DrawFrame!(f);
                    _frames++;
                }
                catch (Exception e)
                {
                    _faults++;
                    // A fault in the renderer's own bracket is this thread's whole contract failing: it may
                    // have left GL state, and the game thread cannot fix it from another context.
                    RequestDown($"the frame draw threw -- {e.GetType().Name}: {e.Message}");
                }
                finally { RenderSnapshot.Release(); }
            }
        }
        finally
        {
            GlContextService.ReleaseCurrentHere();
            CompleteDown();
        }
    }

    /// <summary>Park here while paused -- between frames, owning nothing. <c>_run</c> is in the condition so
    /// a stand-down or a shutdown gets the thread out even if something re-raises the pause: the loop's own
    /// <c>while (_run)</c> is the next test either way.</summary>
    private static void WaitIfPaused()
    {
        if (!_paused) return;
        lock (_pauseGate) while (_paused && _run) Monitor.Wait(_pauseGate);
    }

    /// <summary>Ask the render thread to hold outside its bracket while the game thread does something that
    /// needs the drawable to itself: a movie frame handed back to the engine's quad, a snapshot read-back, the
    /// engine's shader compiles at an area load.
    /// <para><b>What it guarantees when it returns:</b> the renderer holds no frame, is not inside its
    /// bracket, and cannot acquire a frame until the caller publishes one -- because the caller IS the only
    /// publisher. That is the mutual exclusion items 4-7 need, and it is enough for a movie frame or a
    /// snapshot read-back.</para>
    /// <para><b>It gets there by DRAINING first</b>, on the caller's thread, and the order is the whole
    /// correctness argument. Latching the pause first would park a renderer that still held the published
    /// frame, and the game thread's next <c>AwaitDrained</c> would then wait on a thread waiting for an
    /// <c>Unpause</c> only that blocked thread could reach -- a deadlock with no core. Draining first cannot
    /// race: no frame can be published between the drain and the latch, because the publisher is here.</para>
    /// <para>A frame already inside its bracket runs to completion -- that is what the drain waits for.
    /// <b>Nothing calls this pair yet</b>: each of items 4-7 gets its call when the live harness shows it
    /// needs one, and saying so is better than a pause nobody can point at.</para></summary>
    internal static void Pause()
    {
        if (!Live) return;
        RenderSnapshot.AwaitDrained();
        lock (_pauseGate) _paused = true;
    }

    /// <summary>Let the render thread go again.</summary>
    internal static void Unpause()
    {
        lock (_pauseGate)
        {
            _paused = false;
            Monitor.PulseAll(_pauseGate);
        }
    }

    /// <summary>Session end (context loss, or the renderer's teardown): wake the thread out of its wait, let
    /// it drop the context, and WAIT for it -- the game thread may own the context again the instant this
    /// returns, so returning early is the same BadAccess as a racy stand-down.</summary>
    internal static void Shutdown()
    {
        if (!_started) return;
        _run = false;
        Unpause();
        RenderSnapshot.Stop();
        _thread?.Join(2000);
        _thread = null;
        _stoodDown = true;
        RenderSnapshot.Resume();
    }

    internal static string Stats(int _)
        => Live ? $"renderthread: LIVE, {_frames} frames drawn off-thread"
               + (GlContextService.HeldHereDepth != 0 ? $", {GlContextService.HeldHereDepth} UNBALANCED 4.6 brackets" : "")
         : _stoodDown ? $"renderthread: STOOD DOWN ({_reason}), {_faults} faults"
         // The preconditions' LIVE state, and this is the only place it can honestly be printed: each is set
         // by whoever makes it true, during that file's Install, which runs after the self-check does.
         : $"renderthread: off ({Reason}) -- met: gui={GuiBuffersOwned} text={TextDocsPublished} "
         + $"uiorder={UiOrderSettled} inputs={FrameInputsPublished} screen={ScreenHandoffSettled}";
}
