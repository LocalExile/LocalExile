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
    [InlineData(0, 34)]
    [InlineData(16, 41)]
    [InlineData(31, 49)]
    [InlineData(32, 34)]
    public void Explicit_saved_roll_reaches_the_equipped_local_defence_calculation(byte roll, int expectedArmour)
    {
        var catalog = Program.Items;
        var mod = catalog.Mods.All.First(m => m.Stats.Any(s => s.Id == "local_physical_damage_reduction_rating_+%"));
        var item = ItemCatalog.Create(catalog.Find("Plate Vest")!, defenceBaseRoll: roll) with
        {
            Quality = 20,
            Mods = [new(ModSection.Explicit, mod.Hash,
                mod.Stats.Select(s => s.Id == "local_physical_damage_reduction_rating_+%" ? 50 : 0).ToArray())],
        };
        var restored = CharacterSaves.FromSaved(CharacterSaves.ToSaved(item, catalog.Mods), catalog);
        var conn = new ClientState(new(0, "ImportedDefence", 128, 128, 32, 32, null, []), 0, 1)
            { Character = character, ClassRow = 4 };
        PlayerInventories.For(character, catalog).Add(PlayerInventories.BodyArmour, 0, 0, restored);

        var equipped = ((int Armour, int Evasion, int EnergyShield))typeof(Program)
            .GetMethod("EquippedBaseDefences", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [conn])!;

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

    public void Dispose() => PlayerInventories.Forget(character);
}
