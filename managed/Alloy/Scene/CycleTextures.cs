using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>#331: the cycle-texture tick the M4 shim removed. A <c>.txi proceduretype cycle</c> texture is
/// advanced by <c>CycleTIDTextureController::Control</c> @0x4d6c90, which rewrites the GL name INSIDE the
/// shared basic (<c>basic+0x6c</c>) -- the <c>CAurTexture*</c> a material hands out never changes, so no
/// pointer compare can see a cycle. Vanilla reaches Control only from the tail of <c>CAurTexture::Bind</c> /
/// <c>BindInUnit</c>, inside the engine bucket walk Alloy no longer performs. So Alloy calls it: a fixed table
/// of the controllers behind owned records, ticked once per frame inside the owned bracket. Control latches on
/// <c>texcurrentframe</c> itself, so a second tick in the same frame (the engine's overlay replay) is a no-op.
/// <para>Only the CycleTID class is ticked: the other controllers (wave, perlin, water, life, arturo)
/// allocate and re-upload pixels, which is not a name rewrite and not this table's business.</para>
/// <para>Lifetime (CX-25): an entry dies with its texture ref, told by the <c>~CAurTexture</c> death notice --
/// a vtable guard cannot see a freed block the next texture re-used. A live ref holds a reference on its
/// basic (the dtor releases <c>+0x18</c>), so the basic and its controller outlive the entry. The per-tick
/// re-guard stays for a controller swapped under a live ref, and the table is cleared with the ownership
/// table.</para></summary>
internal static unsafe class CycleTextures
{
    /// <summary>ponytail: a fixed table; an area has a handful of cycle textures (torches, waterfalls).
    /// Overflow is counted, not grown -- raise the cap if `cycle=` ever reports it.</summary>
    private const int Cap = 1024;
    private static readonly IntPtr[] _ctl = new IntPtr[Cap], _ref = new IntPtr[Cap];
    private static readonly IntPtr[] _dirty = new IntPtr[Cap];
    private static int _n, _dirtyN;
    private static readonly object _lock = new();
    private static AurTextureGetControllerFn _getController;
    private static IntPtr _vtbl;
    private static int* _enabled;
    private static AurTextureDtorFn _origDtor;
    /// <summary>Name steps observed (the live-confirm number), registrations refused by the cap, and
    /// controllers of another class seen on an owned diffuse (the hole this table does not fill).</summary>
    internal static long Steps, Overflow, OtherClass;

    internal static bool Ready => _getController != null && _vtbl != IntPtr.Zero && _origDtor != null;

    /// <summary>#456: the controller layout probe's one-shot latch, and the log it reports through.</summary>
    private static bool _probedCtl;
    private static ExtenderLog? _log;

    internal static void Attach(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (Ready) return;
        // CX-25: no death notice, no table -- a ref kept without one is the crash this hook exists for.
        _origDtor = hooks.InstallAurTextureDtor(&OnTextureDtor);
        _getController = EngineFn.AurTextureGetController;
        _vtbl = EngineAbi.Vtbl(Native.Sym(SymbolMap.CycleTidControllerVtbl));
        _enabled = (int*)Native.Sym(SymbolMap.EnableTextureControllers);
        if (!Ready) log.Info("CycleTextures: AurTextureGetController, the CycleTID vtable or ~CAurTexture unresolved -- cycle textures stay frozen on Alloy parts");
    }

    /// <summary>CX-25: the death notice. Compares the dying pointer, never dereferences it. Any thread;
    /// allocation-free (it fires in bulk on the area edge).</summary>
    [UnmanagedCallersOnly]
    private static void OnTextureDtor(IntPtr tex)
    {
        Forget(tex);
        _origDtor(tex);
    }

