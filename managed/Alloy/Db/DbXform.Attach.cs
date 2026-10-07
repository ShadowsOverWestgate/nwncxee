using System.Numerics;
using CX.Alloy.Seam;
using CX.Engine;

namespace CX.Alloy.Db;

/// <summary>CX-120 (gui): <b>the attachment placement</b> -- the one term an attached object has and the
/// transform step did not apply. Two kinds of edge reach it, because the game places GUI controls two ways.
///
/// <para><b>1. The seam's own attach slots.</b> <c>Gob::AttachToObject</c> @0x43e0e0 does TWO things, and only
/// the second is a behaviour: it writes the parent part's world transform straight into the child's placement
/// words (<c>Part::GetWorld(part, this+0xa8, this+0xb4)</c>, the first statement after the part resolves), and
/// then installs a <c>CAurBehaviorAttach*</c> that repeats that write every animate. The seam records the edge
/// and builds no behaviour (ADR 0028 leaves behaviour lifetime with the game), so nothing repeated the write:
/// a quickbar button's record said <c>pos=(0,0,0)</c> and it drew at the panel camera's origin -- the
/// bottom-left corner of the screen -- while the panel frame it hangs from drew correctly.</para>
///
/// <para><b>2. A behaviour the game installed and CX has no edge for.</b> <c>CGuiObject::AddToPanel</c>
/// places every control of every panel with
/// <c>GuiAurAttachToObject(control, panel, "root", &amp;(Obj_X, Obj_Y, Obj_Z))</c> -- the offset read straight
/// out of the panel's GFF. That free function @0x68b050 is NOT hooked: it runs on the facade, resolves the
/// parent's root part through the <c>GetPart</c> slot, calls <c>Part::GetWorld</c> once, and installs a
/// <c>CAurBehaviorCrossSceneAttach(WithOffset)</c>. It never touches <c>ObjAttachTo</c>, and its one direct
/// write lands before the panel itself is placed, so the control's shell held <c>(0,0,0)</c> too (MEASURED,
/// .scratch/live/gui-db8) and the per-frame <c>Control</c> that would have fixed it is game code DbAnim must
/// not call in DB mode -- it reaches the engine's <c>Part::GetWorld</c>, which mode 0's
/// <c>BehaviorSolvers</c> has chained, on a facade part. The behaviour is READ here instead: its part at
/// <c>+0x30</c> and its offset at <c>+0x84</c> are declared rows carrying both OSes' values, and the
/// placement is CX's own arithmetic.</para>
///
/// <para><b>It overwrites, for text objects too</b> (MEASURED, .scratch/live/gui-db5). CX keeps a text
/// object's position offset in the same <c>ObjPos</c> the attach overwrites, so composing on top of it instead
/// put every quickbar label hundreds of units off screen. The engine agrees with the overwrite:
/// <c>StringGob::SetPositionOffset</c> @0x5923e0 writes the offset onto the behaviour and ONLY when that
/// behaviour's type is <c>0x3ea</c> (<c>CAurBehaviorAttachTranslate</c>), so with no behaviour built there is
/// no offset for the engine to apply either. A real offset needs a column of its own (text stream).</para>
///
/// <para>Both edges stand down the moment DbAnim itself owns the object's behaviour, because the solve would
/// then do the same write with its own subclass offset and a second one here would cancel it.</para>
///
/// <para>NEW FILE, not an edit to seam-anim's <c>DbXform.cs</c>: that file keeps ONE named line.</para></summary>
public static unsafe partial class DbAnim
{
    /// <summary>How deep the parent chain is followed before the placement gives up. A STACK guard, not a
    /// budget: the character-select list attaches each <c>ctl_pre_btn_char</c> to the previous one, so the
    /// chain is as long as the player has characters (39 on the harness's profile, which a cap of 32 cut off
    /// -- the 2242 "TOO DEEP" placements a session). A true cycle is caught exactly by
    /// <see cref="_inSnap"/> at any depth, so this only has to be past any chain content can build.</summary>
    private const int MaxAttachSnap = 256;

    private static int _snapDepth;
    private static long _attachSnaps, _attachDeep, _attachBadPart, _attachCycle, _behPlaced, _behForeign, _textOffsets;
    /// <summary>This object's placement is on the stack right now: reaching it again IS the cycle, whatever
    /// the depth.</summary>
    private static readonly bool[] _inSnap = new bool[SceneDb.MaxObjects];

