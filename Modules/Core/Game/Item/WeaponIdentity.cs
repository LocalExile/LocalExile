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

    public static bool AllOneHandMelee(Item item, ModCatalog mods) =>
        ItemModStats.LocalValue(item, mods, LocalAllOneHandMelee) != 0;

    public static bool CountsAs((Item Item, ItemCatalog.BaseType? Type)? hand, ModCatalog mods, string itemClass) =>
        hand is { } held && (held.Type?.Class == itemClass || OneHandMelee(itemClass) && AllOneHandMelee(held.Item, mods));

    // The native additional-projectile predicate reads both active hands, independently of the attacking hand.
    public static bool WieldingClawOrDagger(Func<string, int> get) =>
        get("main_hand_weapon_type") is 10 or 12 or 16 || get("off_hand_weapon_type") is 10 or 12 or 16
        || get(MainAllOneHandMelee) != 0 || get(OffAllOneHandMelee) != 0;

    // Varunastra alone qualifies. Ordinary/thrusting swords share a type here; mace/sceptre and dagger/rune dagger do not.
    public static bool WieldingDifferentTypes(Func<string, int> get)
    {
        if (get(MainAllOneHandMelee) != 0 || get(OffAllOneHandMelee) != 0) return true;
        static bool OneHand(int type) => type is 0 or 1 or 3 or 5 or 6 or 7 or 10 or 12 or 16;
        int main = get("main_hand_weapon_type"), off = get("off_hand_weapon_type");
        return OneHand(main) && OneHand(off) && (main == 1 ? 0 : main) != (off == 1 ? 0 : off);
    }

    public static bool Allows(IReadOnlyCollection<int> allowed, int? actualClass, bool allOneHandMelee = false) =>
        allowed.Count == 0 || actualClass is int row && allowed.Contains(row)
        || allOneHandMelee && allowed.Any(OneHandMeleeRows.Value.Contains);
}
