using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadFlaskGroundsData()
    {
        FlaskGroundArt = FlaskGrounds.LoadArt("data");
    }

    public static Dictionary<FlaskGrounds.Kind, (string Ao, int ArtRadius)> FlaskGroundArt = default!;

    // Ground creation belongs to the accepted charge transaction. Its art receipts and deadline survive
    // replacement, while taunt uses the same accepted character snapshot as the utility effect.
    static void MakeFlaskGroundsLocked(Player player, IReadOnlyDictionary<uint, int> local, double seconds,
        double now, IReadOnlyDictionary<uint, int> playerStats)
    {
        var at = player.Walk.Position(now);
        foreach (var (kind, radius) in FlaskGrounds.OnUse(local))
        {
            if (kind == FlaskGrounds.Kind.Taunt)
            {
                double duration = TauntSeconds(name => name is var key ? playerStats.GetValueOrDefault(key) : 0);
                TauntLocked(player, at, radius, now, duration);
                continue;
            }
            var ground = kind == FlaskGrounds.Kind.ConsecratedGround
                ? CreateOwnConsecratedGroundLocked(player, at, radius, now + seconds, playerStats)
                : new(kind, at.X, at.Y, radius, now + seconds, QueueGroundArtLocked(player, kind, at, radius));
            player.FlaskGrounds.Add(ground);
            PresentNearby(player);
            Console.WriteLine($"[gateway] flask ground {kind} at ({at.X}, {at.Y}) radius {radius} for {seconds:0.##} s" +
                (ground.EnemyDamageTakenPct > 0 ? $", enemies on it take {ground.EnemyDamageTakenPct}% increased damage" : ""));
        }
    }

    /// <summary>Bottled Faith: the increased damage taken a consecrated ground created now applies to enemies on it
    /// (the running flask effects' strongest).</summary>
    static int ConsecratedEnemyDamageTakenNow(Player player) =>
        UtilityFlaskStat(player, FlaskGrounds.EnemyDamageTakenStat);

    // The modifier belongs to this ground's creator and is captured once at creation.
    static FlaskGrounds.Ground CreateOwnConsecratedGroundLocked(Player player, (int X, int Y) at, int radius,
        double ends, IReadOnlyDictionary<uint, int> playerStats)
    {
        if (playerStats.GetValueOrDefault(Stat.GainArcaneSurgeFor4SecondsWhenYouCreateConsecratedGroundWhileAffectedByZealotry) != 0)
            GainArcaneSurgeLocked(player, AreaTime(player));
        int enemyTaken = ActorScale.Saturate((long)ConsecratedEnemyDamageTakenNow(player)
            + playerStats.GetValueOrDefault(Stat.ConsecratedGroundEnemyDamageTakenPlusPctWhileAffectedByZealotry));
        // A flask may have waited for payment since capturing its use stats. Resolve the jewel at ground creation.
        int lingerMs = AffectingAuras(player).Any(aura => aura.EffectId == "Zealotry")
            ? StatSourceValues(player).GetValueOrDefault(
                Stat.ConsecratedGroundEffectLingersForMsAfterLeavingTheAreaWhileAffectedByZealotry)
            : 0;
        return new(FlaskGrounds.Kind.ConsecratedGround, at.X, at.Y, radius, ends,
            QueueGroundArtLocked(player, FlaskGrounds.Kind.ConsecratedGround, at, radius), enemyTaken)
        {
            AdditionalLingerMs = lingerMs,
        };
    }

    /// <summary>The strongest value of a stat the running utility flasks grant (their "during effect" mods).</summary>
    public static int UtilityFlaskStat(Player player, uint stat) =>
        player.UtilityFlaskEffects.MaximumStat(stat);

    public static Task MaybeCreateProfaneGround(Player player, (int X, int Y) at)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0) return Task.CompletedTask;
        var playerStats = CharacterPlayerStats(player);
        MaybeCreateProfaneGroundLocked(player, at, playerStats);
        return PublishFlaskChanges(player);
    }

    static void MaybeCreateProfaneGroundLocked(Player player, (int X, int Y) at, IReadOnlyDictionary<uint, int> playerStats)
    {
        int chance = FlaskGrounds.ProfaneChance(name => name is var key ? playerStats.GetValueOrDefault(key) : 0);
        if (chance <= 0 || AreaRng(player).Next(100) >= chance) return;
        player.FlaskGrounds.Add(FlaskGrounds.ProfaneGround(at, AreaTime(player),
            QueueGroundArtLocked(player, FlaskGrounds.Kind.ProfaneGround, at, FlaskGrounds.ProfaneRadius)));
        PresentNearby(player);
    }

    static uint? QueueGroundArtLocked(Player player, FlaskGrounds.Kind kind, (int X, int Y) at, int radius)
    {
        if (!FlaskGroundArt.TryGetValue(kind, out var art)) return null;
        uint obj = player.TakeObjectId();
        float scale = (float)(radius * (250.0 / 23) / Math.Max(1, art.ArtRadius));
        var effect = new EffectObject(at.X, at.Y, art.Ao) { Flags0 = EffectObject.FixedRotation, Scale = scale };
        var packets = Automation(player).Packets;
        packets.Add(new(InstanceEntities.CreateObject(obj, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob()),
            $"0x02bb create effect 0x{obj:x} {art.Ao} at ({at.X}, {at.Y}), scale {scale:0.###} for radius {radius}", WorldId: obj, CreatesObject: true));
        packets.Add(new(InstanceProtocol.EnterWakeZone(obj), WorldId: obj));
        return obj;
    }

    // The current area claim advances flask and stationary grounds together: expiry, smoke, linger and
    // presentation cannot be stranded in an old timer when Game is replaced.
    // The grounds are zones (one per ground, at it) reaching the player and the hostile monsters; a walker in or out
    // presents them again.
    const ZoneKind FlaskGroundZones = ZoneKind.FlaskGrounds;

    static void SyncFlaskGroundZones(Player player)
    {
        var hostile = ReachesHostileMonsters(player, body: false);
        SyncZones(player, player.Id, FlaskGroundZones, player.FlaskGrounds.Grounds.Select(g => (g.Id, g.Radius, (g.X, g.Y))),
            new(id => id.Value == player.Id ? 0 : hostile(id), Changed: _ => PresentNearby(player)));
    }

    // Whether a ground of the kind covers the monster.
    static bool InFlaskGround(Player player, FlaskGrounds.Kind kind, uint id) =>
        player.FlaskGrounds.Grounds.Any(g => g.Kind == kind && InZone(player, player.Id, FlaskGroundZones, g.Id, id));

    // Bottled Faith checks the target's ground independently of who created it.
    static bool EnemyOnConsecratedGround(Player player, uint id)
    {
        if (!player.Area.Monsters.ContainsKey(id)) return false;
        foreach (var owner in AreaPlayers(player))
        {
            if (owner.Closed || owner.LeavingArea || !owner.FlaskGrounds.Any) continue;
            double now = AreaTime(owner);
            foreach (var ground in owner.FlaskGrounds.Grounds)
                if (ground.Kind == FlaskGrounds.Kind.ConsecratedGround && now < ground.Ends
                    && (ground.SourceAlive?.Invoke(now) ?? true) && InZone(owner, owner.Id, FlaskGroundZones, ground.Id, id))
                    return true;
        }
        return false;
    }

    // The increased damage taken a monster gets from the consecrated grounds covering it: the strongest one.
    static int ConsecratedEnemyDamageTaken(Player player, uint id) => player.FlaskGrounds.Grounds
        .Where(g => g is { Kind: FlaskGrounds.Kind.ConsecratedGround, EnemyDamageTakenPct: > 0 } && InZone(player, player.Id, FlaskGroundZones, g.Id, id))
        .Select(g => g.EnemyDamageTakenPct).DefaultIfEmpty(0).Max();

    internal static bool OnConsecratedGround(FlaskGrounds.State state, bool alive, IEnumerable<FlaskGrounds.Ground> covering, double now)
    {
        FlaskGrounds.Coverage coverage = default, curseCoverage = default;
        foreach (var ground in covering)
        {
            if (ground.Kind != FlaskGrounds.Kind.ConsecratedGround) continue;
            coverage = CoverWithGround(coverage, ground);
            if (ground.GrantsCurseImmunity) curseCoverage = CoverWithGround(curseCoverage, ground);
        }
        return state.OnConsecrated(alive, now, coverage, curseCoverage, ResolveGroundLinger);
    }

    static FlaskGrounds.Coverage CoverWithGround(FlaskGrounds.Coverage coverage, FlaskGrounds.Ground ground)
    {
        if (ground.AdditionalLingerMs > 0 && (ground.AdditionalLingerMs > coverage.LingerMs
            || ground.AdditionalLingerMs == coverage.LingerMs && ground.Ends > coverage.SourceEnds))
            return new(true, ground.AdditionalLingerMs, ground.Ends);
        return coverage with { Inside = true };
    }

    static FlaskGrounds.Coverage ResolveGroundLinger(FlaskGrounds.Coverage previous, FlaskGrounds.Coverage current, double now)
    {
        double? until = previous.LingersUntil;
        // A naturally expired ground stopped covering the player at its deadline even if processed later.
        if (current.LingerMs < previous.LingerMs)
            until = Math.Max(until ?? double.NegativeInfinity, Math.Min(now, previous.SourceEnds) + previous.LingerMs / 1000.0);
        return current with { LingersUntil = until };
    }

    static bool SetConsecratedGroundBuff(Player player, bool on)
    {
        if (player.FlaskGrounds.SetConsecrated(on, () => 1) is not { } change) return false;
        if (change.Added) player.Area.Buffs.Set(player.Id, new(FlaskGrounds.ConsecratedBuffRow, player.Id));
        else player.Area.Buffs.Clear(player.Id, FlaskGrounds.ConsecratedBuffRow, player.Id);
        return true;
    }

    static void ClearConsecratedGroundOnResurrect(Player player)
    {
        var state = player.FlaskGrounds;
        bool wasCurseImmune = state.CurseImmune;
        state.OnConsecrated(alive: false, covered: false, now: AreaTime(player));
        if (!SetConsecratedGroundBuff(player, on: false) && !wasCurseImmune) return;
        Automation(player).StatRevision++;
        MarkPlayerStats(player, PlayerStatStep.UtilityFlasks);
        MarkPlayerStats(player, PlayerStatStep.SelfVulnerability);
    }

    public static void AdvanceFlaskGrounds(Player player, PlayerUpdatesState updates)
    {
        if (player.FlaskGrounds.Any) _ = FlaskGroundArt;
        if (!PlayerUpdatesCurrent(player, updates) || player.LeavingArea || player.Paused || !player.FlaskGrounds.Any) return;
        double now = updates.Schedule.Now(player);
        var state = player.FlaskGrounds;
        var pending = Automation(player);
        foreach (var ground in state.Expire(now))
            if (ground.Object is uint obj) pending.Packets.Add(new(InstanceProtocol.RemoveObject(obj),
                $"0x02bd remove effect 0x{obj:x} ({ground.Kind} ends)", WorldId: obj, RemovesObject: true));
        foreach (uint gone in state.BlindedMonsters().Except(player.Area.Monsters.Keys)) state.SetBlinded(gone, false, () => 0);
        foreach (uint gone in state.ProfaneMonsters().Except(player.Area.Monsters.Keys))
            state.SetProfane(gone, false, false, now, () => 0);
        SyncFlaskGroundZones(player);
        bool chilledAny = false;
        foreach (var (id, monster) in player.Area.Monsters)
        {
            bool inside = monster.Hostile && !monster.Dead && monster.Life > 0 && monster.Mods.Effects[Stat.CannotBeBlinded] == 0
                && InFlaskGround(player, FlaskGrounds.Kind.SmokeCloud, id);
            if (state.SetBlinded(id, inside, () => 1) is { } change)
            {
                if (change.Added) player.Area.Buffs.Set(id, new(FlaskGrounds.BlindedBuffRow, player.Id, Tag: (ushort)BuffTag.FlaskBlind));
                else player.Area.Buffs.Clear(id, FlaskGrounds.BlindedBuffRow, player.Id, tag: (ushort)BuffTag.FlaskBlind);
            }
            bool aliveMonster = monster.Hostile && !monster.Dead && monster.Life > 0;
            bool inProfane = aliveMonster && InFlaskGround(player, FlaskGrounds.Kind.ProfaneGround, id);
            // Aquamarine Flask's chilled ground: the same chill Arctic Armour's trail applies
            // (ReservationSkills): 0.3 s at the stated 10% action-speed slow, refreshed every 0.2 s while
            // a monster remains in the ground.
            if (aliveMonster && InFlaskGround(player, FlaskGrounds.Kind.ChilledGround, id))
            {
                chilledAny = true;
                InflictedLocked(player, monster, monster.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill,
                    .3, now, _ => 0, n => monster.Mods.Effects[n], stated: 10));
            }
            if (state.SetProfane(id, inProfane, aliveMonster, now, () => 1) is { } profane)
            {
                if (profane.Added) player.Area.Buffs.Set(id, new(FlaskGrounds.ProfaneBuffRow, player.Id));
                else player.Area.Buffs.Clear(id, FlaskGrounds.ProfaneBuffRow, player.Id);
            }
        }
        if (chilledAny) PresentIn(player, .2);
        bool alive = player.PlayerLife > 0;
        var here = player.Walk.Position(now);
        var (stale, make) = state.Stationary(alive && !player.Walk.Moving(now) && !InTown(player), here);
        if (stale?.Object is uint old) pending.Packets.Add(new(InstanceProtocol.RemoveObject(old),
            $"0x02bd remove effect 0x{old:x} (stationary consecrated ground)", WorldId: old, RemovesObject: true));
        if (make > 0)
        {
            state.PlaceStationary(CreateOwnConsecratedGroundLocked(player, here, make,
                double.PositiveInfinity, StatSourceValues(player)));
            SyncFlaskGroundZones(player);
        }
        // A ground placed this step has its members from the next movement update; until then its cells decide.
        var covering = state.Grounds.Where(g => g.Kind == FlaskGrounds.Kind.ConsecratedGround
            && (InZone(player, player.Id, FlaskGroundZones, g.Id, player.Id) || g.Covers(here)));
        bool wasCurseImmune = state.CurseImmune;
        bool consecratedChanged = SetConsecratedGroundBuff(player, OnConsecratedGround(state, alive, covering, now));
        if (consecratedChanged || wasCurseImmune != state.CurseImmune)
        {
            pending.StatRevision++;
            if (HasMapHexes(MapModsOf(player))) RecomputePoolsAndSpeed(player);
        }
        state.StopTickerIfIdle();
    }

    // A nearby presentation: grounds placed (PresentNearby), walkers in or out of them, the player still or moving (a stat
    // condition), and each ground's end or linger (PresentIn).
    static async Task SendFlaskGrounds(Player player)
    {
        if (player.PlayerUpdatesState is not PlayerUpdatesState updates) return;
        AdvanceFlaskGrounds(player, updates);
        double now = updates.Schedule.Now(player);
        if (player.FlaskGrounds.NextChangeAt(now) is double next) PresentIn(player, next - now + 1e-6);
        if (!PlayerUpdatesCurrent(player, updates)) return;
        await PublishFlaskChanges(player);
    }
}
