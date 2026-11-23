namespace GameServer.Gateway.Tests;

public sealed class ModCompatibilityTests
{
    [Fact]
    public void Game_exports_the_combat_and_passive_signatures_used_by_published_mods()
    {
        // A compiled mod refers to the full parameter list even when all optional arguments were omitted in source.
        var notify = typeof(Game).GetMethod(nameof(Game.NotifyMonsterHit),
        [
            typeof(Player), typeof(uint), typeof(SpawnedMonster), typeof(MonsterHit), typeof(int), typeof(string),
            typeof(bool), typeof(bool), typeof(ushort), typeof(List<(uint, SpawnedMonster)>), typeof(byte),
            typeof(StunHit?), typeof(bool), typeof(string)
        ]);
        Assert.NotNull(notify);
        Assert.True(notify.IsStatic);
        Assert.Equal(typeof(Task), notify.ReturnType);

        var change = typeof(Game).GetMethod(nameof(Game.ChangePassives),
        [
            typeof(Player), typeof(IReadOnlyList<ushort>), typeof(bool), typeof(string), typeof(ushort?),
            typeof(bool), typeof(bool)
        ]);
        Assert.NotNull(change);
        Assert.True(change.IsStatic);
        Assert.Equal(typeof(Task<string>), change.ReturnType);
    }
}
