using System.Linq;
using GameServer.Core;
using GameServer.Core.World;

/// <summary>
/// The poison rules a Crucible weapon tree grants to the skills its stat names: "All Damage from Blast Rain and
/// Artillery Ballista Hits can Poison" (<c>blast_rain_artillery_ballista_all_damage_can_poison</c>, Stats row 18203)
/// and "X% chance for Poisons inflicted with Blast Rain or Artillery Ballista to deal 100% more Damage"
/// (<c>blast_rain_artillery_ballista_poison_damage_+100%_final_chance</c>, row 18205). Both are the two stats of one
/// Mods row, <c>WeaponTreeSkillBlastRainArtilleryBallista</c> (36063, GenerationType 31, Domain 1), and the pair
/// repeats for Freezing Pulse and Eye of Winter (rows 18202 / 18204). The weapon's mod grants the stat to the
/// character like every other non-local mod; the skills in the name are what limit it, matching transfigured and
/// Royale variants through their base ActiveSkills id, as PoB scopes both lines with
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
    /// <summary>The stats named after <paramref name="activeSkill"/> (an ActiveSkills Id, <c>artillery_ballista</c> or
    /// its transfigured/Royale variant) give a hit of that skill. <paramref name="get"/> is the caster's and the
    /// skill's merged stats read by id, where an equipped weapon's Crucible mod and the character's passives have
    /// landed.</summary>
    public static SkillPoisonStats Of(string? activeSkill, Func<uint, int> get)
    {
        if (string.IsNullOrEmpty(activeSkill)) return default;
        return BaseSkillId(activeSkill) switch
        {
            "blast_rain" or "artillery_ballista" => new(
                get(Stat.BlastRainArtilleryBallistaAllDamageCanPoison) != 0,
                Math.Clamp(get(Stat.BlastRainArtilleryBallistaPoisonDamagePlus100PctFinalChance), 0, 100)),
            "freezing_pulse" or "eye_of_winter" => new(
                get(Stat.FreezingPulseAndEyeOfWinterAllDamageCanPoison) != 0,
                Math.Clamp(get(Stat.FreezingPulseAndEyeOfWinterPoisonDamagePlus100PctFinalChance), 0, 100)),
            _ => default,
        };
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
    public static string? ActiveSkillId(int effectRow)
    {
        var map = Effects();
        return effectRow >= 0 && effectRow < map.Length ? map[effectRow] : null;
    }

    /// <summary>Warms the GrantedEffects -> ActiveSkills Id snapshot at module load.</summary>
    public static void LoadData() => _ = Effects();

    sealed record EffectMap(object? Revision, string?[] ActiveSkills);
    static EffectMap effectMap = new(null, []);

    static string?[] Effects()
    {
        var current = Volatile.Read(ref effectMap);
        var key = ClientTables.CacheKey;
        if (current.ActiveSkills.Length > 0 && Equals(current.Revision, key))
            return current.ActiveSkills;
        var loaded = LoadEffects();
        if (loaded.Length > 0)
            Volatile.Write(ref effectMap, new(key, loaded));
        return loaded;
    }

    static string?[] LoadEffects()
    {
        if (ClientTables.TableOrNull("GrantedEffects") is not { } granted
            || ClientTables.TableOrNull("ActiveSkills") is not { } active) return [];
        return [.. granted.Select(r => r.IntOrNull("ActiveSkill") is int row && row >= 0 && row < active.Count
            ? active[row].Text("Id") : null)];
    }

    static string BaseSkillId(string skill) =>
        skill.EndsWith("_alt_x", StringComparison.Ordinal) ? skill[..^6]
        : skill.EndsWith("_alt_y", StringComparison.Ordinal) ? skill[..^6]
        : skill.EndsWith("_royale", StringComparison.Ordinal) ? skill[..^7]
        : skill;
}
