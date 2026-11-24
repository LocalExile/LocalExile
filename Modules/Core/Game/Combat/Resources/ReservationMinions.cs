using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static GolemState.Golem? OwnedFighter(Player player, uint id) =>
        player.Golems.All.GetValueOrDefault(id) ?? player.Reservations.Fighters.GetValueOrDefault(id) ?? player.Summons.Fighters.GetValueOrDefault(id);

    static Dictionary<uint, int> ReservationMinionStats(Player player, AuraCatalog.Aura aura, string kind)
    {
        var own = new Dictionary<uint, int>(SupportedSkill(player, aura.SkillId).Stats);
        if (kind == "Agony")
        {
            int stacks = player.Reservations.Virulence;
            GolemAdd(own, Stat.Of("minion_physical_damage_+%"), ReservationStat(player, aura, Stat.ScorpionMinionPhysicalDamagePlusPct) * stacks);
            GolemAdd(own, Stat.MinionAttackSpeedPlusPct, ReservationStat(player, aura, Stat.ScorpionMinionAttackSpeedPlusPct) * stacks);
            GolemAdd(own, Stat.MinionGlobalMinimumAddedPhysicalDamage, ReservationStat(player, aura, Stat.ScorpionMinionMinimumAddedPhysicalDamage) * stacks);
            GolemAdd(own, Stat.MinionGlobalMaximumAddedPhysicalDamage, ReservationStat(player, aura, Stat.ScorpionMinionMaximumAddedPhysicalDamage) * stacks);
        }
        return own;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, uint> MinionStatIds = new();

    static uint MinionStatId(uint n) => MinionStatIds.GetOrAdd(n, static key =>
        Stats.Name(key) is { Length: > 0 } name ? Stat.Of("minion_" + name) : 0);

    static IReadOnlyList<(uint Key, int Value)> SkitterMinionStats =>
        [.. MinionStats, (Stat.HideMiniLifeBar, 1), (Stat.BaseCannotBeDamaged, 1)];

    static int SkitterAuraRadius(GolemState.Golem fighter) =>
        SkillArea.Radius(30, n => GolemStat(fighter.Own, n) + GolemStat(fighter.Player, MinionStatId(n)), spell: true);

    static async Task SummonReservationFighter(Player player,
        AuraCatalog.Aura aura, string kind)
    {
        string path = kind switch
        {
            "Purity" => "Metadata/Monsters/Axis/AxisEliteSoldierHeraldOfLight",
            "Agony" => "Metadata/Monsters/SpiderPlated/HeraldOfAgonySpiderPlated",
            "SkitterCold" => "Metadata/Monsters/Skitterbot/SkitterbotCold",
            "SkitterLightning" => "Metadata/Monsters/Skitterbot/SkitterbotLightning",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (Monsters.Find(path) is not { Supported: true } variety) return;
        var supported = SupportedSkill(player, aura.SkillId);
        if (!supported.RequirementsMet) return;
        var playerStats = CharacterPlayerStats(player);
        if (!DarkMonarchAllows(kind, playerStats)) return;
        int limit = kind == "Purity" ? PurityMinionLimit(supported, playerStats) : 1;
        if (limit <= 0) return;
        if (ReservationAura(player, aura.EffectId) is null) return;
        var existing = player.Reservations.Fighters.Values.Where(g => g.Definition.Kind == kind && !g.Monster.Dead).ToArray();
        if (existing.Length >= limit)
        {
            if (kind == "Purity")
            {
                var refresh = existing.MinBy(g => player.Reservations.Until.GetValueOrDefault(g.Id))!;
                player.Reservations.Until[refresh.Id] = AreaTime(player) + ReservationDuration(player, aura, Stat.BaseSkillEffectDuration, 12000);
                refresh.Monster.Life = refresh.Monster.MaxLife;
                player.Minions.OutputQueue.Add(new([], "Sentinel of Purity refreshed", refresh.Id, refresh.Monster, PlayerMinions.OutputKind.Life));
            }
            return;
        }
        byte level = (byte)Math.Clamp(GolemStat(supported.Stats, Stat.BaseDisplayMinionActorLevel) is > 0 and var actorLevel
            ? actorLevel : SkillsFor(player).PlayerLevelReq(aura.Effect, aura.Level), 1, 100);
        var definition = new GolemCatalog.Definition(aura.Effect, aura.EffectId, kind, path, 0, [], false, false, IsGolem: false);
        playerStats = CharacterPlayerStats(player);
        if (!CanHaveMinion(playerStats, OwnedMinionFamily.Other)) return;
        bool skitter = kind.StartsWith("Skitter", StringComparison.Ordinal);
        double summonDuration = kind == "Purity"
            ? ReservationDuration(player, aura, Stat.BaseSkillEffectDuration, 12000) : double.PositiveInfinity;
        double freshMeat = !skitter ? FreshMeatSeconds(supported.Stats, summonDuration) : 0;
        double preparedNow = AreaTime(player);
        var fighter = new GolemState.Golem(0, null!, definition, aura.SkillId)
        {
            GemLevel = supported.Level, Quality = supported.Quality, Own = ReservationMinionStats(player, aura, kind),
            Player = new(playerStats), Limit = limit,
            FreshMeatUntil = freshMeat > 0 ? preparedNow + freshMeat : 0,
        };
        var mods = GolemModifiers(player, fighter, playerStats, preparedNow);
        var pools = GolemPools.Calculate(Monsters, variety, level, fighter, playerStats, mods.Effects);
        uint transportMaximum = pools.Fatal ? 1 : pools.MaximumLife;
        uint maximumEnergyShield = NativeEnergyShield(pools.MaximumEnergyShield);
        uint energyShield = maximumEnergyShield;
        if (!pools.Fatal)
        {
            fighter.Abilities = skitter
                ? [] : GolemAbilities(player, variety, level, fighter, playerStats, preparedNow);
            fighter.Skills = GolemNativeSkills(player, level, fighter.Abilities);
        }
        var at = NextToPlayer(player, Math.Clamp(variety.ObjectSize - 1, 0, 127));
        await SpawnMonster(player, variety, level, MonsterRarity.Normal, at.X, at.Y,
            friendly: true, mods: mods, unrisen: false, extraStats: skitter ? SkitterMinionStats : MinionStats, ownedSkills: fighter.Skills,
            ownedMaximumLife: transportMaximum, ownedMaximumEnergyShield: maximumEnergyShield,
            ownedEnergyShield: energyShield, damagedLife: pools.Fatal ? 1 : transportMaximum,
            ownedInitialDeath: pools.Fatal,
            ownedCurrent: () => ReservationAura(player, aura.EffectId)?.SkillId == aura.SkillId
                && CanHaveOtherMinions(player) && DarkMonarchAllows(kind, StatSourceValues(player)),
            owned: (id, monster) =>
            {
                double committed = AreaTime(player);
                fighter.Id = id; fighter.Monster = monster;
                fighter.SummonedAt = fighter.LastStep = committed;
                if (freshMeat > 0) fighter.FreshMeatUntil = committed + freshMeat;
                fighter.ShieldPool.SetMaximum(maximumEnergyShield, refill: true);
                monster.Mods = mods;
                player.Reservations.Fighters.Add(id, fighter);
                player.Reservations.Until[id] = double.IsFinite(summonDuration) ? committed + summonDuration : double.PositiveInfinity;
                player.Minions.Add(new(id, monster, aura.EffectId, kind)
                { Until = player.Reservations.Until[id], OwnerAura = RollDivineSentinelAura(player, fighter) });
                monster.NoRewards = true; monster.Aggro = true;
                if (pools.Fatal) GolemDieLocked(player, fighter, committed, emitLife: false);
            });
    }

    static int PurityMinionLimit(SupportedGem gem, IReadOnlyDictionary<uint, int> playerStats) =>
        (int)Math.Clamp(DarkMonarchLimit("Purity",
            (long)GolemStat(gem.Stats, Stat.BaseNumberOfChampionsOfLightAllowed)
            + GolemStat(playerStats, Stat.BaseNumberOfChampionsOfLightAllowed), playerStats), 0, 200);

    static void RemoveReservationFighterLocked(Player player, GolemState.Golem fighter, double now)
    {
        ClearSkitterAuraLocked(fighter);
        player.Reservations.Fighters.Remove(fighter.Id);
        player.Reservations.Until.Remove(fighter.Id);
        RemoveGolemLocked(player, fighter, now);
    }

    static async Task ReservationFightersTick(Player player, double now)
    {
        var state = player.Reservations;
        using (ProjectileMutation(player))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return;
            var agony = ReservationAura(player, "HeraldOfAgony");
            if (agony is null) state.Virulence = 0;
            else
            {
                // The exact retail decay curve is server-only. This increasing rate is deliberately isolated.
                while (state.Virulence > 0 && now >= state.NextVirulence)
                {
                    state.Virulence--;
                    double slower = Math.Max(.01, 1 + GolemStat(CharacterPlayerStats(player), Stat.Of("virulence_decay_rate_+%")) / 100.0);
                    state.NextVirulence += Math.Max(.05, 1.0 / Math.Max(1, state.Virulence)) / slower;
                }
            }
            ReservationStackBuffLocked(player, ref state.VirulenceBuff, ref state.ShownVirulence, state.Virulence, 1178);
            foreach (var fighter in state.Fighters.Values.ToArray())
                if (fighter.Monster.Dead || fighter.Monster.Life == 0 || now >= state.Until.GetValueOrDefault(fighter.Id)
                    || ReservationAura(player, fighter.Definition.Id)?.SkillId != fighter.Source
                    || fighter.Definition.Kind == "Agony" && state.Virulence == 0)
                    RemoveReservationFighterLocked(player, fighter, now);
            GolemIncomingContactsLocked(player, now);
            bool refresh = now >= state.RefreshFightersAt;
            if (refresh) state.RefreshFightersAt = now + .5;
            var playerStats = refresh && state.Fighters.Count > 0 ? CharacterPlayerStats(player) : null;
            if (playerStats is not null && ReservationAura(player, "HeraldOfPurity") is { } purity)
            {
                int limit = PurityMinionLimit(SupportedSkill(player, purity.SkillId), playerStats);
                foreach (var excess in state.Fighters.Values.Where(fighter => fighter.Definition.Kind == "Purity")
                    .OrderByDescending(fighter => state.Until[fighter.Id]).Skip(limit).ToArray())
                    RemoveReservationFighterLocked(player, excess, now);
            }
            foreach (var fighter in state.Fighters.Values.ToArray())
            {
                if (!player.Auras.TryGetValue(fighter.Source, out var aura)) continue;
                if (refresh)
                {
                    int PlayerStat(uint s) => GolemStat(playerStats!, s);
                    if (!MinionFamilyAllowed(PlayerStat, fighter.Definition.Kind))
                    {
                        RemoveReservationFighterLocked(player, fighter, now);
                        continue;
                    }
                    if (fighter.Definition.Kind == "Purity")
                    {
                        int purityLimit = ApplyDarkMonarchLimit(ReservationStat(player, aura, Stat.BaseNumberOfChampionsOfLightAllowed)
                            + PlayerStat(Stat.BaseNumberOfChampionsOfLightAllowed), PlayerStat, "Purity");
                        if (state.Fighters.Values.Count(g => g.Definition.Kind == "Purity" && !g.Monster.Dead) > purityLimit)
                        {
                            RemoveReservationFighterLocked(player, fighter, now);
                            continue;
                        }
                    }
                    fighter.Own = ReservationMinionStats(player, aura, fighter.Definition.Kind);
                    RefreshGolemLocked(player, fighter, playerStats!, now);
                }
                if (fighter.Monster.Dead || fighter.Monster.Life == 0) continue;
                GolemStepLocked(player, fighter, now);
                if (fighter.Monster.Dead || fighter.Monster.Life == 0
                    || !fighter.Definition.Kind.StartsWith("Skitter", StringComparison.Ordinal)) continue;
                SkitterAuraTickLocked(player, fighter, now);
            }
        }
        if (ReservationAura(player, "HeraldOfAgony") is { } activeAgony && state.Virulence > 0)
            await SummonReservationFighter(player, activeAgony, "Agony");
        if (ReservationAura(player, "SummonSkitterbots") is { } skitter)
        {
            await SummonReservationFighter(player, skitter, "SkitterCold");
            await SummonReservationFighter(player, skitter, "SkitterLightning");
        }
    }

    static PlacedSkills.Placed? PendingSkitterPlaced(Player player, GolemState.Golem fighter, (int X, int Y) owner, double now)
    {
        if (player.Placed.All.Count == 0 || now < fighter.NextOrb) return null;
        var state = player.Reservations;
        var here = fighter.Monster.Walk.Position(now);
        PlacedSkills.Placed? best = null;
        double bestDist = double.MaxValue;
        foreach (var p in player.Placed.All.Values)
        {
            if (p.SkitterTriggered || now < p.Armed || Dist(p.At, owner) > 100) continue;
            GolemState.Golem? nearest = null;
            double nearestDist = double.MaxValue;
            foreach (var g in state.Fighters.Values)
            {
                if (!g.Definition.Kind.StartsWith("Skitter", StringComparison.Ordinal) || g.Monster.Dead || g.Monster.Life == 0 || now < g.NextOrb) continue;
                double d = Dist(g.Monster.Walk.Position(now), p.At);
                if (d < nearestDist) { nearestDist = d; nearest = g; }
            }
            if (!ReferenceEquals(nearest, fighter)) continue;
            double dist = Dist(p.At, here);
            if (dist < bestDist) { bestDist = dist; best = p; }
        }
        return best;
    }

    public static int SkitterAilmentStat(GolemState.Golem fighter, uint name)
    {
        int Get(uint n) => GolemStat(fighter.Own, n) + GolemStat(fighter.Player, MinionStatId(n));
        return name switch {
            Stat.VirtualChillEffectPlusPct => Get(name) + Get(Stat.ChillEffectPlusPct) + Get(Stat.ColdAilmentEffectPlusPct) + Get(Stat.NonDamagingAilmentEffectPlusPct),
            Stat.VirtualShockEffectPlusPct => Get(name) + Get(Stat.ShockEffectPlusPct) + Get(Stat.LightningAilmentEffectPlusPct) + Get(Stat.NonDamagingAilmentEffectPlusPct),
            _ => Get(name),
        };
    }

    static void ReservationStackBuffLocked(Player player, ref ushort instance, ref int shown, int count, ushort row)
    {
        if (shown == count) return;
        shown = count;
        instance = (ushort)(count > 0 ? 1 : 0);
        if (count > 0) player.Area.Buffs.Set(player.Id, new(row, player.Id, Stacks: (ushort)Math.Min(ushort.MaxValue, count)));
        else player.Area.Buffs.Clear(player.Id, row, player.Id);
    }
}
