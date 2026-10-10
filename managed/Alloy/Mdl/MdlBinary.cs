using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;

namespace CX.Alloy.Mdl;

/// <summary>The binary <c>.mdl</c> reader, ported from the engine's loader (@0x4bee60 model, @0x4bd400 node,
/// @0x4c01b0 part); both the pre-EE BIF binaries and the engine's own EE output parse. The file is <c>{0,
/// modelDataSize, rawDataSize}</c>, a MODEL BLOCK (a 32-bit image of the old <c>Model</c>, pointers as offsets
/// from its base), then a RAW DATA BLOCK (offsets from ITS base, per field as the loader has it).</summary>
internal static class MdlBinary
{
    // ---- model block (verified in FUN_005bee60; the engine field each lands in is in the comment) ----
    private const int MWrapper = 12;              // not-an-offset: the {0,modelSize,rawSize} prefix
    private const int MName = 0x08;               // char[64]   -> Model+0x10; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int MRoot = 0x48;               // u32 offset -> Model+0x50; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int MNodeCount = 0x4c;          // u32        -> Model+0x58; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int MGeomType = 0x6c;           // u8         -> Model+0x88 (2 = model, 5 = animation); not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int MClassification = 0x72;     // u8         -> Model+0x8e (the `classification` keyword); not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int MFog = 0x73;                // u8         -> Model+0x8f (`ignorefog` clears it); not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int MChildModels = 0x74;         // u32        -> Model+0x90; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int MAnimOffsets = 0x78;        // u32 offset of a u32[] of animation block offsets; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int MAnimCount = 0x7c;          // u32; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int MBMin = 0x88;               // float[3]   -> Model+0xb0; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int MBMax = 0x94;               // float[3]   -> Model+0xbc; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int MRadius = 0xa0;             // float      -> Model+0xc8; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int MAnimScale = 0xa4;          // float      -> Model+0xcc; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int MSuper = 0xa8;              // char[64]   -> Model+0xd0; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    /// <summary>An animation block repeats the model block's prefix and then states its own fields; the engine
    /// copies them in <c>FUN_005bee60</c>'s per-animation loop at the same relative offsets.</summary>
    private const int AnimLength = 0x70, AnimTransTime = 0x74, AnimRoot = 0x78,   // float, float, char[64]; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
                      AnimEventOffset = 0xb8, AnimEventCount = 0xbc;              // u32, u32; 0x24-byte records
    /// <summary>One animation event: a time then a 32-byte name (the engine copies <c>count * 0x24</c>).</summary>
    private const int EventBytes = 0x24, EventNameChars = 0x20;   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object

    // ---- node block (verified at @0x5c01b0 and in @0x5bd400's node-type-1 branch) ----
    private const int NHeader = 0x70;             // node-specific data starts here; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int NInheritColor = 0x18;       // u32 -> MdlNode+0x30; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int NPartNumber = 0x1c;         // u32 -> MdlNode+0x34; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int NName = 0x20;               // char[32] -> MdlNode+0x38; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int NChildOffset = 0x48, NChildCount = 0x4c;   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int NCtlOffset = 0x54, NCtlCount = 0x58;   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int NCtlDataOffset = 0x60, NCtlDataCount = 0x64;   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int NType = 0x6c;               // u32 -> MdlNode+0x98; not-an-offset: a field of the MDL FILE FORMAT, not of an engine object

    /// <summary>A <c>NewController</c> record as the FILE states it: id, key count, the index into the node's
    /// float array where the key times start, the index where the values start, and a byte whose low nibble is
    /// the floats per key and whose <c>0x10</c> bit says bezier.</summary>
    private const int CtlBytes = 0xc, CtlId = 0, CtlKeyCount = 4, CtlTimeKey = 6, CtlValueKey = 8, CtlFormat = 0xa;   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    /// <summary>The bezier bit in a controller's format byte (<c>AddBezierController</c> ORs in <c>0x100000</c>,
    /// which is bit 4 of that byte).</summary>
    private const int CtlBezier = 0x10;   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int CtlColumns = 0x0f;   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object

