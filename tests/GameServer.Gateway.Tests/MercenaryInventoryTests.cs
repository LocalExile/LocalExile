using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Game;

namespace GameServer.Gateway.Tests;

public sealed class MercenaryInventoryTests : IDisposable
{
    readonly List<string> _characters = [];

    public void Dispose()
    {
        foreach (string c in _characters) PlayerInventories.Forget(c);
        TestEnvironment.ReleaseOwnSaves();
    }

    static PlayerInventories.Shape? ShapeOf(Item item) => Program.Items.ByHash(item.TypeHash) is { } t
        ? new(t.Width, t.Height, t.Class, t.MaxStack,
            FullStack: t.FullStackRow >= 0 && Program.Items.Row(t.FullStackRow) is { } becomes ? () => ItemCatalog.Create(becomes) : null,
            IsFossil: t.IsFossil)
        : null;

    static Item Make(string baseMetadataPath) =>
        ItemCatalog.Create(Program.Items.Find(baseMetadataPath) ?? throw new InvalidOperationException($"Missing base {baseMetadataPath}"))
            with { Identified = true };

    [Fact]
    public void Mercenary_slots_place_swap_hand_conflicts_temp_lock_and_persistence_work()
    {
        TestEnvironment.OpenOwnSaves("merc-inv-slots");
        string ch = "MercSlot" + Guid.NewGuid().ToString("N")[..8];
        _characters.Add(ch);
        var inv = PlayerInventories.For(ch, Program.Items);
        inv.ClearRow(PlayerInventories.MainInventory);

        // Verify all 10 MercenaryInventories.dat slot positions and key/row/target helpers.
        Assert.Equal([0, 2, 4, 6, 8, 9, 10, 11, 13, 15], PlayerInventories.MercenarySlots.Select(s => (int)s.CellX));
        Assert.Equal((byte)95, PlayerInventories.MercenaryRowForKey(0));
        Assert.Equal((byte)96, PlayerInventories.MercenaryRowForKey(1));
        Assert.Equal((byte)97, PlayerInventories.MercenaryRowForKey(2));
        Assert.Equal((byte)107, PlayerInventories.MercenaryRowForKey(3));
        Assert.Null(PlayerInventories.MercenaryRowForKey(4));
        Assert.Equal((byte)1, PlayerInventories.MercenaryKeyForTarget(1));
        Assert.Equal((byte)1, PlayerInventories.MercenaryKeyForTarget(96));
        Assert.Equal((byte)3, PlayerInventories.MercenaryKeyForTarget(107));
        Assert.Null(PlayerInventories.MercenaryKeyForTarget(50));

        // Clicking any cell inside BodyArmour1's 2-wide X range (x=1) normalizes to (0, 0).
        var plate1 = Make("Metadata/Items/Armours/BodyArmours/BodyStr1");
        var plate2 = Make("Metadata/Items/Armours/BodyArmours/BodyStr2");
        var helmet = Make("Metadata/Items/Armours/Helmets/HelmetStr1");
        var sword1 = Make("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1");
        var sword2 = Make("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword2");
        var wand = Make("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1");
        var twoHand = Make("Metadata/Items/Weapons/TwoHandWeapons/TwoHandSwords/TwoHandSword1");
        var bow = Make("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1");
        var quiver = Make("Metadata/Items/Quivers/QuiverNew1");
        var shield = Make("Metadata/Items/Armours/Shields/ShieldStr1");

        var p1 = inv.Add(PlayerInventories.MainInventory, 0, 0, plate1);
        Assert.NotNull(inv.PickUp(PlayerInventories.MainInventory, p1.Id));
        var placedPlate = inv.Place(PlayerInventories.Mercenary1, 1, 0, ShapeOf, out string why);
        Assert.NotNull(placedPlate);
        Assert.Equal((byte)0, placedPlate!.Placed.X);
        Assert.Equal((byte)0, placedPlate.Placed.Y);

        // Wrong class in BodyArmour1 slot is rejected; placing a second BodyArmour swaps the first onto cursor.
        var helmEntry = inv.Add(PlayerInventories.MainInventory, 0, 0, helmet);
        Assert.NotNull(inv.PickUp(PlayerInventories.MainInventory, helmEntry.Id));
        Assert.Null(inv.Place(PlayerInventories.Mercenary1, 0, 0, ShapeOf, out why));
        Assert.Contains("does not go in mercenary slot", why);
        Assert.NotNull(inv.Place(PlayerInventories.Mercenary1, 7, 0, ShapeOf, out why)); // Helm1 x=6..7 -> normalizes to 6
        Assert.Equal((byte)6, inv.Items(PlayerInventories.Mercenary1).Single(i => i.Id == helmEntry.Id).X);

        var p2 = inv.Add(PlayerInventories.MainInventory, 0, 0, plate2);
        Assert.NotNull(inv.PickUp(PlayerInventories.MainInventory, p2.Id));
        var swapped = inv.Place(PlayerInventories.Mercenary1, 0, 0, ShapeOf, out _);
        Assert.NotNull(swapped);
        Assert.Equal(p1.Id, swapped!.Displaced?.Id);
        Assert.NotNull(inv.Place(PlayerInventories.MainInventory, 0, 0, ShapeOf, out _)); // put displaced back

        // Hand conflicts: 1H sword + wand refused; 1H sword + 1H sword allowed; 2H sword while offhand occupied refused; bow + quiver allowed.
        var sw1 = inv.Add(PlayerInventories.MainInventory, 4, 0, sword1);
        Assert.NotNull(inv.PickUp(PlayerInventories.MainInventory, sw1.Id));
        Assert.NotNull(inv.Place(PlayerInventories.Mercenary1, 2, 0, ShapeOf, out _));

        var wnd = inv.Add(PlayerInventories.MainInventory, 6, 0, wand);
        Assert.NotNull(inv.PickUp(PlayerInventories.MainInventory, wnd.Id));
        Assert.Null(inv.Place(PlayerInventories.Mercenary1, 4, 0, ShapeOf, out why));
        Assert.Contains("wand", why);
        Assert.NotNull(inv.Place(PlayerInventories.MainInventory, 6, 0, ShapeOf, out _));

        var sh = inv.Add(PlayerInventories.MainInventory, 8, 0, shield);
        Assert.NotNull(inv.PickUp(PlayerInventories.MainInventory, sh.Id));
        Assert.NotNull(inv.Place(PlayerInventories.Mercenary1, 4, 0, ShapeOf, out _));

        var th = inv.Add(PlayerInventories.MainInventory, 10, 0, twoHand);
        Assert.NotNull(inv.PickUp(PlayerInventories.MainInventory, th.Id));
        Assert.Null(inv.Place(PlayerInventories.Mercenary1, 2, 0, ShapeOf, out why));
        Assert.Contains("two-handed", why);
        Assert.NotNull(inv.Place(PlayerInventories.MainInventory, 10, 0, ShapeOf, out _));

        // Bow + Quiver on Mercenary2.
        var bw = inv.Add(PlayerInventories.MainInventory, 0, 3, bow);
        var qv = inv.Add(PlayerInventories.MainInventory, 2, 3, quiver);
        Assert.NotNull(inv.PickUp(PlayerInventories.MainInventory, bw.Id));
        Assert.NotNull(inv.Place(PlayerInventories.Mercenary2, 2, 0, ShapeOf, out _));
        Assert.NotNull(inv.PickUp(PlayerInventories.MainInventory, qv.Id));
        Assert.NotNull(inv.Place(PlayerInventories.Mercenary2, 4, 0, ShapeOf, out _));

        // Out-of-range y for a mercenary slot (Belt1 height is 1, y=2 is invalid) is rejected.
        var belt = inv.Add(PlayerInventories.MainInventory, 6, 3, Make("Metadata/Items/Belts/Belt1"));
        Assert.NotNull(inv.PickUp(PlayerInventories.MainInventory, belt.Id));
        Assert.Null(inv.Place(PlayerInventories.Mercenary1, 15, 2, ShapeOf, out why));
        Assert.NotNull(inv.Place(PlayerInventories.MainInventory, 6, 3, ShapeOf, out _));

        // MercenaryTemporary (row 107) gear is locked against pickup, place, socket, unsocket, and vendor offer.
        var gem = Make("Metadata/Items/Gems/SkillGemFireball");
        var socketedPlate = plate1 with { Sockets = [new ItemSocket(SocketColour.Blue, gem)] };
        var (_, tempAdded) = inv.ReplaceRow(PlayerInventories.MercenaryTemporary, [(0, 0, socketedPlate)]);
        Assert.Single(tempAdded);
        Assert.Null(inv.PickUp(PlayerInventories.MercenaryTemporary, tempAdded[0].Id));
        Assert.Null(inv.Unsocket(PlayerInventories.MercenaryTemporary, tempAdded[0].Id, 0, inv.NewItemId, out why));
        Assert.Contains("temporary", why, StringComparison.OrdinalIgnoreCase);
        Assert.Null(inv.Offer(PlayerInventories.MercenaryTemporary, tempAdded[0].Id, 0, 0, ShapeOf));
        var gemHeld = inv.Add(PlayerInventories.MainInventory, 8, 3, gem);
        Assert.NotNull(inv.PickUp(PlayerInventories.MainInventory, gemHeld.Id));
        Assert.Null(inv.Socket(PlayerInventories.MercenaryTemporary, tempAdded[0].Id, 0, ShapeOf, inv.NewItemId, out why));
        Assert.Contains("temporary", why, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(inv.Place(PlayerInventories.MainInventory, 8, 3, ShapeOf, out _));
        var sw2 = inv.Add(PlayerInventories.MainInventory, 4, 0, sword2);
        Assert.NotNull(inv.PickUp(PlayerInventories.MainInventory, sw2.Id));
        Assert.Null(inv.Place(PlayerInventories.MercenaryTemporary, 2, 0, ShapeOf, out why));
        Assert.Contains("temporary", why);
        Assert.NotNull(inv.Place(PlayerInventories.MainInventory, 4, 0, ShapeOf, out _));

        // Persistence across Forget + reload, plus atomic CharacterSaves.ChangeModuleData.
        Assert.True(CharacterSaves.ChangeModuleData(ch, "mercenary.roster", _ => "{\"invited\":0}"));
        PlayerInventories.Forget(ch);
        var reloaded = PlayerInventories.For(ch, Program.Items);
        Assert.Equal(4, reloaded.Items(PlayerInventories.Mercenary1).Count);
        Assert.Equal(2, reloaded.Items(PlayerInventories.Mercenary2).Count);
        Assert.Single(reloaded.Items(PlayerInventories.MercenaryTemporary));
        Assert.Equal("{\"invited\":0}", CharacterSaves.ModuleData(ch, "mercenary.roster"));

        // ClearRow empties the mercenary row cleanly.
        var cleared = reloaded.ClearRow(PlayerInventories.Mercenary2);
        Assert.Equal(2, cleared.Count);
        Assert.Empty(reloaded.Items(PlayerInventories.Mercenary2));
    }

    [Fact]
    public void ItemMoves_quick_move_handles_ring_slots_canEquip_swaps_target_asymmetry_and_temp_lock()
    {
        TestEnvironment.OpenOwnSaves("merc-quick-move");
        string ch = "MercQM" + Guid.NewGuid().ToString("N")[..8];
        _characters.Add(ch);
        var inv = PlayerInventories.For(ch, Program.Items);

        var ring1 = inv.Add(PlayerInventories.MainInventory, 0, 0, Make("Metadata/Items/Rings/Ring1"));
        var ring2 = inv.Add(PlayerInventories.MainInventory, 1, 0, Make("Metadata/Items/Rings/Ring2"));
        var ring3 = inv.Add(PlayerInventories.MainInventory, 2, 0, Make("Metadata/Items/Rings/Ring3"));

        // Quick-move into mercenary via roster key (0 -> Mercenary1): fills Ring1 (x=9), then empty Ring2 (x=10), then swaps Ring1.
        var m1 = ItemMoves.MoveMercenaryEquipment(inv, target: 0, ring1.Id, toMercenary: true, fromExpanded: false,
            expandedAllowed: false, ShapeOf);
        Assert.NotNull(m1);
        Assert.Equal((PlayerInventories.MainInventory, PlayerInventories.Mercenary1, (byte)9),
            (m1!.From, m1.To, m1.Placed.X));
        Assert.Null(m1.SwappedOut);

        var m2 = ItemMoves.MoveMercenaryEquipment(inv, target: 0, ring2.Id, toMercenary: true, fromExpanded: false,
            expandedAllowed: false, ShapeOf);
        Assert.NotNull(m2);
        Assert.Equal((byte)10, m2!.Placed.X);
        Assert.Null(m2.SwappedOut);

        var m3 = ItemMoves.MoveMercenaryEquipment(inv, target: 0, ring3.Id, toMercenary: true, fromExpanded: false,
            expandedAllowed: false, ShapeOf);
        Assert.NotNull(m3);
        Assert.Equal((byte)9, m3!.Placed.X);
        Assert.NotNull(m3.SwappedOut);
        Assert.Equal(ring1.Id, m3.SwappedOut!.Id);
        Assert.Equal(PlayerInventories.MainInventory, m3.SwappedToRow);

        // Quick-move out accepts Inventories._row (95 = Mercenary1), matching retail C->S 0x010c asymmetry.
        var mOut = ItemMoves.MoveMercenaryEquipment(inv, target: PlayerInventories.Mercenary1, ring3.Id,
            toMercenary: false, fromExpanded: false, expandedAllowed: false, ShapeOf);
        Assert.NotNull(mOut);
        Assert.Equal((PlayerInventories.Mercenary1, PlayerInventories.MainInventory), (mOut!.From, mOut.To));

        // canEquip predicate enforces offhand restrictions (e.g. offhandMode=0 blocks Offhand1 so second 1H swaps Weapon1 instead).
        var s1 = inv.Add(PlayerInventories.MainInventory, 4, 0, Make("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1"));
        var s2 = inv.Add(PlayerInventories.MainInventory, 6, 0, Make("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword2"));
        var equip1 = ItemMoves.MoveMercenaryEquipment(inv, target: 1, s1.Id, toMercenary: true, fromExpanded: false,
            expandedAllowed: false, ShapeOf, canEquip: (slot, _) => slot != PlayerInventories.Offhand1);
        Assert.NotNull(equip1);
        Assert.Equal((byte)2, equip1!.Placed.X); // Weapon1
        var equip2 = ItemMoves.MoveMercenaryEquipment(inv, target: 1, s2.Id, toMercenary: true, fromExpanded: false,
            expandedAllowed: false, ShapeOf, canEquip: (slot, _) => slot != PlayerInventories.Offhand1);
        Assert.NotNull(equip2);
        Assert.Equal((byte)2, equip2!.Placed.X); // Swapped Weapon1 instead of placing in blocked Offhand1
        Assert.Equal(s1.Id, equip2.SwappedOut?.Id);

        // Temporary mercenary (target 3 or row 107) refuses quick-moves in both directions.
        var (_, tempItems) = inv.ReplaceRow(PlayerInventories.MercenaryTemporary, [(9, 0, Make("Metadata/Items/Rings/Ring1"))]);
        Assert.Null(ItemMoves.MoveMercenaryEquipment(inv, target: 3, ring1.Id, toMercenary: true, fromExpanded: false,
            expandedAllowed: false, ShapeOf));
        Assert.Null(ItemMoves.MoveMercenaryEquipment(inv, target: PlayerInventories.MercenaryTemporary, tempItems[0].Id,
            toMercenary: false, fromExpanded: false, expandedAllowed: false, ShapeOf));
    }
}
