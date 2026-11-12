using GameServer.Core;

/// <summary>
/// The mods a monster's own row carries for good (MonsterVarieties <c>ModsKeys</c> and <c>Special_ModsKeys</c> → Mods),
/// limited to the ones that are behaviour the server can express without the systems it lacks: a boss that is immune to
/// knockback, and a stage or a soul of a boss that drops nothing (Agents/docs/game/campaign/*.md, the act bosses).
/// The rolled mods of rare and magic monsters are a different table and are not read here.
/// </summary>
public static class BossMods
{
    /// <summary>BossHealOnPlayerDeath is not here: its stat (<c>heal_aura_owner_%_max_life_on_death</c>) is 0 in the
    /// extracted table for both it and its Minor form, so how much life comes back is not something the data says.</summary>
    public const string ImmuneToKnockback = "ImmuneToKnockback";

    /// <summary>The monster gives no items and no experience (a boss's first stage: Piety's escape, Malachai, Shakari).</summary>
    public const string NoDropsOrExperience = "MonsterNoDropsOrExperience";

    /// <summary>The monster gives no items, and its experience is still its own (Abberath, Shavronne's tower, Maligaro's).</summary>
    public const string NoDrops = "MonsterNoDrops";

    static readonly Lazy<Dictionary<string, HashSet<string>>> Mods_ = new(Load);

    /// <summary>Whether the variety's row names this mod in either of its mod lists.</summary>
    public static bool Has(MonsterCatalog.Variety? variety, string mod) =>
        variety is not null && Mods_.Value.GetValueOrDefault(variety.Id)?.Contains(mod) == true;

    /// <summary>Whether nothing may shove the monster: the client's ImmuneToKnockback component or its mod.</summary>
    public static bool KnockbackImmune(MonsterCatalog.Variety? variety) =>
        variety?.ComponentsOrBase.Contains(ImmuneToKnockback) == true || Has(variety, ImmuneToKnockback);

    /// <summary>Whether the monster's death gives no items and no experience.</summary>
    public static bool NoRewards(MonsterCatalog.Variety? variety) => Has(variety, NoDropsOrExperience);

    /// <summary>Whether the monster's death gives no items (its experience may still be rolled).</summary>
    public static bool GivesNoItems(MonsterCatalog.Variety? variety) => Has(variety, NoDropsOrExperience) || Has(variety, NoDrops);

    static Dictionary<string, HashSet<string>> Load()
    {
        if (!ClientTables.Has("MonsterVarieties") || !ClientTables.Has("Mods")) return [];
        try
        {
            return Parse(ClientTables.Table("MonsterVarieties"), ClientTables.Table("Mods"));
        }
        catch (Exception ex) when (ex is IOException or InvalidCastException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine($"[gateway] monster mods: {ex.Message}");
            return [];
        }
    }

    /// <summary>From the MonsterVarieties and Mods client tables: each variety's mod ids, its own keys into Mods.</summary>
    public static Dictionary<string, HashSet<string>> Parse(ClientTable varieties, ClientTable mods)
    {
        var modIds = mods.Select(r => r.TextOrNull("Id") ?? "").ToList();
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var variety in varieties)
        {
            string id = variety.TextOrNull("Id") ?? "";
            if (id.Length == 0) continue;
            var own = new HashSet<string>(StringComparer.Ordinal);
            foreach (string keys in (string[])["ModsKeys", "Special_ModsKeys"])
                foreach (int row in variety.Ints(keys))
                    if (row >= 0 && row < modIds.Count && modIds[row].Length > 0) own.Add(modIds[row]);
            if (own.Count > 0) result[id] = own;
        }
        return result;
    }
}
