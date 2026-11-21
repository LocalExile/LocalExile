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
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        var chat = new ChatService(module.Log);
        module.Lifetime.Own(chat);
        module.Lifetime.Own(ChatRoutes.Follow(chat.RouteChanged));
        GameEvents.Packet(InstanceChat.LinkItemId).Add(module.Manifest.Id, (conn, bytes) => chat.Linked(conn, InstanceChat.ReadLinkItem(bytes.AsSpan())));
        GameEvents.Packet(InstanceChat.LinkOtherId).Add(module.Manifest.Id, (conn, bytes) => chat.LinkedOther(conn, InstanceChat.ReadLinkOther(bytes.AsSpan())));
        GameEvents.Packet(InstanceChat.LinkAnotherId).Add(module.Manifest.Id, (conn, bytes) => chat.LinkedAnother(conn, InstanceChat.ReadLinkAnother(bytes.AsSpan())));
        GameEvents.Packet(InstanceChat.ChatId).Add(module.Manifest.Id, (conn, bytes) => chat.Typed(conn, InstanceChat.ReadChat(bytes.AsSpan())));
        GameEvents.Packet(InstanceChat.JoinChannelId).Add(module.Manifest.Id, (conn, bytes) => chat.Join(conn, bytes));
        GameEvents.AreaGenerated.Add(module.Manifest.Id, conn => chat.Entered(conn));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, why) => chat.Leaving(conn, why));
        _ = chat.Rejoin();
    }
}

/// <summary>The local chat server and the session of each character on it.</summary>
sealed class ChatService : IDisposable
{
    readonly ChatServer _local = new(IPAddress.Loopback, 0);
    readonly ConcurrentDictionary<string, ChatSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<ClientState, ConcurrentDictionary<byte, Item>> _linked = new();
    readonly ConcurrentDictionary<ClientState, ConcurrentDictionary<byte, string>> _namedLinks = new();
    static readonly Lazy<Dictionary<uint, string>> MtxNames = new(() =>
        ClientTables.TableOrNull("MtxTypes") is { } rows
            ? rows.Select((r, i) => (Id: (uint)(i + 1), Name: r.Text("Name") ?? ""))
                .Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToDictionary(x => x.Id, x => x.Name)
            : []);

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

    ChatSession Session(ClientState conn)
    {
        string name = CharacterName(conn);
        if (_sessions.TryGetValue(name, out var open)) return open;
        var session = new ChatSession(this, name, conn);
        if (!_sessions.TryAdd(name, session)) return _sessions[name];
        session.Open();
        return session;
    }

    // C→S 0x12: an item of the line the next 0x15 brings. Read here, so the line shows it as it is now.
    public Task<bool> Linked(ClientState conn, InstanceChat.LinkItem link)
    {
        Item? found = link.Manager == 1 ? FindShopOrTradeItem(conn, link.InventoryId, link.ItemId) : null;
        if (found is null)
        {
            var stash = StashOf(conn);
            IReadOnlyList<InventoryItem> items = stash.TabOf(link.InventoryId) is int tab ? stash.Inventory(tab).Items
                : PlayerInventories.Row(link.InventoryId) is byte row ? ItemsOf(conn).Items(row) : [];
            found = items.FirstOrDefault(i => i.Id == link.ItemId)?.Item;
        }
        if (found is not null) _linked.GetOrAdd(conn, _ => new())[link.Index] = found;
        else Log($"{conn.Wire.Remote} <= 0x0012 link of {link}: no such item");
        return Task.FromResult(true);
    }

