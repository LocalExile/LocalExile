using System.Collections.Concurrent;
using System.Net;
using GameServer.Core;
using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Chat;

/// <summary>What the player types, on its way to the others: local lines to the players of the instance, global, trade and
/// whisper lines through a chat server.</summary>
public sealed class ChatModule : IModule
{
    /// <summary>This module's game data, built in a fixed order when it loads: before the Gateway listens, as the
    /// Gateway's own (WorldLoader).</summary>
    public static void LoadData() => ChatService.LoadData();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        var chat = new ChatService(module.Log);
        module.Lifetime.Own(chat);
        module.Lifetime.Own(ChatRoutes.Follow(chat.RouteChanged));
        GameEvents.Packet(InstanceChat.LinkItemId).Add(module.Manifest.Id, (player, bytes) => chat.Linked(player, InstanceChat.ReadLinkItem(bytes.AsSpan())));
        GameEvents.Packet(InstanceChat.LinkOtherId).Add(module.Manifest.Id, (player, bytes) => chat.LinkedOther(player, InstanceChat.ReadLinkOther(bytes.AsSpan())));
        GameEvents.Packet(InstanceChat.LinkAnotherId).Add(module.Manifest.Id, (player, bytes) => chat.LinkedAnother(player, InstanceChat.ReadLinkAnother(bytes.AsSpan())));
        GameEvents.Packet(InstanceChat.ChatId).Add(module.Manifest.Id, (player, bytes) => chat.Typed(player, InstanceChat.ReadChat(bytes.AsSpan())));
        GameEvents.Packet(InstanceChat.JoinChannelId).Add(module.Manifest.Id, (player, bytes) => chat.Join(player, bytes));
        GameEvents.PlayerEntered.Add(module.Manifest.Id, player => chat.Entered(player));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, why) => chat.Leaving(player, why));
        _ = chat.Rejoin();
    }
}

/// <summary>The local chat server and the session of each character on it.</summary>
sealed class ChatService : IDisposable
{
    readonly ChatServer _local = new(IPAddress.Loopback, 0);
    readonly ConcurrentDictionary<string, ChatSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<Player, ConcurrentDictionary<byte, Item>> _linked = new();
    readonly ConcurrentDictionary<Player, ConcurrentDictionary<byte, string>> _namedLinks = new();
    static Dictionary<uint, string> MtxNames = [];

    public static void LoadData()
    {
        MtxNames = ClientTables.TableOrNull("MtxTypes") is { } rows
            ? rows.Select((r, i) => (Id: (uint)(i + 1), Name: r.Text("Name") ?? ""))
                .Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToDictionary(x => x.Id, x => x.Name)
            : [];
    }

    public ChatChannels Channels { get; } = new();
    public Action<string> Log { get; }
    /// <summary>The chat server a mod chose, or null for the local one alone.</summary>
    public (string Host, int Port)? Remote => ChatRoutes.Current;
    public int LocalPort => _local.Port;

    public ChatService(Action<string> log)
    {
        Log = log;
        _local.Start();
        log($"local chat server on 127.0.0.1:{LocalPort}" + (Remote is var (host, port) ? $", remote chat server {host}:{port}" : ""));
    }

    public void RouteChanged()
    {
        foreach (var session in _sessions.Values) session.RouteChanged();
    }

    ChatSession Session(Player player)
    {
        string name = CharacterName(player);
        if (_sessions.TryGetValue(name, out var open)) return open;
        var session = new ChatSession(this, name, player);
        if (!_sessions.TryAdd(name, session)) return _sessions[name];
        session.Open();
        return session;
    }

    void SetItemLink(Player player, byte index, Item item)
    {
        _linked.GetOrAdd(player, _ => new())[index] = item;
        if (_namedLinks.TryGetValue(player, out var named)) named.TryRemove(index, out _);
    }

    void SetNamedLink(Player player, byte index, string label)
    {
        _namedLinks.GetOrAdd(player, _ => new())[index] = label;
        if (_linked.TryGetValue(player, out var items)) items.TryRemove(index, out _);
    }

