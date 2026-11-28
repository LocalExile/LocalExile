using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Mod-agnostic host channel for a player's connection, carrying ruleset payloads (<c>S->C 0x02C9</c> / <c>C->S 0x02CA</c>),
/// raw packets, and <c>/nm</c> chat fallback messages. Obtain via <see cref="Client.For(Player)"/>.
/// </summary>
public interface IClientChannel
{
    Player Player { get; }
    string ConnectionId { get; }
    bool Connected { get; }

    Task SendRuleset(byte rulesetId, ReadOnlyMemory<byte> payload, string? log = null, CancellationToken cancellationToken = default);
    Task SendPacket(ushort opcode, ReadOnlyMemory<byte> payload, string? log = null, CancellationToken cancellationToken = default);
    Task SendChat(string text, CancellationToken cancellationToken = default);

    IDisposable OnRuleset(byte rulesetId, Func<ReadOnlyMemory<byte>, Task<bool>> handler);
    IDisposable OnCommand(Func<string, Task<bool>> handler);
}

/// <summary>Mod-agnostic host channel facade (<see cref="For(Player)"/>) and owner-tracked inbound dispatchers.</summary>
public static class Client
{
    public const byte DefaultRulesetId = 0xA0;
    public const string CommandPrefix = "/nm";

    static readonly ConditionalWeakTable<Player, ClientChannel> Channels = new();
    static readonly ConcurrentDictionary<byte, GameEvent<Func<IClientChannel, ReadOnlyMemory<byte>, Task<bool>>>> RulesetEvents = new();
    static readonly GameEvent<Func<IClientChannel, string, Task<bool>>> CommandEvent = new();

    /// <summary>Returns the host channel for <paramref name="player"/>.</summary>
    public static IClientChannel For(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return Channels.GetValue(player, static p => new ClientChannel(p));
    }

    /// <summary>Returns the host channel for <paramref name="player"/>.</summary>
    public static IClientChannel For(IPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return For(Program.PlayerOf(player));
    }

    /// <summary>Subscribes an owner-tracked ruleset handler (<c>C->S 0x02CA</c>); released automatically on owner unload.</summary>
    public static IDisposable OnRuleset(string owner, byte rulesetId, Func<IClientChannel, ReadOnlyMemory<byte>, Task<bool>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentNullException.ThrowIfNull(handler);
        return RulesetEvents.GetOrAdd(rulesetId, static _ => new()).Add(owner, handler);
    }

    /// <summary>Subscribes an owner-tracked <c>/nm</c> command handler; released automatically on owner unload.</summary>
    public static IDisposable OnCommand(string owner, Func<IClientChannel, string, Task<bool>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentNullException.ThrowIfNull(handler);
        return CommandEvent.Add(owner, handler);
    }

    /// <summary>True when <paramref name="line"/> targets the host channel command prefix (<c>/nm</c>).</summary>
    public static bool IsCommand(string? line)
    {
        if (string.IsNullOrEmpty(line) || !line.StartsWith(CommandPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        return line.Length == CommandPrefix.Length || char.IsWhiteSpace(line[CommandPrefix.Length]);
    }

    /// <summary>Dispatches an inbound <c>C->S 0x02CA</c> ruleset packet to per-connection and global subscribers.</summary>
    public static async Task<bool> DispatchRulesetAsync(Player player, ReadOnlyMemory<byte> packet)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (packet.Length < 4 || BinaryPrimitives.ReadUInt16BigEndian(packet.Span) != InstanceRulesets.ActionId)
            return false;

        byte row;
        byte[] payloadBytes;
        try { (row, payloadBytes) = InstanceRulesets.ReadAction(packet.Span); }
        catch (InvalidDataException) { return false; }

        var channel = GetExistingOrNew(player);
        if (!channel.Connected) return false;

        var payload = new ReadOnlyMemory<byte>(payloadBytes);
        if (await channel.DispatchRulesetLocalAsync(row, payload))
            return true;

        if (await GameEvents.RunRulesetAction(player, row, payload))
            return true;

        if (!RulesetEvents.TryGetValue(row, out var evt) || !evt.Any)
            return false;

        foreach (var (owner, run) in evt.Subscribers(player))
        {
            bool handled = false;
            if (await GameEventOwners.Guard(owner, $"Client.OnRuleset(0x{row:x2})", async () => handled = await run(channel, payload)) && handled)
                return true;
        }
        return false;
    }

    /// <summary>Dispatches an inbound <c>/nm</c> chat command to per-connection and global subscribers.</summary>
    public static async Task<bool> DispatchCommandAsync(Player player, string commandLine)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!IsCommand(commandLine)) return false;

        var channel = GetExistingOrNew(player);
        if (!channel.Connected) return false;

        if (await channel.DispatchCommandLocalAsync(commandLine))
            return true;

        if (!CommandEvent.Any)
            return false;

