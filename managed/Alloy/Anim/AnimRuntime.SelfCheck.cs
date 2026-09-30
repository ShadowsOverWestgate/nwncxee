using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// CX-106's off-device suite. Four of the five checks run the SHIPPING code over synthetic engine memory
/// (native allocations at this build's own <see cref="EngineLayout"/> offsets, and a fake vtable whose
/// <c>DoEvent</c> slot is a recorder), so the numbers they assert are the numbers the client will compute.
/// Every expected value below was derived from the disassembly, not from running the code.
/// </summary>
internal static unsafe partial class AnimRuntime
{
    /// <summary>A recorder for the synthetic gob's <c>DoEvent</c> slot: the checks assert on the names and
    /// their order.</summary>
    private static readonly string[] _seen = new string[32];
    private static int _seenCount;
    private static int _freed;

    [UnmanagedCallersOnly]
    private static void CheckDoEvent(IntPtr gob, byte* name, IntPtr data)
    {
        if (_seenCount < _seen.Length) _seen[_seenCount++] = Marshal.PtrToStringUTF8((IntPtr)name) ?? "";
    }

    /// <summary>The order-preserving removal <c>CExoArrayList&lt;AnimRun*&gt;::Remove</c> performs, over the
    /// synthetic list. <paramref name="list"/> is <c>gob + GobAnimRuns</c>, so the count sits one pointer on.</summary>
    [UnmanagedCallersOnly]
    private static void CheckRemove(IntPtr list, IntPtr run)
    {
        IntPtr* data = *(IntPtr**)list;
        int* count = (int*)(list + EngineLayout.GobAnimRunCount - EngineLayout.GobAnimRuns);
        for (int i = 0; i < *count; i++)
            if (data[i] == run)
            {
                for (int j = i; j < *count - 1; j++) data[j] = data[j + 1];
                (*count)--;
                return;
            }
    }

    [UnmanagedCallersOnly]
    private static void CheckDelIndex(IntPtr list, int index)
    {
        IntPtr* data = *(IntPtr**)list;
        int* count = (int*)(list + EngineLayout.GobAnimRunCount - EngineLayout.GobAnimRuns);
        if (index < 0 || index >= *count) return;
        for (int j = index; j < *count - 1; j++) data[j] = data[j + 1];
        (*count)--;
    }

    [UnmanagedCallersOnly]
    private static void CheckFree(IntPtr p) => _freed++;

