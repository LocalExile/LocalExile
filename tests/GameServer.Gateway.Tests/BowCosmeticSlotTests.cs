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
}
