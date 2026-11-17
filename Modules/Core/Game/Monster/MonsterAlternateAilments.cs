using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using Kind = MonsterElementalAilments.Kind;

// Scorch, Brittle and Sap, the non-damaging elemental ailments a hit inflicts only from a source that says so (wiki
// Ailment; ReminderText ReminderTextScorch, ReminderTextBrittle, ReminderTextSapped): on monsters they are kept with chill,
// freeze and shock (MonsterElementalAilments), the strongest of each applying whoever inflicted it, and they act where
// their stats do: Scorch on the monster's elemental resistances (MonsterHitResistance, hits and damage over time), Brittle
// on the critical strike chance of hits against it (CriticalChanceOn) and Sap on the damage its hits deal.
public static partial class Game
{
    /// <summary>One alternate ailment's rules. <paramref name="Base"/>: its effect at a hit equal to the ailment
    /// threshold, <c>Base × (damage / threshold)^0.4</c> floored (PoB CalcOffence: 50 for Scorch, 10 for Brittle, 100/3
    /// for Sap), up to <paramref name="Cap"/> (ReminderText: up to 30%, +6% and 20%; PoB data.nonDamagingAilment).
    /// <paramref name="WithoutHit"/>: its effect from a source that is no hit (ReminderTextScorchNonHit: 10%,
    /// ReminderTextBrittleNonHit: +2%, ReminderTextSapNonHit: 6%). Brittle's numbers are in hundredths of a percent
    /// (PoB precision 2), the unit of the enemy_additional_critical_strike_chance_against_self its buff grants.
    /// <paramref name="Can"/> names it in all_damage_can_X and all_damage_from_critical_strikes_can_X. Its buff is
    /// BuffDefinitions <paramref name="BuffRow"/> (scorched, frostburn, sapped).</summary>
    public sealed record AlternateAilment(Kind Kind, PlayerElementalAilments.Kind PlayerKind, DamageType Element, string Can,
        string Chance, string OnCritical, string OnBlock, double Base, int Cap, int WithoutHit, ushort BuffRow,
        string? SelfEffect, string? Threshold, string? TakenCan, string? WhenHit)
    {
        /// <summary>Its name in the stats (scorch, frostburn, sap).</summary>
        public string Stat => MonsterElementalAilments.StatName(Kind);
    }

    public static readonly AlternateAilment[] AlternateAilments =
    [
        new(Kind.Scorch, PlayerElementalAilments.Kind.Scorch, DamageType.Fire, "scorch", "chance_to_scorch_%",
            "scorch_on_critical_strike", "chance_to_inflict_scorch_on_enemy_on_block_%", 50, 30, 10, 1243,
            "scorch_ailment_effect_on_self_+%", null, "all_damage_taken_can_scorch", "chance_to_be_scorched_when_hit_%"),
        new(Kind.Brittle, PlayerElementalAilments.Kind.Brittle, DamageType.Cold, "inflict_brittle", "chance_to_inflict_frostburn_%",
            "frostburn_on_critical_strike", "chance_to_inflict_brittle_on_enemy_on_block_%", 1000, 600, 200, 1244,
            null, "brittle_ailment_threshold_+%_final", null, null),
        new(Kind.Sap, PlayerElementalAilments.Kind.Sap, DamageType.Lightning, "sap", "chance_to_inflict_sapped_%",
            "sap_on_critical_strike", "chance_to_inflict_sap_on_enemy_on_block_%", 100 / 3.0, 20, 6, 1245,
            null, "sap_ailment_threshold_+%_final", "all_damage_taken_can_sap", "chance_to_be_sapped_when_hit_%"),
    ];

    /// <summary>Their base duration (ReminderText: "for 4 seconds"; PoB duration 4).</summary>
    public const double AlternateAilmentSeconds = 4;

    // "Critical Strikes inflict Scorch, Brittle and Sapped" (ReminderTextKeystoneSecretsOfSuffering), with "Cannot Ignite,
    // Chill, Freeze or Shock" (handed over to MonsterElementalAilments in GameModule.Load). The cluster jewel's keystone and
    // Replica Malachai's Artifice's socketed gems (the skill's own stat) both carry it.
    public const string SecretsOfSuffering = "keystone_secrets_of_suffering";
    const string AnyAlternateChance = "chance_to_inflict_scorch_brittle_sap_%";

