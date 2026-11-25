using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    /// <summary>The owned GolemState actor for this id and exact monster lifetime, if any.</summary>
    public static bool OwnedGolemActor(Player player, uint id, SpawnedMonster monster)
    {
        GolemState.Golem? golem = player.Golems.All.GetValueOrDefault(id)
            ?? player.Reservations.Fighters.GetValueOrDefault(id)
            ?? player.Summons.Fighters.GetValueOrDefault(id);
        return golem is not null && ReferenceEquals(golem.Monster, monster);
    }

    public static bool GolemElementalImmune(GolemState.Golem golem)
    {
        int Player(uint name) => GolemStat(golem.Player, name);
        return golem.Definition.IsGolem && (Player(Stat.GolemImmunityToElementalDamage) > 0
            || golem.Definition.Kind is "Flame" or "Ice" or "Lightning"
                && Player(Stat.ElementalGolemImmunityToElementalDamage) > 0);
    }

    /// <summary>Undamageable owned actors reject enemy hits and damaging ailments.</summary>
    public static bool GolemIgnoresDots(GolemState.Golem golem) =>
        golem.Monster.Invulnerable || !golem.Definition.IsGolem && golem.Definition.Kind is "Agony" or "SkitterCold" or "SkitterLightning";

    static bool GolemHasDots(GolemState.Golem golem) =>
        !golem.Monster.Ignites.Idle || !golem.Monster.Bleeds.Idle || !golem.Monster.Poisons.Idle;

    static double GolemMeatShieldDamageFactor(GolemState.Golem golem) =>
        Math.Max(0, 1 + GolemStat(golem.Own, Stat.SupportMinionDefensiveStanceMinionDamageTakenPlusPctFinal) / 100d);

    static double GolemElementalReductionFactor(MonsterModEffects effects) =>
        (100 - Math.Clamp(effects[Stat.AdditionalElementalDamageReductionPct], 0, DamageModel.DamageReductionCap)) / 100d;

    static void QueueGolemDotPacket(Player player, GolemState.Golem golem, byte[] packet, string? log = null) =>
        player.Minions.OutputQueue.Add(new(packet, log, golem.Id, golem.Monster, WorldId: golem.Id));

    /// <summary>Advances every owned ailment set once, applies its positive whole typed damage to ES then Life, and
    /// interrupts recharge at the latest positive subinterval end.</summary>
    public static void GolemDotsLocked(Player player, GolemState.Golem golem, double from, double to)
    {
        var m = golem.Monster;
        if (!(to > from) || !GolemCurrent(player, golem) || m.Dead || m.Life == 0) return;
        bool elementalImmune = GolemElementalImmune(golem);
        bool ignoresDots = GolemIgnoresDots(golem) || m.Effects[Stat.BaseCannotBeDamaged] != 0;
        double supportReduction = GolemMeatShieldDamageFactor(golem) * CompanionshipDamageTaken(player, golem, to);
        double linkDamageTaken = MinionLinkDamageTaken(player, golem, Math.BitDecrement(to));
        double fireFactor = elementalImmune || ignoresDots ? 0
            : MonsterDotTaken(m, DamageType.Fire, to) * GolemElementalReductionFactor(m.Effects) * linkDamageTaken;
        int fire = m.Ignites.Advance(to, fireFactor * supportReduction, 0, out double? fireAt);
        int legion = InfernalLegionSelfDamage(golem, to - from, fireFactor);
        int blessing = ignoresDots ? 0 : (int)(GuardianBlessingMinionDamage(player, golem, from, to) * linkDamageTaken);
        double bleedFactor = ignoresDots ? 0
            : BleedTaken(m, to, m.Effects[Stat.NoExtraBleedDamageWhileMoving] != 0) * supportReduction * linkDamageTaken
                * (100 - m.Effects.PhysicalReduction) / 100d;
        int bleed = m.Bleeds.Advance(to, bleedFactor,
            ignoresDots ? 0 : AggravatedExtra(m, to) * linkDamageTaken, out double? bleedAt);
        double chaosFactor = ignoresDots ? 0 : MonsterDotTaken(m, DamageType.Chaos, to) * supportReduction * linkDamageTaken;
        int poison = m.Poisons.Advance(to, chaosFactor, out double? poisonAt);
        int whispers = MinionWhispersDamageLocked(player, golem, from, to, chaosFactor);
        if (ignoresDots) fire = bleed = poison = 0;

        double? lastDamageAt = null;
        foreach (double? at in new[] { fireAt, bleedAt, poisonAt })
            if (at is double value && (lastDamageAt is not double last || value > last))
                lastDamageAt = value;
        if (legion > 0 || blessing > 0 || whispers > 0) lastDamageAt = to;
        if (lastDamageAt is double endpoint) golem.ShieldPool.Interrupt(endpoint);

        long lifeDamage = 0;
        if (fire > 0) lifeDamage += golem.ShieldPool.Damage(fire, bypass: false, lastDamageAt!.Value);
        if (legion > 0) lifeDamage += golem.ShieldPool.Damage(legion, bypass: false, to);
        if (blessing > 0) lifeDamage += golem.ShieldPool.Damage(blessing, bypass: false, to);
        if (bleed > 0) lifeDamage += golem.ShieldPool.Damage(bleed, bypass: false, lastDamageAt!.Value);
        if (poison > 0 || whispers > 0)
            lifeDamage += golem.ShieldPool.Damage(ActorScale.Saturate((long)poison + whispers),
                m.Mods.Effects[Stat.ChaosDamageDoesNotBypassEnergyShield] == 0, lastDamageAt!.Value);

        if (lifeDamage > 0)
        {
            m.Life = (uint)Math.Max(0, (long)m.Life - lifeDamage);
            player.Minions.OutputQueue.Add(new([], "golem damage over time", golem.Id, m, PlayerMinions.OutputKind.Life,
                DeathKind: DotDeathKind(m, m.Life)));
        }
        if (m.Life == 0 || !GolemCurrent(player, golem))
        {
            if (m.Life == 0 && GolemCurrent(player, golem)) GolemDieLocked(player, golem, to);
            return;
        }
        if (MinionInstabilityLocked(player, golem, to)) return;
        if (lifeDamage > 0) ShatteringIceGolemBurstLocked(player, golem, to);
        UpdateGolemDotBuffsLocked(player, golem, to);
    }

    static void UpdateGolemDotBuffsLocked(Player player, GolemState.Golem golem, double now)
    {
        var m = golem.Monster;
        if (!m.Ignites.Burning) EndDot(player, golem.Id, ref m.Ignites.Buff, m.Ignites.BuffSource);
        if (!m.Bleeds.Burning) EndDot(player, golem.Id, ref m.Bleeds.Buff, m.Bleeds.BuffSource);
        if (!m.Poisons.Poisoned) { EndDot(player, golem.Id, ref m.Poisons.Buff, m.Poisons.BuffSource); return; }
        uint source = m.Poisons.BuffSource != 0 ? m.Poisons.BuffSource : player.Id;
        ShowDot(player, golem.Id, ref m.Poisons.Buff, ref m.Poisons.BuffSource, new(PoisonBuffRow, source,
            Stacks: (ushort)Math.Clamp(m.Poisons.Stacks(now), 1, ushort.MaxValue), U68: Buff.Seconds(m.Poisons.Until - now)));
    }

    static void ShowGolemDotBuffLocked(Player player, GolemState.Golem golem, ref ushort? shown, ref uint shownSource,
        ushort row, uint source, double seconds, ushort stacks = 1) =>
        ShowDot(player, golem.Id, ref shown, ref shownSource, new(row, source, Stacks: stacks, U68: Buff.Seconds(seconds)));

    /// <summary>Shows the moved sets once on their new actor, using only their remaining absolute lifetimes.</summary>
    public static void PresentTransferredGolemDotBuffsLocked(Player player, GolemState.Golem golem, double now)
    {
        var m = golem.Monster;
        if (m.Ignites.Until > now)
        {
            uint source = m.Ignites.BuffSource != 0 ? m.Ignites.BuffSource : player.Id;
            ShowGolemDotBuffLocked(player, golem, ref m.Ignites.Buff, ref m.Ignites.BuffSource, IgnitedBuffRow, source,
                m.Ignites.Until - now);
        }
        if (m.Bleeds.Until > now && LabyrinthTrials.BleedingBuffRow is ushort bleedRow)
        {
            uint source = m.Bleeds.BuffSource != 0 ? m.Bleeds.BuffSource : player.Id;
            ShowGolemDotBuffLocked(player, golem, ref m.Bleeds.Buff, ref m.Bleeds.BuffSource, bleedRow, source, m.Bleeds.Until - now);
        }
        int stacks = m.Poisons.Stacks(now);
        if (stacks > 0)
        {
            uint source = m.Poisons.BuffSource != 0 ? m.Poisons.BuffSource : player.Id;
            ShowGolemDotBuffLocked(player, golem, ref m.Poisons.Buff, ref m.Poisons.BuffSource, PoisonBuffRow, source,
                m.Poisons.Until - now, (ushort)Math.Clamp(stacks, 1, ushort.MaxValue));
        }
    }

    /// <summary>Admits hostile bleed and poison from an accepted contact's real source effects onto the golem's own
    /// monster ailment sets, using the player pipeline's frozen chance, damage and duration contract.</summary>
    public static void GolemHostileAilmentsLocked(Player player, GolemState.Golem golem, uint attacker,
        SpawnedMonster source, DamagePacket rolled, bool critical, bool attack, double now, double damagingAilmentFactor = 1)
    {
        var target = golem.Monster;
        var effects = source.Mods.Effects;
        var victim = target.Mods.Effects;
        var random = player.IncomingHitRandom;
        // The hit roll carries the monster's 1.3 critical multiplier; ailments use their own ordinary 50%-more critical
        // DoT row, so remove only that hit multiplier before deriving their bases.
        var baseHit = critical ? rolled.Scale(1 / 1.3) : rolled;
        if (damagingAilmentFactor != 1) baseHit = baseHit.Scale(damagingAilmentFactor);
        double physical = baseHit[DamageType.Physical].Min;
        double chaos = baseHit[DamageType.Chaos].Min;
        bool elementalImmune = GolemElementalImmune(golem);

        if (!elementalImmune)
        {
            double fire = baseHit[DamageType.Fire].Min;
            int IgniteStat(uint n) => n == Stat.CriticalAilmentDotMultiplierPlus ? 50
                : IgniteIncreases.Contains(n) ? 0 : effects[n];
            if (fire > 0 && victim[Stat.ImmuneToIgnite] == 0 && victim[Stat.CannotBeIgnited] == 0
                && IgniteFromHit(fire, critical, IgniteStat, 1, target, random) is { } ignite
                && !RandomRolls.Chance(victim[Stat.AvoidIgnitePct] + victim[Stat.BaseAvoidIgnitePct], 100, random)
                && target.Ignites.Add(now, ignite.DamagePerSecond, ignite.RemainingSeconds))
            {
                target.Ignites.BuffSource = attacker;
                ShowGolemDotBuffLocked(player, golem, ref target.Ignites.Buff, ref target.Ignites.BuffSource, IgnitedBuffRow, attacker,
                    ignite.RemainingSeconds);
            }
        }

        int bleedChance = Math.Max(effects[Stat.GlobalBleedOnHit] != 0 ? 100 : 0,
            attack ? effects[Stat.BleedOnHitWithAttacksPct] : 0);
        double bleedDamage = physical * .10
            * Math.Max(0, 1 + effects[Stat.BleedingDamagePlusPct] / 100.0)
            * Math.Max(0, 1 + effects[Stat.MonsterBleedingDamagePlusPctFinal] / 100.0);
        double bleedSeconds = Ailments.BleedDurationSeconds
            * Math.Max(0, 1 + effects[Stat.BaseBleedDurationPlusPct] / 100.0)
            * IncomingDamagingAilments.SelfDuration(name => victim[name], bleed: true);
        if (bleedChance > 0 && bleedDamage > 0)
        {
            var settings = new MonsterIgnites.Settings(bleedChance, bleedDamage, bleedDamage,
                bleedDamage * 1.5, bleedDamage * 1.5, bleedSeconds);
            target.Bleeds.Stacks = settings.Stacks;
            bool immune = victim[Stat.ImmuneToBleeding] != 0 || victim[Stat.CannotGainBleeding] != 0
                || victim[Stat.BaseCannotGainBleeding] != 0;
            if (!immune && RandomRolls.Chance(settings.Chance, 100, random)
                && target.Bleeds.TryApply(settings with { Chance = 100 }, critical, now,
                    victim[Stat.AvoidBleedPct] + victim[Stat.BaseAvoidBleedPct], immune, random)
                && LabyrinthTrials.BleedingBuffRow is ushort row)
            {
                target.Bleeds.BuffSource = attacker;
                ShowGolemDotBuffLocked(player, golem, ref target.Bleeds.Buff, ref target.Bleeds.BuffSource, row, attacker,
                    target.Bleeds.Until - now);
            }
        }

        int poisonChance = Math.Max(effects[Stat.GlobalPoisonOnHit] != 0 ? 100 : 0,
            effects[Stat.BaseChanceToPoisonOnHitPct]);
        if (attack) poisonChance = Math.Max(poisonChance,
            effects[Stat.BaseAttacksPoison] != 0 ? 100 : effects[Stat.ChanceToPoisonOnHitWithAttacksPct]);
        bool allCanPoison = effects[Stat.AllDamageCanPoison] != 0;
        double poisonBase = allCanPoison
            ? baseHit.Components.Where(c => !(elementalImmune && DamageTypes.IsElemental(c.Type)))
                .Sum(c => c.Range.Min)
            : physical + chaos;
        double poisonDamage = poisonBase * .30
            * Math.Max(0, 1 + effects[Stat.BasePoisonDamagePlusPct] / 100.0)
            * Math.Max(0, 1 + effects[Stat.MonsterPoisonDamagePlusPctFinal] / 100.0);
        double poisonSeconds = Ailments.PoisonDurationSeconds
            * Math.Max(0, 1 + effects[Stat.BasePoisonDurationPlusPct] / 100.0)
            * MonsterPoisons.SelfDuration(name => victim[name])
            * IncomingDamagingAilments.SelfDuration(name => victim[name], bleed: false);
        if (poisonChance > 0 && poisonDamage > 0)
        {
            var settings = new MonsterPoisons.Settings(poisonChance, poisonDamage, poisonDamage,
                poisonDamage * 1.5, poisonDamage * 1.5, poisonSeconds);
            bool immune = victim[Stat.CannotBePoisoned] != 0 || victim[Stat.ImmuneToPoison] != 0
                || victim[Stat.Of("base_cannot_be_poisoned")] != 0;
            if (target.Poisons.TryApply(settings, critical, now,
                    victim[Stat.AvoidPoisonPct] + victim[Stat.BaseAvoidPoisonPct], immune, random))
            {
                target.Poisons.BuffSource = attacker;
                ShowGolemDotBuffLocked(player, golem, ref target.Poisons.Buff, ref target.Poisons.BuffSource, PoisonBuffRow, attacker,
                    target.Poisons.Until - now);
            }
        }
    }
}
