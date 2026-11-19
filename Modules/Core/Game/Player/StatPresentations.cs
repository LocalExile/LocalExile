using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // What the client is shown of state the player's stats already follow (S2 marks and times each change): these
    // presentations run when the stats change (PlayerStatsUpdated), or when their state changes without a stat
    // (PresentStats), not on the player tick.
    static readonly Func<Player, Task>[] StatPresentations =
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
    static readonly Func<Player, Task>[] StatPresentationsAfter =
    [
        SendSpiritChieftain,
    ];

    // The quest items' grant flags follow the items carried (dropped, sold, stashed ones too): on an inventory change
    // (PlayerInventories.Changed); no inventory change here (the message path removes items).
    static Task SendQuestItems(Player conn)
    {
        if (conn.PlayerLife == 0) return Task.CompletedTask;
        lock (conn.Monsters)
            if (HardcoreProgressBlocked(conn)) return Task.CompletedTask;
        return SyncQuestItems(conn, removeItems: false);
    }

    // What depends on where the player and the monsters stand: run when a walker moved to another cell (the area's
    // movement), a blocker was added, and with the stat presentations (a stat change moves radii and grants).
    static readonly Func<Player, Task>[] NearbyPresentations =
    [
        SendNearbyAuras, TickPassiveAuras, SendBlind, SendPushOut, SendNearbyWorld, SendQuestThresholds,
        SendFlaskGrounds,
    ];

    // A monster that never dies reached its quest threshold (its PresentationChanged).
    static Task SendQuestThresholds(Player conn)
    {
        if (conn.PlayerLife == 0) return Task.CompletedTask;
        lock (conn.Monsters)
            if (HardcoreProgressBlocked(conn)) return Task.CompletedTask;
        return CheckQuestThresholds(conn);
    }

    static Task SendExpandedInventory(Player conn) =>
        SyncExpandedInventory(conn);

    static Task SendPushOut(Player conn) =>
        PushOutOfCollision(conn);

    // The party's auras: the ones this player gives and gets by range.
    static Task SendNearbyAuras(Player conn) =>
        conn.Auras.Count > 0 || conn.ReceivedAuras.Count > 0 || conn.AuraStatsDirty
            || conn.Instance.PlayersExcept(conn.PeerId).Any(p => p.Current && p.Auras.Count > 0)
            ? SendOwnAuraChanges(conn) : Task.CompletedTask;

    // What the player comes near, as zones (OwnedZones): the waypoint's activation range and the Glyph Wall's sight (at
    // them, reaching the player), and the quests' sight of the watched monsters (around the player). Coming in presents
    // the nearby world again.
    const string SightZones = "sight", WaypointSight = "waypoint", GlyphWallSight = "glyph wall", MonsterSight = "monsters";

    static void SyncSightZones(Player conn, IReadOnlyCollection<string>? watched)
    {
        var points = new List<(object, int, (int, int))>();
        if (conn.Layout.Waypoint is { } waypoint) points.Add((WaypointSight, (int)WaypointActivationRange, (waypoint.GridX, waypoint.GridY)));
        if (conn.Glyphs?.Wall is { } wall) points.Add((GlyphWallSight, (int)Quests.SightRange, (wall.X, wall.Y)));
        SyncZones(conn, conn.PeerId, SightZones, points, new(ReachesOnly(conn.PeerId), Changed: _ => PresentNearby(conn)));
        SyncZones(conn, conn.PeerId, SightZones + " around", watched is { Count: > 0 } ? [(MonsterSight, (int)Quests.SightRange, default((int, int)))] : [],
            new(id => conn.Monsters.GetValueOrDefault(id.Value) is { Dead: false, Life: > 0, Variety: { } v } && watched!.Contains(v.Id) ? 0 : null,
                new EntityId(conn.PeerId), _ => PresentNearby(conn)));
    }

    static bool Sees(Player conn, string what)
    {
        lock (conn.Monsters) return InZone(conn, conn.PeerId, SightZones, what, conn.PeerId);
    }

    // The waypoint, the gold in the auto-loot radius and the quests' sight rules, where the player stands.
    static async Task SendNearbyWorld(Player conn)
    {
        if (conn.PlayerLife == 0) return;
        var watched = QuestsOf(conn).Watched(conn.Layout.Id);
        lock (conn.Monsters)
        {
            if (HardcoreProgressBlocked(conn)) return;
            SyncSightZones(conn, watched);
        }
        if (conn.Layout.Waypoint is not null && Sees(conn, WaypointSight))
            await ActivateWaypoint(conn, "the player came near");
        // The way walked since the last check counts (PickUpNearbyGold), not only where the player stands.
        var presenting = PresentingOf.GetOrCreateValue(conn);
        presenting.GoldCheckedAt = await PickUpNearbyGold(conn, presenting.GoldCheckedAt);
        lock (conn.Monsters)
            if (HardcoreProgressBlocked(conn)) return;
        var at = conn.Walk.Position(AreaTime(conn));
        // Quests that start when the player comes near a monster (a1q1: Hillock, "Kill Hillock").
        if (watched is { Count: > 0 })
        {
            List<string> seen;
            lock (conn.Monsters)
                seen = [.. ZoneMembers(conn, conn.PeerId, SightZones + " around", MonsterSight).Select(id => conn.Monsters.GetValueOrDefault(id.Value))
                    .Where(m => m is { Dead: false, Life: > 0, Variety: not null }).Select(m => m!.Variety!.Id).Distinct()];
            foreach (string variety in seen)
                await SetQuestFlags(conn, QuestsOf(conn).OnMonsterSeen(conn.Layout.Id, variety));
        }
        // a1q4: coming near the Glyph Wall ("Investigate the Mud Flats for a way to lower the water").
        if (conn.Glyphs?.Wall is not null && !conn.HasQuestFlag(GlyphQuest.SeenGlyphWall) && Sees(conn, GlyphWallSight))
            await SetQuestFlags(conn, [GlyphQuest.SeenGlyphWall]);
        // The quest engine's sight rules (room objects, exits).
        await CheckQuestSights(conn, at);
        if (conn.QuestNearSpawns.Count > 0) await CheckQuestNearSpawns(conn, at);
    }

    // Death ends the auras, Righteous Fire and Bladefall of Trarthus; an empty mana pool ends Bladefall (the resource
    // writer spends it). Both are stat conditions, so their flip runs this.
    static async Task SendLifeAndManaEndings(Player conn)
    {
        if (conn.PlayerLife == 0)
        {
            await EndAurasOnDeath(conn);
            RighteousFireState? rf;
            lock (conn.Monsters) rf = conn.RighteousFire;
            if (rf is not null) await EndRighteousFire(conn, "death", rf);
            if (conn.BladefallTrarthus is not null) await EndBladefallTrarthus(conn, "death");
        }
        else if (conn.BladefallTrarthus is not null && conn.PlayerMana == 0)
            await EndBladefallTrarthus(conn, "out of mana");
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
    static async Task SendIncomingAilmentChanges(Player conn)
    {
        IncomingDamagingAilments.State nextIncoming;
        IncomingDamagingAilments.State presented;
        int incomingEpoch;
        lock (conn.Monsters)
        {
            nextIncoming = conn.IncomingAilments.Snapshot(AreaTime(conn));
            presented = conn.PresentedIncomingAilments ??
                new(false, double.NegativeInfinity, 0, double.NegativeInfinity, 0, 0);
            incomingEpoch = conn.IncomingHitEpoch;
        }
        if (nextIncoming == presented) return;
        {
            foreach (var (packet, log) in IncomingAilmentBuffs(presented, nextIncoming, AreaTime(conn)))
                await Send(conn, [], log, atGate: () =>
                {
                    lock (conn.Monsters)
                    {
                        if (conn.Closed || conn.LeavingArea || conn.IncomingHitEpoch != incomingEpoch
                            || conn.IncomingAilments.Snapshot(AreaTime(conn)) != nextIncoming)
                            return null;
                        return packet;
                    }
                });
            lock (conn.Monsters)
                if (!conn.Closed && !conn.LeavingArea && conn.IncomingHitEpoch == incomingEpoch
                    && conn.IncomingAilments.Snapshot(AreaTime(conn)) == nextIncoming)
                {
                    conn.PresentedIncomingAilments = nextIncoming;
                    conn.PresentedIncomingAilmentEpoch = incomingEpoch;
                }
        }
    }

    // The player's elemental ailment buffs, against the ones the client was shown.
    static async Task SendElementalAilmentChanges(Player conn)
    {
        var presenting = PresentingOf.GetOrCreateValue(conn);
        var next = conn.ElementalAilments.Snapshot(AreaTime(conn));
        if (next == presenting.Ailments) return;
        var before = presenting.Ailments;
        presenting.Ailments = next;
        await SendPlayerAilmentBuffs(conn, before, next);
    }
    static readonly Store<Player, Presenting> PresentingOf = new();

    /// <summary>What a stat presentation shows changed: they run after the next Stats phase.</summary>
    static void PresentStats(Player conn) => PresentingOf.GetOrCreateValue(conn).Pending = true;

    /// <summary>A walker moved to another cell or a blocker appeared: the nearby presentations run after the next Stats
    /// phase.</summary>
    public static void PresentNearby(Player conn) => PresentingOf.GetOrCreateValue(conn).NearbyPending = true;

    /// <summary>A presentation's state changes on its own in <paramref name="seconds"/> (an expiry): they run again then.</summary>
    static void PresentIn(Player conn, double seconds)
    {
        if (PresentVerifyTag.Value is not null) return;
        var presenting = PresentingOf.GetOrCreateValue(conn);
        double at = AreaTime(conn) + Math.Max(0, seconds);
        lock (presenting) if (at < presenting.NextAt) presenting.NextAt = at;
    }

    // The Stats phase, after the player's step.
    static void RunStatPresentations(Player conn)
    {
        var presenting = PresentingOf.GetOrCreateValue(conn);
        lock (presenting)
            if (AreaTime(conn) >= presenting.NextAt)
            {
                presenting.NextAt = double.PositiveInfinity;
                presenting.Pending = true;
            }
        if (!presenting.Pending && !presenting.NearbyPending || !presenting.Running.IsCompleted
            || conn.PlayerUpdatesState is not PlayerUpdatesState updates) return;
        bool all = presenting.Pending;
        presenting.Pending = presenting.NearbyPending = false;
        presenting.Ran = true;
        presenting.Running = AsPlayer(conn, updates, async () =>
        {
            if (!await SendPlayerTickBatch(conn, async () =>
            {
                if (all)
                    foreach (var send in StatPresentations)
                    {
                        await send(conn);
                        if (!PlayerUpdatesCurrent(conn, updates)) return false;
                    }
                foreach (var send in NearbyPresentations)
                {
                    await send(conn);
                    if (!PlayerUpdatesCurrent(conn, updates)) return false;
                }
                return true;
            })) return;
            if (all)
                foreach (var send in StatPresentationsAfter)
                {
                    await send(conn);
                    if (!PlayerUpdatesCurrent(conn, updates)) return;
                }
        });
    }

    // Game_PRESENT_VERIFY=1: the content tick runs them too, tagged; whatever it still sends is a change no event reached.
    static readonly bool VerifyPresentations = Environment.GetEnvironmentVariable("Game_PRESENT_VERIFY") == "1";
    static readonly AsyncLocal<string?> PresentVerifyTag = new();

    static async Task VerifyStatPresentations(Player conn)
    {
        var presenting = PresentingOf.GetOrCreateValue(conn);
        if (!VerifyPresentations || !presenting.Ran) return;
        // What is still to run (a mark, a due end, a pending or running presentation) is not missed yet.
        if (StatsUnsettled(conn) || presenting.Pending || presenting.NearbyPending || !presenting.Running.IsCompleted
            || AreaTime(conn) >= presenting.NextAt) return;
        foreach (var send in StatPresentations.Concat(NearbyPresentations).Concat(StatPresentationsAfter))
        {
            PresentVerifyTag.Value = send.Method.Name;
            try { await send(conn); }
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
