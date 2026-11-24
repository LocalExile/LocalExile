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
    static Task SendQuestItems(Player player)
    {
        if (player.PlayerLife == 0) return Task.CompletedTask;
        if (HardcoreProgressBlocked(player)) return Task.CompletedTask;
        // Only when the items changed since the last sync (a quest flag change syncs them itself).
        var items = ItemsOf(player);
        var presenting = PresentingOf.GetOrCreateValue(player);
        if (ReferenceEquals(items, presenting.QuestItems) && items.Changes == presenting.QuestItemChanges) return Task.CompletedTask;
        (presenting.QuestItems, presenting.QuestItemChanges) = (items, items.Changes);
        return SyncQuestItems(player, removeItems: false);
    }

    // What depends on where the player and the monsters stand: run when a walker moved to another cell (the area's
    // movement), a blocker was added, and with the stat presentations (a stat change moves radii and grants).
    static readonly Func<Player, Task>[] NearbyPresentations =
    [
        SendNearbyAuras, SendReceivedTotemAura, TickPassiveAuras, SendBlind, SendPushOut, SendNearbyWorld,
        SendQuestThresholds, SendFlaskGrounds,
    ];

    // A monster that never dies reached its quest threshold (its PresentationChanged).
    static Task SendQuestThresholds(Player player)
    {
        if (player.PlayerLife == 0) return Task.CompletedTask;
        if (HardcoreProgressBlocked(player)) return Task.CompletedTask;
        return CheckQuestThresholds(player);
    }

    static Task SendExpandedInventory(Player player) =>
        SyncExpandedInventory(player);

    static Task SendPushOut(Player player) =>
        PushOutOfCollision(player);

    // The party's auras: the ones this player gives and gets by range.
    static Task SendNearbyAuras(Player player) =>
        player.Auras.Count > 0 || player.ReceivedAuras.Count > 0 || player.AuraStatsDirty
            || player.Instance.PlayersExcept(player.Id).Any(p => p.SharedAuras.Count > 0)
            ? SendOwnAuraChanges(player) : Task.CompletedTask;

    // What the player comes near, as zones (OwnedZones): the waypoint's activation range and the Glyph Wall's sight (at
    // them, reaching the player), and the quests' sight of the watched monsters (around the player). Coming in presents
    // the nearby world again.
    const ZoneKind SightZones = ZoneKind.Sight;
    const uint WaypointSight = 0, GlyphWallSight = 1, MonsterSight = 0;

    static void SyncSightZones(Player player, IReadOnlyCollection<string>? watched)
    {
        var presenting = PresentingOf.GetOrCreateValue(player);
        var waypoint = player.Layout.Waypoint;
        var wall = player.Glyphs?.Wall;
        (int X, int Y)? wallPos = wall is { } gw ? (gw.X, gw.Y) : null;
        int watchedCount = watched?.Count ?? 0;
        if (presenting.SightEpoch == player.ProjectileEpoch && presenting.SightLayout == player.Layout.Id
            && presenting.SightHasWaypoint == (waypoint is not null) && presenting.SightWall == wallPos
            && presenting.SightWatchedCount == watchedCount)
            return;
        presenting.SightEpoch = player.ProjectileEpoch;
        presenting.SightLayout = player.Layout.Id;
        presenting.SightHasWaypoint = waypoint is not null;
        presenting.SightWall = wallPos;
        presenting.SightWatchedCount = watchedCount;
        var points = new List<(uint, int, (int, int))>();
        if (waypoint is not null) points.Add((WaypointSight, (int)WaypointActivationRange, (waypoint.GridX, waypoint.GridY)));
        if (wall is { } w) points.Add((GlyphWallSight, (int)Quests.SightRange, (w.X, w.Y)));
        SyncZones(player, player.Id, SightZones, points, new(ReachesOnly(player.Id), Changed: _ => PresentNearby(player)));
        SyncZones(player, player.Id, ZoneKind.MonsterSight, watched is { Count: > 0 } ? [(MonsterSight, (int)Quests.SightRange, default((int, int)))] : [],
            new(id => player.Area.Monsters.GetValueOrDefault(id.Value) is { Dead: false, Life: > 0, Variety: { } v } && watched!.Contains(v.Id) ? 0 : null,
                new EntityId(player.Id), _ => PresentNearby(player)));
    }

    static bool Sees(Player player, uint what)
    {
        return InZone(player, player.Id, SightZones, what, player.Id);
    }

    // The waypoint, the gold in the auto-loot radius and the quests' sight rules, where the player stands.
    static async Task SendNearbyWorld(Player player)
    {
        if (player.PlayerLife == 0) return;
        var watched = QuestsOf(player).Watched(player.Layout.Id);
        if (HardcoreProgressBlocked(player)) return;
        SyncSightZones(player, watched);
        if (player.Layout.Waypoint is not null && Sees(player, WaypointSight))
            await ActivateWaypoint(player, "the player came near");
        // The way walked since the last check counts (PickUpNearbyGold), not only where the player stands.
        var presenting = PresentingOf.GetOrCreateValue(player);
        presenting.GoldCheckedAt = await PickUpNearbyGold(player, presenting.GoldCheckedAt);
        if (HardcoreProgressBlocked(player)) return;
        var at = player.Walk.Position(AreaTime(player));
        // Quests that start when the player comes near a monster (a1q1: Hillock, "Kill Hillock").
        if (watched is { Count: > 0 })
        {
            List<string> seen;
            seen = [.. ZoneMembers(player, player.Id, ZoneKind.MonsterSight, MonsterSight).Select(id => player.Area.Monsters.GetValueOrDefault(id.Value))
                .Where(m => m is { Dead: false, Life: > 0, Variety: not null }).Select(m => m!.Variety!.Id).Distinct()];
            foreach (string variety in seen)
                await SetQuestFlags(player, QuestsOf(player).OnMonsterSeen(player.Layout.Id, variety));
        }
        // a1q4: coming near the Glyph Wall ("Investigate the Mud Flats for a way to lower the water").
        if (player.Glyphs?.Wall is not null && !player.Character.HasQuestFlag(GlyphQuest.SeenGlyphWall) && Sees(player, GlyphWallSight))
            await SetQuestFlags(player, [GlyphQuest.SeenGlyphWall]);
        // The quest engine's sight rules (room objects, exits).
        await CheckQuestSights(player, at);
        if (player.QuestNearSpawns.Count > 0) await CheckQuestNearSpawns(player, at);
    }

    // Death ends the auras, Righteous Fire and Bladefall of Trarthus; an empty mana pool ends Bladefall (the resource
    // writer spends it). Both are stat conditions, so their flip runs this.
    static async Task SendLifeAndManaEndings(Player player)
    {
        if (player.PlayerLife == 0)
        {
            await EndAurasOnDeath(player);
            RighteousFireState? rf;
            rf = player.RighteousFire;
            if (rf is not null) await EndRighteousFire(player, "death", rf);
            if (player.BladefallTrarthus is not null) await EndBladefallTrarthus(player, "death");
        }
        else if (player.BladefallTrarthus is { PaysLife: false } bladefall)
        {
            bool freeMana = CharacterPlayerStats(player).GetValueOrDefault(Stat.NoManaCost) != 0;
            if (!freeMana && player.PlayerMana == 0)
                await EndBladefallTrarthus(player, "out of mana");
            else
                await SendBladefallCost(player, life: false, freeMana ? 0 : bladefall.CostPerMinute);
        }
    }

    sealed class Presenting
    {
        public volatile bool Pending, NearbyPending;
        public (int X, int Y)? GoldCheckedAt;
        // The items the quest items were last synced from (SendQuestItems).
        public PlayerInventories? QuestItems;
        public long QuestItemChanges;
        public Task Running = Task.CompletedTask;
        // When a presentation's state next changes on its own (PresentIn), on the connection's clock.
        public double NextAt = double.PositiveInfinity;
        // The presentations ran once (the verifier compares only after the first stat update showed them).
        public volatile bool Ran;
        public (bool Ignite, int Chill, bool Freeze, int Shock) Ailments;
        public int SightEpoch = int.MinValue;
        public string? SightLayout;
        public bool SightHasWaypoint;
        public (int X, int Y)? SightWall;
        public int SightWatchedCount = -1;
    }

    // The bleed and poison buffs on the player, against the ones the client was shown.
    static async Task SendIncomingAilmentChanges(Player player)
    {
        IncomingDamagingAilments.State nextIncoming;
        IncomingDamagingAilments.State presented;
        int incomingEpoch;
        nextIncoming = player.IncomingAilments.Snapshot(AreaTime(player));
        presented = player.PresentedIncomingAilments ??
            new(false, double.NegativeInfinity, 0, double.NegativeInfinity, 0, 0);
        incomingEpoch = player.IncomingHitEpoch;
        if (nextIncoming == presented) return;
        {
            IncomingAilmentBuffs(player, presented, nextIncoming, AreaTime(player));
            await Task.CompletedTask;
            if (!player.Closed && !player.LeavingArea && player.IncomingHitEpoch == incomingEpoch
                && player.IncomingAilments.Snapshot(AreaTime(player)) == nextIncoming)
            {
                player.PresentedIncomingAilments = nextIncoming;
            }
        }
    }

    // The player's elemental ailment buffs, against the ones the client was shown.
    static async Task SendElementalAilmentChanges(Player player)
    {
        var presenting = PresentingOf.GetOrCreateValue(player);
        var next = player.ElementalAilments.Snapshot(AreaTime(player));
        if (next == presenting.Ailments) return;
        var before = presenting.Ailments;
        presenting.Ailments = next;
        await SendPlayerAilmentBuffs(player, before, next);
    }
    static readonly Store<Player, Presenting> PresentingOf = new();

    /// <summary>What a stat presentation shows changed: they run after the next Stats phase.</summary>
    static void PresentStats(Player player) => PresentingOf.GetOrCreateValue(player).Pending = true;

    /// <summary>A walker moved to another cell or a blocker appeared: the nearby presentations run after the next Stats
    /// phase.</summary>
    public static void PresentNearby(Player player) => PresentingOf.GetOrCreateValue(player).NearbyPending = true;

    /// <summary>A presentation's state changes on its own in <paramref name="seconds"/> (an expiry): they run again then.</summary>
    static void PresentIn(Player player, double seconds)
    {
        if (PresentVerifyTag.Value is not null) return;
        var presenting = PresentingOf.GetOrCreateValue(player);
        double at = AreaTime(player) + Math.Max(0, seconds);
        lock (presenting) if (at < presenting.NextAt) presenting.NextAt = at;
    }

    // The Stats phase, after the player's step.
    static void RunStatPresentations(Player player)
    {
        var presenting = PresentingOf.GetOrCreateValue(player);
        double now = AreaTime(player);
        CheckIntensityPresentation(player, now);
        lock (presenting)
            if (now >= presenting.NextAt)
            {
                presenting.NextAt = double.PositiveInfinity;
                presenting.Pending = true;
            }
        if (!presenting.Pending && !presenting.NearbyPending || !presenting.Running.IsCompleted
            || player.PlayerUpdatesState is not PlayerUpdatesState updates) return;
        bool all = presenting.Pending;
        presenting.Pending = presenting.NearbyPending = false;
        presenting.Ran = true;
        presenting.Running = AsPlayer(player, updates, async () =>
        {
            if (!await SendPlayerTickBatch(player, async () =>
            {
                if (all)
                    foreach (var send in StatPresentations)
                    {
                        await send(player);
                        if (!PlayerUpdatesCurrent(player, updates)) return false;
                    }
                foreach (var send in NearbyPresentations)
                {
                    await send(player);
                    if (!PlayerUpdatesCurrent(player, updates)) return false;
                }
                return true;
            })) return;
            if (all)
                foreach (var send in StatPresentationsAfter)
                {
                    await send(player);
                    if (!PlayerUpdatesCurrent(player, updates)) return;
                }
        });
    }

    // Game_PRESENT_VERIFY=1: the content tick runs them too, tagged; whatever it still sends is a change no event reached.
    static readonly bool VerifyPresentations = Environment.GetEnvironmentVariable("Game_PRESENT_VERIFY") == "1";
    static readonly AsyncLocal<string?> PresentVerifyTag = new();

    static async Task VerifyStatPresentations(Player player)
    {
        var presenting = PresentingOf.GetOrCreateValue(player);
        if (!VerifyPresentations || !presenting.Ran) return;
        // What is still to run (a mark, a due end, a pending or running presentation) is not missed yet.
        if (StatsUnsettled(player) || presenting.Pending || presenting.NearbyPending || !presenting.Running.IsCompleted
            || AreaTime(player) >= presenting.NextAt) return;
        foreach (var send in StatPresentations.Concat(NearbyPresentations).Concat(StatPresentationsAfter))
        {
            PresentVerifyTag.Value = send.Method.Name;
            try { await send(player); }
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
