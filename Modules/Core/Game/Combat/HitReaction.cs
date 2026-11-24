using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

/// <summary>A hit's stun inputs: its stunning damage (<see cref="HitReaction.StunningDamage"/>), the attacker's stun
/// threshold reduction and stun duration increases, whether it is melee (the duration), the player whose "when you
/// stun" gains it feeds, the melee hit's raw fortification damage plus whether that same hit already fortified, and
/// whether a qualifying hit guarantees a stun, and the stunning skill's own stats (its "on stun" charge gains).
/// Minions, explosions and /kill pass their plain damage and no owner.</summary>
public readonly record struct StunHit(double Damage, int ThresholdReductionPct = 0, int DurationPct = 0, bool Melee = false,
    Player? Owner = null, decimal FortificationDamage = 0m, bool Fortified = false, bool Guaranteed = false,
    double BaseSeconds = 0, IReadOnlyDictionary<uint, int>? Skill = null)
{
    public int DoubleDurationChance { get; init; }
    public Action? OnStun { get; init; }
}

/// <summary>
/// A monster's stun, presented as its hit reaction (native Actor TakeHit recovery, sent by <c>SendMonsterFlinch</c>).
/// wiki Stun: chance = 200 x stunning damage / threshold, ignored at 20% or less, certain at half the threshold; a
/// reduction above 75% diminishes; duration = 350 ms x attacker's increases / defender's recovery; a boss cannot be
/// stunned for 4 s after a stun, or for the 2 s its own row names (BossMods). Damage over time and reflect do not get here. An accepted stun stops the server's walk,
/// invalidates its ordinary attack and holds AI for that same recovery; the native ACT selects the TakeHit animation.
/// </summary>
public static class HitReaction
{
    const double BaseSeconds = .35;
    const double BossImmunitySeconds = 4;

    /// <summary>The hit's damage as the stun chance counts it, by Metadata/Characters/Character.ot:
    /// melee_hit_damage_stun_multiplier_+% 25, non_physical_hit_damage_stun_multiplier_+% -25 and
    /// non_melee_hit_damage_stun_multiplier_+%_final -25. The client reads none of them: the formula is the server's.</summary>
    public static double StunningDamage(double damage, double physicalShare, bool melee)
    {
        double inherent = melee ? 1.25 : 1;
        return damage * (physicalShare * inherent + (1 - physicalShare) * (inherent - .25)) * (melee ? 1 : .75);
    }

    public static double Chance(StunHit hit, double maxLife)
    {
        if (hit.Damage <= 0) return 0;
        if (hit.Guaranteed) return 100;
        double reduction = hit.ThresholdReductionPct;
        if (reduction > 75) reduction = 75 + 25 * (reduction - 75) / (reduction - 50);
        double chance = 200 * hit.Damage / (maxLife * (1 - reduction / 100));
        return chance <= 20 ? 0 : Math.Min(100, chance);
    }

    // Character.ot's object_inherent_melee_hit_stun_duration_+%_final is 50.
    static double Seconds(StunHit hit, SpawnedMonster m) => (hit.BaseSeconds > 0 ? hit.BaseSeconds : BaseSeconds) * Math.Max(0, 1 + hit.DurationPct / 100.0)
        * (hit.Melee ? 1.5 : 1) / Math.Max(.01, 1 + m.Mods.Effects[Stat.BaseStunRecoveryPlusPct] / 100.0);

