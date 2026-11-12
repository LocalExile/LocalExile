using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Game;

static class HeistModifierMagnitudes
{
    public static ItemModStats.ModifierValueRule? For(Item item, ModCatalog mods)
    {
        Dictionary<string, long>? effects = null;
        bool hasEffectStat = false;
        foreach (var mod in item.Mods)
            if (mods.ByHash(mod.Hash) is { } row)
                for (int index = 0; index < row.Stats.Count && index < mod.Values.Count; index++)
                {
                    if (Tag(row.Stats[index].Id) is not { } tag) continue;
                    hasEffectStat = true;
                    if (mod.Section != ModSection.Enchant) continue;
                    effects ??= new(StringComparer.Ordinal);
                    effects[tag] = effects.GetValueOrDefault(tag) + mod.Values[index];
                }
        return hasEffectStat ? Adjust : null;

        int? Adjust(ItemMod mod, ModCatalog.Mod row, int index, long ordinaryEffect)
        {
            var stat = row.Stats[index];
            int value = mod.Values[index];
            if (Tag(stat.Id) is not null) return value;
            if (mod.Section != ModSection.Explicit || effects is null || row.Unscalable
                || row.GrantedEffects != 0 || !stat.Scalable) return null;

            long effect = 0;
            foreach (var (tag, percent) in effects)
                if (row.Tags.Contains(tag)) effect += percent;
            if (effect == 0) return null;

            decimal scaled = (decimal)value * (100 + ordinaryEffect + effect) / 100;
            return (int)Math.Clamp(decimal.Truncate(scaled), int.MinValue, int.MaxValue);
        }
    }

    // The category in the stat id is also the native modifier tag, with two spelling differences.
    static string? Tag(string id)
    {
        const string prefix = "heist_enchantment_", suffix = "_mod_effect_+%";
        if (id.Length <= prefix.Length + suffix.Length || !id.StartsWith(prefix, StringComparison.Ordinal)
            || !id.EndsWith(suffix, StringComparison.Ordinal)) return null;
        return id[prefix.Length..^suffix.Length] switch
        {
            "defence" => "defences",
            "casterdamage" => "caster_damage",
            var tag => tag,
        };
    }
}