    /// <summary>The node-type bit set the file stores at <see cref="NType"/>, and which class the engine makes
    /// for each (<c>FUN_005bd400</c>). <c>1</c> is the header bit every node carries.</summary>
    private const uint TDummy = 1, TLight = 3, TEmitter = 5, TCamera = 9, TReference = 0x11, TTriMesh = 0x21,   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
                       TSkin = 0x61, TAnimMesh = 0xa1, TDangly = 0x121, TAabb = 0x221, TTrigger = 0x421;

    /// <summary>Nodes one model may carry, and how deep its tree may go. A hostile file can point a child
    /// offset at its own node, or at the next node twice forty levels deep; the engine has no bound on either
    /// and dies on the stack. The heaviest real model has a few hundred nodes and nests under ten deep.</summary>
    private const int MaxNodes = 1 << 16, MaxDepth = 256;

    private readonly ref struct Blocks
    {
        public Blocks(ReadOnlySpan<byte> model, ReadOnlySpan<byte> raw, Ctx ctx)
        { Model = model; Raw = raw; C = ctx; }
        public readonly ReadOnlySpan<byte> Model, Raw;
        /// <summary>The mutable half: a ref struct cannot hold it, so it lives behind a class.</summary>
        public readonly Ctx C;
    }

    /// <summary>What the walk has to remember: which node offsets it has already entered (a cycle is a
    /// hostile file, not a model) and how many bounds it refused.</summary>
    internal sealed class Ctx
    {
        internal readonly HashSet<int> Seen = new();
        /// <summary>Counts of what the reader refused: a cyclic or out-of-range child, a count the file
        /// could not supply. Named on the parse's error when nothing else failed.</summary>
        internal int Cycles, Clamped;
    }

    internal static ModelDef? Read(ReadOnlySpan<byte> data, string name, out string? error)
    {
        error = null;
        int modelSize = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int rawSize = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        if (modelSize < MSuper + 64 || (long)MWrapper + modelSize + rawSize > data.Length)
        { error = $"wrapper claims {modelSize}+{rawSize} of {data.Length - MWrapper}"; return null; }
        ReadOnlySpan<byte> model = data.Slice(MWrapper, modelSize);
        ReadOnlySpan<byte> raw = data.Slice(MWrapper + modelSize, rawSize);
        var ctx = new Ctx();
        var b = new Blocks(model, raw, ctx);

        byte geom = model[MGeomType];
        if (geom != 2) { error = $"geometry type {geom} is not a model"; return null; }

        var m = new ModelDef
        {
            Name = name,
            SuperName = Resref(model.Slice(MSuper, 64)),
            Classification = model[MClassification],
            Fog = model[MFog] != 0,
            AnimScale = F(model, MAnimScale),
            BMin = V3(model, MBMin),
            BMax = V3(model, MBMax),
            Radius = F(model, MRadius),
            NodeIdNext = I(model, MNodeCount),
        };
        if (string.Equals(m.SuperName, "null", StringComparison.OrdinalIgnoreCase)) m.SuperName = "";

        var nodes = new List<NodeDef>(Math.Clamp(I(model, MNodeCount), 8, 1024));
        int root = I(model, MRoot);
        if (root <= 0 || root >= modelSize) { error = $"root node offset {root} outside the model block"; return null; }
        ReadNode(b, root, -1, nodes, 0, out string? why);
        if (why != null) { error = why; return null; }
        m.Nodes = nodes.ToArray();
        for (int i = 0; i < m.Nodes.Length; i++) m.NodeByName.TryAdd(m.Nodes[i].Name, i);

        int animCount = I(model, MAnimCount), animAt = I(model, MAnimOffsets);
        if (animCount > 0 && animAt > 0 && (long)animAt + (long)animCount * 4 <= modelSize)
        {
            var anims = new AnimDef[animCount];
            for (int i = 0; i < animCount; i++) anims[i] = ReadAnim(b, I(model, animAt + i * 4));
            m.Anims = anims;
        }
        else if (animCount > 0) ctx.Clamped++;
        m.Refusals = ctx.Cycles + ctx.Clamped;
        if (m.Refusals != 0)
            m.RefusalNote = $"{ctx.Cycles} cyclic/out-of-range node link(s), {ctx.Clamped} count(s) the file cannot supply";
        return m;
    }

