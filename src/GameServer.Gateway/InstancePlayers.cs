using GameServer.Core.Protocol.Instance;

/// <summary>Immutable published poses; observers never lock another connection's simulation.</summary>
sealed record InstancePlayer(uint Id, uint Type, PlayerObject Template, int X, int Y, uint Life,
    ClientState? Owner = null)
{
    // Only session liveness is read through the owner; all replicated values remain immutable snapshots.
    public bool Current => Owner is null || !Owner.Closed && !Owner.LeavingArea && Owner.PlayerSent;
}

sealed partial class InstanceState
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, InstancePlayer> _players = new();
    public void PublishPlayer(InstancePlayer player) => _players[player.Id] = player;
    public void RemovePlayer(uint id) => _players.TryRemove(id, out _);
    public InstancePlayer[] PlayersExcept(uint id) => [.. _players.Values.Where(p => p.Id != id)];
    public InstancePlayer? Player(uint id) => _players.TryGetValue(id, out var p) && p.Current ? p : null;
}

sealed partial class ClientState
{
    static int _nextPeerId = 0x20000000;
    public readonly uint PeerId = unchecked((uint)Interlocked.Increment(ref _nextPeerId));
    public PlayerObject? PeerTemplate;
    public uint PeerType;
    public readonly Dictionary<uint, InstancePlayer> SeenPlayers = [];
    /// <summary>Sends to this connection's client, for another connection (Program.Social).</summary>
    public Func<byte[], string?, Task>? Outbox;
    /// <summary>The open player trade (Program.Social), and the player this one asked to trade with (its PeerId).</summary>
    public PlayerTrade? Trade;
    public uint TradeRequestTo;
}
