using System.Collections.Concurrent;
using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;
using static Program;

namespace GameServer.Modules.M_commands;

/// <summary>Per-character AFK, Do Not Disturb and whisper autoreply status exposed to other modules through league-scoped
/// <see cref="GameEvents.ChatAvailabilityQuery"/> and <see cref="GameEvents.ChatActivity"/>.</summary>
static class ChatPresence
{
    public const string DefaultAfkMessage = "This player is AFK.";
    public const string DefaultDndMessage = "This player has DND mode enabled.";
    const int MaxMessageLength = 200;
    static readonly TimeSpan TransferGrace = TimeSpan.FromSeconds(60);

    static readonly HashSet<ushort> ActionPackets =
    [
        InstanceProtocol.UseSkillAtLocationId,
        InstanceProtocol.UseInstantSkillAtLocationId,
        InstanceProtocol.UseInstantSkillId,
        InstanceProtocol.LockstepMoveStartId,
        InstanceProtocol.LockstepMoveUpdateId,
        InstanceProtocol.LockstepUseOnObjectId,
        InstanceProtocol.ChannelUpdateId,
        InstanceProtocol.ChannelUpdateAltId,
        InstanceProtocol.SwapWeaponsId,
        InstanceItems.PickUpItemId,
        InstanceItems.PlaceItemId,
        InstanceItems.DropItemId,
        InstanceItems.ItemActionId,
    ];

    readonly record struct State(bool Afk, string AfkMessage, bool Dnd, string DndMessage, string? AutoReply);

    static readonly ConcurrentDictionary<string, State> Table = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsActionPacket(ushort id) => ActionPackets.Contains(id);

    static string Sanitize(string? arg)
    {
        string clean = ChatWire.Clean(arg).Trim();
        return clean.Length > MaxMessageLength ? clean[..MaxMessageLength] : clean;
    }

    public static ChatAvailability? Get(Player player)
    {
        if (!Table.TryGetValue(CharacterName(player), out var state)) return null;
        // Surface an independent /autoreply through ChatAvailability without changing the Modding ABI:
        // chatting or acting clears only state.Afk, leaving state.AutoReply intact.
        bool autoReply = state.Afk || state.AutoReply is not null;
        string replyMessage = state.Afk ? state.AfkMessage : (state.AutoReply ?? DefaultAfkMessage);
        return new ChatAvailability(autoReply, replyMessage, state.Dnd, state.DndMessage);
    }

    // BackendErrors AFKModeEnabled (365) / AFKModeDisabled (366).
    public static string ToggleAfk(Player player, string? arg)
    {
        string name = CharacterName(player);
        string trimmed = Sanitize(arg);
        var updated = Table.AddOrUpdate(name,
            _ => new State(true, trimmed.Length > 0 ? trimmed : DefaultAfkMessage, false, DefaultDndMessage, null),
            (_, cur) => !cur.Afk || trimmed.Length > 0
                ? cur with { Afk = true, AfkMessage = trimmed.Length > 0 ? trimmed : DefaultAfkMessage }
                : cur with { Afk = false });
        return updated.Afk ? $"AFK mode is now ON. Autoreply \"{updated.AfkMessage}\"" : "AFK mode is now OFF.";
    }

    // BackendErrors DNDModeEnabled (367) / DNDModeDisabled (368).
    public static string ToggleDnd(Player player, string? arg)
    {
        string name = CharacterName(player);
        string trimmed = Sanitize(arg);
        var updated = Table.AddOrUpdate(name,
            _ => new State(false, DefaultAfkMessage, true, trimmed.Length > 0 ? trimmed : DefaultDndMessage, null),
            (_, cur) => !cur.Dnd || trimmed.Length > 0
                ? cur with { Dnd = true, DndMessage = trimmed.Length > 0 ? trimmed : DefaultDndMessage }
                : cur with { Dnd = false });
        return updated.Dnd ? $"DND mode is now ON. Autoreply \"{updated.DndMessage}\"" : "DND mode is now OFF.";
    }

    // BackendErrors AutoReplyEnabled (369).
    public static string SetAutoReply(Player player, string? arg)
    {
        string name = CharacterName(player);
        string trimmed = Sanitize(arg);
        if (trimmed.Length == 0)
        {
            Table.AddOrUpdate(name,
                _ => new State(false, DefaultAfkMessage, false, DefaultDndMessage, null),
                (_, cur) => cur with { AutoReply = null });
            return "Autoreply cleared.";
        }
        Table.AddOrUpdate(name,
            _ => new State(false, DefaultAfkMessage, false, DefaultDndMessage, trimmed),
            (_, cur) => cur with { AutoReply = trimmed });
        return $"Autoreply set \"{trimmed}\"";
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
                    try { await outbox(InstanceChat.ChatLine("AFK mode is now OFF."), "0x0017 AFK cleared on activity"); }
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
