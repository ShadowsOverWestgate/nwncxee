namespace Sowx.Services.Chat.Core;

/// <summary>Resolves between player names and live connection-scoped PlayerIds. The client implementation
/// walks the engine's player roster; tests inject a fake.</summary>
public interface IPlayerResolver
{
    /// <summary>Name → live PlayerId, or null when the player is offline / unknown.</summary>
    uint? Resolve(string name);

    /// <summary>PlayerId → player name, or null when the id is stale / unknown. Used by portrait
    /// click-to-tell to recover the name from a ChatLine's connection-scoped id.</summary>
    string? NameOf(uint playerId) => null;
}
