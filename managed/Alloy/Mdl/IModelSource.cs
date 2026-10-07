namespace CX.Alloy.Mdl;

/// <summary>CX-113: where a model's bytes come from, in the halves the threading demands.
/// <para><see cref="Locate"/> and <see cref="StillValid"/> run on the GAME thread and are the only halves
/// allowed to touch anything the game owns -- the engine's resource tables for a session, the filesystem for
/// <c>tools/mdlcheck</c>. <see cref="Locate"/> answers an opaque token that must be a VALUE record: the worker
/// reads it, so nothing in it may point into memory the game can free. <see cref="Parse"/> runs on
/// <c>cx-model</c> and does the read and the parse.</para>
/// <para>This is also the seam that keeps <c>Alloy/Mdl/</c> engine-free, which is what lets the model cache and
/// the parser be exercised off-device with no client at all.</para></summary>
public interface IModelSource
{
    /// <summary>Game thread: where the model named <paramref name="resref"/> lives, or null with
    /// <paramref name="why"/> set. Never reads the bytes.</summary>
    object? Locate(string resref, out string? why);

    /// <summary>Worker thread: read and parse what <see cref="Locate"/> found. Null with
    /// <paramref name="why"/> set is a refusal the cache counts and logs.</summary>
    ModelDef? Parse(object token, out string? why);

    /// <summary>Game thread, before the parsed model is filed: does <paramref name="token"/> still name the
    /// resource it named when it was located? A source whose lookup cannot move under a read (a plain
    /// filesystem, a dictionary) has nothing to re-prove and keeps this default; the engine feed re-runs the
    /// resource lookup and compares the <c>RESID</c>, so a resref aliased or a key table rebuilt mid-read costs
    /// a counted miss rather than the wrong model filed under the right name.</summary>
    bool StillValid(object token, out string? why) { why = null; return true; }
}
