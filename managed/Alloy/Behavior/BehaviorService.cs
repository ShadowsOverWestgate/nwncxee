using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-107's behaviour solvers, as pure arithmetic the scene database's own dispatch
/// (<see cref="Db.DbAnim"/>) solves the engine's behaviour classes with: the shared solver pieces
/// (<see cref="BehaviorSolvers"/>) and the math (<see cref="BehaviorMath"/>), bound and checked at boot. Nothing
/// is hooked: a chain on a behaviour's <c>Control</c> would redirect the game's own virtual call into a port
/// that dereferences engine pointers, and under the seam that call lands on the database instead.</summary>
public sealed class BehaviorService : IExtenderService
{
    public void Initialize(ExtenderLog log, HookService hooks)
    {
        BehaviorMath.SelfCheck(log);
        BehaviorSolvers.SelfCheck(log);
    }
}
