using System.Collections.Concurrent;

/// <summary>Players in game hear in chat when a module, mod or league is reloaded, loaded or fails (ModHost's lines).
/// The servers themselves have no hot reload: an edit to them is a restart (live.ps1 -Only Gateway).</summary>
public static class HotReload
{
    static readonly ConcurrentDictionary<Player, byte> Players = new();

    /// <summary>A connection in game that hears about reloads (Program.Connection, after its chat commands).</summary>
    public static void Register(Player player) => Players[player] = 0;

    public static void Tell(string text)
    {
        foreach (var player in Players.Keys)
        {
            if (player.Closed) { Players.TryRemove(player, out _); continue; }
            if (player.Commands is not { } commands) continue;
            _ = Task.Run(async () =>
            {
                try { await commands.Reply(text); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Players.TryRemove(player, out _); }
            });
        }
    }
}
