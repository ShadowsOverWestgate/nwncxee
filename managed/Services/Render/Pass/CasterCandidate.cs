using System;

namespace Sowx.Services.Render;

/// <summary>#93: one part offered to the owned caster pass at enrollment. Only the identity that is stable
/// for the lifetime of the ownership lives here -- the index range and the model matrix are re-read live
/// every frame, because both move under us (Repack, animation).</summary>
internal struct CasterCandidate
{
    /// <summary>The PartTriMesh*, plus the pair the collect loop revalidates it against.</summary>
    public IntPtr Tri, Node, Mat;
    public IntPtr Vdo;
    public int Item, Prim;
    /// <summary>Matrix-class part (node+0x80 &amp; 0x1e): its geometry is object-space and the model matrix at
    /// node+0x18 places it. False = world-space vertices, model = identity. This is the ONLY thing that
    /// differs between a tile and a placeable on the submission path -- and it is a property of the
    /// geometry, not of the content kind.</summary>
    public bool Mx;
    /// <summary>The material declares an environment map, which is the ONE authoring where a diffuse alpha
    /// channel does not mean coverage: <c>fEnvMapLevel = 1 - alpha</c> (inc_standard.shd:248), i.e. a graded
    /// alpha is REFLECTIVITY on a solid surface. Such a caster is still alpha-tested -- its texture may carry
    /// coverage too -- but at a threshold low enough that only empty texels punch through, so a polished
    /// floor keeps its shadow. Everything else with alpha takes the engine's own 0.2 cutout.</summary>
    public bool EnvAlpha;
    /// <summary>#135: a <c>PartDanglyMesh</c>. Its depth is only true for the frame that solved it -- the
    /// same property a creature's pose has, arrived at from the other direction: a creature moves its matrix
    /// and keeps its vertices, a dangly keeps its matrix and moves its vertices. Either way the caster
    /// caches, which key on matrix motion, must not hold it.</summary>
    public bool Dangly;
}