    public static AlternateAilment AlternateAilmentOf(Kind kind) => AlternateAilments[kind - Kind.Scorch];

    /// <summary>The effect a hit's <paramref name="damage"/> of the ailment gives against <paramref name="threshold"/>,
    /// through the effect <paramref name="modifiers"/>, floored and capped; 0 when it gives none (no minimum).</summary>
    public static int AlternateMagnitude(AlternateAilment a, double damage, double threshold, double modifiers) =>
        damage <= 0 || threshold <= 0 ? 0
            : (int)Math.Min(a.Cap, Math.Floor(a.Base * Math.Pow(damage / threshold, .4) * Math.Max(0, modifiers)));

    // The attacker's increased effect (the panel's virtual_X_effect_+%: the ailment's own, its element's and "Non-Damaging
    // Ailments") and the target's on itself.
    static double AlternateEffectModifier(AlternateAilment a, Func<string, int> attacker, Func<string, int> target) =>
        Math.Max(0, 1 + attacker($"virtual_{a.Stat}_effect_+%") / 100.0) * AlternateEffectOnSelf(a, target);

    static double AlternateEffectOnSelf(AlternateAilment a, Func<string, int> target) =>
        Math.Max(0, 1 + (target("non_damaging_ailment_effect_+%_on_self") + (a.SelfEffect is { } self ? target(self) : 0)) / 100.0);

    // Elemental Focus's cannot_inflict_status_ailments, the never_X stats; never_ignite_chill_freeze_shock does not reach them.
    static bool CannotInflictAlternate(AlternateAilment a, Func<string, int> attacker) =>
        MonsterElementalAilments.CannotInflict(a.Stat, attacker) || attacker($"virtual_never_{a.Stat}") != 0;

    // The target's own "cannot" forms beside immune_to_X and cannot_be_X (MonsterElementalAilments checks those).
    static bool AlternateBlocked(AlternateAilment a, Func<string, int> target) =>
        target($"virtual_cannot_be_{MonsterElementalAilments.Past(a.Kind)}") != 0 || a.Kind == Kind.Brittle && target("cannot_gain_brittle") != 0;

    /// <summary>
    /// The Scorch, Brittle and Sap one hit inflicts on a monster, from the damage it took per type
    /// (<paramref name="damage"/>, as chill and shock read it) against its ailment threshold (Brittle's and Sap's
    /// raised by the target's threshold stat): a critical strike with Secrets of Suffering or the X_on_critical_strike
    /// stat, else the chance (chance_to_scorch_% and the like, plus chance_to_inflict_scorch_brittle_sap_%), with damage
    /// that can inflict it (its element; others with all_damage_can_X, all_damage_from_critical_strikes_can_X). Each lasts
    /// 4 s through the duration modifiers and is kept beside the others of its kind (the strongest applies), so a
    /// weaker one from another player never replaces it. Returns the ones added. A chance rolls only when there is one,
    /// so a hit without a source draws nothing.
    /// </summary>
    public static List<MonsterElementalAilments.Effect> AlternateAilmentsOnHit(MonsterElementalAilments ailments, DamagePacket damage,
        bool critical, double threshold, double now, Func<string, int> attacker, Func<string, int> target, Random random)
    {
        var added = new List<MonsterElementalAilments.Effect>();
        if (threshold <= 0) return added;
        bool secrets = critical && attacker(SecretsOfSuffering) != 0;
        int any = attacker(AnyAlternateChance);
        double asThough = Math.Max(0, 1 + attacker("non_damaging_ailments_as_though_damage_+%_final") / 100.0);
        foreach (var a in AlternateAilments)
        {
            double inflicting = MonsterElementalAilments.DamageThatCan(a.Can, a.Element, damage, attacker, critical);
            if (inflicting <= 0) continue;
            if (!(secrets || critical && attacker(a.OnCritical) != 0 || RandomRolls.Chance(attacker(a.Chance) + any, 100, random))
                || CannotInflictAlternate(a, attacker) || AlternateBlocked(a, target)) continue;
            double scaled = threshold * (a.Threshold is { } stat ? Math.Max(0, 1 + target(stat) / 100.0) : 1);
            int magnitude = AlternateMagnitude(a, inflicting * asThough, scaled, AlternateEffectModifier(a, attacker, target));
            if (ailments.Add(new(a.Kind, now, now + AlternateAilmentSeconds, magnitude), attacker, target) is { } effect)
                added.Add(effect);
        }
        return added;
    }

