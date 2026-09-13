using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Sowx.Framework;

namespace Sowx.Alloy;

/// <summary>#192: a one-shot dump of what the Alloy colour pass actually submits, as a file two runs of the
/// same area from the same camera can be diffed against. Armed by <c>/framedump</c>, consumed by the very
/// next <c>BuildBatches</c>, written on the render thread and then disarmed.
/// <para>It exists for the behaviour-preserving refactors (#193, #194): "the renderer draws the same thing"
/// is not a claim an eye can settle on a tileset, and the failure mode is not a crash -- it is one texture
/// rotation wrong on one tileset, found three weeks later.</para>
/// <para><b>Canonical, not chronological.</b> The ticket asked for submission order; submission order is not
/// run-stable. Enrollment walks an open-addressed table hashed on POINTERS, so ASLR reshuffles it between
/// runs and a literal submission-order dump diffs against itself. Every row is therefore sorted on its own
/// content, and every runtime-assigned identity (pointers, GL texture ids, bindless handles, minted group
/// indices) is replaced by something derived from content: texture NAMES, and a VAO ordinal assigned by
/// first appearance in the sorted order.</para>
/// <para>Allocates and does file IO on the frame it fires. That is deliberate and bounded: it is one opt-in
/// frame, the same licence <c>[dumpframe]</c> already takes, and the alternative is a preallocated row table
/// sized for the part cap.</para></summary>
internal static unsafe class FrameDump
{
    /// <summary>Set by <c>/framedump</c>, cleared by the frame that serves it. Not volatile-guarded beyond
    /// this: a missed pickup costs one more frame, and the command is idempotent.</summary>
    internal static bool Armed;

    /// <summary>The number the batching walk cuts groups on run-length (#195): rows carry their FULL key, so
    /// the distinct-key count falls out of the same sort that makes the dump diffable. Kept after a dump so
    /// the command's reply can report it.</summary>
    internal static int LastGroups, LastDistinct, LastDraws;

    private struct Row
    {
        public string Key;    // the batch key, minus the VAO -- what a group is allowed to merge on
        public string Draw;   // this draw's geometry + its SSBO record
        public int Vao;       // raw GL name; replaced by an ordinal before writing
        /// <summary>The item's byte offset and base vertex inside the VDO's SHARED pools. Held so the rows
        /// carry their full identity in memory, and DELIBERATELY NEVER WRITTEN.
        /// <para>They look like content and are not: they are allocation offsets, handed out in the order
        /// geometry was packed into the pool, so they move between runs exactly as a GL texture id or a
        /// pointer does. Printing them raw made 5458 of 5459 draw lines differ between two runs of the same
        /// area -- measured, not reasoned; the first cut of this file asserted they were "a content
        /// fingerprint of the VDO" and they are the opposite.</para>
        /// <para>And unlike the VAO they cannot be ORDINALISED either, which was the obvious next idea and
        /// is worse: a dense first-appearance counter over thousands of items shifts for every row the two
        /// runs do not share, so any scene drift at all renumbers the tail. Measured on the same pair:
        /// dropping them matched 4314 rows, ordinalising them matched 195.</para>
        /// <para>What is lost: two draws that swap items of the SAME index count under the same material
        /// read identically. <c>n=</c>, the model matrix and the material columns are the fingerprint that
        /// remains, and short of reading the index buffer back there is nothing content-derived to put
        /// here.</para></summary>
        public long Off;
        public int Base;
    }

    private static readonly List<Row> _rows = new();
    private static bool _capturing;

    internal static string Dir => Native.ConfigPath("cx_frame_dump");

    /// <summary>Open the capture for one <c>BuildBatches</c>. False = not armed, and the hot loop's row call
    /// stays a predicted-not-taken branch.</summary>
    internal static bool Begin()
    {
        if (!Armed) return false;
        Armed = false;
        _rows.Clear();
        _capturing = true;
        return true;
    }

    internal static bool Capturing => _capturing;

