using System.Collections.Concurrent;
using System.Linq;
using GameServer.Core;
using GameServer.Core.World;

/// <summary>
/// The poison rules a Crucible weapon tree grants to the skills its stat names: "All Damage from Blast Rain and
/// Artillery Ballista Hits can Poison" (<c>blast_rain_artillery_ballista_all_damage_can_poison</c>, Stats row 18203)
/// and "X% chance for Poisons inflicted with Blast Rain or Artillery Ballista to deal 100% more Damage"
/// (<c>blast_rain_artillery_ballista_poison_damage_+100%_final_chance</c>, row 18205). Both are the two stats of one
/// Mods row, <c>WeaponTreeSkillBlastRainArtilleryBallista</c> (36063, GenerationType 31, Domain 1), and the pair
/// repeats for Freezing Pulse and Eye of Winter (rows 18202 / 18204), so they are read by their shape rather than by
/// name: <c>&lt;skills&gt;_all_damage_can_poison</c> counts when <c>&lt;skills&gt;</c> names the hit's ActiveSkills row
/// (the whole prefix, its first id or its last one). The weapon's mod grants the stat to the character like every other
/// non-local mod; the skills in the name are what limit it, exactly as the client's own skill code reads only the stat
/// named after it, and as PoB scopes both lines with
/// <c>{ type = "SkillName", skillNameList = { "Blast Rain", "Artillery Ballista" } }</c>
/// (PoB ModParser.lua 3897-3901 and 3922-3924).
/// <para>
/// "All Damage can Poison" puts every damage type of the hit into the poison's base, each portion keeping the increases
/// of the types it visited and the chaos ones: PoB CalcOffence.lua 4669-4688 adds the Lightning, Cold and Fire portions
/// with <c>dmgTypeFlags.flags.Chaos</c> behind the <c>LightningCanPoison</c> / <c>ColdCanPoison</c> /
/// <c>FireCanPoison</c> flags those lines set. That is the same shape as Shaper of Flames'
/// <c>all_damage_can_ignite</c> in <see cref="AilmentPanelStats.Compute"/>.
/// </para>
/// <para>
/// The chance is rolled for each poison applied, and that poison deals 100% more damage for its whole duration. PoB
/// averages such a chance into the damage instead (ModParser.lua 3922: <c>Damage MORE more x num / 100</c>, so 25% x
/// 100% becomes 25% more poison damage flat), which a panel number has to do and a server must not.
/// </para>
/// <para>
/// The Pathfinder's Master Toxicist (<c>pathfinder_poison_damage_+100%_final_chance_during_flask_effect</c>) is not read
/// here: it names no skill and needs a flask effect, so <c>Game.TryPoisonOnHitLocked</c> reads it for every poison and
/// sums it with this chance (one chance per poison, capped at 100).
/// </para>
/// The unprefixed <c>all_damage_can_poison</c> and <c>poison_damage_+100%_final_chance</c> and the weapon-local
/// implicits (<c>local_all_damage_can_poison</c>) are other items' sources of the same two rules and are deliberately
/// not read here: they would change builds that carry them.
/// </summary>
public readonly record struct SkillPoisonStats(bool AllDamage, int DoubleChance)
{
    const string AllDamageSuffix = "_all_damage_can_poison";
    const string DoubleChanceSuffix = "_poison_damage_+100%_final_chance";

    /// <summary>The stats named after <paramref name="activeSkill"/> (an ActiveSkills Id, <c>artillery_ballista</c>)
    /// give a hit of that skill. <paramref name="get"/> is the caster's and the skill's merged stats read by id, where an
    /// equipped weapon's Crucible mod and the character's passives have landed. "All Damage can Poison" needs a skill
    /// name; the chance does not.</summary>
    public static SkillPoisonStats Of(string? activeSkill, Func<uint, int> get)
    {
        long chance = 0;
        // Without the Stats table there is nothing to match by name; it is not cached, so a later load still works.
        if (string.IsNullOrEmpty(activeSkill) || Program.Stats.Names.Count == 0)
            return new(false, (int)Math.Clamp(chance, 0, 100));
        var ids = Named.GetOrAdd(activeSkill, Resolve);
        return new(ids.AllDamage.Any(id => get(id) != 0),
            (int)Math.Clamp(chance + ids.DoubleChance.Sum(id => (long)get(id)), 0, 100));
    }

    /// <summary>The stats of the skill a release used, from its GrantedEffects row.</summary>
    public static SkillPoisonStats OfEffect(int effectRow, Func<uint, int> get) =>
        Of(ActiveSkillId(effectRow), get);

    /// <summary>Whether a landed hit can poison at all: its physical or chaos damage, or any of its damage with "All
    /// Damage can Poison" (as <see cref="MonsterIgnites.Settings.CanIgnite"/> does for Shaper of Flames).</summary>
    public bool CanPoison(DamagePacket hit) => AllDamage
        ? hit.Components.Any(c => c.Range.Min > 0)
        : hit[DamageType.Physical].Min + hit[DamageType.Chaos].Min > 0;

    /// <summary>Whether one poison being applied deals 100% more damage: an independent roll per poison.</summary>
    public bool Doubles(Random random) => DoubleChance > 0 && RandomRolls.Chance(DoubleChance, 100, random);

    /// <summary>A GrantedEffects row's ActiveSkills Id (<c>artillery_ballista</c>); null for a support, a row without an
    /// active skill, or without the client tables.</summary>
    public static string? ActiveSkillId(int effectRow) =>
        effectRow >= 0 && effectRow < Effects().Length ? Effects()[effectRow] : null;

    static string?[] effects = [];

    /// <summary>Fills the GrantedEffects -> ActiveSkills Id map lazily (the world loader reads this at startup).</summary>
    static string?[] Effects() => effects.Length == 0
        ? (effects = LoadEffects())
        : effects;

    static string?[] LoadEffects()
    {
        if (ClientTables.TableOrNull("GrantedEffects") is not { } granted
            || ClientTables.TableOrNull("ActiveSkills") is not { } active) return [];
        return [.. granted.Select(r => r.IntOrNull("ActiveSkill") is int row && row >= 0 && row < active.Count
            ? active[row].Text("Id") : null)];
    }

    readonly record struct StatNames(uint[] AllDamage, uint[] DoubleChance);
    static readonly ConcurrentDictionary<string, StatNames> Named = new(StringComparer.Ordinal);

    static StatNames Resolve(string skill) =>
        new([.. Matching(skill, AllDamageSuffix).Where(n => Program.Stats.Key(n) is not null).Select(n => Stat.Of(n))],
            [.. Matching(skill, DoubleChanceSuffix).Where(n => Program.Stats.Key(n) is not null).Select(n => Stat.Of(n))]);

    static IEnumerable<string> Matching(string skill, string suffix) => Program.Stats.Names.Where(name =>
        name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)
        && NamesSkill(name[..^suffix.Length], skill));

    // <skills> names the skill when it is the whole prefix, its first id or its last one: the two prefixes in
    // 3.29.3c are blast_rain_artillery_ballista and freezing_pulse_and_eye_of_winter. A prefix that names no skill
    // (local, virtual, map_monsters, triggered_spells) never matches one.
    static bool NamesSkill(string prefix, string skill) => prefix == skill
        || prefix.StartsWith(skill + "_", StringComparison.Ordinal)
        || prefix.EndsWith("_" + skill, StringComparison.Ordinal);
}
