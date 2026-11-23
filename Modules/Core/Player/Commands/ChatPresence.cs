using System.Collections.Concurrent;
using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;
using static Program;

namespace GameServer.Modules.M_commands;

/// <summary>Per-character AFK and Do Not Disturb status exposed to other modules through league-scoped
/// <see cref="GameEvents.ChatAvailabilityQuery"/> and <see cref="GameEvents.ChatActivity"/>.</summary>
static class ChatPresence
{
    public const string DefaultAfkMessage = "This player is AFK.";
    public const string DefaultDndMessage = "This player is in DND mode.";
    const int MaxMessageLength = 200;
    static readonly TimeSpan TransferGrace = TimeSpan.FromSeconds(60);

    static readonly ConcurrentDictionary<string, ChatAvailability> Table = new(StringComparer.OrdinalIgnoreCase);

    static string Sanitize(string? arg)
    {
        string clean = ChatWire.Clean(arg).Trim();
        return clean.Length > MaxMessageLength ? clean[..MaxMessageLength] : clean;
    }

    public static ChatAvailability? Get(Player player) =>
        Table.TryGetValue(CharacterName(player), out var status) ? status : null;

    public static string ToggleAfk(Player player, string? arg)
    {
        string name = CharacterName(player);
        string trimmed = Sanitize(arg);
        var updated = Table.AddOrUpdate(name,
            _ => new ChatAvailability(true, trimmed.Length > 0 ? trimmed : DefaultAfkMessage, false, DefaultDndMessage),
            (_, cur) => !cur.Afk || trimmed.Length > 0
                ? cur with { Afk = true, AfkMessage = trimmed.Length > 0 ? trimmed : DefaultAfkMessage }
                : cur with { Afk = false });
        return updated.Afk ? $"AFK mode is now ON. Autoreply \"{updated.AfkMessage}\"" : "AFK mode is now OFF.";
    }

    public static string ToggleDnd(Player player, string? arg)
    {
        string name = CharacterName(player);
        string trimmed = Sanitize(arg);
        var updated = Table.AddOrUpdate(name,
            _ => new ChatAvailability(false, DefaultAfkMessage, true, trimmed.Length > 0 ? trimmed : DefaultDndMessage),
            (_, cur) => !cur.Dnd || trimmed.Length > 0
                ? cur with { Dnd = true, DndMessage = trimmed.Length > 0 ? trimmed : DefaultDndMessage }
                : cur with { Dnd = false });
        return updated.Dnd ? $"Do Not Disturb mode is now ON. Autoreply \"{updated.DndMessage}\"" : "Do Not Disturb mode is now OFF.";
    }

    public static string SetAutoReply(Player player, string? arg)
    {
        string name = CharacterName(player);
        string trimmed = Sanitize(arg);
        if (trimmed.Length == 0)
        {
            Table.AddOrUpdate(name,
                _ => new ChatAvailability(false, DefaultAfkMessage, false, DefaultDndMessage),
                (_, cur) => cur with { Afk = false, AfkMessage = DefaultAfkMessage });
            return "Autoreply cleared.";
        }
        Table.AddOrUpdate(name,
            _ => new ChatAvailability(true, trimmed, false, DefaultDndMessage),
            (_, cur) => cur with { Afk = true, AfkMessage = trimmed });
        return $"Autoreply set to \"{trimmed}\"";
    }

    public static async Task ClearAfkOnActivity(Player player)
    {
        string name = CharacterName(player);
        while (Table.TryGetValue(name, out var current))
        {
            if (!current.Afk) return;
            var updated = current with { Afk = false };
            if (Table.TryUpdate(name, updated, current))
            {
                if (player is { Closed: false, Connection.Outbox: { } outbox })
                {
                    try { await outbox(InstanceChat.ChatLine("AFK mode is now OFF."), "0x0017 AFK cleared on chat"); }
                    catch (Exception e) when (e is IOException or ObjectDisposedException) { }
                }
                return;
            }
        }
    }

    public static void Clear(Player player, string why)
    {
        if (why != "disconnected") return;
        string name = CharacterName(player);
        if (!player.LeavingArea && player.PendingTransfer is null)
        {
            Table.TryRemove(name, out _);
            return;
        }
        _ = ClearIfNotReturned(player, name);
    }

    static async Task ClearIfNotReturned(Player leaving, string name)
    {
        await Task.Delay(TransferGrace);
        if (InGame.Keys.Any(p => !ReferenceEquals(p, leaving) && !p.Closed
            && string.Equals(CharacterName(p), name, StringComparison.OrdinalIgnoreCase)))
            return;
        Table.TryRemove(name, out _);
    }
}