    /// <summary>One submitted draw, called from the staging walk with everything in hand.
    /// <para>TWO strings, not one, and the split is what makes the numbers mean anything. <b>Key</b> is the
    /// batcher's OWN cut key, reproduced exactly -- including #153's bindless merge (a lit batch stops
    /// splitting on its textures, so neither does the key) and the normalised blend pair (zero for a class
    /// that does not blend). Counting distinct Keys is therefore #195's question and not a near-miss of it.
    /// <b>Show</b> is the human column: the texture NAMES, which is where the diff surface's value is, and
    /// which under merge belong on the draw rather than on the group precisely because the group no longer
    /// splits on them.</para></summary>
    internal static void Row_(int cls, bool lit, int vao, IntPtr mat, uint mode, int feat, bool dis,
                              int blend, bool noCull, int slots, bool merge, int idxCount, nint off,
                              int baseVertex, float* r)
    {
        var k = new StringBuilder(128);
        k.Append("cls=").Append(((AlloyClass)cls).ToString())
         .Append(" lit=").Append(lit ? 1 : 0)
         .Append(" mode=0x").Append(mode.ToString("x", CultureInfo.InvariantCulture))
         .Append(" feat=").Append(feat)
         .Append(" dis=").Append(dis ? 1 : 0)
         .Append(" blend=").Append(blend)
         // #292: the material's `twosided`. In the dump because it is now a batch-key term AND the one piece
         // of a part's state that can make it vanish outright rather than look wrong -- a mesh drawn under
         // the culling it did not ask for shows nothing at all, and no other column would say so.
         .Append(" nocull=").Append(noCull ? 1 : 0);
        // Under merge the batcher compares neither the diffuse id nor the seven slot ids, so a key that
        // carried them would report fragmentation the cut does not have.
        // The mask is in the key either way -- it is what keeps a material declaring the EnvSlot bit (64,
        // which has no texture behind it) from merging with one that does not.
        k.Append(" mask=").Append(slots);
        if (merge) k.Append(" tex=<merged>");
        else
        {
            // ...and the NON-merge cut compares all seven slot ids against the group's, so they are key
            // material here exactly as much as they are not key material above.
            k.Append(" tex=").Append(Slot(mat, 0, true));
            AppendSlots(k, mat, slots);
        }

        var d = new StringBuilder(320);
        d.Append("n=").Append(idxCount);
        // Only under merge: without it the names are already on the G line, and repeating them per draw
        // would triple the file for nothing.
        if (merge)
        {
            d.Append(" tex=").Append(Slot(mat, 0, true));
            AppendSlots(d, mat, slots);
        }
        d.Append(" m=");
        for (int f = 0; f < 16; f++) { if (f != 0) d.Append(','); F(d, r[f]); }
        d.Append(" rot="); F(d, r[16]); d.Append(','); F(d, r[17]);
        // r[20..39] are written by the LIT branch alone, and _dData is allocated once and never cleared --
        // so printing them for an unlit draw serialises whatever the previous frame left in that slot, and
        // slot assignment follows enrollment order, which is pointer-hash order. That is a diff that changes
        // between runs for no reason connected to the code under test, which is the one thing this file
        // exists not to do.
        if (lit)
        {
            d.Append(" amb="); F(d, r[20]); d.Append(','); F(d, r[21]); d.Append(','); F(d, r[22]);
            if (cls != (int)AlloyClass.Water)
            { d.Append(" dif="); F(d, r[24]); d.Append(','); F(d, r[25]); d.Append(','); F(d, r[26]); }
            d.Append(" emi="); F(d, r[28]); d.Append(','); F(d, r[29]); d.Append(','); F(d, r[30]);
            d.Append(" par=");
            for (int f = 32; f < 40; f++) { if (f != 32) d.Append(','); F(d, r[f]); }
        }
        // #147's water body overwrites the diffuse slot whether or not the batch was lit, so it is the one
        // group of four an unlit draw does have written.
        if (cls == (int)AlloyClass.Water)
        {
            d.Append(" body=");
            for (int f = 24; f < 28; f++) { if (f != 24) d.Append(','); F(d, r[f]); }
        }
        d.Append(" f4=");
        for (int f = 40; f < 44; f++) { if (f != 40) d.Append(','); F(d, r[f]); }
        // Everything past here varies with time rather than with the code, so a diff of an animated scene
        // can cut the line at the bar instead of reading around it.
        d.Append(" | dissolve="); F(d, r[18]);
        d.Append(" alpha="); F(d, r[19]);

        _rows.Add(new Row { Key = k.ToString(), Draw = d.ToString(), Vao = vao, Off = off, Base = baseVertex });
    }