    // C→S 0x12: an item of the line the next 0x15 brings. Read here, so the line shows it as it is now.
    public Task<bool> Linked(Player player, InstanceChat.LinkItem link)
    {
        Item? found = link.Manager == 1 ? FindShopOrTradeItem(player, link.InventoryId, link.ItemId) : null;
        if (found is null)
        {
            var stash = StashOf(player);
            IReadOnlyList<InventoryItem> items = stash.TabOf(link.InventoryId) is int tab ? stash.Inventory(tab).Items
                : PlayerInventories.Row(link.InventoryId) is byte row ? ItemsOf(player).Items(row) : [];
            found = items.FirstOrDefault(i => i.Id == link.ItemId)?.Item;
        }
        if (found is not null) SetItemLink(player, link.Index, found);
        else Log($"{player.Connection.Remote} <= 0x0012 link of {link}: no such item");
        return Task.FromResult(true);
    }

    static Item? FindShopOrTradeItem(Player player, uint inventoryId, uint itemId)
    {
        if (player.Shop is { } shop && shop.Items.TryGetValue(itemId, out var entry) && entry.Tab == inventoryId)
            return entry.Item.Item;
        if (player.Sale is { } sale)
        {
            if (inventoryId == sale.Mine && ItemsOf(player).Offered().FirstOrDefault(i => i.Id == itemId) is { } mine)
                return mine.Item;
            if (inventoryId == sale.Theirs && sale.Reward.FirstOrDefault(i => i.Id == itemId) is { } reward)
                return reward.Item;
        }
        if (player.Trade is { } trade)
        {
            if (inventoryId == PlayerTradeMine && ItemsOf(player).Offered().FirstOrDefault(i => i.Id == itemId) is { } mine)
                return mine.Item;
            if (inventoryId == PlayerTradeTheirs)
            {
                var other = trade.Other(player);
                uint ownerId = 0;
                lock (trade.Lock)
                    ownerId = trade.Views[other].FirstOrDefault(kv => kv.Value == itemId).Key;
                if (ownerId != 0 && ItemsOf(other).Offered().FirstOrDefault(i => i.Id == ownerId) is { } theirs)
                    return theirs.Item;
            }
        }
        return null;
    }

    // C→S 0x13: microtransaction link (0x14018ee00) or non-inventory item id. MTX records are not BaseItemTypes items,
    // so resolve their display name to replace the placeholder '_' instead of leaving a bare underscore.
    public Task<bool> LinkedOther(Player player, InstanceChat.LinkOther link)
    {
        if (MtxNames.TryGetValue(link.Id, out var mtxName))
            SetNamedLink(player, link.Index, $"[{ChatWire.Clean(mtxName)}]");
        else if (player.Instance.Ground.Get(link.Id) is { } ground)
            SetItemLink(player, link.Index, ground.Item);
        else
            Log($"{player.Connection.Remote} <= 0x0013 link of {link}: unresolved");
        return Task.FromResult(true);
    }

    // C→S 0x14: ground item link by world object id (0x14018f5d0).
    public Task<bool> LinkedAnother(Player player, InstanceChat.LinkAnother link)
    {
        if (player.Instance.Ground.Get(link.ObjectId) is { } ground)
            SetItemLink(player, link.Index, ground.Item);
        else
            Log($"{player.Connection.Remote} <= 0x0014 ground link of {link}: no such ground item");
        return Task.FromResult(true);
    }

    // The linked items of a line, where they stand in its text: cut is what the text lost from the front of the typed line.
    static IReadOnlyList<(int At, Item Item)> Placed(IReadOnlyList<uint> links, ConcurrentDictionary<byte, Item>? linked, string text, int cut)
    {
        if (linked is null || linked.IsEmpty || links.Count == 0 || cut < 0) return [];
        var result = new List<(int At, Item Item)>();
        for (int i = 0; i < links.Count && i <= byte.MaxValue; i++)
        {
            uint pos = links[i];
            if (pos > (uint)int.MaxValue || pos < (uint)cut) continue;
            int at = (int)pos - cut;
            if (at >= 0 && at < text.Length && text[at] == ChatWire.ItemMark && linked.TryGetValue((byte)i, out var item))
                result.Add((at, item));
        }
        return [.. result.DistinctBy(l => l.At)];
    }

    static IReadOnlyList<ChatFrameItem>? FrameItems(Player player, IReadOnlyList<(int At, Item Item)> placed, string text) =>
        ChatWire.Items(text, [.. placed.Select(l => new ChatFrameItem(l.At, ChatItems.Write(l.Item, ItemsFor(player))))]);

    // The items of the sender's own line, as the 0x17 it comes back with carries them: in the order of the text, up to
    // the limit the client reads.
    static IReadOnlyList<ChatLineItem> OwnLine(IReadOnlyList<uint> links, ConcurrentDictionary<byte, Item>? linked, string text, int cut) =>
        [.. Placed(links, linked, text, cut).OrderBy(l => l.At).Take(ChatWire.MaxItems).Select(l => new ChatLineItem((uint)l.At, l.Item.Wire()))];

