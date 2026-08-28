using System;
using System.Runtime.InteropServices;
using Sowx.Framework;


namespace Sowx.Graphics;

/// <summary>
/// #80 (map #68, spec #77 M1): owns the share-listed GL 4.6 core context on the engine's drawable --
/// the promotion of the #70 probe. Created once on the first presented frame (the render thread has the
/// engine's 3.3 context current there); clean failure path disables the service and leaves the engine
/// untouched. Every in-play frame it runs the context-switch bracket: fence the engine's commands,
/// switch to the 4.6 context, server-wait the fence, drain <c>glGetError</c>, fence back, switch to the
/// engine context, server-wait, drain again -- 2 switches/frame, proving zero GL errors and no state
/// leakage in live play. Per #70: buffers/fences share, VAOs do not -- own 4.6-side VAOs are the
/// tracer-draw ticket's job, done inside the bracket via <see cref="Available"/>/<see cref="Context"/>.
/// <para>Opt-out: <c>gl46=0</c> in <c>sowx.cfg</c> (ADR 0013 bucket 2 -- the capability gate is
/// decided before the context is probed). Hot path allocates nothing (cached fn pointers, GL fence objects
/// only); the ~900-frame stats line is the sole cold part.</para>
/// </summary>
public sealed unsafe class GlContextService : IExtenderService
{
    private const int LogEvery = 900;
    private const ulong TimeoutIgnored = 0xFFFFFFFFFFFFFFFFUL;
    private const uint GL_SYNC_GPU_COMMANDS_COMPLETE = 0x9117, GL_VERSION = 0x1F02;
    private const int GLX_FBCONFIG_ID = 0x8013;
    private const int GLX_CONTEXT_MAJOR_VERSION_ARB = 0x2091, GLX_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    private const int GLX_CONTEXT_PROFILE_MASK_ARB = 0x9126, GLX_CONTEXT_CORE_PROFILE_BIT_ARB = 0x1;

    /// <summary>True once the 4.6 context is live; consumers (tracer draw) check this per frame.</summary>
    public static bool Available;
    /// <summary>The GLX handle of the 4.6 core context (valid while <see cref="Available"/>).</summary>
    public static IntPtr Context;

    /// <summary>#216: <c>gl46=0</c> (or <c>lockchat</c>) -- a CHOICE, not a verdict about the device. Kept
    /// apart from <c>_disabled</c>, which <see cref="Fail"/> also sets, or the frame verdict would report
    /// every driver failure as a config opt-out.</summary>
    public static bool OptedOut { get; private set; }

    private static ExtenderLog? _log;
    private static bool _disabled, _tried;
    private static IntPtr _dpy, _drw, _engCtx;

    private static delegate* unmanaged<byte*, IntPtr> _glxGetProc;
    private static delegate* unmanaged<IntPtr, IntPtr, IntPtr, int> _makeCur;
    private static delegate* unmanaged<IntPtr, IntPtr, void> _destroy;
    private static delegate* unmanaged<uint, uint, IntPtr> _fenceSync;
    private static delegate* unmanaged<IntPtr, uint, ulong, void> _waitSync;
    private static delegate* unmanaged<IntPtr, void> _deleteSync;
    private static delegate* unmanaged<void> _flush;