    private static void AppendSlots(StringBuilder b, IntPtr mat, int slots)
    {
        b.Append(" slots=");
        for (int s = 0; s < AlloyRenderer.MapSlots; s++)
        {
            if (s != 0) b.Append('|');
            b.Append(Slot(mat, s == AlloyRenderer.AoSlot ? AlloyRenderer.AoTexture : s + 1, (slots & AlloyRenderer.SlotBit(s)) != 0));
        }
    }

    /// <summary>Put the rows in an order derived from their content alone, fold the run-assigned VAO name
    /// into an ordinal, and return the count of DISTINCT full keys -- which is what the run-length cut in
    /// BuildBatches would have minted had the sort agreed with the key (#195).
    /// <para>Two sorts, not one: the ordinal can only be assigned once an order exists, and it has to be in
    /// the key afterwards or two groups differing only by VAO interleave.</para>
    /// <para>ponytail: two rows identical in key AND draw but on different VDOs could swap ordinals between
    /// runs -- indistinguishable geometry on two VAOs. Not seen; add the VDO's item count as a tiebreak if a
    /// diff ever shows a pure vao= flip.</para></summary>
    private static int Canonicalize()
    {
        _rows.Sort(Compare);
        // The VAO ordinal is assigned in THIS order -- derived from content alone -- which is what makes it
        // reproducible. It works because there are a handful of VAOs and both runs see all of them; the same
        // trick does NOT extend to per-item ordinals, see Row.Off.
        var vaoOrd = new Dictionary<int, int>();
        for (int i = 0; i < _rows.Count; i++)
            if (!vaoOrd.ContainsKey(_rows[i].Vao)) vaoOrd[_rows[i].Vao] = vaoOrd.Count;
        for (int i = 0; i < _rows.Count; i++)
        {
            Row r = _rows[i];
            r.Key = $"vao={vaoOrd[r.Vao]} {r.Key}";
            _rows[i] = r;
        }
        _rows.Sort(Compare);
        int distinct = 0;
        for (int i = 0; i < _rows.Count; i++)
            if (i == 0 || _rows[i].Key != _rows[i - 1].Key) distinct++;
        return distinct;
    }

    private static int Compare(Row a, Row b)
    {
        int c = string.CompareOrdinal(a.Key, b.Key);
        return c != 0 ? c : string.CompareOrdinal(a.Draw, b.Draw);
    }

    /// <summary>Off-device: the canonical form really is independent of the order the rows arrived in, which
    /// is the ONE property the whole harness rests on -- a dump that reorders between runs is not a diff
    /// surface, and enrollment order is pointer-hash order, so it does reorder. Two shuffles of the same
    /// three rows on two VAOs must produce byte-identical output and the same distinct-key count.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        // The pool offsets differ between the two shuffles exactly as they differ between two runs of the
        // client -- same geometry, repacked. If they reach the output, this fails.
        string A = Render(new[] { (1, "b", "y", 900L), (2, "a", "x", 100L), (1, "b", "x", 500L) });
        string B = Render(new[] { (1, "b", "x", 41L), (1, "b", "y", 77L), (2, "a", "x", 12L) });
        bool ok = A == B && LastDistinct == 2;
        if (!ok) log.Info($"FrameDump.SelfCheck: FAILED -- canonical form depends on arrival order ({LastDistinct} keys)");
        else log.Info("FrameDump.SelfCheck: passed");
        _rows.Clear();
        return ok;

