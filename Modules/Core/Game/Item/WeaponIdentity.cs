using GameServer.Core;
using GameServer.Core.Protocol.Instance;

// Extra gameplay identities do not replace the item's class, stance or native weapon-type enum.
public static class WeaponIdentity
{
    public const uint LocalAllOneHandMelee = Stat.LocalVarunastraWeaponCountsAsAll1hMeleeWeaponTypes,
        MainAllOneHandMelee = Stat.MainHandVarunastraWeaponCountsAsAll1hMeleeWeaponTypes,
        OffAllOneHandMelee = Stat.OffHandVarunastraWeaponCountsAsAll1hMeleeWeaponTypes;

    public static bool OneHandMelee(string? itemClass) => itemClass is "Claw" or "Dagger" or "Rune Dagger"
        or "One Hand Sword" or "Thrusting One Hand Sword" or "One Hand Axe" or "One Hand Mace" or "Sceptre";

    static HashSet<int> OneHandMeleeRows = [];

    public static void LoadGameData()
    {
        OneHandMeleeRows = ClientTables.Has("ItemClasses")
            ? ClientTables.Table("ItemClasses")
                .Select((row, index) => (Class: row.Text("Id"), Row: index))
                .Where(row => OneHandMelee(row.Class)).Select(row => row.Row).ToHashSet()
            : [];
    }

    public static bool AllOneHandMelee(IReadOnlyDictionary<uint, int>? local) =>
        local is not null && local.GetValueOrDefault(LocalAllOneHandMelee) != 0;

    public static bool CountsAs(string? actualClass, IReadOnlyDictionary<uint, int>? local, string itemClass) =>
        actualClass == itemClass || AllOneHandMelee(local) && OneHandMelee(itemClass);

    public static bool AllOneHandMelee(Item item, ModCatalog mods) =>
        ItemModStats.LocalValue(item, mods, LocalAllOneHandMelee) != 0;

    public static bool CountsAs((Item Item, ItemCatalog.BaseType? Type)? hand, ModCatalog mods, string itemClass) =>
        hand is { } held && (held.Type?.Class == itemClass || OneHandMelee(itemClass) && AllOneHandMelee(held.Item, mods));

    // The native additional-projectile predicate reads both active hands, independently of the attacking hand.
    public static bool WieldingClawOrDagger(Func<uint, int> get) =>
        get(Stat.MainHandWeaponType) is 10 or 12 or 16 || get(Stat.OffHandWeaponType) is 10 or 12 or 16
        || get(MainAllOneHandMelee) != 0 || get(OffAllOneHandMelee) != 0;

    // Varunastra alone qualifies. Ordinary/thrusting swords share a type here; mace/sceptre and dagger/rune dagger do not.
    public static bool WieldingDifferentTypes(Func<uint, int> get)
    {
        if (get(MainAllOneHandMelee) != 0 || get(OffAllOneHandMelee) != 0) return true;
        static bool OneHand(int type) => type is 0 or 1 or 3 or 5 or 6 or 7 or 10 or 12 or 16;
        int main = get(Stat.MainHandWeaponType), off = get(Stat.OffHandWeaponType);
        return OneHand(main) && OneHand(off) && (main == 1 ? 0 : main) != (off == 1 ? 0 : off);
    }

    public static bool Allows(IReadOnlyCollection<int> allowed, int? actualClass, bool allOneHandMelee = false) =>
        allowed.Count == 0 || actualClass is int row && allowed.Contains(row)
        || allOneHandMelee && allowed.Any(OneHandMeleeRows.Contains);
}
