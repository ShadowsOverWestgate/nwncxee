using CX.Framework;

namespace CX.Engine;

public readonly unsafe partial struct StringGob
{
    /// <summary>The <c>CAurString</c> sub-object of this gob -- what <c>NewCAurString</c> hands back and what
    /// every holder (a text bubble's label, an icon's count label) stores.</summary>
    public CAurString AurString => new(Ptr + EngineLayout.StringGobReaderAdjust);
}

public readonly unsafe partial struct CAurString
{
    /// <summary>The <c>this</c> the exported <c>StringGob</c> line readers want: the object base on Linux, the
    /// sub-object itself on MSVC (<see cref="EngineLayout.StringGobReaderAdjust"/>).</summary>
    public StringGob Reader => new(Ptr - EngineLayout.StringGobReaderAdjust);
}
