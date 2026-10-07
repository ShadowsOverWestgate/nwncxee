using System;
using CX.Alloy.Db;
using CX.Alloy.Mdl;

namespace CX.Alloy.Seam;

/// <summary>The per-object texture overrides behind <c>Gob::ReplaceTexture</c> / <c>RestoreTexture</c> /
/// <c>SetEnvironmentMap</c>. The engine swapped the <c>CAurTexture*</c> on the part's material, which every
/// instance of that model shared -- CX writes <see cref="SceneDb.PartTexId"/>, which is per PART, so one
/// creature's swapped armour no longer repaints every creature wearing the same model.
/// <para>The feed's stage uses <c>PartTexId</c> when it is non-zero and the recipe's texture otherwise, so an
/// override is one column write plus <see cref="Dirty.Texture"/>, and restoring is zeroing it.</para></summary>
internal static class ObjectTex
{
    /// <summary>Point every part of <paramref name="obj"/> whose current texture in slot
    /// <paramref name="slot"/> is named <paramref name="from"/> at <paramref name="to"/>; a null or empty
    /// <paramref name="from"/> matches every part, which is what the engine's whole-object form does. Returns
    /// how many parts changed -- the engine's non-zero "it worked".
    /// <para><paramref name="rows"/>, when given, are the PLT layer rows the swapped parts are coloured with.
    /// The engine's <c>AurTextureReplaceSingleReference</c> layers only the texture that matched, and
    /// <c>CNWCAnimBaseParts::ReplaceTexture</c> @0xad8b10 issues one call per body part with that part's own
    /// rows (per-part armour colours), so the rows go on the matched parts, not on the whole creature.</para></summary>
    internal static int Replace(DbId obj, string? from, string? to, int slot = 0, bool subtree = false,
                                ReadOnlySpan<int> rows = default)
    {
        if (string.IsNullOrEmpty(to) || (uint)slot >= 4) return 0;
        return ReplaceIn(obj, from, to, slot, 0, rows);
    }

    /// <summary>How deep a composition may be grafted before the walk gives up. A creature is one hop; the
    /// bound is a cycle guard, not a budget.</summary>
    private const int MaxGraft = 8;

    /// <summary>The object's own parts, then every piece <c>Gob::Append</c> grafted onto it. The engine had
    /// ONE part tree per creature, so its ReplaceTexture reached the torso, the arms and the cloak; CX makes
    /// each of those a child object, and a walk of the base range alone left them wearing the model's own
    /// texture -- a character in her DEFAULT body where the armour should be.</summary>
    private static int ReplaceIn(DbId obj, string? from, string? to, int slot, int depth, ReadOnlySpan<int> rows)
    {
        if (depth > MaxGraft || !SceneDb.Objects.Valid(obj)) return 0;
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        int hits = 0;
        for (int p = first; p < first + count; p++)
        {
            // `subtree` was the engine's "only this part's descendants" form. CX's range IS the subtree of
            // the object's root, and the only caller that passes it is the whole-object case, so the flag
            // changes nothing here; it is accepted rather than ignored silently.
            if (ShowsOverride(p, slot, to!)) Refresh(p, slot);
            if (!Matches(p, slot, from) && !GraftNamed(p, slot, from)) continue;
            int at = p * 4 + slot;
            var old = new TexId((uint)SceneDb.PartTexId[at]);
            SceneDb.PartTexId[at] = (int)TexRegistry.Swap(old, to!).Value;
            if (!rows.IsEmpty) PltColors.SetPartRows(p, rows);
            hits++;
        }
        if (hits > 0) SceneDb.Touch(obj, Dirty.Texture);
        for (DbId c = SceneDb.ObjSeamAppendHead[obj.Index]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (!SceneDb.Objects.Valid(c)) break;
            hits += ReplaceIn(c, from, to, slot, depth + 1, rows);
        }
        return hits;
    }

    /// <summary><c>Material::Refresh(material, MdlNodeTriMesh::GetMaterialName(mesh))</c>, the step
    /// <c>Gob::ReplaceTexture</c> @0x43eb30 runs on a part that ALREADY shows the texture being asked for:
    /// the material goes back to the model's own name, and only then is <paramref name="slot"/> matched
    /// again. It is what lets the same <c>(from, to)</c> pair be issued twice and mean something -- the load
    /// panel issues <c>ReplaceTexture("gui_replace02", "Screen")</c> for every save the player picks, and the
    /// file behind <c>Screen</c> is a different one each time (the panel swaps the save's resource directory
    /// first). Without it the second selection matched nothing and the preview and portrait never changed.
    /// <para>The released texture is also FORGOTTEN, because the engine's would have died with its last
    /// reference and been re-read; CX's registry would otherwise re-serve the previous save's pixels.</para></summary>
    private static void Refresh(int p, int slot)
    {
        int at = p * 4 + slot;
        if (SceneDb.PartTexId[at] == 0) return;              // already the model's own texture
        var had = new TexId((uint)SceneDb.PartTexId[at]);
        string resref = TexRegistry.ResrefOf(had);
        TexRegistry.Release(had);
        SceneDb.PartTexId[at] = 0;
        TexRegistry.Forget(resref);
    }

