# Tell tabs are pinned by player name, not PlayerId

A per-player Tell tab identifies its player by **name** (a stable string), not by
`playerId`. `playerId` is connection-scoped and changes every relog, so a tab pinned by
id can't be restored next session; the name is stable on a persistent-world server and
`ChatLine` already carries `senderName`. At send time the live `playerId` is resolved
from the name. Trade-off: name collisions or renames can mis-resolve (rare, and the
alternative is Tell tabs that vanish on every relog). Consequence: the persisted layout
stores the name, and targeting does a name→id lookup on each send.

**Addendum 2026-09-13.** "Player name" here means the CHARACTER name (`CNWCPlayer+0x40`, what
`GetPlayerByCreatureName` compares and what the player list shows), never the account name at `+0x10`.
Every pin entry point canonicalises through `IPlayerResolver.NameOf`, and incoming routing produces the
same name, so a reply lands on the tab the player-list button opened.
