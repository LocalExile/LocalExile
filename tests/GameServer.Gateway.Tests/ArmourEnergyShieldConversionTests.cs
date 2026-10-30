using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ArmourEnergyShieldConversionTests : IDisposable
{
    const string Full = "gain_maximum_life_instead_of_maximum_es_from_armour";
    const string Percent = "gain_maximum_life_instead_of_maximum_es_from_armour_at_x%_of_the_value";
    readonly string _name = "ArmourESLife" + Guid.NewGuid().ToString("N");

    public ArmourEnergyShieldConversionTests() => TestSaves.Ensure();

    ClientState Character() => new(new(0, "ArmourESLife", 128, 128, 32, 32, null, []), 0, 1)
        { Character = _name, ClassRow = SkillCatalog.WitchRow };

    PlayerInventories Inventory => PlayerInventories.For(_name, Program.Items);

    static ItemMod Mod(string stat, int value)
    {
        var row = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == stat));
        return new(ModSection.Explicit, row.Hash, row.Stats.Select(s => s.Id == stat ? value : 0).ToArray());
    }

    static Item Armour(string name, byte roll, byte quality = 0, params (string Stat, int Value)[] mods) =>
        ItemCatalog.Create(Program.Items.Find(name)!, defenceBaseRoll: roll) with
        { Quality = quality, Mods = mods.Select(m => Mod(m.Stat, m.Value)).ToArray() };

    static Item Ring(params (string Stat, int Value)[] mods) => ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        { Mods = mods.Select(m => Mod(m.Stat, m.Value)).ToArray() };

    static (int Armour, int Evasion, int EnergyShield) Local(Item item) =>
        ItemDefences.Calculate(item, Program.Items.Defences(Program.Items.ByHash(item.TypeHash)!)!.Value, Program.Items.Mods);

    static Dictionary<uint, int> Published(ClientState conn) =>
        ((List<(uint, int)>)typeof(Program).GetMethod("PlayerStatsWithCleared",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!)
        .ToDictionary(s => s.Item1, s => s.Item2);

    (InventoryItem Body, InventoryItem Gloves, InventoryItem Shield) Equip69ES()
    {
        var body = Armour("Simple Robe", 16, 20, ("local_energy_shield", 10), ("local_energy_shield_+%", 50));
        var gloves = Armour("Wool Gloves", 31, 0, ("local_energy_shield", 6));
        var shield = Armour("Twig Spirit Shield", 0, 0, ("local_energy_shield", 1));
        Assert.Equal(49, Local(body).EnergyShield); // round((17 rolled base + 10) * 1.5 * 1.2).
        Assert.Equal(13, Local(gloves).EnergyShield);
        Assert.Equal(7, Local(shield).EnergyShield);
        return (Inventory.Add(PlayerInventories.BodyArmour, 0, 0, body),
            Inventory.Add(PlayerInventories.Gloves, 0, 0, gloves),
            Inventory.Add(PlayerInventories.Offhand1, 0, 0, shield));
    }

    [Theory]
    [InlineData(true, 0, 69)]
    [InlineData(false, 50, 33)]
    [InlineData(true, 50, 69)]
    [InlineData(false, 0, 0)]
    public void Conversion_uses_final_local_ES_and_full_conversion_takes_precedence(
        bool full, int percent, int addedLife)
    {
        var conn = Character();
        var bare = Program.PoolsFor(conn, 100);
        _ = Equip69ES();
        var conversion = Ring((Full, full ? 1 : 0), (Percent, percent));
        Inventory.Add(6, 0, 0, conversion);
        var pools = Program.PoolsFor(conn, 100);

        // Native truncates per item: 50% gives floor(49/2)+floor(13/2)+floor(7/2)=33, not floor(69/2)=34.
        Assert.Equal(bare.Life + (uint)addedLife, pools.Life);
        Assert.Equal(addedLife == 0 ? 71u : 0u, pools.EnergyShield); // Unconverted local69 * 1.03, rounded once.
    }

    [Fact]
    public void Global_ES_remains_ES_and_global_ES_increases_do_not_scale_the_converted_life()
    {
        var conn = Character();
        _ = Equip69ES();
        Inventory.Add(7, 0, 0, Ring(("base_maximum_energy_shield", 100), ("maximum_energy_shield_+%", 100)));
        var before = Program.PoolsFor(conn, 100);
        Assert.Equal(343u, before.EnergyShield); // (69 local item ES + 100 flat global ES) * 2.03 = 343.07.

        Inventory.Add(6, 0, 0, Ring((Full, 1)));
        var after = Program.PoolsFor(conn, 100);

        Assert.Equal(before.Life + 69u, after.Life);
        Assert.Equal(203u, after.EnergyShield); // The independent 100 flat global ES still receives global increases.
    }

    [Fact]
    public void Converted_ES_is_base_life_before_global_life_increases()
    {
        var conn = Character();
        _ = Equip69ES();
        Inventory.Add(7, 0, 0, Ring(("maximum_life_+%", 100)));
        var before = Program.PoolsFor(conn, 100);

        Inventory.Add(6, 0, 0, Ring((Full, 1)));
        var after = Program.PoolsFor(conn, 100);

        Assert.Equal(before.Life + 138u, after.Life);
        Assert.Equal(0u, after.EnergyShield);
    }

    [Fact]
    public void Item_ES_prohibitions_are_applied_before_conversion_without_removing_other_global_ES()
    {
        var conn = Character();
        var bare = Program.PoolsFor(conn, 100);
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Simple Robe", 31, 20,
            ("local_energy_shield", 100), ("local_unique_tabula_rasa_no_requirement_or_energy_shield", 1)));
        Inventory.Add(6, 0, 0, Ring((Full, 1)));
        Inventory.Add(7, 0, 0, Ring(("base_maximum_energy_shield", 100)));

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal(bare.Life, pools.Life);
        Assert.Equal(103u, pools.EnergyShield);
    }

    [Fact]
    public void Only_the_active_shield_contributes_converted_life_after_weapon_swap()
    {
        var conn = Character();
        var bare = Program.PoolsFor(conn, 100);
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Simple Robe", 16, 20,
            ("local_energy_shield", 10), ("local_energy_shield_+%", 50)));
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Armour("Twig Spirit Shield", 0, 0, ("local_energy_shield", 1)));
        Inventory.Add(PlayerInventories.Offhand2, 0, 0, Armour("Twig Spirit Shield", 31, 0, ("local_energy_shield", 1)));
        Inventory.Add(6, 0, 0, Ring((Full, 1)));

        Assert.Equal(bare.Life + 56u, Program.PoolsFor(conn, 100).Life);
        Inventory.SwapWeaponSets();
        var swapped = Program.PoolsFor(conn, 100);
        Assert.Equal(bare.Life + 58u, swapped.Life);
        Assert.Equal(0u, swapped.EnergyShield);
        Inventory.SwapWeaponSets();
        Assert.Equal(bare.Life + 56u, Program.PoolsFor(conn, 100).Life);
    }

    [Fact]
    public void Requirement_disabled_armour_and_backpack_items_do_not_add_converted_life()
    {
        var conn = Character();
        var bare = Program.PoolsFor(conn, 100);
        var inactive = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Vaal Regalia", 31, 20,
            ("local_energy_shield", 1000)));
        Inventory.Add(PlayerInventories.MainInventory, 0, 0, Armour("Simple Robe", 31, 20,
            ("local_energy_shield", 1000)));
        Inventory.Add(PlayerInventories.Gloves, 0, 0, Armour("Wool Gloves", 31, 0, ("local_energy_shield", 6)));
        Inventory.Add(6, 0, 0, Ring((Full, 1)));

        var pools = Program.PoolsFor(conn, 100);

        Assert.Contains(inactive.Id, Program.InactiveItems[_name]);
        Assert.Equal(bare.Life + 13u, pools.Life);
        Assert.Equal(0u, pools.EnergyShield);
    }

    [Fact]
    public void Necromantic_Aegis_excludes_the_shields_converted_life()
    {
        var conn = Character();
        var bare = Program.PoolsFor(conn, 100);
        _ = Equip69ES();
        Inventory.Add(6, 0, 0, Ring((Full, 1), ("minions_get_shield_stats_instead_of_you", 1)));

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal(bare.Life + 62u, pools.Life); // 49 body + 13 gloves; the shield's 7 belongs to the minions.
        Assert.Equal(0u, pools.EnergyShield);
    }

    [Fact]
    public void The_native_no_shield_defences_stat_also_excludes_the_shields_converted_life()
    {
        var conn = Character();
        _ = Equip69ES();
        // This native stat has no current Mods.dat or passive source; exercise its input at the conversion boundary.
        int Get(string name) => name is Full or "gain_no_defences_from_equipped_shield" ? 1 : 0;
        var converted = ((int Life, int EnergyShield))typeof(Program).GetMethod("ConvertEquipmentEnergyShield",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn, (Func<string, int>)Get, 69])!;

        Assert.Equal((62, 0), converted);
    }

    [Fact]
    public void Removing_conversion_restores_gear_ES_and_clears_converted_life_in_the_next_panel_update()
    {
        var conn = Character();
        var bare = Program.PoolsFor(conn, 100);
        _ = Equip69ES();
        var ring = Inventory.Add(6, 0, 0, Ring((Full, 1)));
        uint life = Program.Stats.Key("maximum_life")!.Value;
        uint es = Program.Stats.Key("maximum_energy_shield")!.Value;
        conn.Pools = Program.PoolsFor(conn, 100);
        var before = Published(conn);
        Assert.Equal(bare.Life + 69u, conn.Pools.Life);
        Assert.Equal((int)conn.Pools.Life, before[life]);
        Assert.Equal(0, before[es]);

        Assert.NotNull(Inventory.Remove(6, ring.Id));
        conn.Pools = Program.PoolsFor(conn, 100);
        var after = Published(conn);

        Assert.Equal(bare.Life, conn.Pools.Life);
        Assert.Equal(71u, conn.Pools.EnergyShield);
        Assert.Equal((int)bare.Life, after[life]);
        Assert.Equal(71, after[es]);
    }

    [Fact]
    public void Large_converted_life_and_life_increases_saturate_instead_of_wrapping()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Simple Robe", 31, 20,
            ("local_energy_shield", int.MaxValue)));
        Inventory.Add(6, 0, 0, Ring((Full, 1), ("maximum_life_+%", 100)));

        var pools = Program.PoolsFor(conn, 100);

        Assert.Equal((uint)int.MaxValue, pools.Life);
        Assert.Equal(0u, pools.EnergyShield);
        conn.Pools = pools;
        Assert.Equal(int.MaxValue, Published(conn)[Program.Stats.Key("maximum_life")!.Value]);
    }

    [Fact]
    public void Apostate_hidden_requirements_keep_the_conversion_active_without_the_bases_intelligence()
    {
        var conn = Character();
        var bare = Program.PoolsFor(conn, 100);
        var body = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Cabalist Regalia", 16, 20,
            ("local_unique_tabula_rasa_no_requirement_or_energy_shield", 1), (Full, 1)));
        Inventory.Add(PlayerInventories.Gloves, 0, 0, Armour("Wool Gloves", 31, 0, ("local_energy_shield", 6)));

        var pools = Program.PoolsFor(conn, 100);

        Assert.DoesNotContain(body.Id, Program.InactiveItems[_name]);
        Assert.Equal(bare.Life + 13u, pools.Life); // Cabalist Regalia contributes zero ES; the gloves' 13 becomes Life.
        Assert.Equal(0u, pools.EnergyShield);
    }

    public void Dispose()
    {
        PlayerInventories.Forget(_name);
        Program.InactiveItems.TryRemove(_name, out _);
    }
}
