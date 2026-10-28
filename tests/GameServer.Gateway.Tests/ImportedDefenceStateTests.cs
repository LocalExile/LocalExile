using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ImportedDefenceStateTests : IDisposable
{
    readonly string character = "ImportedDefence" + Guid.NewGuid().ToString("N");

    public ImportedDefenceStateTests() => TestSaves.Ensure();

    [Fact]
    public void Ordinary_creation_keeps_the_upstream_minimum_base_policy()
    {
        var type = Program.Items.Find("Plate Vest")!;
        for (int i = 0; i < 16; i++)
            Assert.Equal((byte)0, ItemCatalog.Create(type).DefenceBaseRoll);
    }

    [Theory]
    [InlineData(0, 34, false)]
    [InlineData(16, 41, false)]
    [InlineData(31, 49, false)]
    [InlineData(32, 34, false)]
    [InlineData(0, 34, true)]
    [InlineData(16, 41, true)]
    [InlineData(31, 49, true)]
    [InlineData(32, 34, true)]
    public void Explicit_saved_roll_reaches_the_equipped_local_defence_calculation(byte roll, int expectedArmour, bool ruthless)
    {
        var conn = new ClientState(new(0, "ImportedDefence", 128, 128, 32, 32, null, []), 0, 1)
            { Character = character, ClassRow = 4, Ruthless = ruthless };
        var catalog = Program.ItemsFor(conn);
        var mod = catalog.Mods.All.First(m => m.Stats.Any(s => s.Id == "local_physical_damage_reduction_rating_+%"));
        var item = ItemCatalog.Create(catalog.Find("Plate Vest")!, defenceBaseRoll: roll) with
        {
            Quality = 20,
            Mods = [new(ModSection.Explicit, mod.Hash,
                mod.Stats.Select(s => s.Id == "local_physical_damage_reduction_rating_+%" ? 50 : 0).ToArray())],
        };
        var restored = CharacterSaves.FromSaved(CharacterSaves.ToSaved(item, catalog.Mods), catalog);
        PlayerInventories.For(character, catalog).Add(PlayerInventories.BodyArmour, 0, 0, restored);

        var equipped = ((decimal Armour, decimal Evasion, int EnergyShield, decimal Ward))typeof(Program)
            .GetMethod("EquippedBaseDefences", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [conn, (Func<string, int>)(_ => 0)])!;

        Assert.Equal(roll, restored.DefenceBaseRoll);
        Assert.Equal(expectedArmour, equipped.Armour);
    }

    [Theory]
    [InlineData("local_no_attribute_requirements")]
    [InlineData("local_unique_tabula_rasa_no_requirement_or_energy_shield")]
    public void Native_requirement_exemptions_remove_attributes_but_keep_the_level_gate(string stat)
    {
        var catalog = Program.Items;
        var mod = catalog.Mods.All.First(m => m.Stats.Any(s => s.Id == stat));
        var item = ItemCatalog.Create(catalog.Find("Simple Robe")!) with
        {
            Mods = [new(ModSection.Hidden, mod.Hash, mod.Stats.Select(s => s.Id == stat ? 1 : 0).ToArray())],
        };
        var worn = new ItemModStats.Worn(1, item, 200, 200, 200, 5);
        var lowLevel = ItemModStats.ResolveEquipment([worn], 4, _ => (0, 0, 0), mods: catalog.Mods);
        var meetsLevel = ItemModStats.ResolveEquipment([worn], 5, _ => (0, 0, 0), mods: catalog.Mods);

        Assert.Empty(lowLevel.Active);
        Assert.Contains(1u, lowLevel.Inactive);
        Assert.Single(meetsLevel.Active);
        Assert.Empty(meetsLevel.Inactive);
    }

    [Theory]
    [InlineData("local_no_attribute_requirements")]
    [InlineData("local_unique_tabula_rasa_no_requirement_or_energy_shield")]
    public void Current_gem_snapshot_honours_equipment_exemptions_without_publishing_inactive_items(string stat)
    {
        var catalog = Program.Items;
        var conn = new ClientState(new(0, "ImportedDefence", 128, 128, 32, 32, null, []), 0, 1)
            { Character = character, ClassRow = SkillCatalog.WitchRow };
        conn.PlayerStats[Program.Stats.Key("intelligence_is_0")!.Value] = 1;
        var inventory = PlayerInventories.For(character, catalog);
        var robe = inventory.Add(PlayerInventories.BodyArmour, 0, 0,
            ItemCatalog.Create(catalog.Find("Simple Robe")!));
        conn.Pools = Program.PoolsFor(conn, 100);
        var pools = conn.Pools;
        var published = Program.InactiveItems[character];
        Assert.Contains(robe.Id, published);

        var mod = catalog.Mods.All.First(m => m.Stats.Any(s => s.Id == stat));
        Assert.NotNull(inventory.Change(PlayerInventories.BodyArmour, robe.Id, item => item with
        {
            Mods = [new(ModSection.Hidden, mod.Hash, mod.Stats.Select(s => s.Id == stat ? 1 : 0).ToArray())],
        }));
        var snapshot = typeof(Program).GetMethod("CurrentGemRequirements", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [conn, null])!;
        var inactive = (IReadOnlySet<uint>)snapshot.GetType().GetProperty("Inactive")!.GetValue(snapshot)!;

        Assert.DoesNotContain(robe.Id, inactive);
        Assert.Same(pools, conn.Pools);
        Assert.Same(published, Program.InactiveItems[character]);
        Assert.Contains(robe.Id, published);
        conn.Pools = Program.PoolsFor(conn, 100);
        Assert.DoesNotContain(robe.Id, Program.InactiveItems[character]);
    }

    public void Dispose()
    {
        PlayerInventories.Forget(character);
        Program.InactiveItems.TryRemove(character, out _);
    }
}
