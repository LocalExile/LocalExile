using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class EquippedDefenceStatsTests : IDisposable
{
    readonly string _name = "ItemDefence" + Guid.NewGuid().ToString("N");

    public EquippedDefenceStatsTests() => TestSaves.Ensure();

    ClientState Character() => new(new(0, "ItemDefences", 128, 128, 32, 32, null, []), 0, 1)
    { Character = _name, ClassRow = 4 }; // Duelist: 23 strength/dexterity meets Scale Vest's 17/17 requirements.

    PlayerInventories Inventory => PlayerInventories.For(_name, Program.Items);

    static ItemMod Mod(string stat, int value)
    {
        // Native mod rows preserve the real IsLocal classification and exact value count.
        var row = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == stat));
        return new(ModSection.Explicit, row.Hash, row.Stats.Select(s => s.Id == stat ? value : 0).ToArray());
    }

    static Item Armour(string name, byte quality = 0, params (string Stat, int Value)[] mods) =>
        ItemCatalog.Create(Program.Items.Find(name)!) with
        { DefenceBaseRoll = 0, Quality = quality, Mods = mods.Select(m => Mod(m.Stat, m.Value)).ToArray() };

    static (int Armour, int Evasion, int EnergyShield) Equipped(ClientState conn)
    {
        var (armour, evasion, shield, _) = ((decimal, decimal, int, decimal))typeof(Program).GetMethod("EquippedBaseDefences",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn, null])!;
        return ((int)Math.Min(int.MaxValue, armour), (int)Math.Min(int.MaxValue, evasion), shield);
    }

    static Dictionary<uint, int> Published(ClientState conn) =>
        ((List<(uint, int)>)typeof(Program).GetMethod("PlayerStatsWithCleared",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!)
        .ToDictionary(s => s.Item1, s => s.Item2);

    [Fact]
    public void Local_flat_increased_armour_and_quality_compose_before_one_final_round()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Plate Vest", 20,
            ("local_base_physical_damage_reduction_rating", 10),
            ("local_physical_damage_reduction_rating_+%", 50)));

        // (19 base + 10 flat) * 1.50 local increase * 1.20 quality = 52.2 -> 52.
        // Flooring the local result before quality would incorrectly produce 51.
        Assert.Equal((52, 0, 0), Equipped(conn));
    }

    [Theory]
    [InlineData(0, 29)]
    [InlineData(20, 34)]
    public void Armour_midpoints_round_up_and_quality_is_a_separate_multiplier(byte quality, int expected)
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0,
            Armour("Plate Vest", quality, ("local_physical_damage_reduction_rating_+%", 50)));

        Assert.Equal(expected, Equipped(conn).Armour); // 19 * 1.5 = 28.5; with 20 quality, 34.2.
    }

    [Fact]
    public void Evasion_uses_its_own_flat_local_increase_and_quality()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.Gloves, 0, 0, Armour("Rawhide Gloves", 20,
            ("local_base_evasion_rating", 7), ("local_evasion_rating_+%", 50)));

        Assert.Equal((0, 36, 0), Equipped(conn)); // (13 + 7) * 1.5 * 1.2.
    }

    [Fact]
    public void Hybrid_modifiers_add_once_only_to_their_applicable_defences()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Scale Vest", 20,
            ("local_base_physical_damage_reduction_rating", 13),
            ("local_base_evasion_rating", 3),
            ("local_evasion_rating_and_energy_shield", 10),
            ("local_physical_damage_reduction_rating_+%", 25),
            ("local_evasion_rating_+%", 50),
            ("local_armour_and_evasion_+%", 20),
            ("local_armour_and_energy_shield_+%", 10),
            ("local_evasion_and_energy_shield_+%", 30),
            ("local_armour_and_evasion_and_energy_shield_+%", 5)));

        var gear = Equipped(conn);
        Assert.Equal(77, gear.Armour); // (27 + 13) * (1 + .25 + .20 + .10 + .05) * 1.20.
        Assert.Equal(98, gear.Evasion); // (27 + 3 + 10) * (1 + .50 + .20 + .30 + .05) * 1.20.
    }

    [Fact]
    public void Alternate_armour_quality_suppresses_the_quality_defence_multiplier()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Scale Vest", 20,
            ("local_physical_damage_reduction_rating_+%", 50),
            ("local_evasion_rating_+%", 50),
            ("local_quality_does_not_increase_defences", 1)));

        var gear = Equipped(conn);
        Assert.Equal(41, gear.Armour); // 27 * 1.50 = 40.5, no 20% quality multiplier.
        Assert.Equal(41, gear.Evasion);
    }

    [Fact]
    public void Local_added_quality_combines_with_the_item_quality_before_scaling_both_defences()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Scale Vest", 20,
            ("local_item_quality_+", 10), ("local_armour_and_evasion_+%", 50)));

        var gear = Equipped(conn);
        Assert.Equal(53, gear.Armour); // 27 * 1.5 * (1 + (20 + 10) / 100) = 52.65.
        Assert.Equal(53, gear.Evasion);
    }

    [Fact]
    public void Each_equipped_item_is_rounded_before_its_defence_is_added()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0,
            Armour("Scale Vest", 0, ("local_evasion_rating_+%", 50)));
        Inventory.Add(PlayerInventories.Gloves, 0, 0,
            Armour("Rawhide Gloves", 0, ("local_evasion_rating_+%", 50)));

        Assert.Equal(61, Equipped(conn).Evasion); // round(27 * 1.5) + round(13 * 1.5) = 41 + 20.
    }

    [Fact]
    public void Only_the_active_shield_contributes_and_weapon_swap_refreshes_its_local_defences()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Armour("Splintered Tower Shield", 20,
            ("local_base_physical_damage_reduction_rating", 1),
            ("local_physical_damage_reduction_rating_+%", 50)));
        Inventory.Add(PlayerInventories.Offhand2, 0, 0, Armour("Goathide Buckler", 20,
            ("local_base_evasion_rating", 6), ("local_evasion_rating_+%", 50)));

        Assert.Equal((18, 0, 0), Equipped(conn));
        Inventory.SwapWeaponSets();
        Assert.Equal((0, 36, 0), Equipped(conn));
        Inventory.SwapWeaponSets();
        Assert.Equal((18, 0, 0), Equipped(conn));
    }

    [Fact]
    public void Armour_in_the_backpack_does_not_contribute_its_base_mods_or_quality()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.MainInventory, 0, 0, Armour("Scale Vest", 20,
            ("local_base_physical_damage_reduction_rating", 100),
            ("local_base_evasion_rating", 100)));

        Assert.Equal((0, 0, 0), Equipped(conn));
    }

    [Fact]
    public void Requirement_disabled_equipment_contributes_no_local_defences()
    {
        var conn = Character();
        var placed = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Glorious Plate", 20,
            ("local_base_physical_damage_reduction_rating", 100),
            ("local_physical_damage_reduction_rating_+%", 50)));

        conn.Pools = Program.PoolsFor(conn, 100); // Duelist strength 23 cannot meet Glorious Plate's 191.
        Assert.Contains(placed.Id, Program.InactiveItems[_name]);
        Assert.Equal((0, 0, 0), Equipped(conn));
        Assert.Equal(0, conn.Pools.Armour);
        Assert.Equal(16, conn.Pools.Evasion); // 15 character base * 1.04 from Duelist dexterity = 15.6.
    }

    [Fact]
    public void Global_increases_apply_once_after_item_local_values_and_feed_the_real_panel()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Scale Vest", 20,
            ("local_base_physical_damage_reduction_rating", 13),
            ("local_base_evasion_rating", 3),
            ("local_physical_damage_reduction_rating_+%", 50),
            ("local_evasion_rating_+%", 50),
            ("physical_damage_reduction_rating_+%", 50),
            ("evasion_rating_+%", 50)));

        Assert.Equal((72, 54, 0), Equipped(conn));
        conn.Pools = Program.PoolsFor(conn, 100);
        Assert.Equal(108, conn.Pools.Armour); // 72 local armour * 1.50 global increase.
        Assert.Equal(106, conn.Pools.Evasion); // (15 character base + 54 local) * (1 + .50 + .04 dexterity).
        var panel = Program.CharacterPlayerStats(conn);
        Assert.Equal(conn.Pools.Armour, panel[Program.Stats.Key("physical_damage_reduction_rating")!.Value]);
        Assert.Equal(conn.Pools.Evasion, panel[Program.Stats.Key("evasion_rating")!.Value]);
    }

    [Fact]
    public void Unequipping_modded_armour_clears_its_values_in_the_next_full_panel_update()
    {
        var conn = Character();
        var placed = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Scale Vest", 20,
            ("local_base_physical_damage_reduction_rating", 13),
            ("local_base_evasion_rating", 3),
            ("local_armour_and_evasion_+%", 50)));
        conn.Pools = Program.PoolsFor(conn, 100);
        var before = Published(conn);
        Assert.Equal(72, before[Program.Stats.Key("physical_damage_reduction_rating")!.Value]);
        Assert.Equal(72, before[Program.Stats.Key("evasion_rating")!.Value]); // (15 character base + 54 local) * 1.04 = 71.76.

        Assert.NotNull(Inventory.Remove(PlayerInventories.BodyArmour, placed.Id));
        conn.Pools = Program.PoolsFor(conn, 100);
        var after = Published(conn);
        Assert.Equal(0, after[Program.Stats.Key("physical_damage_reduction_rating")!.Value]);
        Assert.Equal(16, after[Program.Stats.Key("evasion_rating")!.Value]); // The bare Duelist keeps round(15 * 1.04).
    }

    [Fact]
    public void Iron_Reflexes_converts_the_modified_item_evasion_without_dexterity_bonus()
    {
        var conn = Character();
        conn.Passives.Add(10661); // Iron Reflexes, the real passive stat rather than a display override.
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Scale Vest", 20,
            ("local_base_physical_damage_reduction_rating", 13),
            ("local_base_evasion_rating", 3),
            ("local_armour_and_evasion_+%", 50)));

        conn.Pools = Program.PoolsFor(conn, 100);
        Assert.Equal(141, conn.Pools.Armour); // 72 armour + (15 character base + 54 item evasion).
        Assert.Equal(0, conn.Pools.Evasion);
        var panel = Program.CharacterPlayerStats(conn);
        Assert.Equal(141, panel[Program.Stats.Key("physical_damage_reduction_rating")!.Value]);
        Assert.Equal(0, panel[Program.Stats.Key("evasion_rating")!.Value]);
    }

    [Fact]
    public void Energy_shield_uses_the_same_local_and_quality_item_calculation()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Simple Robe", 20,
            ("local_energy_shield", 10), ("local_energy_shield_+%", 50)));

        Assert.Equal((0, 0, 43), Equipped(conn)); // (14 minimum + 10 flat) * 1.50 * 1.20 = 43.2.
    }

    [Fact]
    public void Multiple_large_item_defences_do_not_wrap_the_equipped_total()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Plate Vest", 20,
            ("local_base_physical_damage_reduction_rating", int.MaxValue)));
        Inventory.Add(PlayerInventories.Helmet, 0, 0, Armour("Iron Hat", 20,
            ("local_base_physical_damage_reduction_rating", int.MaxValue)));
        Inventory.Add(PlayerInventories.Gloves, 0, 0, Armour("Rawhide Gloves", 20,
            ("local_base_evasion_rating", int.MaxValue)));
        Inventory.Add(PlayerInventories.Boots, 0, 0, Armour("Rawhide Boots", 20,
            ("local_base_evasion_rating", int.MaxValue)));

        Assert.Equal((int.MaxValue, int.MaxValue, 0), Equipped(conn));
    }

    public void Dispose()
    {
        PlayerInventories.Forget(_name);
        Program.InactiveItems.TryRemove(_name, out _);
    }
}