    private static AnimDef ReadAnim(in Blocks b, int at)
    {
        var a = new AnimDef
        {
            Name = Resref(b.Model.Slice(at + MName, 64)),
            Length = F(b.Model, at + AnimLength),
            TransTime = F(b.Model, at + AnimTransTime),
            AnimRoot = Resref(b.Model.Slice(at + AnimRoot, 64)),
        };
        int ec = I(b.Model, at + AnimEventCount), eo = I(b.Model, at + AnimEventOffset);
        if (ec > 0 && (eo <= 0 || (long)eo + (long)ec * EventBytes > b.Model.Length)) { b.C.Clamped++; ec = 0; }
        if (ec > 0 && eo > 0)
        {
            var ev = new AnimEvent[ec];
            for (int i = 0; i < ec; i++)
            {
                int r = eo + i * EventBytes;
                ev[i] = new AnimEvent { Time = F(b.Model, r), Name = Resref(b.Model.Slice(r + 4, EventNameChars)) };
            }
            a.Events = ev;
        }
        // An animation's node tree is the same node layout; only the name and the controllers are kept, because
        // an animation binds to whichever instance tree names the same nodes.
        var list = new List<NodeDef>();
        int rootAt = I(b.Model, at + MRoot);
        // An animation's tree is read with the model's own walk, so its refusals land on the same counters;
        // a partial tree is never silent.
        if (rootAt > 0) { ReadNode(b, rootAt, -1, list, 0, out string? why); if (why != null) b.C.Clamped++; }
        var an = new AnimNode[list.Count];
        for (int i = 0; i < list.Count; i++)
            an[i] = new AnimNode
            {
                Name = list[i].Name,
                // The animation-to-node binding is parent-scoped (CX-113), so a track carries its parent's
                // index WITHIN the animation -- which the pre-order walk above already numbered.
                Parent = list[i].Parent,
                Controllers = list[i].Controllers,
                AnimMesh = list[i].AnimMesh,
            };
        a.Nodes = an;
        return a;
    }

    /// <summary>Read one node and, depth first in file order, its children. Returns the node's index.</summary>
    private static int ReadNode(in Blocks b, int at, int parent, List<NodeDef> nodes, int depth, out string? error)
    {
        error = null;
        if (at < 0 || at + NHeader > b.Model.Length) { error = $"node offset {at} outside the model block"; return -1; }
        // Hostile input: a child offset may point at an ancestor (a cycle), at this node (self), or make the
        // same node reachable twice so the walk goes exponential. All three are refused and counted.
        if (depth > MaxDepth || nodes.Count >= MaxNodes || !b.C.Seen.Add(at)) { b.C.Cycles++; return -1; }
        uint type = (uint)I(b.Model, at + NType);
        var n = new NodeDef
        {
            Name = Text(b.Model.Slice(at + NName, 32)),
            Index = nodes.Count,
            Parent = parent,
            PartNumber = I(b.Model, at + NPartNumber),
            FileIndex = nodes.Count,
            InheritColor = I(b.Model, at + NInheritColor) != 0,
            ContentFlags = type,
            Kind = KindOf(type),
            Controllers = ReadControllers(b, at),
        };
        nodes.Add(n);
        MdlBinaryPayload.Read(b.Model, b.Raw, at, type, n, b.C);
        RestPose(n);

        int cc = I(b.Model, at + NChildCount), co = I(b.Model, at + NChildOffset);
        if (cc > 0 && (co <= 0 || (long)co + (long)cc * 4 > b.Model.Length)) { b.C.Clamped++; cc = 0; }
        if (cc > 0)
        {
            var kids = new List<int>(cc);
            for (int i = 0; i < cc; i++)
            {
                int kid = ReadNode(b, I(b.Model, co + i * 4), n.Index, nodes, depth + 1, out error);
                if (error != null) return n.Index;
                if (kid >= 0) kids.Add(kid);
            }
            n.Children = kids.ToArray();
        }
        return n.Index;
    }

