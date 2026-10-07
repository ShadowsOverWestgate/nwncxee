using CX.Framework;

namespace CX.Alloy;

internal static partial class AlloyCensus
{
    /// <summary>Off-device: the late keywords' precedence and the ownable bucket list. The live signal is a class
    /// that renders or does not, and the two values of one keyword are different classes.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyCensus.SelfCheck");
        check.Expect(!RefusedByLateKeywords(0, false, false), "an ordinary material passes");
        check.Expect(!RefusedByLateKeywords(SampleFbLate, false, false), "sample_framebuffer 1 is ownable: it asks for draw order, not shading");
        check.Expect(!RefusedByLateKeywords(SampleFbWater, false, true), "sample_framebuffer 2 is water, which the water class draws");
        check.Expect(!RefusedByLateKeywords(0, false, true), "and so is bumpy-shiny water, which declares no keyword at all");
        check.Expect(RefusedByLateKeywords(0, true, false), "volumetric is refused: the engine draws it twice per pass");
        check.Expect(RefusedByLateKeywords(SampleFbLate, true, false), "volumetric beats the late keyword");
        check.Expect(RefusedByLateKeywords(3, false, false), "an unknown sample_framebuffer value is refused, not guessed at");
        check.Expect(OwnableBucket(LateBucket), "...and bucket 5 is in the ownable set");
        return check.Ok;
    }
}
