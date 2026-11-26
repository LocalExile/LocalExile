using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

// Scorch, Brittle and Sap, the non-damaging elemental ailments a hit inflicts only from a source that says so (wiki
// Ailment; ReminderText ReminderTextScorch, ReminderTextBrittle, ReminderTextSapped). They are kept in the framework's
// RuleAilments store under the kinds below, the strongest of each applying whoever inflicted it, and act where their
// stats do: Scorch on the elemental resistances (MonsterHitResistance, every damage source that passes a time), Brittle
// on the critical strike chance of every hit against the target, Sap on the damage the sapped monster's hits deal.
public static partial class Game
{
    public enum AlternateKind { Scorch, Brittle, Sap }

    /// <summary>One alternate ailment's rules. <paramref name="Base"/>: its effect at a hit equal to the ailment
    /// threshold, <c>Base × (damage / threshold)^0.4</c> floored (PoB CalcOffence: 50 for Scorch, 10 for Brittle, 100/3
    /// for Sap), up to <paramref name="Cap"/> (ReminderText: up to 30%, +6% and 20%; PoB data.nonDamagingAilment), with
    /// no minimum. <paramref name="WithoutHit"/>: its effect from a source that is no hit (ReminderTextScorchNonHit: 10%,
    /// ReminderTextBrittleNonHit: +2%, ReminderTextSapNonHit: 6%). Brittle's numbers are in hundredths of a percent (PoB
    /// precision 2), the unit of the stat its buff grants. <paramref name="StatName"/> names it in the stats (Brittle is
    /// frostburn), <paramref name="Can"/> in all_damage_can_X. Its buff is BuffDefinitions <paramref name="BuffRow"/>
    /// on the fixed <paramref name="Instance"/>; <paramref name="Sign"/> turns the magnitude into its GrantedStats value.
    /// <paramref name="OnPlayer"/>: whether the player side models its effect (Sap on the player's damage is not).</summary>
    public sealed record AlternateAilment(AlternateKind Kind, string StatName, string Past, DamageType Element, string Can,
        uint Chance, uint OnCritical, uint OnBlock, double Base, int Cap, int WithoutHit, ushort BuffRow, ushort Instance,
        int Sign, uint? SelfEffect, uint? Threshold, uint? TakenCan, uint? WhenHit, bool OnPlayer)
    {
        public uint Always { get; init; }
        public uint VirtualEffect { get; } = Stat.Of($"virtual_{StatName}_effect_+%");
        public uint VirtualDuration { get; } = Stat.Of($"virtual_{StatName}_duration_+%");
        public uint Never { get; } = Stat.Of($"never_{StatName}");
        public uint VirtualNever { get; } = Stat.Of($"virtual_never_{StatName}");
        public uint Immune { get; } = Stat.Of($"immune_to_{StatName}");
        public uint CannotBe { get; } = Stat.Of($"cannot_be_{Past}");
        public uint BaseCannotBe { get; } = Stat.Of($"base_cannot_be_{Past}");
        public uint VirtualCannotBe { get; } = Stat.Of($"virtual_cannot_be_{Past}");
        public uint Avoid { get; } = Stat.Of($"avoid_{StatName}_%");
        public uint BaseAvoid { get; } = Stat.Of($"base_avoid_{StatName}_%");
    }

    public static readonly AlternateAilment[] AlternateAilments =
    [
        // scorched: base_resist_all_elements_% ("+X% to all Elemental Resistances"), negative.
        new(AlternateKind.Scorch, "scorch", "scorched", DamageType.Fire, "scorch", Stat.Of("chance_to_scorch_%"),
            Stat.Of("scorch_on_critical_strike"), Stat.Of("chance_to_inflict_scorch_on_enemy_on_block_%"), 50, 30, 10, 1243,
            Player.FixedBuffInstances, -1, Stat.Of("scorch_ailment_effect_on_self_+%"), null, Stat.Of("all_damage_taken_can_scorch"),
            Stat.Of("chance_to_be_scorched_when_hit_%"), OnPlayer: true) { Always = Stat.VirtualAlwaysInflictScorch },
        // frostburn ("Brittle"): enemy_additional_critical_strike_chance_against_self, divided by 100 in its description.
        new(AlternateKind.Brittle, "frostburn", "frostburned", DamageType.Cold, "inflict_brittle", Stat.Of("chance_to_inflict_frostburn_%"),
            Stat.Of("frostburn_on_critical_strike"), Stat.Of("chance_to_inflict_brittle_on_enemy_on_block_%"), 1000, 600, 200, 1244,
            (ushort)(Player.FixedBuffInstances + 1), 1, null, Stat.Of("brittle_ailment_threshold_+%_final"), null, null, OnPlayer: true)
            { Always = Stat.VirtualAlwaysInflictFrostburn },
        // sapped: sapped_damage_+%_final, negative.
        new(AlternateKind.Sap, "sap", "sapped", DamageType.Lightning, "sap", Stat.Of("chance_to_inflict_sapped_%"),
            Stat.Of("sap_on_critical_strike"), Stat.Of("chance_to_inflict_sap_on_enemy_on_block_%"), 100 / 3.0, 20, 6, 1245,
            (ushort)(Player.FixedBuffInstances + 2), -1, null, Stat.Of("sap_ailment_threshold_+%_final"), Stat.Of("all_damage_taken_can_sap"),
            Stat.Of("chance_to_be_sapped_when_hit_%"), OnPlayer: false) { Always = Stat.VirtualAlwaysInflictSapped },
    ];