        static string Render((int vao, string key, string draw, long off)[] rows)
        {
            _rows.Clear();
            foreach (var r in rows)
                _rows.Add(new Row { Vao = r.vao, Key = r.key, Draw = r.draw, Off = r.off, Base = (int)r.off });
            LastDistinct = Canonicalize();
            var sb = new StringBuilder();
            foreach (Row r in _rows) sb.Append(r.Key).Append('/').Append(r.Draw).Append(';');
            return sb.ToString();
        }
    }

    private static void F(StringBuilder b, float v) => b.Append(v.ToString("F4", CultureInfo.InvariantCulture));

    /// <summary>One slot's texture name, and ONLY for a slot the staging walk already resolved.
    /// <para><paramref name="declared"/> is not politeness, it is the same bound
    /// <c>AlloyRenderer.ReadMaterial</c> enforces: on a plain <c>Material</c> with no SharedMaterial,
    /// <c>GetTexture(4..7)</c> is an unbounded read of whatever field happens to sit at that offset, and
    /// <see cref="AlloyCensus.TexName"/> then walks two vtable slots off the result. Plain materials are the
    /// majority of shipped content, so an ungated version segfaults on the render thread the first time
    /// anyone types the command.</para></summary>
    private static string Slot(IntPtr mat, int slot, bool declared)
    {
        if (!declared || mat == IntPtr.Zero) return "-";
        TextureRef t = new MaterialRef(mat).TextureAt(slot);
        return t.IsNull ? "-" : t.ResolveName();
    }

    /// <summary>Close the capture: canonicalise, count the distinct full keys against the groups the
    /// run-length cut actually minted (#195's step 1), and write the file.</summary>
    internal static void Finish(int groups, ExtenderLog? log)
    {
        if (!_capturing) return;
        _capturing = false;

        int distinct = Canonicalize();
        LastGroups = groups; LastDistinct = distinct; LastDraws = _rows.Count;

        try
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, "frame.txt");
            using var w = new StreamWriter(path, false);
            w.WriteLine("# sow-client-extender frame dump (#192) -- one G line per distinct BATCH KEY (the");
            w.WriteLine("# batcher's own cut key, merge and blend normalisation included), one D line per");
            w.WriteLine("# submitted draw. Canonical order, not submission order: see FrameDump's summary.");
            w.WriteLine("# Under #153's bindless merge the texture names sit on the D lines, because that is");
            w.WriteLine("# where they belong -- a merged group does not split on them.");
            w.WriteLine("# Everything right of the | is time-varying; cut it before diffing an animated scene.");
            w.WriteLine("# The diff is only as reproducible as the SCENE. Creatures animate, so their rows");
            w.WriteLine("# differ every frame; a camera that is not identical to the pixel changes which");
            w.WriteLine("# tiles are in view. Dump from a fixed vantage with nothing alive in shot, and read");
            w.WriteLine("# a residual of moving rows as the scene, not as the renderer.");
            w.WriteLine($"# draws={_rows.Count} distinct-keys={distinct} groups-minted={groups} " +
                        $"fragmentation={groups - distinct}");
            // #294: a G line's vao= naming one of these is a part sampling every map at the constant (0,0).
            // The harvest counts come with it because there is no log file to read them off -- a part that
            // is MISSING from the dump entirely is a refusal, and these say which of the three it was.
            w.WriteLine($"# uvless-vaos={AlloyRenderer.UvlessSlots()}");
            w.WriteLine($"# harvest-refused-vdos: no-engine-vao {AlloyRenderer.HarvestNoVao}"
                        + $" no-elembuf {AlloyRenderer.HarvestNoElem} no-pos {AlloyRenderer.HarvestNoPos}"
                        + $" no-uv {AlloyRenderer.HarvestNoUv}");
            for (int i = 0; i < _rows.Count; i++)
            {
                if (i == 0 || _rows[i].Key != _rows[i - 1].Key)
                {
                    int n = 1;
                    for (int j = i + 1; j < _rows.Count && _rows[j].Key == _rows[i].Key; j++) n++;
                    w.WriteLine($"G count={n} {_rows[i].Key}");
                }
                w.WriteLine("  D " + _rows[i].Draw);
            }
            log?.Info($"[framedump] {_rows.Count} draws, {distinct} distinct keys, {groups} groups minted " +
                      $"-> {path}");
        }
        catch (Exception e)
        {
            log?.Info($"[framedump] write to {Dir} failed -- {e.Message}");
        }
        _rows.Clear();
    }
}
