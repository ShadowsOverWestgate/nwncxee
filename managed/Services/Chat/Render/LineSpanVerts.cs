namespace CX.Services.Chat.Render;

/// <summary>The vertex range [<see cref="Start"/>,<see cref="End"/>) (vertex indices) of one visible chat
/// line's body geometry, plus its message time. The draw walks these to apply the per-line fade and the
/// shared slide/scroll residual (map #13).</summary>
public readonly record struct LineSpanVerts(int Start, int End, System.DateTime Ts);