    /// <summary>Text position offsets applied at compose this session (CX-119).</summary>
    internal static long TextOffsetsApplied => _textOffsets;

    /// <summary>Place <paramref name="i"/> on the part it rides, from whichever of the two edges it has.
    /// Called at the top of the compose, before the staleness test -- which is what makes a MOVING parent
    /// recompose the child, since the move is noticed here and reported through <see cref="Moved"/>.</summary>
    internal static void SnapAttached(int i)
    {
        // A game-made CAurBehavior already writes this object's placement every animate (its own subclass
        // offset included), and the solve runs before the pose -- but only once it is actually SOLVING: one merely
        // installed on an object nothing steps (a GUI panel whose scene never animates) is placed from here.
        if (BehaviourPlacing(i)) return;
        Vector3 offset = default;
        int part;
        DbId up = SceneDb.ObjAttachTo[i];
        if (SceneDb.Objects.Valid(up))
        {
            part = ParentPart(up, SceneDb.ObjAttachPart[i]);
            // A TEXT object is the one case that still rides the parent's ROOT when its named anchor did not
            // resolve. Vanilla leaves such a label where its own SetPosition put it -- but CX cannot: measured
            // (.scratch/live/menu-db9), the value the game hands CAurString::SetPosition for these is a
            // PIXEL-space pair ((175,145), (175,135) a line apart) that is not a placement in the panel's
            // world at all, so honouring it puts the label nowhere. Riding the root is the engine's own
            // fallback for a NULL part name and lands the label on its control; what is still missing is the
            // text BOX that would then centre it there (see the note on the box below).
            if (part < 0 && SceneDb.ObjKindOf[i] == ObjKind.Text) { part = RootPart(up); ReportRooted(i, up); }
            if (part < 0) { _attachBadPart++; ReportLostAnchor(i, up); return; }
            LinkChild(i, up);
        }
        else if (!ShellAttach(i, out part, out offset)) return;

        if (_inSnap[i]) { _attachCycle++; ReportDeep(i, "closes a CYCLE"); return; }
        if (_snapDepth >= MaxAttachSnap) { _attachDeep++; ReportDeep(i, $"is deeper than {MaxAttachSnap} hops"); return; }
        DbId owner = SceneDb.PartObj[part];
        if (SceneDb.Objects.Valid(owner))
        {
            _snapDepth++;
            _inSnap[i] = true;
            // try/finally, not optimism: a throw that left the flag set would refuse this object's placement
            // for the rest of the session and report a cycle that is not there.
            try { ComposeIndex(owner.Index); }
            finally { _inSnap[i] = false; _snapDepth--; }
        }
        Matrix4x4 m = SceneDb.PartWorld[part];
        var p = new Vector3(m.M41, m.M42, m.M43) + offset;
        Quaternion q = RotOf(m);
        _attachSnaps++;
        _byMode[SceneDb.ObjAttachMode[i] & 7]++;
        p += TextOffset(i, q);
        if (SceneDb.ObjPos[i] == p && SceneDb.ObjRot[i] == q) return;
        // Through the facade, not into the columns alone: Gob::GetPosition answers the SHELL word and the
        // game's own code reads it by interior address, so a snap that moved only the column would be a lie.
        Facade.SetXform(SceneDb.Objects.IdAt(i), SceneDb.ObjFacade[i], p, q);
        Moved(i);
    }

    /// <summary>CX-119: a text object's position offset, applied the way
    /// <c>CAurBehaviorAttachTranslate::Control</c> @0x4a2960 applies it -- rotated into the frame the base
    /// attach has just snapped onto, then added to the position:
    /// <c>SetPosition(GetPosition() + GetOrientation() * offset)</c>. Zero for everything else.
    /// <para>The offset lives on the behaviour in the engine, so it is applied only while the behaviour it was
    /// set for is still the installed one -- one pointer load, no vtable call on the compose path.</para></summary>
    private static Vector3 TextOffset(int i, in Quaternion q)
    {
        nint owner = SceneDb.TextOffsetBehavior[i];
        if (owner == 0)
        {
            // The usual case in a GUI panel: the translate attach is the DATABASE's own edge, so there is no
            // behaviour pointer to key the lifetime on. The attach-mode column stands in for it -- the next
            // attach clears the offset, which is exactly when the engine's behaviour would have been replaced.
            if (SceneDb.ObjAttachMode[i] != SceneDb.AttachTranslate) return default;
            Vector3 own = SceneDb.TextPosOffset[i];
            if (own == default) return default;
            _textOffsets++;
            return Vector3.Transform(own, q);
        }
        nint shell = SceneDb.ObjFacade[i];
        if (shell == 0 || *(nint*)(shell + SeamLayout.GobBehavior) != owner)
        { SceneDb.TextOffsetBehavior[i] = 0; return default; }
        _textOffsets++;
        return Vector3.Transform(SceneDb.TextPosOffset[i], q);
    }

