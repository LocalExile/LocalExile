using GameServer.Core.World;

/// <summary>
/// A spell's hit before crits and the target's mitigation (Agents/docs/protocol/11-skills.md §9): per damage type, the
/// skill's spell_minimum/maximum_base_{type}_damage scaled by 1 + the summed increases that apply: damage_+%,
/// spell_damage_+%, elemental_damage_+% (fire, cold, lightning), the type's own {type}_damage_+%, and
/// projectile_damage_+% for projectile skills (base_is_projectile). The skill's stats include the player's (passives,
/// items, auras), so item mods count. Added spell damage uses damage effectiveness; Wrath and Zealotry supply
/// their more multipliers. Packet resolves conversion with visited-type history; Hit is its unconverted scalar view.
/// </summary>
static class SpellDamage
{
    /// <summary>The summed minimum and maximum hit, unrounded (the tooltip averages these; a real hit rolls between them
    /// and drops the fraction, <see cref="Roll"/>); (0, 0) for a skill with no base spell damage.</summary>
    public static (double Min, double Max) Hit(Func<string, int> get, double addedEffectiveness = 1)
    {
        var raw = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type.ToString().ToLowerInvariant();
            raw = raw.Add(type, new(get($"spell_minimum_base_{slug}_damage"), get($"spell_maximum_base_{slug}_damage")));
        }
        var packet = Packet(raw, get, addedEffectiveness);
        return (packet.MinTotal, packet.MaxTotal);
    }

    /// <summary>Resolve raw spell base/added damage with visited-type modifiers, retaining fractions for mitigation.</summary>
    public static GameServer.Core.World.DamagePacket Packet(GameServer.Core.World.DamagePacket hit, Func<string, int> get,
        double addedEffectiveness = 1, GameServer.Core.World.DamageConversion? conversion = null) =>
        PlayerHitDamage.Resolve(DamageStats.RawAdded(hit, get, spell: true, addedEffectiveness), get,
            conversion ?? new(), spell: true, area: get("is_area_damage") != 0, projectile: get("base_is_projectile") != 0);

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

    /// <summary>Damage per Use and DPS (both × 100), retaining packet fractions and the exact action rate.
    /// The optional factor weights critical hits; ranges remain the noncritical packet. Saturate native integers.</summary>
    public static (int PerUse, int PerSecond) Display((double Min, double Max) hit, double usesPerSecond,
        double damageFactor = 1)
    {
        double average = (hit.Min + hit.Max) / 2.0 * Math.Max(0, damageFactor);
        return (Native(100 * average), Native(100 * average * Math.Max(0, usesPerSecond)));

        static int Native(double value) => double.IsNaN(value) || value <= 0 ? 0
            : (int)Math.Min(int.MaxValue, Math.Round(value));
    }
}
