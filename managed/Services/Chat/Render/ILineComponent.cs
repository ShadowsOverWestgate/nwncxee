using System.Collections.Generic;
using System.Text;
using Sowx.Services.Chat.Core;

namespace Sowx.Services.Chat.Render;

/// <summary>A stateless render component: contributes glyphs+colours for one line when its
/// <see cref="Bit"/> is set in the window's toggle bitset. Extensibility bought with structure.</summary>
public interface ILineComponent
{
    uint Bit { get; }
    void Contribute(ChatWindow win, ChatLine line, List<Rune> runes, List<uint> cols);
}