    /// <summary>The second edge: a <c>CAurBehaviorCrossSceneAttach</c> the game installed through
    /// <c>SetBehavior</c> on an object CX has no attach edge for. The behaviour is READ, never called.
    /// <paramref name="offset"/> is the <c>WithOffset</c> subclass's constant world vector, added unrotated
    /// exactly as its <c>Control</c> @0x68c280 adds it.</summary>
    private static bool ShellAttach(int i, out int part, out Vector3 offset)
    {
        part = -1; offset = default;
        nint shell = SceneDb.ObjFacade[i];
        if (shell == 0) return false;
        nint beh = *(nint*)(shell + SeamLayout.GobBehavior);
        if (beh == 0) return false;
        DbBehaviorKind kind = Kind(beh);
        if (kind != DbBehaviorKind.CrossSceneAttach && kind != DbBehaviorKind.CrossSceneAttachOffset)
        { _behForeign++; return false; }
        var att = new CAurBehaviorAttach((System.IntPtr)beh);
        if (att.Part == System.IntPtr.Zero || ResolvePart == null) return false;
        int p = ResolvePart(att.Part);
        if ((uint)p >= SceneDb.MaxParts || !SceneDb.Parts.LiveAt(p)) { _attachBadPart++; return false; }
        if (kind == DbBehaviorKind.CrossSceneAttachOffset)
            offset = new CAurBehaviorCrossSceneAttach((System.IntPtr)beh).Offset;
        part = p;
        // The link, from the other side: this object is attached to whatever owns that part, and the death
        // edge sweeps on it. Noted here as well as at the solve, because a control whose behaviour has not
        // solved yet is still a child -- and vanilla's own notice (the callback UpdateScenesAttach registers
        // on the parent) exists from the behaviour's CONSTRUCTOR, not from its first Control.
        NoteAttached(SceneDb.PartObj[p]);
        LinkChild(i, SceneDb.PartObj[p]);
        if (_attachParent[i].IsNull) _attachParent[i] = SceneDb.PartObj[p];
        _behPlaced++;
        return true;
    }

    /// <summary>The part an attachment rides, or -1 for an attachment that is not one.
    /// <para><b>A <c>-1</c> is NOT "the root"</b>, and reading it that way is what put every main-menu button's
    /// label on its button's bottom-left corner. <c>Gob::AttachToObject</c> @0x43e0e0 resolves a NULL part name
    /// to the parent's root (<c>*(CAurPart**)(parent+0x90)</c>) and attaches; a name that does NOT resolve makes
    /// it clear the behaviour and <c>return 0</c> WITHOUT touching the owner's placement. Both of CX's resolvers
    /// answer the root's index for a null or empty name, so a <c>-1</c> can only be the second case -- and the
    /// menu's labels are exactly that: <c>CGuiCharGenRadioButton</c> and its kin attach the label to a part
    /// named <c>"text"</c>, which <c>ctl_btn_pre_176</c> does not have (its nodes are the root, <c>text0</c>,
    /// <c>text1</c>, <c>Plane248</c>, <c>Plane40</c>). Vanilla leaves such a label where its own
    /// <c>SetPosition</c> put it; placing it on the parent's root moved it to the button's origin every
    /// frame.</para>
    /// <para>A named part that has since gone is refused for the same reason: the object is not riding
    /// anything any more, and the root is not where it was.</para></summary>
    /// <summary>No-silent-fallbacks: a rider whose anchor no longer resolves is NOT placed, and it keeps the
    /// world matrix it last had -- which looks like a body part standing beside its creature, with nothing in
    /// the log. Said once per object, with the anchor it wanted and what that index holds now.</summary>
    private static void ReportLostAnchor(int i, DbId up)
    {
        if (_saidLost.Count >= MaxLostSaid || !_saidLost.Add(i)) return;
        int part = SceneDb.ObjAttachPart[i];
        string holder = (uint)part < SceneDb.MaxParts && SceneDb.Parts.LiveAt(part)
                      ? "o=" + SceneDb.PartObj[part].Index : "nothing (dead slot)";
        _log?.Always($"DbXform: \"{SceneDb.ObjModel[i]?.Name ?? "-"}\" (o={i}) rides part {part} of "
                   + $"\"{SceneDb.ObjModel[up.Index]?.Name ?? "-"}\" (o={up.Index}) by the name "
                   + $"\"{SceneDb.ObjAttachNode[i] ?? "(root)"}\", and that part belongs to {holder} -- it is "
                   + "NOT PLACED and keeps the transform it last had. Counted on /alloy as 'no parent part'.");
    }

