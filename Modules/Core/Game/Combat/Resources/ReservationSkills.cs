using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    static void EndReservationRuntimeLocked(Player player, AuraCatalog.Aura aura)
    {
        EndCatAspectRuntimeLocked(player, aura);
        EndAvianAspectLocked(player, aura);
        EndCrabAspectLocked(player, aura);
        EndSpiderAspectLocked(player, aura);
        var state = player.Reservations;
        var pending = state.Procs.Where(p => p.Skill != aura.SkillId).ToArray();
        state.Procs.Clear(); foreach (var proc in pending) state.Procs.Enqueue(proc);
        if (aura.EffectId == "HeraldOfThunder") state.ThunderUntil = state.NextThunder = 0;
        if (aura.EffectId == "HeraldOfAgony") state.Virulence = 0;
        if (aura.EffectId == "TemporalRift") state.Echoes.Clear();
        foreach (var fighter in state.Fighters.Values.Where(g => g.Source == aura.SkillId).ToArray())
            RemoveReservationFighterLocked(player, fighter, AreaTime(player));
    }

    static AuraCatalog.Aura? ReservationAura(Player player, string effect) =>
        player.PlayerLife > 0 && !InTown(player) ? player.Auras.Values.FirstOrDefault(a => a.EffectId == effect) : null;

    static int ReservationStat(Player player, AuraCatalog.Aura aura, uint name) => name is var key
        ? SkillsFor(player).EffectStats(aura.Effect, aura.Level, aura.Quality).FirstOrDefault(p => p.Item1 == key).Item2 : 0;

    public static void ReservationKill(KillContext kill)
    {
        if (!kill.PlayerKill) return;
        var player = kill.Player;
        double now = AreaTime(player);
        foreach (var aura in player.Auras.Values)
            if (player.PlayerLife > 0 && (aura.EffectId == "HeraldOfPurity"
                || aura.EffectId == "HeraldOfAsh" && kill.Monster.Overkill > 0
                || aura.EffectId == "HeraldOfIce" && kill.Monster.Ailments.Shattered))
                player.Reservations.Procs.Enqueue(new(aura.SkillId, kill.At,
                    aura.EffectId == "HeraldOfAsh" ? (uint)kill.Monster.Overkill : 0));
        if (ReservationAura(player, "HeraldOfThunder") is { } thunder && kill.Monster.Ailments.ShockedAtDeath(now))
            StartThunderStormLocked(player, thunder, now);
    }

    static void ReservationShockLocked(Player player, double now)
    {
        if (!player.Reservations.Secondary && ReservationAura(player, "HeraldOfThunder") is { } thunder)
            StartThunderStormLocked(player, thunder, now);
    }

    static void StartThunderStormLocked(Player player, AuraCatalog.Aura thunder, double now)
    {
        var state = player.Reservations;
        if (state.ThunderUntil <= now) state.NextThunder = now;
        state.ThunderUntil = now + ReservationDuration(player, thunder, Stat.BaseSkillEffectDuration, 6000);
    }

    static double ReservationDuration(Player player, AuraCatalog.Aura aura, uint name, int fallback)
    {
        var skill = SupportedSkill(player, aura.SkillId);
        var playerStats = CharacterPlayerStats(player);
        int ms = ReservationStat(player, aura, name);
        return SkillDurationModifiers.Seconds(ms > 0 ? ms : fallback, n => GolemStat(playerStats, n), n => GolemStat(skill.Stats, n),
            DurationKinds(player, aura.Effect));
    }

    static void ReservationHitLocked(Player player, SpawnedMonster target)
    {
        if (target.Rarity is MonsterRarity.Rare or MonsterRarity.Unique && AffectingAuras(player).Any(a => a.EffectId == "Zealotry"))
        {
            var playerStats = CharacterPlayerStats(player);
            if (RandomRolls.Chance(GolemStat(playerStats, Stat.CreateConsecratedGroundOnHitPctVsRareOrUniqueEnemy), 100, AreaRng(player)))
            {
                double now = AreaTime(player);
                var at = target.Walk.Position(now);
                player.FlaskGrounds.Add(new(FlaskGrounds.Kind.ConsecratedGround, at.X, at.Y, 14,
                    now + 8, QueueGroundArtLocked(player, FlaskGrounds.Kind.ConsecratedGround, at, 14),
                    OwnConsecratedGroundCreatedLocked(player, playerStats)));
                PresentNearby(player);
            }
        }
        if (ReservationAura(player, "HeraldOfPurity") is { } purity && !target.Dead
            && target.Rarity is MonsterRarity.Rare or MonsterRarity.Unique
            && RandomRolls.Chance(ReservationStat(player, purity, Stat.HeraldOfLightSummonChampionOnUniqueOrRareEnemyHitPct), 100, AreaRng(player)))
            player.Reservations.Procs.Enqueue(new(purity.SkillId, target.Walk.Position(AreaTime(player))));
    }

    static void ReservationPoisonLocked(Player player, double now)
    {
        if (player.Reservations.Secondary || ReservationAura(player, "HeraldOfAgony") is not { } agony) return;
        int maximum = ReservationStat(player, agony, Stat.MaximumVirulenceStacks)
            + GolemStat(CharacterPlayerStats(player), Stat.MaximumVirulenceStacks);
        if (player.Reservations.Virulence++ == 0) player.Reservations.NextVirulence = now + .25;
        player.Reservations.Virulence = Math.Clamp(player.Reservations.Virulence, 0, Math.Max(0, maximum));
    }

    static void ReservationWhenHitLocked(Player player, uint attacker, SpawnedMonster monster, bool blocked, double now)
    {
        if (ReservationAura(player, "ArcticArmour") is { } arctic)
            InflictedLocked(player, monster, monster.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill,
                ReservationStat(player, arctic, Stat.ArcticArmourChillWhenHitDuration) / 1000.0, now,
                n => GolemStat(CharacterPlayerStats(player), n), n => monster.Mods.Effects[n], stated: 30));
        if (blocked && ReservationAura(player, "TempestShield") is { } tempest)
            player.Reservations.Procs.Enqueue(new(tempest.SkillId, monster.Walk.Position(now), attacker));
    }

    // Secondary damage never borrows a weapon or spell-only increases, added damage or supports.
    public static DamagePacket ReservationHitPacket(Player player, AuraCatalog.Aura aura)
    {
        if (aura.EffectId == "TempestShield") return SupportedHitFor(player, aura.SkillId, suppress: false);
        var skill = SupportedSkill(player, aura.SkillId);
        return ReservationSecondaryProfiles(player, aura, skill, CharacterPlayerStats(player))(0, 0, false, false, false, default).Hit;
    }

    // One proc captures the raw secondary packet and its source modifiers before selecting any target.
    static Func<long, int, bool, bool, bool, TargetContact, ReservationDamageProfile> ReservationSecondaryProfiles(Player player, AuraCatalog.Aura aura,
        SupportedGem skill, IReadOnlyDictionary<uint, int> playerStats)
    {
        var stats = MergeSkillStats(playerStats, skill.Stats);
        int Get(uint n) => GolemStat(stats, n);
        var raw = SkillsFor(player).SecondaryHitDamage(aura.Effect, aura.Level, aura.Quality);
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type.ToString().ToLowerInvariant();
            double effectiveness = AddedEffectiveness(player, aura.Effect, aura.Level);
            raw = raw.Add(type, new(Get(Stat.Of($"global_minimum_added_{slug}_damage")) * effectiveness,
                Get(Stat.Of($"global_maximum_added_{slug}_damage")) * effectiveness));
        }
        var conversion = DamageStats.Conversion(stats, Stats, attack: null);
        var modifiers = skill.Supports.SelectMany(s => s.Stats)
            .Select(s => (s.Key, s.Value)).ToArray();
        var types = Supports.Of(aura.Effect)?.Types.ToHashSet();
        int herald = aura.IsHerald ? Get(Stat.DamagePlusPctWithHeraldSkills) : 0;
        return (increased, webs, ignited, bleeding, lowLife, contact) =>
        {
            int TargetStat(uint name) => TypedTargetDamageStat(Get, name, ignited, bleeding, lowLife, attack: false);
            int HitStat(uint name) => TargetHitStat(name, TargetStat(name), contact);
            var target = TargetModifiers(Get, lowLife, contact: contact);
            increased += herald + SpiderDamageIncrease(Get, webs);
            var woven = SpiderRawDamage(raw, Get, webs, AddedEffectiveness(player, aura.Effect, aura.Level));
            var hit = PlayerHitDamage.Resolve(woven, HitStat, conversion,
                spell: false, area: aura.EffectId != "HeraldOfThunder", secondary: true,
                supportModifiers: modifiers, skillTypes: types, additionalIncreased: increased + target.HitIncrease);
            var rows = AilmentPanelStats.Compute(new("secondary", Spell: false), woven, conversion, TargetStat,
                increased + target.AilmentIncrease);
            var ignite = MonsterIgnites.Settings.From(rows, "secondary", TargetStat);
            var poison = MonsterPoisons.Settings.From(rows, "secondary", TargetStat);
            if (target.HitMore != 1) hit = hit.Scale(target.HitMore);
            return target.AilmentMore == 1 ? new(hit, ignite, MonsterIgnites.Settings.None, poison)
                : new(hit, RepeatAilment(ignite, target.AilmentMore), MonsterIgnites.Settings.None,
                    RepeatAilment(poison, target.AilmentMore));
        };
    }

    /// <summary>The immutable hit and damaging-ailment outputs selected for one reservation contact.</summary>
    public sealed record ReservationDamageProfile(DamagePacket Hit, MonsterIgnites.Settings Ignite,
        MonsterIgnites.Settings Bleed, MonsterPoisons.Settings Poison);

    // skill and playerStats: the caller's own (a Herald proc runs per kill; the radius builds no stats of its own).
    static double ReservationRadius(Player player, AuraCatalog.Aura aura, int fallback, SupportedGem skill,
        IReadOnlyDictionary<uint, int> playerStats)
    {
        int basis = ReservationStat(player, aura, Stat.ActiveSkillBaseAreaOfEffectRadius);
        return SkillRadius(player, basis > 0 ? basis : fallback, skill.Stats, spell: false, aura.Effect, playerStats);
    }

    internal static void ReservationDamageLocked(Player player,
        AuraCatalog.Aura aura, (int X, int Y) at, double now, uint target = 0)
    {
        using var statScope = new PlayerStatScope(player, fresh: true);
        var skill = SupportedSkill(player, aura.SkillId);
        if (!skill.RequirementsMet || skill.Effect != aura.Effect
            || SkillsFor(player).EffectId(skill.Effect) != aura.EffectId) return;
        var playerStats = CharacterPlayerStats(player);
        long additionalIncreased = PlayerHitDamage.VsBlindedDamageStat is var key
            ? (long)playerStats.GetValueOrDefault(key) + skill.Stats.GetValueOrDefault(key) : 0;
        ReservationDamageProfile normalProfile, blindedProfile;
        Func<long, int, bool, bool, bool, TargetContact, ReservationDamageProfile> targetProfile;
        bool contactDamage = HasContactDamage(playerStats.GetValueOrDefault);
        if (aura.EffectId == "TempestShield")
        {
            if (SupportedHitInputsFor(player, aura.SkillId, capturedGem: skill) is not { } inputs) return;
            ReservationDamageProfile Profile(long increased) => new(
                SupportedHitFrom(inputs, suppress: true, additionalIncreased: increased),
                SupportedIgnite(player, aura.SkillId, spell: true, playerStats, additionalIncreased: increased,
                    capturedInputs: inputs),
                SupportedBleed(player, aura.SkillId, spell: true, playerStats, additionalIncreased: increased,
                    capturedInputs: inputs),
                SupportedPoison(player, aura.SkillId, spell: true, playerStats, additionalIncreased: increased,
                    capturedInputs: inputs));
            normalProfile = Profile(0);
            blindedProfile = additionalIncreased == 0 ? normalProfile : Profile(additionalIncreased);
            var spider = CaptureSpiderDamage(player, inputs, AddedEffectiveness(player, aura.Effect, aura.Level), context: true);
            targetProfile = (increase, webs, ignited, bleeding, lowLife, contact) =>
            {
                if (spider is null) return increase == 0 ? normalProfile : blindedProfile;
                var woven = SpiderDamageProfile(spider, webs, increase != 0, ignited, bleeding, lowLife,
                    maimed: false, contact: contact);
                return new(woven.Hit, woven.Ignite, woven.Bleed, woven.Poison);
            };
        }
        else
        {
            var secondary = ReservationSecondaryProfiles(player, aura, skill, playerStats);
            normalProfile = secondary(0, 0, false, false, false, default);
            blindedProfile = additionalIncreased == 0 ? normalProfile : secondary(additionalIncreased, 0, false, false, false, default);
            targetProfile = secondary;
        }
        double radius = ReservationRadius(player, aura, aura.EffectId == "TempestShield" ? 60 : 32, skill, playerStats);
        var candidates = player.Area.MonstersNear(at, radius).Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                && !MonsterAsleep(player, p.Key) && Dist(p.Value.Walk.Position(now), at) <= radius)
            .OrderBy(p => target != 0 && p.Key == target ? -1 : Dist(p.Value.Walk.Position(now), at)).ToArray();
        if (aura.EffectId == "HeraldOfThunder") candidates = candidates.Take(1).ToArray();
        else if (aura.EffectId == "TempestShield") candidates = candidates.Take(1 + Math.Max(0, GolemStat(skill.Stats, Stat.NumberOfChains))).ToArray();
        player.Reservations.Secondary = true;
        try
        {
            foreach (var (id, m) in candidates)
            {
                if (m.Hostile != true || m.Dead || m.Life == 0 || m.Invulnerable || m.Asleep
                    || MonsterAsleep(player, id)
                    || !player.Area.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m)) continue;
                // Select the target profile before any RNG, outgoing effects, or this contact's own Blind.
                bool targetWasBlinded = Blinded(player, id, now);
                var profile = targetProfile(targetWasBlinded ? additionalIncreased : 0, m.SpiderWebs.Count(now),
                    m.Ignites.IgnitedAt(now), m.Bleeds.IgnitedAt(now), EnemyOnLowLife(m), contactDamage ? ContactDamage(player, m, now, playerStats) : default);
                var targetPacket = profile.Hit;
                bool spell = aura.EffectId == "TempestShield";
                int Player(uint n) => GolemStat(playerStats, n);
                int Own(uint n) => GolemStat(skill.Stats, n);
                List<byte[]>? ignited = null;
                string? igniteLog = null;
                int criticalChance = spell ? SupportedCriticalChance(player, aura.SkillId)
                    : Player(Stat.GlobalCannotCrit) != 0 || Own(Stat.GlobalCannotCrit) != 0 ? 0
                    : CriticalChanceFinal.Project((long)Player(AddedBaseCriticalStrike.Generic) + Own(AddedBaseCriticalStrike.Generic),
                        (long)Player(Stat.CriticalStrikeChancePlusPct) + Own(Stat.CriticalStrikeChancePlusPct), Player, Own);
                bool critical = RandomRolls.Chance(criticalChance + BrittleCriticalChance(m, now), 10000, AreaRng(player));
                int multiplier = spell ? SupportedCriticalMultiplier(player, aura.SkillId)
                    : CriticalHitMultiplier(150L + Player(Stat.BaseCriticalStrikeMultiplierPlus)
                        + Own(Stat.BaseCriticalStrikeMultiplierPlus), playerStats, skill.Stats);
                var rolled = targetPacket.Roll(AreaRng(player)).Scale(critical ? multiplier / 100.0 : 1);
                var mitigated = MitigateMonsterHit(m, rolled, now, cannotPenetrateOrIgnore:
                    Math.Max(Player(Stat.CannotPenetrateOrIgnoreElementalResistances), Own(Stat.CannotPenetrateOrIgnoreElementalResistances)));
                int damage = MonsterTakesMitigated(m, mitigated, spell, now, random: AreaRng(player));
                if (ApplyTypedMonsterHitLocked(player, m, damage, MonsterHitChaosDamage(m, mitigated, spell, now, damage)) is not { } hit) continue;
                if (hit.StageStarts)
                { CommitBossStage(player, id, m, now); hit = hit with { StageStarts = false }; }
                {
                    var own = new Dictionary<uint, int>(skill.Stats);
                    if (aura.EffectId == "HeraldOfThunder" && Stat.NeverShock is var noShock) own[noShock] = 1;
                    AilmentsOnHitLocked(player, id, m, mitigated, spell, critical, 100, playerStats, own, now,
                        AreaRng(player), poison: profile.Poison,
                        blind: BlindHitInput.Capture(playerStats, own));
                    if (m.Life > 0 && (critical ? profile.Ignite.CriticalMax : profile.Ignite.Max) > 0
                        && IgniteOnHitLocked(player, id, m, profile.Ignite, critical, now) is { } packets)
                        (ignited, igniteLog) = (packets, IgnitedLog(id, m, critical, now));
                }
                var to = m.Walk.Position(now);
                // Immediate kind 3 before the life update reaches the client's result consumer; 0xffff has no consuming action.
                player.Minions.OutputQueue.Add(new(InstanceProtocol.ActionHit(player.Id, 0xffff, id,
                    flags: MonsterHitFlags(blocked: false, critical), kind: 0x03, code: HitCode(mitigated),
                    angle: HitAngle(to.X - at.X, to.Y - at.Y)), aura.Name + " triggered hit", id, m, WorldId: player.Id));
                CommitAcceptedHitLocked(player, id, m, hit, damage, now, aura.Name, playerKill: true);
                if (ignited is { } packetsToSend)
                    for (int i = 0; i < packetsToSend.Count; i++)
                        player.Minions.OutputQueue.Add(new(packetsToSend[i],
                            i == packetsToSend.Count - 1 ? igniteLog : null, id, m, WorldId: id));
            }
        }
        finally { player.Reservations.Secondary = false; }
    }

    public static void ReservationAshLocked(Player player, AuraCatalog.Aura aura, (int X, int Y) at, uint overkill, double now)
    {
        var skill = SupportedSkill(player, aura.SkillId);
        var playerStats = CharacterPlayerStats(player);
        int Get(uint n) => GolemStat(skill.Stats, n) + GolemStat(playerStats, n);
        if (DamageStats.Prohibited(DamageType.Fire, Get)) return;
        // Overkill is already final hit damage. Only burning/DoT (including typed DoT multipliers) scales this burn.
        double dps = overkill * ReservationStat(player, aura, Stat.HeraldOfAshBurningPctOverkillDamagePerMinute) / 6000.0
            * Math.Max(0, 1 + (Get(Stat.BurnDamagePlusPct) + Get(Stat.DamageOverTimePlusPct)
                + Get(Stat.DamagePlusPctWithHeraldSkills)) / 100.0)
            * Math.Max(0, 1 + (Get(Stat.DotMultiplierPlus) + Get(Stat.FireDotMultiplierPlus)) / 100.0)
            * Math.Max(0, 1 + ReservationStat(player, aura, Stat.HeraldOfAshBurningDamagePlusPctFinal) / 100.0)
            * DamageStats.ElementalMore(Get(Stat.CombinedElementalDamagePlusPctFinal))
            * Math.Max(0, 1 + Get(Stat.DeliriumAuraDamageOverTimePlusPctFinal) / 100.0);
        double radius = ReservationRadius(player, aura, 10, skill, playerStats);
        double burnSeconds = ReservationDuration(player, aura, Stat.BaseSkillEffectDuration, 4000);
        foreach (var (id, m) in player.Area.Monsters)
            if (m is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false } && Dist(m.Walk.Position(now), at) <= radius
                && m.HeraldBurns.Add(now, dps, burnSeconds))
            {
                player.Area.ActiveHeraldBurnMonsters.Add(id);
                m.LastHitBy = player;
                if (m.HeraldBurns.Buff is not null) continue;
                m.HeraldBurns.Buff = 1;
                ushort? burnVisual = aura.ArtVariation != 0 ? Art.BuffVisual("herald_of_ash_burning_buff", aura.ArtVariation) : null;
                player.Area.Buffs.Set(id, new(877, player.Id, Visual: burnVisual));
            }
    }

    public static async Task ReservationSkillsTick(Player player, double now)
    {
        var summons = new List<AuraCatalog.Aura>();
        bool catChanged = false;
        using (ProjectileMutation(player))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return;
            var state = player.Reservations;
            catChanged = CatAspectReconcileLocked(player, now);
            catChanged |= AvianReconcileLocked(player, now);
            catChanged |= CrabReconcileLocked(player, now);
            catChanged |= MadnessReconcileLocked(player, now);
            if (catChanged)
            {
                player.Pools = PoolsFor(player, player.Pools.Level);
                player.ApplyMovementSpeed();
            }
            for (int count = Math.Min(256, state.Procs.Count); count > 0; count--)
            {
                var proc = state.Procs.Dequeue();
                if (!player.Auras.TryGetValue(proc.Skill, out var aura) || player.PlayerLife == 0) continue;
                if (aura.EffectId == "HeraldOfPurity") summons.Add(aura);
                else if (aura.EffectId == "HeraldOfAsh") ReservationAshLocked(player, aura, proc.At, proc.Target, now);
                else ReservationDamageLocked(player, aura, proc.At, now, proc.Target);
            }
            if (ReservationAura(player, "HeraldOfThunder") is { } thunder && now < state.ThunderUntil && now >= state.NextThunder)
            {
                var stats = CharacterPlayerStats(player);
                double frequency = 1 + ((long)ReservationStat(player, thunder, Stat.HeraldOfThunderBoltFrequencyPlusPct)
                    + stats.GetValueOrDefault(Stat.HeraldOfThunderBoltFrequencyPlusPct)) / 100.0;
                double interval = ReservationStat(player, thunder, Stat.HeraldOfThunderBoltBaseFrequency) / 1000.0;
                state.NextThunder = now + Math.Max(.05, interval / Math.Max(.01, frequency));
                ReservationDamageLocked(player, thunder, player.Walk.Position(now), now);
            }
            if (ReservationAura(player, "TemporalRift") is not null && now >= state.NextEcho)
            {
                var at = player.Walk.Position(now);
                state.NextEcho = now + .1;
                state.Echoes.Enqueue(new(now, at.X, at.Y, player.PlayerLife, player.PlayerMana, player.EnergyShield.Current));
                while (state.Echoes.Count > 1 && state.Echoes.ElementAt(1).At <= now - 4) state.Echoes.Dequeue();
            }
            else if (ReservationAura(player, "TemporalRift") is null) state.Echoes.Clear();
            ReservationArcticTrailLocked(player, now);
            SpiderAspectTickLocked(player, now);
            MadnessExplosionsLocked(player, now);
        }
        if (catChanged) await SendOwnAuraChanges(player);
        await TickCooldownTriggers(player, now);
        TickBanner(player, now);
        await DrainTwisterGains(player);
        await DrainCatCryLosses(player);
        await PresentSiphoningCharges(player);
        foreach (var aura in summons) await SummonReservationFighter(player, aura, "Purity");
        await ReservationFightersTick(player, now);
        await TickStrongestDots(player, player.Area.ActiveHeraldBurnMonsters, m => m.HeraldBurns,
            (m, t) => MonsterDotTaken(m, DamageType.Fire, t), "burnt to death by Herald of Ash");
        await PublishMinionOutcomes(player);
    }

    static void ReservationArcticTrailLocked(Player player, double now)
    {
        var state = player.Reservations;
        state.Trail.RemoveAll(patch => patch.Epoch is int epoch && epoch != player.ProjectileEpoch);
        foreach (var expired in state.Trail.Where(t => t.Until <= now).ToArray())
        { state.Trail.Remove(expired); if (expired.Object is uint obj) player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(obj), null, WorldId: obj, RemovesObject: true)); }
        var arctic = ReservationAura(player, "ArcticArmour");
        bool stationary = arctic is not null && !player.Walk.Moving(now);
        if (!stationary && state.StationaryBuff != 0)
        {
            player.Area.Buffs.Clear(player.Id, 428, player.Id);
            state.StationaryBuff = 0;
        }
        else if (stationary && state.StationaryBuff == 0)
        {
            state.StationaryBuff = 1;
            player.Area.Buffs.Set(player.Id, new(428, player.Id));
        }
        if (arctic is not null && player.Walk.Moving(now) && now >= state.NextIce)
        {
            state.NextIce = now + .25;
            var at = player.Walk.Position(now);
            AddChilledGroundLocked(player, at, now + ReservationDuration(player, arctic, Stat.BaseSkillEffectDuration, 2500),
                4, "Metadata/Effects/Spells/ground_effects_v3/chilled/chilled_grd_arctic_armour.ao");
        }
        if (state.Trail.Count == 0) return;
        foreach (var m in player.Area.Monsters.Values)
            if (m is { Hostile: true, Dead: false, Life: > 0 } && state.Trail.Any(t => Dist(t.At, m.Walk.Position(now)) <= t.Radius))
                InflictedLocked(player, m, m.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill, .3, now, _ => 0, n => m.Mods.Effects[n], stated: 10));
    }

    static void AddChilledGroundLocked(Player player, (int X, int Y) at, double until, int radius, string artPath)
    {
        if (radius <= 0) return;
        uint obj = player.TakeObjectId();
        var art = new EffectObject(at.X, at.Y, artPath)
        { Flags0 = EffectObject.FixedRotation, Scale = radius / 23f };
        player.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(obj, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
            art.Blob()), null, WorldId: obj, CreatesObject: true));
        player.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(obj), null, WorldId: obj));
        player.Reservations.Trail.Add(new(at, until, obj, radius, player.ProjectileEpoch));
    }

    static byte[] RecallTemporalRiftLocked(Player player)
    {
        var history = player.Reservations.Echoes;
        if (!history.TryPeek(out var old) || player.PlayerLife == 0 || !player.Layout.WalkableFor(old.X, old.Y, 0)) return [];
        player.Walk.Teleport(old.X, old.Y, AreaTime(player));
        player.PlayerLife = Math.Min(old.Life, player.PlayerUnreservedLife);
        player.PlayerMana = Math.Min(old.Mana, (uint)UnreservedMana(player));
        player.EnergyShield.Empty();
        player.EnergyShield.Recover((uint)Math.Clamp(old.Shield, 0, uint.MaxValue));
        history.Clear();
        return InstanceProtocol.Teleport(player.Id, old.X, old.Y);
    }
}