    /// <summary>Null the ref of every entry on <paramref name="tex"/>. A tombstone, not a removal: the next
    /// <see cref="Tick"/> drops a null ref through its own guard and reports the controller in
    /// <see cref="Dropped"/>, and a notice fired from inside Tick (an engine call freeing a texture on the
    /// same thread, the lock is re-entrant) cannot reshuffle the table under the loop.</summary>
    private static void Forget(IntPtr tex)
    {
        lock (_lock)
            for (int i = 0; i < _n; i++)
                if (_ref[i] == tex) _ref[i] = IntPtr.Zero;
    }

    /// <summary>The CycleTID controller behind this texture ref, registered for ticking, or zero when it has
    /// none. Cold: called from the record's texture re-resolve only. Any thread.</summary>
    internal static IntPtr Register(IntPtr texRef)
    {
        if (!Ready || !AurTex.IsLive(texRef)) return IntPtr.Zero;
        IntPtr ctl = _getController(texRef);
        if (ctl == IntPtr.Zero) return IntPtr.Zero;
        if (*(IntPtr*)ctl != _vtbl) { OtherClass++; return IntPtr.Zero; }
        lock (_lock)
        {
            // a dedup hit refreshes the ref: the one on file may die (a re-skin) while the basic lives on
            for (int i = 0; i < _n; i++) if (_ctl[i] == ctl) { _ref[i] = texRef; return ctl; }
            if (_n == Cap) { Overflow++; return IntPtr.Zero; }
            _ctl[_n] = ctl; _ref[_n] = texRef; _n++;
        }
        return ctl;
    }

    /// <summary>Once per frame, main thread, inside the bracket: tick every controller and collect the ones
    /// whose name stepped. An entry whose ref died is dropped and reported through <see cref="Dropped"/>, so
    /// the records behind it re-resolve and re-register with a live ref. Honours
    /// <c>enabletexturecontrollers</c>. Allocation-free.</summary>
    internal static int Tick()
    {
        _dirtyN = 0; _droppedN = 0;
        if (!Ready || (_enabled != null && *_enabled == 0)) return 0;
        lock (_lock)
        {
            for (int i = 0; i < _n; i++)
            {
                IntPtr r = _ref[i], ctl = _ctl[i];
                if (!AurTex.IsLive(r) || _getController(r) != ctl || *(IntPtr*)ctl != _vtbl)
                {
                    _n--; _ctl[i] = _ctl[_n]; _ref[i] = _ref[_n]; i--;
                    if (_droppedN < Cap) _dropped[_droppedN++] = ctl;
                    continue;
                }
                AlloyProbe.Once(ref _probedCtl, "CycleTIDTextureController", ctl, _log);
                int old = AurTex.Tid(r);
                new CycleTIDTextureController(ctl).Control();
                if (_ref[i] == r && AurTex.Tid(r) != old) { _dirty[_dirtyN++] = ctl; Steps++; }   // r may have died inside Control
            }
        }
        return _dirtyN;
    }

    private static readonly IntPtr[] _dropped = new IntPtr[Cap];
    private static int _droppedN;
    /// <summary>Controllers dropped by the last <see cref="Tick"/>; the caller un-registers its records.</summary>
    internal static ReadOnlySpan<IntPtr> Dropped => new(_dropped, 0, _droppedN);

    internal static int Count => _n;

    internal static void Clear() { lock (_lock) { _n = 0; _dirtyN = 0; _droppedN = 0; Overflow = 0; } }

    /// <summary>CX-25, off-device: a freed ref is tombstoned (which Tick's guard drops) and a ref that did
    /// not die stays. Synthetic pointers, nothing dereferenced; runs at init on the empty table.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok;
        lock (_lock)
        {
            if (_n + 2 > Cap) return true;
            int n = _n;
            _ctl[n] = 0x1000; _ref[n] = 0x2000; _ctl[n + 1] = 0x3000; _ref[n + 1] = 0x4000; _n = n + 2;
            Forget(0x2000);
            ok = _ref[n] == IntPtr.Zero && _ctl[n] == 0x1000 && _ref[n + 1] == 0x4000;
            _n = n;
        }
        log.Info(ok ? "CycleTextures.SelfCheck: passed" : "CycleTextures.SelfCheck: FAILED");
        return ok;
    }
}
