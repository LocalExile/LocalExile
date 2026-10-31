namespace GameServer.Gateway.Tests;

public class DelveEncounterTests
{
    [Fact]
    public void First_mine_has_supported_native_quest_monsters()
    {
        foreach (string name in new[] { "delve-azurite-weta-pack", "delve-azurite-spider-pack" })
        {
            var pack = Program.Packs.Find(name);
            Assert.NotNull(pack);
            Assert.Contains(pack.Entries, e => Program.Monsters.Find(e.Variety.ToString()) is { Supported: true });
        }
    }
}
