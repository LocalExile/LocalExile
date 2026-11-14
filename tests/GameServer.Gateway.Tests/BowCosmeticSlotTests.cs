namespace GameServer.Gateway.Tests;

public class BowCosmeticSlotTests
{
    [Fact]
    public void Equipped_seraph_bow_skin_dresses_the_bow_and_leaves_the_arrow_and_quiver_intact()
    {
        const uint seraphBow = 1972361898;
        var previous = Program.GameLogicOrNull;
        var character = "BowSkin" + Guid.NewGuid().ToString("N");
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            var conn = new ClientState(new(1, "1_1_1", 100, 100, 20, 20, null, []), 1, 1) { Character = character };
            var items = Game.ItemsOf(conn);
            items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Crude Bow")!));
            CharacterSaves.SetCosmetic(character, 1, new(340, Hash: seraphBow));
            var looks = Game.Looks(conn, items);
            Assert.Equal(seraphBow, looks.Single(i => i.Slot == 1).SkinHash);
            Assert.NotEqual(0, looks.Single(i => i.Slot == 1).SkinVisual);
            Assert.Equal(0u, looks.Single(i => i.Slot == 0).SkinHash);
            Assert.Equal(0u, looks.Single(i => i.Slot == 7).SkinHash);
        }
        finally
        {
            PlayerInventories.Forget(character);
            Program.SetGameLogic(previous!);
        }
    }

    // A quiver skin is worn in the off hand's Weapon Skin slot (4) and drawn on the quiver, which a bow character carries
    // on the back (model slot 7). A skin of another item class is not drawn: a quiver skin in the main hand's slot (1)
    // leaves the bow as it is.
    [Fact]
    public void Quiver_skin_dresses_the_quiver_on_the_back_and_only_items_of_its_class()
    {
        const uint dragonHunterQuiver = 756778323;
        var previous = Program.GameLogicOrNull;
        var character = "QuiverSkin" + Guid.NewGuid().ToString("N");
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            var conn = new ClientState(new(1, "1_1_1", 100, 100, 20, 20, null, []), 1, 1)
                { Character = character, Experience = CharacterStats.ExperienceFor(100) };
            var items = Game.ItemsOf(conn);
            items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Crude Bow")!));
            items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Serrated Arrow Quiver")!));
            CharacterSaves.SetCosmetic(character, 4, new(1555, Hash: dragonHunterQuiver));
            CharacterSaves.SetCosmetic(character, 1, new(1555, Hash: dragonHunterQuiver));
            var looks = Game.Looks(conn, items);
            var quiver = looks.Single(i => i.Slot == 7);
            Assert.Equal(dragonHunterQuiver, quiver.SkinHash);
            Assert.NotEqual(0, quiver.SkinVisual);
            Assert.Equal(Program.Items.Find("Serrated Arrow Quiver")!.VisualIdentity, quiver.VisualIdentity);
            Assert.Equal(0u, looks.Single(i => i.Slot == 1).SkinHash);
            Assert.Equal(0u, looks.Single(i => i.Slot == 0).SkinHash);
        }
        finally
        {
            PlayerInventories.Forget(character);
            Program.SetGameLogic(previous!);
        }
    }

    // The Goblin Shaman Skin ("Transforms you into a Goblin Shaman when in a town or hideout") gives mtx_goblin_morph,
    // which the client never reads: in a town the server sends the morph number the client's own calculator would give
    // (mtx_virtual_morph_art_variation_index 28) with the goblin's model and animation set, and nothing elsewhere.
    [Fact]
    public void Goblin_skin_morphs_the_character_in_a_town_and_nowhere_else()
    {
        const uint goblinShamanSkin = 3385546574;
        var previous = Program.GameLogicOrNull;
        var character = "GoblinSkin" + Guid.NewGuid().ToString("N");
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            CharacterSaves.SetCosmetic(character, 15, new(4935, Hash: goblinShamanSkin));
            uint morphStat = Program.Stats.Key("mtx_virtual_morph_art_variation_index")!.Value;
            var town = new ClientState(new(9052, "1_1_town", 100, 100, 20, 20, null, []), 1, 1) { Character = character };
            var morph = Game.WornMorph(town);
            Assert.Equal(28, morph!.Index);
            Assert.EndsWith("GoblinShamanPet_01.ao", morph.Model);
            Assert.EndsWith("GoblinShamanTransform1.act", morph.Actor);
            Assert.Contains((morphStat, 28), Game.CosmeticStats(town));
            var strand = new ClientState(new(61450, "1_1_1", 100, 100, 20, 20, null, []), 1, 1) { Character = character };
            Assert.Null(Game.WornMorph(strand));
            Assert.DoesNotContain(Game.CosmeticStats(strand), s => s.Stat == morphStat);
        }
        finally
        {
            PlayerInventories.Forget(character);
            Program.SetGameLogic(previous!);
        }
    }
}
