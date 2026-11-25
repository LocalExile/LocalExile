using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Mercenary;

/// <summary>
/// What a mercenary wears, and the ruleset body that lists it. In the six official mercenary areas the panel's item list is
/// exactly the visuals of the mercenary's own Inventories entries, in slot order: a two-hander alone, or one hand and a shield
/// or a second weapon, or a bow with its arrow and quiver (the weapons of the build's MercenaryBuilds.WieldableTypes), plus the
/// eight slots every one of them fills (body, helmet, gloves, boots, two rings, belt, amulet).
/// </summary>
public static class MercenaryGear
{
    /// <summary>The model slots of an Inventories entry (<see cref="VisibleItem"/>).</summary>
    public const byte MainHand = 0, OffHand = 1, Body = 2, Helmet = 4, Gloves = 5, Boots = 6, Back = 7,
        Ring1 = 8, Ring2 = 9, Belt = 10, Amulet = 11;

    /// <summary>The model slot of each <c>MercenaryBuildVisualOverrides.SlotId</c>: the four armour pieces.</summary>
    static readonly Dictionary<int, byte> ArmourSlots = new() { [7] = Helmet, [9] = Body, [11] = Gloves, [12] = Boots };

    /// <summary>The classes whose one weapon needs no second hand. [data: ItemClasses Ids]</summary>
    static readonly HashSet<string> TwoHanded =
        new(["Two Hand Sword", "Two Hand Axe", "Two Hand Mace", "Staff", "Warstaff", "Bow", "FishingRod"],
            StringComparer.OrdinalIgnoreCase);

