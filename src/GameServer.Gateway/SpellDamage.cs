/// <summary>
/// A spell's hit before crits and the target's mitigation (Agents/docs/protocol/11-skills.md §9): per damage type, the
/// skill's spell_minimum/maximum_base_{type}_damage scaled by 1 + the summed increases that apply: damage_+%,
/// spell_damage_+%, elemental_damage_+% (fire, cold, lightning), the type's own {type}_damage_+%, and
/// projectile_damage_+% for projectile skills (base_is_projectile). The skill's stats include the player's (passives,
/// items, auras), so item mods count. Added spell damage uses damage effectiveness; Wrath and Zealotry supply
/// their more multipliers. Conversion is applied by the caller after this packet is built.
/// </summary>
static class SpellDamage
{
    static readonly string[] Types = ["physical", "fire", "cold", "lightning", "chaos"];

    /// <summary>The summed minimum and maximum hit, unrounded (the tooltip averages these; a real hit rolls between them
    /// and drops the fraction, <see cref="Roll"/>); (0, 0) for a skill with no base spell damage.</summary>
    public static (double Min, double Max) Hit(Func<string, int> get, double addedEffectiveness = 1)
    {
        int common = get("damage_+%") + get("spell_damage_+%") + (get("base_is_projectile") != 0 ? get("projectile_damage_+%") : 0)
            + (get("is_area_damage") != 0 ? get("area_damage_+%") : 0);
        double min = 0, max = 0;
        foreach (string type in Types)
        {
            double baseMin = get($"spell_minimum_base_{type}_damage") + get($"spell_minimum_added_{type}_damage") * addedEffectiveness,
                baseMax = get($"spell_maximum_base_{type}_damage") + get($"spell_maximum_added_{type}_damage") * addedEffectiveness;
            if (baseMin == 0 && baseMax == 0) continue;
            bool elemental = type is "fire" or "cold" or "lightning";
            double factor = Math.Max(0, 1 + (common + (elemental ? get("elemental_damage_+%") : 0) + get($"{type}_damage_+%")) / 100.0);
            double more = More(get, type);
            min += baseMin * factor * more;
            max += baseMax * factor * more;
        }
        return (min, Math.Max(min, max));
    }

    /// <summary>The same increases applied to a per-type packet, keeping each type separate for conversion and
    /// mitigation.</summary>
    public static GameServer.Core.World.DamagePacket Packet(GameServer.Core.World.DamagePacket hit, Func<string, int> get, double addedEffectiveness = 1)
    {
        hit = DamageStats.Added(hit, get, spell: true, melee: false, effectiveness: addedEffectiveness);
        int common = get("damage_+%") + get("spell_damage_+%") + (get("base_is_projectile") != 0 ? get("projectile_damage_+%") : 0)
            + (get("is_area_damage") != 0 ? get("area_damage_+%") : 0);
        return hit.Select((type, range) =>
        {
            string slug = type switch
            {
                GameServer.Core.World.DamageType.Physical => "physical",
                GameServer.Core.World.DamageType.Lightning => "lightning",
                GameServer.Core.World.DamageType.Cold => "cold",
                GameServer.Core.World.DamageType.Fire => "fire",
                GameServer.Core.World.DamageType.Chaos => "chaos",
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
            bool elemental = type is GameServer.Core.World.DamageType.Fire
                or GameServer.Core.World.DamageType.Cold
                or GameServer.Core.World.DamageType.Lightning;
            double factor = Math.Max(0, 1 + (common + (elemental ? get("elemental_damage_+%") : 0) + get($"{slug}_damage_+%")) / 100.0);
            return range.Scale(factor * More(get, slug));
        });
    }

    static double More(Func<string, int> get, string type) =>
        Math.Max(0, 1 + get("spell_damage_aura_spell_damage_+%_final") / 100.0)
        * (type == "lightning" ? Math.Max(0, 1 + get("wrath_aura_spell_lightning_damage_+%_final") / 100.0) : 1)
        * ChargeEffects.DamageMore(get, spell: true);

    /// <summary>
    /// An attack's hit scaled by the skill's resolved damage effectiveness in permyriad:
    /// Flicker Strike's "Deals 177.3% of base Attack Damage" at level 1 is 17730. The weapon's hit already
    /// holds the increases, which multiply the base the same way, so scaling the total is the same as scaling the base
    /// first. 0 leaves the hit alone (the default attack has no effectiveness) and so does 10000. A spell's own base
    /// damage is already final: its effectiveness only multiplies added damage in Packet/Hit.
    /// </summary>
    public static GameServer.Core.World.DamagePacket WithEffectiveness(GameServer.Core.World.DamagePacket hit, int permyriad) =>
        permyriad is > 0 and not 10_000 ? hit.Scale(permyriad / 10_000.0) : hit;

    /// <summary>One hit: uniform between the minimum and the maximum, fraction dropped.</summary>
    public static int Roll((double Min, double Max) hit, Random rng) => (int)Math.Floor(hit.Min + rng.NextDouble() * (hit.Max - hit.Min));

    /// <summary>The panel's Damage per Use and Damage per Second (both × 100) for a spell cast this often.</summary>
    public static (int PerUse, int PerSecond) Display((double Min, double Max) hit, double castsPerSecond)
    {
        double average = (hit.Min + hit.Max) / 2.0;
        return ((int)Math.Round(100 * average), (int)Math.Round(100 * average * castsPerSecond));
    }
}