    /// <summary>Their base duration (ReminderText: "for 4 seconds"; PoB duration 4).</summary>
    public const double AlternateAilmentSeconds = 4;

    // "Critical Strikes inflict Scorch, Brittle and Sapped" (ReminderTextKeystoneSecretsOfSuffering), with "Cannot Ignite,
    // Chill, Freeze or Shock" (SecretsOfSufferingProhibitions, handed over in GameModule.Load). The cluster jewel's
    // keystone and Replica Malachai's Artifice's socketed gems (the skill's own stat) both carry it.
    public const uint SecretsOfSuffering = Stat.KeystoneSecretsOfSuffering;
    internal static readonly (uint Stat, string Ailment)[] SecretsOfSufferingProhibitions =
        [(SecretsOfSuffering, "ignite"), (SecretsOfSuffering, "chill"), (SecretsOfSuffering, "freeze"), (SecretsOfSuffering, "shock")];
    // Stormblood and its cold sibling: all damage from critical strikes can apply lightning (cold) ailments during the
    // effect, Sap (Brittle) among them; a source still has to inflict it.
    internal static readonly (uint Local, uint Stat)[] AlternateAilmentFlaskFlags =
    [
        (Stat.LocalUniqueFlaskAllDamageFromCriticalStrikesCanSapDuringEffect, Stat.AllDamageFromCriticalStrikesCanSap),
        (Stat.LocalUniqueFlaskAllDamageFromCriticalStrikesCanInflictBrittleDuringEffect,
            Stat.AllDamageFromCriticalStrikesCanInflictBrittle),
    ];
    const uint AnyAlternateChance = Stat.ChanceToInflictScorchBrittleSapPct;

    public static AlternateAilment AlternateAilmentOf(AlternateKind kind) => AlternateAilments[(int)kind];

    /// <summary>The effect a hit's <paramref name="damage"/> of the ailment gives against <paramref name="threshold"/>,
    /// through the effect <paramref name="modifiers"/>, floored and capped; 0 when it gives none.</summary>
    public static int AlternateMagnitude(AlternateAilment a, double damage, double threshold, double modifiers) =>
        damage <= 0 || threshold <= 0 ? 0
            : (int)Math.Min(a.Cap, Math.Floor(a.Base * Math.Pow(damage / threshold, .4) * Math.Max(0, modifiers)));

    // The attacker's increased effect (the panel's virtual_X_effect_+%: the ailment's own, its element's and "Non-Damaging
    // Ailments") and the target's on itself.
    static double AlternateEffectModifier(AlternateAilment a, Func<uint, int> attacker, Func<uint, int> target) =>
        Math.Max(0, 1 + attacker(a.VirtualEffect) / 100.0) * AlternateEffectOnSelf(a, target);

    static double AlternateEffectOnSelf(AlternateAilment a, Func<uint, int> target) =>
        Math.Max(0, 1 + (target(Stat.NonDamagingAilmentEffectPlusPctOnSelf) + (a.SelfEffect is uint self ? target(self) : 0)) / 100.0);

    // The attacker's duration modifiers (the panel's virtual_X_duration_+%: elemental, all and the element's ailments').
    static double AlternateSeconds(AlternateAilment a, Func<uint, int> attacker) =>
        AlternateAilmentSeconds * Math.Max(0, 1 + attacker(a.VirtualDuration) / 100.0);

    // Elemental Focus's cannot_inflict_status_ailments and the never_X stats.
    static bool CannotInflictAlternate(AlternateAilment a, Func<uint, int> attacker) =>
        attacker(Stat.CannotInflictStatusAilments) != 0 || attacker(a.Never) != 0 || attacker(a.VirtualNever) != 0;

