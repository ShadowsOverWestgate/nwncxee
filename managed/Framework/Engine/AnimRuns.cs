using System;
using CX.Framework;

namespace CX.Engine;

/// <summary><see cref="EngineLayout.GobAnimRuns"/> is a <c>CExoArrayList&lt;AnimRun*&gt;</c>: an array of
/// POINTERS, so an element is a load and not a stride step off a first record -- a shape no field row
/// describes, which is why the indexer is a concept here.</summary>
public readonly unsafe ref partial struct Gob
{
    /// <summary>How many runs the gob is playing; 0 while the list has no storage.</summary>
    public int RunCount => AnimRuns == IntPtr.Zero ? 0 : AnimRunCount;

    /// <summary>Run <paramref name="i"/> of <see cref="RunCount"/>. A slot the engine has emptied reads null.
    /// </summary>
    public AnimRun Run(int i) => new(((IntPtr*)AnimRuns)[i]);
}
