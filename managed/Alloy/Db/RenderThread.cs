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
/// <item>CX's own 2D UI. <c>UiHostService.OnSwap</c> -> <c>UiHost.DrawFrame</c> draws every registered window
/// (chat, the settings panel, nameplates, combat text) with raw GL in the ENGINE context, and
/// <c>UiHostService.HandBack</c> resyncs <c>GLRender</c>'s shadow cache afterwards. Per frame.</item>
/// <item>NUI -- <b>checked and NOT on this list.</b> <c>Pass/NuiFlush.cs</c> chains <c>nk_sdl_render</c> but
/// does not call it: it converts nuklear's command list and issues the quads through <c>GuiCapture</c>, in
/// ALLOY's context ("drawn by Alloy; nk_sdl_render is not called"). Only the nuklear TEXTURES are
/// engine-created names, and those are reachable through the share list.</item>
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
/// context. The thread's own per-view draw has a second set on Alloy's side, and they are the four
/// preconditions (<see cref="GuiBuffersOwned"/>, <see cref="TextDocsPublished"/>, <see cref="UiOrderSettled"/>,
/// <see cref="FrameInputsPublished"/>): shared <c>GuiCapture</c> buffers, text documents rebuilt under their
/// reader, a 2D UI that draws in the ENGINE context at the present, and frame inputs that are still the game
/// thread's live globals. Each gates the start and nothing sets any of them, so the knob refuses -- loudly,
/// naming every unmet one -- instead of producing a corruption nobody would trace back to a flag. Measuring
/// <c>renderthread 0 vs 1</c> needs them fixed first.</para>
/// <para><b>What the pose columns do NOT need.</b> The obvious reading -- copy <c>SceneDb</c>'s pose columns
/// into the frame -- is an expensive answer to the wrong question. <see cref="RenderSnapshot.Fold"/> already
/// runs on the game thread inside <see cref="RenderSnapshot.Publish"/>, under the lock that IS the frame's
/// happens-before edge, and its job is already to turn columns into the renderer's own persistent records. The
/// columns are read off-thread only because <c>StageDbPart</c> re-reads them at DRAW time for the per-frame
/// half of the record (model, alpha, dissolve, the lighting folds). Moving those reads into the fold costs a
/// change-driven write per dirty part instead of a copy of every column every frame -- and it is the same
/// change that makes the stage loop stop touching the database at all.</para>
/// <para>Items 1-3 are the ordering problem, and they are why this thread does NOT present: the game thread
/// still does, from the engine context, after this thread has resolved the frame into framebuffer 0 and
/// signalled it. Items 4-7 are on-demand or per-session, and <see cref="Pause"/>/<see cref="Unpause"/> is the
/// hook that parks this thread around one of them. <b>Nothing calls that pair yet</b> -- each entry gets its
/// call when the live harness shows it needs one, and saying so is better than a pause nobody can point at.</para>
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

    /// <summary><b>Precondition: </b><c>GuiCapture</c>'s buffers are no longer shared with the game thread.
    /// <para>Each precondition is a FACT about what the per-view draw touches, not a capability, so none can
    /// be checked from here -- each is set by whoever makes it true, and the thread refuses to start while any
    /// is unset, naming EVERY unmet one (<see cref="Unmet"/>). They are separate bools rather than one
    /// <c>GuiOffThread</c> flag because they are met by different work at different times, and one flag means
    /// whoever finishes first arms the other two's races. Setting one that is not actually true is the one way
    /// to turn this knob into a corruption.</para>
    /// <para>Nothing sets any of the four yet, and that is the honest state of the feature.</para></summary>
    internal static bool GuiBuffersOwned;

    /// <summary>Precondition: <c>TextObjects</c>/<c>TextQuads</c> document state is published to the frame
    /// rather than rebuilt under its reader. <c>TextQuads.Build</c> is game-thread-on-change and
    /// <c>TextQuads.Issue</c> runs in the per-view draw, walking <c>d.Quads</c> through UNCHECKED pointer
    /// arithmetic (<c>q + run.First * VertFloats</c>) against a <c>d.Runs</c>/<c>d.RunCount</c> the builder
    /// rewrites -- so a chat line arriving mid-draw is an out-of-bounds read, not a torn glyph.</summary>
    internal static bool TextDocsPublished;

    /// <summary>Precondition: the 2D UI's ordering against this thread is settled.
    /// <c>UiHostService.OnSwap</c> -> <c>UiHost.DrawFrame</c> draws every registered window with raw GL in the
    /// ENGINE context at the present, and <c>FrameRegistry.Run(Start/PrePresent/PostPresent)</c> is a
    /// game-thread contract whose PrePresent assumes the world frame is already resolved. With the draw on
    /// this thread it is not.
    /// <para>Concretely, and the reason this one cannot be waved through: in DB mode
    /// <c>UiHostService.OnNkRender</c> skips its own roll-call and <c>SeamDraw.OnBoundary</c> draws the CX
    /// windows instead, in the <c>finally</c> of the GAME thread's draw -- a branch the render-thread path
    /// returns before, and one <c>SeamDraw.DrawSnapshot</c> does not call. So with this thread live, chat, the
    /// settings panel, nameplates and combat text would not be drawn at all; it is masked only by the thread
    /// refusing to start (review #11).</para></summary>
    internal static bool UiOrderSettled;

    /// <summary>Precondition: the frame's INPUTS are this thread's and not a global the game thread writes.
    /// <para>Traced for DB mode, and the answer is not the one the name suggests. <c>FrameInputs</c> does not
    /// need publishing here, because in DB mode nothing on the game thread writes it: the mode-0 writers
    /// (<c>FrameRead.Tick</c>, <c>SceneInputs.FrameStart</c>) hang off <c>RspShim</c>'s anchor, and
    /// <c>RenderSinglePass</c> does not run in DB mode. Every DB-mode writer -- <c>SceneFeed.TickFromView</c>,
    /// <c>DbLights.Walk</c>, the cube bind -- is inside the per-view draw, so the whole structure moves to
    /// whichever thread draws and stays private to it. <c>FrameInputs.Publish</c>/<c>Unpublish</c> have no
    /// caller because in this mode they have no job; the remaining asymmetry (a scalar setter writes
    /// <c>_cur</c> while an element write through an array getter lands in the reader's array) only bites once
    /// something publishes, which nothing does.</para>
    /// <para><b>What the trace found instead, and why this stays unset.</b> The inputs step does not merely
    /// read CX state -- it reads ENGINE memory. <c>AlloyRenderer.DbPass</c> calls
    /// <c>DbLights.Walk(scene, SceneInputs.LatchAtten(), SceneInputs.CurrentArea())</c> inside the draw, which
    /// walks the engine's area and its light list. On a render thread that is a read of engine objects while
    /// the game thread simulates them -- a different and larger problem than a CX global, and not one a
    /// publish of <c>FrameInputs</c> addresses. The inputs walk has to happen on the game thread and hand its
    /// RESULT across (which is what publishing would then be for), or this flag is a lie.</para></summary>
    internal static bool FrameInputsPublished;

    /// <summary>Every unmet precondition as one refusal line, or null when all are met.</summary>
    private static string? Unmet()
    {
        string s = "";
        if (!GuiBuffersOwned)
            s += "the per-view draw still shares GuiCapture's buffers (Layers/Draws/Records/Vtx/Idx and their "
               + "cursors) with the game thread's producers -- UiHost's 2D windows via NuiFlush, TextQuads.Issue, "
               + "and DbGui's BeginDbLayer/EndDbLayer; ";
        if (!TextDocsPublished)
            s += "TextObjects/TextQuads document state is rebuilt on the game thread while the draw walks it "
               + "through unchecked pointer arithmetic; ";
        if (!UiOrderSettled)
            s += "UiHost's 2D draw runs in the ENGINE context at the present, FrameRegistry's "
               + "Start/PrePresent/PostPresent contract assumes the world frame is already resolved, and "
               + "SeamDraw draws the CX windows only on the game thread's path; ";
        if (!FrameInputsPublished)
            s += "the inputs step runs inside the DRAW and reads ENGINE memory there -- DbPass calls "
               + "DbLights.Walk(scene, SceneInputs.LatchAtten(), SceneInputs.CurrentArea()), walking the "
               + "engine's area and light list while the game thread simulates them. FrameInputs itself needs "
               + "no publish in this mode (every DB-mode writer is in the draw); the walk behind it does; ";
        return s.Length == 0 ? null : s.TrimEnd(' ', ';');
    }

    /// <summary>Off-device: the thread refuses to start while any precondition is unset, and the refusal names
    /// EVERY unmet one. A single flag would let whoever met one arm the others' races.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "RenderThread.SelfCheck");
        bool g = GuiBuffersOwned, t = TextDocsPublished, u = UiOrderSettled, f = FrameInputsPublished;
        GuiBuffersOwned = TextDocsPublished = UiOrderSettled = FrameInputsPublished = false;
        string? all = Unmet();
        check.Expect(all != null, "with nothing met, the thread refuses");
        check.Expect(all != null && all.Contains("GuiCapture") && all.Contains("TextObjects")
                     && all.Contains("UiHost") && all.Contains("FrameInputs"),
                     "the refusal names all four unmet preconditions, not the first");
        GuiBuffersOwned = true; TextDocsPublished = true; FrameInputsPublished = true;
        string? one = Unmet();
        check.Expect(one != null && !one.Contains("GuiCapture") && one.Contains("UiHost"),
                     "three met still refuses, naming only the fourth");
        GuiBuffersOwned = TextDocsPublished = UiOrderSettled = FrameInputsPublished = true;
        check.Expect(Unmet() == null, "all four met is the only state that does not refuse");
        GuiBuffersOwned = g; TextDocsPublished = t; UiOrderSettled = u; FrameInputsPublished = f;
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
                RenderSnapshot.Frame? f = RenderSnapshot.Take();
                if (f == null) break;
                WaitIfPaused();
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

    private static void WaitIfPaused()
    {
        if (!_paused) return;
        lock (_pauseGate) while (_paused) Monitor.Wait(_pauseGate);
    }

    /// <summary>Ask the render thread to hold outside its bracket while the game thread does something that
    /// needs the drawable to itself: a movie frame handed back to the engine's quad, a snapshot read-back, the
    /// engine's shader compiles at an area load.
    /// <para><b>It RAISES the request and returns -- it does not wait for the renderer to park</b>, and
    /// <see cref="WaitIfPaused"/> is polled once per frame, between frames, so the thread parks only at the
    /// next boundary and a frame already inside its bracket runs to completion. So this pair is not yet the
    /// mutual exclusion items 4-7 above need: whoever calls it first owes the handshake (the request, then a
    /// wait on an acknowledgement the loop sets after <c>WaitIfPaused</c>), and <b>nothing calls it yet</b>,
    /// which is why the gap is named here rather than built blind (review #10).</para></summary>
    internal static void Pause()
    {
        if (!Live) return;
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
         : _stoodDown ? $"renderthread: STOOD DOWN ({_reason}), {_faults} faults"
         : "renderthread: off (alloy.renderthread=0)";
}
