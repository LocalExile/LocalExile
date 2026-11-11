using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    /// <summary>The owned GolemState actor for this id and exact monster lifetime, if any.</summary>
    public static bool OwnedGolemActor(ClientState conn, uint id, SpawnedMonster monster)
    {
        GolemState.Golem? golem = conn.Golems.All.GetValueOrDefault(id)
            ?? conn.Reservations.Fighters.GetValueOrDefault(id)
            ?? conn.Summons.Fighters.GetValueOrDefault(id);
        return golem is not null && ReferenceEquals(golem.Monster, monster);
    }

    public static bool GolemElementalImmune(GolemState.Golem golem)
    {
        int Player(string name) => GolemStat(golem.Player, name);
        return golem.Definition.IsGolem && (Player("golem_immunity_to_elemental_damage") > 0
            || golem.Definition.Kind is "Flame" or "Ice" or "Lightning"
                && Player("elemental_golem_immunity_to_elemental_damage") > 0);
    }

    /// <summary>The invulnerable reservation families reject every contact and every owned damaging ailment.</summary>
    public static bool GolemIgnoresDots(GolemState.Golem golem) =>
        !golem.Definition.IsGolem && golem.Definition.Kind is "Agony" or "SkitterCold" or "SkitterLightning";

    static bool GolemHasDots(GolemState.Golem golem) =>
        !golem.Monster.Ignites.Idle || !golem.Monster.Bleeds.Idle || !golem.Monster.Poisons.Idle;

    static void QueueGolemDotPacket(ClientState conn, GolemState.Golem golem, byte[] packet, string? log = null) =>
        conn.Minions.OutputQueue.Add(new(packet, log, golem.Id, golem.Monster, WorldId: golem.Id));

    /// <summary>Advances every owned ailment set once, applies its positive whole typed damage to ES then Life, and
    /// interrupts recharge at the latest positive subinterval end.</summary>
    public static void GolemDotsLocked(ClientState conn, GolemState.Golem golem, double from, double to)
    {
        var m = golem.Monster;
        if (!(to > from) || !GolemCurrent(conn, golem) || m.Dead || m.Life == 0) return;
        bool elementalImmune = GolemElementalImmune(golem);
        bool ignoresDots = GolemIgnoresDots(golem);
        double fireFactor = elementalImmune || ignoresDots ? 0 : MonsterDotTaken(m, DamageType.Fire, to);
        int fire = m.Ignites.Advance(to, fireFactor, 0, out double? fireAt);
        int bleed = m.Bleeds.Advance(to, ignoresDots ? 0 : BleedTaken(m, to),
            ignoresDots ? 0 : AggravatedExtra(m, to), out double? bleedAt);
        int poison = m.Poisons.Advance(to, ignoresDots ? 0 : MonsterDotTaken(m, DamageType.Chaos, to),
            out double? poisonAt);
        if (ignoresDots) fire = bleed = poison = 0;

        double? lastDamageAt = null;
        foreach (double? at in new[] { fireAt, bleedAt, poisonAt })
            if (at is double value && (lastDamageAt is not double last || value > last))
                lastDamageAt = value;
        if (lastDamageAt is double endpoint) golem.ShieldPool.Interrupt(endpoint);

        long lifeDamage = 0;
        if (fire > 0) lifeDamage += golem.ShieldPool.Damage(fire, bypass: false, lastDamageAt!.Value);
        if (bleed > 0) lifeDamage += golem.ShieldPool.Damage(bleed, bypass: false, lastDamageAt!.Value);
        if (poison > 0)
            lifeDamage += golem.ShieldPool.Damage(poison,
                m.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"] == 0, lastDamageAt!.Value);

        if (lifeDamage > 0)
        {
            m.Life = (uint)Math.Max(0, (long)m.Life - lifeDamage);
            conn.Minions.OutputQueue.Add(new([], "golem damage over time", golem.Id, m, PlayerMinions.OutputKind.Life));
        }
        if (m.Life == 0 || !GolemCurrent(conn, golem))
        {
            if (m.Life == 0 && GolemCurrent(conn, golem)) GolemDieLocked(conn, golem, to);
            return;
        }
        UpdateGolemDotBuffsLocked(conn, golem, to);
    }

    static void UpdateGolemDotBuffsLocked(ClientState conn, GolemState.Golem golem, double now)
    {
        var m = golem.Monster;
        if (!m.Ignites.Burning && m.Ignites.Buff is ushort ignited)
        {
            QueueGolemDotPacket(conn, golem, InstanceEntities.RemoveBuff(golem.Id, ignited));
            m.Ignites.Buff = null;
        }
        if (!m.Bleeds.Burning && m.Bleeds.Buff is ushort bleeding)
        {
            QueueGolemDotPacket(conn, golem, InstanceEntities.RemoveBuff(golem.Id, bleeding));
            m.Bleeds.Buff = null;
        }
        if (!m.Poisons.Poisoned)
        {
            if (m.Poisons.Buff is ushort poison)
            {
                QueueGolemDotPacket(conn, golem, InstanceEntities.RemoveBuff(golem.Id, poison));
                m.Poisons.Buff = null;
            }
            return;
        }
        if (m.Poisons.Buff is ushort old)
            QueueGolemDotPacket(conn, golem, InstanceEntities.RemoveBuff(golem.Id, old));
        ushort next = conn.NextBuff++;
        m.Poisons.Buff = next;
        uint source = m.Poisons.BuffSource != 0 ? m.Poisons.BuffSource : PlayerId;
        QueueGolemDotPacket(conn, golem, InstanceEntities.AddBuff(golem.Id, next, PoisonBuffRow, source,
            stacks: (ushort)Math.Clamp(m.Poisons.Stacks(now), 1, ushort.MaxValue),
            u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, m.Poisons.Until - now))));
    }

    static void ShowGolemDotBuffLocked(ClientState conn, GolemState.Golem golem, ref ushort? shown, ushort row, uint source,
        double seconds, ushort stacks = 1)
    {
        if (shown is ushort old) QueueGolemDotPacket(conn, golem, InstanceEntities.RemoveBuff(golem.Id, old));
        shown = conn.NextBuff++;
        QueueGolemDotPacket(conn, golem, InstanceEntities.AddBuff(golem.Id, shown.Value, row, source,
            stacks: stacks, u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, seconds))));
    }

    /// <summary>Shows the moved sets once on their new actor, using only their remaining absolute lifetimes.</summary>
    public static void PresentTransferredGolemDotBuffsLocked(ClientState conn, GolemState.Golem golem, double now)
    {
        var m = golem.Monster;
        if (m.Ignites.Until > now)
        {
            uint source = m.Ignites.BuffSource != 0 ? m.Ignites.BuffSource : PlayerId;
            ShowGolemDotBuffLocked(conn, golem, ref m.Ignites.Buff, IgnitedBuffRow, source,
                m.Ignites.Until - now);
        }
        if (m.Bleeds.Until > now && LabyrinthTrials.BleedingBuffRow is ushort bleedRow)
        {
            uint source = m.Bleeds.BuffSource != 0 ? m.Bleeds.BuffSource : PlayerId;
            ShowGolemDotBuffLocked(conn, golem, ref m.Bleeds.Buff, bleedRow, source, m.Bleeds.Until - now);
        }
        int stacks = m.Poisons.Stacks(now);
        if (stacks > 0)
        {
            uint source = m.Poisons.BuffSource != 0 ? m.Poisons.BuffSource : PlayerId;
            ShowGolemDotBuffLocked(conn, golem, ref m.Poisons.Buff, PoisonBuffRow, source,
                m.Poisons.Until - now, (ushort)Math.Clamp(stacks, 1, ushort.MaxValue));
        }
    }

    /// <summary>Admits hostile bleed and poison from an accepted contact's real source effects onto the golem's own
    /// monster ailment sets, using the player pipeline's frozen chance, damage and duration contract.</summary>
    public static void GolemHostileAilmentsLocked(ClientState conn, GolemState.Golem golem, uint attacker,
        SpawnedMonster source, DamagePacket rolled, bool critical, bool attack, double now)
    {
        var target = golem.Monster;
        var effects = source.Mods.Effects;
        var victim = target.Mods.Effects;
        var random = conn.IncomingHitRandom;
        // The hit roll carries the monster's 1.3 critical multiplier; ailments use their own ordinary 50%-more critical
        // DoT row, so remove only that hit multiplier before deriving their bases.
        var baseHit = critical ? rolled.Scale(1 / 1.3) : rolled;
        double physical = baseHit[DamageType.Physical].Min;
        double chaos = baseHit[DamageType.Chaos].Min;
        bool elementalImmune = GolemElementalImmune(golem);

        if (!elementalImmune)
        {
            double fire = baseHit[DamageType.Fire].Min;
            int IgniteStat(string n) => n == "critical_ailment_dot_multiplier_+" ? 50
                : IgniteIncreases.Contains(n) ? 0 : effects[n];
            if (fire > 0 && victim["immune_to_ignite"] == 0 && victim["cannot_be_ignited"] == 0
                && IgniteFromHit(fire, critical, IgniteStat, 1, target, random) is { } ignite
                && !RandomRolls.Chance(victim["avoid_ignite_%"] + victim["base_avoid_ignite_%"], 100, random)
                && target.Ignites.Add(now, ignite.DamagePerSecond, ignite.RemainingSeconds))
            {
                target.Ignites.BuffSource = attacker;
                ShowGolemDotBuffLocked(conn, golem, ref target.Ignites.Buff, IgnitedBuffRow, attacker,
                    ignite.RemainingSeconds);
            }
        }

        int bleedChance = Math.Max(effects["global_bleed_on_hit"] != 0 ? 100 : 0,
            attack ? effects["bleed_on_hit_with_attacks_%"] : 0);
        double bleedDamage = physical * .10
            * Math.Max(0, 1 + effects["bleeding_damage_+%"] / 100.0)
            * Math.Max(0, 1 + effects["monster_bleeding_damage_+%_final"] / 100.0);
        double bleedSeconds = Ailments.BleedDurationSeconds
            * Math.Max(0, 1 + effects["base_bleed_duration_+%"] / 100.0)
            * IncomingDamagingAilments.SelfDuration(name => victim[name], bleed: true);
        if (bleedChance > 0 && bleedDamage > 0)
        {
            var settings = new MonsterIgnites.Settings(bleedChance, bleedDamage, bleedDamage,
                bleedDamage * 1.5, bleedDamage * 1.5, bleedSeconds);
            target.Bleeds.Stacks = settings.Stacks;
            bool immune = victim["immune_to_bleeding"] != 0 || victim["cannot_gain_bleeding"] != 0
                || victim["base_cannot_gain_bleeding"] != 0;
            if (!immune && RandomRolls.Chance(settings.Chance, 100, random)
                && target.Bleeds.TryApply(settings with { Chance = 100 }, critical, now,
                    victim["avoid_bleed_%"] + victim["base_avoid_bleed_%"], immune, random)
                && LabyrinthTrials.BleedingBuffRow is ushort row)
            {
                target.Bleeds.BuffSource = attacker;
                ShowGolemDotBuffLocked(conn, golem, ref target.Bleeds.Buff, row, attacker,
                    target.Bleeds.Until - now);
            }
        }

        int poisonChance = Math.Max(effects["global_poison_on_hit"] != 0 ? 100 : 0,
            effects["base_chance_to_poison_on_hit_%"]);
        if (attack) poisonChance = Math.Max(poisonChance,
            effects["base_attacks_poison"] != 0 ? 100 : effects["chance_to_poison_on_hit_with_attacks_%"]);
        bool allCanPoison = effects["all_damage_can_poison"] != 0;
        double poisonBase = allCanPoison
            ? baseHit.Components.Where(c => !(elementalImmune && DamageTypes.IsElemental(c.Type)))
                .Sum(c => c.Range.Min)
            : physical + chaos;
        double poisonDamage = poisonBase * .30
            * Math.Max(0, 1 + effects["base_poison_damage_+%"] / 100.0)
            * Math.Max(0, 1 + effects["monster_poison_damage_+%_final"] / 100.0);
        double poisonSeconds = Ailments.PoisonDurationSeconds
            * Math.Max(0, 1 + effects["base_poison_duration_+%"] / 100.0)
            * MonsterPoisons.SelfDuration(name => victim[name])
            * IncomingDamagingAilments.SelfDuration(name => victim[name], bleed: false);
        if (poisonChance > 0 && poisonDamage > 0)
        {
            var settings = new MonsterPoisons.Settings(poisonChance, poisonDamage, poisonDamage,
                poisonDamage * 1.5, poisonDamage * 1.5, poisonSeconds);
            bool immune = victim["cannot_be_poisoned"] != 0 || victim["immune_to_poison"] != 0
                || victim["base_cannot_be_poisoned"] != 0;
            if (target.Poisons.TryApply(settings, critical, now,
                    victim["avoid_poison_%"] + victim["base_avoid_poison_%"], immune, random))
            {
                target.Poisons.BuffSource = attacker;
                ShowGolemDotBuffLocked(conn, golem, ref target.Poisons.Buff, PoisonBuffRow, attacker,
                    target.Poisons.Until - now);
            }
        }
    }
}
