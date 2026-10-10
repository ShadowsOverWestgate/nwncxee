using System;
using CX.Framework;

namespace CX.Engine;

/// <inheritdoc cref="Nui__Layout__LayoutGroup"/>
public readonly unsafe partial struct Nui__Layout__LayoutGroup
{
    /// <summary>The group a solver is embedded in. kiwi hands its callers the solver, never the group, so the
    /// walk back out is the one place an offset is subtracted rather than added.</summary>
    public static Nui__Layout__LayoutGroup FromSolver(kiwi__impl__SolverImpl solver)
        => new(solver.Ptr - EngineLayout.NuiGroupSolver);
}