    private static int _frames, _err46, _errEng, _switchFails;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // #170: the 4.6-context bracket wraps the present, so it is the LAST pre-present participant.
        FrameRegistry.Global.Register(FramePhase.PrePresent, 10, OnFrame, "GlContextService.bracket", log);
        _disabled = !Settings.Bool("gl46", true) || GameGate.Locked;
        OptedOut = _disabled;
        log.Info(_disabled
            ? "GlContextService: stood down (gl46=0 or lockchat in sowx.cfg)"
            : "GlContextService: armed -- 4.6 share-listed context on first presented frame");
    }

    [DllImport("libdl.so.2")] private static extern IntPtr dlsym(IntPtr handle, string name);
    private static IntPtr Dl(string n) => dlsym(IntPtr.Zero, n);

    private static IntPtr GP(string name)
    {
        Span<byte> b = stackalloc byte[name.Length + 1];
        for (int i = 0; i < name.Length; i++) b[i] = (byte)name[i];
        b[name.Length] = 0;
        fixed (byte* p = b) return _glxGetProc(p);
    }

    [UnmanagedCallersOnly]
    private static int XErrorNoop(IntPtr dpy, IntPtr ev) => 0;   // default handler exits the client

    private static bool _worked;   // a consumer ran Enter/Leave since the last present

    /// <summary>Per-frame entry from the frame owner's present (render thread). Creates the context on
    /// first call, then runs the health bracket -- unless a consumer (tracer draw) already ran one this
    /// frame. Near-free when disabled/failed.</summary>
    public static void OnFrame()
    {
        if (_disabled) return;
        if (!_tried) { _tried = true; try { Create(); } catch (Exception e) { Fail($"EXCEPTION {e.Message}"); } }
        if (Available && !_worked && Enter()) { InBracket?.Invoke(); Leave(); }
        _worked = false;
    }

    /// <summary>Work that needs A context but not the Alloy pass: the health bracket runs every in-play frame
    /// whether or not anything drew, so it is the one place a resource retired while Alloy is stood down can
    /// still be freed. #211's texture deletes hang here -- otherwise /vanilla, a module unload back to the
    /// main menu, or the permanent disarm latch would hold the whole owned texture set for the session,
    /// because the colour bracket returns before <c>Enter</c> forever in those states.</summary>
    public static Action? InBracket;

    private static void Fail(string why)
    {
        _disabled = true;
        Available = false;
        _log?.Info($"GlContextService: FAIL -- {why}; service disabled, engine context untouched");
    }

    /// <summary>Create the 4.6 core context share-listed with the engine's, on the engine's own
    /// FBConfig/drawable (the #70 recipe). Cold, one-shot; any missing piece is a clean disable.</summary>
    private static void Create()
    {
        var log = _log!;
        if (!Gl.Init(log)) { Fail("Gl.Init"); return; }

        _glxGetProc = (delegate* unmanaged<byte*, IntPtr>)Dl("glXGetProcAddressARB");
        var getCtx = (delegate* unmanaged<IntPtr>)Dl("glXGetCurrentContext");
        var getDpy = (delegate* unmanaged<IntPtr>)Dl("glXGetCurrentDisplay");
        var getDrw = (delegate* unmanaged<IntPtr>)Dl("glXGetCurrentDrawable");
        _makeCur = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)Dl("glXMakeCurrent");
        _destroy = (delegate* unmanaged<IntPtr, IntPtr, void>)Dl("glXDestroyContext");
        var queryDrw = (delegate* unmanaged<IntPtr, IntPtr, int, uint*, void>)Dl("glXQueryDrawable");
        var chooseFbc = (delegate* unmanaged<IntPtr, int, int*, int*, IntPtr>)Dl("glXChooseFBConfig");
        var xDefScreen = (delegate* unmanaged<IntPtr, int>)Dl("XDefaultScreen");
        var xSetErr = (delegate* unmanaged<IntPtr, IntPtr>)Dl("XSetErrorHandler");
        var xSync = (delegate* unmanaged<IntPtr, int, int>)Dl("XSync");
        var xFree = (delegate* unmanaged<IntPtr, int>)Dl("XFree");
        if (_glxGetProc == null || getCtx == null || getDpy == null || getDrw == null || _makeCur == null
            || _destroy == null || queryDrw == null || chooseFbc == null || xDefScreen == null || xSetErr == null)
        { Fail("a GLX/X11 entry point did not resolve"); return; }

        var createCtx = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int, int*, IntPtr>)GP("glXCreateContextAttribsARB");
        _fenceSync = (delegate* unmanaged<uint, uint, IntPtr>)GP("glFenceSync");
        _waitSync = (delegate* unmanaged<IntPtr, uint, ulong, void>)GP("glWaitSync");
        _deleteSync = (delegate* unmanaged<IntPtr, void>)GP("glDeleteSync");
        _flush = (delegate* unmanaged<void>)GP("glFlush");
        var getString = (delegate* unmanaged<uint, byte*>)GP("glGetString");
        if (createCtx == null || _fenceSync == null || _waitSync == null || _deleteSync == null || _flush == null)
        { Fail("a GL entry point did not resolve"); return; }

        _dpy = getDpy(); _drw = getDrw(); _engCtx = getCtx();
        if (_engCtx == IntPtr.Zero) { _tried = false; return; }   // no context yet -- retry next frame

        uint fbcId = 0;
        queryDrw(_dpy, _drw, GLX_FBCONFIG_ID, &fbcId);
        int n = 0;
        int* pick = stackalloc int[3] { GLX_FBCONFIG_ID, (int)fbcId, 0 };
        IntPtr fbcArr = chooseFbc(_dpy, xDefScreen(_dpy), pick, &n);
        if (fbcArr == IntPtr.Zero || n == 0) { Fail($"FBConfig id {fbcId} not found"); return; }
        IntPtr fbc = *(IntPtr*)fbcArr;
        if (xFree != null) xFree(fbcArr);

        int* attribs = stackalloc int[7] {
            GLX_CONTEXT_MAJOR_VERSION_ARB, 4, GLX_CONTEXT_MINOR_VERSION_ARB, 6,
            GLX_CONTEXT_PROFILE_MASK_ARB, GLX_CONTEXT_CORE_PROFILE_BIT_ARB, 0 };
        IntPtr oldHandler = xSetErr((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&XErrorNoop);
        IntPtr ctx = createCtx(_dpy, fbc, _engCtx, 1, attribs);
        if (xSync != null) xSync(_dpy, 0);
        xSetErr(oldHandler);
        if (ctx == IntPtr.Zero) { Fail("4.6 core share-listed context creation returned null"); return; }

        if (_makeCur(_dpy, _drw, ctx) == 0)
        {
            _destroy(_dpy, ctx);
            _makeCur(_dpy, _drw, _engCtx);
            Fail("glXMakeCurrent(4.6 ctx, engine drawable) refused");
            return;
        }
        string ver = getString != null ? CStr(getString(GL_VERSION)) : "?";
        while (Gl.GetError() != 0) { }   // fresh-context error queue starts clean
        _makeCur(_dpy, _drw, _engCtx);

        Context = ctx;
        Available = true;
        log.Info($"GlContextService: 4.6 core context LIVE (0x{ctx:x}, GL_VERSION='{ver}') -- bracket every frame");
    }

    /// <summary>Open the context-switch bracket (hot, zero-alloc): fence the engine's queued commands,
    /// switch to the 4.6 context, server-wait the fence (glWaitSync -- no CPU stall). True = the 4.6
    /// context is current and consumers may issue GL; every successful Enter MUST be paired with
    /// <see cref="Leave"/> before returning to engine code.</summary>
    public static bool Enter()
    {
        if (!Available) return false;
        IntPtr fIn = _fenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
        _flush();
        if (_makeCur(_dpy, _drw, Context) == 0) { _switchFails++; if (fIn != IntPtr.Zero) _deleteSync(fIn); return false; }
        if (fIn != IntPtr.Zero) { _waitSync(fIn, 0, TimeoutIgnored); _deleteSync(fIn); }
        return true;
    }

    /// <summary>Close the bracket: drain glGetError on the 4.6 side (an error can never leak across the
    /// boundary unnoticed), fence, switch back to the engine context, server-wait, drain again.</summary>
    public static void Leave()
    {
        int guard = 0;
        while (Gl.GetError() != 0 && ++guard < 16) _err46++;

        IntPtr fOut = _fenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
        _flush();
        if (_makeCur(_dpy, _drw, _engCtx) == 0)
        {
            // cannot leave the engine on the wrong context -- disable rather than risk the present
            Fail("glXMakeCurrent back to engine context refused");
            return;
        }
        if (fOut != IntPtr.Zero) { _waitSync(fOut, 0, TimeoutIgnored); _deleteSync(fOut); }
        guard = 0;
        while (Gl.GetError() != 0 && ++guard < 16) _errEng++;
        _worked = true;

        if (++_frames >= LogEvery)
        {
            _log?.Info($"GlContextService: [gl46] {_frames} brackets (>= 1/frame; +1 per Alloy pass), " +
                       $"errors 4.6={_err46} engine={_errEng}, switch-fails={_switchFails}");
            _frames = 0; _err46 = 0; _errEng = 0; _switchFails = 0;
        }
    }

    private static string CStr(byte* p)
    {
        if (p == null) return "<null>";
        int len = 0; while (p[len] != 0 && len < 256) len++;
        return System.Text.Encoding.ASCII.GetString(p, len);
    }
}