    private static readonly System.Collections.Generic.HashSet<int> _saidLost = new();
    private const int MaxLostSaid = 32;

    /// <summary>No-silent-fallbacks for the OTHER half of a lost anchor: a label whose named part did not
    /// resolve rides its parent's ROOT instead, which places it but not where the panel meant. Said once per
    /// object, bounded, with the label's own first line so the panel it belongs to is identifiable.</summary>
    private static void ReportRooted(int i, DbId up)
    {
        if (_saidRooted.Count >= MaxLostSaid || !_saidRooted.Add(i)) return;
        TextDoc? d = TextObjects.Of(SceneDb.Objects.IdAt(i));
        string text = d != null && d.LineCount > 0
                    ? System.Text.Encoding.Latin1.GetString(d.Lines[0].Bytes, 0, d.Lines[0].Length) : "";
        _log?.Always($"DbXform: the label \"{text}\" (o={i}) asked for a part of "
                   + $"\"{SceneDb.ObjModel[up.Index]?.Name ?? "-"}\" (o={up.Index}) that does not resolve, so it "
                   + $"rides that object's ROOT -- its place is then its position offset alone "
                   + $"(box {d?.MaxWidth ?? 0f:F2}x{d?.MaxHeight ?? 0f:F2}, alignH {d?.AlignH ?? 0f:F2}).");
    }

    private static readonly System.Collections.Generic.HashSet<int> _saidRooted = new();

    /// <summary>Is <paramref name="part"/> a part of <paramref name="up"/>'s tree? The engine grew ONE part
    /// range per gob, so "belongs to the parent" was a single compare; CX makes every <c>Gob::Append</c>ed
    /// piece a child OBJECT, so a name the game resolved on a grafted body part answers with a part the parent
    /// does not own -- and the attachment was then refused as a lost anchor. A part of the parent's
    /// COMPOSITION is a part of the parent, which is what the engine's one tree says.</summary>
    private static int ParentPart(DbId up, int part)
    {
        if (part < 0 || part >= SceneDb.MaxParts || !SceneDb.Parts.LiveAt(part)) return -1;
        DbId owner = SceneDb.PartObj[part];
        if (owner.Value == up.Value) return part;
        for (int hop = 0; hop < MaxAttachSnap && SceneDb.Objects.Valid(owner); hop++)
        {
            if ((SceneDb.ObjSeamFlags[owner.Index] & (uint)SeamFlag.Composed) == 0) break;
            owner = SceneDb.ObjAttachTo[owner.Index];
            if (owner.Value == up.Value) { _composedAnchors++; return part; }
        }
        return -1;
    }

    /// <summary>Anchors that resolved through the parent's COMPOSITION rather than its own part range: how much
    /// of a creature the game addresses through a grafted piece, and zero if it never does.</summary>
    private static long _composedAnchors;

    /// <summary>The parent's root part, which is what <c>Gob::AttachToObject</c> resolves a NULL part name to.
    /// <para><b>The label's box, not its origin, is what is still wrong at the main menu.</b> With the origin
    /// here -- the button's root, its left edge at x 5.51 -- and the label's own <c>AlignH</c> of 0.5, a
    /// <c>MaxWidth</c> of the button's 1.74 would centre the text at 6.38, which is mode 0's 639 px. The box
    /// arrives as 0 (measured: <c>box=0.000x-0.000 align=0.50/0.50</c>), so <c>TextQuads</c> centres each
    /// label on its own origin instead and three labels of different lengths share one centre.
    /// <c>AttachCornersToObjects(parent, "text0", "text1")</c> is what fills it -- the engine's own idiom for
    /// this control family (<c>CGuiCharGenRadioButton</c> @0x4d9a40 and its kin do exactly
    /// <c>AttachToObject(label, parent, "text", 0)</c> then that call) and <c>ctl_btn_pre_176</c> carries both
    /// dummies. It never reaches CX for these buttons: TEXT stream.</para></summary>
    private static int RootPart(DbId up)
    {
        int first = SceneDb.ObjPartFirst[up.Index];
        return SceneDb.ObjPartCount[up.Index] > 0 && (uint)first < SceneDb.MaxParts
               && SceneDb.Parts.LiveAt(first) ? first : -1;
    }

