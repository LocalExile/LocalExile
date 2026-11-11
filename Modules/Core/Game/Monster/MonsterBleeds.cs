using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    static bool Has(IReadOnlyDictionary<uint, int> player, string stat) =>
        Stats.Key(stat) is uint key && player.GetValueOrDefault(key) != 0;
    internal static bool BleedReflected(IReadOnlyDictionary<uint, int> player) => Has(player, "bleeding_reflected_to_self")
        && !Has(player, "cannot_gain_bleeding") && !Has(player, "virtual_immune_to_bleeding")
        && !Has(player, "immune_to_bleeding");

    /// <summary>The wiki's Bleeding: a moving enemy takes 200% more damage from it (the trial spike traps' bleed too).</summary>
    internal const double BleedMovingMore = 3;

    /// <summary>Crimson Dance: "You can inflict Bleeding on an Enemy up to 8 times" (ReminderTextKeystoneCrimsonDance).</summary>
    internal const int CrimsonDanceStacks = 8;

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
    internal static MonsterIgnites.Settings BleedSettings(IEnumerable<(string Stat, int Value)> rows, string hand,
        Func<string, int> get, IReadOnlyDictionary<string, int>? local, bool melee, bool bow,
        bool projectileAttack = false)
    {
        if (get("cannot_cause_bleeding") != 0 || get("never_damaging_ailments") != 0) return MonsterIgnites.Settings.None;
        double faster = Math.Max(0, 1 + ((double)get("faster_bleed_%") + get("damaging_ailments_deal_damage_+%_faster")) / 100.0);
        if (faster <= 0) return MonsterIgnites.Settings.None;
        var r = rows.ToDictionary(x => x.Stat, x => x.Value);
        double Row(string bound, string kind) =>
            r.GetValueOrDefault($"{bound}_bleeding_damage_to_deal_per_minute_from_{kind}_{hand}_damage") / 60.0 * faster;
        int Local(string name) => local?.GetValueOrDefault(name) ?? 0;
        // The fixed chances are 1 on their mods: "25% chance to cause Bleeding on Hit" is local_chance_to_bleed_on_hit_25%.
        long projectile = projectileAttack
            ? get("projectile_attacks_chance_to_bleed_on_hit_%_if_you_have_beast_minion") : 0;
        long chance = get("global_bleed_on_hit") != 0 || Local("local_bleed_on_hit") != 0 ? 100
            : (long)get("bleed_on_hit_with_attacks_%") + Local("local_chance_to_bleed_on_hit_%")
                + 25L * Local("local_chance_to_bleed_on_hit_25%") + 50L * Local("local_chance_to_bleed_on_hit_50%")
                + (melee ? get("bleed_on_melee_attack_chance_%") : 0) + (bow ? get("bleed_on_bow_attack_chance_%") : 0)
                + projectile;
        long critical = chance + get("bleed_on_crit_%_with_attacks") + Local("local_bleed_on_critical_strike_chance_%")
            + 50L * Local("local_chance_to_bleed_on_crit_50%")
            + (melee ? get("bleed_on_melee_critical_strike") != 0 ? 100 : get("bleed_on_melee_crit_chance_%") : 0);
        double seconds = Ailments.BleedDurationSeconds * Math.Max(0, 1 + ((double)get("bleed_duration_+%")
            + get("base_bleed_duration_+%") + get("base_all_ailment_duration_+%")) / 100) / faster
            * MonsterIgnites.DurationMultiplier(get);
        bool dance = get("keystone_crimson_dance") != 0;
        int aggravate = dance ? 0 : get("base_aggravate_bleeding_on_attack_hit_chance_%");
        // Rupture Support: a critical strike of the supported skill that inflicts bleeding also ruptures the target.
        int rupture = get("critical_strikes_that_inflict_bleeding_also_rupture") != 0
            ? Math.Max(0, get("support_rupture_bleeding_damage_taken_+%_final")) : 0;
        return new((int)Math.Clamp(chance, 0, 100), Row("minimum", "non_critical"), Row("maximum", "non_critical"),
            Row("minimum", "critical"), Row("maximum", "critical"), seconds, (int)Math.Clamp(critical, 0, 100),
            dance ? CrimsonDanceStacks : 1, Math.Clamp(aggravate, 0, 100),
            dance ? 0 : (int)Math.Clamp((long)aggravate + get("aggravate_bleeding_on_attack_crit_chance_%"), 0, 100),
            !dance && get("aggravate_inflicted_bleeding") != 0, rupture);
    }

    /// <summary>A skill's bleed, as <see cref="SupportedIgnite"/>: none for a spell; a shield skill's shield
    /// (<paramref name="weapon"/>) brings no main-hand local chance.</summary>
    internal static MonsterIgnites.Settings SupportedBleed(ClientState conn, ushort skillId, bool spell,
        IReadOnlyDictionary<uint, int> player, SkillCatalog.Unarmed? weapon = null, bool melee = false,
        long additionalIncreased = 0, SupportedHitInputs? capturedInputs = null)
    {
        if (spell) return MonsterIgnites.Settings.None;
        Func<string, int> get;
        if (capturedInputs is null)
        {
            var merged = MergeSkillStats(player, SupportedSkill(conn, skillId).Stats);
            get = name => Stats.Key(name) is uint key ? merged.GetValueOrDefault(key) : 0;
        }
        else
            get = capturedInputs.Get;
        var rows = capturedInputs is null
            ? SupportedAilmentRows(conn, skillId, "main_hand", weapon, additionalIncreased)
            : SupportedAilmentRowsFrom(capturedInputs, "main_hand", additionalIncreased);
        return BleedSettings(rows, "main_hand", get,
            weapon is null ? MainHandLocalSources(conn) : null, melee, bow: false);
    }

    /// <summary>The default melee attack's bleed, from the rows <see cref="DefaultAttackIgnite"/> reads.</summary>
    internal static MonsterIgnites.Settings DefaultAttackBleed(ClientState conn, IReadOnlyDictionary<uint, int> player,
        SkillCatalog.Unarmed? weapon = null, long additionalIncreased = 0, DamageConversion? conversion = null)
    {
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        var sourceWeapon = weapon ?? WeaponFor(conn);
        return BleedSettings(AilmentPanelStats.Compute(new("main_hand", Spell: false, Melee: true),
            SupportedAttackDamage.Raw(sourceWeapon, Get), conversion ?? DamageStats.Conversion(player, Stats, attack: true),
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
    internal static List<byte[]>? BleedOnHitLocked(ClientState conn, uint id, SpawnedMonster target,
        MonsterIgnites.Settings settings, bool critical, double now, bool reflect = false)
    {
        if (target.Invulnerable || LabyrinthTrials.BleedingBuffRow is not ushort row) return null;
        target.Bleeds.Stacks = settings.Stacks;
        if (target.Bleeds.Burning
            && RandomRolls.Chance(critical ? settings.CriticalAggravate : settings.Aggravate, 100, conn.IgniteRandom))
            target.Bleeds.Aggravate();
        if (!RandomRolls.Chance(critical ? settings.CriticalChance : settings.Chance, 100, conn.IgniteRandom)) return null;
        var e = target.Mods.Effects;
        if (!target.Bleeds.TryApply(settings with { Chance = 100 }, critical, now, e["avoid_bleed_%"] + e["base_avoid_bleed_%"],
                e["immune_to_bleeding"] != 0 || e["cannot_gain_bleeding"] != 0 || e["base_cannot_gain_bleeding"] != 0,
                conn.IgniteRandom, out double perSecond)) return null;
        // Rupture Support: the critical strike that inflicted this bleeding also ruptures the target.
        if (critical && settings.RupturePercent > 0) target.Ruptures.Add(now, settings.RupturePercent);
        if (reflect)
        {
            var player = CharacterPlayerStats(conn);
            conn.IncomingAilments.ApplyBleed(now, perSecond, settings.Seconds, reflected: true,
                victim: name => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0);
        }
        return ShowDotBuffLocked(conn, id, target.Bleeds, row, now);
    }

    internal static string BleedingLog(uint id, SpawnedMonster target, bool critical, double now) =>
        $"monster 0x{id:x} ({target.Label}) bleeding{(critical ? " from a critical hit" : "")} for {target.Bleeds.Until - now:0.##} s";

    internal static void BleedOnHitLocked(ProjectileSession session, uint id, SpawnedMonster target,
        MonsterIgnites.Settings settings, bool critical, double now, bool reflect = false) =>
        QueueDotBuff(session, BleedOnHitLocked(session.Conn, id, target, settings, critical, now, reflect),
            BleedingLog(id, target, critical, now));

    /// <summary>A bleeding monster's mitigation: its physical damage over time taken (resistance, damage taken, Pride;
    /// armour stops hits only), times <see cref="BleedMovingMore"/> while it moves, but not for Crimson Dance's
    /// stacks.</summary>
    internal static double BleedTaken(SpawnedMonster m, double now) =>
        MonsterDotTaken(m, DamageType.Physical, now) * m.Ruptures.More(now)
        * (m.Bleeds.Stacks == 1 && m.Walk.Moving(now) ? BleedMovingMore : 1);

    /// <summary>A standing monster's strongest aggravated bleed deals its moving bonus on top of the strongest bleed's
    /// damage; moving, the strongest already deals it, and the two do not stack (wiki Bleeding, Aggravate).</summary>
    internal static double AggravatedExtra(SpawnedMonster m, double now) =>
        m.Bleeds.Stacks == 1 && !m.Walk.Moving(now) ? BleedMovingMore - 1 : 0;

    // One tick of the bleeding monsters (RunPlayerTick), as BurnIgnited: only the strongest bleed deals damage (Crimson
    // Dance: the 8 strongest).
    internal static Task BleedMonsters(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        TickStrongestDots(remote, stream, ciphers, conn, m => m.Bleeds, BleedTaken, "bled to death", AggravatedExtra);
}
