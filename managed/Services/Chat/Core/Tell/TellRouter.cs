using Sowx.Framework;

namespace Sowx.Services.Chat.Core;

/// <summary>Pure routing decision for incoming Tells (#46): given a mode, a layout snapshot, and the
/// sender name, decide whether to create a tab, a window, or do nothing. Self-checks off-device.</summary>
public static class TellRouter
{
    /// <summary>Result of <see cref="Route"/>: what to do + where.</summary>
    public readonly struct Decision
    {
        public readonly TellRoute Action;
        /// <summary>Window index for <see cref="TellRoute.NewTab"/> / <see cref="TellRoute.NewWindow"/>.
        /// For Exists, the window holding the existing pinned tab.</summary>
        public readonly int WindowIndex;
        /// <summary>Tab index within the window for <see cref="TellRoute.Exists"/>.</summary>
        public readonly int TabIndex;
        public Decision(TellRoute action, int windowIndex = 0, int tabIndex = 0)
        { Action = action; WindowIndex = windowIndex; TabIndex = tabIndex; }
    }

    /// <summary>Decide what to do with an incoming Tell from <paramref name="senderName"/>.</summary>
    public static Decision Route(TellAutoOpen mode, ChatWindow[] windows, int focusedIndex, string senderName)
    {
        if (mode == TellAutoOpen.Off) return new(TellRoute.None);

        // AC3: a Tell from a player with an existing pinned tab routes there — no duplicate.
        for (int i = 0; i < windows.Length; i++)
        {
            var tabs = windows[i].Tabs;
            for (int j = 0; j < tabs.Count; j++)
                if (tabs[j].PinnedName == senderName) return new(TellRoute.Exists, i, j);
        }

        // AC2: target = first TellsHere window, else focused.
        int target = focusedIndex;
        for (int i = 0; i < windows.Length; i++)
            if (windows[i].TellsHere) { target = i; break; }

        if (mode == TellAutoOpen.NewTab)
            return new(TellRoute.NewTab, target);

        // NewWindow: fall back to new-tab when at the cap.
        if (windows.Length >= ChatLayout.MaxWindows)
            return new(TellRoute.NewTab, target);
        return new(TellRoute.NewWindow, target);
    }

    /// <summary>Off-device proof of the four ACs + edge cases.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"TellRouter.SelfCheck FAIL: {w}"); } }

        // AC1: Off mode -> None.
        var wins = new[] { new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) }) };
        var d = Route(TellAutoOpen.Off, wins, 0, "Alice");
        E(d.Action == TellRoute.None, "Off mode returns None");

        // AC3: existing pinned tab -> Exists (no duplicate).
        var pinned = new ChatWindow(0, 0, 400, 300, new[] { new Tab("Tell:Alice", 0x002) { PinnedName = "Alice" } });
        wins = new[] { new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) }), pinned };
        d = Route(TellAutoOpen.NewTab, wins, 0, "Alice");
        E(d.Action == TellRoute.Exists && d.WindowIndex == 1, "existing pin -> Exists in window 1");

        // AC2 NewTab: no TellsHere -> focused window.
        wins = new[]
        {
            new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) }),
            new ChatWindow(50, 50, 400, 300, new[] { new Tab("All", 0xffffffff) }),
        };
        d = Route(TellAutoOpen.NewTab, wins, 1, "Bob");
        E(d.Action == TellRoute.NewTab && d.WindowIndex == 1, "NewTab targets focused window");

        // AC2 NewTab: TellsHere set on window 0, focused=1 -> routes to window 0.
        wins[0].TellsHere = true;
        d = Route(TellAutoOpen.NewTab, wins, 1, "Bob");
        E(d.Action == TellRoute.NewTab && d.WindowIndex == 0, "NewTab targets TellsHere window");
        wins[0].TellsHere = false;

        // NewWindow mode.
        d = Route(TellAutoOpen.NewWindow, wins, 0, "Carol");
        E(d.Action == TellRoute.NewWindow, "NewWindow mode creates a window");

        // NewWindow at cap -> falls back to NewTab.
        var full = new ChatWindow[ChatLayout.MaxWindows];
        for (int i = 0; i < full.Length; i++)
            full[i] = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) });
        d = Route(TellAutoOpen.NewWindow, full, 0, "Dave");
        E(d.Action == TellRoute.NewTab, "NewWindow at cap falls back to NewTab");

        log.Info(ok ? "TellRouter.SelfCheck: passed" : "TellRouter.SelfCheck: FAILED");
    }
}