    /// <summary>The chain ran past <see cref="MaxAttachSnap"/>, or closed on itself: walk it and say what it
    /// is, ONCE. The two want opposite fixes -- a bigger cap, or the seam slot that recorded an edge the engine
    /// would have replaced -- so the walk names every hop and marks the object the chain returns to.</summary>
    private static void ReportDeep(int i, string why)
    {
        // Bounded per OBJECT, not once per session: a single global latch was spent by the boot self-check's
        // own synthetic chain, and every real refusal for the rest of the run was then silent.
        if (_saidDeep.Count >= MaxLostSaid || !_saidDeep.Add(i)) return;
        string s = $"DbAnim: an attach chain {why} -- the objects on it are "
                 + "NOT PLACED (totals on /alloy). The chain from o=" + i + ":";
        var seen = new System.Collections.Generic.HashSet<int>();
        int at = i;
        for (int hop = 0; hop < 64; hop++)
        {
            if (!seen.Add(at)) { s += $"  -> o={at} **CYCLE CLOSES HERE**"; break; }
            DbId up = SceneDb.ObjAttachTo[at];
            int part = SceneDb.ObjAttachPart[at];
            string owner = (uint)part < SceneDb.MaxParts && SceneDb.Parts.LiveAt(part)
                         ? SceneDb.PartObj[part].Index.ToString() : "-";
            s += $"  o={at} ({SceneDb.ObjKindOf[at]} \"{SceneDb.ObjModel[at]?.Name ?? "-"}\""
               + $" scene={SceneDb.ObjScene[at].Index} parts={SceneDb.ObjPartCount[at]})"
               + $" -attached to-> o={up.Index} at part {part} (owned by o={owner})";
            if (!SceneDb.Objects.Valid(up)) { s += "  [chain ends: parent not live]"; break; }
            at = up.Index;
        }
        _log?.Always(s);
    }

    private static readonly System.Collections.Generic.HashSet<int> _saidDeep = new();

    /// <summary>Placements by attach VARIANT (<see cref="SceneDb.ObjAttachMode"/>), so "which subclass does
    /// the game actually install" is a measurement rather than a guess. <c>Gob::AttachToObject</c> @0x43e0e0's
    /// switch has five arms and CX composes all of them as the plain one plus, for translate, the position
    /// offset -- what rotate's euler offset, no-orientation's kept orientation and GUI-scale's divide are worth
    /// is exactly what these counts say.</summary>
    private static readonly long[] _byMode = new long[8];

    private static string Variants()
    {
        string s = "";
        for (int m = 0; m < 5; m++)
            if (_byMode[m] != 0) s += $"{(s.Length == 0 ? "" : " ")}{Name(m)}={_byMode[m]}";
        return s.Length == 0 ? "" : $", variants {s}";
    }

    private static string Name(int m) => m switch
    {
        0 => "plain", 1 => "translate", 2 => "rotate", 3 => "no-orientation", 4 => "gui-scale", _ => "?",
    };

    // ---- the parent's side of the edge: who has to be carried when this object moves ----

    /// <summary>Parent -&gt; its first attached child, and child -&gt; the next child of the same parent: the
    /// attachment list vanilla keeps on the gob itself (<c>Gob::AddAttachment</c> @0x4424a0, called from every
    /// <c>CAurBehaviorAttach</c> constructor and dropped by its destructor). CX needs it for the engine's own
    /// reason: <c>CAurBehaviorAttach::Control</c> @0x4a2810 re-derives the child's placement from the parent's
    /// part EVERY animate, so a parent that moves carries its children with it. The compose visits only what
    /// changed, and a child is not marked by its parent's move -- without this list a GUI label was placed once,
    /// at the attach, and stayed there when <c>CGuiMan::SetGuiSize</c> @0x57efc0 re-centred the panel under it
    /// on a window resize (the user's repro: halve the window and the text floats where the centre used to be).
    /// <para>Index 0 is the sentinel, never a live record, so the lists need no initialisation pass.
    /// <see cref="_kidOf"/> is the parent a child is linked under right now, so a re-attach MOVES the link
    /// instead of leaving the child on two lists.</para></summary>
    private static readonly int[] _kidHead = new int[SceneDb.MaxObjects];
    private static readonly int[] _kidNext = new int[SceneDb.MaxObjects];
    private static readonly int[] _kidOf = new int[SceneDb.MaxObjects];
    private static int _kidDepth;
    private static long _kidPlaced, _kidDeep;

