using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// CX-110: the area's environment map -- the reflection vanilla's water and every shiny material sample -- is
/// ALLOY's choice and Alloy's fallback.
/// <para><c>Scene::Render</c> @0x4762d0 picks it as: the module's current area's tileset names a resref
/// (<c>CNWTileSet::GetEnvMapResRef</c> @0x5e10a0, a 17-byte <c>CResRef</c> at <c>CNWTileSet+0x32</c>), else
/// <c>AurTextureGetFallbackEnvironmentMap</c> @0x48e430, which is <c>chrome1</c>. Alloy makes the same choice
/// from the tileset the area load hands it, keeps the answer, and never reads
/// <c>Scene+0x1e8</c>.</para>
/// </summary>
/// <remarks>
/// <para><b>The ticket's premise was half wrong and it matters.</b> The engine does not resolve this every
/// frame: the whole block is gated on <c>Scene+0x1e8 == 0</c>, so it runs once per Scene and again only when
/// the <c>SafePointer</c> drops (the texture died). What runs per frame is the BIND. So "move the per-frame
/// lookup into Alloy" is really "own the choice and the fallback", which is what this does.</para>
/// <para><b>Where the tileset comes from.</b> The <c>CNWTileSetManager::GetTileSet</c> chain, which CX already
/// subscribes to twice (<see cref="ModelLoad"/>, <see cref="Weather"/>) and which <c>CNWCArea::LoadArea</c>
/// calls exactly once per area load. That avoids needing <c>CNWCArea+0x290</c> at all, and it is the natural
/// invalidation edge: a new area means a new resref.</para>
/// <para><b>Two-step on purpose.</b> The hook only COPIES the resref -- 17 bytes of data with no lifetime --
/// because it runs deep inside the area load. The texture is looked up in <see cref="Tick"/>, once per world
/// frame in ENGINE context ahead of the frame anchor, because the lookup can BUILD a texture and building one
/// inside Alloy's 4.6 bracket would upload it into the wrong context (<c>engine-state-cache-traps</c>).</para>
/// <para><b>What Alloy owns is the resref, never the pointer.</b> <c>AurTextureGet</c> returns a BORROWED
/// <c>CAurTexture*</c> and the engine frees AND RELOADS its textures on an area transition as well as on a
/// module unload (agent memory <c>aurtexture-lifetime</c>, a twice-cored crash). A pointer cached across one
/// of those both dangles and names the wrong object, and taking a reference over it does not help: the
/// release that would pair with it cannot be made safely once the object is gone. So the first cut of this
/// class -- <c>LoadTexture</c> once per area, held -- was wrong twice over, and the fix is the rule that
/// already applies everywhere else in Alloy: keep the NAME, re-fetch the pointer every world frame, and let
/// <see cref="AurTex.Tid"/> refuse a stale one. The lookup is a resref-keyed resman hit; nothing is
/// allocated and no reference is taken, so there is nothing to release.</para>
/// </remarks>
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
    /// <summary>THIS frame's borrowed <c>CAurTexture*</c>. Written once per world frame by <see cref="Tick"/>
    /// and read only inside that frame -- see the class remarks for why it is never carried across one.</summary>
    private static IntPtr _tex;
    private static byte* _resref;      // ResRefBytes of native memory: the name in force
    private static byte* _wanted;      // ...and what the last area load asked for (empty = the fallback)
    private static byte* _fallback;    // ...and the engine's own fallback name, zero-filled once
    private static bool _dirty, _armed;
    /// <summary>The name in force did not resolve. Latched so a missing resref is not probed every frame, and
    /// cleared at every area edge, which is the only thing that can change the answer.</summary>
    private static bool _missing, _onFallback;
    private static long _adopts, _fallbacks, _failures;

    /// <summary>Alloy's choice is driving. Off sends every reader back to <c>Scene+0x1e8</c>.</summary>
    internal static bool Owned => _armed && ViewKnobs.EnvMap.V;

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
        if (EngineFn.AurTextureGet == null || EngineFn.TileSetGetEnvMapResRef == null)
        {
            log.Always("AlloyEnvMap: AurTextureGet or CNWTileSet::GetEnvMapResRef unresolved "
                       + "-- the environment map stays the ENGINE's choice (Scene+0x1e8)");
            return;
        }
        _tileSetSlot = hooks.ChainTileSetGet(&OnGetTileSet, TileSetPriority);
        if (_tileSetSlot == null)
        {
            log.Always("AlloyEnvMap: CNWTileSetManager::GetTileSet did not chain -- CX cannot see which "
                       + "tileset an area loads, so the environment map stays the ENGINE's choice");
            return;
        }
        _armed = true;
        // The fallback is a valid answer before any area has loaded (the main menu's vista), so ask for it now.
        Want(null);
        AdoptWanted();
        log.Info("AlloyEnvMap: armed -- the area's environment map and its chrome1 fallback are CX's choice "
                 + "(view.envmap 0 reads the engine's Scene+0x1e8 instead)");
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

    /// <summary>Note the resref the next <see cref="Tick"/> should adopt, or null for the fallback. Nothing is
    /// looked up here.
    /// <para>ALWAYS dirty, even for a name identical to the one in force: an area edge is what clears the
    /// "this name does not resolve" latch, and two adjacent areas sharing a tileset must still re-adopt (the
    /// first cut compared the names and skipped, which is how a stale answer survived a transition).</para></summary>
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
        bool fallback = _wanted[0] == 0;
        byte* src = fallback ? _fallback : _wanted;
        for (int i = 0; i < ResRefBytes; i++) _resref[i] = src[i];
        _onFallback = fallback;
        _missing = false;
        _dirty = false;
        _adopts++;
        if (fallback) _fallbacks++;
    }

    /// <summary>Once per world frame, from <see cref="RspShim.Run"/>, in ENGINE context and ahead of the frame
    /// anchor: adopt a new area's name if one arrived, then FETCH this frame's pointer. The fetch is a
    /// resref-keyed resman hit and takes no reference -- see the class remarks for why it is per frame.</summary>
    internal static void Tick(IntPtr scene)
    {
        if (!_armed) return;
        if (_dirty) AdoptWanted();
        _tex = _missing ? IntPtr.Zero : Fetch(_resref);
        if (_tex == IntPtr.Zero && !_onFallback)
        {
            // The engine's own order: the tileset's name first, chrome1 second. A tileset that names nothing
            // and a name that does not resolve take the same branch, which is what vanilla does.
            for (int i = 0; i < ResRefBytes; i++) _resref[i] = _fallback[i];
            _onFallback = true;
            _fallbacks++;
            _tex = Fetch(_resref);
        }
        if (_tex != IntPtr.Zero || _missing) return;
        // CX-45: the chosen name and the engine's own fallback both refused. Say it once and stop probing; the
        // next area edge lifts the latch.
        _missing = true;
        _failures++;
        _log?.Always($"AlloyEnvMap: neither the tileset's environment map nor {FallbackResref} resolved -- "
                     + "shiny surfaces and water have NO reflection this area (view.envmap 0 for the engine's)");
    }

    /// <summary>The engine's resref-keyed lookup, exactly as <c>AurTextureGetFallbackEnvironmentMap</c>
    /// @0x48e430 makes it: name in both slots, no out parameter, no flags, no reference taken.</summary>
    private static IntPtr Fetch(byte* name)
        => name[0] == 0 ? IntPtr.Zero : EngineFn.AurTextureGet(name, IntPtr.Zero, name, null, 0, 0);

    /// <summary>The GL texture id for this frame's environment map, or 0 for none. The pointer behind it was
    /// fetched by THIS frame's <see cref="Tick"/>, and <see cref="AurTex.Tid"/> validates it anyway.</summary>
    internal static int Tid(IntPtr scene)
    {
        if (!Owned) return scene == IntPtr.Zero ? 0 : AurTex.Tid(new CX.Engine.Scene(scene).EnvMap.Ptr);
        return _tex == IntPtr.Zero ? 0 : AurTex.Tid(_tex);
    }

    /// <summary>Is there an environment map at all? The shader reads it as a switch, so a missing one must
    /// leave the surface shaded rather than multiplied by black.</summary>
    internal static bool Present(IntPtr scene)
    {
        if (!Owned) return scene != IntPtr.Zero && !new CX.Engine.Scene(scene).EnvMap.IsNull;
        return _tex != IntPtr.Zero;
    }

    /// <summary>The world went away -- a part-table wipe, an area change or a module unload. The pointer is
    /// dropped (never released: nothing was ever taken) and the name re-adopted, which also lifts the
    /// "does not resolve" latch. The RESREF survives, so the next world frame gets the same choice back.
    /// <para>Wired to BOTH the pass roll-call's world edge and <see cref="AlloyRenderer.EndFrame"/>'s module
    /// edge. Neither is strictly required now that the pointer is re-fetched per frame -- what they buy is the
    /// retry of a name that did not resolve, and belt-and-braces against a cached pointer creeping back
    /// in.</para></summary>
    internal static void Revoke()
    {
        _tex = IntPtr.Zero;
        _dirty = true;
    }

    /// <inheritdoc cref="Revoke"/>
    internal static void ModuleUnloaded() => Revoke();

    private static bool Same(byte* a, byte* b)
    {
        for (int i = 0; i < ResRefBytes; i++) if (a[i] != b[i]) return false;
        return true;
    }

    internal static string Describe()
    {
        if (!_armed) return "engine's (Scene+0x1e8)";
        if (!ViewKnobs.EnvMap.V) return "off -- engine's Scene+0x1e8";
        string name = _resref != null && _resref[0] != 0
            ? System.Text.Encoding.ASCII.GetString(_resref, Len(_resref)) : "(none)";
        string s = $"on, {name}{(_onFallback ? " (fallback)" : "")}";
        if (_missing) s += " NOT RESOLVED";
        else if (_tex == IntPtr.Zero) s += " not fetched yet";
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
        AdoptWanted();
        check.Expect(!_dirty && !_onFallback && !_missing && Same(_resref, _wanted),
                     "adopting takes the tileset's own name and lifts the refusal latch");
        Want(other);
        check.Expect(_dirty, "the SAME name at the next area edge still asks for a re-adopt");

        // An empty name is the engine's "this tileset declares none" -- the fallback, not a hole.
        Want(null);
        AdoptWanted();
        check.Expect(_onFallback && Same(_resref, _fallback), "an empty tileset name adopts chrome1");
        check.Expect(Same(_wanted, _resref) == false || _wanted[0] == 0, "...without pretending the tileset asked for it");
        check.Expect(FallbackResref == "chrome1", "the fallback is the engine's own chrome1");

        // A refusal latches and a world edge lifts it -- otherwise a missing resref is probed every frame.
        _missing = true;
        Revoke();
        AdoptWanted();
        check.Expect(!_missing, "a world edge lifts the refusal latch");
        check.Expect(_tex == IntPtr.Zero, "...and drops the frame's pointer (nothing to release: none was taken)");

        _onFallback = onFb; _missing = miss; _dirty = dirty0;
        _tex = IntPtr.Zero;
        Want(null);
        _armed = armed;
        log.Info(check.Ok ? "AlloyEnvMap.SelfCheck: passed (resref bookkeeping, fallback rule)"
                          : "AlloyEnvMap.SelfCheck: FAILED");
    }
}
