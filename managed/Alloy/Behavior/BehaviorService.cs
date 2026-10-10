using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Binds and self-checks the behaviour arithmetic (<see cref="BehaviorSolvers"/>,
/// <see cref="BehaviorMath"/>) that <see cref="Db.DbAnim"/> solves the engine's behaviour classes with.
/// Nothing is hooked: under the seam a behaviour's <c>Control</c> call already lands on the database.</summary>
public sealed class BehaviorService : IExtenderService
{
    public void Initialize(ExtenderLog log, HookService hooks)
    {
        BehaviorMath.SelfCheck(log);
        BehaviorSolvers.SelfCheck(log);
    }
}
