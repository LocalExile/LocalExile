using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    static bool Has(IReadOnlyDictionary<uint, int> player, uint stat) =>
        stat is var key && player.GetValueOrDefault(key) != 0;
    public static bool BleedReflected(IReadOnlyDictionary<uint, int> player) => Has(player, Stat.BleedingReflectedToSelf)
        && !Has(player, Stat.CannotGainBleeding) && !Has(player, Stat.VirtualImmuneToBleeding)
        && !Has(player, Stat.ImmuneToBleeding);

    /// <summary>The wiki's Bleeding: a moving enemy takes 200% more damage from it (the trial spike traps' bleed too).</summary>
    public const double BleedMovingMore = 3;

    /// <summary>Crimson Dance: "You can inflict Bleeding on an Enemy up to 8 times" (ReminderTextKeystoneCrimsonDance).</summary>
    public const int CrimsonDanceStacks = 8;

    /// <summary>
    /// What an attack's hit can bleed with (wiki Bleeding), in the shape of an ignite (<see cref="MonsterIgnites.Settings"/>):
    /// the context's bleeding rows (<see cref="AilmentPanelStats"/>: 70% of the hit's physical damage per second through
    /// the ailment's buckets) and the chance from bleed_on_hit_with_attacks_% and the weapon's
    /// local_chance_to_bleed_on_hit_% and fixed 25% / 50% (the panel's virtual_total_main_hand_chance_to_bleed_on_hit_%),
    /// with bleed_on_melee_attack_chance_% for a melee and bleed_on_bow_attack_chance_% for a bow attack; global_bleed_on_hit
    /// or the weapon's local_bleed_on_hit always bleed. A critical strike adds the "on Critical Strike" chances (PoB
    /// BleedChanceOnCrit): bleed_on_crit_%_with_attacks, the weapon's local_bleed_on_critical_strike_chance_% and fixed
    /// 50%, and for a melee attack bleed_on_melee_crit_chance_% (bleed_on_melee_critical_strike: always). A hit aggravates
    /// the bleeds already on its target at base_aggravate_bleeding_on_attack_hit_chance_% (a critical strike adds
    /// aggravate_bleeding_on_attack_crit_chance_%), and aggravate_inflicted_bleeding aggravates the hit's own; Crimson
    /// Dance (keystone_crimson_dance) stacks 8 instead and cannot aggravate (ReminderTextKeystoneCrimsonDance). It lasts
    /// 5 s × (1 + bleed_duration_+%, base_bleed_duration_+% and
    /// base_all_ailment_duration_+%); faster_bleed_% and damaging_ailments_deal_damage_+%_faster multiply its damage per
    /// second and divide its duration, as ignite's faster stats do. cannot_cause_bleeding and never_damaging_ailments stop
    /// it; a physical prohibition already leaves its rows empty.
    /// </summary>
    public static MonsterIgnites.Settings BleedSettings(IEnumerable<(uint Stat, int Value)> rows, string hand,
        Func<uint, int> get, IReadOnlyDictionary<uint, int>? local, bool melee, bool bow,
        bool projectileAttack = false)
    {
        if (get(Stat.CannotCauseBleeding) != 0 || get(Stat.NeverDamagingAilments) != 0) return MonsterIgnites.Settings.None;
        double faster = Math.Max(0, 1 + ((double)get(Stat.FasterBleedPct) + get(Stat.DamagingAilmentsDealDamagePlusPctFaster)) / 100.0);
        if (faster <= 0) return MonsterIgnites.Settings.None;
        var r = rows.ToDictionary(x => x.Stat, x => x.Value);
        double Row(string bound, string kind) =>
            r.GetValueOrDefault(Stat.Of($"{bound}_bleeding_damage_to_deal_per_minute_from_{kind}_{hand}_damage")) / 60.0 * faster;
        int Local(uint name) => local?.GetValueOrDefault(name) ?? 0;
        // The fixed chances are 1 on their mods: "25% chance to cause Bleeding on Hit" is local_chance_to_bleed_on_hit_25%.
        long projectile = projectileAttack
            ? get(Stat.ProjectileAttacksChanceToBleedOnHitPctIfYouHaveBeastMinion) : 0;
        long chance = get(Stat.GlobalBleedOnHit) != 0 || Local(Stat.LocalBleedOnHit) != 0 ? 100
            : (long)get(Stat.BleedOnHitWithAttacksPct) + Local(Stat.LocalChanceToBleedOnHitPct)
                + 25L * Local(Stat.LocalChanceToBleedOnHit25Pct) + 50L * Local(Stat.LocalChanceToBleedOnHit50Pct)
                + (melee ? get(Stat.BleedOnMeleeAttackChancePct) : 0) + (bow ? get(Stat.BleedOnBowAttackChancePct) : 0)
                + projectile;
        long critical = chance + get(Stat.BleedOnCritPctWithAttacks) + Local(Stat.LocalBleedOnCriticalStrikeChancePct)
            + 50L * Local(Stat.LocalChanceToBleedOnCrit50Pct)
            + (melee ? get(Stat.BleedOnMeleeCriticalStrike) != 0 ? 100 : get(Stat.BleedOnMeleeCritChancePct) : 0);
        // Puncture (bleed_duration_is_skill_duration): its bleed lasts the skill's duration, with the skill duration
        // modifiers (ActiveSkills puncture: "affected by modifiers to skill duration"), then the bleed's own.
        double baseSeconds = get(Stat.BleedDurationIsSkillDuration) != 0 && get(Stat.BaseSkillEffectDuration) > 0
            ? SkillDurationModifiers.Seconds(get(Stat.BaseSkillEffectDuration), _ => 0, get) : Ailments.BleedDurationSeconds;
        double seconds = baseSeconds * Math.Max(0, 1 + ((double)get(Stat.BleedDurationPlusPct)
            + get(Stat.BaseBleedDurationPlusPct) + get(Stat.BaseAllAilmentDurationPlusPct)) / 100) / faster
            * MonsterIgnites.DurationMultiplier(get);
        bool dance = get(Stat.KeystoneCrimsonDance) != 0;
        int aggravate = dance ? 0 : get(Stat.BaseAggravateBleedingOnAttackHitChancePct);
        // Rupture Support: a critical strike of the supported skill that inflicts bleeding also ruptures the target.
        int rupture = get(Stat.CriticalStrikesThatInflictBleedingAlsoRupture) != 0
            ? Math.Max(0, get(Stat.SupportRuptureBleedingDamageTakenPlusPctFinal)) : 0;
        return new((int)Math.Clamp(chance, 0, 100), Row("minimum", "non_critical"), Row("maximum", "non_critical"),
            Row("minimum", "critical"), Row("maximum", "critical"), seconds, (int)Math.Clamp(critical, 0, 100),
            dance ? CrimsonDanceStacks : 1, Math.Clamp(aggravate, 0, 100),
            dance ? 0 : (int)Math.Clamp((long)aggravate + get(Stat.AggravateBleedingOnAttackCritChancePct), 0, 100),
            !dance && get(Stat.AggravateInflictedBleeding) != 0, rupture,
            NeverFromCrit: get(Stat.KeystoneElementalOverload) != 0,
            OnlyFromCrit: get(Stat.KeystoneAilmentCrit) != 0);
    }

    /// <summary>A skill's bleed, as <see cref="SupportedIgnite"/>: none for a spell; the swung weapon
    /// (<paramref name="weapon"/>: a shield skill's shield, a dual-wielded off hand) brings its own local chance.</summary>
    public static MonsterIgnites.Settings SupportedBleed(Player player, ushort skillId, bool spell,
        IReadOnlyDictionary<uint, int> playerStats, SkillCatalog.Unarmed? weapon = null, bool melee = false,
        long additionalIncreased = 0, SupportedHitInputs? capturedInputs = null)
    {
        if (spell) return MonsterIgnites.Settings.None;
        Func<uint, int> get;
        if (capturedInputs is null)
        {
            var merged = MergeSkillStats(playerStats, SupportedSkill(player, skillId).Stats);
            get = name => name is var key ? merged.GetValueOrDefault(key) : 0;
        }
        else
            get = capturedInputs.Get;
        var rows = capturedInputs is null
            ? SupportedAilmentRows(player, skillId, "main_hand", weapon, additionalIncreased)
            : SupportedAilmentRowsFrom(capturedInputs, "main_hand", additionalIncreased);
        return WithOverheat(player, skillId, BleedSettings(rows, "main_hand", get,
            weapon is null ? MainHandLocalSources(player) : weapon.LocalSources, melee, bow: false));
    }

    /// <summary>The default melee attack's bleed, from the rows <see cref="DefaultAttackIgnite"/> reads.</summary>
    public static MonsterIgnites.Settings DefaultAttackBleed(Player player, IReadOnlyDictionary<uint, int> playerStats,
        SkillCatalog.Unarmed? weapon = null, long additionalIncreased = 0, DamageConversion? conversion = null,
        int? accuracy = null)
    {
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        var sourceWeapon = weapon ?? WeaponFor(player);
        return BleedSettings(AilmentPanelStats.Compute(new("main_hand", Spell: false, Melee: true),
            SupportedAttackDamage.Raw(sourceWeapon, Get, new(accuracy ?? PlayerAccuracy(player, playerStats)), melee: true),
            conversion ?? SupportedAttackDamage.WithWeaponGains(DamageStats.Conversion(playerStats, Stats, attack: true), sourceWeapon),
            Get, additionalIncreased),
            "main_hand", Get, sourceWeapon.LocalSources, melee: true, bow: false);
    }

    // Called under the combat lock at an accepted attack hit that dealt physical damage and left the monster alive: the
    // hit's bleed and the packets of the monster's bleeding buff (BuffDefinitions "bleeding", its timer to the last bleed's
    // end), or null. Unlike ignite a critical strike rolls its chance too (Settings.CriticalChance), then bleeds with the
    // critical rows (wiki Bleeding). The target's avoid_bleed_% and base_avoid_bleed_% avoid it; immune_to_bleeding and
    // cannot_gain_bleeding stop it. First the hit aggravates the bleeds already on the target, whether or not it bleeds
    // (wiki Bleeding, Aggravate): they deal damage as though it moved for the rest of their durations.
    // shortcut: callers only come here for a hit that dealt physical damage, so a hit without any never aggravates.
    public static List<byte[]>? BleedOnHitLocked(Player player, uint id, SpawnedMonster target,
        MonsterIgnites.Settings settings, bool critical, double now, bool reflect = false)
    {
        if (target.Invulnerable || LabyrinthTrials.BleedingBuffRow is not ushort row) return null;
        // Elemental Overload: the ailment is not from a critical strike, so it rolls the hit's chance and aggravate.
        if (settings.NeverFromCrit) critical = false;
        SettleBleeding(target, now);
        target.Bleeds.Stacks = settings.Stacks;
        if (target.Bleeds.Burning
            && RandomRolls.Chance(critical ? settings.CriticalAggravate : settings.Aggravate, 100, player.IgniteRandom))
            target.Bleeds.Aggravate();
        if (!RandomRolls.Chance(critical ? settings.CriticalChance : settings.Chance, 100, player.IgniteRandom)) return null;
        var e = target.Mods.Effects;
        if (!target.Bleeds.TryApply(settings with { Chance = 100 }, critical, now, e[Stat.AvoidBleedPct] + e[Stat.BaseAvoidBleedPct],
                e[Stat.ImmuneToBleeding] != 0 || e[Stat.CannotGainBleeding] != 0 || e[Stat.BaseCannotGainBleeding] != 0,
                player.IgniteRandom, out double perSecond)) return null;
        player.Area.ActiveBleedMonsters.Add(id);
        InflictedLocked(player, target, AilmentType.Bleed, now + settings.Seconds);
        // Rupture Support: the critical strike that inflicted this bleeding also ruptures the target.
        if (critical && settings.RupturePercent > 0)
        {
            target.Ruptures.SetExpiryRate(now, target.RecipientDebuffRate);
            target.Ruptures.Add(now, settings.RupturePercent);
            target.Bleeds.SetExpiryRate(now, BleedingExpiryRate(target, now));
        }
        if (reflect)
        {
            var playerStats = CharacterPlayerStats(player);
            player.IncomingAilments.ApplyBleed(now, perSecond, settings.Seconds, reflected: true,
                victim: name => name is var key ? playerStats.GetValueOrDefault(key) : 0);
        }
        ShowDotBuffLocked(player, id, target.Bleeds, row, now);
        return PresentRupturesLocked(player, id, target, now);
    }

    /// <summary>A sure bleed of so much physical damage a second for so long, from no skill (a trap's hit that always
    /// bleeds: the Labyrinth's spikes), as an attack hit's bleed lands: the target's avoidance and immunity still apply.</summary>
    public static async Task BleedMonsterFor(Player player,
        uint id, SpawnedMonster m, double perSecond, double seconds)
    {
        List<byte[]>? packets;
        string log;
        using (ProjectileMutation(player))
        {
            double now = ProjectileWorldTime(player);
            if (m.Dead || m.Life == 0 || !player.Area.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m)) return;
            packets = BleedOnHitLocked(player, id, m, new(100, perSecond, perSecond, perSecond, perSecond, seconds, 100), false, now);
            log = BleedingLog(id, m, false, now);
        }
        await SendDotBuff(player, packets, log);
    }

    public static string BleedingLog(uint id, SpawnedMonster target, bool critical, double now) =>
        $"monster 0x{id:x} ({target.Label}) bleeding{(critical ? " from a critical hit" : "")} for {target.Bleeds.Until - now:0.##} s";

    public static void BleedOnHitLocked(ProjectileSession session, uint id, SpawnedMonster target,
        MonsterIgnites.Settings settings, bool critical, double now, bool reflect = false) =>
        QueueDotBuff(session, BleedOnHitLocked(session.Player, id, target, settings, critical, now, reflect),
            BleedingLog(id, target, critical, now));

    /// <summary>A bleeding monster's mitigation: its physical damage over time taken (resistance, damage taken, Pride;
    /// armour stops hits only), times <see cref="BleedMovingMore"/> while it moves, but not for Crimson Dance's
    /// stacks.</summary>
    public static double BleedTaken(SpawnedMonster m, double now) =>
        MonsterDotTaken(m, DamageType.Physical, now) * m.Ruptures.More(now)
        * (m.Bleeds.Stacks == 1 && (m.Walk.Moving(now) || IsEnsnared(m, now)) ? BleedMovingMore : 1);

    /// <summary>A standing monster's strongest aggravated bleed deals its moving bonus on top of the strongest bleed's
    /// damage; moving, the strongest already deals it, and the two do not stack (wiki Bleeding, Aggravate).</summary>
    public static double AggravatedExtra(SpawnedMonster m, double now) =>
        m.Bleeds.Stacks == 1 && !m.Walk.Moving(now) && !IsEnsnared(m, now) ? BleedMovingMore - 1 : 0;

    // Rupture changes the target's running bleed clocks, including the bleed from the hit that applied it.
    // GGG's mechanics Q&A: https://www.pathofexile.com/forum/view-thread/3123794
    public static double BleedingExpiryRate(SpawnedMonster target, double now) =>
        target.RecipientDebuffRate * (1 + .25 * target.Ruptures.Stacks(now));

    // Settle at each stack's expiry, even if a delayed tick crosses several. Damage is committed by the normal DoT
    // tick; changing the rate or adding another bleed must not discard damage already earned by an expired instance.
    public static void SettleBleeding(SpawnedMonster target, double now)
    {
        while (target.Ruptures.NextExpiry is var end && end <= now)
        {
            double before = Math.BitDecrement(end);
            target.Bleeds.Accrue(end, BleedTaken(target, before), AggravatedExtra(target, before));
            target.Bleeds.SetExpiryRate(end, BleedingExpiryRate(target, end));
        }
        target.Bleeds.Accrue(now, BleedTaken(target, now), AggravatedExtra(target, now));
    }

    // One tick of the bleeding monsters (the area's monster damage over time), as BurnIgnited: only the strongest bleed deals damage (Crimson
    // Dance: the 8 strongest).
    public static Task BleedMonsters(Player player) =>
        TickStrongestDots(player, player.Area.ActiveBleedMonsters, m => m.Bleeds, BleedTaken, "bled to death", AggravatedExtra, SettleBleeding);
}
