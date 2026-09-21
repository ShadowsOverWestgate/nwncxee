using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy;

/// <summary>#275: <c>.mtr</c> texture slots past the engine's ceiling. <c>SharedMaterial::ParseField</c>
/// @0x4c6760 is a flat strcasecmp chain ending at <c>texture10</c>, and the storage behind it is inline and
/// fixed (eleven 17-byte names at shared+0x18, eleven SafePointers at shared+0xd8) -- a <c>texture11</c> line
/// is parsed, matches nothing and is dropped. So the slots live BESIDE the object: a table keyed on the
/// <c>SharedMaterial*</c>, filled by a hook under ParseField, loaded after <c>Init</c> (where the engine
/// loads its own eleven), evicted by the destructor.
/// <para>Why: the community uses texture6..texture10 per model; Alloy's own conventions (the AO map) must sit
/// where a pre-Alloy model cannot already have put something, and where a stock client sees nothing at all.
/// The flow map stays on texture6 -- that is the flowmap SHADER's contract (its header says so), vanilla reads
/// it too, and a part only opts in by naming the shader.</para>
/// <para>Lifetime: <c>LoadTexture</c> @0x4c50f0 is <c>AurTextureGetReference</c>, an ADDREF'd reference the
/// engine's own slots release with <c>AurTextureRelease</c> in <c>~SharedMaterial</c> @0x4c5260 -- so does this
/// table, on the same edge. The engine's SafePointers are additionally nulled when a texture dies out from
/// under them; a raw pointer here is not, so a read goes through <see cref="AurTex"/>'s vtable guard and a
/// release is skipped for a pointer that is no longer a texture.</para>
/// <para>ponytail: one global lock. ParseField/Init/dtor are load-edge cold; the read is per owned part per
/// colour-cadence frame off the stage workers (ADR 0022), uncontended in practice.</para></summary>
public sealed unsafe class MaterialExtraSlots : IExtenderService
{
    /// <summary>First extender-owned slot: <c>texture11</c>, the one past the engine's <c>texture10</c>.</summary>
    internal const int First = 11;
    /// <summary>How many: texture11..texture14. ponytail: raise the constant when a fourth Alloy map exists.</summary>
    internal const int Count = 4;
    /// <summary>16 resref bytes + NUL, the engine's own buffer size.</summary>
    private const int NameLen = 17;

    /// <summary>One material's extra slots: names as parsed, pointers as loaded (<c>long</c> because a fixed
    /// buffer cannot hold <c>IntPtr</c>). Native-allocated so the buffers are addressable.</summary>
    private struct Entry
    {
        public fixed byte Names[Count * NameLen];
        public fixed long Tex[Count];
    }

