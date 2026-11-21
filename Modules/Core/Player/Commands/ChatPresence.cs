using System.Collections.Concurrent;

namespace GameServer.Modules.M_commands;

/// <summary>Per-character AFK and Do Not Disturb status shared with the Chat module through a BCL-only
/// <see cref="AppContext"/> dictionary so collectible reloads retain no unloaded module types.</summary>
static class ChatPresence
{
    public const string StatusKey = "LocalExile.Chat.Status";
    public const string DefaultAfkMessage = "This player is AFK.";
    public const string DefaultDndMessage = "This player is in DND mode.";

    static ConcurrentDictionary<string, (bool, string, bool, string)> Table()
    {
        if (AppContext.GetData(StatusKey) is ConcurrentDictionary<string, (bool, string, bool, string)> existing)
            return existing;
        var created = new ConcurrentDictionary<string, (bool, string, bool, string)>(StringComparer.OrdinalIgnoreCase);
        AppContext.SetData(StatusKey, created);
        return created;
    }

    public static string ToggleAfk(ClientState conn, string? arg)
    {
        string name = CharacterName(conn);
        var table = Table();
        var (afk, afkMsg, dnd, dndMsg) = table.GetValueOrDefault(name, (false, DefaultAfkMessage, false, DefaultDndMessage));
        string trimmed = arg?.Trim() ?? "";
        if (!afk || trimmed.Length > 0)
        {
            string message = trimmed.Length > 0 ? trimmed : DefaultAfkMessage;
            table[name] = (true, message, dnd, dndMsg);
            return $"AFK mode is now ON. Autoreply \"{message}\"";
        }
        table[name] = (false, afkMsg, dnd, dndMsg);
        return "AFK mode is now OFF.";
    }

    public static string ToggleDnd(ClientState conn, string? arg)
    {
        string name = CharacterName(conn);
        var table = Table();
        var (afk, afkMsg, dnd, dndMsg) = table.GetValueOrDefault(name, (false, DefaultAfkMessage, false, DefaultDndMessage));
        string trimmed = arg?.Trim() ?? "";
        if (!dnd || trimmed.Length > 0)
        {
            string message = trimmed.Length > 0 ? trimmed : DefaultDndMessage;
            table[name] = (afk, afkMsg, true, message);
            return $"Do Not Disturb mode is now ON. Autoreply \"{message}\"";
        }
        table[name] = (afk, afkMsg, false, dndMsg);
        return "Do Not Disturb mode is now OFF.";
    }

    public static string SetAutoReply(ClientState conn, string? arg)
    {
        string name = CharacterName(conn);
        var table = Table();
        var (_, _, dnd, dndMsg) = table.GetValueOrDefault(name, (false, DefaultAfkMessage, false, DefaultDndMessage));
        string trimmed = arg?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            table[name] = (false, DefaultAfkMessage, dnd, dndMsg);
            return "Autoreply cleared.";
        }
        table[name] = (true, trimmed, dnd, dndMsg);
        return $"Autoreply set to \"{trimmed}\"";
    }

    public static void Clear(ClientState conn, string why)
    {
        if (why == "disconnected" && !conn.LeavingArea && conn.PendingTransfer is null)
            Table().TryRemove(CharacterName(conn), out _);
    }
}
