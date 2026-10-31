using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ConditionalItemEnergyShieldTests : IDisposable
{
    const string Conditional = "local_maximum_energy_shield_+%_if_item_corrupted";
    const string GlobalIncrease = "maximum_energy_shield_+%";
    const string Conversion = "gain_maximum_life_instead_of_maximum_es_from_armour";
    readonly string _name = "ConditionalES" + Guid.NewGuid().ToString("N");

    public ConditionalItemEnergyShieldTests() => TestSaves.Ensure();

    ClientState Character() => new(new(0, "ConditionalES", 128, 128, 32, 32, null, []), 0, 1)
        { Character = _name, ClassRow = SkillCatalog.WitchRow };

    PlayerInventories Inventory => PlayerInventories.For(_name, Program.Items);

    static ItemMod Mod(string stat, int value, ModSection section = ModSection.Explicit)
    {
        var row = Program.Items.Mods.All.First(mod => mod.Stats.Any(s => s.Id == stat));
        return new(section, row.Hash, row.Stats.Select(s => s.Id == stat ? value : 0).ToArray());
    }

    static Item ConditionalArmour(string name = "Simple Robe", bool corrupted = false) =>
        ItemCatalog.Create(Program.Items.Find(name)!, defenceBaseRoll: 16) with
        {
            Quality = 20, Corrupted = corrupted,
            Mods = [Mod("local_energy_shield", 10), Mod("local_energy_shield_+%", 50),
                Mod(Conditional, 10, ModSection.Implicit)],
        };

    static Item Ring(params (string Stat, int Value)[] mods) =>
        ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        { Mods = mods.Select(mod => Mod(mod.Stat, mod.Value)).ToArray() };

    static int LocalES(Item item) => ItemDefences.Calculate(item,
        Program.Items.Defences(Program.Items.ByHash(item.TypeHash)!)!.Value, Program.Items.Mods).EnergyShield;

    void Equip13ESGloves()
    {
        var gloves = ItemCatalog.Create(Program.Items.Find("Wool Gloves")!, defenceBaseRoll: 31) with
        { Mods = [Mod("local_energy_shield", 6)] };
        Assert.Equal(13, LocalES(gloves));
        Inventory.Add(PlayerInventories.Gloves, 0, 0, gloves);
    }

    [Theory]
    [InlineData(false, 100, 126)]
    [InlineData(true, 110, 132)]
    public void Corruption_enables_a_global_increase_without_changing_the_item_tooltip(
        bool corrupted, int expectedGlobalIncrease, uint expectedES)
    {
        var conn = Character();
        var body = ConditionalArmour(corrupted: corrupted);
        var ring = Ring((GlobalIncrease, 100));
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, body);
        Inventory.Add(6, 0, 0, ring);
        Equip13ESGloves();

        Assert.Equal(49, LocalES(body)); // round((17 base + 10 flat) * 1.5 local * 1.2 quality), in both states.
        var global = ItemModStats.Global([body, ring], Program.Items.Mods, Program.Stats);
        Assert.Equal(expectedGlobalIncrease, global[Program.Stats.Key(GlobalIncrease)!.Value]);
        var pools = Program.PoolsFor(conn, 100);
        // The conditional increase reaches all 62 gear ES alongside 100% increased ES and Witch Intelligence's 3%.
        // Round the final total: 62 * 2.03 = 125.86 -> 126, or 62 * 2.13 = 132.06 -> 132 when corrupted.
        Assert.Equal(expectedES, pools.EnergyShield);
    }

    [Fact]
    public void Conditional_ES_does_not_increase_converted_life_but_still_scales_independent_global_ES()
    {
        var conn = Character();
        var bare = Program.PoolsFor(conn, 100);
        var body = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, ConditionalArmour());
        Equip13ESGloves();
        Inventory.Add(6, 0, 0, Ring((Conversion, 1)));
        Inventory.Add(7, 0, 0, Ring(("base_maximum_energy_shield", 100), (GlobalIncrease, 100)));
        var before = Program.PoolsFor(conn, 100);

        Assert.NotNull(Inventory.Change(PlayerInventories.BodyArmour, body.Id, item => item with { Corrupted = true }));
        var after = Program.PoolsFor(conn, 100);

        Assert.Equal(bare.Life + 62u, before.Life);
        Assert.Equal(before.Life, after.Life); // Only the displayed local item ES converts to Life.
        Assert.Equal(203u, before.EnergyShield);
        Assert.Equal(213u, after.EnergyShield); // The separate 100 global ES receives the extra global 10%.
    }

    [Theory]
    [InlineData(PlayerInventories.MainInventory, "Simple Robe")]
    [InlineData(PlayerInventories.BodyArmour, "Vaal Regalia")]
    [InlineData(PlayerInventories.Offhand2, "Twig Spirit Shield")]
    public void Backpack_requirement_disabled_and_inactive_weapon_set_sources_do_not_enable_the_condition(
        byte row, string baseName)
    {
        var conn = Character();
        var source = Inventory.Add(row, 0, 0, ConditionalArmour(baseName, corrupted: true));
        Inventory.Add(6, 0, 0, Ring(("base_maximum_energy_shield", 100)));

        var pools = Program.PoolsFor(conn, 100);

        if (row == PlayerInventories.BodyArmour) Assert.Contains(source.Id, Program.InactiveItems[_name]);
        Assert.Equal(103u, pools.EnergyShield);
    }

    public void Dispose()
    {
        PlayerInventories.Forget(_name);
        Program.InactiveItems.TryRemove(_name, out _);
    }
}
