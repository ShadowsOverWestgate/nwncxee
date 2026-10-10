namespace CX.Services.Chat.Core;

/// <summary>Render-component toggle bits (see the render pipeline in WindowRenderer).</summary>
public static class ChatToggle
{
    public const uint Timestamp = 1u << 0;
    public const uint MessageBody = 1u << 1;
    /// <summary>Draw a speaker portrait in a left gutter on lines that carry a portrait resref.</summary>
    public const uint Portrait = 1u << 2;
    // ponytail: SenderName is the next component (post-floor); add a bit + a component.
}
