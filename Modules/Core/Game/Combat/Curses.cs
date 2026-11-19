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

    static bool ExternalTemporalChains(Player conn, string effect) => effect == "TemporalChains"
        && GameEvents.GrantedSkillExecutable.For(conn).Any(accept => accept(conn, effect));

    static void CurseCharges(Player conn, SpawnedMonster target, bool death)
    {
        foreach (var (kind, name) in new[] { (ChargeType.Power, "power"), (ChargeType.Frenzy, "frenzy"), (ChargeType.Endurance, "endurance") })
            if (RandomRolls.Chance(target.Curses[Stat.Of(death ? $"chance_to_grant_{name}_charge_on_death_%" : $"grant_attacker_{name}_charge_when_hit_%_chance")], 100, Random.Shared))
                ChargesOf(conn).Enqueue(new ChargeEvent.Grant(kind, 1, "curse"));
    }

    static int CurseLimit(Player conn, IReadOnlyDictionary<uint, int> player)
    {
        int Get(uint n) => GolemStat(player, n);
        return Get(Stat.HasCurseLimitEqualToMaximumPowerCharges) != 0 ? Get(Stat.MaxPowerCharges)
            : Math.Max(0, 1 + Get(Stat.NumberOfAdditionalCursesAllowed));
    }

    public static int CurseRadius(Player conn, ushort skill, bool aura, SupportedGem? captured = null, IReadOnlyDictionary<uint, int>? sources = null)
    {
        var own = (captured ?? SupportedSkill(conn, skill)).Stats; var player = sources ?? CharacterPlayerStats(conn);
        int Get(uint n) => GolemStat(player, n) + GolemStat(own, n);
        // Hexes retain their own base radius when Blasphemy converts them to auras.
        return SkillArea.Radius(22,
            n => Get(n) + (n == SkillArea.BaseIncreased ? Get(Stat.CurseAreaOfEffectPlusPct) : 0), spell: true);
    }

    public static bool ApplyCurseLocked(Player conn, SpawnedMonster monster, ushort skill, double now, bool aura,
        SupportedGem? captured = null, IReadOnlyDictionary<uint, int>? sources = null)
    {
        var gem = captured ?? SupportedSkill(conn, skill); string effect = SkillsFor(conn).EffectId(gem.Effect) ?? "";
        if (!CurseCatalog.All.TryGetValue(effect, out var definition) || ExternalTemporalChains(conn, effect)
            || monster.Dead || monster.Life == 0 || !monster.Hostile || !gem.RequirementsMet) return false;
        var player = sources ?? CharacterPlayerStats(conn);
        int Own(uint n) => GolemStat(gem.Stats, n);
        int Get(uint n) => GolemStat(player, n) + Own(n);
        if (monster.Mods.Effects[Stat.ImmuneToCurses] != 0 || !definition.Mark && monster.Mods.Effects[Stat.Hexproof] != 0
            && Get(Stat.IgnoreHexproof) == 0 && Get(Stat.Of("hexes_ignore_hexproof")) == 0
            && !RandomRolls.Chance(Get(Stat.ChanceToIgnoreHexproofPct), 100, Random.Shared)) return false;
        double factor = Math.Max(0, 1 + (Get(Stat.CurseEffectPlusPct) + Get(Stat.Of(definition.Mark ? "mark_effect_+%" : "hex_effect_+%"))
            + (definition.Mark ? Get(Stat.MarkSkillsCurseEffectPlusPct) : 0)
            + Get(Stat.Of(ReservationNames.GetValueOrDefault(effect, effect.ToLowerInvariant()) + "_curse_effect_+%"))
            + monster.Mods.Effects[Stat.CurseEffectOnSelfPlusPct]) / 100.0)
            * Math.Max(0, 1 + Own(Stat.SupportBlasphemyCurseEffectPlusPctFinal) / 100.0)
            * Math.Max(0, 1 + Own(Stat.SupportHextouchCurseEffectPlusPctFinal) / 100.0);
        var stats = gem.Stats.Where(p => CurseRecipientStats.Contains(p.Key))
            .ToDictionary(p => p.Key, p => (int)Math.Truncate(p.Value * factor));
        double seconds = definition.Mark ? double.PositiveInfinity : SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration),
            n => GolemStat(player, n) + (n == SkillDurationModifiers.IncreasedDuration ? Get(Stat.CurseDurationPlusPct) + Get(Stat.CurseSkillEffectDurationPlusPct) : 0),
            n => Own(n) + (n == SkillDurationModifiers.IncreasedDuration ? Own(Stat.BaseCurseDurationPlusPct) : 0));
        double rate = RecipientEffectTime.Rate(5, n => monster.Mods.Effects[n], temporalChains: effect == "TemporalChains");
        seconds *= Math.Max(0, 1 + Own(Stat.HextouchSupportCurseDurationPlusPctFinal) / 100d);
        if (aura && monster.Curses.All.GetValueOrDefault((conn.PeerId, effect)) is { Aura: true } existing
            && existing.Skill == skill && existing.Stats.Count == stats.Count && existing.Stats.All(p => stats.GetValueOrDefault(p.Key) == p.Value)) return true;
        if (definition.Mark)
            foreach (var m in conn.Monsters.Values.Where(m => !ReferenceEquals(m, monster)))
                foreach (var mark in m.Curses.All.Values.Where(c => c.Caster == conn.PeerId && c.Mark).ToArray()) m.Curses.Remove(mark.Caster, mark.Effect);
        monster.Curses.Put(new(conn.PeerId, effect, skill, definition.Buff, conn.NextBuff++, now,
            aura ? double.PositiveInfinity : now + seconds / rate, aura, definition.Mark, stats), CurseLimit(conn, player));
        if (!aura) monster.PresentationChanged?.Invoke();
        return true;
    }

    static void CursesOnHitLocked(Player conn, SpawnedMonster target, ushort sourceSkill, bool spell, double now)
    {
        if (target.Dead || target.Life == 0 || conn.Reservations.Secondary) return;
        var source = sourceSkill != 0 ? SupportedSkill(conn, sourceSkill) : null;
        bool hextouch = source is not null && GolemStat(source.Stats, Stat.ApplyLinkedCursesOnHitPct) is var chance
            && RandomRolls.Chance(chance, 100, Random.Shared);
        if (!hextouch && (spell || target.Rarity is not (MonsterRarity.Rare or MonsterRarity.Unique))) return;
        var player = CharacterPlayerStats(conn);
        foreach (var skill in GemSkillEffects(conn))
        {
            string effect = SkillsFor(conn).EffectId(skill.Effect) ?? "";
            if (!CurseCatalog.All.TryGetValue(effect, out var curse)) continue;
            var gem = SupportedSkill(conn, skill.Id);
            if (!gem.RequirementsMet) continue;
            if (!curse.Mark && hextouch && source!.Host != 0 && gem.Host == source.Host && gem.Group == source.Group
                && GolemStat(gem.Stats, Stat.CurseTriggeredByHextouch) != 0)
                ApplyCurseLocked(conn, target, skill.Id, now, false, gem, player);
            if (curse.Mark && !spell && target.Rarity is MonsterRarity.Rare or MonsterRarity.Unique
                && GolemStat(gem.Stats, Stat.TriggerOnAttackHitAgainstRareOrUnique) != 0
                && now >= conn.SkillReadyAt.GetValueOrDefault(gem.Effect))
            {
                if (!PayMana(conn, skill.Id, out _, out _)) continue;
                conn.SkillReadyAt[gem.Effect] = now + 4;
                ApplyCurseLocked(conn, target, skill.Id, now, false, gem, player);
            }
        }
    }

    static async Task<bool> CastCurse(Player conn,
        ushort skill, (int X, int Y) aim, uint target, int recovery, Func<bool> current)
    {
        var gem = SupportedSkill(conn, skill); string effect = SkillsFor(conn).EffectId(gem.Effect)!;
        PlayerSkillSource? source;
        lock (conn.Monsters) source = CurrentPlayerSkillSourceLocked(conn, skill);
        if (source is not { Active: true }) return false;
        double release = AreaTime(conn) + (CastMs(conn, gem.Effect, skill) ?? 1000) / 1000d;
        var lifetime = CombatLifetime;
        while (true)
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || conn.Closed || conn.LeavingArea
                    || conn.PlayerLife == 0 || recovery != conn.ActionInterruptionGeneration) return false;
                if (!conn.Paused && AreaTime(conn) >= release)
                {
                    double now = AreaTime(conn); var player = CharacterPlayerStats(conn);
                    var currentSource = CurrentPlayerSkillSourceLocked(conn, skill);
                    var currentGem = SupportedSkill(conn, skill);
                    if (currentSource is not { Active: true } || currentSource.Identity != source.Identity
                        || currentGem.Effect != gem.Effect || !currentGem.RequirementsMet
                        || GemSkillInstanceDisabled(conn, currentGem.Effect, skill) is not null) return false;
                    RecordCompletedCurseCast(conn, effect, conn.Clock.Elapsed.TotalSeconds);
                    int radius = CurseRadius(conn, skill, false, currentGem, player);
                    foreach (var (id, m) in conn.Monsters)
                        if (CurseCatalog.All[effect].Mark ? id == target : Dist(m.Walk.Position(now), aim) <= radius)
                            ApplyCurseLocked(conn, m, skill, now, false, currentGem, player);
                    break;
                }
            }
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        await EnemyDebuffsTick(conn, AreaTime(conn));
        return true;
    }

    static void CursesTickLocked(Player conn, double now, List<byte> bytes)
    {
        var active = conn.Auras.Values.Where(a => CurseCatalog.Hex(a.EffectId) && !ExternalTemporalChains(conn, a.EffectId)).ToArray();
        var player = active.Length > 0 ? CharacterPlayerStats(conn) : null;
        var auras = active.TakeLast(player is null ? 0 : CurseLimit(conn, player))
            .Select(a => (Aura: a, Gem: SupportedSkill(conn, a.SkillId)))
            .Select(a => (a.Aura, a.Gem, Radius: CurseRadius(conn, a.Aura.SkillId, true, a.Gem, player))).ToArray();
        var here = conn.Walk.Position(now);
        foreach (var (id, m) in conn.Monsters)
        {
            foreach (var curse in m.Curses.All.Values.Where(c => c.Mark && (m.Dead || m.Life == 0 || c.Caster == conn.PeerId && conn.PlayerLife == 0)).ToArray())
                m.Curses.Remove(curse.Caster, curse.Effect);
            foreach (var curse in m.Curses.All.Values.Where(c => c.Caster == conn.PeerId && c.Aura).ToArray())
                if (conn.PlayerLife == 0 || m.Dead || !auras.Any(a => a.Aura.SkillId == curse.Skill && Dist(m.Walk.Position(now), here) <= a.Radius))
                    m.Curses.Remove(curse.Caster, curse.Effect);
            if (conn.PlayerLife > 0 && !InTown(conn))
                foreach (var aura in auras)
                    if (Dist(m.Walk.Position(now), here) <= aura.Radius) ApplyCurseLocked(conn, m, aura.Aura.SkillId, now, true, aura.Gem, player);
            m.Curses.Expire(now);
            var changed = m.Curses.Presented.Values.Where(c => !m.Curses.All.TryGetValue((c.Caster, c.Effect), out var next) || next.Buff != c.Buff).ToArray();
            foreach (var old in changed)
            { bytes.AddRange(InstanceEntities.RemoveBuff(id, old.Buff)); m.Curses.Presented.Remove((old.Caster, old.Effect)); }
            foreach (var curse in m.Curses.All.Values.Where(c => !m.Curses.Presented.ContainsKey((c.Caster, c.Effect))))
            {
                bytes.AddRange(InstanceEntities.AddBuff(id, curse.Buff, curse.Row, PlayerId, curse.Skill,
                    u68: double.IsFinite(curse.Until) ? BitConverter.SingleToUInt32Bits((float)Math.Max(0, curse.Until - now)) : null));
                m.Curses.Presented[(curse.Caster, curse.Effect)] = curse;
            }
            double slow = m.Rarity is MonsterRarity.Rare or MonsterRarity.Unique
                ? m.Curses[Stat.TemporalChainsActionSpeedPlusPctVsRareOrUniqueFinal] : m.Curses[Stat.TemporalChainsActionSpeedPlusPctFinal];
            if (!ExternalTemporalChains(conn, "TemporalChains"))
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
                bytes.AddRange(InstanceEntities.StatsUpdate(id, keys.Where(StatCatalog.IsClientId)
                    .Select(n => (n, m.Effects[n] + SpiderRecipientStat(m, n, now))).ToList()));
                m.Curses.PresentedStats.Clear(); m.Curses.PresentedStats.UnionWith(m.Curses.Stats.Keys);
                m.Curses.PresentedVersion = m.Curses.Version;
            }
        }
    }
}