    /// <summary>A Scorch, Brittle or Sap from a source that is no hit ("Scorch Enemies when you Block their Damage"):
    /// the ailment's set effect (10%, +2%, 6%) through the effect modifiers, up to its cap, for 4 s.</summary>
    public static MonsterElementalAilments.Effect? AlternateAilmentWithoutHit(MonsterElementalAilments ailments, Kind kind,
        double now, Func<string, int> attacker, Func<string, int> target)
    {
        var a = AlternateAilmentOf(kind);
        if (CannotInflictAlternate(a, attacker) || AlternateBlocked(a, target)) return null;
        int magnitude = (int)Math.Min(a.Cap, Math.Floor(a.WithoutHit * AlternateEffectModifier(a, attacker, target)));
        return ailments.Add(new(kind, now, now + AlternateAilmentSeconds, magnitude), attacker, target);
    }

    /// <summary>The player's "X% chance to Scorch Enemies when you Block their Damage" (and Brittle's, Sap's) on the
    /// monster whose hit was blocked, each on its own roll of Random.Shared. Under the Monsters lock.</summary>
    static void AlternateAilmentsOnBlockLocked(SpawnedMonster m, Func<string, int> player, double now)
    {
        foreach (var a in AlternateAilments)
            if (RandomRolls.Chance(player(a.OnBlock), 100, Random.Shared))
                AlternateAilmentWithoutHit(m.Ailments, a.Kind, now, player, name => m.Effects[name]);
    }

    /// <summary>Scorch: the monster's fire, cold and lightning resistances lowered by its strongest scorch (a negative
    /// number; 0 without a time).</summary>
    public static int ScorchedResistance(SpawnedMonster m, DamageType type, double now) =>
        DamageTypes.IsElemental(type) ? -(int)m.Ailments.Magnitude(Kind.Scorch, now) : 0;

    /// <summary>Brittle: the critical strike chance (hundredths of a percent) hits against the monster have in addition.</summary>
    public static int BrittleCriticalChance(SpawnedMonster m, double now) => (int)m.Ailments.Magnitude(Kind.Brittle, now);

    /// <summary>The less damage the monster's hits deal: Sap's (ReminderTextSapped "deal up to 20% less Damage") and Glacial
    /// Wave's chill's, multiplied (PoB Damage MORE both).</summary>
    public static double MonsterDamageDealtFactor(SpawnedMonster m, double now) =>
        m.Ailments.ChillDamageDealtFactor(now) * Math.Max(0, 1 - m.Ailments.Magnitude(Kind.Sap, now) / 100.0);

    /// <summary>The value an ailment's buff carries, in the unit and sign of the stat it grants: Scorch's
    /// base_resist_all_elements_% and Sap's sapped_damage_+%_final are negative, Brittle's
    /// enemy_additional_critical_strike_chance_against_self is in hundredths.</summary>
    public static int AilmentBuffValue(Kind kind, int magnitude) => kind is Kind.Scorch or Kind.Sap ? -magnitude : magnitude;

    /// <summary>A magnitude as the logs print it.</summary>
    public static string AilmentMagnitudeText(Kind kind, int magnitude) =>
        kind == Kind.Brittle ? $"+{magnitude / 100.0:0.##}% critical strike chance" : $"{magnitude}%";

    // ---- On the player ----

