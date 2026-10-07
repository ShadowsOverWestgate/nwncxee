using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>CX-120 (seam-scene): the frame boundary in DB mode, and the two threads' halves of it.
///
/// <para><b>The boundary.</b> Today the frame ends at <c>GLRender::SwapBuffers</c>, which
/// <c>UiHostService.OnSwap</c> owns and <c>ScreenOwner.Present</c> presents from. In DB mode the engine's
/// scene layer never ran, so the Alloy frame has no <c>RenderSinglePass</c> to hang off -- the boundary IS the
/// frame. This registers as a <c>PrePresent</c> participant one priority ahead of
/// <see cref="GlContextService"/>'s 4.6 bracket (which is deliberately last), so the order per frame is:
/// latch the window state, fold the dirty sets, publish, draw, then the UI and the present.</para>
///
/// <para><b>The two paths are ONE path.</b> Fold, publish, draw -- in that order, in both modes; the only
/// difference is which thread runs the draw. <see cref="DbFeed.Present"/> is deliberately NOT called: it
/// folds AND draws, and the fold has to happen inside <see cref="RenderSnapshot.Publish"/> (ahead of
/// <c>TakeDirty</c>) or it sees an empty dirty list. So the seam registers
/// <see cref="DbFeed.Draw"/> as the contract asks, drives it off the snapshot's view list, and hands the feed
/// its fold through <see cref="RenderSnapshot.Fold"/>. <c>DbFeed.Present</c> still works for any other
/// caller; nothing here calls it.</para>
///
/// <para><b>Who draws what.</b> A GUI panel view draws itself out of the database -- seam-feed's
/// <see cref="DbGui"/> walks the scene's controls in creation order and issues their quads, which is exactly
/// what the engine's <c>Scene::RenderOnlyGobs</c> did -- so the main menu and every panel are complete here.
/// A WORLD view needs Alloy's pass chain, and <see cref="Pass"/> is where the renderer registers it; nothing
/// sets it yet, because that chain is still driven by the engine-scene path's <c>RenderSinglePass</c> hook.
/// Until it is set, a world frame folds and publishes correctly and draws nothing, and the absence is ONE
/// loud line rather than a black screen with no explanation.</para></summary>
internal static class SeamDraw
{
    private static ExtenderLog? _log;
    private static bool _saidNoPass, _registered;
    private static long _frames, _views;
    private static Action<DbId, DbId>? _perView;
    private static bool _saidReasserted, _saidOrder;

    /// <summary>The renderer's per-view draw in DB mode: run Alloy's pass chain for one view of one scene.
    /// Set by the renderer when DB mode arms.</summary>
    internal static Action<DbId, DbId>? Pass;

    /// <summary>Register the boundary. Called from the seam's boot, after the knobs are declared.</summary>
    internal static void Install(ExtenderLog log)
    {
        _log = log;
        if (_registered || !Renderer.On) return;
        _registered = true;
        _perView = PerView;
        DbFeed.Draw = _perView;
        RenderThread.DrawFrame = DrawSnapshot;
        // THE fold, and the one place it happens. RenderSnapshot.Publish invokes this before it takes the
        // dirty lists, which is the only order that works: a fold after TakeDirty sees an empty list and
        // writes nothing, so the frame would publish and draw records nobody rebuilt.
        // The trigger highlight's snapshot too: it reads the engine's PartTriggers, which only the game thread may.
        RenderSnapshot.Fold = _ => { DbAnim.Settle(); DbFeed.FoldFrame(); TriggerHighlight.Enroll(); };
        FrameRegistry.Global.Register(FramePhase.PrePresent, 9, OnBoundary, "SeamDraw.frame", log);
        log.Info("SeamDraw: DB-mode frame boundary armed -- the CX scene database's frame runs at the present, "
               + "one priority ahead of the 4.6 bracket");
    }

