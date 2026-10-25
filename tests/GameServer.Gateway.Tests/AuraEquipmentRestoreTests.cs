using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class AuraEquipmentRestoreTests
{
    [Fact]
    public void Fresh_connection_does_not_disable_aura_equipment_using_uninitialized_gem_attributes()
    {
        TestSaves.Ensure();
        var catalog = BenchCraftingTests.Setup.Value.Catalog;
        var types = BenchCraftingTests.Setup.Value.Types;
        var bodyType = types.Single(t => t.Class == "Body Armour");
        var amuletType = types.Single(t => t.Class == "Amulet");
        var precision = ItemCatalog.Create(catalog.Find("Metadata/Items/Gems/SkillGemPrecision")!);
        var clarity = ItemCatalog.Create(catalog.Find("Metadata/Items/Gems/SkillGemClarity")!);
        var attributes = catalog.Mods.All.First(m => m.GenerationType is 1 or 2 && m.Stats.Count == 1 && m.Stats[0].Id == "additional_all_attributes");
        var amulet = ItemCatalog.Create(amuletType) with { Rarity = ItemRarity.Rare,
            Mods = [new(ModSection.Explicit, attributes.Hash, [1000])] };
        var body = ItemCatalog.Create(bodyType) with { Sockets = [new(SocketColour.Green, precision), new(SocketColour.Blue, clarity)] };
        string character = "AuraBootstrap" + Guid.NewGuid().ToString("N");
        CharacterSaves.SetInventories(character, new()
        {
            [PlayerInventories.BodyArmour] = [new(1, 0, 0, CharacterSaves.ToSaved(body, catalog.Mods))],
            [5] = [new(2, 0, 0, CharacterSaves.ToSaved(amulet, catalog.Mods))],
        });
        CharacterSaves.SetAuraActive(character, "Precision", true);
        CharacterSaves.SetAuraActive(character, "Clarity", true);
        // The old pool-building path saw initial 14 dexterity and disabled this entire host because
        // Precision needs 22. Equipment eligibility must not depend on those unfinished gem pools.
        var resolved = ItemModStats.ResolveEquipment(
            [new(1, body, 0, 0, 0, 1), new(2, amulet, 0, 0, 0, 1)], (14, 14, 32), 100, catalog.Mods);
        Assert.Empty(resolved.Inactive);
        Assert.Equal(2, resolved.Active.Count);
        var stats = new StatCatalog(BenchCraftingTests.Data());
        var result = ItemModStats.Global(resolved.Active, catalog.Mods, stats);
        Assert.True(CharacterStats.Attributes(name => stats.Key(name) is uint key ? result.GetValueOrDefault(key) : 0).Dexterity >= 1000);
        Assert.Equal(new[] { "Precision", "Clarity" }, CharacterSaves.Load(character)!.ActiveAuras);
    }

    [Fact]
    public void Every_supported_aura_returns_after_town_with_no_count_limit()
    {
        string[] ids = ["Vitality", "Precision", "Clarity", "Grace", "Determination", "Anger", "Hatred", "Wrath",
            "Haste", "Discipline", "PurityOfElements", "PurityOfFire", "PurityOfIce", "PurityOfLightning", "Zealotry", "Malevolence", "Envy"];
        var available = ids.Select((id, i) => new AuraCatalog.Aura((ushort)(0x200 + i), 0, id, id,
            (ushort)(i * 2 + 1), AuraCatalog.BuffRow(id), 100, 5000, SourceBuff: (ushort)(i * 2 + 2))).ToArray();
        var pools = new CharacterStats.Pools(1, 50, 100, 2, 0);
        Assert.Empty(AuraCatalog.Restore(ids, available, pools, inTown: true));
        var restored = AuraCatalog.Restore(ids, available, pools);
        Assert.Equal(ids, restored.Select(a => a.EffectId));
        Assert.Equal(34, restored.Sum(a => AuraCatalog.StartPackets(a, 0x1000, 1).Count()));
    }
}
