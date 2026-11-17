using GameServer.Core;
using GameServer.Core.Protocol.Instance;

// Extra gameplay identities do not replace the item's class, stance or native weapon-type enum.
public static class WeaponIdentity
{
    public const string LocalAllOneHandMelee = "local_varunastra_weapon_counts_as_all_1h_melee_weapon_types",
        MainAllOneHandMelee = "main_hand_varunastra_weapon_counts_as_all_1h_melee_weapon_types",
        OffAllOneHandMelee = "off_hand_varunastra_weapon_counts_as_all_1h_melee_weapon_types";

    public static bool OneHandMelee(string? itemClass) => itemClass is "Claw" or "Dagger" or "Rune Dagger"
        or "One Hand Sword" or "Thrusting One Hand Sword" or "One Hand Axe" or "One Hand Mace" or "Sceptre";

    static readonly Lazy<HashSet<int>> OneHandMeleeRows = new(() => ClientTables.Table("ItemClasses")
        .Select((row, index) => (Class: row.Text("Id"), Row: index))
        .Where(row => OneHandMelee(row.Class)).Select(row => row.Row).ToHashSet());

    public static bool AllOneHandMelee(IReadOnlyDictionary<string, int>? local) =>
        local is not null && local.GetValueOrDefault(LocalAllOneHandMelee) != 0;

    public static bool CountsAs(string? actualClass, IReadOnlyDictionary<string, int>? local, string itemClass) =>
        actualClass == itemClass || AllOneHandMelee(local) && OneHandMelee(itemClass);

    public static bool CountsAs((Item Item, ItemCatalog.BaseType? Type)? hand, ModCatalog mods, string itemClass) =>
        hand is { } held && CountsAs(held.Type?.Class, ItemModStats.Local(held.Item, mods), itemClass);

    public static bool Allows(IReadOnlyCollection<int> allowed, int? actualClass, bool allOneHandMelee = false) =>
        allowed.Count == 0 || actualClass is int row && allowed.Contains(row)
        || allOneHandMelee && allowed.Any(OneHandMeleeRows.Value.Contains);
}
