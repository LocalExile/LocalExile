using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // What the client is shown of state the player's stats already follow (S2 marks and times each change): these
    // presentations run when the stats change (PlayerStatsUpdated), or when their state changes without a stat
    // (PresentStats), not on the player tick.
    static readonly Func<string, NetworkStream, ConnectionCiphers, ClientState, Task>[] StatPresentations =
    [
        SendAntiquarian, SendKillOnslaught, SendSoulEater, SendVaalAuraExpiry, SendRecentEnemyHits,
        SendAvatarOfFire, SendHerEmbrace, SendElusive, SendConditionalItemStats, SendElementalAilmentChanges,
        SendIncomingAilmentChanges, SendLeechBuffs,
    ];

    sealed class Presenting
    {
        public volatile bool Pending;
        public Task Running = Task.CompletedTask;
        // When a presentation's state next changes on its own (PresentIn), on the connection's clock.
        public double NextAt = double.PositiveInfinity;
        // The presentations ran once (the verifier compares only after the first stat update showed them).
        public volatile bool Ran;
        public (bool Ignite, int Chill, bool Freeze, int Shock) Ailments;
    }

    // The bleed and poison buffs on the player, against the ones the client was shown.
    static async Task SendIncomingAilmentChanges(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        IncomingDamagingAilments.State nextIncoming;
        IncomingDamagingAilments.State presented;
        int incomingEpoch;
        lock (conn.Monsters)
        {
            nextIncoming = conn.IncomingAilments.Snapshot(conn.Clock.Elapsed.TotalSeconds);
            presented = conn.PresentedIncomingAilments ??
                new(false, double.NegativeInfinity, 0, double.NegativeInfinity, 0, 0);
            incomingEpoch = conn.IncomingHitEpoch;
        }
        if (nextIncoming == presented) return;
        {
            foreach (var (packet, log) in IncomingAilmentBuffs(presented, nextIncoming, conn.Clock.Elapsed.TotalSeconds))
                await Send(remote, stream, ciphers, [], log, atGate: () =>
                {
                    lock (conn.Monsters)
                    {
                        if (conn.Closed || conn.LeavingArea || conn.IncomingHitEpoch != incomingEpoch
                            || conn.IncomingAilments.Snapshot(conn.Clock.Elapsed.TotalSeconds) != nextIncoming)
                            return null;
                        return packet;
                    }
                });
            lock (conn.Monsters)
                if (!conn.Closed && !conn.LeavingArea && conn.IncomingHitEpoch == incomingEpoch
                    && conn.IncomingAilments.Snapshot(conn.Clock.Elapsed.TotalSeconds) == nextIncoming)
                {
                    conn.PresentedIncomingAilments = nextIncoming;
                    conn.PresentedIncomingAilmentEpoch = incomingEpoch;
                }
        }
    }

    // The player's elemental ailment buffs, against the ones the client was shown.
    static async Task SendElementalAilmentChanges(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var presenting = PresentingOf.GetOrCreateValue(conn);
        var next = conn.ElementalAilments.Snapshot(conn.Clock.Elapsed.TotalSeconds);
        if (next == presenting.Ailments) return;
        var before = presenting.Ailments;
        presenting.Ailments = next;
        await SendPlayerAilmentBuffs(remote, stream, ciphers, before, next);
    }
    static readonly RuleTable<ClientState, Presenting> PresentingOf = new();

    /// <summary>What a stat presentation shows changed: they run after the next Stats phase.</summary>
    static void PresentStats(ClientState conn) => PresentingOf.GetOrCreateValue(conn).Pending = true;

    /// <summary>A presentation's state changes on its own in <paramref name="seconds"/> (an expiry): they run again then.</summary>
    static void PresentIn(ClientState conn, double seconds)
    {
        if (PresentVerifyTag.Value is not null) return;
        var presenting = PresentingOf.GetOrCreateValue(conn);
        double at = conn.Clock.Elapsed.TotalSeconds + Math.Max(0, seconds);
        lock (presenting) if (at < presenting.NextAt) presenting.NextAt = at;
    }

    // The Stats phase, after the player's step.
    static void RunStatPresentations(ClientState conn)
    {
        var presenting = PresentingOf.GetOrCreateValue(conn);
        lock (presenting)
            if (conn.Clock.Elapsed.TotalSeconds >= presenting.NextAt)
            {
                presenting.NextAt = double.PositiveInfinity;
                presenting.Pending = true;
            }
        if (!presenting.Pending || !presenting.Running.IsCompleted
            || conn.PlayerUpdatesState is not PlayerUpdatesState updates) return;
        presenting.Pending = false;
        presenting.Ran = true;
        var (remote, stream, ciphers) = conn.Wire;
        presenting.Running = AsPlayer(conn, updates, () => SendPlayerTickBatch(remote, stream, ciphers, conn, async () =>
        {
            foreach (var send in StatPresentations)
            {
                await send(remote, stream, ciphers, conn);
                if (!PlayerUpdatesCurrent(conn, updates)) return false;
            }
            return true;
        }));
    }

    // Game_PRESENT_VERIFY=1: the player tick runs them too, tagged; whatever it still sends is a change no event reached.
    static readonly bool VerifyPresentations = Environment.GetEnvironmentVariable("Game_PRESENT_VERIFY") == "1";
    static readonly AsyncLocal<string?> PresentVerifyTag = new();

    static async Task VerifyStatPresentations(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (!VerifyPresentations || !PresentingOf.GetOrCreateValue(conn).Ran) return;
        foreach (var send in StatPresentations)
        {
            PresentVerifyTag.Value = send.Method.Name;
            try { await send(remote, stream, ciphers, conn); }
            finally { PresentVerifyTag.Value = null; }
        }
    }

    // A send made under the verify tag that has something to send.
    static Func<T?>? Tagged<T>(Func<T?>? atGate, string remote, string? log) where T : class
    {
        if (PresentVerifyTag.Value is not { } tag || atGate is null) return atGate;
        return () =>
        {
            var result = atGate();
            if (result is not (null or byte[] { Length: 0 })) Console.WriteLine($"[present-verify] {remote} {tag} sent unmarked: {log ?? "(records)"}");
            return result;
        };
    }
}
