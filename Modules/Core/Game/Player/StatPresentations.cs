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
        SendIncomingAilmentChanges, SendLeechBuffs, SendSpreadPoisonRegeneration,
        SendSupportGemBuffs, SendLifetap, SendPetrifiedBloodLoss,
        SendPhysicalAegis, SendUnhinged, SyncPhasing, SendLifeAndManaEndings,
        SendAttackerSlows, SendExpandedInventory, SendQuestItems, SendRage, SendFortification, DrainFlaskTriggers,
        SendConsumedCorpseCharges,
    ];

    // After the others, outside their batch: Spirit Chieftain's minion follows its published state.
    static readonly Func<string, NetworkStream, ConnectionCiphers, ClientState, Task>[] StatPresentationsAfter =
    [
        SendSpiritChieftain,
    ];

    // The quest items' grant flags follow the items carried (dropped, sold, stashed ones too): on an inventory change
    // (PlayerInventories.Changed); no inventory change here (the message path removes items).
    static Task SendQuestItems(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.PlayerLife == 0) return Task.CompletedTask;
        lock (conn.Monsters)
            if (HardcoreProgressBlocked(conn)) return Task.CompletedTask;
        return SyncQuestItems(remote, stream, ciphers, conn, removeItems: false);
    }

    // What depends on where the player and the monsters stand: run when a walker moved to another cell (the area's
    // movement), a blocker was added, and with the stat presentations (a stat change moves radii and grants).
    static readonly Func<string, NetworkStream, ConnectionCiphers, ClientState, Task>[] NearbyPresentations =
    [
        SendNearbyAuras, TickPassiveAuras, SendBlind, SendPushOut, SendNearbyWorld, SendQuestThresholds,
        SendFlaskGrounds,
    ];

    // A monster that never dies reached its quest threshold (its PresentationChanged).
    static Task SendQuestThresholds(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.PlayerLife == 0) return Task.CompletedTask;
        lock (conn.Monsters)
            if (HardcoreProgressBlocked(conn)) return Task.CompletedTask;
        return CheckQuestThresholds(remote, stream, ciphers, conn);
    }

    static Task SendExpandedInventory(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        SyncExpandedInventory(conn);

    static Task SendPushOut(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        PushOutOfCollision(remote, stream, ciphers, conn);

    // The party's auras: the ones this player gives and gets by range.
    static Task SendNearbyAuras(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        conn.Auras.Count > 0 || conn.ReceivedAuras.Count > 0 || conn.AuraStatsDirty
            || conn.Instance.PlayersExcept(conn.PeerId).Any(p => p.Current && p.Auras.Count > 0)
            ? SendOwnAuraChanges(remote, stream, ciphers, conn) : Task.CompletedTask;

    // The waypoint, the gold in the auto-loot radius and the quests' sight rules, where the player stands.
    static async Task SendNearbyWorld(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.PlayerLife == 0) return;
        lock (conn.Monsters)
            if (HardcoreProgressBlocked(conn)) return;
        if (conn.Layout.Waypoint is { } waypoint
            && Dist(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), (waypoint.GridX, waypoint.GridY)) <= WaypointActivationRange)
            await ActivateWaypoint(remote, stream, ciphers, conn, "the player came near");
        // The way walked since the last check counts (PickUpNearbyGold), not only where the player stands.
        var presenting = PresentingOf.GetOrCreateValue(conn);
        presenting.GoldCheckedAt = await PickUpNearbyGold(remote, stream, ciphers, conn, presenting.GoldCheckedAt);
        lock (conn.Monsters)
            if (HardcoreProgressBlocked(conn)) return;
        var at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        // Quests that start when the player comes near a monster (a1q1: Hillock, "Kill Hillock").
        if (QuestsOf(conn).Watched(conn.Layout.Id) is { Count: > 0 } watched)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            List<string> seen;
            lock (conn.Monsters)
                seen = [.. conn.Monsters.Values.Where(m => !m.Dead && m.Life > 0 && m.Variety is { } v && watched.Contains(v.Id)
                    && Dist(m.Walk.Position(now), at) <= Quests.SightRange).Select(m => m.Variety!.Id).Distinct()];
            foreach (string variety in seen)
                await SetQuestFlags(remote, stream, ciphers, conn, QuestsOf(conn).OnMonsterSeen(conn.Layout.Id, variety));
        }
        // a1q4: coming near the Glyph Wall ("Investigate the Mud Flats for a way to lower the water").
        if (conn.Glyphs?.Wall is { } glyphWall && !conn.HasQuestFlag(GlyphQuest.SeenGlyphWall)
            && Dist(at, (glyphWall.X, glyphWall.Y)) <= Quests.SightRange)
            await SetQuestFlags(remote, stream, ciphers, conn, [GlyphQuest.SeenGlyphWall]);
        // The quest engine's sight rules (room objects, exits).
        await CheckQuestSights(remote, stream, ciphers, conn, at);
        if (conn.QuestNearSpawns.Count > 0) await CheckQuestNearSpawns(remote, stream, ciphers, conn, at);
    }

    // Death ends the auras, Righteous Fire and Bladefall of Trarthus; an empty mana pool ends Bladefall (the resource
    // writer spends it). Both are stat conditions, so their flip runs this.
    static async Task SendLifeAndManaEndings(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.PlayerLife == 0)
        {
            await EndAurasOnDeath(remote, stream, ciphers, conn);
            RighteousFireState? rf;
            lock (conn.Monsters) rf = conn.RighteousFire;
            if (rf is not null) await EndRighteousFire(remote, stream, ciphers, conn, "death", rf);
            if (conn.BladefallTrarthus is not null) await EndBladefallTrarthus(remote, stream, ciphers, conn, "death");
        }
        else if (conn.BladefallTrarthus is not null && conn.PlayerMana == 0)
            await EndBladefallTrarthus(remote, stream, ciphers, conn, "out of mana");
    }

    sealed class Presenting
    {
        public volatile bool Pending, NearbyPending;
        public (int X, int Y)? GoldCheckedAt;
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

    /// <summary>A walker moved to another cell or a blocker appeared: the nearby presentations run after the next Stats
    /// phase.</summary>
    public static void PresentNearby(ClientState conn) => PresentingOf.GetOrCreateValue(conn).NearbyPending = true;

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
        if (!presenting.Pending && !presenting.NearbyPending || !presenting.Running.IsCompleted
            || conn.PlayerUpdatesState is not PlayerUpdatesState updates) return;
        bool all = presenting.Pending;
        presenting.Pending = presenting.NearbyPending = false;
        presenting.Ran = true;
        var (remote, stream, ciphers) = conn.Wire;
        presenting.Running = AsPlayer(conn, updates, async () =>
        {
            if (!await SendPlayerTickBatch(remote, stream, ciphers, conn, async () =>
            {
                if (all)
                    foreach (var send in StatPresentations)
                    {
                        await send(remote, stream, ciphers, conn);
                        if (!PlayerUpdatesCurrent(conn, updates)) return false;
                    }
                foreach (var send in NearbyPresentations)
                {
                    await send(remote, stream, ciphers, conn);
                    if (!PlayerUpdatesCurrent(conn, updates)) return false;
                }
                return true;
            })) return;
            if (all)
                foreach (var send in StatPresentationsAfter)
                {
                    await send(remote, stream, ciphers, conn);
                    if (!PlayerUpdatesCurrent(conn, updates)) return;
                }
        });
    }

    // Game_PRESENT_VERIFY=1: the content tick runs them too, tagged; whatever it still sends is a change no event reached.
    static readonly bool VerifyPresentations = Environment.GetEnvironmentVariable("Game_PRESENT_VERIFY") == "1";
    static readonly AsyncLocal<string?> PresentVerifyTag = new();

    static async Task VerifyStatPresentations(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var presenting = PresentingOf.GetOrCreateValue(conn);
        if (!VerifyPresentations || !presenting.Ran) return;
        // What is still to run (a mark, a due end, a pending or running presentation) is not missed yet.
        if (StatsUnsettled(conn) || presenting.Pending || presenting.NearbyPending || !presenting.Running.IsCompleted
            || conn.Clock.Elapsed.TotalSeconds >= presenting.NextAt) return;
        foreach (var send in StatPresentations.Concat(NearbyPresentations).Concat(StatPresentationsAfter))
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
