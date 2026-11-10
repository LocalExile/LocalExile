using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

// Combine completed damage and defences; A alone owns weapon speed, critical chance, identity and sockets.
static class SkinTransferStats
{
    // Keep donor selection outside ItemModStats: both items use upstream's effect and condition evaluation.
    // A's own skill modifiers remain active; only B's granted/triggered skill sources are excluded.
    static IEnumerable<Item> Donors(IEnumerable<Item> items, ModCatalog mods) => (EquipmentAttachmentPolicy.Current.GlobalModifiers ? items : [])
        .Select(i => i.SkinTransferSource).OfType<Item>()
        .Select(i => i with { Mods = i.Mods.Where(m => mods.ByHash(m.Hash)?.GrantedEffects is 0).ToArray() });

    static bool SkillExecutionStat(string id) => id.Contains("trigger_a_", StringComparison.OrdinalIgnoreCase)
        || id.Contains("trigger_socketed", StringComparison.OrdinalIgnoreCase)
        || id.Contains("trigger_level_", StringComparison.OrdinalIgnoreCase)
        || id.Contains("_trigger_on_", StringComparison.OrdinalIgnoreCase)
        || id.Contains("_spells_triggered", StringComparison.OrdinalIgnoreCase)
        || id.Contains("cast_on_", StringComparison.OrdinalIgnoreCase)
        || id.Contains("cast_when_", StringComparison.OrdinalIgnoreCase)
        || id.Contains("granted_skill", StringComparison.OrdinalIgnoreCase);

    internal static Dictionary<string, int> Sum(IEnumerable<Item> items, ModCatalog mods,
        StatConditions.Subject subject = default, Func<string, bool>? statExists = null)
    {
        var equipped = items.ToArray();
        var result = ItemModStats.Sum(equipped, mods, subject: subject, statExists: statExists);
        foreach (var donor in Donors(equipped, mods))
            foreach (var (id, value) in ItemModStats.Sum([donor], mods, subject: subject, statExists: statExists))
                if (!SkillExecutionStat(id)) result[id] = ArcaneMight.Combine(id, result.GetValueOrDefault(id), value);
        return result;
    }

    internal static Dictionary<uint, int> Global(IEnumerable<Item> items, ModCatalog mods, StatCatalog stats,
        StatConditions.Subject subject = default)
    {
        var equipped = items.ToArray();
        var result = ItemModStats.Global(equipped, mods, stats, subject);
        foreach (var donor in Donors(equipped, mods))
        {
            var contribution = ItemModStats.Global([donor], mods, stats, subject);
            foreach (uint key in contribution.Keys.Where(k => stats.Name(k) is string id && SkillExecutionStat(id)).ToArray())
                contribution.Remove(key);
            result = ItemModStats.Plus(result, contribution);
        }
        return result;
    }

    internal static Dictionary<string, long> RawAttributes(IEnumerable<Item> items, ModCatalog mods)
    {
        var equipped = items.ToArray();
        return ItemModStats.RawAttributes(equipped.Concat(Donors(equipped, mods)), mods);
    }

    public static IEnumerable<Item> Items(Item item)
    {
        yield return item;
        if (EquipmentAttachmentPolicy.Current.LocalDefences && item.SkinTransferSource is { } source) yield return source;
    }

    public static Dictionary<string, int> Local(Item item, ModCatalog mods)
    {
        var result = ItemModStats.Local(item, mods);
        if (EquipmentAttachmentPolicy.Current.LocalAccuracy && item.SkinTransferSource is { } source)
        {
            // Only completed flat accuracy joins A's raw local view. B's damage is calculated separately below.
            const string accuracy = "local_accuracy_rating";
            result[accuracy] = (int)Math.Clamp((long)result.GetValueOrDefault(accuracy)
                + ItemModStats.Local(source, mods).GetValueOrDefault(accuracy), int.MinValue, int.MaxValue);
        }
        return result;
    }

    public static int Critical(Item item, ItemCatalog catalog)
    {
        if (catalog.ByHash(item.TypeHash) is not { } type) return 0;
        var local = ItemModStats.Local(item, catalog.Mods);
        return AddedBaseCriticalStrike.LocalWeaponChance(catalog.WeaponCritical(type),
            local.GetValueOrDefault("local_critical_strike_chance"), local.GetValueOrDefault("local_critical_strike_chance_+%"));
    }

    public static ItemWeaponStats.Result Weapon(Item item, (int AttackMs, int Min, int Max, int Range) basis,
        SkillCatalog skills, ItemCatalog catalog)
    {
        var a = ItemWeaponStats.Calculate(item, basis, catalog.Mods);
        if (!EquipmentAttachmentPolicy.Current.LocalDamage || item.SkinTransferSource is not { } source || catalog.ByHash(source.TypeHash) is not { } type
            || skills.WeaponBase(type.Row) is not { } bBase) return a;
        var b = ItemWeaponStats.Calculate(source, bBase, catalog.Mods);
        return a with { Damage = a.Damage.Add(b.Damage) };
    }

    public static (decimal Armour, decimal Evasion, int EnergyShield, decimal Ward) Defences(Item item, ItemCatalog catalog)
    {
        decimal armour = 0, evasion = 0, ward = 0;
        long es = 0;
        foreach (var part in Items(item))
            if (catalog.ByHash(part.TypeHash) is { } type && catalog.Defences(type) is { } basis)
            {
                var (a, e, s) = ItemDefences.Calculate(part, basis, catalog.Mods);
                armour += a; evasion += e; es += s;
                var local = ItemModStats.Local(part, catalog.Mods);
                ward += (long)WardState.Max((decimal)basis.WardMin + local.GetValueOrDefault("local_ward"),
                    local.GetValueOrDefault("local_ward_+%")) * (100 + Math.Clamp((int)part.Quality, 0, 100)) / 100;
            }
        return (armour, evasion, (int)Math.Min(int.MaxValue, es), ward);
    }
}
