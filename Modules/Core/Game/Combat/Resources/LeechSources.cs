using GameServer.Core.World;


/// <summary>Per-type leech coefficients from raw sources. Panel scopes overlap and are never inputs here.</summary>
sealed record LeechSources(DamagePacket Life, DamagePacket Mana, DamagePacket EnergyShield)
{
    public bool Attack { get; init; }
    public static LeechSources From(Func<string, int> get, bool attack, Func<string, int>? local = null)
    {
        DamagePacket Pool(string pool)
        {
            var result = DamagePacket.Empty;
            foreach (var type in DamageTypes.ConversionOrder)
            {
                string name = DamageTypes.Name(type).ToLowerInvariant();
                double coefficient = (double)get($"{pool}_leech_from_any_damage_permyriad")
                    + get($"base_{pool}_leech_from_{name}_damage_permyriad");
                if (type is DamageType.Fire or DamageType.Cold or DamageType.Lightning)
                    coefficient += get($"base_{pool}_leech_from_elemental_damage_permyriad");
                if (pool == "life") coefficient += get($"horrible_ugly_life_leech_from_{name}_damage_perhundredthousand") / 10.0;
                if (!attack) coefficient += get($"base_{pool}_leech_from_spell_damage_permyriad");
                else
                {
                    coefficient += get($"base_{pool}_leech_from_attack_damage_permyriad")
                        + (local?.Invoke($"local_{pool}_leech_from_any_damage_permyriad") ?? 0);
                    if (pool == "mana") coefficient += get("attack_skill_mana_leech_from_any_damage_permyriad");
                    if (type == DamageType.Physical)
                    {
                        coefficient += get($"{pool}_leech_from_physical_attack_damage_permyriad")
                            + (local?.Invoke($"local_{pool}_leech_from_physical_damage_permyriad") ?? 0);
                        if (pool != "energy_shield") coefficient += get("life_and_mana_leech_from_physical_damage_permyriad")
                            + (local?.Invoke("local_life_and_mana_leech_from_physical_damage_permyriad") ?? 0);
                    }
                }
                result = result.Add(DamagePacket.Of(type, Math.Max(0, coefficient), Math.Max(0, coefficient)));
            }
            return result;
        }
        return new(Pool("life"), Pool("mana"), Pool("energy_shield")) { Attack = attack };
    }

    // Preserve the existing whole direct-hit total while retaining its post-mitigation type proportions.
    // Impale reflection and damage over time never enter this method. Overkill is still damage dealt by the hit.
    public (double Life, double Mana, double EnergyShield) Amounts(DamagePacket taken, int directDamage)
    {
        if (directDamage <= 0 || taken.MinTotal <= 0) return default;
        double scale = directDamage / taken.MinTotal / 10_000;
        double Amount(DamagePacket coefficients) => taken.Components.Sum(c => c.Range.Min * coefficients[c.Type].Min) * scale;
        return (Amount(Life), Amount(Mana), Amount(EnergyShield));
    }
}
