using Sowx.Ui;

namespace Sowx.Services.Chords;

public sealed class SlashChord : IChordConsumer
{
    /// <summary>SDL keycode for <c>/</c> (SDLK_SLASH).
    private const int KeySlash = 47;

    /// <summary>Raw SDL modifier masks (KMOD_SHIFT / KMOD_CTRL).</summary>
    private const int KmodShift = 0x0003, KmodCtrl = 0x00C0;

    public int Priority => 1;

    public bool WantsKey(int key) => key == KeySlash;

    public void Fire(uint key, int sdlMods)
    {
        // Any modifier means the player meant something else (shift+/ = "?", ctrl+/ = a bind).
        if (key != KeySlash || (sdlMods & (KmodShift | KmodCtrl)) != 0) return;
        Chat.ChatService.OpenWith('/');
    }

    public void FireMouse(int button, int sdlMods) { }
}