    /// <summary>The shield tag a house's own shield carries; only a preference, since a house with no shield of its own takes any.</summary>
    static readonly Dictionary<string, string> Shields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Str"] = "str_shield", ["Dex"] = "dex_shield", ["StrDex"] = "str_dex_shield",
        ["StrInt"] = "str_int_shield", ["DexInt"] = "dex_int_shield",
    };

    /// <summary>The ruleset body's attribute byte: the Str / Dex / Int mask of the class's MercenaryAttributes row (Str 1, Dex 2,
    /// Int 4), 0 for the all-three house. Official bodies show 1, 2, 6 and 0; the mask for the houses with no capture is inferred.</summary>
    public static byte Attributes(MercenaryTables.Class kind)
    {
        string id = kind.Attribute;
        int mask = (id.Contains("Str", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            | (id.Contains("Dex", StringComparison.OrdinalIgnoreCase) ? 2 : 0)
            | (id.Contains("Int", StringComparison.OrdinalIgnoreCase) ? 4 : 0);
        return mask == 7 ? (byte)0 : (byte)mask;
    }

    /// <summary>The ItemClasses rows this build may wield: its <c>WieldableTypes</c> through <c>MercenaryWieldableTypes.ItemClass</c>.</summary>
    public static IReadOnlyList<int> Wieldable(MercenaryTables.Build build)
    {
        var wieldable = ClientTables.TableOrEmpty("MercenaryWieldableTypes");
        var rows = new List<int>();
        foreach (var r in ClientTables.TableOrEmpty("MercenaryBuilds"))
        {
            if (r.Index != build.Row) continue;
            foreach (int row in r.Ints("WieldableTypes"))
                if (wieldable.Row(row) is { } w && w.IntOrNull("ItemClass") is int cls)
                    rows.Add(cls);
        }
        return rows;
    }

    /// <summary>The build's four armour pieces: <c>MercenaryBuildVisualOverrides</c> names one <c>MtxTypeGameSpecific</c> per slot and
    /// <c>ItemisedVisualEffect</c> turns it into the ItemVisualIdentity and the ItemClasses row of the art. The armour is the build's
    /// own, not rolled: all 24 official armour visuals of the six mercenaries are exactly their builds' four.</summary>
    public static IReadOnlyList<VisibleItem> ArmourOf(MercenaryTables.Build build)
    {
        var effects = ClientTables.TableOrEmpty("ItemisedVisualEffect");
        var visuals = ClientTables.TableOrEmpty("ItemVisualIdentity");
        var byMtx = new Dictionary<int, (ushort Hash, byte Type)>();
        foreach (var r in effects)
        {
            if (r.IntOrNull("Mtx") is not int mtx || r.IntOrNull("VisualIdentity") is not int row) continue;
            if (visuals.Row(row) is not { } art || art.IntOrNull("HASH16") is not int hash || hash is <= 0 or > ushort.MaxValue) continue;
            var classes = r.Ints("ItemClasses");
            byMtx[mtx] = ((ushort)hash, classes.Length > 0 && classes[0] is >= 0 and < 255 ? (byte)(classes[0] + 1) : (byte)1);
        }
        var worn = new List<VisibleItem>();
        foreach (var r in ClientTables.TableOrEmpty("MercenaryBuildVisualOverrides"))
        {
            if (r.IntOrNull("Id") != build.Row || r.IntOrNull("SlotId") is not int slotId) continue;
            if (!ArmourSlots.TryGetValue(slotId, out byte slot)) continue;
            if (r.IntOrNull("MtxGameSpecific") is not int mtx || !byMtx.TryGetValue(mtx, out var art)) continue;
            worn.Add(new VisibleItem(slot, art.Hash, BowSetup.NoStance, art.Type));
        }
        return worn;
    }

    /// <summary>Everything this mercenary wears, in slot order. Which base item fills a weapon or jewellery slot is not in the
    /// official data (only the classes are), so it is drawn uniformly from <paramref name="rng"/> among the bases a mercenary of
    /// this level can wear.</summary>
    public static IReadOnlyList<VisibleItem> Of(MercenaryTables.Class kind, MercenaryTables.Build build, int level, Random rng)
    {
        var classes = ClientTables.TableOrEmpty("ItemClasses");
        string NameOf(int row) => classes.Row(row) is { } r ? r.TextOrNull("Id") ?? "" : "";
        int RowOf(string id)
        {
            foreach (var r in classes)
                if (string.Equals(r.TextOrNull("Id"), id, StringComparison.OrdinalIgnoreCase)) return r.Index;
            return -1;
        }
        int shield = RowOf("Shield"), quiver = RowOf("Quiver"), bow = RowOf("Bow");
        var wieldable = Wieldable(build);
        var hands = new List<VisibleItem>();

        if (bow >= 0 && wieldable.Contains(bow) && Pick([bow], level, rng) is { } bowBase)
        {
            // A bow is held in the left hand with an arrow in the right and the quiver on the back (BowSetup).
            byte type = bowBase.ClassRow is >= 0 and < 255 ? (byte)(bowBase.ClassRow + 1) : (byte)1;
            if (Program.Items.Visual("DefaultArrow") is { } arrow)
                hands.Add(new VisibleItem(MainHand, arrow, bowBase.Stance, type));
            hands.Add(new VisibleItem(OffHand, bowBase.VisualIdentity, BowSetup.NoStance, BowSetup.NoWeaponType));
            if (quiver >= 0 && Pick([quiver], level, rng) is { } quiverBase)
                hands.Add(new VisibleItem(Back, quiverBase.VisualIdentity, BowSetup.NoStance, BowSetup.NoWeaponType));
        }
        else
        {
            var weaponClasses = wieldable.Where(c => c != shield && c != quiver).ToList();
            if (Pick(weaponClasses, level, rng) is { } main)
            {
                hands.Add(Look(MainHand, main));
                // A shield where Shield is wieldable, else a second weapon of the main hand's class unless it is two-handed.
                string prefer = kind.Attribute.Length == 0 ? "" : Shields.GetValueOrDefault(kind.Attribute, "");
                if (wieldable.Contains(shield) && Pick([shield], level, rng, prefer) is { } shieldBase)
                    hands.Add(Look(OffHand, shieldBase));
                else if (!TwoHanded.Contains(NameOf(main.ClassRow)) && Pick([main.ClassRow], level, rng) is { } second)
                    hands.Add(Look(OffHand, second));
            }
        }

        var worn = new List<VisibleItem>(hands);
        worn.AddRange(ArmourOf(build));
        foreach (var (slot, id) in new[] { (Ring1, "Ring"), (Ring2, "Ring"), (Belt, "Belt"), (Amulet, "Amulet") })
            if (RowOf(id) is int row and >= 0 && Pick([row], level, rng) is { } jewel)
                worn.Add(Look(slot, jewel));
        return [.. worn.OrderBy(w => w.Slot)];
    }

    /// <summary>The ruleset body's item list: the worn entries' ItemVisualIdentity HASH16, in slot order.</summary>
    public static IReadOnlyList<ushort> Visuals(IReadOnlyList<VisibleItem> worn) =>
        [.. worn.OrderBy(w => w.Slot).Select(w => w.VisualIdentity).Where(v => v != 0)];

    // The record's type is the base's ItemClasses row + 1, as Equipment.LookOf writes it for a player's item.
    static VisibleItem Look(byte slot, ItemCatalog.BaseType type) =>
        new(slot, type.VisualIdentity, type.Stance, type.ClassRow is >= 0 and < 255 ? (byte)(type.ClassRow + 1) : (byte)1);

    // One of the bases of these ItemClasses rows a mercenary of this level can wear: real game bases, no Demigod, Royale or
    // Talisman art. `prefer` is a tag to take first when any base carries it. Sorted by id so the draw is the same on every run.
    static ItemCatalog.BaseType? Pick(IReadOnlyList<int> classRows, int level, Random rng, string prefer = "")
    {
        if (classRows.Count == 0) return null;
        var allowed = Program.Items.All
            .Where(t => classRows.Contains(t.ClassRow) && t.VisualIdentity != 0 && t.DropLevel <= Math.Max(1, level)
                && t.Id.StartsWith("Metadata/Items/", StringComparison.Ordinal)
                && !t.Id.Contains("Demigod", StringComparison.Ordinal)
                && !t.Id.Contains("Royale", StringComparison.Ordinal)
                && !t.Id.Contains("Talisman", StringComparison.Ordinal))
            .ToList();
        if (prefer.Length > 0 && allowed.Any(t => t.Tags.Contains(prefer)))
            allowed = [.. allowed.Where(t => t.Tags.Contains(prefer))];
        var fitting = allowed.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
        return fitting.Count == 0 ? null : fitting[rng.Next(fitting.Count)];
    }
}
