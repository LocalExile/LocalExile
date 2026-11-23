namespace GameServer.Gateway.Tests;

public sealed class PlayerFeedbackPopulationTests
{
    [Fact]
    public async Task An_entry_without_walkability_does_not_claim_an_empty_population_for_later_entries()
    {
        var player = SpellTotemExecutionTests.PlayerOf("PopulationNotReady");
        var previous = Environment.GetEnvironmentVariable("Game_MONSTERS");
        try
        {
            Environment.SetEnvironmentVariable("Game_MONSTERS", null);
            player.Use(player.Layout with { Walkable = null });
            await Game.PopulateArea(player);
            Assert.False(player.Instance.World.Populated);
            Assert.False(player.MonstersPlaced);
            // A later entry can still claim the population; an established empty save, however, is final.
            player.Instance.Monsters = [];
            await Game.PopulateArea(player);
            Assert.True(player.Instance.World.Populated);
            Assert.True(player.MonstersPlaced);
            Assert.False(Game.ClaimMonsterHost(player));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Game_MONSTERS", previous);
            PlayerInventories.Forget(player.Character.Name!);
        }
    }
}
