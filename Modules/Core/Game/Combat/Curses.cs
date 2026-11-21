using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    static readonly HashSet<uint> CurseRecipientStats = [
        Stat.BaseResistAllElementsPct, Stat.BaseFireDamageResistancePct, Stat.BaseColdDamageResistancePct,
        Stat.BaseLightningDamageResistancePct, Stat.BaseChaosDamageResistancePct, Stat.AccuracyRatingPlusPct,
        Stat.EnfeebleDamagePlusPctFinal, Stat.EnfeebleDamagePlusPctVsRareOrUniqueFinal, Stat.PhysicalDamageTakenPlusPct,
        Stat.DamageTakenPlusPctOnLowLife, Stat.DebilitateSelfForXMillisecondsOnHit,
        Stat.TemporalChainsActionSpeedPlusPctFinal, Stat.TemporalChainsActionSpeedPlusPctVsRareOrUniqueFinal,
        Stat.BuffTimePassedPlusPctOtherThanTemporalChains, Stat.ChanceToBeIgnitedPct, Stat.ChanceToBeFrozenPct, Stat.ChanceToBeShockedPct,
        Stat.ReceiveBleedingChancePctWhenHitByAttack, Stat.EnemyAggravateBleedingOnAttackHitChancePctAgainstSelf,
        Stat.EnemyAdditionalCriticalStrikeChanceAgainstSelf, Stat.EnemyAdditionalCriticalStrikeMultiplierAgainstSelf,
        Stat.LifeGrantedWhenKilled, Stat.ManaGrantedWhenKilled, Stat.ChanceToGrantPowerChargeOnDeathPct,
        Stat.ChanceToGrantFrenzyChargeOnDeathPct, Stat.ChanceToGrantEnduranceChargeOnDeathPct,
        Stat.GrantAttackerPowerChargeWhenHitPctChance, Stat.GrantAttackerFrenzyChargeWhenHitPctChance, Stat.GrantAttackerEnduranceChargeWhenHitPctChance,
        Stat.ProjectilesHittingSelfSplitIntoX, Stat.ProjectileDamageTakenPlusPct, Stat.BaseAdditionalPhysicalDamageReductionPct,
        Stat.MinimumAddedPhysicalDamageTaken, Stat.MaximumAddedPhysicalDamageTaken, Stat.LifeGrantedWhenHitByAttacks, Stat.ManaGrantedWhenHitByAttacks,
        Stat.EnemyGrantXRageOnStun, Stat.EnemyChanceToDoubleStunDurationPctVsSelf,
        Stat.LifeLeechOnAnyDamageWhenHitByAttackPermyriad, Stat.ManaLeechOnAnyDamageWhenHitByAttackPermyriad,
        Stat.AlchemistsMarkGrantAttackerXFlaskChargesWhenHitOncePer3s,
        Stat.AlchemistsMarkIgniterCreatesBurningGroundPctIgniteDamage, Stat.AlchemistsMarkPoisonerCreatesCausticGroundPctPoisonDamage
    ];

    static bool ExternalTemporalChains(Player player, string effect) => effect == "TemporalChains"
        && GameEvents.GrantedSkillExecutable.For(player).Any(accept => accept(player, effect));

    static void CurseCharges(Player player, SpawnedMonster target, bool death)
    {
        foreach (var (kind, name) in new[] { (ChargeType.Power, "power"), (ChargeType.Frenzy, "frenzy"), (ChargeType.Endurance, "endurance") })
            if (RandomRolls.Chance(target.Curses[Stat.Of(death ? $"chance_to_grant_{name}_charge_on_death_%" : $"grant_attacker_{name}_charge_when_hit_%_chance")], 100, AreaRng(player)))
                ChargesOf(player).Enqueue(new ChargeEvent.Grant(kind, 1, "curse"));
    }

    static int CurseLimit(Player player, IReadOnlyDictionary<uint, int> playerStats)
    {
        int Get(uint n) => GolemStat(playerStats, n);
        return Get(Stat.HasCurseLimitEqualToMaximumPowerCharges) != 0 ? Get(Stat.MaxPowerCharges)
            : Math.Max(0, 1 + Get(Stat.NumberOfAdditionalCursesAllowed));
    }

    public static int CurseRadius(Player player, ushort skill, bool aura, SupportedGem? captured = null, IReadOnlyDictionary<uint, int>? sources = null)
    {
        var own = (captured ?? SupportedSkill(player, skill)).Stats; var playerStats = sources ?? CharacterPlayerStats(player);
        int Get(uint n) => GolemStat(playerStats, n) + GolemStat(own, n);
        // Hexes retain their own base radius when Blasphemy converts them to auras.
        return SkillArea.Radius(22,
            n => Get(n) + (n == SkillArea.BaseIncreased ? Get(Stat.CurseAreaOfEffectPlusPct) : 0), spell: true);
    }

    public static bool ApplyCurseLocked(Player player, SpawnedMonster monster, ushort skill, double now, bool aura,
        SupportedGem? captured = null, IReadOnlyDictionary<uint, int>? sources = null)
    {
        var gem = captured ?? SupportedSkill(player, skill); string effect = SkillsFor(player).EffectId(gem.Effect) ?? "";
        if (!CurseCatalog.All.TryGetValue(effect, out var definition) || ExternalTemporalChains(player, effect)
            || monster.Dead || monster.Life == 0 || !monster.Hostile || !gem.RequirementsMet) return false;
        var playerStats = sources ?? CharacterPlayerStats(player);
        int Own(uint n) => GolemStat(gem.Stats, n);
        int Get(uint n) => GolemStat(playerStats, n) + Own(n);
        if (monster.Mods.Effects[Stat.ImmuneToCurses] != 0 || !definition.Mark && monster.Mods.Effects[Stat.Hexproof] != 0
            && Get(Stat.IgnoreHexproof) == 0 && Get(Stat.Of("hexes_ignore_hexproof")) == 0
            && !RandomRolls.Chance(Get(Stat.ChanceToIgnoreHexproofPct), 100, AreaRng(player))) return false;
        double factor = Math.Max(0, 1 + (Get(Stat.CurseEffectPlusPct) + Get(Stat.Of(definition.Mark ? "mark_effect_+%" : "hex_effect_+%"))
            + (definition.Mark ? Get(Stat.MarkSkillsCurseEffectPlusPct) : 0)
            + Get(Stat.Of(ReservationNames.GetValueOrDefault(effect, effect.ToLowerInvariant()) + "_curse_effect_+%"))
            + monster.Mods.Effects[Stat.CurseEffectOnSelfPlusPct]) / 100.0)
            * Math.Max(0, 1 + Own(Stat.SupportBlasphemyCurseEffectPlusPctFinal) / 100.0)
            * Math.Max(0, 1 + Own(Stat.SupportHextouchCurseEffectPlusPctFinal) / 100.0);
        var stats = gem.Stats.Where(p => CurseRecipientStats.Contains(p.Key))
            .ToDictionary(p => p.Key, p => (int)Math.Truncate(p.Value * factor));
        double seconds = definition.Mark ? double.PositiveInfinity : SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration),
            n => GolemStat(playerStats, n) + (n == SkillDurationModifiers.IncreasedDuration ? Get(Stat.CurseDurationPlusPct) + Get(Stat.CurseSkillEffectDurationPlusPct) : 0),
            n => Own(n) + (n == SkillDurationModifiers.IncreasedDuration ? Own(Stat.BaseCurseDurationPlusPct) : 0));
        double rate = RecipientEffectTime.Rate(5, n => monster.Mods.Effects[n], temporalChains: effect == "TemporalChains");
        seconds *= Math.Max(0, 1 + Own(Stat.HextouchSupportCurseDurationPlusPctFinal) / 100d);
        if (aura && monster.Curses.All.GetValueOrDefault((player.Id, effect)) is { Aura: true } existing
            && existing.Skill == skill && existing.Stats.Count == stats.Count && existing.Stats.All(p => stats.GetValueOrDefault(p.Key) == p.Value)) return true;
        if (definition.Mark)
            foreach (var m in player.Area.Monsters.Values.Where(m => !ReferenceEquals(m, monster)))
                foreach (var mark in m.Curses.All.Values.Where(c => c.Caster == player.Id && c.Mark).ToArray()) m.Curses.Remove(mark.Caster, mark.Effect);
        monster.Curses.Put(new(player.Id, effect, skill, definition.Buff, player.NextBuff++, now,
            aura ? double.PositiveInfinity : now + seconds / rate, aura, definition.Mark, stats), CurseLimit(player, playerStats));
        if (!aura) monster.PresentationChanged?.Invoke();
        return true;
    }

    static void CursesOnHitLocked(Player player, SpawnedMonster target, ushort sourceSkill, bool spell, double now)
    {
        if (target.Dead || target.Life == 0 || player.Reservations.Secondary) return;
        var source = sourceSkill != 0 ? SupportedSkill(player, sourceSkill) : null;
        bool hextouch = source is not null && GolemStat(source.Stats, Stat.ApplyLinkedCursesOnHitPct) is var chance
            && RandomRolls.Chance(chance, 100, AreaRng(player));
        if (!hextouch && (spell || target.Rarity is not (MonsterRarity.Rare or MonsterRarity.Unique))) return;
        var playerStats = CharacterPlayerStats(player);
        foreach (var skill in GemSkillEffects(player))
        {
            string effect = SkillsFor(player).EffectId(skill.Effect) ?? "";
            if (!CurseCatalog.All.TryGetValue(effect, out var curse)) continue;
            var gem = SupportedSkill(player, skill.Id);
            if (!gem.RequirementsMet) continue;
            if (!curse.Mark && hextouch && source!.Host != 0 && gem.Host == source.Host && gem.Group == source.Group
                && GolemStat(gem.Stats, Stat.CurseTriggeredByHextouch) != 0)
                ApplyCurseLocked(player, target, skill.Id, now, false, gem, playerStats);
            if (curse.Mark && !spell && target.Rarity is MonsterRarity.Rare or MonsterRarity.Unique
                && GolemStat(gem.Stats, Stat.TriggerOnAttackHitAgainstRareOrUnique) != 0
                && now >= player.SkillReadyAt.GetValueOrDefault(gem.Effect))
            {
                if (!PayMana(player, skill.Id, out _, out _)) continue;
                player.SkillReadyAt[gem.Effect] = now + 4;
                ApplyCurseLocked(player, target, skill.Id, now, false, gem, playerStats);
            }
        }
    }

    static async Task<bool> CastCurse(Player player,
        ushort skill, (int X, int Y) aim, uint target, int recovery, Func<bool> current)
    {
        var gem = SupportedSkill(player, skill); string effect = SkillsFor(player).EffectId(gem.Effect)!;
        PlayerSkillSource? source;
        source = CurrentPlayerSkillSourceLocked(player, skill);
        if (source is not { Active: true }) return false;
        double release = AreaTime(player) + (CastMs(player, gem.Effect, skill) ?? 1000) / 1000d;
        var lifetime = CombatLifetime;
        while (true)
        {
            if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || player.Closed || player.LeavingArea
                || player.PlayerLife == 0 || recovery != player.ActionInterruptionGeneration) return false;
            if (!player.Paused && AreaTime(player) >= release)
            {
                double now = AreaTime(player); var playerStats = CharacterPlayerStats(player);
                var currentSource = CurrentPlayerSkillSourceLocked(player, skill);
                var currentGem = SupportedSkill(player, skill);
                if (currentSource is not { Active: true } || currentSource.Identity != source.Identity
                    || currentGem.Effect != gem.Effect || !currentGem.RequirementsMet
                    || GemSkillInstanceDisabled(player, currentGem.Effect, skill) is not null) return false;
                RecordCompletedCurseCast(player, effect, AreaTime(player));
                int radius = CurseRadius(player, skill, false, currentGem, playerStats);
                foreach (var (id, m) in player.Area.Monsters)
                    if (CurseCatalog.All[effect].Mark ? id == target : Dist(m.Walk.Position(now), aim) <= radius)
                        ApplyCurseLocked(player, m, skill, now, false, currentGem, playerStats);
                break;
            }
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        await EnemyDebuffsTick(player, AreaTime(player));
        return true;
    }

    static void CursesTickLocked(Player player, double now, List<byte> bytes)
    {
        var active = player.Auras.Values.Where(a => CurseCatalog.Hex(a.EffectId) && !ExternalTemporalChains(player, a.EffectId)).ToArray();
        var playerStats = active.Length > 0 ? CharacterPlayerStats(player) : null;
        var auras = active.TakeLast(playerStats is null ? 0 : CurseLimit(player, playerStats))
            .Select(a => (Aura: a, Gem: SupportedSkill(player, a.SkillId)))
            .Select(a => (a.Aura, a.Gem, Radius: CurseRadius(player, a.Aura.SkillId, true, a.Gem, playerStats))).ToArray();
        var here = player.Walk.Position(now);
        foreach (var (id, m) in player.Area.Monsters)
        {
            foreach (var curse in m.Curses.All.Values.Where(c => c.Mark && (m.Dead || m.Life == 0 || c.Caster == player.Id && player.PlayerLife == 0)).ToArray())
                m.Curses.Remove(curse.Caster, curse.Effect);
            foreach (var curse in m.Curses.All.Values.Where(c => c.Caster == player.Id && c.Aura).ToArray())
                if (player.PlayerLife == 0 || m.Dead || !auras.Any(a => a.Aura.SkillId == curse.Skill && Dist(m.Walk.Position(now), here) <= a.Radius))
                    m.Curses.Remove(curse.Caster, curse.Effect);
            if (player.PlayerLife > 0 && !InTown(player))
                foreach (var aura in auras)
                    if (Dist(m.Walk.Position(now), here) <= aura.Radius) ApplyCurseLocked(player, m, aura.Aura.SkillId, now, true, aura.Gem, playerStats);
            m.Curses.Expire(now);
            var changed = m.Curses.Presented.Values.Where(c => !m.Curses.All.TryGetValue((c.Caster, c.Effect), out var next) || next.Buff != c.Buff).ToArray();
            foreach (var old in changed)
            { player.Area.Buffs.Clear(id, old.Row, old.Caster, tag: old.Buff); m.Curses.Presented.Remove((old.Caster, old.Effect)); }
            // A curse is its caster's; each application its own buff (its tag), so a renewed curse restarts its countdown.
            foreach (var curse in m.Curses.All.Values.Where(c => !m.Curses.Presented.ContainsKey((c.Caster, c.Effect))))
            {
                player.Area.Buffs.Set(id, new(curse.Row, curse.Caster, curse.Skill, Tag: curse.Buff,
                    U68: double.IsFinite(curse.Until) ? Buff.Seconds(curse.Until - now) : null));
                m.Curses.Presented[(curse.Caster, curse.Effect)] = curse;
            }
            double slow = m.Rarity is MonsterRarity.Rare or MonsterRarity.Unique
                ? m.Curses[Stat.TemporalChainsActionSpeedPlusPctVsRareOrUniqueFinal] : m.Curses[Stat.TemporalChainsActionSpeedPlusPctFinal];
            if (!ExternalTemporalChains(player, "TemporalChains"))
            {
                double oldRate = m.RecipientDebuffRate;
                double rate = RecipientEffectTime.Rate(2, n => m.Effects[n]);
                if (rate != oldRate)
                {
                    SettleBleeding(m, now);
                    m.Ignites.SetExpiryRate(now, rate); m.Poisons.SetExpiryRate(now, rate); m.Ailments.SetExpiryRate(now, rate);
                    m.SpiderWebs.SetExpiryRate(now, rate);
                    m.TauntedUntil = RecipientEffectTime.RetimedDeadline(m.TauntedUntil, now, oldRate, rate); m.RecipientDebuffRate = rate;
                    m.Ruptures.SetExpiryRate(now, rate);
                    m.Bleeds.SetExpiryRate(now, BleedingExpiryRate(m, now));
                }
                m.ReceivedActionFactor = Math.Max(BossMods.SlowestActionFactor(m.Variety), 1 + slow / 100); m.ReceivedActionUntil = slow != 0 ? double.PositiveInfinity : now;
            }
            if (m.Curses.PresentedVersion != m.Curses.Version)
            {
                var keys = m.Curses.PresentedStats.Concat(m.Curses.Stats.Keys).Distinct().ToArray();
                SetStats(player, id, keys.Where(StatCatalog.IsClientId)
                    .Select(n => (n, m.Effects[n] + SpiderRecipientStat(m, n, now))));
                m.Curses.PresentedStats.Clear(); m.Curses.PresentedStats.UnionWith(m.Curses.Stats.Keys);
                m.Curses.PresentedVersion = m.Curses.Version;
            }
        }
    }
}
