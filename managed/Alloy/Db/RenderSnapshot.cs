using System;
using System.Numerics;
using System.Threading;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>CX-120 / ADR 0028 section 5: ONE frame as it crosses from the game thread to the renderer.
/// <para>The game thread owns the database. At the present boundary it PUBLISHES a snapshot: the frame's view
/// list in the order the game issued it, and the dirty sets folded into the renderer's own copies. The render
/// thread then draws that snapshot while the game thread simulates the next frame.</para>
/// <para><b>What crosses and what does not.</b> A snapshot carries the VIEWS by value -- camera matrices,
/// viewport, depth range, the scene handle -- because they are small, per-frame and the renderer needs them
/// before it touches anything else. It does NOT copy the record tables: the renderer reads
/// <see cref="SceneDb"/>'s columns directly, and the contract that makes that safe is the dirty FOLD: a
/// column the renderer reads for frame N is only written again by the game thread for frame N+1 after the
/// publish, and the publish waits until the renderer has released N-1. So at most one frame of column writes
/// is in flight, and the renderer-side copies the fold builds (<c>_drawBuf</c> and friends, seam-feed's) are
/// what a pass actually reads mid-draw.</para>
/// <para><b>Allocation.</b> Two snapshots, both grown once to their high-water mark and then reused forever:
/// a publish copies into the spare one's arrays and swaps. Nothing here allocates per frame, which is the
/// NativeAOT rule for a hook -- the GC runs on whatever thread allocates, and a collection on the game thread
/// at the publish is a stutter.</para></summary>
public static class RenderSnapshot
{
    /// <summary>Views one frame may carry -- <see cref="DbFeed.MaxViews"/> exactly, spelled once so the
    /// recording side and the crossing side cannot disagree about which views exist. A main-menu frame issues
    /// ~6 panel views; an in-game frame is the world view plus the open panels plus the two post-NUI overlay
    /// views. Over the cap the view is DROPPED with a loud line -- a silently missing panel is exactly the
    /// hole no-silent-fallbacks forbids.</summary>
    public const int MaxViews = DbFeed.MaxViews;

    /// <summary>One recorded view: everything the renderer needs to draw one <c>RenderScene</c>.</summary>
    public struct ViewRec
    {
        public DbId Scene, Camera;
        public Matrix4x4 View, Proj;
        public int X, Y, W, H;
        public float Near, Far, Fovy, Aspect, DepthNear, DepthFar;
        public Vector3 CamPos;
        public Quaternion CamRot;
        /// <summary>A world view (the scene has a tile grid) vs a GUI panel view: the same discriminator the
        /// engine forks <c>Scene::Render</c> on, and the one that decides which pass chain runs.</summary>
        public bool World;
        public bool Perspective;
        /// <summary>CX-188: this view's fog, resolved on the GAME thread at the fold. Per view and not per
        /// frame because the area's fog answers for the area's scene and a GUI panel's scene is not it; see
        /// <see cref="FrameHoist.FoldFog"/>.</summary>
        public FrameHoist.ViewFog Fog;
    }

    /// <summary>One frame's worth. Two of these ping-pong.</summary>
    public sealed class Frame
    {
        public readonly ViewRec[] Views = new ViewRec[MaxViews];
        public int ViewCount;
        /// <summary>Monotonic frame number, so a consumer can say WHICH frame it drew and a late one is
        /// visible rather than merely wrong.</summary>
        public long Index;
        /// <summary>Dirty object/part/scene/camera counts this frame folded, for the period line.</summary>
        public int DirtyObjects, DirtyParts, DirtyScenes, DirtyCameras;
        /// <summary>CX-188: every ENGINE read the draw used to do, done once on the game thread at the fold
        /// and carried across by value. See <see cref="FrameHoist"/>.</summary>
        public FrameHoist Hoist;

        public void Reset(long index)
        {
            ViewCount = 0;
            Index = index;
            DirtyObjects = 0; DirtyParts = 0; DirtyScenes = 0; DirtyCameras = 0;
        }
    }

    private static readonly Frame _a = new(), _b = new();
    /// <summary>The frame the GAME thread is filling. Only the game thread touches it.</summary>
    private static Frame _building = _a;
    /// <summary>The frame the RENDERER holds, or null between a release and the next publish. Written by the
    /// game thread under <see cref="_gate"/>, read by both.</summary>
    private static Frame? _published;
    private static readonly object _gate = new();
    private static long _frames, _dropped, _waits, _waitTicks;
    private static ExtenderLog? _log;
    private static bool _saidFull;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>This frame's number -- the one the game thread is filling. Bumped once per
    /// <see cref="Publish"/>, so a producer can say "this frame" without a hook of its own
    /// (<c>TextDoc.NextList</c> is what needs it).</summary>
    internal static long FrameSeq => _frames;