    /// <summary>The rest pose the scene layer reads: the position/orientation/scale controllers at key 0.
    /// An orientation key is stored <c>(x, y, z, w)</c> -- the order <c>Parse(AurGenericKey&amp;)</c> leaves
    /// after its axis-angle conversion, which is also <see cref="Quaternion"/>'s.</summary>
    internal static void RestPose(NodeDef n)
    {
        ControllerTrack? p = n.Controller(Ctl.Position);
        if (p is { Values.Length: >= 3 }) n.Position = new Vector3(p.Values[0], p.Values[1], p.Values[2]);
        ControllerTrack? o = n.Controller(Ctl.Orientation);
        if (o is { Values.Length: >= 4 }) n.Orientation = new Quaternion(o.Values[0], o.Values[1], o.Values[2], o.Values[3]);
        ControllerTrack? s = n.Controller(Ctl.Scale);
        if (s is { Values.Length: >= 1 }) n.Scale = s.Values[0];
    }

    private static ControllerTrack[] ReadControllers(in Blocks b, int at)
    {
        int cn = I(b.Model, at + NCtlCount), co = I(b.Model, at + NCtlOffset);
        int dn = I(b.Model, at + NCtlDataCount), dof = I(b.Model, at + NCtlDataOffset);
        if (cn <= 0 || co <= 0) return Array.Empty<ControllerTrack>();
        // Both controller arrays live in the MODEL block (the loader's memcpys take the model base). Every
        // bound is computed in long: a declared count times a stride overflows an int on a hostile file.
        ReadOnlySpan<byte> src = b.Model;
        if ((long)co + (long)cn * CtlBytes > src.Length) { b.C.Clamped++; return Array.Empty<ControllerTrack>(); }
        if (dn < 0 || dof < 0 || (long)dof + (long)dn * 4 > src.Length) { b.C.Clamped++; dn = 0; }
        var outp = new ControllerTrack[cn];
        for (int i = 0; i < cn; i++)
        {
            int r = co + i * CtlBytes;
            int keys = BinaryPrimitives.ReadUInt16LittleEndian(src[(r + CtlKeyCount)..]);
            int tk = BinaryPrimitives.ReadUInt16LittleEndian(src[(r + CtlTimeKey)..]);
            int vk = BinaryPrimitives.ReadUInt16LittleEndian(src[(r + CtlValueKey)..]);
            byte fmt = src[r + CtlFormat];
            bool bez = (fmt & CtlBezier) != 0;
            int cols = fmt & CtlColumns;
            var t = new ControllerTrack
            {
                Type = BinaryPrimitives.ReadInt32LittleEndian(src[(r + CtlId)..]),
                Columns = cols,
                Bezier = bez,
            };
            int per = bez ? cols * 3 : cols;
            if (keys > 0 && cols > 0 && dof > 0 && (long)tk + keys <= dn && (long)vk + (long)keys * per <= dn)
            {
                t.Times = new float[keys];
                t.Values = new float[keys * per];
                for (int k = 0; k < keys; k++) t.Times[k] = F(src, dof + (tk + k) * 4);
                for (int k = 0; k < keys * per; k++) t.Values[k] = F(src, dof + (vk + k) * 4);
            }
            outp[i] = t;
        }
        return outp;
    }