        foreach (var (owner, run) in CommandEvent.Subscribers(player))
        {
            bool handled = false;
            if (await GameEventOwners.Guard(owner, "Client.OnCommand", async () => handled = await run(channel, commandLine)) && handled)
                return true;
        }
        return false;
    }

    internal static void Release(Player player)
    {
        if (Channels.TryGetValue(player, out var channel))
            channel.Release();
    }

    static ClientChannel GetExistingOrNew(Player player) =>
        Channels.GetValue(player, static p => new ClientChannel(p));

    sealed class ClientChannel(Player player) : IClientChannel
    {
        readonly ConcurrentDictionary<byte, ImmutableArray<(long Id, Func<ReadOnlyMemory<byte>, Task<bool>> Run)>> _rulesetHandlers = new();
        ImmutableArray<(long Id, Func<string, Task<bool>> Run)> _commandHandlers = [];
        long _nextHandlerId;
        bool _released;

        public Player Player { get; } = player;
        public string ConnectionId { get; } = $"{player.Connection.Remote}#{player.Id}";
        public bool Connected => !Player.Closed && !Volatile.Read(ref _released);

        public Task SendRuleset(byte rulesetId, ReadOnlyMemory<byte> payload, string? log = null, CancellationToken cancellationToken = default)
        {
            if (payload.Length > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, $"Ruleset payload exceeds {ushort.MaxValue} bytes.");
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            if (!Connected) return Task.CompletedTask;
            byte[] bytes = InstanceRulesets.Data(rulesetId, payload.Span);
            return SendBytesAsync(bytes, log ?? $"0x{InstanceRulesets.DataId:x4} ruleset 0x{rulesetId:x2}");
        }

        public Task SendPacket(ushort opcode, ReadOnlyMemory<byte> payload, string? log = null, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            if (!Connected) return Task.CompletedTask;
            byte[] bytes = new byte[2 + payload.Length];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, opcode);
            payload.Span.CopyTo(bytes.AsSpan(2));
            return SendBytesAsync(bytes, log ?? $"0x{opcode:x4} packet");
        }

        public Task SendChat(string text, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            if (!Connected) return Task.CompletedTask;
            if (Player.Commands is { } commands)
                return commands.Reply(text);
            return SendChatLinesAsync(text, cancellationToken);
        }

        public IDisposable OnRuleset(byte rulesetId, Func<ReadOnlyMemory<byte>, Task<bool>> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (!Connected) return Subscription.Empty;
            long id = Interlocked.Increment(ref _nextHandlerId);
            _rulesetHandlers.AddOrUpdate(
                rulesetId,
                static (_, entry) => [entry],
                static (_, list, entry) => list.Add(entry),
                (id, handler));
            if (Volatile.Read(ref _released))
            {
                _rulesetHandlers.TryRemove(rulesetId, out _);
                return Subscription.Empty;
            }
            return new Subscription(() => RemoveRulesetHandler(rulesetId, id));
        }

        public IDisposable OnCommand(Func<string, Task<bool>> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (!Connected) return Subscription.Empty;
            long id = Interlocked.Increment(ref _nextHandlerId);
            ImmutableInterlocked.Update(ref _commandHandlers, list => list.Add((id, handler)));
            if (Volatile.Read(ref _released))
            {
                ImmutableInterlocked.Update(ref _commandHandlers, static _ => []);
                return Subscription.Empty;
            }
            return new Subscription(() => ImmutableInterlocked.Update(ref _commandHandlers, list => list.RemoveAll(e => e.Id == id)));
        }

        internal async Task<bool> DispatchRulesetLocalAsync(byte rulesetId, ReadOnlyMemory<byte> payload)
        {
            if (!_rulesetHandlers.TryGetValue(rulesetId, out var handlers) || handlers.IsEmpty)
                return false;
            foreach (var (_, run) in handlers)
            {
                try
                {
                    if (await run(payload)) return true;
                }
                catch (Exception ex)
                {
                    GameEventOwners.Failed(ConnectionId, $"ClientChannel.OnRuleset(0x{rulesetId:x2})", ex);
                }
            }
            return false;
        }

        internal async Task<bool> DispatchCommandLocalAsync(string commandLine)
        {
            var handlers = _commandHandlers;
            if (handlers.IsEmpty) return false;
            foreach (var (_, run) in handlers)
            {
                try
                {
                    if (await run(commandLine)) return true;
                }
                catch (Exception ex)
                {
                    GameEventOwners.Failed(ConnectionId, "ClientChannel.OnCommand", ex);
                }
            }
            return false;
        }

        internal void Release()
        {
            Volatile.Write(ref _released, true);
            _rulesetHandlers.Clear();
            ImmutableInterlocked.Update(ref _commandHandlers, static _ => []);
        }

        void RemoveRulesetHandler(byte rulesetId, long id)
        {
            while (_rulesetHandlers.TryGetValue(rulesetId, out var current))
            {
                var updated = current.RemoveAll(e => e.Id == id);
                if (updated.IsEmpty)
                {
                    if (_rulesetHandlers.TryRemove(new KeyValuePair<byte, ImmutableArray<(long, Func<ReadOnlyMemory<byte>, Task<bool>>)>>(rulesetId, current)))
                        return;
                }
                else if (_rulesetHandlers.TryUpdate(rulesetId, updated, current))
                {
                    return;
                }
            }
        }

        Task SendBytesAsync(byte[] bytes, string? log)
        {
            if (Player.Connection.Outbox is { } outbox)
                return outbox(bytes, log);
            return Program.Send(Player, bytes, log);
        }

        async Task SendChatLinesAsync(string text, CancellationToken cancellationToken)
        {
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (cancellationToken.IsCancellationRequested) return;
                string line = raw.Length == 0 ? " " : raw;
                await SendBytesAsync(InstanceChat.ChatLine(line), $"0x{InstanceChat.ChatLineId:x4} channel chat");
            }
        }

        sealed class Subscription(Action? remove) : IDisposable
        {
            public static readonly Subscription Empty = new(null);
            Action? _remove = remove;
            public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
        }
    }
}
