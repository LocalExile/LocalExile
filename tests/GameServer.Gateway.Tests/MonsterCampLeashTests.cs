namespace GameServer.Gateway.Tests;

public sealed class MonsterCampLeashTests
{
    [Fact]
    public void MonsterInitializesSpawnPointAndRecordsReturningState()
    {
        var walk = new GameServer.Core.World.PlayerWalk(100, 100, 30);
        var monster = new SpawnedMonster("test_crab@2", walk, 105, 5, 100)
        {
            SpawnPoint = (100, 100)
        };

        Assert.Equal((100, 100), monster.SpawnPoint);
        Assert.False(monster.Returning);

        monster.Returning = true;
        Assert.True(monster.Returning);
    }
}