    // The target's immunities to it.
    static bool AlternateImmune(AlternateAilment a, Func<uint, int> target) =>
        target(a.Immune) != 0 || target(a.CannotBe) != 0 || target(a.BaseCannotBe) != 0
        || target(a.VirtualCannotBe) != 0 || a.Kind == AlternateKind.Brittle && target(Stat.CannotGainBrittle) != 0;

    // The damage that can inflict it: its own element's, and another type's with all_damage_can_X, the type's
    // X_damage_can_X or, on a critical strike, all_damage_from_critical_strikes_can_X (Stormblood). Runs per hit with a
    // source: the stat ids are built once (AlternateCanNames), and no LINQ.
    static double AlternateInflicting(AlternateAilment a, DamagePacket damage, Func<uint, int> attacker, bool critical)
    {
        var names = AlternateCanNames[(int)a.Kind];
        bool all = attacker(names.All) != 0 || critical && attacker(names.FromCritical) != 0;
        double sum = 0;
        foreach (var c in damage.Components)
            if (c.Type == a.Element || all || attacker(names.PerType[(int)c.Type]) != 0) sum += c.Range.Min;
        return sum;
    }

    static readonly (uint All, uint FromCritical, uint[] PerType)[] AlternateCanNames = AlternateAilments
        .Select(a => (Stat.Of($"all_damage_can_{a.Can}"), Stat.Of($"all_damage_from_critical_strikes_can_{a.Can}"),
            Enum.GetValues<DamageType>().Select(t => Stat.Of($"{t.ToString().ToLowerInvariant()}_damage_can_{a.Can}")).ToArray()))
        .ToArray();

    /// <summary>
    /// The Scorch, Brittle and Sap one hit inflicts on <paramref name="target"/>'s store, from the damage it took per
    /// type (<paramref name="damage"/>, as chill and shock read it) against its ailment threshold (Brittle's and Sap's
    /// raised by the target's threshold stat): a critical strike with Secrets of Suffering or the X_on_critical_strike
    /// stat, else the chance (chance_to_scorch_% and the like, plus chance_to_inflict_scorch_brittle_sap_%), with damage
    /// that can inflict it. Each lasts 4 s through the duration modifiers and is kept beside the others of its kind (the
    /// strongest applies), so a weaker one from another player never replaces it. Returns the ones added. A chance
    /// rolls only when there is one, so a hit without a source draws nothing.
    /// </summary>
    public static List<(AlternateAilment A, RuleAilments.Instance Instance)> AlternateAilmentsOnHit(RuleAilments store,
        DamagePacket damage, bool critical, double threshold, double now, Func<uint, int> attacker, Func<uint, int> target,
        Random random)
    {
        var added = new List<(AlternateAilment, RuleAilments.Instance)>();
        if (threshold <= 0) return added;
        bool secrets = critical && attacker(SecretsOfSuffering) != 0;
        int any = attacker(AnyAlternateChance);
        foreach (var a in AlternateAilments)
        {
            // Per hit (Hot paths): a hit without a source of this ailment stops here, before any other stat is read.
            bool sure = secrets || attacker(a.Always) != 0 || critical && attacker(a.OnCritical) != 0;
            int chance = attacker(a.Chance) + any;
            if (!sure && chance <= 0) continue;
            double inflicting = AlternateInflicting(a, damage, attacker, critical);
            if (inflicting <= 0) continue;
            if (!(sure || RandomRolls.Chance(chance, 100, random))
                || CannotInflictAlternate(a, attacker) || AlternateImmune(a, target)) continue;
            double asThough = Math.Max(0, 1 + attacker(Stat.NonDamagingAilmentsAsThoughDamagePlusPctFinal) / 100.0);
            double scaled = threshold * (a.Threshold is uint stat ? Math.Max(0, 1 + target(stat) / 100.0) : 1);
            int magnitude = AlternateMagnitude(a, inflicting * asThough, scaled, AlternateEffectModifier(a, attacker, target));
            if (store.Add((int)a.Kind, now, AlternateSeconds(a, attacker), magnitude) is { } instance) added.Add((a, instance));
        }
        return added;
    }

    /// <summary>The Scorch, Brittle and Sap a player's hit inflicts on a monster (<see cref="AlternateAilmentsOnHit"/>),
    /// none during an encounter's grace (SpawnedMonster.EntranceGrace). Under the Monsters lock.</summary>
    public static List<(AlternateAilment A, RuleAilments.Instance Instance)> AlternateAilmentsOnHitLocked(SpawnedMonster m,
        DamagePacket damage, bool critical, double now, Func<uint, int> attacker, Random random) =>
        m.EntranceGrace ? [] : AlternateAilmentsOnHit(m.RuleAilments, damage, critical, m.MaxLife, now, attacker, name => m.Effects[name], random);

