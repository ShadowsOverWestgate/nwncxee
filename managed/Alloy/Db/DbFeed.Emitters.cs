namespace CX.Alloy.Db;

/// <summary>What the non-mesh consumers need from the FRAME rather than from a record -- today just its
/// sequence number. The particle simulation is stepped per world VIEW, so a frame with two world views must
/// re-enrol the same particles rather than advance them twice; this token says which is which.</summary>
public static partial class DbFeed
{
    /// <summary>Frames this feed has folded. Monotonic and cheap; the emitter walk compares it against its own
    /// last-stepped value.</summary>
    internal static int FrameSeq => _frames;
}