    /// <summary>Record that <paramref name="child"/> rides <paramref name="parent"/>, from whichever edge read
    /// the link. Cheap to repeat: an unchanged link is one compare.</summary>
    private static void LinkChild(int child, DbId parent)
    {
        int p = SceneDb.Objects.Valid(parent) ? parent.Index : 0;
        if (_kidOf[child] == p) return;
        UnlinkChild(child);
        if (p == 0 || p == child) return;
        _kidNext[child] = _kidHead[p];
        _kidHead[p] = child;
        _kidOf[child] = p;
    }

    /// <summary>Take <paramref name="child"/> off its parent's list (<c>Gob::RemoveAttachment</c>'s job).</summary>
    private static void UnlinkChild(int child)
    {
        int p = _kidOf[child];
        _kidOf[child] = 0;
        if (p != 0)
        {
            if (_kidHead[p] == child) _kidHead[p] = _kidNext[child];
            else
                for (int at = _kidHead[p]; at != 0; at = _kidNext[at])
                    if (_kidNext[at] == child) { _kidNext[at] = _kidNext[child]; break; }
        }
        _kidNext[child] = 0;
    }

    /// <summary>Every column this file keeps for <paramref name="i"/>, for a RECYCLED record: the link to its
    /// old parent, and the children that were riding the record's previous tenant.</summary>
    internal static void ForgetKids(int i)
    {
        UnlinkChild(i);
        for (int c = _kidHead[i]; c != 0; )
        {
            int next = _kidNext[c];
            _kidOf[c] = 0; _kidNext[c] = 0;
            c = next;
        }
        _kidHead[i] = 0;
    }

    /// <summary>Carry <paramref name="i"/>'s attached children, now that its own matrices are current. This is
    /// what <c>CAurBehaviorAttach::Control</c> does for the engine, one frame later than the parent's move
    /// rather than at it.</summary>
    private static void PlaceChildren(int i)
    {
        int c = _kidHead[i];
        if (c == 0) return;
        if (_kidDepth >= MaxAttachSnap) { _kidDeep++; return; }
        _kidDepth++;
        try
        {
            // next read BEFORE the compose: that compose may re-link or retire this child.
            for (int next; c != 0; c = next)
            {
                next = _kidNext[c];
                if (!SceneDb.Objects.LiveAt(c)) { UnlinkChild(c); continue; }
                if (_kidOf[c] != i) continue;
                // The child whose own climb brought us here is on the snap stack, and that climb places it
                // with this very matrix on the way back out. Carrying it again re-enters its SnapAttached,
                // which reads the stack flag as a CYCLE and refuses -- half a million refusals a session,
                // every one of them a placement that then happened anyway, and a real cycle lost among them.
                if (_inSnap[c]) continue;
                _kidPlaced++;
                ComposeIndex(c);
            }
        }
        finally { _kidDepth--; }
    }

    /// <summary>Attachment placements this session by edge, and the refusals: a chain that closed on itself,
    /// one past <see cref="MaxAttachSnap"/>, and a link whose part is gone.</summary>
    internal static string AttachStats()
        => $"attach: {_attachSnaps} placed ({_behPlaced} off a game-installed cross-scene behaviour)"
         + (_kidPlaced == 0 ? "" : $", {_kidPlaced} carried by a parent that moved")
         + Variants()
         + (_composedAnchors == 0 ? "" : $", {_composedAnchors} anchored on a GRAFTED piece of the parent")
         + (_kidDeep == 0 ? "" : $", {_kidDeep} carry(s) past the depth guard")
         + (_behForeign == 0 ? "" : $", {_behForeign} snap(s) left to a non-attach behaviour that has not solved yet")
         + (_attachCycle == 0 ? "" : $", {_attachCycle} CYCLES (not placed)")
         + (_attachDeep == 0 ? "" : $", {_attachDeep} TOO DEEP (not placed)")
         + (_attachBadPart == 0 ? "" : $", {_attachBadPart} with no parent part (not placed)");
}