    /// <summary>A Scorch, Brittle or Sap from a source that is no hit ("Scorch Enemies when you Block their Damage"):
    /// the ailment's set effect (10%, +2%, 6%) through the effect modifiers, up to its cap, for 4 s.</summary>
    public static RuleAilments.Instance? AlternateAilmentWithoutHit(RuleAilments store, AlternateKind kind, double now,
        Func<uint, int> attacker, Func<uint, int> target)
    {
        var a = AlternateAilmentOf(kind);
        if (CannotInflictAlternate(a, attacker) || AlternateImmune(a, target)) return null;
        int magnitude = (int)Math.Min(a.Cap, Math.Floor(a.WithoutHit * AlternateEffectModifier(a, attacker, target)));
        return store.Add((int)kind, now, AlternateSeconds(a, attacker), magnitude);
    }

    /// <summary>The player's "X% chance to Scorch Enemies when you Block their Damage" (and Brittle's, Sap's) on the
    /// monster whose hit was blocked, each on its own roll of Random.Shared. Under the Monsters lock.</summary>
    static void AlternateAilmentsOnBlockLocked(SpawnedMonster m, Func<uint, int> player, double now, Func<uint, bool>? ruled = null)
    {
        foreach (var a in AlternateAilments)
            if (ruled?.Invoke(a.OnBlock) != true && RandomRolls.Chance(player(a.OnBlock), 100, Random.Shared) && !m.EntranceGrace)
                AlternateAilmentWithoutHit(m.RuleAilments, a.Kind, now, player, name => m.Effects[name]);
    }

    /// <summary>Scorch: the monster's fire, cold and lightning resistances lowered by its strongest scorch (a negative
    /// number; 0 without a time).</summary>
    public static int ScorchedResistance(SpawnedMonster m, DamageType type, double now) =>
        DamageTypes.IsElemental(type) ? -(int)m.RuleAilments.Magnitude((int)AlternateKind.Scorch, now) : 0;

    /// <summary>Brittle: the critical strike chance (hundredths of a percent) every hit against the monster has in addition.</summary>
    public static int BrittleCriticalChance(SpawnedMonster m, double now) => (int)m.RuleAilments.Magnitude((int)AlternateKind.Brittle, now);

    /// <summary>Sap: the share of their damage the monster's hits still deal (ReminderTextSapped "deal up to 20% less
    /// Damage"; PoB Damage MORE).</summary>
    public static double SappedDamageFactor(SpawnedMonster m, double now) =>
        Math.Max(0, 1 - m.RuleAilments.Magnitude((int)AlternateKind.Sap, now) / 100.0);

    /// <summary>The buff of an alternate ailment (0x1c1), with its magnitude as its one GrantedStats value. The scorched,
    /// frostburn and sapped rows have the chilled and shocked rows' shape (IsRecovery, one value, the +0xff bool so
    /// +0x82 is read and sent 0); no duration, as the server ends it with 0x1c2.</summary>
    public static byte[] AlternateAilmentBuff(uint id, AlternateAilment a, int magnitude, uint source = 0) =>
        InstanceEntities.AddBuff(id, a.Instance, a.BuffRow, source, u82: 0, values: [unchecked((uint)(a.Sign * magnitude))]);

    /// <summary>A magnitude as the logs print it.</summary>
    public static string AlternateMagnitudeText(AlternateAilment a, int magnitude) =>
        a.Kind == AlternateKind.Brittle ? $"+{magnitude / 100.0:0.##}% critical strike chance" : $"{magnitude}%";

    /// <summary>The 0x1c2 and 0x1c1 that show what changed in a store at <paramref name="now"/> on object
    /// <paramref name="id"/> (a monster's, or the player's own).</summary>
    public static List<(byte[] Packet, string Log)> AlternateAilmentBuffs(uint id, string who, RuleAilments store, double now)
    {
        var packets = new List<(byte[], string)>();
        foreach (var (kind, was, magnitude) in store.Present(now))
        {
            if (kind < 0 || kind >= AlternateAilments.Length) continue;
            var a = AlternateAilments[kind];
            if (was > 0) packets.Add((InstanceEntities.RemoveBuff(id, a.Instance), $"0x01c2 {who} {a.Kind} ends"));
            if (magnitude > 0) packets.Add((AlternateAilmentBuff(id, a, magnitude), $"0x01c1 {who} {a.Kind} {AlternateMagnitudeText(a, magnitude)}"));
        }
        return packets;
    }