    /// <summary>An arbitrary part byte offset the scalar check writes through; only its arithmetic matters.</summary>
    private const int ScalarSlot = 64;

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(KeySearch(), "key search: the binary search answers exactly what the engine's linear scan does, on 4000 probes over random tracks");
        check.Expect(SlerpMath(), "slerp: the engine's shortest-arc negation, its two exact escapes, and the halfway rotation of a 90 degree arc");
        check.Expect(ControlBlend(), "controller blend: a float lerps, a vector lerps per component, a rotation slerps, all into the part offset the controller names");
        check.Expect(Ramps(), "queue advance: the fade-in step, the NET fade-out step (dt/fadeTime, not 2x), both zero-fadeTime cases, and the full-weight purge of every older run");
        check.Expect(MorphFrames(), "animmesh morph: the frame index is shared -- the position pool's clamp is what the UV pool then reads");
        check.Expect(EventWindow(), "animation events: the forward window is half-open at the low end, a wrap fires the tail then the head, and reverse walks the events backwards");
        log.Info(check.Ok ? "AnimRuntime.SelfCheck: passed" : "AnimRuntime.SelfCheck: FAILED");
    }

    /// <summary><c>NewController::GetKeyIndexAndAlpha</c> transcribed as the engine writes it -- a linear scan
    /// from key 1 -- so the shipping binary search can be compared against it rather than against a belief.</summary>
    private static void EngineKey(float* times, int n, float t, out int idx, out float alpha)
    {
        idx = 0;
        alpha = 1f;
        if (n == 0) return;
        if (!(times[0] <= t && t != times[0])) return;
        uint i = 1;
        while (true)
        {
            idx = (int)(ushort)i;
            if (idx >= n) break;
            if (t <= times[(int)i]) break;
            i = (i + 1) & 0xffff;
        }
        if (idx == n) return;
        float t0 = times[(int)i - 1];
        alpha = (t - t0) / (times[(int)i] - t0);
    }

    private static bool KeySearch()
    {
        float* times = stackalloc float[32];
        var rng = new Random(106);
        for (int trial = 0; trial < 200; trial++)
        {
            int n = trial % 32;
            float at = 0f;
            for (int i = 0; i < n; i++)
            {
                // Every third track holds a pair of keys at one time, which is where the engine's own
                // division by zero lives: the two implementations must agree even on that.
                at += trial % 3 == 0 && i % 5 == 2 ? 0f : (float)rng.NextDouble() * 0.5f + 0.01f;
                times[i] = at;
            }
            for (int p = 0; p < 20; p++)
            {
                float t = p switch
                {
                    0 => -1f,
                    1 => n == 0 ? 0f : times[0],
                    2 => n == 0 ? 0f : times[n - 1],
                    3 => n == 0 ? 0f : times[n - 1] + 1f,
                    _ => n == 0 ? (float)rng.NextDouble() : times[p % Math.Max(n, 1)] * (float)rng.NextDouble() * 1.2f,
                };
                EngineKey(times, n, t, out int wantIdx, out float wantA);
                AnimKeys.Key(times, n, t, out int gotIdx, out float gotA);
                if (gotIdx != wantIdx) return false;
                bool same = wantA.Equals(gotA) || MathF.Abs(wantA - gotA) < 1e-6f
                            || (float.IsNaN(wantA) && float.IsNaN(gotA))
                            || (float.IsInfinity(wantA) && float.IsInfinity(gotA));
                if (!same) return false;
            }
        }
        return true;
    }

    private static bool SlerpMath()
    {
        Quat id = new() { W = 1f };
        // |dot| >= 1 returns the first argument UNCHANGED -- not a lerp, not normalised.
        Quat big = new() { W = 3f };
        if (AnimKeys.Slerp(big, id, 0.5f) is not { W: 3f, X: 0f, Y: 0f, Z: 0f }) return false;
        // A 90 degree turn about Z, halved, is 45 degrees about Z.
        float c = MathF.Cos(MathF.PI / 4f), s = MathF.Sin(MathF.PI / 4f);
        Quat q90 = new() { W = c, Z = s };
        Quat half = AnimKeys.Slerp(id, q90, 0.5f);
        float c45 = MathF.Cos(MathF.PI / 8f), s45 = MathF.Sin(MathF.PI / 8f);
        if (MathF.Abs(half.W - c45) > 1e-5f || MathF.Abs(half.Z - s45) > 1e-5f) return false;
        // Shortest arc: the NEGATED 90 degree turn must give the same halfway rotation up to sign, because
        // the negation is applied to the first argument and only when the dot product is negative.
        Quat neg = new() { W = -c, Z = -s };
        Quat other = AnimKeys.Slerp(neg, id, 0.5f);
        if (MathF.Abs(MathF.Abs(other.W) - c45) > 1e-5f || MathF.Abs(MathF.Abs(other.Z) - s45) > 1e-5f) return false;
        // Both endpoints are reached exactly.
        Quat at0 = AnimKeys.Slerp(id, q90, 0f), at1 = AnimKeys.Slerp(id, q90, 1f);
        return MathF.Abs(at0.W - 1f) < 1e-5f && MathF.Abs(at1.W - c) < 1e-5f && MathF.Abs(at1.Z - s) < 1e-5f;
    }

    /// <summary>A synthetic MdlNode carrying one controller over a two-key track, and a synthetic Part it
    /// writes into. Exercises <see cref="AnimKeys.Control"/>, so the part offsets, the key strides and the
    /// blend are all the shipping ones.</summary>
    private static bool ControlBlend()
    {
        const int PoolFloats = 64;
        IntPtr node = (IntPtr)NativeMemory.AllocZeroed(1024);
        IntPtr part = (IntPtr)NativeMemory.AllocZeroed(256);
        IntPtr ctrl = (IntPtr)NativeMemory.AllocZeroed((nuint)(3 * EngineLayout.NewControllerStride));
        float* pool = (float*)NativeMemory.AllocZeroed((nuint)(PoolFloats * sizeof(float)));
        try
        {
            *(IntPtr*)(node + EngineLayout.MdlNodeControllerData) = (IntPtr)pool;
            // times at index 0..1, values from index 2
            pool[0] = 0f; pool[1] = 1f;

            // --- a scalar track: values 10, 20 -> at t = 0.25, 12.5; blended 0.5 over a part field of 0 -> 6.25
            var c = new NewController(ctrl);
            c.Id = ScalarSlot; c.KeyCount = 2; c.TimeKey = 0; c.ValueKey = 2; c.Format = 1;
            pool[2] = 10f; pool[3] = 20f;
            *(float*)(part + ScalarSlot) = 0f;
            AnimKeys.Control(ctrl, part, node, 0.25f, 0.5f);
            if (MathF.Abs(*(float*)(part + ScalarSlot) - 6.25f) > 1e-5f) return false;
            // weight 1 writes the key outright
            AnimKeys.Control(ctrl, part, node, 1f, 1f);
            if (MathF.Abs(*(float*)(part + ScalarSlot) - 20f) > 1e-5f) return false;

            // --- a vector track at the part's POSITION offset. position_scale_factor is the runtime's, so
            //     this only asserts the lerp; the scale is asserted by the row that names it.
            c.Id = EngineLayout.PartPos; c.KeyCount = 2; c.TimeKey = 0; c.ValueKey = 4; c.Format = 3;
            pool[4] = 0f; pool[5] = 0f; pool[6] = 0f;
            pool[7] = 2f; pool[8] = 4f; pool[9] = 8f;
            *(Vector3*)(part + EngineLayout.PartPos) = Vector3.Zero;
            AnimKeys.Control(ctrl, part, node, 0.5f, 1f);
            Vector3 got = *(Vector3*)(part + EngineLayout.PartPos);
            float sc = AnimKeys.Scale;
            if (MathF.Abs(got.X - 1f * sc) > 1e-4f || MathF.Abs(got.Y - 2f * sc) > 1e-4f
                || MathF.Abs(got.Z - 4f * sc) > 1e-4f) return false;

            // --- a rotation track: keys stored (x, y, z, w), identity then 90 degrees about Z. Halfway is
            //     45 degrees, and the part quaternion is (w, x, y, z).
            c.Id = EngineLayout.PartRot; c.KeyCount = 2; c.TimeKey = 0; c.ValueKey = 10; c.Format = 4;
            float cc = MathF.Cos(MathF.PI / 4f), ss = MathF.Sin(MathF.PI / 4f);
            pool[10] = 0f; pool[11] = 0f; pool[12] = 0f; pool[13] = 1f;
            pool[14] = 0f; pool[15] = 0f; pool[16] = ss; pool[17] = cc;
            *(Quat*)(part + EngineLayout.PartRot) = new Quat { W = 1f };
            AnimKeys.Control(ctrl, part, node, 0.5f, 1f);
            Quat q = *(Quat*)(part + EngineLayout.PartRot);
            float c45 = MathF.Cos(MathF.PI / 8f), s45 = MathF.Sin(MathF.PI / 8f);
            return MathF.Abs(q.W - c45) < 1e-4f && MathF.Abs(q.Z - s45) < 1e-4f
                   && MathF.Abs(q.X) < 1e-6f && MathF.Abs(q.Y) < 1e-6f;
        }
        finally
        {
            NativeMemory.Free((void*)node); NativeMemory.Free((void*)part);
            NativeMemory.Free((void*)ctrl); NativeMemory.Free(pool);
        }
    }

    /// <summary>The weight arithmetic of the shipping <see cref="Advance"/> over a synthetic gob, with the
    /// three engine calls it makes swapped for recorders. No part tree, so no hierarchy walk; no events on the
    /// animation, so the only <c>DoEvent</c> names are the lifecycle ones.</summary>
    private static bool Ramps()
    {
        IntPtr gob = (IntPtr)NativeMemory.AllocZeroed(1280);
        IntPtr vtbl = (IntPtr)NativeMemory.AllocZeroed(1024);
        IntPtr anim = (IntPtr)NativeMemory.AllocZeroed(512);
        IntPtr* list = (IntPtr*)NativeMemory.AllocZeroed((nuint)(8 * sizeof(IntPtr)));
        IntPtr r0 = (IntPtr)NativeMemory.AllocZeroed(48), r1 = (IntPtr)NativeMemory.AllocZeroed(48);
        byte* scratch = (byte*)NativeMemory.AllocZeroed(32);

        var saveRemove = _remove; var saveDel = _delIndex; var saveFree = _free;
        var saveTrace = _pTraceRun; var saveFlags = _pTraceFlags; var saveLen = _pTraceLength; var saveScale = _pPosScale;
        try
        {
            _remove = &CheckRemove;
            _delIndex = &CheckDelIndex;
            _free = &CheckFree;
            _pTraceRun = (IntPtr*)scratch;
            _pTraceFlags = (int*)(scratch + 8);
            _pTraceLength = (float*)(scratch + 12);
            _pPosScale = (float*)(scratch + 16);

            *(IntPtr*)gob = vtbl;
            delegate* unmanaged<IntPtr, byte*, IntPtr, void> rec = &CheckDoEvent;
            *(IntPtr*)(vtbl + EngineLayout.GobDoEventSlot) = (IntPtr)rec;
            *(float*)(anim + EngineLayout.AnimationLength) = 10f;
            // "walk" as the animation name, so a lifecycle event reads "loopwalk" / "donewalk".
            byte* nm = (byte*)(anim + EngineLayout.AnimationName);
            nm[0] = (byte)'w'; nm[1] = (byte)'a'; nm[2] = (byte)'l'; nm[3] = (byte)'k'; nm[4] = 0;
            var g = new Gob(gob);
            g.AnimRuns = (IntPtr)list;

            // (1) fade in: blend 0, fadeTime 0.5, dt 0.1 -> step 0.2
            Run(r0, anim, rate: 1f, flags: 0, time: 1f, blend: 0f, fade: 0.5f);
            list[0] = r0;
            g.AnimRunCount = 1;
            _seenCount = 0; _freed = 0;
            SafePtr sp = new() { Object = gob, Index = -1 };
            bool moved = false;
            if (!Advance(g, gob, 0.1f, 1, ref sp, ref moved)) return false;
            if (MathF.Abs(new AnimRun(r0).Blend - 0.2f) > 1e-5f) return false;
            if (!moved) return false;

            // (2) the NET fade-out step: 2*dt/fadeTime down, then dt/fadeTime back up in the same visit.
            //     blend 1.0, fadeTime 0.5, dt 0.1 -> 1.0 - 0.4 + 0.2 = 0.8, NOT 0.6.
            Run(r0, anim, 1f, FadeOut | FadeNow, time: 1f, blend: 1f, fade: 0.5f);
            list[0] = r0; g.AnimRunCount = 1;
            if (!Advance(g, gob, 0.1f, 1, ref sp, ref moved)) return false;
            if (MathF.Abs(new AnimRun(r0).Blend - 0.8f) > 1e-5f) return false;

            // (3) a fade-out that reaches zero latches done and stays at zero
            Run(r0, anim, 1f, FadeOut | FadeNow, time: 1f, blend: 0.1f, fade: 0.5f);
            list[0] = r0; g.AnimRunCount = 1;
            if (!Advance(g, gob, 0.1f, 1, ref sp, ref moved)) return false;
            if (new AnimRun(r0).Blend != 0f || new AnimRun(r0).Done != 1) return false;

            // (4) fadeTime 0 on the way out: full weight for this one frame, done latched
            Run(r0, anim, 1f, FadeOut | FadeNow, time: 1f, blend: 1f, fade: 0f);
            list[0] = r0; g.AnimRunCount = 1;
            if (!Advance(g, gob, 0.1f, 1, ref sp, ref moved)) return false;
            if (new AnimRun(r0).Blend != 1f || new AnimRun(r0).Done != 1) return false;

            // (5) ...and the NEXT visit fires done<name>, removes and frees it
            _seenCount = 0; _freed = 0;
            if (!Advance(g, gob, 0.1f, 1, ref sp, ref moved)) return false;
            if (_seenCount != 1 || _seen[0] != "donewalk" || _freed != 1 || g.AnimRunCount != 0) return false;

            // (6) a run reaching full weight drops every OLDER run (higher index), silently
            Run(r0, anim, 1f, 0, time: 1f, blend: 0.9f, fade: 0.1f);
            Run(r1, anim, 1f, 0, time: 1f, blend: 1f, fade: 0.1f);
            list[0] = r0; list[1] = r1; g.AnimRunCount = 2;
            _seenCount = 0; _freed = 0;
            if (!Advance(g, gob, 0.1f, 2, ref sp, ref moved)) return false;
            if (new AnimRun(r0).Blend != 1f || g.AnimRunCount != 1 || _freed != 1 || _seenCount != 0) return false;

            // (7) a looping run wrapping past the end fires loop<name> and keeps the overshoot
            Run(r0, anim, 1f, Loop, time: 9.95f, blend: 1f, fade: 0f);
            list[0] = r0; g.AnimRunCount = 1;
            _seenCount = 0;
            if (!Advance(g, gob, 0.1f, 1, ref sp, ref moved)) return false;
            if (_seenCount != 1 || _seen[0] != "loopwalk") return false;
            if (MathF.Abs(new AnimRun(r0).Time - 0.05f) > 1e-4f) return false;

            // (8) a queued run is skipped outright, and the OLDEST one is not started here (that is One's job)
            Run(r0, anim, 1f, Queued, time: 1f, blend: 0.5f, fade: 0.5f);
            list[0] = r0; g.AnimRunCount = 1;
            _seenCount = 0;
            if (!Advance(g, gob, 0.1f, 1, ref sp, ref moved)) return false;
            return new AnimRun(r0).Blend == 0.5f && new AnimRun(r0).Time == 1f && _seenCount == 0;
        }
        finally
        {
            _remove = saveRemove; _delIndex = saveDel; _free = saveFree;
            _pTraceRun = saveTrace; _pTraceFlags = saveFlags; _pTraceLength = saveLen; _pPosScale = saveScale;
            NativeMemory.Free((void*)gob); NativeMemory.Free((void*)vtbl); NativeMemory.Free((void*)anim);
            NativeMemory.Free(list); NativeMemory.Free((void*)r0); NativeMemory.Free((void*)r1);
            NativeMemory.Free(scratch);
        }
    }

    private static void Run(IntPtr p, IntPtr anim, float rate, uint flags, float time, float blend, float fade)
    {
        var r = new AnimRun(p);
        r.Animation = new Animation(anim);
        r.Rate = rate;
        r.Flags = flags;
        r.PrevTime = time;
        r.Time = time;
        r.Blend = blend;
        r.BlendTime = fade;
        r.PosScale = 1f;
        r.Done = 0;
    }

    /// <summary>
    /// <see cref="AnimHierarchy.Frames"/> against the engine's own control flow. The rows that matter are the
    /// last two: with the two pools at different frame counts, the UV pool sees the frame the POSITION pool
    /// clamped, not one it clamped itself. Every expected value is read off the disassembly
    /// (0x4993c2, 0x4993de, 0x499748, 0x499940, 0x499555, 0x49986f), not off this implementation.
    /// </summary>
    private static bool MorphFrames()
    {
        // (frame, vertFrames, uvFrames) -> (posFrame, lerpPos, uvFrame, lerpUv)
        (int F, int Vf, int Tf, int P, bool Lp, int U, bool Lu)[] rows =
        [
            // interior of both: neither pool clamps
            (2, 8, 8, 2, true, 2, true),
            // last segment of both: the position pool holds its last key, and so does the UV pool
            (7, 8, 8, 7, false, 7, false),
            // past the end of both: the position pool clamps to vf-1 and the UV pool reads THAT
            (99, 8, 8, 7, false, 7, false),
            // before the start: the position pool forces the shared index to 0 and holds key 0, and the UV
            // pool -- reading that 0 as an in-range frame -- LERPS 0->1 with a negative alpha, i.e. it
            // extrapolates backward. Asymmetric, and exactly what 0x499940 -> 0x499555 does.
            (-3, 8, 8, 0, false, 0, true),
            // no position frames at all: the frame stays negative, the UV pool forces 0 and holds it
            (-3, 0, 8, -3, false, 0, false),
            // no position frames, frame in range: the UV pool decides on the RAW frame
            (2, 0, 8, 2, false, 2, true),
            // THE FIX: 4 morph positions, 16 UVs, frame past the positions. The position pool clamps the
            // shared index to 3, so the UV pool holds key 3 -- it does NOT lerp 12->13 off the raw frame.
            (12, 4, 16, 3, false, 3, true),
            // ...and past both, so the UV pool holds its own last key because the clamped index reaches it
            (99, 4, 2, 3, false, 1, false),
        ];
        foreach (var r in rows)
        {
            int f = r.F;
            AnimHierarchy.Frames(ref f, r.Vf, r.Tf, out bool lp, out bool lu, out int uf);
            if (f != r.P || lp != r.Lp || uf != r.U || lu != r.Lu) return false;
        }
        return true;
    }

    /// <summary>The event window, over a synthetic animation with events at 1, 2 and 3 seconds.</summary>
    private static bool EventWindow()
    {
        IntPtr gob = (IntPtr)NativeMemory.AllocZeroed(256);
        IntPtr vtbl = (IntPtr)NativeMemory.AllocZeroed(1024);
        IntPtr anim = (IntPtr)NativeMemory.AllocZeroed(512);
        byte* events = (byte*)NativeMemory.AllocZeroed((nuint)(3 * EngineLayout.AnimEventStride));
        IntPtr alive = gob, dead = IntPtr.Zero;
        IntPtr* alivep = &alive, deadp = &dead;
        try
        {
            *(IntPtr*)gob = vtbl;
            delegate* unmanaged<IntPtr, byte*, IntPtr, void> rec = &CheckDoEvent;
            *(IntPtr*)(vtbl + EngineLayout.GobDoEventSlot) = (IntPtr)rec;
            var a = new Animation(anim);
            a.Events = (IntPtr)events;
            a.EventCount = 3;
            for (int i = 0; i < 3; i++)
            {
                byte* e = events + i * EngineLayout.AnimEventStride;
                *(float*)e = i + 1f;
                byte* n = e + EngineLayout.AnimEventName;
                n[0] = (byte)('a' + i); n[1] = 0;
            }

            // (t0, t1]: the low end is EXCLUSIVE, the high end inclusive.
            _seenCount = 0;
            AnimEvents.Fire(gob, anim, 1f, 2f, 0, alivep);
            if (_seenCount != 1 || _seen[0] != "b") return false;

            // The whole span, ascending.
            _seenCount = 0;
            AnimEvents.Fire(gob, anim, 0f, 3f, 0, alivep);
            if (_seenCount != 3 || _seen[0] != "a" || _seen[1] != "b" || _seen[2] != "c") return false;

            // A wrap (from > to) fires the TAIL of the cycle first, then the head -- so time order holds.
            _seenCount = 0;
            AnimEvents.Fire(gob, anim, 2.5f, 1.5f, 0, alivep);
            if (_seenCount != 2 || _seen[0] != "c" || _seen[1] != "a") return false;

            // Reverse: [to, from), so the HIGH end is the exclusive one. Descending index.
            _seenCount = 0;
            AnimEvents.Fire(gob, anim, 3.5f, 0f, 1, alivep);
            if (_seenCount != 3 || _seen[0] != "c" || _seen[1] != "b" || _seen[2] != "a") return false;
            _seenCount = 0;
            AnimEvents.Fire(gob, anim, 3f, 0f, 1, alivep);
            if (_seenCount != 2 || _seen[0] != "b" || _seen[1] != "a") return false;
            _seenCount = 0;
            AnimEvents.Fire(gob, anim, 2f, 1f, 1, alivep);
            if (_seenCount != 1 || _seen[0] != "a") return false;

            // A dead gob stops the walk at the first event that killed it.
            _seenCount = 0;
            AnimEvents.Fire(gob, anim, 0f, 3f, 0, deadp);
            return _seenCount == 1;
        }
        finally
        {
            NativeMemory.Free((void*)gob); NativeMemory.Free((void*)vtbl);
            NativeMemory.Free((void*)anim); NativeMemory.Free(events);
        }
    }
}
