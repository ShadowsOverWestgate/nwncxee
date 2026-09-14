using System;
using System.Runtime.InteropServices;
using CX.Framework;


namespace CX.Graphics;

/// <summary>
/// #80 (map #68, spec #77 M1): owns the share-listed GL 4.6 core context on the engine's drawable --
/// the promotion of the #70 probe. Created once on the first presented frame (the render thread has the
/// engine's 3.3 context current there); clean failure path disables the service and leaves the engine
/// untouched. Every in-play frame it runs the context-switch bracket: fence the engine's commands,
/// switch to the 4.6 context, server-wait the fence, drain <c>glGetError</c>, fence back, switch to the
/// engine context, server-wait, drain again -- 2 switches/frame, proving zero GL errors and no state
/// leakage in live play. Per #70: buffers/fences share, VAOs do not -- own 4.6-side VAOs are the
/// tracer-draw ticket's job, done inside the bracket via <see cref="Available"/>/<see cref="Context"/>.
/// <para>Opt-out: <c>gl46=0</c> in <c>cx.cfg</c> (ADR 0013 bucket 2 -- the capability gate is
/// decided before the context is probed). Hot path allocates nothing (cached fn pointers, GL fence objects
/// only); the ~900-frame stats line is the sole cold part.</para>
/// </summary>
public sealed unsafe class GlContextService : IExtenderService
{
    private const int LogEvery = 900;
    private const ulong TimeoutIgnored = 0xFFFFFFFFFFFFFFFFUL;
    private const uint GL_SYNC_GPU_COMMANDS_COMPLETE = 0x9117, GL_VERSION = 0x1F02, GL_RENDERER = 0x1F01;
    /// <summary>The 4.6 context's GL_RENDERER names zink (Mesa's GL-on-Vulkan): the test vehicle whose bindless
    /// teardown faults on a worker thread at texture delete; OwnedTextures reads it to keep such names alive.</summary>
    internal static bool Zink => _zink;
    private static bool _zink;
    private const int GLX_FBCONFIG_ID = 0x8013;
    private const int GLX_CONTEXT_MAJOR_VERSION_ARB = 0x2091, GLX_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    private const int GLX_CONTEXT_PROFILE_MASK_ARB = 0x9126, GLX_CONTEXT_CORE_PROFILE_BIT_ARB = 0x1;
    private const int GLX_CONTEXT_FLAGS_ARB = 0x2094, GLX_CONTEXT_DEBUG_BIT_ARB = 0x1;
    private const uint GL_DEBUG_OUTPUT = 0x92E0, GL_DEBUG_OUTPUT_SYNCHRONOUS = 0x8242, GL_DONT_CARE = 0x1100,
                       GL_DEBUG_SEVERITY_NOTIFICATION = 0x826B, GL_DEBUG_TYPE_ERROR = 0x824C,
                       GL_DEBUG_TYPE_DEPRECATED_BEHAVIOR = 0x824D, GL_DEBUG_TYPE_UNDEFINED_BEHAVIOR = 0x824E,
                       GL_DEBUG_TYPE_PORTABILITY = 0x824F, GL_DEBUG_TYPE_PERFORMANCE = 0x8250;
    private const int DebugIds = 64, DebugLines = 200;

    /// <summary><c>gldebug=1</c> in <c>cx.cfg</c>: create the 4.6 context as a DEBUG context and route
    /// KHR_debug messages into the extender log. This is the only channel undefined behaviour reaches --
    /// <c>glGetError</c> has no code for it, and a non-debug context "may have zero debug output" (KHR_debug).
    /// Mesa reports a great deal here that NVIDIA never mentions, which is the point: the tester's driver
    /// gets to say what it objects to (docs/research/opengl-46-portability-audit.md, E1). Synchronous, so the
    /// callback runs on this thread inside the offending call. ERROR and UNDEFINED_BEHAVIOR messages are
    /// logged every time (Mesa files EVERY API error under one id, so an id is not an identity there), up to
    /// <see cref="DebugLines"/> per session; the other types log their first sighting per id. An allocation
    /// per logged line, accepted under the gate. Totals go on the [gl46] line.</summary>
    private static bool _debug;
    /// <summary><c>gldebug=1</c>: the passes may spend on per-call validation the release path skips.</summary>
    internal static bool Debug => _debug;
    private static readonly uint[] _dbgId = new uint[DebugIds];
    private static int _dbgN, _dbgLogged, _dbgTotal, _dbgDropped;

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
    /// <summary>#456: the <c>glsync=1</c> ordering primitive. <c>glFinish</c> is the only barrier that is
    /// unconditionally correct across two contexts sharing one drawable -- it returns when the commands have
    /// actually EXECUTED, where the fence pair only promises the GPU will order them. Costs a full pipeline
    /// stall per switch, so it is the diagnosis and, if it is also the cure, a decision to take deliberately.</summary>
    private static delegate* unmanaged<void> _finish;

