using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

/// <summary>A hit's stun inputs: its stunning damage (<see cref="HitReaction.StunningDamage"/>), the attacker's stun
/// threshold reduction and stun duration increases, whether it is melee (the duration), and the player whose "when you
/// stun" gains it feeds. Minions, explosions and /kill pass their plain damage and no owner.</summary>
readonly record struct StunHit(double Damage, int ThresholdReductionPct = 0, int DurationPct = 0, bool Melee = false,
    ClientState? Owner = null);

/// <summary>
/// A monster's stun, presented as its hit reaction (native Actor TakeHit recovery, sent by <c>SendMonsterFlinch</c>).
/// wiki Stun: chance = 200 x stunning damage / threshold, ignored at 20% or less, certain at half the threshold; a
/// reduction above 75% diminishes; duration = 350 ms x attacker's increases / defender's recovery; a boss cannot be
/// stunned for 4 s after a stun. Damage over time and reflect do not get here. An accepted stun stops the server's walk,
/// invalidates its ordinary attack and holds AI for that same recovery; the native ACT selects the TakeHit animation.
/// </summary>
static class HitReaction
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
        double reduction = hit.ThresholdReductionPct;
        if (reduction > 75) reduction = 75 + 25 * (reduction - 75) / (reduction - 50);
        double chance = 200 * hit.Damage / (maxLife * (1 - reduction / 100));
        return chance <= 20 ? 0 : Math.Min(100, chance);
    }

    // Character.ot's object_inherent_melee_hit_stun_duration_+%_final is 50.
    static double Seconds(StunHit hit, SpawnedMonster m) => BaseSeconds * Math.Max(0, 1 + hit.DurationPct / 100.0)
        * (hit.Melee ? 1.5 : 1) / Math.Max(.01, 1 + m.Mods.Effects["base_stun_recovery_+%"] / 100.0);

    /// <summary>Rolls the stun; true when a new recovery started (<c>FlinchUntil</c>), to present. Call under the Monsters lock.</summary>
    public static bool TryFlinch(SpawnedMonster m, StunHit hit, double now, Random random)
    {
        if (m.Life == 0 || m.Dead || m.Invulnerable || m.HitAnimations is not { Count: > 0 }) return false;
        if (m.CannotBeStunned || m.Mods.Effects.CannotBeStunned || now < m.StunImmuneUntil) return false;   // Unwavering
        if (random.NextDouble() * 100 >= Chance(hit, m.MaxLife)) return false;
        if (hit.Owner is { } owner) Game.ChargesOf(owner).Enqueue(new ChargeEvent.Stun());
        // shortcut: a stun during a running recovery counts for on-stun gains but neither restarts nor extends it (restarting
        // would cut the clip and repeat its SoundEvent), until a capture shows how the real server re-presents one.
        if (now < m.FlinchUntil) return false;
        var at = m.Walk.Position(now);
        m.Walk.Teleport(at.X, at.Y, now);
        m.LastTarget = null;   // after recovery the chase must acquire a fresh path, even to the same target cell
        if (m.SwingGeneration == m.ActionGeneration && !m.StageRunning) m.BusyUntil = now;
        m.SwingGeneration = null;
        m.ActionGeneration++;
        m.FlinchUntil = now + Seconds(hit, m);
        if (m.Rarity == MonsterRarity.Unique) m.StunImmuneUntil = m.FlinchUntil + BossImmunitySeconds;
        return true;
    }
}

static partial class Game
{
    /// <summary>The stun of the player's own hit that dealt <paramref name="damage"/>. A critical strike with cold damage
    /// taken cannot stun (wiki Stun). shortcut: not the wiki's exception for attackers whose critical strikes cannot
    /// inherently freeze, and only the main hand's local reduction, until those sources are in play. Call under the
    /// Monsters lock.</summary>
    internal static StunHit OwnerStun(ClientState conn, SpawnedMonster m, int damage, DamagePacket mitigated, bool melee, bool spell,
        bool critical, IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? own)
    {
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) + (own?.GetValueOrDefault(key) ?? 0) : 0;
        var taken = MonsterTakenComponents(m, mitigated, spell);
        double total = taken.MaxTotal;
        double stunning = total <= 0 || (critical && taken[DamageType.Cold].Max > 0) ? 0
            : HitReaction.StunningDamage(damage, taken[DamageType.Physical].Max / total, melee);
        int local = spell ? 0 : MainHandLocalSources(conn)?.GetValueOrDefault("local_stun_threshold_reduction_+%") ?? 0;
        return new(stunning, Get("base_stun_threshold_reduction_+%") + local, Get("base_stun_duration_+%"), melee, conn);
    }
}