    /// <summary>The frame, on the game thread: fold, publish, and then either draw it here or leave it to the
    /// render thread. ONE order for both modes -- the only difference is which thread runs
    /// <see cref="DrawSnapshot"/> -- so the self-check exercises the live protocol and not a test-only one.
    /// Hot; allocates nothing.</summary>
    private static void OnBoundary()
    {
        if (!Renderer.On) return;
        long t = FrameZones.Now, top0 = FrameZones.TopLevelTicks();
        try { Boundary(); }
        finally { FrameZones.Add(FrameZones.Zone.Seam, t, FrameZones.TopLevelTicks() - top0); }
    }

    private static void Boundary()
    {
        _frames++;
        // Re-assert the draw delegate. DbFeed's own self-check sets `Draw = null` on purpose and does not put
        // it back (DbFeed.SelfCheck.cs:56), so whether a real session has a draw at all depends on whether
        // that fixture happens to run before this Install or after it. Today it runs before; nothing enforces
        // that. One reference compare a frame removes the ordering dependency, and the restore is LOUD -- a
        // delegate that went missing is a fact about the boot order, not something to paper over quietly.
        if (!ReferenceEquals(DbFeed.Draw, _perView))
        {
            DbFeed.Draw = _perView;
            if (!_saidReasserted)
            {
                _saidReasserted = true;
                _log?.Always("SeamDraw: DbFeed.Draw had been cleared by something else (the feed's own "
                           + "self-check nulls it and does not restore it) -- re-asserted. Nothing was lost "
                           + "this frame, but the boot order is deciding whether the frame draws at all.");
            }
        }
        // The 4.6 context does not exist until GlContextService's first frame, so the render thread cannot be
        // started at Install: it would find Available false and stand down permanently. Started here, at the
        // first DB frame that has a context to take.
        RenderThread.StartWhenReady(_log);
        // A stand-down raised on the render thread is not finished until that thread has dropped the context.
        // Blocking here is the handshake: the game thread must not take the context back early.
        RenderThread.SettleIfStandingDown();
        // The GUI depth latch is per RUN of GUI views, not per view, exactly as the engine's own
        // DAT_019aa3a4 is -- so it resets once a frame, here, and not inside the per-view draw.
        DbGui.NewFrame();
        DbCamera.NewFrame();   // latch every camera's previous position, once, as the engine does
        RenderSnapshot.Frame f = RenderSnapshot.Publish();   // Fold runs inside, ahead of TakeDirty
        DbFeed.CloseViews();
        if (RenderThread.Live) return;                       // the render thread draws it and releases it
        // try/finally, not optimism: Publish waits while a frame is still held, so a throw that skipped the
        // Release would block the GAME thread forever on the next present -- a frozen client, which leaves no
        // core and is the hardest failure in this codebase to diagnose (nativeaot-hang-forensics).
        try { DrawSnapshot(f); }
        finally
        {
            // CX's own 2D UI (chat, the settings panel, nameplates, combat text) goes LAST, after every view
            // the game asked for. In mode 0 it is drawn during the engine's GUI render, before the world
            // frame; in DB mode the whole frame runs here at PrePresent, so drawing it at its usual site put
            // it UNDER the world and it disappeared the moment the world started drawing.
            CX.Ui.UiHostService.DrawWindowsForDbFrame();
            RenderSnapshot.Release();
        }
    }

