using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The area's environment map -- what water and every shiny material reflect -- chosen by ALLOY: the area's
/// tileset names a resref (<c>CNWTileSet::GetEnvMapResRef</c> @0x5e10a0), else the engine's <c>chrome1</c>
/// fallback @0x48e430. The <c>CNWTileSetManager::GetTileSet</c> hook only COPIES the 17-byte name, being deep
/// in the area load; <see cref="Present"/> acquires the texture, holds it for the area, drops it at the next adopt.
/// </summary>
internal static unsafe class AlloyEnvMap
{
    /// <summary>A <c>CResRef</c> is 16 characters and a NUL: <c>GetEnvMapResRef</c> copies 16 bytes plus the
    /// terminator byte, and <c>Scene::Render</c>'s own <c>__strcpy_chk</c> bound is 0x11.</summary>
    private const int ResRefBytes = 17;
    /// <summary>The engine's fallback, read off <c>AurTextureGetFallbackEnvironmentMap</c> @0x48e430: it
    /// caches <c>AurTextureGet("chrome1", 0, "chrome1", 0, 0, false)</c> in
    /// <c>CAurTextureBasic::m_pFallbackEnvMapTexture</c>. Alloy takes the same resref through its own load
    /// rather than calling that accessor, so the choice -- not just the texture -- is Alloy's.</summary>
    private const string FallbackResref = "chrome1";
    /// <summary>Chained ABOVE ModelLoad's subscriber (which has no priority of its own) and Weather's 100:
    /// this only reads, so it wants to see the tileset whatever the others do with the call.</summary>
    private const int TileSetPriority = 120;