    /// <summary>The frame being built right now (game thread only).</summary>
    internal static Frame Building => _building;

    /// <summary>Record a view: the game issued a <c>RenderScene</c> for this scene through this camera. Order
    /// is the game's -- <c>CGuiPanel::RenderSceneViewFirst</c>/<c>Last</c> reorder the engine's own view list
    /// and the panels are drawn in that order, so the list is append-only here and never sorted.
    /// <para>Game thread, hot (once per view per frame), allocates nothing.</para></summary>
    public static void RequestView(DbId scene, DbId camera)
    {
        Frame f = _building;
        // The frame's window state is latched at its first view, because every view's rect and aspect come
        // off it and the game issues them all before the present boundary.
        if (f.ViewCount == 0) Seam.SeamFrame.Latch();
        if (f.ViewCount >= MaxViews)
        {
            _dropped++;
            if (!_saidFull)
            {
                _saidFull = true;
                _log?.Always($"RenderSnapshot: more than {MaxViews} views in one frame -- this one is DROPPED "
                           + "and will not draw; raise MaxViews (the cap is a measured high-water mark, not a budget)");
            }
            return;
        }
        ref ViewRec r = ref f.Views[f.ViewCount];
        r.Scene = scene;
        r.Camera = camera;
        SeamFrameRect(out float vx, out float vy, out float vw, out float vh, out float gui);
        if (!DbCamera.BuildView(camera, vx, vy, vw, vh, Seam.SeamFrame.ScreenW, Seam.SeamFrame.ScreenH, gui,
                                out AlloyView v))
            return;   // a degenerate rect: the engine skips such a view too, and an invalid one is not recorded
        DbCamera.SetViewVolume(camera, v);
        r.View = v.View; r.Proj = v.Proj;
        r.X = v.X; r.Y = v.Y; r.W = v.W; r.H = v.H;
        r.Near = v.Near; r.Far = v.Far; r.Fovy = v.Fovy; r.Aspect = v.Aspect;
        r.DepthNear = v.DepthNear; r.DepthFar = v.DepthFar;
        r.Perspective = v.Perspective;
        r.CamPos = DbCamera.PosOf(camera);
        // RotOf, not the raw column, for the same reason PosOf is above it: a camera the game moves through
        // its sub-object writes ObjRot and leaves CamRot where NewCAurCamera left it (identity). The view
        // matrix is built from RotOf, so the raw column published a camera that looked straight DOWN while
        // the frame was drawn from the chase camera -- and every consumer of FrameInputs.SceneCamFwd (the
        // receiver-frustum caster cull, the cube-light visibility cull, the particle billboards) culled
        // against that downward cone.
        r.CamRot = DbCamera.RotOf(camera);
        r.World = SceneDb.Scenes.Valid(scene) && SceneDb.SceneIsWorld[scene.Index];
        f.ViewCount++;
    }

    private static void SeamFrameRect(out float x, out float y, out float w, out float h, out float gui)
    {
        Seam.SeamFrame.Viewport(out x, out y, out w, out h);
        gui = Seam.SeamFrame.GuiScale;
    }

    /// <summary>Fold this frame's dirty sets into the renderer's side and publish. Game thread, at the present
    /// boundary.
    /// <para>The fold is a COUNT plus <see cref="DbTable.TakeDirty"/> here; the per-record work it drives
    /// (rewriting the renderer's persistent draw records for dirty parts) is seam-feed's, called through
    /// <see cref="Fold"/> so the order -- fold, then publish, then the game thread is free -- is this file's to
    /// state and not a convention each stream remembers.</para>
    /// <para>Blocks only if the renderer still holds frame N-1, which bounds latency at one frame. The wait is
    /// counted and reported: a frame that waits is the renderer being the bottleneck, which is the number the
    /// render-thread decision is judged on.</para></summary>
    public static Frame Publish()
    {
        Frame f = _building;
        f.DirtyObjects = SceneDb.Objects.DirtyCount;
        f.DirtyParts = SceneDb.Parts.DirtyCount;
        f.DirtyScenes = SceneDb.Scenes.DirtyCount;
        f.DirtyCameras = SceneDb.Cameras.DirtyCount;
        // CX-188: the frame's ENGINE reads, FIRST -- before seam-feed's fold, which is where the records the
        // renderer draws are written, and before anything hands the frame across. Every read below is of
        // memory the game thread owns, so this is the only moment in the frame at which it may happen.
        FrameHoist.Fold(ref f.Hoist);
        for (int k = 0; k < f.ViewCount; k++)
            FrameHoist.FoldFog(f.Views[k].Scene, in f.Hoist, out f.Views[k].Fog);
        Fold?.Invoke(f);
        SceneDb.Objects.TakeDirty();
        SceneDb.Parts.TakeDirty();
        SceneDb.Scenes.TakeDirty();
        SceneDb.Cameras.TakeDirty();

        lock (_gate)
        {
            // _stopping as well as the held frame: a renderer that faulted while holding N-1 must not be able
            // to park the GAME thread here. Stand-downs null the frame too, so this is the second lock on the
            // one failure that would read as a frozen client rather than a bad frame.
            while (_published != null && !_stopping)
            {
                _waits++;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Monitor.Wait(_gate);
                _waitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            }
            // CX-120: the fill target flips INSIDE the lock, BEFORE the frame is published and pulsed.
            // Flipping it after the lock left a window in which _published and _building named the same frame
            // -- a consumer woken by this pulse could Take() f and still see it as the one being filled. The
            // game thread is the only writer of _building either way, so nothing else changes; what changes is
            // that the invariant "the renderer never holds the frame being filled" is true at every instant a
            // consumer can observe, rather than merely by the time the publisher got round to it. That window
            // is what DbScene.SelfCheck's alias check caught intermittently under load, and the check was
            // right.
            _building = ReferenceEquals(f, _a) ? _b : _a;
            _published = f;
            Monitor.PulseAll(_gate);
        }
        _frames++;
        _building.Reset(_frames);
        return f;
    }

