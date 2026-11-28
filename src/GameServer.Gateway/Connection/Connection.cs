using GameServer.Core.Protocol.Instance;
using System.Diagnostics;

/// <summary>
/// A client's connection: the socket session (stream, ciphers, keys), the client's mode and frames, what the server
/// writes to it, and what that client has (the objects it has created or awake, the players it sees): the replication
/// state. It controls one player (Player).
/// </summary>
public sealed partial class Connection(Player player)
{
    /// <summary>The player this connection controls.</summary>
    public Player Player => player;
    /// <summary>The client's connection; the framework's alone (the rules send with Program.Send).</summary>
    internal Session Session
    {
        get => session;
        set { session = value; if (value.Ciphers is { } ciphers) Program.PlayersByCiphers.AddOrUpdate(ciphers, player); }
    }
    Session session = Session.None;
    /// <summary>The client's address, for logs.</summary>
    public string Remote => Session.Remote;
    /// <summary>Whether the rules run for it: a client is connected, or the server drives it (a test's or a tool's player has
    /// neither).</summary>
    public bool Connected => Session.Connected;
    /// <summary>Set for a player the server drives, which has no client (Program.DriveByServer): what it takes part in.</summary>
    public ServerDriven? Driven { get; internal set; }
    /// <summary>Drops the client's connection; its reading ends and the connection closes.</summary>
    public void Disconnect() => Session.Stream?.Close();
    /// <summary>The key a ticket of this connection's keyring gives (the next instance's connect, S→C 0x14).</summary>
    public byte[] TicketKey(uint ticket) => Session.Keyring.Key(ticket);
    /// <summary>The ticket-bound 8-byte Data8 nonce for S→C 0x27, verified against C→S 0x0E SipHash-2-4.</summary>
    public byte[] TicketData8(uint ticket) => Session.Keyring.Data8(ticket);
    public readonly ConnectionPerformance Performance = new();
    // Why the server closed the connection (the DISCONNECT line).
    public volatile string? CloseReason;
    /// <summary>Completes once the connection has ended and everything it saves is written (Program.EndAsync waits for it,
    /// so a connection that takes over the character loads what this one saved, never an older copy).</summary>
    public readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool PlayerSent;
    // Server frames ended so far (0x2b7).
    public long ServerFrame;
    public bool PlayerObjectCreated;
    public readonly HashSet<int> StashTabsSent = [];
    public readonly HashSet<int> GuildStashTabsSent = [];
    public readonly HashSet<int> TradeStashTabsSent = [];
    // The area transitions created on this instance's client (ids), and those a script showed before their quest flag
    // (Game.TransitionShown).
    public HashSet<uint> ShownTransitions = [];
    public HashSet<uint> ScriptShownTransitions = [];
    public readonly WorldOutput WorldOutput = new();
    // The mode-only C→S messages this connection (one area) has had, by mode (ClientPackets, 07 §6). A client sends
    // only one mode's, so the first one is the mode the handlers serve; one of each is a fault to report. Until then a
    // client is served in Lockstep, the default way of playing: the mode with no desync, which a local server's latency
    // suits and Auto picks on it. Whoever drives a player with no client sets the mode of the messages it gives it.
    readonly Lock _modeLock = new();
    readonly SortedSet<ushort> _predictiveIds = [], _lockstepIds = [];
    volatile bool _modeSeen, _lockstep;
    bool _mixedReported;
    // Set: the mode is known (a test, or a connection whose mode is given).
    public bool LockstepClient
    {
        get => !_modeSeen || _lockstep;
        set { _lockstep = value; _modeSeen = true; }
    }

    /// <summary>Whether this connection has established that its client is in lockstep.</summary>
    public bool LockstepConfirmed => _modeSeen && _lockstep;

    /// <summary>Notes a C→S message from the client's wire (not the server's own made-up inputs). Returns the ids of
    /// both modes the first time this area has had messages of both, else null.</summary>
    public (ushort[] Predictive, ushort[] Lockstep)? NotePacket(string remote, ushort id)
    {
        var mode = ClientPackets.ModeOf(id);
        if (mode == PacketMode.Shared) return null;
        lock (_modeLock)
        {
            var ids = mode == PacketMode.Lockstep ? _lockstepIds : _predictiveIds;
            if (!ids.Add(id)) return null;
            if (_predictiveIds.Count + _lockstepIds.Count == 1)
            {
                _lockstep = mode == PacketMode.Lockstep;
                _modeSeen = true;
                Console.WriteLine($"[gateway] {remote} the client sends {mode.ToString().ToLowerInvariant()} messages (first 0x{id:x4})" +
                    (_lockstep ? "" : ": served in predictive, but Lockstep is the preferred mode " +
                        "(Options > Game > Network Mode, then log out to the character screen and Play again)"));
            }
            if (_mixedReported || _predictiveIds.Count == 0 || _lockstepIds.Count == 0) return null;
            _mixedReported = true;
            return ([.. _predictiveIds], [.. _lockstepIds]);
        }
    }
    // What the default attack's last skill record was built for: a lockstep client (DefaultAttackRecordLockstep), and
    // the projectile whose count it carries (DefaultArrow or WandProjectile), which the client then draws itself
    // (Game.PlayerDefaultSkills); null when it carries none.
    public volatile bool DefaultAttackRecordLockstep;
    public volatile string? ClientDrawnShot;
    // The real round trip (LockstepLatency), its probe in flight (Stopwatch timestamp, 0 for none) and the next one's time.
    public LockstepLatency LockstepLatency = new();
    public long LockstepProbeSentAt;
    // Where the player was at each recent frame start (Game.LockstepClientCell), and the teleport epoch it belongs to.
    public readonly Queue<(double At, (int X, int Y) Cell)> LockstepTrail = new();
    public int LockstepTrailTeleportCount;
    public double LockstepNextProbeAt;
    // The frame now being sent, on Clock (PlayerWalk.FrameOf).
    public (double Start, double End)? LockstepFrame;
    // Completed and replaced at every frame end (Program.EndWorldFrame): what waits for this connection's next frame.
    TaskCompletionSource frameTurn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void TurnFrame() =>
        Interlocked.Exchange(ref frameTurn, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    /// <summary>Sends to this connection's client, for another connection (Program.Social).</summary>
    public Func<byte[], string?, Task>? Outbox;
}
