using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class TwilightStrandTutorialTests
{
    [Fact]
    public void Campaign_drops_and_strand_packs_respect_level_and_rarity_gates()
    {
        var items = new ItemCatalog();
        var atlas = MapDevice.Load("data", row => items.Row(row)?.Hash, items).Atlas;
        var uniques = new UniqueCatalog(items);
        var drops = new ItemDrops(items, new ItemRolls(items), maps: atlas, uniques: uniques);
        var rng = new Random(17);

        for (int i = 0; i < 200; i++)
            Assert.Null(drops.Map(1, MonsterRarity.Unique, mapTier: 0, rng));
        Assert.Contains(Enumerable.Range(0, 200), _ => drops.Map(58, MonsterRarity.Unique, mapTier: 0, rng) is not null);

        var level1Drops = Enumerable.Range(0, 400)
            .SelectMany(_ => drops.Roll(1, MonsterRarity.Unique, rng, increasedRarity: 500, mapTier: 0))
            .ToList();
        Assert.DoesNotContain(level1Drops, d => d.Type.Tags.Contains("atlas_base_type"));
        Assert.DoesNotContain(level1Drops, d => d.Item.Rarity == ItemRarity.Unique
            && ItemModStats.LevelRequirement(d.Item, d.Type.DropLevel, items.Mods) > 1);

        var pack = new PackCatalog.Pack(0, "Act1-ZombieDrownedStrand", 2, 3, 0, 0, [], [(5, 1)], new Dictionary<int, int>());
        var strand = new PackCatalog.AreaPacks(TutorialObjects.Strand, 1, [(pack, 100)], new Dictionary<string, PackCatalog.Pack>(), []);
        bool Field(int x, int y) => x is > 5 and < 595 && y is > 5 and < 595;
        for (int seed = 1; seed <= 5; seed++)
        {
            var spawns = AreaPopulation.Plan(strand, 600, 600, Field, (50, 50), [], [], seed, _ => true, []);
            Assert.NotEmpty(spawns);
            Assert.All(spawns, s => Assert.Equal(MonsterRarity.Normal, s.Rarity));
        }
    }

    [Fact]
    public void Tutorial_catalog_and_auto_equip_slot_rules_match_client_protocol()
    {
        Assert.DoesNotContain(Quests.CompletedAllTutorials, Quests.EveryCharacter);
        Assert.Equal(TutorialObjects.SkillGemTutorialFlag, Quests.TutorialFlag(0));
        Assert.Equal(TutorialObjects.SupportGemTutorialFlag, Quests.TutorialFlag(3));
        Assert.Equal(Quests.CompletedAllTutorials, Quests.TutorialFlag(17));
        Assert.Equal(Quests.TutorialPanelHideArticlePopups, Quests.TutorialFlag(77));
        Assert.Null(Quests.TutorialFlag(78));
        Assert.Contains(TutorialObjects.SkillGemTutorialFlag, Quests.AllTutorialFlags);
        Assert.Contains(TutorialObjects.SupportGemTutorialFlag, Quests.AllTutorialFlags);
        Assert.Contains(Quests.CompletedAllTutorials, Quests.AllTutorialFlags);
        Assert.Contains(Quests.TutorialPanelHideArticlePopups, Quests.AllTutorialFlags);

        var items = new ItemCatalog();
        var inv = new PlayerInventories();
        PlayerInventories.Shape? ShapeOf(Item item) => items.ByHash(item.TypeHash) is { } t
            ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;

        var sword = ItemCatalog.Create(items.Find("Rusted Sword")!);
        var wand = ItemCatalog.Create(items.Find("Driftwood Wand")!);
        var first = inv.FindEmptySlot([PlayerInventories.Weapon1, PlayerInventories.Offhand1], ShapeOf(sword)!.Value, ShapeOf);
        Assert.Equal((PlayerInventories.Weapon1, ((byte)0, (byte)0)), first);
        inv.Put(PlayerInventories.Weapon1, new InventoryItem(inv.NewItemId(), 0, 0, sword));
        // A wand cannot auto-equip into Offhand1 alongside a melee sword; a second sword can.
        Assert.Null(inv.FindEmptySlot([PlayerInventories.Weapon1, PlayerInventories.Offhand1], ShapeOf(wand)!.Value, ShapeOf));
        var offhand = inv.FindEmptySlot([PlayerInventories.Weapon1, PlayerInventories.Offhand1], ShapeOf(sword)!.Value, ShapeOf);
        Assert.Equal((PlayerInventories.Offhand1, ((byte)0, (byte)0)), offhand);
    }
}
