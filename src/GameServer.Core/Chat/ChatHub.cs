using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Chat;

/// <summary>A character connected to a <see cref="ChatHub"/>. A member is on at most one channel of each kind.</summary>
public sealed class ChatMember(string name, Action<ChatFrame> send, double tokens, DateTimeOffset now)
{
    public string Name => name;
    internal Action<ChatFrame> Send => send;
    internal Dictionary<ChatChannelKind, (ushort Channel, byte Language)> Channels { get; } = [];
    internal double Tokens = tokens;
    internal DateTimeOffset Refilled = now;
}

/// <summary>Who is online and on which channels, all of it in memory: a restart forgets everything, and the members rejoin
/// as they reconnect. <c>send</c> must not block: it runs under the hub's lock.</summary>
public sealed class ChatHub(TimeProvider? time = null)
{
    /// <summary>The lines a member can send at once, and one more for each second it stays quiet.</summary>
    public const int Burst = 5;

    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly Lock _gate = new();
    readonly Dictionary<string, ChatMember> _members = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(ChatChannelKind, ushort, byte), HashSet<ChatMember>> _channels = [];

    /// <summary>The new member, or null when the name is invalid. Characters of separate Gateways can share a name: the later
    /// ones get the first free "Name_n".</summary>
    public ChatMember? Enter(string? name, Action<ChatFrame> send)
    {
        if (!ChatWire.ValidName(name)) return null;
        lock (_gate)
        {
            string free = name!;
            for (int n = 1; _members.ContainsKey(free); n++) free = $"{name}_{n}";
            var member = new ChatMember(free, send, Burst, _time.GetUtcNow());
            _members.Add(free, member);
            return member;
        }
    }

    public void Leave(ChatMember member)
    {
        lock (_gate)
        {
            foreach (var (kind, room) in member.Channels) Remove(member, kind, room);
            member.Channels.Clear();
            _members.Remove(member.Name);
        }
    }

    /// <summary>A frame from a member; one the hub does not know is ignored.</summary>
    public void Handle(ChatMember member, ChatFrame frame)
    {
        lock (_gate)
        {
            switch (frame.Type)
            {
                case ChatFrame.Join: Join(member, frame); break;
                case ChatFrame.Say: Say(member, frame); break;
                case ChatFrame.Whisper: Whisper(member, frame); break;
            }
        }
    }

    // A channel is its number in one language, as the client's Chat Language and /global N language choose: any number of them,
    // made by their first member and gone with their last.
    void Join(ChatMember member, ChatFrame frame)
    {
        if (frame is not { Kind: ChatChannelKind.Global or ChatChannelKind.Trade, Channel: { } channel }) return;
        var kind = frame.Kind.Value;
        if (member.Channels.TryGetValue(kind, out var current)) Remove(member, kind, current);
        (ushort Channel, byte Language) room = (channel, frame.Language ?? 0);
        member.Channels[kind] = room;
        var key = (kind, room.Channel, room.Language);
        if (!_channels.TryGetValue(key, out var members)) _channels[key] = members = [];
        members.Add(member);
    }

    void Remove(ChatMember member, ChatChannelKind kind, (ushort Channel, byte Language) room)
    {
        var key = (kind, room.Channel, room.Language);
        if (_channels.TryGetValue(key, out var members) && members.Remove(member) && members.Count == 0) _channels.Remove(key);
    }

    void Say(ChatMember member, ChatFrame frame)
    {
        if (frame.Kind is not { } kind || !member.Channels.TryGetValue(kind, out var room)) return;
        frame = ChatWire.CleanLine(frame);
        if (frame.Text is not { Length: > 0 } text || !Spend(member)) return;
        var line = ChatWire.Fitted(new ChatFrame(ChatFrame.Line, member.Name, kind, Text: text, Items: frame.Items));
        foreach (var listener in _channels[(kind, room.Channel, room.Language)]) listener.Send(line);
    }

    void Whisper(ChatMember member, ChatFrame frame)
    {
        if (!ChatWire.ValidMember(frame.Name)) return;
        frame = ChatWire.CleanLine(frame);
        if (frame.Text is not { Length: > 0 } text || !Spend(member)) return;
        if (!_members.TryGetValue(frame.Name!, out var target))
        {
            member.Send(new ChatFrame(ChatFrame.Offline, frame.Name));
            return;
        }
        var items = frame.Items;
        target.Send(ChatWire.Fitted(new ChatFrame(ChatFrame.Whisper, member.Name, Text: text, Items: items)));
        member.Send(ChatWire.Fitted(new ChatFrame(ChatFrame.Whispered, target.Name, Text: text, Items: items)));
    }

    bool Spend(ChatMember member)
    {
        var now = _time.GetUtcNow();
        member.Tokens = Math.Min(Burst, member.Tokens + (now - member.Refilled).TotalSeconds);
        member.Refilled = now;
        if (member.Tokens >= 1)
        {
            member.Tokens--;
            return true;
        }
        member.Send(new ChatFrame(ChatFrame.TooFast));
        return false;
    }
}
