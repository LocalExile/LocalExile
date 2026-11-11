using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    static GolemState.Golem? OwnedFighter(ClientState conn, uint id) =>
        conn.Golems.All.GetValueOrDefault(id) ?? conn.Reservations.Fighters.GetValueOrDefault(id) ?? conn.Summons.Fighters.GetValueOrDefault(id);

    static Dictionary<uint, int> ReservationMinionStats(ClientState conn, AuraCatalog.Aura aura, string kind)
    {
        var own = new Dictionary<uint, int>(SupportedSkill(conn, aura.SkillId).Stats);
        if (kind == "Agony")
        {
            int stacks = conn.Reservations.Virulence;
            GolemAdd(own, "minion_physical_damage_+%", ReservationStat(conn, aura, "scorpion_minion_physical_damage_+%") * stacks);
            GolemAdd(own, "minion_attack_speed_+%", ReservationStat(conn, aura, "scorpion_minion_attack_speed_+%") * stacks);
            GolemAdd(own, "minion_global_minimum_added_physical_damage", ReservationStat(conn, aura, "scorpion_minion_minimum_added_physical_damage") * stacks);
            GolemAdd(own, "minion_global_maximum_added_physical_damage", ReservationStat(conn, aura, "scorpion_minion_maximum_added_physical_damage") * stacks);
        }
        return own;
    }

    static async Task SummonReservationFighter(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
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
        var supported = SupportedSkill(conn, aura.SkillId);
        if (!supported.RequirementsMet) return;
        int limit = kind == "Purity" ? ReservationStat(conn, aura, "base_number_of_champions_of_light_allowed")
            + GolemStat(CharacterPlayerStats(conn), "base_number_of_champions_of_light_allowed") : 1;
        if (limit <= 0) return;
        lock (conn.Monsters)
        {
            if (ReservationAura(conn, aura.EffectId) is null) return;
            var existing = conn.Reservations.Fighters.Values.Where(g => g.Definition.Kind == kind && !g.Monster.Dead).ToArray();
            if (existing.Length >= limit)
            {
                if (kind == "Purity")
                {
                    var refresh = existing.MinBy(g => conn.Reservations.Until.GetValueOrDefault(g.Id))!;
                    conn.Reservations.Until[refresh.Id] = AreaTime(conn) + ReservationDuration(conn, aura, "base_skill_effect_duration", 12000);
                    refresh.Monster.Life = refresh.Monster.MaxLife;
                    conn.Minions.OutputQueue.Add(new([], "Sentinel of Purity refreshed", refresh.Id, refresh.Monster, PlayerMinions.OutputKind.Life));
                }
                return;
            }
        }
        byte level = (byte)Math.Clamp(GolemStat(supported.Stats, "base_display_minion_actor_level") is > 0 and var actorLevel
            ? actorLevel : SkillsFor(conn).PlayerLevelReq(aura.Effect, aura.Level), 1, 100);
        var definition = new GolemCatalog.Definition(aura.Effect, aura.EffectId, kind, path, 0, [], false, false, IsGolem: false);
        var player = CharacterPlayerStats(conn);
        var fighter = new GolemState.Golem(0, null!, definition, aura.SkillId)
        { GemLevel = supported.Level, Quality = supported.Quality, Own = ReservationMinionStats(conn, aura, kind), Player = new(player), Limit = limit };
        var mods = GolemModifiers(conn, fighter, player);
        var pools = GolemPools.Calculate(Monsters, variety, level, fighter, player, mods.Effects);
        uint transportMaximum = pools.Fatal ? 1 : pools.MaximumLife;
        uint maximumEnergyShield = NativeEnergyShield(pools.MaximumEnergyShield);
        uint energyShield = maximumEnergyShield;
        if (!pools.Fatal)
        {
            fighter.Abilities = kind.StartsWith("Skitter", StringComparison.Ordinal)
                ? [] : GolemAbilities(conn, variety, level, fighter, player);
            fighter.Skills = GolemNativeSkills(conn, level, fighter.Abilities);
        }
        var at = NextToPlayer(conn);
        await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Normal, at.X, at.Y,
            friendly: true, mods: mods, unrisen: false, extraStats: MinionStats, ownedSkills: fighter.Skills,
            ownedMaximumLife: transportMaximum, ownedMaximumEnergyShield: maximumEnergyShield,
            ownedEnergyShield: energyShield, damagedLife: pools.Fatal ? 1 : transportMaximum,
            ownedInitialDeath: pools.Fatal,
            ownedCurrent: () => ReservationAura(conn, aura.EffectId)?.SkillId == aura.SkillId,
            owned: (id, monster) =>
            {
                fighter.Id = id; fighter.Monster = monster;
                fighter.SummonedAt = fighter.LastStep = AreaTime(conn);
                fighter.ShieldPool.SetMaximum(maximumEnergyShield, refill: true);
                monster.Mods = mods;
                conn.Reservations.Fighters.Add(id, fighter);
                conn.Reservations.Until[id] = kind == "Purity"
                    ? AreaTime(conn) + ReservationDuration(conn, aura, "base_skill_effect_duration", 12000) : double.PositiveInfinity;
                conn.Minions.Add(new(id, monster, aura.EffectId, kind) { Until = conn.Reservations.Until[id] });
                monster.NoRewards = true; monster.Aggro = true;
                if (pools.Fatal) GolemDieLocked(conn, fighter, AreaTime(conn), emitLife: false);
            });
    }

    static void RemoveReservationFighterLocked(ClientState conn, GolemState.Golem fighter, double now)
    {
        conn.Reservations.Fighters.Remove(fighter.Id);
        conn.Reservations.Until.Remove(fighter.Id);
        RemoveGolemLocked(conn, fighter, now);
    }

    static async Task ReservationFightersTick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double now)
    {
        var state = conn.Reservations;
        using (ProjectileMutation(conn))
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused) return;
            var agony = ReservationAura(conn, "HeraldOfAgony");
            if (agony is null) state.Virulence = 0;
            else
            {
                // The exact retail decay curve is server-only. This increasing rate is deliberately isolated.
                while (state.Virulence > 0 && now >= state.NextVirulence)
                {
                    state.Virulence--;
                    double slower = Math.Max(.01, 1 + GolemStat(CharacterPlayerStats(conn), "virulence_decay_rate_+%") / 100.0);
                    state.NextVirulence += Math.Max(.05, 1.0 / Math.Max(1, state.Virulence)) / slower;
                }
            }
            ReservationStackBuffLocked(conn, ref state.VirulenceBuff, ref state.ShownVirulence, state.Virulence, 1178);
            foreach (var fighter in state.Fighters.Values.ToArray())
                if (fighter.Monster.Dead || fighter.Monster.Life == 0 || now >= state.Until.GetValueOrDefault(fighter.Id)
                    || ReservationAura(conn, fighter.Definition.Id)?.SkillId != fighter.Source
                    || fighter.Definition.Kind == "Agony" && state.Virulence == 0)
                    RemoveReservationFighterLocked(conn, fighter, now);
            GolemIncomingContactsLocked(remote, stream, ciphers, conn, now);
            bool refresh = now >= state.RefreshFightersAt;
            if (refresh) state.RefreshFightersAt = now + .5;
            var player = refresh && state.Fighters.Count > 0 ? CharacterPlayerStats(conn) : null;
            foreach (var fighter in state.Fighters.Values.ToArray())
            {
                if (!conn.Auras.TryGetValue(fighter.Source, out var aura)) continue;
                if (refresh)
                {
                    fighter.Own = ReservationMinionStats(conn, aura, fighter.Definition.Kind);
                    RefreshGolemLocked(conn, fighter, player!);
                }
                if (fighter.Monster.Dead || fighter.Monster.Life == 0) continue;
                var pending = fighter.Definition.Kind.StartsWith("Skitter", StringComparison.Ordinal) && now >= fighter.NextOrb
                    ? conn.Placed.All.Values.Where(p => !p.SkitterTriggered && now >= p.Armed
                        && Dist(p.At, conn.Walk.Position(now)) <= 100).MinBy(p => Dist(p.At, fighter.Monster.Walk.Position(now))) : null;
                if (pending is not null) GolemMoveLocked(conn, fighter, pending.At, now);
                else GolemStepLocked(remote, stream, ciphers, conn, fighter, now);
                if (fighter.Monster.Dead || fighter.Monster.Life == 0
                    || !fighter.Definition.Kind.StartsWith("Skitter", StringComparison.Ordinal)) continue;
                bool cold = fighter.Definition.Kind == "SkitterCold";
                foreach (var monster in conn.Monsters.Values)
                    if (monster is { Hostile: true, Dead: false, Life: > 0 }
                        && Dist(monster.Walk.Position(now), fighter.Monster.Walk.Position(now)) <= SkillArea.Radius(30,
                            n => GolemStat(fighter.Own, n) + GolemStat(fighter.Player, "minion_" + n), spell: true))
                        monster.Ailments.WithoutDamage(cold ? MonsterElementalAilments.Kind.Chill : MonsterElementalAilments.Kind.Shock,
                            .2, now, n => SkitterAilmentStat(fighter, n),
                            n => monster.Mods.Effects[n]);
            }
        }
        if (ReservationAura(conn, "HeraldOfAgony") is { } activeAgony && state.Virulence > 0)
            await SummonReservationFighter(remote, stream, ciphers, conn, activeAgony, "Agony");
        if (ReservationAura(conn, "SummonSkitterbots") is { } skitter)
        {
            await SummonReservationFighter(remote, stream, ciphers, conn, skitter, "SkitterCold");
            await SummonReservationFighter(remote, stream, ciphers, conn, skitter, "SkitterLightning");
        }
    }

    internal static int SkitterAilmentStat(GolemState.Golem fighter, string name)
    {
        int Get(string n) => GolemStat(fighter.Own, n) + GolemStat(fighter.Player, "minion_" + n);
        return name switch {
            "virtual_chill_effect_+%" => Get(name) + Get("chill_effect_+%") + Get("cold_ailment_effect_+%") + Get("non_damaging_ailment_effect_+%"),
            "virtual_shock_effect_+%" => Get(name) + Get("shock_effect_+%") + Get("lightning_ailment_effect_+%") + Get("non_damaging_ailment_effect_+%"),
            _ => Get(name),
        };
    }

    static void ReservationStackBuffLocked(ClientState conn, ref ushort instance, ref int shown, int count, ushort row)
    {
        if (shown == count) return;
        if (instance != 0) conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, instance), null));
        instance = 0; shown = count;
        if (count > 0)
            conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(PlayerId, instance = conn.NextBuff++, row, PlayerId,
                stacks: (ushort)Math.Min(ushort.MaxValue, count)), "reservation stacks"));
    }
}
