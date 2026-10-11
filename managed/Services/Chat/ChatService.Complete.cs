using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Nameplate;

namespace CX.Services.Chat;

/// <summary>Tab name completion in the input line (<c>chat.complete</c>): the one <see cref="NameCompleter"/> and the
/// names it offers, read from the engine on the first Tab of a cycle.</summary>
public sealed unsafe partial class ChatService
{
    private static readonly NameCompleter _completer = new();

    /// <summary>Tab / Shift+Tab: start a completion at the caret, or step the running one.</summary>
    private static void Complete(bool back)
    {
        var r = _completer.Active ? _completer.Step(back) : _completer.Begin(Edit.Text, Edit.Cursor, CompletionNames());
        if (r is var (start, end, with)) Edit.ReplaceRange(start, end, with);
    }

    /// <summary>What Tab offers: the visible creatures of the current area, nearest first (the tab-target walk's
    /// reads, so a creature hidden from the player never completes), then every player online. Cold: one area
    /// walk per first Tab, on the input thread.</summary>
    private static List<string> CompletionNames()
    {
        var names = new List<string>();
        IntPtr app = PlayerIdentity.App(), me = PlayerIdentity.Creature();
        if (app != IntPtr.Zero && me != IntPtr.Zero && EngineFn.GetModule != null && EngineFn.GetGameObject != null)
        {
            var exo = new CClientExoApp(app);
            CNWCModule module = exo.GetModule();
            CNWCArea area = module.IsNull ? default : module.CurrentArea;
            if (!area.IsNull && area.ObjectIds != IntPtr.Zero)
            {
                var near = new List<(float Dist, string Name)>();
                Vector3 from = new CNWCObject(me).Position;
                uint* ids = (uint*)area.ObjectIds;
                for (int i = 0, n = area.ObjectCount; i < n; i++)
                {
                    CGameObject go = exo.GetGameObject(ids[i]);
                    CNWCObject obj = go.IsNull ? default : go.AsNWCObject();
                    if (obj.IsNull || obj.Ptr == me || obj.Type != NameplateNative.TypeCreature || obj.Visible == 0) continue;
                    string name = NameplateNative.CreatureName(obj.Ptr, obj.Type);
                    if (name.Length > 0) near.Add((Vector3.Distance(obj.Position, from), name));
                }
                near.Sort((a, b) => a.Dist.CompareTo(b.Dist));
                foreach (var c in near) names.Add(c.Name);
            }
        }
        if (_playerResolver != null) names.AddRange(_playerResolver.OnlineNames());
        for (int i = 0; i < names.Count; i++) names[i] = Native.EngineText(NameCompleter.Clean(names[i]));   // tags are raw bytes: strip first
        return names;
    }
}
