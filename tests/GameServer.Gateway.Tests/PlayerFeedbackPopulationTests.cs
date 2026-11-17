namespace GameServer.Gateway.Tests;

public sealed class PlayerFeedbackPopulationTests
{
    [Fact]
    public async Task An_entry_without_walkability_does_not_claim_an_empty_population_for_later_entries()
    {
        var conn = SpellTotemExecutionTests.Connection("PopulationNotReady");
        var previous = Environment.GetEnvironmentVariable("Game_MONSTERS");
        try
        {
            Environment.SetEnvironmentVariable("Game_MONSTERS", null);
            conn.Use(conn.Layout with { Walkable = null });
            await Game.PopulateArea("no-walkability", null!, null!, conn);
            Assert.False(conn.Instance.World.Populated);
            Assert.False(conn.MonstersPlaced);
            // A later entry can still claim the population; an established empty save, however, is final.
            conn.Instance.Monsters = [];
            await Game.PopulateArea("restore-empty", null!, null!, conn);
            Assert.True(conn.Instance.World.Populated);
            Assert.True(conn.MonstersPlaced);
            Assert.False(Game.ClaimMonsterHost("duplicate", conn));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Game_MONSTERS", previous);
            PlayerInventories.Forget(conn.Character!);
        }
    }
}