    /// <summary>seam-feed's fold: rewrite the renderer's persistent records for the frame's dirty parts. Set
    /// once at boot; a null one means the renderer draws nothing new, which is a valid state only before the
    /// feed is armed.</summary>
    public static Action<Frame>? Fold;

    /// <summary>Take the published frame, waiting for one if none is up. Render thread. Null = the session is
    /// shutting down (<see cref="Stop"/> was called), which is the only way out of the wait.</summary>
    internal static Frame? Take()
    {
        lock (_gate)
        {
            while (_published == null && !_stopping) Monitor.Wait(_gate);
            Frame? f = _published;
            return _stopping ? null : f;
        }
    }

    /// <summary>The renderer is done with the frame it took: the game thread may publish the next one. Render
    /// thread, and the ONE call that keeps the pipeline moving -- a renderer that faults without releasing
    /// would hang the game thread at the next publish, which is why <see cref="RenderThread"/> releases in a
    /// <c>finally</c>.</summary>
    internal static void Release()
    {
        lock (_gate)
        {
            _published = null;
            Monitor.PulseAll(_gate);
        }
    }

    private static volatile bool _stopping;

    /// <summary>Wake every waiter and keep them awake: the session is ending, or the render thread stood down
    /// and the single-threaded path takes over.</summary>
    internal static void Stop()
    {
        lock (_gate)
        {
            _stopping = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Back to a usable state after a <see cref="Stop"/>, for the self-check's second pass.</summary>
    internal static void Resume()
    {
        lock (_gate)
        {
            _stopping = false;
            _published = null;
        }
    }

    /// <summary>CX-188: <b>THE FENCE.</b> Block until the renderer holds no frame -- it has finished the one
    /// it was handed and released it. Game thread, once per frame, at the top of the frame boundary.
    /// <para>This is the whole of the two threads' mutual exclusion, and it is why the game thread may touch
    /// framebuffer 0, the engine's GL state and the engine's own context at all with a render thread live:
    /// from the moment this returns until the next <see cref="Publish"/>, the render thread is parked inside
    /// <see cref="Take"/> with nothing to draw. The present and the 2D UI go in that gap.</para>
    /// <para>It is not a new wait. <see cref="Publish"/> already waited for exactly this, at exactly this
    /// point in the frame, because latency is bounded at one frame either way; hoisting it out of the publish
    /// only makes the gap it opens NAMED, so the UI and the present can be ordered inside it rather than
    /// racing it. On the single-threaded path nothing is ever published-but-unreleased here, so it returns
    /// without touching the monitor and the frame is byte-identical.</para></summary>
    internal static void AwaitDrained()
    {
        lock (_gate)
        {
            while (_published != null && !_stopping)
            {
                _waits++;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Monitor.Wait(_gate);
                _waitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            }
        }
    }

    /// <summary>The frame currently published, without taking it or waiting. The single-threaded path reads
    /// it to look a view up; it does NOT consume -- <see cref="Release"/> is still what hands the slot back.</summary>
    internal static Frame? Drain()
    {
        Frame? f;
        lock (_gate) f = _published;
        return f;
    }

    internal static string Stats(int _)
    {
        double waitMs = _waitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        string s = $"snapshot: {_frames} frames, {_waits} publish waits ({waitMs:F1} ms total)";
        if (_dropped > 0) s += $", {_dropped} views DROPPED (over the {MaxViews} cap)";
        _waits = 0; _waitTicks = 0;
        return s;
    }
}
