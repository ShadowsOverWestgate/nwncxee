using CX.Framework;

namespace CX.Services.Anim;

/// <summary>The <c>anim</c> group: the procedural pose layer's knobs, read hot by <see cref="PoseService"/>.</summary>
internal static class AnimKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<float> Distance;
    internal static readonly Knob<bool> FootIk;
    internal static readonly Knob<float> Max;
    internal static readonly Knob<float> Tilt;
    internal static readonly Knob<float> Lift;
    internal static readonly Knob<bool> Stride;
    internal static readonly Knob<bool> FootLock;
    internal static readonly Knob<bool> SyncAttach;
    internal static readonly Knob<bool> LookBody;
    internal static readonly Knob<float> Lean;
    internal static readonly Knob<bool> Aim;
    internal static readonly Knob<float> Hit;

    static AnimKnobs()
    {
        G = KnobGroup.Declare("anim", "Graphics", "Animation",
            header: "Corrections laid over the engine's own animation, each with its own toggle. Client-side only: other players see nothing different, and a client without CX looks exactly as before.");
        Distance = G.Slider("distance", "Detail distance", 10f, 80f, 5f, 30f, "m",
            help: "Characters farther than this from yours keep the engine's own animation: at that range the corrections cannot be seen, and a crowded area pays for every character in it. Cloaks stay in step at any distance.");
        G.Section("Feet");
        FootIk = G.Toggle("footik", "Stand on uneven floors", true,
            help: "Characters stand on the floor that is drawn -- raised platforms, rugs, decks, stairs and sloped tiles -- instead of the flat walkmesh under it: the body is raised or lowered and each leg bends to its own foothold. Sitting, lying, knockdown and death are left alone.");
        Max = G.Slider("footik_max", "Largest step", 0.1f, 1.5f, 0.05f, 0.5f, "m",
            help: "How far above or below the walkmesh a floor may be and still count. A table the character walks past is higher than this and is ignored.");
        Tilt = G.Slider("footik_tilt", "Foot tilt", 0f, 45f, 5f, 30f, "°",
            help: "The steepest slope a foot turns to lie flat on, so a sole follows a ramp or a sloped tile instead of cutting into it. 0 keeps the foot as animated.");
        Lift = G.Slider("footik_lift", "Highest knee lift", 0.05f, 0.6f, 0.05f, 0.25f, "m",
            help: "How much higher one foot may be set than the other. A steep step past this leaves the foot in the step rather than folding the leg up to it.");
        Stride = G.Toggle("stride", "Match stride to speed", true,
            help: "The walk and run cycles play at the pace the character actually covers ground, measured from the animation's own footfalls. The engine paces them from a fixed stride length per appearance, which a custom walk cycle, haste or a slow effect does not match, so feet slide.");
        FootLock = G.Toggle("footlock", "Plant feet", true,
            help: "While walking or running, a foot on the ground stays where it landed until the animation lifts it, so what is left of the sliding is taken up by the leg.");
        SyncAttach = G.Toggle("syncattach", "Keep cloaks in step", true,
            help: "Fixes a bug with cloak desynchronizing from the body in certain situations.");
        G.Section("Body");
        LookBody = G.Toggle("lookbody", "Turn the body with the head", true,
            help: "When a character looks at something -- a conversation partner, or your aim under the action camera -- the chest and neck share the turn instead of the head swivelling alone. Where the head points does not change.");
        Lean = G.Slider("lean", "Lean", 0f, 200f, 10f, 100f, "%",
            help: "The upper body leans into turns, forward when speeding up, back when stopping, and into a climb. The head keeps half of it level. 0 turns it off.");
        Aim = G.Toggle("aim", "Aim at the target", true,
            help: "During an attack the upper body bends toward the target's height: a bow shot at a creature on a ledge or a swing at a giant tilts up, a strike at a rat tilts down.");
        Hit = G.Slider("hitreact", "Hit reactions", 0f, 200f, 10f, 100f, "%",
            help: "A landed hit knocks the upper body away from the blow, harder for more damage and for a critical hit, on top of the engine's own flinch. 0 turns it off.");
        G.Status = PoseService.Report;
    }
}
