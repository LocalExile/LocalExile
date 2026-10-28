using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class InstanceItemsTests
{
    static readonly string[] Wand = ["Base", "LocalStats", "Mods", "AttributeRequirements", "Weapon", "Quality", "Sockets"];
    static readonly string[] Flask = ["Base", "Quality", "LocalStats", "Charges", "Mods", "Usable", "Flask"];

    [Fact]
    public void Item_type_hash_is_the_BaseItemTypes_HASH32()
    {
        // BaseItemTypes.HASH32 of Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1 (3.29.3b) is 3652280879.
        Assert.Equal(3652280879u, InstanceEntities.ObjectTypeHash("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1"));
    }

    [Fact]
    public void Wand_payload_layout()
    {
        var wand = new Item(0xd9b1a12f, Wand, 0x40a1);
        Assert.Equal(
            "2FA1B1D9" + "0000" +                   // type hash (LE), item flags
            "A140" + "00" + "00" + "0000" + "00" +  // Base: visual identity HASH16, u8, u8 flags, u16 flags, u8
            "01" + "00" + "0000" + "0800" + "00" + "00" + "00" + // Mods: item level, u8, flags, magic props (identified, normal), u8, u8
            "00" +                                   // Quality
            "00",                                    // Sockets: none
            Convert.ToHexString(wand.Payload()));
        Assert.Equal("0018" + Convert.ToHexString(wand.Payload()), Convert.ToHexString(wand.Wire()));
    }

    [Fact]
    public void Mods_component_carries_rarity_and_mod_lists_in_the_readers_order()
    {
        var item = new Item(1, ["Mods"], 0)
        {
            ItemLevel = 40, Rarity = ItemRarity.Rare,
            Mods = [new(ModSection.Explicit, 0x1234, [3, 5]), new(ModSection.Implicit, 0x5654, [10]), new(ModSection.Enchant, 0x0102, [])],
        };
        Assert.Equal(
            "01000000" + "0000" + "28" + "00" + "0000" +
            "7A00" + "00" +          // magic properties: rare | identified | bits 4, 5, 6; u8
            "01" + "5456" + "0A" +   // bit 4 (implicit): count, HASH16, a value per stat
            "01" + "0201" +          // bit 6 (enchant)
            "01" + "3412" + "03" + "05" + // bit 5 (explicit), last: only read for identified items
            "00" + "00",
            Convert.ToHexString(item.Payload()));
    }

    [Fact]
    public void Mods_component_carries_the_level_requirement_after_the_item_level()
    {
        var item = new Item(1, ["Mods"], 0) { ItemLevel = 40, RequiredLevel = 84 };
        Assert.StartsWith("01000000" + "0000" + "28" + "54", Convert.ToHexString(item.Payload()));
    }

    [Fact]
    public void A_rare_name_comes_before_the_explicit_list_and_only_when_identified()
    {
        var item = new Item(1, ["Mods"], 0)
        {
            Rarity = ItemRarity.Rare, NameWords = [0x11223344, 0xaabbccdd], Mods = [new(ModSection.Explicit, 0x1234, [3])],
        };
        Assert.Equal("01000000" + "0000" + "01" + "00" + "0000" +
            "2A01" + "00" +                                // rare | identified | bit 5 | bit 8
            "02" + "44332211" + "DDCCBBAA" +               // u8 n × u32 Words.dat key
            "01" + "3412" + "03" + "00" + "00",
            Convert.ToHexString(item.Payload()));
        Assert.Equal("0200", Convert.ToHexString((item with { Identified = false }).Payload(), 10, 2));
    }

    [Fact]
    public void Use_item_on_item_reads_both_items_big_endian()
    {
        // Live 2026-10-20: a Scroll of Wisdom (stash-range id 0x40000018) on an unidentified ring (187), both in the main inventory.
        byte[] m = Convert.FromHexString("0071000000014000001800000001000000BB");
        Assert.Equal(InstanceItems.UseItemOnLength, m.Length);
        Assert.Equal(InstanceItems.UseItemOnLength, InstanceProtocol.KnownClientLength(InstanceItems.UseItemOnId));
        Assert.Equal(new InstanceItems.UseItemOn(1, 0x40000018, 1, 187), InstanceItems.ReadUseItemOn(m));
    }

    [Fact]
    public void Use_item_type_on_item_reads_the_currency_type_and_the_target()
    {
        // Live 2026-10-21: Chromatic Orb (HASH32 0x1C1E192B) used again with Shift on main-inventory item 13.
        byte[] m = Convert.FromHexString("00721C1E192B000000010000000D000000000100070002");
        Assert.Equal(InstanceItems.UseItemTypeOnLength, m.Length);
        Assert.Equal(InstanceItems.UseItemTypeOnLength, InstanceProtocol.KnownClientLength(InstanceItems.UseItemTypeOnId));
        Assert.Equal(new InstanceItems.UseItemTypeOn(0x1C1E192B, 1, 13), InstanceItems.ReadUseItemTypeOn(m));
    }

    [Fact]
    public void Fusing_normal_and_shift_captures_frame_and_target_the_same_item()
    {
        byte[] normal = Convert.FromHexString("0071000000010000005B00000001000000E5");
        byte[] shift = Convert.FromHexString("0072C71BF58D00000001000000E5000000000100060000");
        Assert.Equal(new InstanceItems.UseItemOn(1, 91, 1, 229), InstanceItems.ReadUseItemOn(normal));
        Assert.Equal(new InstanceItems.UseItemTypeOn(0xC71BF58D, 1, 229), InstanceItems.ReadUseItemTypeOn(shift));
        var messages = InstanceProtocol.SplitClientMessages([.. normal, .. shift, .. normal], out var partial, out var unknown);
        Assert.Equal(new[] { 18, 23, 18 }, messages.Select(m => m.Bytes.Length));
        Assert.Empty(partial);
        Assert.Empty(unknown);
        var incomplete = InstanceProtocol.SplitClientMessages(shift.AsSpan(0, 22), out partial, out unknown);
        Assert.Empty(incomplete);
        Assert.Equal(22, partial.Length);
        Assert.Empty(unknown);
    }

    [Fact]
    public void Use_flask_reads_the_flask_bar_slot()
    {
        // Live 2026-10-20: flask keys 1 and 2 of the start kit.
        byte[] key1 = Convert.FromHexString("007000000000"), key2 = Convert.FromHexString("007000000001");
        Assert.Equal(InstanceItems.UseFlaskLength, key2.Length);
        Assert.Equal(InstanceItems.UseFlaskLength, InstanceProtocol.KnownClientLength(InstanceItems.UseFlaskId));
        Assert.Equal(0u, InstanceItems.ReadUseFlask(key1));
        Assert.Equal(1u, InstanceItems.ReadUseFlask(key2));
    }

    [Fact]
    public void Unidentified_items_send_no_explicit_mods()
    {
        var item = new Item(1, ["Mods"], 0)
        {
            Rarity = ItemRarity.Magic, Identified = false,
            Mods = [new(ModSection.Explicit, 0x1234, [3]), new(ModSection.Implicit, 0x5654, [10])],
        };
        Assert.Equal("01000000" + "0000" + "01" + "00" + "0000" + "1100" + "00" + "01" + "5456" + "0A" + "00" + "00",
            Convert.ToHexString(item.Payload()));
    }

    [Fact]
    public void Flask_writes_charges_as_a_signed_compact_int()
    {
        var flask = new Item(1, Flask, 2) { Charges = 21 };
        byte[] p = flask.Payload();
        // Base (7) then Quality (1), then Charges.
        Assert.Equal(0x15, p[6 + 7 + 1]);
        Assert.Equal(6 + 7 + 1 + 1 + 9, p.Length);
    }

    [Fact]
    public void Sockets_carry_colours_links_and_gems()
    {
        var gem = new Item(7, ["Base", "SkillGem", "Quality"], 3);
        var wand = new Item(1, ["Sockets"], 0) { Sockets = [new(SocketColour.Blue, gem), new(SocketColour.Red)] };
        Assert.Equal("01000000" + "0000" + "48" + "07" + Convert.ToHexString(gem.Payload()) + "02" + "01" + "01",
            Convert.ToHexString(wand.Payload()));
    }

    [Fact]
    public void Shield_armour_component_is_one_byte()
    {
        var shield = new Item(1, ["Base", "Armour", "Sockets"], 2);
        byte[] p = shield.Payload();
        Assert.Equal(0x0F, p.Length);   // 6 header + 7 Base + 1 Armour + 1 Sockets
        Assert.Equal(0, p[13]);         // Armour byte
        Assert.Equal(0, p[14]);         // no sockets
    }

    [Fact]
    public void Unsupported_components_are_refused()
    {
        Assert.False(Item.CanSerialize(["Base", "MapKey"]));
        Assert.Throws<ArgumentException>(() => new Item(1, ["MapKey"], 0).Payload());
    }

    [Fact]
    public void Add_inventory_with_items()
    {
        var item = new Item(1, ["Base"], 2);
        var bytes = InstanceInventories.AddInventory(3, InstanceInventories.PlayerDefaults[2], items: [new InventoryItem(9, 0, 1, item)]);
        Assert.Equal("00C0" + "00" + "00000003" + "02" + "02" + "02" + "04" + "03" + "01" +
                     "00000009" + "00" + "01" + Convert.ToHexString(item.Wire()) + "00" + "00", Convert.ToHexString(bytes));
    }

    [Fact]
    public void Equipment_and_cursor_are_single_item_inventories()
    {
        Assert.True(InstanceInventories.PlayerDefaults[2].SingleItem);    // Weapon1
        Assert.True(InstanceInventories.PlayerDefaults[12].SingleItem);   // Cursor1
        Assert.False(InstanceInventories.PlayerDefaults[0].SingleItem);   // MainInventory1
        Assert.False(InstanceInventories.PlayerDefaults[11].SingleItem);  // Flask1
    }

    [Fact]
    public void Inventory_change_layout()
    {
        var item = new Item(1, ["Base"], 2);
        Assert.Equal("00BF" + "00" + "0000000D" + "00000002" + "00000001" + "00000007" + "00000001" + "00000008" + "00" + "00" +
                     Convert.ToHexString(item.Wire()) + "00",
            Convert.ToHexString(InstanceItems.InventoryChange(13, 2, [7], [new InventoryItem(8, 0, 0, item)])));
    }

    [Fact]
    public void Pick_up_masks_the_side_channel_bits()
    {
        // Captured 2026-10-18: a click on item 2 in the main inventory (id 1).
        var p = InstanceItems.ReadPickUpItem(Convert.FromHexString("00283D0000010000000203B85A"));
        Assert.Equal((1u, 2u, (byte)3), (p.InventoryId, p.ItemId, p.A));
        Assert.Equal(13, InstanceProtocol.KnownClientLength(InstanceItems.PickUpItemId));
    }

    [Fact]
    public void Item_action_layout()
    {
        // Captured 2026-10-19: a held Scroll Fragment clicked on item 197 (0xc5) in the main inventory (id 1, HNC bits set).
        var a = InstanceItems.ReadItemAction(Convert.FromHexString("004A29800001000000C530036774"));
        Assert.Equal((1u, 197u, (byte)0, (byte)3), (a.InventoryId, a.ItemId, a.Flags, a.B));
        Assert.Equal(14, InstanceProtocol.KnownClientLength(InstanceItems.ItemActionId));
    }

    [Fact]
    public void Split_stack_layout()
    {
        // Captured 2026-10-19: 10 split off item 273 (0x111) in the main inventory.
        var s = InstanceItems.ReadSplitStack(Convert.FromHexString("007C00000001000001110000000A037F67"));
        Assert.Equal((1u, 273u, 10u, (byte)3), (s.InventoryId, s.ItemId, s.Amount, s.B));
        Assert.Equal(17, InstanceProtocol.KnownClientLength(InstanceItems.SplitStackId));
    }

    [Fact]
    public void Place_item_layout()
    {
        // Captured 2026-10-18: the held item put into the main inventory (id 1) at (8, 2).
        var p = InstanceItems.ReadPlaceItem(Convert.FromHexString("002A0000000100000008000000020003001F"));
        Assert.Equal((1u, 8u, 2u, (byte)3), (p.InventoryId, p.X, p.Y, p.B));
        Assert.Equal(18, InstanceProtocol.KnownClientLength(InstanceItems.PlaceItemId));
    }

    [Fact]
    public void Socket_gem_layout()
    {
        // Captured 2026-10-18: the held gem into socket 1 of item 1 in Weapon1 (inventory 3).
        var s = InstanceItems.ReadSocketGem(Convert.FromHexString("003000000003000000010000000188A7"));
        Assert.Equal(new InstanceItems.SocketGem(3, 1, 1), s);
        Assert.Equal(16, InstanceProtocol.KnownClientLength(InstanceItems.SocketGemId));
    }

    [Fact]
    public void Gem_level_up_and_lock_layout()
    {
        // Captured 2026-10-23: the "+" on socket 0 of item 1 in Weapon1 (inventory 3), and the right-click lock.
        Assert.Equal(new InstanceItems.SocketGem(3, 1, 0),
            InstanceItems.ReadLevelUpGem(Convert.FromHexString("0031000000030000000100000000")));
        Assert.Equal(14, InstanceProtocol.KnownClientLength(InstanceItems.LevelUpGemId));
        Assert.Equal(new InstanceItems.LockGem(3, 1, 0, true),
            InstanceItems.ReadLockGem(Convert.FromHexString("00D700000003000000010000000001")));
        Assert.Equal(new InstanceItems.LockGem(3, 1, 1, false),
            InstanceItems.ReadLockGem(Convert.FromHexString("00D700000003000000010000000100")));
        Assert.Equal(15, InstanceProtocol.KnownClientLength(InstanceItems.LockGemId));
    }

    [Fact]
    public void Skill_gem_component_carries_the_level_lock_flag()
    {
        // SkillGem: i32 experience, u32 variant, u8 flags (+0x10, bit 2 = level locked), u16.
        var gem = new Item(7, ["SkillGem"], 0) { GemExperience = 378, GemVariant = 3, GemFlags = 4 };
        Assert.Equal("070000000000" + "7A010000" + "03000000" + "24" + "0000", Convert.ToHexString(gem.Payload()));
    }

    [Fact]
    public void Command_framing_and_destroy()
    {
        // The town popup's Destroy: command 9 (Commands.dat DestroyCursorItem), no argument.
        byte[] m = Convert.FromHexString("0016090000");
        Assert.Equal(5, InstanceItems.CommandLength(m));
        Assert.Equal(new InstanceItems.Command(9, ""), InstanceItems.ReadCommand(m));
        Assert.Equal(8, InstanceItems.CommandLength(Convert.FromHexString("001601000361")));
        var split = InstanceProtocol.SplitClientMessages(Convert.FromHexString("00160900000029"), out _, out var unknown);
        Assert.Equal([(ushort)0x16, (ushort)0x29], split.Select(s => s.Id));
        Assert.Empty(unknown);
    }

    [Fact]
    public void Visible_item_and_world_item_layouts()
    {
        var w = new BlobWriter();
        new VisibleItem(0, 0x40a1, 2).Write(w);
        Assert.Equal("00" + "01" + "A140" + "00000000" + "00" + "0000" + "00000000" + "00" + "02" + "A140" + "00", Convert.ToHexString(w.ToArray()));
        var item = new Item(1, ["Base"], 2);
        Assert.Equal("0000" + "0A000000" + "14000000" + "00000000" + "0000" + "00" + "00" + "00" + "00000000" + "00" + "00000000" +
                     Convert.ToHexString(item.Payload()) + "00" + "00", Convert.ToHexString(new WorldItemObject(10, 20, item).Blob()));
    }
}