    /// <summary>Does part <paramref name="p"/>'s slot already carry an override named <paramref name="name"/>?</summary>
    private static bool ShowsOverride(int p, int slot, string name)
    {
        int ov = SceneDb.PartTexId[p * 4 + slot];
        return ov != 0 && string.Equals(TexRegistry.ResrefOf(new TexId((uint)ov)), name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drop every override on <paramref name="obj"/> so each part falls back to its recipe's own
    /// texture -- <c>Gob::RestoreTexture</c>.</summary>
    internal static int Restore(DbId obj) => RestoreIn(obj, 0);

    /// <inheritdoc cref="ReplaceIn"/>
    private static int RestoreIn(DbId obj, int depth)
    {
        if (depth > MaxGraft || !SceneDb.Objects.Valid(obj)) return 0;
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        int hits = 0;
        for (int p = first; p < first + count; p++)
            for (int slot = 0; slot < 4; slot++)
            {
                int at = p * 4 + slot;
                if (SceneDb.PartTexId[at] == 0) continue;
                if (slot == 0) PltColors.ClearPart(p);
                TexRegistry.Release(new TexId((uint)SceneDb.PartTexId[at]));
                SceneDb.PartTexId[at] = 0;
                hits++;
            }
        if (hits > 0) SceneDb.Touch(obj, Dirty.Texture);
        for (DbId c = SceneDb.ObjSeamAppendHead[obj.Index]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (!SceneDb.Objects.Valid(c)) break;
            hits += RestoreIn(c, depth + 1);
        }
        return hits;
    }

    /// <summary>Release the overrides a dying record holds; nothing else owns those texture references.</summary>
    internal static void Free(DbId obj) => Restore(obj);

    /// <summary>Point every part of <paramref name="obj"/> whose RESOLVED texture name in slot
    /// <paramref name="slot"/> is <paramref name="from"/> at <paramref name="to"/>, or -- when
    /// <paramref name="to"/> is null or empty -- back at its own texture. Returns how many parts changed.
    /// <para><see cref="Matches"/> cannot serve the tile swap: it reads the MODEL NODE's mesh, and a tile's
    /// nodes are merged away into one <see cref="SceneDb.PartMeshDef"/> per draw state, so a merged floor
    /// part matched nothing and the swap never reached it. <see cref="SceneDb.PartTexName"/> is the part's
    /// own resolved name, merged or not, with the <c>.mtr</c> winning exactly as
    /// <c>Material::GetTexture</c> @0x5c5810 has it.</para>
    /// <para>The reference is held in <see cref="SceneDb.PartTexId"/> like every other override, so a
    /// re-swap releases through <see cref="TexRegistry.Swap"/> and the tile's death releases through
    /// <see cref="Free"/> -- there is no second lifetime to get wrong.</para></summary>
    internal static int ReplaceByTexName(DbId obj, string from, string? to, int slot = 0)
    {
        if (string.IsNullOrEmpty(from) || (uint)slot >= 4 || !SceneDb.Objects.Valid(obj)) return 0;
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        int hits = 0;
        for (int p = first; p < first + count; p++)
        {
            if (!string.Equals(SceneDb.PartTexName(p, slot), from, StringComparison.OrdinalIgnoreCase)) continue;
            int at = p * 4 + slot;
            var old = new TexId((uint)SceneDb.PartTexId[at]);
            if (string.IsNullOrEmpty(to))
            {
                if (SceneDb.PartTexId[at] == 0) continue;   // already its own texture
                TexRegistry.Release(old);
                SceneDb.PartTexId[at] = 0;
            }
            else SceneDb.PartTexId[at] = (int)TexRegistry.Swap(old, to).Value;
            hits++;
        }
        if (hits > 0) SceneDb.Touch(obj, Dirty.Texture);
        return hits;
    }

    /// <summary>Is part <paramref name="p"/> a mesh of the graft MODEL named <paramref name="from"/>?
    /// <c>CreateBodyParts</c> @0x834440 issues <c>ReplaceTexture(from = the part's model resref, to = its PLT)</c>,
    /// and many body models declare a different bitmap (<c>pfh0_chest003</c>'s mesh says
    /// <c>pfh0_chest001</c>, the naked torso). The engine draws <c>to</c> on that piece; matching the texture
    /// name alone left every such torso and pelvis on the default body. Verified by the result (the PLTs
    /// decoded offline, the user's vanilla vs DB screenshots), not by the match key: the decompile's
    /// <c>Gob::ReplaceTexture</c> path compares names this reading does not reconcile.</summary>
    private static bool GraftNamed(int p, int slot, string? from)
    {
        if (slot != 0 || string.IsNullOrEmpty(from)) return false;
        ModelDef? m = SceneDb.PartModel[p];
        int n = SceneDb.PartNode[p];
        if (m == null || (uint)n >= (uint)m.Nodes.Length || m.Nodes[n].Mesh == null) return false;
        return string.Equals(m.Name, from, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Is part <paramref name="p"/>'s slot <paramref name="slot"/> the model's texture named
    /// <paramref name="from"/>? Matched on the MODEL's name whatever override is in place: the engine's
    /// <c>AurTextureReplaceSingleReference</c> @0x48d960 compares against CAurTextureBasic+0x88, and
    /// <c>AurTextureGet</c> keys the replacement by that same name and loads only its file from <c>to</c>.
    /// <c>CGuiInvbox::SetObjectHilite</c> @0x691d50 un-highlights with ("gui_boxes_ingame", "gui_boxes_ingame")
    /// over a part showing "gui_inv_hilite"; matched on the override, that left the slot lit for good.</summary>
    private static bool Matches(int p, int slot, string? from)
    {
        if (string.IsNullOrEmpty(from)) return true;
        ModelDef? m = SceneDb.PartModel[p];
        int n = SceneDb.PartNode[p];
        if (m == null || (uint)n >= (uint)m.Nodes.Length) return false;
        // The texture names are the MESH's, not the node's: a node with no mesh has none to match.
        MeshDef? mesh = m.Nodes[n].Mesh;
        if (mesh == null || (uint)slot >= (uint)mesh.Textures.Length) return false;
        return string.Equals(mesh.Textures[slot], from, StringComparison.OrdinalIgnoreCase);
    }
}
