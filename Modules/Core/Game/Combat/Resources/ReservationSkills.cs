using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    static void EndReservationRuntimeLocked(ClientState conn, AuraCatalog.Aura aura)
    {
        EndCatAspectRuntimeLocked(conn, aura);
        EndAvianAspectLocked(conn, aura);
        EndCrabAspectLocked(conn, aura);
        EndSpiderAspectLocked(conn, aura);
        var state = conn.Reservations;
        var pending = state.Procs.Where(p => p.Skill != aura.SkillId).ToArray();
        state.Procs.Clear(); foreach (var proc in pending) state.Procs.Enqueue(proc);
        if (aura.EffectId == "HeraldOfThunder") state.ThunderUntil = state.NextThunder = 0;
        if (aura.EffectId == "HeraldOfAgony") state.Virulence = 0;
        if (aura.EffectId == "TemporalRift") state.Echoes.Clear();
        foreach (var fighter in state.Fighters.Values.Where(g => g.Source == aura.SkillId).ToArray())
            RemoveReservationFighterLocked(conn, fighter, AreaTime(conn));
    }

    static AuraCatalog.Aura? ReservationAura(ClientState conn, string effect) =>
        conn.PlayerLife > 0 && !InTown(conn) ? conn.Auras.Values.FirstOrDefault(a => a.EffectId == effect) : null;

    static int ReservationStat(ClientState conn, AuraCatalog.Aura aura, string name) => Stats.Key(name) is uint key
        ? SkillsFor(conn).EffectStats(aura.Effect, aura.Level, aura.Quality).FirstOrDefault(p => p.Item1 == key).Item2 : 0;

    public static void ReservationKill(KillContext kill)
    {
        if (!kill.PlayerKill) return;
        var conn = kill.Conn;
        lock (conn.Monsters)
        {
            double now = AreaTime(conn);
            foreach (var aura in conn.Auras.Values)
                if (conn.PlayerLife > 0 && (aura.EffectId == "HeraldOfPurity"
                    || aura.EffectId == "HeraldOfAsh" && kill.Monster.Overkill > 0
                    || aura.EffectId == "HeraldOfIce" && kill.Monster.Ailments.Shattered))
                    conn.Reservations.Procs.Enqueue(new(aura.SkillId, kill.At,
                        aura.EffectId == "HeraldOfAsh" ? (uint)kill.Monster.Overkill : 0));
            if (ReservationAura(conn, "HeraldOfThunder") is { } thunder && kill.Monster.Ailments.ShockedAtDeath(now))
                conn.Reservations.ThunderUntil = now + ReservationDuration(conn, thunder, "base_skill_effect_duration", 6000);
        }
    }

    static double ReservationDuration(ClientState conn, AuraCatalog.Aura aura, string name, int fallback)
    {
        var skill = SupportedSkill(conn, aura.SkillId);
        var player = CharacterPlayerStats(conn);
        int Get(string n) => GolemStat(skill.Stats, n) + GolemStat(player, n);
        int ms = ReservationStat(conn, aura, name);
        return Math.Max(0, (ms > 0 ? ms : fallback) / 1000.0 * (1 + Get("skill_effect_duration_+%") / 100.0)
            * Math.Max(0, 1 + Get("support_increased_duration_skill_effect_duration_+%_final") / 100.0)
            * Math.Max(0, 1 + Get("support_reduced_duration_skill_effect_duration_+%_final") / 100.0));
    }

    static void ReservationHitLocked(ClientState conn, SpawnedMonster target)
    {
        if (target.Rarity is MonsterRarity.Rare or MonsterRarity.Unique && AffectingAuras(conn).Any(a => a.EffectId == "Zealotry")
            && RandomRolls.Chance(GolemStat(CharacterPlayerStats(conn), "create_consecrated_ground_on_hit_%_vs_rare_or_unique_enemy"), 100, Random.Shared))
        {
            var at = target.Walk.Position(AreaTime(conn));
            conn.FlaskGrounds.Add(new(FlaskGrounds.Kind.ConsecratedGround, at.X, at.Y, 14,
                AreaTime(conn) + 8, QueueGroundArtLocked(conn, FlaskGrounds.Kind.ConsecratedGround, at, 14)));
        }
        if (ReservationAura(conn, "HeraldOfPurity") is { } purity && !target.Dead
            && target.Rarity is MonsterRarity.Rare or MonsterRarity.Unique
            && RandomRolls.Chance(ReservationStat(conn, purity, "herald_of_light_summon_champion_on_unique_or_rare_enemy_hit_%"), 100, Random.Shared))
            conn.Reservations.Procs.Enqueue(new(purity.SkillId, target.Walk.Position(AreaTime(conn))));
    }

    static void ReservationPoisonLocked(ClientState conn, double now)
    {
        if (conn.Reservations.Secondary || ReservationAura(conn, "HeraldOfAgony") is not { } agony) return;
        int maximum = ReservationStat(conn, agony, "maximum_virulence_stacks")
            + GolemStat(CharacterPlayerStats(conn), "maximum_virulence_stacks");
        if (conn.Reservations.Virulence++ == 0) conn.Reservations.NextVirulence = now + .25;
        conn.Reservations.Virulence = Math.Clamp(conn.Reservations.Virulence, 0, Math.Max(0, maximum));
    }

    static void ReservationWhenHitLocked(ClientState conn, uint attacker, SpawnedMonster monster, bool blocked, double now)
    {
        if (ReservationAura(conn, "ArcticArmour") is { } arctic)
            monster.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill,
                ReservationStat(conn, arctic, "arctic_armour_chill_when_hit_duration") / 1000.0, now,
                n => GolemStat(CharacterPlayerStats(conn), n), n => monster.Mods.Effects[n], stated: 30);
        if (blocked && ReservationAura(conn, "TempestShield") is { } tempest)
            conn.Reservations.Procs.Enqueue(new(tempest.SkillId, monster.Walk.Position(now), attacker));
    }

    // Secondary damage never borrows a weapon or spell-only increases, added damage or supports.
    public static DamagePacket ReservationHitPacket(ClientState conn, AuraCatalog.Aura aura)
    {
        if (aura.EffectId == "TempestShield") return SupportedHitFor(conn, aura.SkillId, suppress: false);
        var skill = SupportedSkill(conn, aura.SkillId);
        return ReservationSecondaryProfiles(conn, aura, skill, CharacterPlayerStats(conn))(0, 0).Hit;
    }

    // One proc captures the raw secondary packet and its source modifiers before selecting any target.
    static Func<long, int, ReservationDamageProfile> ReservationSecondaryProfiles(ClientState conn, AuraCatalog.Aura aura,
        SupportedGem skill, IReadOnlyDictionary<uint, int> player)
    {
        var stats = MergeSkillStats(player, skill.Stats);
        int Get(string n) => GolemStat(stats, n);
        var raw = SkillsFor(conn).SecondaryHitDamage(aura.Effect, aura.Level, aura.Quality);
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type.ToString().ToLowerInvariant();
            double effectiveness = AddedEffectiveness(conn, aura.Effect, aura.Level);
            raw = raw.Add(type, new(Get($"global_minimum_added_{slug}_damage") * effectiveness,
                Get($"global_maximum_added_{slug}_damage") * effectiveness));
        }
        var conversion = DamageStats.Conversion(stats, Stats, attack: null);
        var modifiers = skill.Supports.SelectMany(s => s.Stats)
            .Select(s => (Stats.Name(s.Key) ?? "", s.Value)).ToArray();
        var types = Supports.Of(aura.Effect)?.Types.ToHashSet();
        return (increased, webs) =>
        {
            increased += SpiderDamageIncrease(Get, webs);
            var woven = SpiderRawDamage(raw, Get, webs, AddedEffectiveness(conn, aura.Effect, aura.Level));
            var hit = PlayerHitDamage.Resolve(woven, Get, conversion,
                spell: false, area: aura.EffectId != "HeraldOfThunder", secondary: true,
                supportModifiers: modifiers, skillTypes: types, additionalIncreased: increased);
            var rows = AilmentPanelStats.Compute(new("secondary", Spell: false), woven, conversion, Get, increased);
            return new(hit, MonsterIgnites.Settings.From(rows, "secondary", Get), MonsterIgnites.Settings.None,
                MonsterPoisons.Settings.From(rows, "secondary", Get));
        };
    }

    /// <summary>The immutable hit and damaging-ailment outputs selected for one reservation contact.</summary>
    public sealed record ReservationDamageProfile(DamagePacket Hit, MonsterIgnites.Settings Ignite,
        MonsterIgnites.Settings Bleed, MonsterPoisons.Settings Poison);

    static double ReservationRadius(ClientState conn, AuraCatalog.Aura aura, int fallback)
    {
        var skill = SupportedSkill(conn, aura.SkillId);
        var player = CharacterPlayerStats(conn);
        int Get(string n) => GolemStat(skill.Stats, n) + GolemStat(player, n);
        int basis = ReservationStat(conn, aura, "active_skill_base_area_of_effect_radius");
        return SkillArea.Radius(basis > 0 ? basis : fallback, Get, spell: false);
    }

    static void ReservationDamageLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        AuraCatalog.Aura aura, (int X, int Y) at, double now, uint target = 0)
    {
        using var statScope = new PlayerStatScope(conn, fresh: true);
        var skill = SupportedSkill(conn, aura.SkillId);
        if (!skill.RequirementsMet || skill.Effect != aura.Effect
            || SkillsFor(conn).EffectId(skill.Effect) != aura.EffectId) return;
        var player = CharacterPlayerStats(conn);
        long additionalIncreased = Stats.Key(PlayerHitDamage.VsBlindedDamageStat) is uint key
            ? (long)player.GetValueOrDefault(key) + skill.Stats.GetValueOrDefault(key) : 0;
        ReservationDamageProfile normalProfile, blindedProfile;
        Func<long, int, ReservationDamageProfile> targetProfile;
        if (aura.EffectId == "TempestShield")
        {
            if (SupportedHitInputsFor(conn, aura.SkillId, capturedGem: skill) is not { } inputs) return;
            ReservationDamageProfile Profile(long increased) => new(
                SupportedHitFrom(inputs, suppress: true, additionalIncreased: increased),
                SupportedIgnite(conn, aura.SkillId, spell: true, player, additionalIncreased: increased,
                    capturedInputs: inputs),
                SupportedBleed(conn, aura.SkillId, spell: true, player, additionalIncreased: increased,
                    capturedInputs: inputs),
                SupportedPoison(conn, aura.SkillId, spell: true, player, additionalIncreased: increased,
                    capturedInputs: inputs));
            normalProfile = Profile(0);
            blindedProfile = additionalIncreased == 0 ? normalProfile : Profile(additionalIncreased);
            var spider = CaptureSpiderDamage(conn, inputs, AddedEffectiveness(conn, aura.Effect, aura.Level), context: true);
            targetProfile = (increase, webs) =>
            {
                if (spider is null || webs <= 0) return increase == 0 ? normalProfile : blindedProfile;
                var woven = SpiderDamageProfile(spider, webs, increase != 0);
                return new(woven.Hit, woven.Ignite, woven.Bleed, woven.Poison);
            };
        }
        else
        {
            var secondary = ReservationSecondaryProfiles(conn, aura, skill, player);
            normalProfile = secondary(0, 0);
            blindedProfile = additionalIncreased == 0 ? normalProfile : secondary(additionalIncreased, 0);
            targetProfile = secondary;
        }
        double radius = ReservationRadius(conn, aura, aura.EffectId == "TempestShield" ? 60 : 32);
        var candidates = conn.Monsters.Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                && !conn.AsleepMonsters.Contains(p.Key) && Dist(p.Value.Walk.Position(now), at) <= radius)
            .OrderBy(p => target != 0 && p.Key == target ? -1 : Dist(p.Value.Walk.Position(now), at)).ToArray();
        if (aura.EffectId == "HeraldOfThunder") candidates = candidates.Take(1).ToArray();
        else if (aura.EffectId == "TempestShield") candidates = candidates.Take(1 + Math.Max(0, GolemStat(skill.Stats, "number_of_chains"))).ToArray();
        conn.Reservations.Secondary = true;
        try
        {
            foreach (var (id, m) in candidates)
            {
                if (m.Hostile != true || m.Dead || m.Life == 0 || m.Invulnerable || m.Asleep
                    || conn.AsleepMonsters.Contains(id)
                    || !conn.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m)) continue;
                // Select the target profile before any RNG, outgoing effects, or this contact's own Blind.
                bool targetWasBlinded = Blinded(conn, id);
                var profile = m.SpiderWebs.Count(now) is > 0 and var webs
                    ? targetProfile(targetWasBlinded ? additionalIncreased : 0, webs)
                    : targetWasBlinded ? blindedProfile : normalProfile;
                var targetPacket = profile.Hit;
                bool spell = aura.EffectId == "TempestShield";
                int Player(string n) => GolemStat(player, n);
                int Own(string n) => GolemStat(skill.Stats, n);
                List<byte[]>? ignited = null;
                string? igniteLog = null;
                int criticalChance = spell ? SupportedCriticalChance(conn, aura.SkillId)
                    : Player("global_cannot_crit") != 0 || Own("global_cannot_crit") != 0 ? 0
                    : CriticalChanceFinal.Project((long)Player(AddedBaseCriticalStrike.Generic) + Own(AddedBaseCriticalStrike.Generic),
                        (long)Player("critical_strike_chance_+%") + Own("critical_strike_chance_+%"), Player, Own);
                bool critical = RandomRolls.Chance(criticalChance, 10000, Random.Shared);
                int multiplier = spell ? SupportedCriticalMultiplier(conn, aura.SkillId)
                    : 150 + Player("base_critical_strike_multiplier_+") + Own("base_critical_strike_multiplier_+");
                var rolled = targetPacket.Roll(Random.Shared).Scale(critical ? multiplier / 100.0 : 1);
                var mitigated = DamageModel.Mitigate(rolled, type => MonsterHitResistance(m, type, default, now));
                int damage = MonsterTakesMitigated(m, mitigated, spell, now);
                if (ApplyMonsterHitLocked(conn, m, damage) is not { } hit) continue;
                if (hit.StageStarts)
                { CommitBossStage(remote, stream, ciphers, conn, id, m, now); hit = hit with { StageStarts = false }; }
                {
                    var own = new Dictionary<uint, int>(skill.Stats);
                    if (aura.EffectId == "HeraldOfThunder" && Stats.Key("never_shock") is uint noShock) own[noShock] = 1;
                    AilmentsOnHitLocked(conn, id, m, mitigated, spell, critical, 100, player, own, now,
                        Random.Shared, poison: profile.Poison,
                        blind: BlindHitInput.Capture(player, own));
                    if (m.Life > 0 && (critical ? profile.Ignite.CriticalMax : profile.Ignite.Max) > 0
                        && IgniteOnHitLocked(conn, id, m, profile.Ignite, critical, now) is { } packets)
                        (ignited, igniteLog) = (packets, IgnitedLog(id, m, critical, now));
                }
                CommitAcceptedHitLocked(conn, id, m, hit, damage, now, aura.Name, playerKill: true);
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.ActionHit(PlayerId, 0xffff, id,
                    flags: (ushort)(0x4201 | (critical ? 0x20 : 0)), kind: 2, u34: 450), aura.Name + " triggered hit", id, m, WorldId: PlayerId));
                if (ignited is { } packetsToSend)
                    for (int i = 0; i < packetsToSend.Count; i++)
                        conn.Minions.OutputQueue.Add(new(packetsToSend[i],
                            i == packetsToSend.Count - 1 ? igniteLog : null, id, m, WorldId: id));
            }
        }
        finally { conn.Reservations.Secondary = false; }
    }

    static void ReservationAshLocked(ClientState conn, AuraCatalog.Aura aura, (int X, int Y) at, uint overkill, double now)
    {
        var skill = SupportedSkill(conn, aura.SkillId);
        var player = CharacterPlayerStats(conn);
        int Get(string n) => GolemStat(skill.Stats, n) + GolemStat(player, n);
        if (DamageStats.Prohibited(DamageType.Fire, Get)) return;
        // Overkill is already final hit damage. Only burning/DoT (including typed DoT multipliers) scales this burn.
        double dps = overkill * ReservationStat(conn, aura, "herald_of_ash_burning_%_overkill_damage_per_minute") / 6000.0
            * Math.Max(0, 1 + (Get("burn_damage_+%") + Get("damage_over_time_+%")) / 100.0)
            * Math.Max(0, 1 + (Get("dot_multiplier_+") + Get("fire_dot_multiplier_+")) / 100.0)
            * Math.Max(0, 1 + ReservationStat(conn, aura, "herald_of_ash_burning_damage_+%_final") / 100.0)
            * Math.Max(0, 1 + Get("delirium_aura_damage_over_time_+%_final") / 100.0);
        double radius = ReservationRadius(conn, aura, 10);
        foreach (var (id, m) in conn.Monsters)
            if (m is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false } && Dist(m.Walk.Position(now), at) <= radius
                && m.HeraldBurns.Add(now, dps, ReservationDuration(conn, aura, "base_skill_effect_duration", 4000)))
            {
                m.LastHitBy = conn;
                if (m.HeraldBurns.Buff is not null) continue;
                m.HeraldBurns.Buff = conn.NextBuff++;
                conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(id, m.HeraldBurns.Buff.Value, 877, PlayerId), "Herald of Ash burn", id, m, WorldId: id));
            }
    }

    public static async Task ReservationSkillsTick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double now)
    {
        var summons = new List<AuraCatalog.Aura>();
        bool catChanged = false;
        using (ProjectileMutation(conn))
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused) return;
            var state = conn.Reservations;
            catChanged = CatAspectReconcileLocked(conn, now);
            catChanged |= AvianReconcileLocked(conn, now);
            catChanged |= CrabReconcileLocked(conn, now);
            catChanged |= MadnessReconcileLocked(conn, now);
            if (catChanged)
            {
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeed();
            }
            for (int count = Math.Min(256, state.Procs.Count); count > 0; count--)
            {
                var proc = state.Procs.Dequeue();
                if (!conn.Auras.TryGetValue(proc.Skill, out var aura) || conn.PlayerLife == 0) continue;
                if (aura.EffectId == "HeraldOfPurity") summons.Add(aura);
                else if (aura.EffectId == "HeraldOfAsh") ReservationAshLocked(conn, aura, proc.At, proc.Target, now);
                else ReservationDamageLocked(remote, stream, ciphers, conn, aura, proc.At, now, proc.Target);
            }
            if (ReservationAura(conn, "HeraldOfThunder") is { } thunder && now < state.ThunderUntil && now >= state.NextThunder)
            {
                state.NextThunder = now + Math.Max(.05, ReservationStat(conn, thunder, "herald_of_thunder_bolt_base_frequency") / 1000.0);
                ReservationDamageLocked(remote, stream, ciphers, conn, thunder, conn.Walk.Position(now), now);
            }
            if (ReservationAura(conn, "TemporalRift") is not null && now >= state.NextEcho)
            {
                var at = conn.Walk.Position(now);
                state.NextEcho = now + .1;
                state.Echoes.Enqueue(new(now, at.X, at.Y, conn.PlayerLife, conn.PlayerMana, conn.EnergyShield.Current));
                while (state.Echoes.Count > 1 && state.Echoes.ElementAt(1).At <= now - 4) state.Echoes.Dequeue();
            }
            else if (ReservationAura(conn, "TemporalRift") is null) state.Echoes.Clear();
            ReservationArcticTrailLocked(conn, now);
            SpiderAspectTickLocked(conn, now);
            MadnessExplosionsLocked(conn, now);
        }
        if (catChanged) await SendOwnAuraChanges(remote, stream, ciphers, conn);
        await DrainTwisterGains(remote, stream, ciphers, conn);
        await DrainCatCryLosses(remote, stream, ciphers, conn);
        await PresentSiphoningCharges(remote, stream, ciphers, conn);
        foreach (var aura in summons) await SummonReservationFighter(remote, stream, ciphers, conn, aura, "Purity");
        await ReservationFightersTick(remote, stream, ciphers, conn, now);
        await TickStrongestDots(remote, stream, ciphers, conn, m => m.HeraldBurns,
            (m, t) => MonsterDotTaken(m, DamageType.Fire, t), "burnt to death by Herald of Ash");
        await PublishMinionOutcomes(remote, stream, ciphers, conn);
    }

    static void ReservationArcticTrailLocked(ClientState conn, double now)
    {
        var state = conn.Reservations;
        foreach (var expired in state.Trail.Where(t => t.Until <= now).ToArray())
        { state.Trail.Remove(expired); if (expired.Object is uint obj) conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(obj), null, WorldId: obj, RemovesObject: true)); }
        var arctic = ReservationAura(conn, "ArcticArmour");
        bool stationary = arctic is not null && !conn.Walk.Moving(now);
        if (!stationary && state.StationaryBuff != 0)
        {
            conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, state.StationaryBuff), null, WorldId: PlayerId));
            state.StationaryBuff = 0;
        }
        else if (stationary && state.StationaryBuff == 0)
            conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(PlayerId, state.StationaryBuff = conn.NextBuff++, 428, PlayerId), null, WorldId: PlayerId));
        if (arctic is not null && conn.Walk.Moving(now) && now >= state.NextIce)
        {
            state.NextIce = now + .25;
            var at = conn.Walk.Position(now);
            uint obj = conn.NextObjectId++;
            var art = new EffectObject(at.X, at.Y, "Metadata/Effects/Spells/ground_effects_v3/chilled/chilled_grd_arctic_armour.ao")
            { Flags0 = EffectObject.FixedRotation, Scale = (float)(4.0 / 23) };
            conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(obj, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), art.Blob()), null, WorldId: obj, CreatesObject: true));
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(obj), null, WorldId: obj));
            state.Trail.Add(new(at, now + ReservationDuration(conn, arctic, "base_skill_effect_duration", 2500), obj));
        }
        foreach (var m in conn.Monsters.Values)
            if (m is { Hostile: true, Dead: false, Life: > 0 } && state.Trail.Any(t => Dist(t.At, m.Walk.Position(now)) <= 4))
                m.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill, .3, now, _ => 0, n => m.Mods.Effects[n], stated: 10);
    }

    static byte[] RecallTemporalRiftLocked(ClientState conn)
    {
        var history = conn.Reservations.Echoes;
        if (!history.TryPeek(out var old) || conn.PlayerLife == 0 || !conn.Layout.WalkableFor(old.X, old.Y, 0)) return [];
        conn.Walk.Teleport(old.X, old.Y, AreaTime(conn));
        conn.PlayerLife = Math.Min(old.Life, conn.PlayerUnreservedLife);
        conn.PlayerMana = Math.Min(old.Mana, (uint)UnreservedMana(conn));
        conn.EnergyShield.Empty();
        conn.EnergyShield.Recover((uint)Math.Clamp(old.Shield, 0, uint.MaxValue));
        history.Clear();
        return [.. InstanceProtocol.Teleport(PlayerId, old.X, old.Y),
            .. InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife, conn.PlayerReservedLife),
            .. InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMana, (uint)ReservedMana(conn), pool: ManaPool),
            .. InstanceProtocol.LifeUpdate(PlayerId, conn.EnergyShield.Current, pool: 2)];
    }
}