    /// <summary>Rolls the stun; true when a new recovery started (<c>FlinchUntil</c>), to present. Call in the area's frame.</summary>
    public static bool TryFlinch(SpawnedMonster m, StunHit hit, double now, Random random)
    {
        if (m.Life == 0 || m.Dead || m.Invulnerable || m.HitAnimations is not { Count: > 0 }) return false;
        if (m.CannotBeStunned || m.Mods.Effects.CannotBeStunned || now < m.StunImmuneUntil) return false;   // Unwavering
        double threshold = m.MaxLife * Math.Max(.01, 1 + m.Mods.Effects[Stat.StunThresholdPlusPct] / 100d);
        if (random.NextDouble() * 100 >= Chance(hit, threshold)) return false;
        hit.OnStun?.Invoke();
        if (hit.Owner is { } owner)
        {
            Func<uint, int>? own = hit.Skill is { } skill ? key => skill.GetValueOrDefault(key) : null;
            Game.ChargesOf(owner).Enqueue(new ChargeEvent.Stun(hit.Melee, own));
        }
        // "Melee Hits which Stun have X% chance to Fortify" (wiki Fortification): a stun from a melee hit that did not
        // already Fortify it rolls the chance once and grants the hit's damage-based stacks. A stun during a running
        // recovery counts too, before the recovery shortcut below.
        if (hit.Melee && !hit.Fortified && hit.FortificationDamage > 0 && hit.Owner is { } fortifier)
            Game.FortificationFromMeleeStun(fortifier, hit.FortificationDamage, m.MaxLife, m.Rarity, random);
        double seconds = Seconds(hit, m);
        if (RandomRolls.Chance(hit.DoubleDurationChance, 100, random)) seconds *= 2;
        double nextUntil = now + seconds;
        // A new qualifying stun whose recovery extends beyond an active recovery refreshes/extends the flinch,
        // rather than silently dropping consecutive hit reactions while the monster is still recovering.
        if (nextUntil <= m.FlinchUntil) return false;
        // A walker on frames walks on until its client has the 0x1d1: the stop is placed in the frame that is written in
        // (Game.TakeHitPacket), which is a later one when the hit was committed ahead of its output (a projectile's).
        if (!m.Walk.OnFrames(now)) m.Walk.Cancel(now);
        m.LastTarget = null;   // after recovery the chase must acquire a fresh path, even to the same target cell
        if (m.SwingGeneration == m.ActionGeneration && !m.StageRunning) m.BusyUntil = now;
        m.SwingGeneration = null;
        m.ActionGeneration++;
        m.FlinchUntil = nextUntil;
        // An act boss's row states its own repeat immunity (2 s after the stun ends); a unique without one keeps the wiki's 4 s.
        double immunity = BossMods.StunImmunityMilliseconds(m.Variety) is int ms ? ms / 1000d
            : m.Rarity == MonsterRarity.Unique ? BossImmunitySeconds : 0;
        if (immunity > 0) m.StunImmuneUntil = m.FlinchUntil + immunity;
        return true;
    }
}

public static partial class Game
{
    /// <summary>The stun of the player's own hit that dealt <paramref name="damage"/>. A critical strike with cold damage
    /// taken cannot stun (wiki Stun). shortcut: not the wiki's exception for attackers whose critical strikes cannot
    /// inherently freeze, and only the main hand's local reduction, until those sources are in play. Call under the
    /// Monsters lock.</summary>
    public static StunHit OwnerStun(Player player, SpawnedMonster m, int damage, DamagePacket mitigated, bool melee, bool spell,
        bool critical, IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int>? own, uint? lifeBefore = null,
        int? localStunReduction = null, string? weaponClass = null)
    {
        int Get(uint key) => playerStats.GetValueOrDefault(key) + (own?.GetValueOrDefault(key) ?? 0);
        var taken = MonsterTakenComponents(m, mitigated, spell);
        double total = taken.MaxTotal;
        double stunning = total <= 0 || (critical && taken[DamageType.Cold].Max > 0) || Get(Stat.CannotStun) > 0 ? 0
            : HitReaction.StunningDamage(damage, taken[DamageType.Physical].Max / total, melee);
        int local = spell ? 0 : localStunReduction ?? MainHandLocalSources(player)?.GetValueOrDefault(Stat.LocalStunThresholdReductionPlusPct) ?? 0;
        // Sample before the caller applies this hit: a damaging hit can leave its previously full-life target injured.
        uint preLife = lifeBefore ?? m.Life;
        bool fullLife = preLife > 0 && preLife == m.MaxLife;
        bool tinctureWeapon = !spell && Get(Stat.TinctureApplied) != 0;
        int duration = Get(Stat.BaseStunDurationPlusPct)
            + (tinctureWeapon ? Get(Stat.StunDurationPlusPctWithTincturedWeapons) : 0)
            + (fullLife ? Get(Stat.StunDurationPlusPctVsEnemiesThatAreOnFullLife) : 0);
        int thresholdReduction = Get(Stat.BaseStunThresholdReductionPlusPct) + local
            + (tinctureWeapon ? Get(Stat.StunThresholdReductionPlusPctWithTincturedWeapons) : 0);
        if (!spell)
        {
            weaponClass ??= WeaponFor(player).ItemClass;
            if (melee && IsTwoHandMeleeClass(weaponClass))
                duration = ActorScale.Saturate((long)duration + Get(Stat.TwoHandedMeleeStunDurationPlusPct));
            if (WeaponTypeDamage.FamilyOf(weaponClass) == "mace")
                thresholdReduction = ActorScale.Saturate((long)thresholdReduction + Get(Stat.WhileUsingMaceStunThresholdReductionPlusPct));
        }
        bool guaranteed = stunning > 0 && fullLife && (Get(Stat.AlwaysStunEnemiesThatAreOnFullLife) > 0
            || tinctureWeapon && Get(Stat.AlwaysStunEnemiesThatAreOnFullLifeWithTincturedWeapons) > 0);
        return new(stunning, thresholdReduction, duration, melee, player, Guaranteed: guaranteed,
            Skill: own) { DoubleDurationChance = Get(Stat.ChanceToDoubleStunDurationPct) };
    }
}