    /// <summary>Native, so the fixed buffers are addressable; freed by <see cref="Forget"/>.</summary>
    private static readonly Dictionary<IntPtr, IntPtr> _table = new();
    private static readonly object _lock = new();
    private static delegate* unmanaged<IntPtr, byte*, void> _origParse;
    private static delegate* unmanaged<IntPtr, byte*, void> _origInit;
    private static delegate* unmanaged<IntPtr, uint, IntPtr> _origDtor;
    private static delegate* unmanaged<byte*, IntPtr> _loadTexture;
    private static delegate* unmanaged<IntPtr, void> _release;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        SelfCheck(log);
        _loadTexture = (delegate* unmanaged<byte*, IntPtr>)Native.Sym(SymbolMap.LoadTexture);
        _release = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.AurTextureRelease);
        if (_loadTexture == null || _release == null)
        { log.Info("MaterialExtraSlots: LoadTexture/AurTextureRelease unresolved -- texture11+ stand down"); return; }
        // The death notice FIRST: without it the table would fill and hold ADDREF'd textures nobody evicts,
        // so neither of the other two hooks may install unless this one did.
        _origDtor = (delegate* unmanaged<IntPtr, uint, IntPtr>)hooks.Install(
            SymbolMap.SharedMaterialDtor, (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr>)&OnDtor);
        if (_origDtor == null)
        { log.Info("MaterialExtraSlots: ~SharedMaterial unhooked -- texture11+ stand down rather than leak"); return; }
        _origParse = (delegate* unmanaged<IntPtr, byte*, void>)hooks.Install(
            SymbolMap.SharedMaterialParseField, (IntPtr)(delegate* unmanaged<IntPtr, byte*, void>)&OnParseField);
        _origInit = (delegate* unmanaged<IntPtr, byte*, void>)hooks.Install(
            SymbolMap.SharedMaterialInit, (IntPtr)(delegate* unmanaged<IntPtr, byte*, void>)&OnInit);
        log.Info(_origParse != null && _origInit != null
            ? $"MaterialExtraSlots: armed -- texture{First}..texture{First + Count - 1} are extender-owned"
            : "MaterialExtraSlots: ParseField/Init unhooked -- texture11+ read as empty");
    }

    /// <summary>The extra slot's texture, or null. <paramref name="n"/> is the <c>.mtr</c> index
    /// (<c>texture11</c> = 11). Borrowed like every material texture: guard before dereferencing.</summary>
    internal static TextureRef Texture(IntPtr shared, int n)
    {
        int s = n - First;
        if (shared == IntPtr.Zero || (uint)s >= Count) return new(IntPtr.Zero);
        lock (_lock)
            return _table.TryGetValue(shared, out var e) ? new((IntPtr)((Entry*)e)->Tex[s]) : new(IntPtr.Zero);
    }

    [UnmanagedCallersOnly]
    private static void OnParseField(IntPtr shared, byte* line)
    {
        _origParse(shared, line);
        if (line != null) Note(shared, line);
    }

    [UnmanagedCallersOnly]
    private static void OnInit(IntPtr shared, byte* name)
    {
        _origInit(shared, name);
        Load(shared);
    }

    /// <summary>Linux <c>D2</c> ignores the second argument and returns nothing; Windows is the scalar deleting
    /// destructor (<c>this</c>, flags) returning <c>this</c>. One handler forwards both faithfully.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnDtor(IntPtr shared, uint flags)
    {
        Forget(shared);
        return _origDtor(shared, flags);
    }

    /// <summary>One <c>.mtr</c> line after the engine has had it: <c>textureN value</c> with N in range is
    /// kept, anything else (including N the engine owns) is ignored. Cold; allocates the entry.</summary>
    internal static void Note(IntPtr shared, byte* line)
    {
        byte* p = line;
        while (*p == ' ' || *p == '\t') p++;
        if (!StartsWithTexture(p)) return;
        p += 7;
        int n = 0, digits = 0;
        while (*p >= '0' && *p <= '9' && digits < 3) { n = n * 10 + (*p - '0'); p++; digits++; }
        if (digits == 0 || (*p != 0 && *p != ' ' && *p != '\t' && *p != '\r' && *p != '\n')) return;
        int s = n - First;
        if ((uint)s >= Count) return;
        while (*p == ' ' || *p == '\t') p++;
        lock (_lock)
        {
            if (!_table.TryGetValue(shared, out IntPtr ep))
                _table[shared] = ep = (IntPtr)NativeMemory.AllocZeroed((nuint)sizeof(Entry));
            byte* dst = ((Entry*)ep)->Names + s * NameLen;
            int i = 0;
            // sscanf("%s") shape, as ::Parse(char const*, char*) @0x446740 does it: one whitespace-delimited token.
            while (i < NameLen - 1 && *p > ' ') dst[i++] = *p++;
            dst[i] = 0;
        }
    }

    private static bool StartsWithTexture(byte* p)
    {
        const string k = "texture";
        for (int i = 0; i < k.Length; i++) if ((p[i] | 0x20) != k[i]) return false;
        return true;
    }

    /// <summary>Load every named extra slot, the way <c>SharedMaterial::LoadTextureArray</c> loads the
    /// engine's; a pointer already held is released first, as its SafePointer assignment would.</summary>
    private static void Load(IntPtr shared)
    {
        // Names are copied out so the engine's texture load (disk, NWSync) runs with the lock released: the
        // stage workers take it per owned part, and a load edge must not stall a colour frame.
        byte* names = stackalloc byte[Count * NameLen];
        lock (_lock)
        {
            if (!_table.TryGetValue(shared, out var ep)) return;
            Buffer.MemoryCopy(((Entry*)ep)->Names, names, Count * NameLen, Count * NameLen);
        }
        for (int s = 0; s < Count; s++)
        {
            byte* name = names + s * NameLen;
            if (*name == 0) continue;
            IntPtr tex = _loadTexture(name);
            IntPtr old;
            lock (_lock)
            {
                if (!_table.TryGetValue(shared, out var ep)) { ReleaseIfLive(tex); return; }
                old = (IntPtr)((Entry*)ep)->Tex[s];
                ((Entry*)ep)->Tex[s] = (long)tex;
            }
            ReleaseIfLive(old);
        }
    }

    /// <summary>The death notice: drop the entry and the references it holds. True if there was one.</summary>
    internal static bool Forget(IntPtr shared)
    {
        IntPtr ep;
        lock (_lock) if (!_table.Remove(shared, out ep)) return false;
        var e = (Entry*)ep;
        for (int s = 0; s < Count; s++) ReleaseIfLive((IntPtr)e->Tex[s]);
        NativeMemory.Free(e);
        return true;
    }

    private static void ReleaseIfLive(IntPtr tex)
    {
        if (_release != null && AurTex.IsLive(tex)) _release(tex);
    }

    /// <summary>Name of extra slot <paramref name="n"/> as parsed, for the self-check and diagnostics.</summary>
    internal static string Name(IntPtr shared, int n)
    {
        int s = n - First;
        if ((uint)s >= Count) return "";
        lock (_lock)
        {
            if (!_table.TryGetValue(shared, out var ep)) return "";
            return Marshal.PtrToStringAnsi((IntPtr)(((Entry*)ep)->Names + s * NameLen)) ?? "";
        }
    }

    /// <summary>Off-device: the parser's accept/reject line and the eviction, on synthetic keys.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        IntPtr a = (IntPtr)0x1000, b = (IntPtr)0x2000;
        var check = new Checker(log);
        void Line(IntPtr k, string s) { fixed (byte* p = System.Text.Encoding.ASCII.GetBytes(s + "\0")) Note(k, p); }
        Line(a, "texture11 plc_ao");
        Line(a, "  Texture12\tbar_ao  trailing");
        Line(a, "texture9 engine_owned");
        Line(a, "texture100 too_far");
        Line(a, "texture11x junk");
        Line(a, "texture14 0123456789abcdefXYZ");
        Line(b, "bitmap diffuse");
        check.Expect(Name(a, 11) == "plc_ao", "texture11 value");
        check.Expect(Name(a, 12) == "bar_ao", "case + tabs + trailing words");
        check.Expect(Name(a, 13) == "", "unset slot empty");
        check.Expect(Name(a, 14) == "0123456789abcdef", "resref clipped to 16");
        check.Expect(Name(a, 9) == "" && Name(a, 15) == "", "out-of-range reads empty");
        check.Expect(Texture(a, 11).IsNull, "unloaded slot reads null");
        check.Expect(!Forget(b), "no entry for a material with no extra lines");
        check.Expect(Forget(a) && !Forget(a) && Name(a, 11) == "", "evicted once");
        log.Info(check.Ok ? "MaterialExtraSlots.SelfCheck: passed" : "MaterialExtraSlots.SelfCheck: FAILED");
        return check.Ok;
    }
}
