using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;

namespace CX.Alloy;

/// <summary>CX-120: <b>one creature's composed body, dumped from whichever scene layer owns it, in a line
/// format the two modes' logs can be handed to <c>diff</c>.</b>
/// <para>A creature is not one model: <c>CNWCCreatureAppearance::CreateBodyParts</c> @0x834440 grafts a chest,
/// arms, legs and a head onto the parts base model (<c>Gob::Append</c> @0x4422a0, one per
/// <c>parts_*.2da</c> <c>NODENAME</c>), amputates the piece it replaces (<c>Gob::Amputate</c> @0x443fa0) and
/// then colours each graft through <c>Material::ReplaceTexturePLT</c> @0x5c7a10. Four engine entry points,
/// each with a DB-mode counterpart in <see cref="ObjectSlots"/> / <see cref="ObjectTex"/>, and a wrong body
/// means one of them did something different. This prints the RESULT of all four, per node, so the question
/// stops being which seam to suspect.</para>
/// <para>Armed by <c>alloy.paritydump</c> = the world pass to dump at (0 = off), the same arming as
/// <c>alloy.framediff</c> and for the same reason: the first pass of an area is before the body is composed.
/// Cold by construction -- it fires once per area and allocates freely.</para>
/// <para>Line format, identical in both modes and sorted so <c>diff</c> lines up:
/// <c>cre=&lt;key&gt; graft=&lt;piece&gt; node=&lt;name&gt; en= mesh= tex= plt= rows= lpos=</c>.
/// The pose is the part's LOCAL rest/animated pose, which both layers hold in the same terms; the world
/// position is printed once per creature, where it means the same thing either way.</para></summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>An object type id, not an offset.</summary>
    private const byte ParityTypeCreature = 5;   // not-an-offset: CNWCObject::Type for a creature

    /// <summary>Creatures dumped, nearest the player first. Enough for the PC plus the NPCs around it; a
    /// whole area would bury the diff in border-ring extras that are the same in both modes anyway.</summary>
    private const int ParityCreatures = 16;

    /// <summary>Graft depth the walk descends -- a creature is one hop, so this is a cycle guard.</summary>
    private const int ParityMaxGraft = 8;

    private static bool _parityDone;
    private static long _parityPass;

    /// <summary>Which dump of the session this is. The count is per AREA, so a ladder run dumps once per
    /// area edge; numbering them keeps the two modes' lines aligned when <c>diff</c> reads the pair.</summary>
    private static int _parityShot;

    /// <summary>Restart the count: a new area composes new bodies.</summary>
    internal static void CreatureParityAreaEdge() { _parityPass = 0; _parityDone = false; }

    /// <summary>Dump every nearby creature's composition if the knob names this pass.</summary>
    internal static void CreatureParityDump()
    {
        int want = Renderer.ParityDump.Boot;
        if (want <= 0 || _parityDone) return;
        if (++_parityPass < want) return;
        // The PC is the creature the diff is about and she joins the area list late -- later in DB mode,
        // which runs the world at a different rate. Waiting for her keeps the two modes' dumps comparable
        // instead of making the knob's value a guess at a frame number.
        if (CameraService.PlayerCreature() == IntPtr.Zero) return;
        _parityDone = true;
        _parityShot++;
        string m = "db";
        var lines = new List<string>();
        try { ParityCollect(lines); }
        catch (Exception e) { lines.Add($"!! paritydump threw {e.GetType().Name}: {e.Message}"); }
        lines.Sort(StringComparer.Ordinal);
        _log?.Always($"[parity.{m}] shot {_parityShot} at pass {_parityPass}: {lines.Count} line(s)");
        foreach (string s in lines) _log?.Always($"[parity.{m}] {s}");
    }

    /// <summary>The area's creatures, nearest the player first.</summary>
    private static void ParityCollect(List<string> outp)
    {
        IntPtr app = CameraService.App(), module = CameraService.Module();
        if (app == IntPtr.Zero || module == IntPtr.Zero) { outp.Add("!! no app or module"); return; }
        IntPtr area = new CNWCModule(module).CurrentArea.Ptr;
        if (area == IntPtr.Zero) { outp.Add("!! no current area"); return; }
        var av = new CNWCArea(area);
        int n = av.ObjectCount;
        uint* ids = (uint*)av.ObjectIds;
        if (n <= 0 || ids == null) { outp.Add("!! area holds no object list"); return; }
        IntPtr player = CameraService.PlayerCreature();
        Vector3 me = player != IntPtr.Zero ? new CNWCObject(player).Position : default;

        var found = new List<(float Dist, IntPtr Obj)>();
        for (int i = 0; i < n; i++)
        {
            CGameObject go = new CClientExoApp(app).GetGameObject(ids[i]);
            if (go.IsNull) continue;
            CNWCObject o = go.AsNWCObject();
            if (o.IsNull || o.Type != ParityTypeCreature) continue;
            found.Add((Vector3.Distance(o.Position, me), o.Ptr));
        }
        found.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        outp.Add($"area creatures={found.Count} dumped={Math.Min(found.Count, ParityCreatures)} "
               + $"player={(player == IntPtr.Zero ? "NONE -- sorted from the origin" : "ok")}");
        for (int k = 0; k < found.Count && k < ParityCreatures; k++) ParityCreature(outp, found[k].Obj, k);
        ParityPlaceables(outp);
    }

    /// <summary>Every placeable record the database holds: its model, the animation run its pose comes from,
    /// and the animation id the GAME side is holding for it. A placeable's persistent state -- opened,
    /// destroyed, switched off -- is one looping animation the engine plays on load
    /// (<c>CNWCPlaceable::SetAnimation</c> @0x8eb9b0, named by <c>CNWCAnimBasePlaceable::GetAnimationName</c>
    /// @0xadc560), so a barricade standing upright where it should be lying down is those two columns
    /// disagreeing. DB only: mode 0 holds no record to walk.</summary>
    private static void ParityPlaceables(List<string> outp)
    {
        for (int o = 1; o < SceneDb.Objects.High; o++)
        {
            if (!SceneDb.Objects.LiveAt(o)) continue;
            DbId id = SceneDb.Objects.IdAt(o);
            if (FacadeParts.ExternalDataType(id) != EngineLayout.GobKindPlaceable) continue;
            IntPtr game = (IntPtr)FacadeParts.ExternalData(id);
            int anim = game == IntPtr.Zero ? -1 : new CNWCObject(game).Animation;
            outp.Add($"plc obj={o} model={SceneDb.ObjModel[o]?.Name ?? "-"} "
                   + $"run={DbAnim.CurrentAnimation(id) ?? "-"} gameanim={anim} wpos={F3(SceneDb.ObjPos[o])}");
        }
    }

    /// <summary>One creature: its key, its world position, and its whole composition.</summary>
    private static void ParityCreature(List<string> outp, IntPtr obj, int rank)
    {
        IntPtr root = new CNWCCreature(obj).GetModel(0xff, 1);
        string key = ParityKey(obj, root, rank);
        Vector3 w = new CNWCObject(obj).Position;
        if (root == IntPtr.Zero) { outp.Add($"cre={key} wpos={F3(w)} NO BODY MODEL"); return; }
        outp.Add($"cre={key} wpos={F3(w)}");
        ParityDbObject(outp, key, Facade.ObjectId(root), "-", 0);

        // The cloak is its OWN gob, reached through CNWCAnimBaseParts+0x300 -- not a graft, so a walk of the
        // body's append list never sees it. CNWCAnimBaseParts::ReplaceTexture @0xad8b10 colours it with a
        // second call on that pointer, which is the only reason a cloak is ever the right colour.
        IntPtr cloak = new CNWCCreature(obj).GetModel(ParityPartCloak, 1);
        if (cloak != IntPtr.Zero)
        {
            ParityDbObject(outp, key, Facade.ObjectId(cloak), "CLOAK", 0);
        }
        ParityDbAttached(outp, key, Facade.ObjectId(root));
    }

    /// <summary>Everything the game ATTACHED to this creature rather than grafting onto it -- the head and
    /// the cloak are their own gobs (<c>CNWCAnimBaseParts+0x300</c>), so they are neither in the append list
    /// nor in the part range, and a walk of the composition alone never sees what they wear. DB only.</summary>
    private static void ParityDbAttached(List<string> outp, string key, DbId root)
    {
        if (!SceneDb.Objects.Valid(root)) return;
        var inside = new HashSet<int>();
        ParityComposition(root, inside, 0);
        for (int o = 1; o < SceneDb.Objects.High; o++)
        {
            if (!SceneDb.Objects.LiveAt(o) || inside.Contains(o)) continue;
            DbId to = SceneDb.ObjAttachTo[o];
            if (to.IsNull || !inside.Contains(to.Index)) continue;
            ParityDbObject(outp, key, SceneDb.Objects.IdAt(o), $"ATTACH:{SceneDb.ObjModel[o]?.Name ?? "-"}", 0);
        }
    }

    /// <summary>The object indices the composition covers: the record and every piece grafted onto it.</summary>
    private static void ParityComposition(DbId id, HashSet<int> into, int depth)
    {
        if (depth > ParityMaxGraft || !SceneDb.Objects.Valid(id) || !into.Add(id.Index)) return;
        for (DbId c = SceneDb.ObjSeamAppendHead[id.Index]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (!SceneDb.Objects.Valid(c)) break;
            ParityComposition(c, into, depth + 1);
        }
    }

    /// <summary>The body-part id of the cloak, as <c>CNWCAnimBaseParts::GetModel</c> answers it.</summary>
    private const byte ParityPartCloak = 0x12;   // not-an-offset: a body-part id

    /// <summary>A name both runs can match on: the creature's own name when it has one, else its base model,
    /// plus its distance rank so two guards of the same name stay apart. The ladder spawns a different
    /// character each run, so the PC's rank (0) is what lines its lines up.</summary>
    private static string ParityKey(IntPtr obj, IntPtr root, int rank)
    {
        CNWCCreatureStats st = new CNWCCreature(obj).Stats;
        string name = st.IsNull ? "" : ParityText((byte*)st.FirstName, st.FirstNameLen);
        if (name.Length == 0) name = ParityBaseModel(root);
        return $"{_parityShot}:{rank}/{(name.Length == 0 ? "?" : name)}";
    }

    private static string ParityText(byte* p, uint len)
        => p == null || len == 0 || len > 64 ? "" : System.Text.Encoding.Latin1.GetString(p, (int)len).Trim();

    /// <summary>The base model's name, which both layers can answer: the root part's node name is the model's
    /// own root node (<c>CreateInstanceTree</c> instances it), and in CX it is the <see cref="ModelDef"/>.</summary>
    private static string ParityBaseModel(IntPtr root)
    {
        if (root == IntPtr.Zero) return "";
        DbId id = Facade.ObjectId(root);
        return SceneDb.Objects.Valid(id) ? SceneDb.ObjModel[id.Index]?.Name ?? "" : "";
    }

    // ---- mode 0: the ENGINE's Gob / Part tree ------------------------------------------------------------

    /// <summary>Walk the engine's one part tree. A graft is a subtree whose root is in the gob's appended-root
    /// list (<c>Gob+0x130</c>), and it is named by that root part's node name -- which is the appended model's
    /// own root node, i.e. the resref <c>Gob::Append</c> was given. That is what makes the name comparable
    /// with DB mode, where the graft is a separate record carrying the <see cref="ModelDef"/>.</summary>
    private static void ParityEngine(List<string> outp, string key, IntPtr root, string label)
    {
        var g = new Gob(root);
        Part top = g.PartTree;
        if (top.IsNull) { outp.Add($"cre={key} graft={label} NO PART TREE"); return; }
        var grafts = new HashSet<IntPtr>();
        int ac = g.AppendCount;
        var ar = (IntPtr*)g.AppendRoots;
        for (int i = 0; i < ac && ar != null; i++) if (ar[i] != IntPtr.Zero) grafts.Add(ar[i]);
        outp.Add($"cre={key} OBJ graft={label} model={ParityBaseModel(root)} appended={ac}");

        var stack = new Stack<(IntPtr Part, string Graft)>();
        stack.Push((top.Ptr, label));
        int guard = 0;
        while (stack.Count > 0 && guard++ < 4096)
        {
            (IntPtr ptr, string graft) = stack.Pop();
            var p = new Part(ptr);
            string node = p.Node.NameText ?? "?";
            if (grafts.Contains(ptr))
            {
                graft = node;
                outp.Add($"cre={key} OBJ graft={graft} model={node} appended=0");
            }
            Material mat = p.Material;
            string tex = mat.IsNull ? "-" : AlloyCensus.TexName(mat.Slot(0).Ptr);
            int cnt = mat.IsNull ? 0 : mat.PltRowCount;
            outp.Add(ParityLine(key, graft, node, p.Visible != 0, !p.AsPartTriMesh().IsNull, tex, cnt,
                                ParityEngineRows(mat, cnt), p.Pos));
            int kids = p.ChildCount;
            var items = (IntPtr*)p.Children;
            for (int k = 0; k < kids && items != null; k++)
                if (items[k] != IntPtr.Zero) stack.Push((items[k], graft));
        }
    }

    /// <summary><c>Material::ReplaceTexturePLT</c> @0x5c7a10 stores each scheme integer DIVIDED by the master
    /// palette's height (<c>row / 1792.0</c>), so the dump multiplies back to the integer DB mode keeps.</summary>
    private static string ParityEngineRows(Material mat, int count)
    {
        if (mat.IsNull || count <= 0) return "-";
        var sb = new System.Text.StringBuilder();
        Span<float> rows = mat.PltRows;
        for (int i = 0; i < count && i < rows.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append((int)MathF.Round(rows[i] * PltMasterHeight));
        }
        return sb.ToString();
    }

    /// <summary>The master palette's height, the divisor <c>Material::ReplaceTexturePLT</c> applies.</summary>
    private const float PltMasterHeight = 1792f;

    // ---- DB mode: the scene database ---------------------------------------------------------------------

    /// <summary>One DB record's parts, then every piece grafted onto it -- the composition as
    /// <c>ObjSeamAppendHead</c> holds it.</summary>
    private static void ParityDbObject(List<string> outp, string key, DbId id, string graft, int depth)
    {
        if (depth > ParityMaxGraft) return;
        if (!SceneDb.Objects.Valid(id)) { outp.Add($"cre={key} graft={graft} NO DB RECORD"); return; }
        int i = id.Index;
        int first = SceneDb.ObjPartFirst[i], count = SceneDb.ObjPartCount[i];
        outp.Add($"cre={key} OBJ graft={graft} model={SceneDb.ObjModel[i]?.Name ?? "-"} "
               + $"appended={ParityAppendCount(id)}");
        ParityDbEnv(outp, key, i, first, count);
        for (int p = first; p < first + count; p++)
        {
            ModelDef? md = SceneDb.PartModel[p];
            int nd = SceneDb.PartNode[p];
            string node = md != null && (uint)nd < (uint)md.Nodes.Length ? md.Nodes[nd].Name : "?";
            int tid = SceneDb.PartTexId[p * 4];
            string tex = tid != 0 ? TexRegistry.ResrefOf(new TexId((uint)tid)) : SceneDb.PartTexName(p, 0);
            if (string.IsNullOrEmpty(tex)) tex = "-";
            bool own = SceneDb.PartPltOwn[p];
            string rows = ParityDbRows(i, p, own, out int cnt);
            outp.Add(ParityLine(key, graft, node, SceneDb.PartEnabled[p],
                                ParityHasMesh(p), tex, cnt, rows, SceneDb.PartPos[p]));
        }
        for (DbId c = SceneDb.ObjSeamAppendHead[i]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (!SceneDb.Objects.Valid(c)) break;
            ParityDbObject(outp, key, c, SceneDb.ObjModel[c.Index]?.Nodes[0].Name ?? "?", depth + 1);
        }
    }

    /// <summary>The instance environment map, which is what <c>PartTriMesh::Draw</c> @0x4513d0 routes on
    /// (<c>gob+0x480</c>): <c>CreateBodyParts</c> hands the appearance's ENVMAP column to
    /// <c>Gob::SetEnvironmentMap</c>, and the seam keeps it as the object's name plus texture slot 3 on every
    /// part. A creature with no shine is this line reading <c>-</c>. One line per record, outside the
    /// <c>cre=</c> format, so the two modes' body lines still diff clean.</summary>
    private static void ParityDbEnv(List<string> outp, string key, int i, int first, int count)
    {
        int slotted = 0;
        for (int p = first; p < first + count; p++) if (SceneDb.PartTexId[p * 4 + 3] != 0) slotted++;
        outp.Add($"env cre={key} obj={i} envmap={SceneDb.ObjEnvMap[i] ?? "-"} slot3={slotted}/{count}");
    }

    /// <summary>The DB answer to mode 0's <c>mesh=</c>, which asks <c>Part::AsPartTriMesh</c>: that slot is
    /// inherited by <c>PartSkin</c>, <c>PartDanglyMesh</c> and <c>PartAnimMesh</c> too, so a skinned body part
    /// answers YES. Counting only <see cref="PartKind.Mesh"/> printed <c>mesh=0</c> for every skin node and
    /// made the dump diff on parts that in fact match.</summary>
    private static bool ParityHasMesh(int p) => SceneDb.PartKindOf[p] switch
    {
        PartKind.Mesh or PartKind.Skin or PartKind.Dangly or PartKind.AnimMesh or PartKind.Aabb
            or PartKind.Merged => true,
        _ => false,
    };

    private static int ParityAppendCount(DbId id)
    {
        int n = 0;
        for (DbId c = SceneDb.ObjSeamAppendHead[id.Index]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (!SceneDb.Objects.Valid(c)) break;
            n++;
        }
        return n;
    }

    /// <summary>The rows the part actually shades with: its own when a <c>ReplaceTexture</c> matched it,
    /// otherwise its object's. All-zero rows mean no scheme was ever recorded, which is the inheritance bug's
    /// signature.</summary>
    private static string ParityDbRows(int obj, int part, bool own, out int count)
    {
        var sb = new System.Text.StringBuilder();
        bool any = false;
        for (int l = 0; l < SceneDb.PltLayers; l++)
        {
            int r = own ? SceneDb.PartPltRow[part * SceneDb.PltLayers + l]
                        : SceneDb.ObjPltRow[obj * SceneDb.PltLayers + l];
            if (r != 0) any = true;
            if (l > 0) sb.Append(',');
            sb.Append(r);
        }
        count = any ? SceneDb.PltLayers : 0;
        return any ? sb.ToString() : "-";
    }

    private static string ParityLine(string key, string graft, string node, bool enabled, bool mesh,
                                     string tex, int pltCount, string rows, Vector3 lpos)
        => $"cre={key} graft={graft} node={node} en={(enabled ? 1 : 0)} mesh={(mesh ? 1 : 0)} "
         + $"tex={tex} plt={pltCount} rows={rows} lpos={F3(lpos)}";
}
