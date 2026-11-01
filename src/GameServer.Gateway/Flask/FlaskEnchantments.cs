using GameServer.Core.Protocol.Instance;

/// <summary>Native utility-flask orb pools and trigger conditions; all rolls use Mods.dat weights and ranges.</summary>
static class FlaskEnchantments
{
    public const string Enkindling = "Metadata/Items/Currency/CurrencyEnkindlingOrb";
    public const string Instilling = "Metadata/Items/Currency/CurrencyInstillingOrb";
    public const string CannotGain = "local_flask_cannot_gain_charges_during_flask_effect";
    public enum Trigger { Adjacent, End, Full, Guard, Travel, RareHit, Freeze, Chill, Shock, Ignite, Bleed, Poison, Block, SavageHit, LifeFlask }
    static string Stat(Trigger trigger) => "local_flask_use_on_" + (trigger switch
    {
        Trigger.Adjacent => "adjacent_flask_use", Trigger.End => "flask_effect_ended", Trigger.Full => "full_charges",
        Trigger.Guard => "guard_skill_used", Trigger.Travel => "travel_skill_used",
        Trigger.RareHit => "hitting_rare_unique_enemy_while_inactive", Trigger.Freeze => "affected_by_freeze",
        Trigger.Chill => "affected_by_chill", Trigger.Shock => "affected_by_shock", Trigger.Ignite => "affected_by_ignite",
        Trigger.Bleed => "affected_by_bleed", Trigger.Poison => "affected_by_poison", Trigger.Block => "damage_blocked",
        Trigger.SavageHit => "taking_savage_hit", _ => "using_a_life_flask",
    });
    public static bool Matches(IReadOnlyDictionary<string, int> local, Trigger trigger, byte slot, byte sourceSlot,
        bool active) => local.GetValueOrDefault(Stat(trigger)) > 0
        && (trigger != Trigger.Adjacent || Math.Abs(slot - sourceSlot) == 1)
        && (trigger != Trigger.RareHit || !active);

    public static bool BlocksGain(IReadOnlyDictionary<string, int> local, bool active) => active && local.GetValueOrDefault(CannotGain) > 0;

    public static Item? Apply(Item item, ItemCatalog.BaseType type, ModCatalog mods, bool instilling, Random random)
    {
        if (type.Class != "UtilityFlask" || !type.Supported || type.Unmodifiable || !item.Components.Contains("Flask")
            || !item.Components.Contains("Mods") || item.Corrupted || item.Mirrored) return null;
        int generation = instilling ? 22 : 21;
        var pool = mods.All.Where(m => m.GenerationType == generation)
            .Select(m => (Mod: m, Weight: m.WeightFor(type.Tags))).Where(m => m.Weight > 0).ToArray();
        if (pool.Length == 0) return null;
        int roll = random.Next(pool.Sum(m => m.Weight));
        var chosen = pool[^1].Mod;
        foreach (var entry in pool) { roll -= entry.Weight; if (roll < 0) { chosen = entry.Mod; break; } }
        // Enkindling and Instilling are separate enchantments: each replaces only its own type and keeps the other.
        // Both replace the old Harvest/bench flask enchantment family.
        int other = generation == 21 ? 22 : 21;
        var kept = item.Mods.Where(m =>
        {
            var mod = mods.ByHash(m.Hash);
            if (m.Section == ModSection.Enchant) return mod?.GenerationType == other;
            if (mod is not null && mod.ModType.Contains("FlaskEnchantment", StringComparison.Ordinal)
                && mod.GenerationType != other) return false;
            return mod is null || !mod.Families.Intersect(chosen.Families).Any();
        });
        var result = item with { Mods = [.. kept, chosen.Roll(ModSection.Enchant, random)] };
        int maximum = Math.Max(1, type.MaxCharges + ItemModStats.Local(result, mods).GetValueOrDefault("local_extra_max_charges"));
        return result with { Charges = Math.Min(result.Charges, maximum) };
    }
}