    /// <summary>Draw one published frame: every view in the order the game asked for it, each with the camera
    /// matrices the snapshot recorded at the time of the request. Render thread (or the game thread on a
    /// stand-down).</summary>
    private static void DrawSnapshot(RenderSnapshot.Frame f)
    {
        // Once: whether the world view leads the list or trails it decides nothing here any more (the GUI
        // flush is at the end either way), but it is the fact every "my panel is behind the world" question
        // starts from, and nothing else prints it.
        if (!_saidOrder && f.ViewCount > 1)
        {
            _saidOrder = true;
            Span<char> order = stackalloc char[RenderSnapshot.MaxViews];
            for (int k = 0; k < f.ViewCount; k++) order[k] = f.Views[k].World ? 'W' : 'g';
            _log?.Info($"SeamDraw: view list order (W = world, g = GUI panel): {new string(order[..f.ViewCount])}");
        }
        int nui = GuiCapture.LayerN;
        bool say = Renderer.HitCheck.Boot && _orderSaid < OrderBudget;
        ulong sig = 14695981039346656037;   // not-an-offset: the FNV-1a 64 offset basis
        for (int k = 0; k < f.ViewCount; k++)
        {
            ref RenderSnapshot.ViewRec v = ref f.Views[k];
            DbGui.LastWhy = 'W'; DbGui.LastIssued = 0;
            PerViewWithState(v.Scene, v.Camera, in v);
            if (!say || k >= RenderSnapshot.MaxViews) continue;
            _why[k] = DbGui.LastWhy; _issued[k] = DbGui.LastIssued;
            sig = (sig ^ v.Scene.Value) * 1099511628211;
            sig = (sig ^ DbGui.LastWhy) * 1099511628211;
        }
        if (say && sig != _orderSig) { _orderSig = sig; SayOrder(f); }
        // CX-120 (gui): issue the frame's captured GUI layers HERE, after every view. GuiPass's own two flush
        // sites are the engine's draw order -- nk_sdl_render inside CGuiMan::UpdateAndRender, and PrePresent
        // priority 5 -- and both run BEFORE this boundary, so a panel captured here was issued at the NEXT
        // frame's nk flush, i.e. before that frame's world pass composited the whole display over it. The
        // panels were drawn every frame and overwritten every frame. Flushing at the end of the view list is
        // also order-proof: whether the world view comes first or last, the GUI lands on top of it.
        if (Renderer.On) { GuiCapture.Lead(nui); GuiPass.FlushNow(); }
    }

    /// <summary>One view, as <see cref="DbFeed.Draw"/>. Reached on the single-threaded path, where the
    /// snapshot's record for this view is the one just published.</summary>
    private static void PerView(DbId scene, DbId camera)
    {
        RenderSnapshot.Frame? f = RenderSnapshot.Drain();
        if (f != null)
            for (int k = 0; k < f.ViewCount; k++)
                if (f.Views[k].Scene == scene && f.Views[k].Camera == camera)
                {
                    PerViewWithState(scene, camera, in f.Views[k]);
                    return;
                }
        // The feed asked for a view the snapshot does not carry, which can only mean the camera's own view
        // did not build (a degenerate rect -- the engine skips such a view too) or the two lists drifted.
        // Its own cause, not Pass's: a refusal that names the wrong reason is worse than none.
        _unmatched++;
        if (!_saidUnmatched)
        {
            _saidUnmatched = true;
            _log?.Always("SeamDraw: the feed asked to draw a view the frame's snapshot does not carry -- "
                       + "that view is NOT DRAWN. Either its camera's rect was degenerate (which the engine "
                       + "skips too) or the feed's view list and the snapshot's have drifted apart.");
        }
    }

    private static long _unmatched;
    private static bool _saidUnmatched;

    /// <summary>Fill the frame inputs this view's passes read, then run the pass chain. The inputs come from
    /// the SNAPSHOT's matrices, not from a fresh read of the camera record: the game thread may already have
    /// moved the camera for frame N+1, and a pass that re-derived its own view would then draw frame N's
    /// geometry through frame N+1's camera.</summary>
    private static void PerViewWithState(DbId scene, DbId camera, in RenderSnapshot.ViewRec v)
    {
        _views++;
        SceneFeed.TickFromView(scene, camera, in v);
        // THE view for this pass: the snapshot's, not AlloyCamera.WorldView (which is measured at an engine
        // hook that does not run in DB mode, and is one camera for the whole frame regardless). Set around
        // the pass and cleared in a finally, so a throw cannot leave a GUI panel's ortho camera latched for
        // the next frame's world.
        AlloyRenderer.DbView = new AlloyView
        {
            View = v.View, Proj = v.Proj, X = v.X, Y = v.Y, W = v.W, H = v.H,
            Aspect = v.Aspect, Near = v.Near, Far = v.Far, Fovy = v.Fovy,
            DepthNear = v.DepthNear, DepthFar = v.DepthFar,
            Perspective = v.Perspective, Valid = true,
        };
        AlloyRenderer.DbViewValid = true;
        try { DrawView(scene, camera, in v); }
        finally { AlloyRenderer.DbViewValid = false; }
    }