    // ---- On the player ----

    /// <summary>
    /// A monster hit that landed on the player inflicts Scorch or Brittle only from a source: the attack's skill's chance
    /// (<see cref="PlayerElementalAilments.HitSettings.SourceStats"/>), the monster's own (a Simulacrum wave's "Monsters
    /// have X% chance to inflict Scorch") or the player's "X% chance to be Scorched when Hit" (an altar's downside, with
    /// "All Damage taken from Hits can Scorch you": every type counts). The effect reads the damage taken of the
    /// ailment's element against the player's maximum life, through the player's effect on self; 4 s through the
    /// player's elemental ailment duration on self; avoided by avoid_X_% and Avoid Elemental Ailments
    /// (ReminderTextElementalAilments lists them). Sap is not inflicted on the player: its less damage dealt is not
    /// modelled for the player's hits. Under the Monsters lock.
    /// </summary>
    public static void AlternateAilmentsOnPlayerLocked(Player player, SpawnedMonster m, DamagePacket taken, bool blocked,
        double now, IReadOnlyDictionary<uint, int> stats, PlayerElementalAilments.HitSettings settings, bool critical = false)
    {
        if (blocked || player.Immune || player.Closed || player.LeavingArea || player.PlayerLife == 0 || InGracePeriod(player)
            || player.PlayerMaxLife == 0) return;
        int Get(uint key) => stats.GetValueOrDefault(key);
        int critAvoid = critical ? Math.Max(0, Get(Stat.AvoidAilmentsPctFromCrit)) : 0;
        foreach (var a in AlternateAilments)
        {
            if (!a.OnPlayer) continue;
            int chance = (settings.SourceStats is { } skill ? skill(a.Chance) + skill(AnyAlternateChance) : 0)
                + m.Effects[a.Chance] + (a.WhenHit is uint whenHit ? Get(whenHit) : 0);
            double damage = a.TakenCan is uint can && Get(can) != 0 ? taken.MinTotal : taken[a.Element].Min;
            if (chance <= 0 || damage <= 0 || !RandomRolls.Chance(chance, 100, player.IncomingHitRandom)) continue;
            int magnitude = AlternateMagnitude(a, damage, player.PlayerMaxLife, AlternateEffectOnSelf(a, Get));
            AlternateOnPlayerLocked(player, a, magnitude, now, Get, player.IncomingHitRandom, critAvoid);
        }
    }

    // One Scorch or Brittle on the player: its immunity, then the avoidance roll, then its duration on self.
    static void AlternateOnPlayerLocked(Player player, AlternateAilment a, double magnitude, double now, Func<uint, int> get,
        Random random, int critAvoid = 0)
    {
        if (!a.OnPlayer || magnitude <= 0 || AlternateImmune(a, get)
            || RandomRolls.Chance(get(a.Avoid) + get(a.BaseAvoid) + get(Stat.AvoidAllElementalStatusPct) + critAvoid, 100, random))
            return;
        double seconds = AlternateAilmentSeconds
            * Math.Max(0, 1 - (get(Stat.SelfElementalStatusDurationMinusPct) - get(Stat.BaseAllAilmentDurationOnSelfPlusPct)) / 100.0);
        player.RuleAilments.Add((int)a.Kind, now, seconds, magnitude);
    }

    /// <summary>Mirrored: the Scorch and Brittle a hit added act on the player as well, the effect they landed with
    /// through the player's effect on self, up to the cap. Under the Monsters lock.</summary>
    static void ReflectAlternateAilmentsLocked(Player player, List<(AlternateAilment A, RuleAilments.Instance Instance)> added,
        double now, Func<uint, int> playerStats, Random random)
    {
        if (player.PlayerLife == 0) return;
        foreach (var (a, instance) in added)
            AlternateOnPlayerLocked(player, a, Math.Min(a.Cap, Math.Floor(instance.Magnitude * AlternateEffectOnSelf(a, playerStats))),
                now, playerStats, random);
    }

    /// <summary>Scorch on the player: its fire, cold and lightning resistances lowered by its strongest scorch.</summary>
    public static int PlayerScorchedResistance(Player player, DamageType type) =>
        DamageTypes.IsElemental(type) ? -(int)player.RuleAilments.Magnitude((int)AlternateKind.Scorch, AreaTime(player)) : 0;

    /// <summary>Brittle on the player: the critical strike chance (hundredths) monster hits against it have in addition.</summary>
    public static int PlayerBrittleCriticalChance(Player player, double now) =>
        (int)player.RuleAilments.Magnitude((int)AlternateKind.Brittle, now);
}