    static Item? FindShopOrTradeItem(ClientState conn, uint inventoryId, uint itemId)
    {
        if (conn.Shop is { } shop && shop.Items.TryGetValue(itemId, out var entry) && entry.Tab == inventoryId)
            return entry.Item.Item;
        if (conn.Sale is { } sale)
        {
            if (inventoryId == sale.Mine && ItemsOf(conn).Offered().FirstOrDefault(i => i.Id == itemId) is { } mine)
                return mine.Item;
            if (inventoryId == sale.Theirs && sale.Reward.FirstOrDefault(i => i.Id == itemId) is { } reward)
                return reward.Item;
        }
        if (conn.Trade is { } trade)
        {
            if (inventoryId == PlayerTradeMine && ItemsOf(conn).Offered().FirstOrDefault(i => i.Id == itemId) is { } mine)
                return mine.Item;
            if (inventoryId == PlayerTradeTheirs)
            {
                var other = trade.Other(conn);
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
    public Task<bool> LinkedOther(ClientState conn, InstanceChat.LinkOther link)
    {
        if (MtxNames.Value.TryGetValue(link.Id, out var mtxName))
            _namedLinks.GetOrAdd(conn, _ => new())[link.Index] = $"[{mtxName}]";
        else if (conn.Instance.Ground.Get(link.Id) is { } ground)
            _linked.GetOrAdd(conn, _ => new())[link.Index] = ground.Item;
        else
            Log($"{conn.Wire.Remote} <= 0x0013 link of {link}: unresolved");
        return Task.FromResult(true);
    }

    // C→S 0x14: ground item link by world object id (0x14018f5d0).
    public Task<bool> LinkedAnother(ClientState conn, InstanceChat.LinkAnother link)
    {
        if (conn.Instance.Ground.Get(link.ObjectId) is { } ground)
            _linked.GetOrAdd(conn, _ => new())[link.Index] = ground.Item;
        else
            Log($"{conn.Wire.Remote} <= 0x0014 ground link of {link}: no such ground item");
        return Task.FromResult(true);
    }

    // Expands text-only links (C→S 0x13 MTX names) in place and shifts remaining item link offsets so S→C 0x17
    // item indices still land on their exact '_' placeholders.
    static (string Text, IReadOnlyList<uint> Links) ExpandNamedLinks(string text, IReadOnlyList<uint> links, int cut,
        ConcurrentDictionary<byte, string>? named)
    {
        if (named is null || named.IsEmpty || links.Count == 0) return (text, links);
        uint[] adjusted = [.. links];
        // Process right-to-left by position so earlier replacements do not shift indices we still need to inspect.
        var replacements = links.Select((pos, i) => (Index: (byte)i, At: (int)pos - cut))
            .Where(x => named.ContainsKey(x.Index) && x.At >= 0 && x.At < text.Length && text[x.At] == ChatWire.ItemMark)
            .OrderByDescending(x => x.At)
            .ToArray();
        if (replacements.Length == 0) return (text, links);
        foreach (var (index, at) in replacements)
        {
            string label = named[index];
            text = string.Concat(text.AsSpan(0, at), label, text.AsSpan(at + 1));
            int delta = label.Length - 1;
            for (int i = 0; i < adjusted.Length; i++)
                if ((int)adjusted[i] - cut > at) adjusted[i] = (uint)((int)adjusted[i] + delta);
        }
        return (text, adjusted);
    }

    // The linked items of a line, where they stand in its text: cut is what the text lost from the front of the typed line.
    static IReadOnlyList<(int At, Item Item)> Placed(IReadOnlyList<uint> links, ConcurrentDictionary<byte, Item>? linked, string text, int cut) =>
        linked is null ? [] : [.. links.Select((link, index) => (At: (int)link - cut, Item: linked.GetValueOrDefault((byte)index)))
            .Where(l => l.Item is not null && l.At >= 0 && l.At < text.Length && text[l.At] == ChatWire.ItemMark).Select(l => (l.At, l.Item!))];

    static IReadOnlyList<ChatFrameItem>? FrameItems(ClientState conn, IReadOnlyList<(int At, Item Item)> placed, string text) =>
        ChatWire.Items(text, [.. placed.Select(l => new ChatFrameItem(l.At, ChatItems.Write(l.Item, ItemsFor(conn))))]);

    // The items of the sender's own line, as the 0x17 it comes back with carries them: in the order of the text, up to
    // the limit the client reads.
    static IReadOnlyList<ChatLineItem> OwnLine(IReadOnlyList<uint> links, ConcurrentDictionary<byte, Item>? linked, string text, int cut) =>
        [.. Placed(links, linked, text, cut).OrderBy(l => l.At).Take(ChatWire.MaxItems).Select(l => new ChatLineItem((uint)l.At, l.Item.Wire()))];

    /// <summary>The items of a line from a chat server that this client can be shown: the others stay '_'.</summary>
    public IReadOnlyList<ChatLineItem>? LineItems(ClientState conn, string text, IReadOnlyList<ChatFrameItem>? items)
    {
        if (ChatWire.Items(text, items) is not { } placed) return null;
        var catalog = ItemsFor(conn);
        return [.. placed.Select(i => (i.At, Wire: ChatItems.Wire(i.Item, catalog))).Where(i => i.Wire is not null).Select(i => new ChatLineItem((uint)i.At, i.Wire!))];
    }

    // C→S 0x15 without a slash: a slash command is the Gateway's. The sender sees its own line as the server sends it back.
    public async Task<bool> Typed(ClientState conn, InstanceChat.ChatInput chat)
    {
        _linked.TryRemove(conn, out var linked);
        _namedLinks.TryRemove(conn, out var namedLinks);
        if (chat.IsCommand) return false;
        string name = CharacterName(conn);
        Log($"{conn.Wire.Remote} <= 0x0015 chat \"{chat.Text}\"");
        var (channel, typed) = InstanceChat.SplitChannel(chat.Text);
        string text = ChatWire.Clean(typed);
        if (string.IsNullOrWhiteSpace(text)) return true;
        int prefix = chat.Text.Length - typed.Length;
        switch (channel)
        {
            case TypedChannel.Local:
            {
                var (finalText, finalLinks) = ExpandNamedLinks(text, chat.Links, prefix, namedLinks);
                await SayLocally(conn, finalText, name, OwnLine(finalLinks, linked, finalText, prefix));
                break;
            }
            case TypedChannel.Party:
            {
                var (finalText, finalLinks) = ExpandNamedLinks(text, chat.Links, prefix, namedLinks);
                // The party is a module's to know, so the framework hands the line over; nothing else keeps it.
                if (!await GameEvents.RunPartyChat(conn, finalText, OwnLine(finalLinks, linked, finalText, prefix)))
                    Log($"{conn.Wire.Remote} <= 0x0015 party line \"{finalText}\" from {name} with no party to take it");
                break;
            }
            case TypedChannel.Global or TypedChannel.Trade:
            {
                var (finalText, finalLinks) = ExpandNamedLinks(text, chat.Links, prefix, namedLinks);
                var kind = channel == TypedChannel.Global ? ChatChannelKind.Global : ChatChannelKind.Trade;
                var session = Session(conn);
                await session.Ready;
                session.Send(ChatWire.Fitted(new ChatFrame(ChatFrame.Say, Kind: kind, Text: finalText, Items: FrameItems(conn, Placed(finalLinks, linked, finalText, prefix), finalText))));
                break;
            }
            case TypedChannel.Whisper:
            {
                // "@Name the text".
                int space = text.IndexOf(' ');
                if (space <= 0 || text[(space + 1)..].Trim() is not { Length: > 0 } whisper) break;
                int start = text.Length - text[(space + 1)..].TrimStart().Length;
                var (finalWhisper, finalLinks) = ExpandNamedLinks(whisper, chat.Links, prefix + start, namedLinks);
                var whispering = Session(conn);
                await whispering.Ready;
                whispering.Send(ChatWire.Fitted(new ChatFrame(ChatFrame.Whisper, text[..space], Text: finalWhisper,
                    Items: FrameItems(conn, Placed(finalLinks, linked, finalWhisper, prefix + start), finalWhisper))));
                break;
            }
        }
        return true;
    }

    // Not through a chat server: who is in the area is this Gateway's to know. Each gets its own bytes, as a send encrypts
    // them in place: a second client given the same array read noise and crashed (live 2026-11-07).
    static async Task SayLocally(ClientState conn, string text, string name, IReadOnlyList<ChatLineItem> items)
    {
        foreach (var other in InGame.Keys)
            if (other is { Controlled: false, Closed: false, Outbox: { } outbox } && ReferenceEquals(other.Instance.Presence, conn.Instance.Presence))
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
    public async Task<bool> Join(ClientState conn, ReadOnlyMemory<byte> bytes)
    {
        string remote = conn.Wire.Remote;
        if (InstanceChat.ReadJoinChannel(bytes.Span) is not { } join)
        {
            Log($"{remote} <= 0x0134 join with an unknown channel kind, ignored: {Convert.ToHexString(bytes.Span)}");
            return true;
        }
        string asked = join.Channel == InstanceChat.AnyChannel ? "any" : join.Channel.ToString();
        Log($"{remote} <= 0x0134 join {join.Kind} chat channel {asked} ({(join.Automatic ? "on entering" : "command")}, language {join.Language})");
        var (channel, changed) = JoinOn(Session(conn), CharacterName(conn), join.Kind, join.Channel, join.Language);
        await Send(remote, conn.Wire.Stream, conn.Wire.Ciphers, InstanceChat.ChannelJoined(join.Kind, channel, join.Language), $"0x0135 joined {join.Kind} chat channel {channel}");
        if (changed || !join.Automatic)
        {
            string text = ChatChannels.JoinedText(join.Kind, channel, join.Language);
            await Send(remote, conn.Wire.Stream, conn.Wire.Ciphers, InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");
        }
        return true;
    }

    // Every area: the channels the character joined earlier in the session (S→C 0x135), since the new instance connection
    // starts without any and the client asks only once per session.
    public async Task Entered(ClientState conn)
    {
        var channels = Channels.Current(CharacterName(conn));
        if (channels.Count == 0) return;
        Session(conn).Entered(conn);
        foreach (var (kind, channel, language) in channels)
            await Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, InstanceChat.ChannelJoined(kind, channel, language), $"0x0135 still on {kind} chat channel {channel}");
    }

    // A load while players are in game (a changed setting, a new build) knows no channels, and the clients do not join
    // again: they go back to the default ones, on the servers of this load.
    public async Task Rejoin()
    {
        foreach (var conn in InGame.Keys)
        {
            if (conn is not { Controlled: false, Closed: false, PlayerSent: true }) continue;
            var session = Session(conn);
            foreach (var kind in (ChatChannelKind[])[ChatChannelKind.Global, ChatChannelKind.Trade])
                JoinOn(session, CharacterName(conn), kind, InstanceChat.AnyChannel, 0);
            try { await Entered(conn); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    // An area change closes the connection too, and the next one takes the session over.
    public Task Leaving(ClientState conn, string why)
    {
        _linked.TryRemove(conn, out _);
        _namedLinks.TryRemove(conn, out _);
        if (why == "disconnected" && !conn.LeavingArea && conn.PendingTransfer is null
            && _sessions.TryGetValue(CharacterName(conn), out var session) && ReferenceEquals(session.Conn, conn)
            && _sessions.TryRemove(CharacterName(conn), out _))
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