    /// <summary>One view's draw, with <see cref="AlloyRenderer.DbView"/> already latched.</summary>
    private static void DrawView(DbId scene, DbId camera, in RenderSnapshot.ViewRec v)
    {
        if (!v.World)
        {
            // A GUI panel view draws itself out of the database (seam-feed's DbGui): its controls are model
            // objects in its own scene and its text is CX-119's glyph runs, so there is no pass chain to run
            // and no world verdict to wait for. The view's own view*proj is what the quads sit on.
            _guiViews++;
            // CX-120 (gui): panel geometry AND text, both on mode 0's GuiPass/GuiCapture path, which already
            // owns the per-rect viewport and the GUI depth convention. Not the owned pass chain: that is not
            // re-entrant per view -- every BeginOwnedFrame clears the HDR target and ends by compositing the
            // WHOLE display -- so running it once per panel made the last panel's empty composite overwrite
            // the world (the black frames in .scratch/live/gui2 and gui3).
            _guiText += DbGui.Draw(scene, in v, (uint)Environment.TickCount);
            return;
        }
        Action<DbId, DbId>? pass = Pass;
        if (pass == null) { Refuse(); return; }
        pass(scene, camera);
    }

    private static long _guiViews, _guiText;

    private const int OrderBudget = 80;
    private static int _orderSaid;
    private static ulong _orderSig;
    private static readonly char[] _why = new char[RenderSnapshot.MaxViews];
    private static readonly int[] _issued = new int[RenderSnapshot.MaxViews];

    /// <summary><c>alloy.hitcheck=1</c>: the frame's views in paint order, each with its outcome
    /// (<see cref="DbGui.LastWhy"/>) and draw count, said whenever that list changes.</summary>
    private static void SayOrder(RenderSnapshot.Frame f)
    {
        _orderSaid++;
        var sb = new System.Text.StringBuilder();
        for (int k = 0; k < f.ViewCount && k < RenderSnapshot.MaxViews; k++)
        {
            ref RenderSnapshot.ViewRec v = ref f.Views[k];
            int s = v.Scene.Index;
            string name = (uint)s < SceneDb.MaxScenes ? SceneDb.SceneName[s] ?? "?" : "?";
            if (name.StartsWith("sceneGUI_", StringComparison.Ordinal)) name = name[9..];
            sb.Append(k == 0 ? "" : " > ").Append(name).Append('#').Append(s).Append('[').Append(_why[k])
              .Append(_issued[k]).Append(' ').Append(v.X).Append(',').Append(v.Y).Append(' ').Append(v.W)
              .Append('x').Append(v.H).Append(']');
        }
        _log?.Always($"[dbviews] frame {f.Index}: {sb}");
    }

    private static void Refuse()
    {
        if (_saidNoPass) return;
        _saidNoPass = true;
        _log?.Always("SeamDraw: no renderer pass is registered for a DB-mode view (SeamDraw.Pass is unset) -- "
                   + "the scene database is being fed and published, and NOTHING IS DRAWN this session. "
                   + "This is the draw side of CX-120, not a fault in the database.");
    }

    internal static string Stats(int _)
        // "gui draws" was a lie that cost an investigation: DbGui.Draw RETURNS its text-run count, not its
        // draw count, so this line read "0 gui draws" while DbGui had walked 366498 controls and refused no
        // layer. Two numbers now, named for what they actually are -- a counter you have to read the callee's
        // source to interpret is worse than no counter.
        => $"seamdraw: {_frames} frames, {_views} views, {_guiViews} gui views ({_guiText} text runs)"
         + (_unmatched > 0 ? $", {_unmatched} views UNMATCHED" : "")
         + (Pass == null ? ", NO WORLD PASS REGISTERED" : "");
}