    private static int _frames, _err46, _errEng, _switchFails;

    /// <summary>#324: GPU span of the frame's Alloy work -- t0 at the first Enter, t1 re-stamped at every
    /// Leave, so the pair brackets the shadow bake through the colour frame on the GPU timeline. NOT the
    /// frame's GPU time: engine GL outside the brackets (2D UI, present) is excluded and idle gaps inside
    /// them are included. Read beside the CPU [zones] line and the [alloy] elapsed queries: span far above
    /// the elapsed sum means the GPU idled on the CPU; frame period minus span bounds the headroom the
    /// GPU-side moves (#1-#4) spend into.</summary>
    private static GpuStamp _span;
    private static bool _stamped;

    /// <summary>The period's GPU span, formatted for the [zones] line; resets the period.</summary>
    public static string GpuSpanLine()
    {
        string s = _span.Measured ? $" || gpu-span avg {_span.AvgMs:F1}ms max {_span.MaxMs:F1}ms" : " || gpu-span n/a";
        _span.Reset();
        return s;
    }

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // #170: the 4.6-context bracket wraps the present, so it is the LAST pre-present participant.
        FrameRegistry.Global.Register(FramePhase.PrePresent, 10, OnFrame, "GlContextService.bracket", log);
        _disabled = !Settings.Bool("gl46", true) || GameGate.Locked;
        OptedOut = _disabled;
        _debug = Settings.Bool("gldebug", false);
        // #456: does the 4.6 context's DEFAULT FRAMEBUFFER address the same surface the engine presents?
        // On GLX both contexts name one drawable and the question never arises. Under WGL the pairing is
        // Wine's to make, and if the two contexts track their own back buffer against a shared chain, the
        // owned frame lands in whichever one Alloy happens to hold -- half the presents, which reads as a
        // black frame every other frame. One pixel read either side of the switch answers it; the read
        // stalls the pipeline, so it is a knob and it stops on its own.
        _swapProbe = Settings.Int("swapprobe", 0);
        // #456: how the bracket orders the two contexts against one drawable. 0 = the fence pair
        // (glFenceSync/glWaitSync, a GPU-side wait that costs nothing on the CPU). 1 = glFinish, which
        // stalls until the work is done. If the black frames go away at 1, the frames were racing the
        // present and the fence pair is not the barrier this drawable needs on this platform.
        _syncMode = Settings.Int("glsync", 0, 0, 1);
        log.Info(_disabled
            ? "GlContextService: stood down (gl46=0 or lockchat in cx.cfg)"
            : "GlContextService: armed -- 4.6 share-listed context on first presented frame");
    }

    [DllImport("libdl.so.2")] private static extern IntPtr dlsym(IntPtr handle, string name);
    private static IntPtr Dl(string n) => dlsym(IntPtr.Zero, n);

    /// <summary>#455: the WGL mirror of the GLX handles above. Lazy-bound like <c>libdl.so.2</c> is on
    /// Windows, so neither import is resolved on the platform that never calls it. There is no FBConfig
    /// step: the window's pixel format is already set by the engine.</summary>
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentDC();
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentContext();
    [DllImport("opengl32.dll")] private static extern int wglMakeCurrent(IntPtr hdc, IntPtr ctx);
    [DllImport("opengl32.dll")] private static extern int wglDeleteContext(IntPtr ctx);
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetProcAddress(string name);

    private static readonly bool Win = OperatingSystem.IsWindows();

    /// <summary>Resolve a GL entry point. Windows goes through the client's SDL loader rather than
    /// <c>wglGetProcAddress</c>, which returns null for the GL 1.1 names (<c>glGetString</c>, <c>glFlush</c>,
    /// <c>glEnable</c>) that live in opengl32 itself.</summary>
    private static IntPtr GP(string name)
    {
        if (Win) return Gl.P(name);
        Span<byte> b = stackalloc byte[name.Length + 1];
        for (int i = 0; i < name.Length; i++) b[i] = (byte)name[i];
        b[name.Length] = 0;
        fixed (byte* p = b) return _glxGetProc(p);
    }

    /// <summary>Make <paramref name="ctx"/> current on the engine's drawable; 0 = refused. The one place
    /// the two windowing APIs differ in the hot bracket.</summary>
    private static int MakeCur(IntPtr ctx) => Win ? wglMakeCurrent(_drw, ctx) : _makeCur(_dpy, _drw, ctx);

    private static void Destroy(IntPtr ctx) { if (Win) wglDeleteContext(ctx); else _destroy(_dpy, ctx); }

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
        _span.Rotate(); _stamped = false;
    }

    /// <summary>Work that needs A context but not the Alloy pass: the health bracket runs every in-play frame
    /// whether or not anything drew, so it is the one place a resource retired while Alloy is stood down can
    /// still be freed. #211's texture deletes hang here -- otherwise a module unload back to the
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
    /// drawable (the #70 recipe). Cold, one-shot; any missing piece is a clean disable.</summary>
    private static void Create()
    {
        var log = _log!;
        if (!Gl.Init(log)) { Fail("Gl.Init"); return; }
        if (!(Win ? ResolveWin() : ResolveGlx())) return;
        if (_engCtx == IntPtr.Zero) { _tried = false; return; }   // no context yet -- retry next frame

        _fenceSync = (delegate* unmanaged<uint, uint, IntPtr>)GP("glFenceSync");
        _waitSync = (delegate* unmanaged<IntPtr, uint, ulong, void>)GP("glWaitSync");
        _deleteSync = (delegate* unmanaged<IntPtr, void>)GP("glDeleteSync");
        _flush = (delegate* unmanaged<void>)GP("glFlush");
        _finish = (delegate* unmanaged<void>)GP("glFinish");
        var getString = (delegate* unmanaged<uint, byte*>)GP("glGetString");
        if (_fenceSync == null || _waitSync == null || _deleteSync == null || _flush == null)
        { Fail("a GL entry point did not resolve"); return; }

        // WGL_CONTEXT_* are numerically identical to the GLX_CONTEXT_* values above (wglext.h), so one
        // attribute list serves both.
        int* attribs = stackalloc int[9] {
            GLX_CONTEXT_MAJOR_VERSION_ARB, 4, GLX_CONTEXT_MINOR_VERSION_ARB, 6,
            GLX_CONTEXT_PROFILE_MASK_ARB, GLX_CONTEXT_CORE_PROFILE_BIT_ARB,
            _debug ? GLX_CONTEXT_FLAGS_ARB : 0, _debug ? GLX_CONTEXT_DEBUG_BIT_ARB : 0, 0 };
        IntPtr ctx = Win ? CreateWin(attribs) : CreateGlx(attribs);
        if (ctx == IntPtr.Zero) { Fail("4.6 core share-listed context creation returned null"); return; }

        if (MakeCur(ctx) == 0)
        {
            Destroy(ctx);
            MakeCur(_engCtx);
            Fail("MakeCurrent(4.6 ctx, engine drawable) refused");
            return;
        }
        string ver = getString != null ? CStr(getString(GL_VERSION)) : "?";
        string renderer = getString != null ? CStr(getString(GL_RENDERER)) : "?";
        _zink = renderer.Contains("zink", StringComparison.OrdinalIgnoreCase);
        bool dbg = _debug && ArmDebug();
        while (Gl.GetError() != 0) { }   // fresh-context error queue starts clean
        MakeCur(_engCtx);

        Context = ctx;
        Available = true;
        log.Info($"GlContextService: 4.6 core context LIVE (0x{ctx:x}, GL_VERSION='{ver}') -- bracket every frame" +
                 (dbg ? "; DEBUG context, KHR_debug messages go to this log (gldebug=1)" : ""));
    }

    /// <summary>GLX side: the display/drawable/context the engine is currently on, plus the entry points
    /// only this path uses. False = a clean disable (<see cref="Fail"/> already called).</summary>
    private static bool ResolveGlx()
    {
        _glxGetProc = (delegate* unmanaged<byte*, IntPtr>)Dl("glXGetProcAddressARB");
        var getCtx = (delegate* unmanaged<IntPtr>)Dl("glXGetCurrentContext");
        var getDpy = (delegate* unmanaged<IntPtr>)Dl("glXGetCurrentDisplay");
        var getDrw = (delegate* unmanaged<IntPtr>)Dl("glXGetCurrentDrawable");
        _makeCur = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)Dl("glXMakeCurrent");
        _destroy = (delegate* unmanaged<IntPtr, IntPtr, void>)Dl("glXDestroyContext");
        if (_glxGetProc == null || getCtx == null || getDpy == null || getDrw == null || _makeCur == null
            || _destroy == null || Dl("glXQueryDrawable") == IntPtr.Zero || Dl("glXChooseFBConfig") == IntPtr.Zero
            || Dl("XDefaultScreen") == IntPtr.Zero || Dl("XSetErrorHandler") == IntPtr.Zero)
        { Fail("a GLX/X11 entry point did not resolve"); return false; }

        _dpy = getDpy(); _drw = getDrw(); _engCtx = getCtx();
        return true;
    }

    /// <summary>WGL side: the engine's current DC and context. No FBConfig equivalent -- the window's
    /// pixel format is already set by the engine, and the share context is an argument to
    /// <c>wglCreateContextAttribsARB</c> rather than a separate <c>wglShareLists</c> step.</summary>
    private static bool ResolveWin()
    {
        _drw = wglGetCurrentDC(); _engCtx = wglGetCurrentContext();
        if (_drw == IntPtr.Zero) { _tried = false; return false; }   // no window yet -- retry next frame
        return true;
    }

    private static IntPtr CreateGlx(int* attribs)
    {
        var queryDrw = (delegate* unmanaged<IntPtr, IntPtr, int, uint*, void>)Dl("glXQueryDrawable");
        var chooseFbc = (delegate* unmanaged<IntPtr, int, int*, int*, IntPtr>)Dl("glXChooseFBConfig");
        var xDefScreen = (delegate* unmanaged<IntPtr, int>)Dl("XDefaultScreen");
        var xSetErr = (delegate* unmanaged<IntPtr, IntPtr>)Dl("XSetErrorHandler");
        var xSync = (delegate* unmanaged<IntPtr, int, int>)Dl("XSync");
        var xFree = (delegate* unmanaged<IntPtr, int>)Dl("XFree");
        var createCtx = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int, int*, IntPtr>)GP("glXCreateContextAttribsARB");
        if (createCtx == null) { Fail("glXCreateContextAttribsARB did not resolve"); return IntPtr.Zero; }

        uint fbcId = 0;
        queryDrw(_dpy, _drw, GLX_FBCONFIG_ID, &fbcId);
        int n = 0;
        int* pick = stackalloc int[3] { GLX_FBCONFIG_ID, (int)fbcId, 0 };
        IntPtr fbcArr = chooseFbc(_dpy, xDefScreen(_dpy), pick, &n);
        if (fbcArr == IntPtr.Zero || n == 0) { Fail($"FBConfig id {fbcId} not found"); return IntPtr.Zero; }
        IntPtr fbc = *(IntPtr*)fbcArr;
        if (xFree != null) xFree(fbcArr);

        IntPtr oldHandler = xSetErr((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&XErrorNoop);
        IntPtr ctx = createCtx(_dpy, fbc, _engCtx, 1, attribs);
        if (xSync != null) xSync(_dpy, 0);
        xSetErr(oldHandler);
        return ctx;
    }

    private static IntPtr CreateWin(int* attribs)
    {
        IntPtr p = wglGetProcAddress("wglCreateContextAttribsARB");
        // WGL reports "unsupported" as 0, 1, 2, 3 or -1, not null alone.
        if ((long)p is >= -1 and <= 3) { Fail("wglCreateContextAttribsARB did not resolve"); return IntPtr.Zero; }
        var createCtx = (delegate* unmanaged<IntPtr, IntPtr, int*, IntPtr>)p;
        return createCtx(_drw, _engCtx, attribs);
    }

    /// <summary>Open the context-switch bracket (hot, zero-alloc): fence the engine's queued commands,
    /// switch to the 4.6 context, server-wait the fence (glWaitSync -- no CPU stall). True = the 4.6
    /// context is current and consumers may issue GL; every successful Enter MUST be paired with
    /// <see cref="Leave"/> before returning to engine code.</summary>
    public static bool Enter()
    {
        if (!Available) return false;
        // Latch the probe pixel here, in the ENGINE context, where the viewport is the window.
        if (_swapProbe > 0 && GameGate.InModule)
        {
            int* vp = stackalloc int[4];
            Gl.GetIntegerv(Gl.VIEWPORT, vp);
            if (vp[2] > 16 && vp[3] > 16) { _probeX = vp[0] + vp[2] / 2; _probeY = vp[1] + vp[3] / 2; }
        }
        if (_syncMode == 1)
        {
            if (_finish != null) _finish();
            if (MakeCur(Context) == 0) { _switchFails++; return false; }
        }
        else
        {
            IntPtr fIn = _fenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
            _flush();
            if (MakeCur(Context) == 0) { _switchFails++; if (fIn != IntPtr.Zero) _deleteSync(fIn); return false; }
            if (fIn != IntPtr.Zero) { _waitSync(fIn, 0, TimeoutIgnored); _deleteSync(fIn); }
        }
        if (!_stamped) { _span.LastMs = -1; _stamped = _span.Begin(); if (_span.LastMs >= 0) FrameZones.Gpu(_span.LastMs, GpuStamp.Age); }
        return true;
    }

    /// <summary>Close the bracket: drain glGetError on the 4.6 side (an error can never leak across the
    /// boundary unnoticed), fence, switch back to the engine context, server-wait, drain again.</summary>
    /// <summary>#456: frames of <c>swapprobe</c> left. The probe reads the SAME pixel of framebuffer 0 twice
    /// -- once in the 4.6 context, once in the engine's, with nothing drawn between -- so a difference is
    /// the two contexts disagreeing about what framebuffer 0 IS, and nothing else.</summary>
    private static int _swapProbe;
    private static int _syncMode;

    /// <summary>The pixel both sides read, in WINDOW coordinates, latched in <see cref="Enter"/> from the
    /// engine context. It must not come from the current viewport: <see cref="Leave"/> also closes the
    /// shadow-bake bracket, whose viewport is a shadow map, and "the centre of the viewport" is then a
    /// different pixel on each side of the switch -- which is a probe that reports a difference it created.</summary>
    private static int _probeX, _probeY;

    private static uint ProbeCentrePixel()
    {
        if (Gl.ReadPixels == null) return 0;
        uint px = 0;
        int prev; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &prev);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        Gl.ReadPixels(_probeX, _probeY, 1, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, &px);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)prev);
        for (int i = 0; i < 4 && Gl.GetError != null && Gl.GetError() != 0; i++) { }
        return px;
    }

    public static void Leave()
    {
        int guard = 0;
        while (Gl.GetError() != 0 && ++guard < 16) _err46++;
        if (_stamped) _span.End();

        // Read BEFORE the switch, with the owned frame's resolve already in framebuffer 0. glReadPixels is
        // itself the sync -- it cannot return pixels the queue has not produced -- so no explicit finish.
        uint alloySide = 0;
        if (_swapProbe > 0 && GameGate.InModule)
        {
            alloySide = ProbeCentrePixel();
        }

        IntPtr fOut = IntPtr.Zero;
        if (_syncMode == 1) { if (_finish != null) _finish(); }
        else { fOut = _fenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0); _flush(); }
        if (MakeCur(_engCtx) == 0)
        {
            // cannot leave the engine on the wrong context -- disable rather than risk the present
            Fail("MakeCurrent back to the engine context refused");
            return;
        }
        if (fOut != IntPtr.Zero) { _waitSync(fOut, 0, TimeoutIgnored); _deleteSync(fOut); }
        guard = 0;
        while (Gl.GetError() != 0 && ++guard < 16) _errEng++;
        _worked = true;

        if (_swapProbe > 0 && GameGate.InModule)
        {
            _swapProbe--;
            _log?.Always($"[swapprobe] fb0 centre: 4.6 ctx 0x{alloySide:x8}, engine ctx 0x{ProbeCentrePixel():x8}"
                       + " -- equal means one surface, different means two");
        }
        if (++_frames >= LogEvery)
        {
            _log?.Info($"GlContextService: [gl46] {_frames} brackets (>= 1/frame; +1 per Alloy pass), " +
                       $"errors 4.6={_err46} engine={_errEng}, switch-fails={_switchFails}" +
                       (_debug ? $", debug msgs={_dbgTotal} this period ({_dbgLogged} logged, {_dbgDropped} dropped since boot)" : ""));
            _dbgTotal = 0;
            _frames = 0; _err46 = 0; _errEng = 0; _switchFails = 0;
        }
    }

    /// <summary>Wire the KHR_debug callback on the (current) 4.6 context. Everything enabled, then the
    /// NOTIFICATION severity muted -- Mesa files shader statistics there on every build.</summary>
    private static bool ArmDebug()
    {
        var callback = (delegate* unmanaged<delegate* unmanaged<uint, uint, uint, uint, int, byte*, void*, void>, void*, void>)GP("glDebugMessageCallback");
        var control = (delegate* unmanaged<uint, uint, uint, int, uint*, byte, void>)GP("glDebugMessageControl");
        var enable = (delegate* unmanaged<uint, void>)GP("glEnable");
        if (callback == null || control == null || enable == null)
        { _log?.Info("GlContextService: gldebug=1 but KHR_debug entry points did not resolve"); return false; }
        enable(GL_DEBUG_OUTPUT);
        enable(GL_DEBUG_OUTPUT_SYNCHRONOUS);
        control(GL_DONT_CARE, GL_DONT_CARE, GL_DONT_CARE, 0, null, 1);
        control(GL_DONT_CARE, GL_DONT_CARE, GL_DEBUG_SEVERITY_NOTIFICATION, 0, null, 0);
        callback(&OnDebugMessage, null);
        return true;
    }

    [UnmanagedCallersOnly]
    private static void OnDebugMessage(uint source, uint type, uint id, uint severity, int length, byte* msg, void* user)
    {
        _dbgTotal++;
        bool always = type == GL_DEBUG_TYPE_ERROR || type == GL_DEBUG_TYPE_UNDEFINED_BEHAVIOR;
        if (!always)
        {
            for (int i = 0; i < _dbgN; i++) if (_dbgId[i] == id) return;
            if (_dbgN >= DebugIds) { _dbgDropped++; return; }
            _dbgId[_dbgN++] = id;
        }
        if (_dbgLogged >= DebugLines) { _dbgDropped++; return; }
        _dbgLogged++;
        string kind = type switch
        {
            GL_DEBUG_TYPE_ERROR => "ERROR", GL_DEBUG_TYPE_UNDEFINED_BEHAVIOR => "UNDEFINED BEHAVIOUR",
            GL_DEBUG_TYPE_DEPRECATED_BEHAVIOR => "deprecated", GL_DEBUG_TYPE_PORTABILITY => "PORTABILITY",
            GL_DEBUG_TYPE_PERFORMANCE => "performance", _ => "other",
        };
        _log?.Info($"GlContextService: [gldebug] {kind} id {id} (source 0x{source:x}, severity 0x{severity:x}): {CStr(msg, 1024)}");
    }

    private static string CStr(byte* p, int max)
    {
        if (p == null) return "<null>";
        int len = 0; while (p[len] != 0 && len < max) len++;
        return System.Text.Encoding.UTF8.GetString(p, len).Replace('\n', ' ');
    }

    private static string CStr(byte* p)
    {
        if (p == null) return "<null>";
        int len = 0; while (p[len] != 0 && len < 256) len++;
        return System.Text.Encoding.ASCII.GetString(p, len);
    }
}