    private static ExtenderLog? _log;
    private static TileSetGetFn* _tileSetSlot;
    /// <summary>The name in force, held in the texture registry until the next adopt releases it.</summary>
    private static TexId _tex;
    private static byte* _resref;      // ResRefBytes of native memory: the name in force
    private static byte* _wanted;      // ...and what the last area load asked for (empty = the fallback)
    private static byte* _fallback;    // ...and the engine's own fallback name, zero-filled once
    private static bool _dirty, _armed;
    /// <summary>The name in force did not resolve. Latched so a missing resref is not probed every frame, and
    /// cleared at every area edge, which is the only thing that can change the answer.</summary>
    private static bool _missing, _onFallback;
    private static long _adopts, _fallbacks, _failures;


    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        ViewKnobs.Touch();
        if (_resref == null) _resref = (byte*)NativeMemory.AllocZeroed(ResRefBytes);
        if (_wanted == null) _wanted = (byte*)NativeMemory.AllocZeroed(ResRefBytes);
        if (_fallback == null)
        {
            // AllocZeroed, and the whole buffer is compared and copied as a unit: a stack buffer with only
            // "chrome1\0" written left 9 bytes of junk behind the terminator, so the same name never compared
            // equal to itself and every area re-adopted it.
            _fallback = (byte*)NativeMemory.AllocZeroed(ResRefBytes);
            System.Text.Encoding.ASCII.GetBytes(FallbackResref, new Span<byte>(_fallback, ResRefBytes - 1));
        }
    }

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!EngineLayout.Require(log, "AlloyEnvMap", "CNWTileSet")) return;
        if (EngineFn.TileSetGetEnvMapResRef == null)
        {
            log.Always("AlloyEnvMap: CNWTileSet::GetEnvMapResRef unresolved -- shiny surfaces and water have "
                       + "NO environment map this session");
            return;
        }
        _tileSetSlot = hooks.ChainTileSetGet(&OnGetTileSet, TileSetPriority);
        if (_tileSetSlot == null)
        {
            log.Always("AlloyEnvMap: CNWTileSetManager::GetTileSet did not chain -- CX cannot see which "
                       + "tileset an area loads, so shiny surfaces and water have NO environment map this session");
            return;
        }
        _armed = true;
        // The fallback is a valid answer before any area has loaded (the main menu's vista), so ask for it now.
        Want(null);
        log.Info("AlloyEnvMap: armed -- the area's environment map and its chrome1 fallback are CX's choice");
    }

    /// <summary>The area load's one ask for its tileset. Cold -- once per area -- and it only copies the
    /// resref: see the class remarks for why the texture is not taken here.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnGetTileSet(IntPtr mgr, IntPtr resref)
    {
        IntPtr tileset = _tileSetSlot != null ? (*_tileSetSlot)(mgr, resref) : IntPtr.Zero;
        if (_armed && tileset != IntPtr.Zero)
        {
            byte* name = stackalloc byte[ResRefBytes + 1];
            // GetEnvMapResRef writes all 17 bytes of the CResRef; the extra one guarantees a terminator even
            // for a 16-character name the engine left unterminated.
            new CNWTileSet(tileset).TileSetGetEnvMapResRef(name);
            name[ResRefBytes] = 0;
            Want(name);
        }
        return tileset;
    }

    /// <summary>Note the resref the next <see cref="Present"/> should adopt, or null for the fallback; nothing
    /// is looked up here. ALWAYS dirty, even for the name already in force: an area edge is what clears the
    /// "this name does not resolve" latch, and two adjacent areas sharing a tileset must still re-adopt.</summary>
    private static void Want(byte* name)
    {
        for (int i = 0; i < ResRefBytes; i++) _wanted[i] = name != null ? name[i] : (byte)0;
        _wanted[ResRefBytes - 1] = 0;
        _dirty = true;
    }

    /// <summary>Take <see cref="_wanted"/> (or the engine's fallback name when it is empty) as the name in
    /// force, and clear the refusal latch. Cold: once per area edge.</summary>
    private static void AdoptWanted()
    {
        Choose();
        Hold();
    }

    /// <summary><see cref="AdoptWanted"/>'s bookkeeping half: the name and the latches, no texture touched.</summary>
    private static void Choose()
    {
        bool fallback = _wanted[0] == 0;
        Copy(fallback ? _fallback : _wanted);
        _onFallback = fallback;
        _missing = false;
        _dirty = false;
        _adopts++;
        if (fallback) _fallbacks++;
    }

    /// <summary>Once per world frame, before anything samples it: adopt a new area's name if one arrived, and
    /// fall back to <c>chrome1</c> when the tileset's name fails to load, as <c>Scene::Render</c> @0x4762d0
    /// does. Both failing is said once; the next area edge lifts the latch.</summary>
    private static void Step()
    {
        if (!_armed) return;
        if (_dirty) AdoptWanted();
        if (_missing || !TexRegistry.Final(_tex)) return;
        if (!_onFallback)
        {
            Copy(_fallback);
            Hold();
            _onFallback = true;
            _fallbacks++;
            return;
        }
        _missing = true;
        _failures++;
        _log?.Always($"AlloyEnvMap: neither the tileset's environment map nor {FallbackResref} loaded -- "
                     + "shiny surfaces and water have NO environment map this area");
    }

    private static void Copy(byte* name)
    {
        for (int i = 0; i < ResRefBytes; i++) _resref[i] = name[i];
    }

    /// <summary>Acquire the name in force from the registry and release the one it replaces.</summary>
    private static void Hold()
    {
        TexId was = _tex;
        _tex = _resref[0] == 0 ? default : TexRegistry.Acquire(System.Text.Encoding.ASCII.GetString(_resref, Len(_resref)));
        if (!was.IsNull) TexRegistry.Release(was);
    }

    /// <summary>The GL texture for the environment map this frame, or 0 while it is loading or missing.</summary>
    internal static int Tid() => TexRegistry.TryDrawable(_tex, out uint name, out _) ? (int)name : 0;

    /// <summary>Is there an environment map at all this frame? The shader reads it as a switch, so a missing
    /// one must leave the surface shaded rather than multiplied by black. Steps the choice first: this is
    /// the frame's first reader.</summary>
    internal static bool Present()
    {
        Step();
        return Tid() != 0;
    }

    /// <summary>The world went away -- a part-table wipe, an area change or a module unload. The name is
    /// re-adopted at the next frame, which also lifts the "does not load" latch.</summary>
    internal static void Revoke() => _dirty = true;

    /// <inheritdoc cref="Revoke"/>
    internal static void ModuleUnloaded() => Revoke();

    private static bool Same(byte* a, byte* b)
    {
        for (int i = 0; i < ResRefBytes; i++) if (a[i] != b[i]) return false;
        return true;
    }

    internal static string Describe()
    {
        if (!_armed) return "NONE (unarmed)";
        string name = _resref != null && _resref[0] != 0
            ? System.Text.Encoding.ASCII.GetString(_resref, Len(_resref)) : "(none)";
        string s = $"on, {name}{(_onFallback ? " (fallback)" : "")}";
        if (_missing) s += " NOT RESOLVED";
        else if (Tid() == 0) s += " loading";
        s += $", {_adopts} area adopts";
        if (_fallbacks > 0) s += $", {_fallbacks} fallbacks";
        if (_failures > 0) s += $", {_failures} FAILURES";
        return s;
    }

    private static int Len(byte* s)
    {
        int n = 0;
        while (n < ResRefBytes - 1 && s[n] != 0) n++;
        return n;
    }

    /// <summary>Off-device: the fallback rule and the resref bookkeeping, over native memory only.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyEnvMap.SelfCheck");
        Attach(log);
        bool armed = _armed;
        byte* a = stackalloc byte[ResRefBytes];
        byte* b = stackalloc byte[ResRefBytes];
        for (int i = 0; i < ResRefBytes; i++) { a[i] = 0; b[i] = 0; }
        check.Expect(Same(a, b), "two empty resrefs compare equal");
        a[0] = (byte)'x';
        check.Expect(!Same(a, b), "...and one character apart do not");

        // The engine's fallback name must be a clean 17 bytes, or it never compares equal to itself and every
        // area re-adopts it (the bug the stack buffer had).
        check.Expect(_fallback[7] == 0, "the fallback name is NUL-terminated");
        bool clean = true;
        for (int i = 8; i < ResRefBytes; i++) if (_fallback[i] != 0) clean = false;
        check.Expect(clean, "...and zero-filled past the terminator, so Same() is reflexive on it");

        _armed = true;
        bool onFb = _onFallback; bool miss = _missing; bool dirty0 = _dirty;
        byte* other = stackalloc byte[ResRefBytes];
        for (int i = 0; i < ResRefBytes; i++) other[i] = 0;
        other[0] = (byte)'t'; other[1] = (byte)'s';

        // An area edge ALWAYS asks for a re-adopt -- including one whose tileset names the name already in
        // force, which is the case that let a stale pointer survive a transition.
        Want(other);
        check.Expect(_dirty, "an area edge asks for a re-adopt");
        check.Expect(_wanted[0] == (byte)'t' && _wanted[1] == (byte)'s' && _wanted[2] == 0,
                     "...and the name is copied, NUL included");
        Choose();
        check.Expect(!_dirty && !_onFallback && !_missing && Same(_resref, _wanted),
                     "adopting takes the tileset's own name and lifts the refusal latch");
        Want(other);
        check.Expect(_dirty, "the SAME name at the next area edge still asks for a re-adopt");

        // An empty name is the engine's "this tileset declares none" -- the fallback, not a hole.
        Want(null);
        Choose();
        check.Expect(_onFallback && Same(_resref, _fallback), "an empty tileset name adopts chrome1");
        check.Expect(_wanted[0] == 0, "...without pretending the tileset asked for it: the wanted name stays empty");

        // A refusal latches and a world edge lifts it -- otherwise a missing resref is probed every frame.
        _missing = true;
        Revoke();
        Choose();
        check.Expect(!_missing, "a world edge lifts the refusal latch");

        _onFallback = onFb; _missing = miss; _dirty = dirty0;
        Want(null);
        _armed = armed;
        log.Info(check.Ok ? "AlloyEnvMap.SelfCheck: passed (resref bookkeeping, fallback rule)"
                          : "AlloyEnvMap.SelfCheck: FAILED");
    }
}
