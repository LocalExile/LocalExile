using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class SpecialLocalDefenceTests : IDisposable
{
    const string Tabula = "local_unique_tabula_rasa_no_requirement_or_energy_shield";
    const string NoEnergyShield = "local_no_energy_shield";
    readonly string _name = "LocalDefence" + Guid.NewGuid().ToString("N");

    public SpecialLocalDefenceTests() => TestSaves.Ensure();

    ClientState Character() => new(new(0, "LocalDefences", 128, 128, 32, 32, null, []), 0, 1)
        { Character = _name, ClassRow = SkillCatalog.WitchRow };

    PlayerInventories Inventory => PlayerInventories.For(_name, Program.Items);

    static ItemMod Mod(string stat, int value, ModSection section = ModSection.Explicit)
    {
        var row = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == stat));
        return new(section, row.Hash, row.Stats.Select(s => s.Id == stat ? value : 0).ToArray());
    }

    static Item Armour(string name, byte roll = 16, byte quality = 0, params (string Stat, int Value)[] mods) =>
        ItemCatalog.Create(Program.Items.Find(name)!, defenceBaseRoll: roll) with
        { Quality = quality, Mods = mods.Select(m => Mod(m.Stat, m.Value)).ToArray() };

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

    static bool HasESForRecovery(ClientState conn, Func<string, int> get) =>
        (bool)typeof(Program).GetMethod("PlayerHasEnergyShieldForRecovery",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn, get])!;

    [Theory]
    [InlineData(Tabula, 0, 49)]
    [InlineData(Tabula, 1, 0)]
    [InlineData(NoEnergyShield, 1, 0)]
    public void ES_prohibitions_override_the_rolled_base_flat_mods_increases_and_quality_only_for_ES(
        string prohibition, int value, int expectedES)
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Simple Robe", 16, 20,
            ("local_base_physical_damage_reduction_rating", 7),
            ("local_base_evasion_rating", 11),
            ("local_energy_shield", 10),
            ("local_armour_and_evasion_and_energy_shield_+%", 50),
            (prohibition, value)));

        // The rounded base is 17 ES; (17 + 10) * 1.5 * 1.2 = 48.6 -> 49 without a prohibition.
        // These flags do not remove the independent local armour or evasion contributions.
        Assert.Equal((13, 20, expectedES), Equipped(conn));
    }

    [Theory]
    [InlineData(ModSection.Implicit)]
    [InlineData(ModSection.Explicit)]
    [InlineData(ModSection.Hidden)]
    public void Tabulas_native_mod_removes_ES_from_every_mod_list_even_while_unidentified(ModSection section)
    {
        var conn = Character();
        var native = Program.Items.Mods.ById("RemoveStatsFromTabulaRasa")!;
        Assert.Equal(Tabula, Assert.Single(native.Stats).Id);
        Assert.True(Assert.Single(native.Stats).Local);
        var item = Armour("Simple Robe", 31, 20) with
        {
            Identified = false,
            Mods = [native.Roll(section, new Random(75)), Mod("local_energy_shield", 100)],
        };
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, item);

        Assert.Equal((0, 0, 0), Equipped(conn));
    }

    [Fact]
    public void An_ES_prohibition_does_not_suppress_ES_from_another_equipped_item()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Simple Robe", 16, 20,
            ("local_energy_shield_+%", 50)));
        Inventory.Add(PlayerInventories.Gloves, 0, 0, Armour("Rawhide Gloves", 0, 0,
            (Tabula, 1), ("local_energy_shield", 100)));

        Assert.Equal((0, 13, 31), Equipped(conn));
    }

    [Fact]
    public void A_backpack_prohibition_does_not_change_the_worn_robe()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Simple Robe", 16, 20,
            ("local_energy_shield_+%", 50)));
        Inventory.Add(PlayerInventories.MainInventory, 0, 0, Armour("Simple Robe", 31, 20,
            (Tabula, 1), ("local_energy_shield", 100)));

        Assert.Equal((0, 0, 31), Equipped(conn));
    }

    [Fact]
    public void The_active_shields_local_ES_prohibition_follows_weapon_swap()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Armour("Simple Robe", 16, 20,
            ("local_energy_shield_+%", 50)));
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Armour("Twig Spirit Shield", 31, 20,
            (Tabula, 1), ("local_energy_shield_+%", 50)));
        Inventory.Add(PlayerInventories.Offhand2, 0, 0, Armour("Twig Spirit Shield", 31, 20,
            ("local_energy_shield_+%", 50)));

        Assert.Equal((0, 0, 31), Equipped(conn));
        Inventory.SwapWeaponSets();
        Assert.Equal((0, 0, 45), Equipped(conn)); // 31 robe + round(8 shield * 1.5 * 1.2) = 31 + 14.
        Inventory.SwapWeaponSets();
        Assert.Equal((0, 0, 31), Equipped(conn));
    }

    [Fact]
    public void Adding_and_removing_the_prohibition_clears_and_restores_the_real_ES_pool_and_panel()
    {
        var conn = Character();
        var item = Armour("Simple Robe", 16, 20, ("local_energy_shield", 10), ("local_energy_shield_+%", 50));
        var placed = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, item);
        uint key = Program.Stats.Key("maximum_energy_shield")!.Value;
        conn.Pools = Program.PoolsFor(conn, 100);
        Assert.Equal(50u, conn.Pools.EnergyShield); // 49 item ES * 1.03 from 32 Intelligence = 50.47, rounding to 50.
        Assert.Equal(50, Published(conn)[key]);

        Assert.NotNull(Inventory.Change(PlayerInventories.BodyArmour, placed.Id,
            i => i with { Mods = [.. i.Mods, Mod(Tabula, 1)] }));
        conn.Pools = Program.PoolsFor(conn, 100);
        Assert.Equal(0u, conn.Pools.EnergyShield);
        Assert.Equal(0, Published(conn)[key]);

        Assert.NotNull(Inventory.Change(PlayerInventories.BodyArmour, placed.Id, _ => item));
        conn.Pools = Program.PoolsFor(conn, 100);
        Assert.Equal(50u, conn.Pools.EnergyShield);
        Assert.Equal(50, Published(conn)[key]);

        Assert.NotNull(Inventory.Remove(PlayerInventories.BodyArmour, placed.Id));
        conn.Pools = Program.PoolsFor(conn, 100);
        Assert.Equal(0u, conn.Pools.EnergyShield);
        Assert.Equal(0, Published(conn)[key]);
    }

    [Theory]
    [InlineData(Tabula)]
    [InlineData(NoEnergyShield)]
    public void Prohibited_item_ES_does_not_create_a_phantom_energy_shield_recovery_guard(string prohibition)
    {
        var conn = Character();
        var item = Armour("Simple Robe", 31, 20, ("local_energy_shield", 100), (prohibition, 1));
        var placed = Inventory.Add(PlayerInventories.BodyArmour, 0, 0, item);
        conn.Pools = Program.PoolsFor(conn, 100);

        Assert.False(HasESForRecovery(conn, _ => 0));
        Assert.True(HasESForRecovery(conn, name => name == "base_maximum_energy_shield" ? 1 : 0));
        Assert.NotNull(Inventory.Change(PlayerInventories.BodyArmour, placed.Id,
            _ => Armour("Simple Robe", 31, 20, ("local_energy_shield", 100))));
        conn.Pools = Program.PoolsFor(conn, 100);
        Assert.True(HasESForRecovery(conn, _ => 0));
    }

    public void Dispose()
    {
        PlayerInventories.Forget(_name);
        Program.InactiveItems.TryRemove(_name, out _);
    }
}