    internal static NodeKind KindOf(uint type) => type switch
    {
        TAabb => NodeKind.Aabb,
        TAnimMesh => NodeKind.AnimMesh,
        TDangly => NodeKind.Dangly,
        TSkin => NodeKind.Skin,
        TTrigger => NodeKind.Trigger,
        TTriMesh => NodeKind.TriMesh,
        TEmitter => NodeKind.Emitter,
        TLight => NodeKind.Light,
        TReference => NodeKind.Reference,
        TCamera => NodeKind.Camera,
        _ => NodeKind.Dummy,
    };

    /// <summary>The node-type bit set a kind writes back, so an ASCII node carries the same
    /// <see cref="NodeDef.ContentFlags"/> a compiled one does.</summary>
    internal static uint FlagsOf(NodeKind k) => k switch
    {
        NodeKind.Aabb => TAabb,
        NodeKind.AnimMesh => TAnimMesh,
        NodeKind.Dangly => TDangly,
        NodeKind.Skin => TSkin,
        NodeKind.Trigger => TTrigger,
        NodeKind.TriMesh => TTriMesh,
        NodeKind.Emitter => TEmitter,
        NodeKind.Light => TLight,
        NodeKind.Reference => TReference,
        NodeKind.Camera => TCamera,
        _ => TDummy,
    };

    /// <summary>The engine's own controller numbering: the byte offset of the field inside the concrete
    /// <c>Part</c> subclass, which is what <c>NewController::Control</c> writes to and what the live comparison
    /// sees in engine memory. The loader's piecewise remap of a file id; the ladder is PER NODE CLASS, in the
    /// engine's own test order -- emitter, then the mesh family, then light, then the generic rule.</summary>
    internal static int EngineControllerId(int file, NodeKind kind)
    {
        if (kind == NodeKind.Emitter)
        {
            if (file >= 0x1b4) return file + 0x5c;
            if (file >= 0x188) return file + 0x48;
            if (file >= 0x164) return file + 0x38;
            if (file >= 0x148) return file + 0x30;
            if (file >= 0x114) return file + 0x24;
            if (file >= 0xe4) return file + 0x1c;
            return Light(file);
        }
        // Every mesh subclass answers AsMdlNodeTriMesh, and so does a trigger (0x421 & 0x21 == 0x21).
        if (kind is NodeKind.TriMesh or NodeKind.Skin or NodeKind.AnimMesh or NodeKind.Dangly
                 or NodeKind.Aabb or NodeKind.Trigger)
        {
            if (file >= 0x80) return file + 0x20;
            if (file >= 0x74) return file + 0x1c;
            return Light(file);
        }
        if (kind == NodeKind.Light) return Light(file);
        return file >= 0x48 ? file + 0x18 : file >= 8 ? file + 8 : file;

        static int Light(int f) => f >= 0x4c ? f + 0x18 : f >= 8 ? f + 8 : f;
    }

    private static int I(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt32LittleEndian(s[at..]);
    private static float F(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadSingleLittleEndian(s[at..]);
    private static Vector3 V3(ReadOnlySpan<byte> s, int at) => new(F(s, at), F(s, at + 4), F(s, at + 8));

    /// <summary>A fixed-width <c>char[]</c> field verbatim, cut at the first NUL: a node name, which the
    /// engine stores with its case and compares case-insensitively.</summary>
    internal static string Text(ReadOnlySpan<byte> s)
    {
        int n = s.IndexOf((byte)0);
        if (n == 0) return "";
        return MdlTok.TextRaw(n < 0 ? s : s[..n]);
    }

    /// <summary>A fixed-width <c>char[]</c> field as a lower-cased string, cut at the first NUL.</summary>
    internal static string Resref(ReadOnlySpan<byte> s)
    {
        int n = s.IndexOf((byte)0);
        if (n == 0) return "";
        return MdlTok.Text(n < 0 ? s : s[..n]);
    }
}