    /// <summary>
    /// A monster hit that landed on the player inflicts Scorch, Brittle or Sap only from a source: the attack's chance
    /// (its skill's chance_to_scorch_% and the like: <see cref="PlayerElementalAilments.HitSettings"/>), the monster's
    /// own (a Simulacrum wave's "Monsters have X% chance to inflict Scorch") or the player's "X% chance to be Scorched when
    /// Hit" (an altar's downside, with "All Damage taken from Hits can Scorch you": every type counts). The effect reads
    /// the damage taken of the ailment's element against the player's maximum life, through the player's effect on
    /// self, for 4 s through the player's immunity, avoidance (Avoid Elemental Ailments counts: the panel's avoid_X_%)
    /// and elemental ailment duration on self. Under the Monsters lock.
    /// </summary>
    public static void AlternateAilmentsOnPlayerLocked(ClientState conn, SpawnedMonster m, DamagePacket taken, bool blocked,
        double now, IReadOnlyDictionary<uint, int> stats, PlayerElementalAilments.HitSettings settings)
    {
        if (blocked || conn.Immune || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || InGracePeriod(conn)
            || conn.PlayerMaxLife == 0) return;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        var rolls = new List<(AlternateAilment A, double Damage)>();
        foreach (var a in AlternateAilments)
        {
            int chance = a.Kind switch
            {
                Kind.Scorch => settings.ScorchChance, Kind.Brittle => settings.BrittleChance, _ => settings.SapChance,
            } + m.Effects[a.Chance] + (a.WhenHit is { } whenHit ? Get(whenHit) : 0);
            if (chance <= 0) continue;
            double damage = a.TakenCan is { } can && Get(can) != 0 ? taken.MinTotal : taken[a.Element].Min;
            if (damage > 0 && RandomRolls.Chance(chance, 100, conn.IncomingHitRandom)) rolls.Add((a, damage));
        }
        if (rolls.Count == 0) return;
        // "Reduced Duration of Elemental Ailments on you" and "of Ailments on you" (no stat names Scorch's own).
        double seconds = AlternateAilmentSeconds
            * Math.Max(0, 1 - (Get("self_elemental_status_duration_-%") - Get("base_all_ailment_duration_on_self_+%")) / 100.0);
        ApplyPlayerAilmentsLocked(conn, now, ailments =>
        {
            foreach (var (a, damage) in rolls)
                if (AlternateMagnitude(a, damage, conn.PlayerMaxLife, AlternateEffectOnSelf(a, Get)) is > 0 and var magnitude
                    && !AlternateBlocked(a, Get))
                    ailments.Inflict(a.PlayerKind, magnitude, seconds, now, Get, conn.IncomingHitRandom);
        });
    }

    /// <summary>A reflected Scorch, Brittle or Sap on the player (a Mirrored monster): the effect it landed with, through
    /// the player's effect on self, up to the cap.</summary>
    static double AlternateOnSelf(Kind kind, double magnitude, Func<string, int> player)
    {
        var a = AlternateAilmentOf(kind);
        return Math.Min(a.Cap, Math.Floor(magnitude * AlternateEffectOnSelf(a, player)));
    }

    /// <summary>Scorch on the player: its fire, cold and lightning resistances lowered by its strongest scorch.</summary>
    public static int PlayerScorchedResistance(ClientState conn, DamageType type) =>
        DamageTypes.IsElemental(type) && conn.ElementalAilments.HasEffects
            ? -(int)conn.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Scorch, conn.Clock.Elapsed.TotalSeconds) : 0;

    /// <summary>Brittle on the player: the critical strike chance (hundredths) monster hits against it have in addition.</summary>
    public static int PlayerBrittleCriticalChance(ClientState conn, double now) =>
        conn.ElementalAilments.HasEffects ? (int)conn.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Brittle, now) : 0;

    /// <summary>The scorched, brittle and sapped buffs on the player, as on monsters (AilmentBuff, on the fixed instances):
    /// a kind whose strongest magnitude differs from <paramref name="shown"/> (what the client was last sent, by
    /// <see cref="AlternateAilments"/> index, updated here) is sent again, the old one off first.</summary>
    public static List<(byte[] Packet, string Log)> PlayerAlternateAilmentBuffs(ClientState conn, int[] shown, double now)
    {
        var packets = new List<(byte[], string)>();
        for (int i = 0; i < AlternateAilments.Length; i++)
        {
            var a = AlternateAilments[i];
            int magnitude = conn.ElementalAilments.HasEffects ? (int)conn.ElementalAilments.Magnitude(a.PlayerKind, now) : 0;
            if (magnitude == shown[i]) continue;
            ushort instance = MonsterElementalAilments.Instance(a.Kind);
            if (shown[i] > 0) packets.Add((InstanceEntities.RemoveBuff(PlayerId, instance), $"0x01c2 player {a.Kind} ends"));
            if (magnitude > 0)
                packets.Add((AilmentBuff(PlayerId, instance, a.Kind, magnitude), $"0x01c1 player {a.Kind} {AilmentMagnitudeText(a.Kind, magnitude)}"));
            shown[i] = magnitude;
        }
        return packets;
    }
}