    /// <summary>The items of a line from a chat server that this client can be shown: the others stay '_'.</summary>
    public IReadOnlyList<ChatLineItem>? LineItems(Player player, string text, IReadOnlyList<ChatFrameItem>? items)
    {
        if (ChatWire.Items(text, items) is not { } placed) return null;
        var catalog = ItemsFor(player);
        return [.. placed.Select(i => (i.At, Wire: ChatItems.Wire(i.Item, catalog))).Where(i => i.Wire is not null).Select(i => new ChatLineItem((uint)i.At, i.Wire!))];
    }

    // C→S 0x15 without a slash: a slash command is the Gateway's. The sender sees its own line as the server sends it back.
    public async Task<bool> Typed(Player player, InstanceChat.ChatInput chat)
    {
        _linked.TryRemove(player, out var linked);
        _namedLinks.TryRemove(player, out var namedLinks);
        if (chat.IsCommand) return false;
        string name = CharacterName(player);
        await GameEvents.RunChatActivity(player);
        Log($"{player.Connection.Remote} <= 0x0015 chat \"{chat.Text}\"");
        var (channel, typed) = InstanceChat.SplitChannel(chat.Text);
        string text = ChatWire.Clean(typed);
        if (string.IsNullOrWhiteSpace(text)) return true;
        int prefix = chat.Text.Length - typed.Length;
        switch (channel)
        {
            case TypedChannel.Local:
            {
                var (finalText, finalLinks) = ChatWire.ExpandNamedLinks(text, chat.Links, prefix, namedLinks);
                await SayLocally(player, finalText, name, OwnLine(finalLinks, linked, finalText, prefix));
                break;
            }
            case TypedChannel.Party:
            {
                var (finalText, finalLinks) = ChatWire.ExpandNamedLinks(text, chat.Links, prefix, namedLinks);
                // The party is a module's to know, so the framework hands the line over; nothing else keeps it.
                if (!await GameEvents.RunPartyChat(player, finalText, OwnLine(finalLinks, linked, finalText, prefix)))
                    Log($"{player.Connection.Remote} <= 0x0015 party line \"{finalText}\" from {name} with no party to take it");
                break;
            }
            case TypedChannel.Global or TypedChannel.Trade:
            {
                var (finalText, finalLinks) = ChatWire.ExpandNamedLinks(text, chat.Links, prefix, namedLinks);
                var kind = channel == TypedChannel.Global ? ChatChannelKind.Global : ChatChannelKind.Trade;
                var session = Session(player);
                await session.Ready;
                session.Send(ChatWire.Fitted(new ChatFrame(ChatFrame.Say, Kind: kind, Text: finalText, Items: FrameItems(player, Placed(finalLinks, linked, finalText, prefix), finalText))));
                break;
            }
            case TypedChannel.Whisper:
            {
                // "@Name the text".
                int space = text.IndexOf(' ');
                if (space <= 0 || text[(space + 1)..].Trim() is not { Length: > 0 } whisper) break;
                int start = text.Length - text[(space + 1)..].TrimStart().Length;
                var (finalWhisper, finalLinks) = ChatWire.ExpandNamedLinks(whisper, chat.Links, prefix + start, namedLinks);
                var whispering = Session(player);
                await whispering.Ready;
                whispering.Send(ChatWire.Fitted(new ChatFrame(ChatFrame.Whisper, text[..space], Text: finalWhisper,
                    Items: FrameItems(player, Placed(finalLinks, linked, finalWhisper, prefix + start), finalWhisper))));
                break;
            }
        }
        return true;
    }

