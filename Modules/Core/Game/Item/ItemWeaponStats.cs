using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;


/// <summary>A weapon's local item damage and duration, before character, skill and support modifiers.</summary>
static class ItemWeaponStats
{
    public sealed record Result(DamagePacket Damage, int AttackMs);

    /// <summary>PoB 3.29.x Item.lua: local physical increases and ordinary quality multiply, then round once.
    /// Elemental local increases apply to fire, cold and lightning; chaos has only its local flat range here.</summary>
    public static Result Calculate(Item item, (int AttackMs, int Min, int Max, int Range) basis, ModCatalog mods)
    {
        // Keep signed sources wide until the completed item value is bounded. Multiple native mod sections can
        // contribute to the same local key, including opposing values that cancel before scaling.
        var local = new Dictionary<string, long>();
        foreach (var mod in item.Mods)
            if (mods.ByHash(mod.Hash) is { } row)
                for (int i = 0; i < row.Stats.Count && i < mod.Values.Count; i++)
                    if (row.Stats[i].Local)
                    {
                        string name = row.Stats[i].Id;
                        local[name] = local.GetValueOrDefault(name) + mod.Values[i];
                    }
        return Calculate(basis, name => local.GetValueOrDefault(name), item.Quality);
    }

    public static Result Calculate((int AttackMs, int Min, int Max, int Range) basis,
        Func<string, long> Get, int itemQuality)
    {
        long quality = Get("local_quality_does_not_increase_physical_damage") != 0 ? 0
            : Math.Max(0L, itemQuality + Get("local_item_quality_+"));

        int Bound(long value, long increased, long quality = 0)
        {
            decimal flat = Math.Max(0L, value);
            decimal factor = Math.Max(0m, 100m + increased) / 100m;
            decimal qualityFactor = 1 + quality / 100m;
            if (flat == 0 || factor == 0) return 0;
            // Compare before multiplying: extreme synthetic source totals cannot overflow decimal, and no
            // intermediate saturation can erase a later reduction. Real item values round halfway up once.
            if (flat >= int.MaxValue / factor / qualityFactor) return int.MaxValue;
            return (int)Math.Round(flat * factor * qualityFactor, 0, MidpointRounding.AwayFromZero);
        }

        var hit = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            if (type == DamageType.Physical && Get("local_weapon_no_physical_damage") != 0) continue;
            string slug = type.ToString().ToLowerInvariant();
            long minimum = (type == DamageType.Physical ? basis.Min : 0L) + Get($"local_minimum_added_{slug}_damage");
            long maximum = (type == DamageType.Physical ? basis.Max : 0L) + Get($"local_maximum_added_{slug}_damage");
            long increased = type == DamageType.Physical ? Get("local_physical_damage_+%")
                : DamageTypes.IsElemental(type) ? Get("local_elemental_damage_+%")
                    + Get("local_attacks_with_this_weapon_elemental_damage_+%") : 0;
            int lo = Bound(minimum, increased, type == DamageType.Physical ? quality : 0);
            int hi = Bound(maximum, increased, type == DamageType.Physical ? quality : 0);
            hit = hit.Add(type, new DamageRange(lo, Math.Max(lo, hi)));
        }

        // Local attack speed belongs to the item duration and must not be multiplied into the character's
        // increased-speed bucket again. Preserve the existing integer-ms convention and its one-ms floor.
        // The positive denominator guard is numeric protection, not a measured official minimum attack rate.
        decimal duration = Math.Max(1, basis.AttackMs) * 100m / Math.Max(1m, 100m + Get("local_attack_speed_+%"));
        int attackMs = (int)Math.Clamp(Math.Round(duration), 1, int.MaxValue);
        return new(hit, attackMs);
    }
}
