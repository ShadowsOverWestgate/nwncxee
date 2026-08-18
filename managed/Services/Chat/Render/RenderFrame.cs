using System.Collections.Generic;
using Sowx.Ui;

namespace Sowx.Services.Chat.Render;

/// <summary>One built chatbox frame: the interleaved verts + the metadata the animated draw needs. The
/// body slice [<see cref="BodyStart"/>,<see cref="BodyEnd"/>) is the only part that fades/slides and is
/// clipped to the body rect; everything outside it is static chrome. Built cold, drawn hot.</summary>
public sealed class RenderFrame
{
    public float[] Verts = System.Array.Empty<float>();
    public int Count;
    public List<PortraitQuad> Portraits = new();
    public List<LineSpanVerts> Spans = new();
    public int BodyStart, BodyEnd;    // vertex indices bounding the animated body slice
    public float Bx, By, Bw, Bh;      // body rect (px, y-down) -- the scissor for the body draw
    public bool Scrolled;             // active tab scrolled up (ScrollPx != 0): freeze the fade -- reading history
    public bool ApplyResidual;       // true for the focused window (slide/scroll ease); others snap content in
    // Per-window animation settings (carried here so the hot draw reads frame-local values, not shared statics).
    public bool AnimEnabled;
    public double HoldSec, FadeSec;
    public float TauMs;
    public System.DateTime LastScroll;
    /// <summary>Vertex range for unread-tab blink borders (start..end indices). Alpha is modulated at draw time.</summary>
    public int BlinkStart, BlinkEnd;
}