    internal async Task SendAutoReply(Player responder, string fromCharacter, string toCharacter, string message)
    {
        string clean = ChatWire.Clean(message).Trim();
        if (clean.Length == 0) return;
        var target = _sessions.TryGetValue(toCharacter, out var session) ? session.Player
            : InGame.Keys.FirstOrDefault(c => !c.Controlled && !c.Closed
                && string.Equals(CharacterName(c), toCharacter, StringComparison.OrdinalIgnoreCase));
        if (target is not { Closed: false, Connection.Outbox: { } outbox }) return;
        if (IsIgnored(target, responder.Character.Account ?? fromCharacter)) return;
        try
        {
            await outbox(InstanceChat.ChatLine(clean, "@" + fromCharacter, InstanceChat.AutoReplyFlag),
                $"0x0017 autoreply from {fromCharacter} to {toCharacter}");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    internal static bool IsIgnored(Player viewer, string? senderAccountOrChar) =>
        GameEvents.IsPlayerIgnored(viewer, senderAccountOrChar);

    // Not through a chat server: who is in the area is this Gateway's to know. Each gets its own bytes, as a send encrypts
    // them in place: a second client given the same array read noise and crashed (live 2026-11-07).
    static async Task SayLocally(Player player, string text, string name, IReadOnlyList<ChatLineItem> items)
    {
        foreach (var other in InGame.Keys)
            if (other is { Controlled: false, Closed: false, Connection.Outbox: { } outbox } && ReferenceEquals(other.Instance.Presence, player.Instance.Presence)
                && (ReferenceEquals(other, player) || !IsIgnored(other, player.Character.Account ?? name)))
            {
                try { await outbox(InstanceChat.ChatLine(text, name, items: items), $"0x0017 chat line from {name}"); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            }
    }

    (ushort Channel, bool Changed) JoinOn(ChatSession session, string name, ChatChannelKind kind, ushort requested, byte language)
    {
        var joined = Channels.Join(name, kind, requested, language);
        session.Send(new ChatFrame(ChatFrame.Join, Kind: kind, Channel: joined.Channel, Language: language));
        return joined;
    }

    // C→S 0x134: the client joins a global or trade channel after entering an area and for /global N and /trade N, and
    // prints "Timeout while joining … chat channel" when no 0x135 comes within 5 s. The join is answered with 0x135 at once,
    // whatever the chat server is doing, then a chat line naming the channel when it is new for the character or was asked
    // for by a command.
    public async Task<bool> Join(Player player, ReadOnlyMemory<byte> bytes)
    {
        string remote = player.Connection.Remote;
        if (InstanceChat.ReadJoinChannel(bytes.Span) is not { } join)
        {
            Log($"{remote} <= 0x0134 join with an unknown channel kind, ignored: {Convert.ToHexString(bytes.Span)}");
            return true;
        }
        string asked = join.Channel == InstanceChat.AnyChannel ? "any" : join.Channel.ToString();
        Log($"{remote} <= 0x0134 join {join.Kind} chat channel {asked} ({(join.Automatic ? "on entering" : "command")}, language {join.Language})");
        var (channel, changed) = JoinOn(Session(player), CharacterName(player), join.Kind, join.Channel, join.Language);
        await Send(player, InstanceChat.ChannelJoined(join.Kind, channel, join.Language), $"0x0135 joined {join.Kind} chat channel {channel}");
        if (changed || !join.Automatic)
        {
            string text = ChatChannels.JoinedText(join.Kind, channel, join.Language);
            await Send(player, InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");
        }
        return true;
    }

    // Every area: the channels the character joined earlier in the session (S→C 0x135), since the new instance connection
    // starts without any and the client asks only once per session.
    public async Task Entered(Player player)
    {
        var channels = Channels.Current(CharacterName(player));
        if (channels.Count == 0) return;
        Session(player).Entered(player);
        foreach (var (kind, channel, language) in channels)
            await Send(player, InstanceChat.ChannelJoined(kind, channel, language), $"0x0135 still on {kind} chat channel {channel}");
    }

    // A load while players are in game (a changed setting, a new build) knows no channels, and the clients do not join
    // again: they go back to the default ones, on the servers of this load.
    public async Task Rejoin()
    {
        foreach (var player in InGame.Keys)
        {
            if (player is not { Controlled: false, Closed: false, Connection.PlayerSent: true }) continue;
            var session = Session(player);
            foreach (var kind in (ChatChannelKind[])[ChatChannelKind.Global, ChatChannelKind.Trade])
                JoinOn(session, CharacterName(player), kind, InstanceChat.AnyChannel, 0);
            try { await Entered(player); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    // An area change closes the connection too, and the next one takes the session over.
    public Task Leaving(Player player, string why)
    {
        _linked.TryRemove(player, out _);
        _namedLinks.TryRemove(player, out _);
        if (why == "disconnected" && !player.LeavingArea && player.PendingTransfer is null
            && _sessions.TryGetValue(CharacterName(player), out var session) && ReferenceEquals(session.Player, player)
            && _sessions.TryRemove(CharacterName(player), out _))
            session.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        _local.Dispose();
    }
}
