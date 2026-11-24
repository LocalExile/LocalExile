using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ModCompatibilityTests
{
    [Fact]
    public void Game_exports_the_monster_spawn_and_placement_signatures_used_by_published_mods()
    {
        var spawn = typeof(Game).GetMethod(nameof(Game.SpawnMonster),
        [
            typeof(Player), typeof(MonsterCatalog.Variety), typeof(byte), typeof(MonsterRarity), typeof(int), typeof(int),
            typeof(string), typeof(bool), typeof(bool), typeof(uint?), typeof(MonsterModSet), typeof(bool), typeof(bool?),
            typeof(float), typeof(IReadOnlyList<(uint, int)>), typeof(Action<uint, SpawnedMonster>), typeof(Func<bool>),
            typeof(IReadOnlyList<GrantedSkill>), typeof(uint?), typeof(uint), typeof(uint), typeof(MonsterModSet), typeof(bool),
            typeof(IReadOnlyList<VisibleItem>), typeof(Func<bool>), typeof(bool), typeof(Game.MonsterPlacement), typeof(int?),
            typeof(MonsterMovement), typeof((uint, string, byte, byte)?), typeof(bool), typeof(bool), typeof(bool)
        ]);
        Assert.NotNull(spawn);
        Assert.True(spawn.IsStatic);
        Assert.Equal(typeof(Task<uint>), spawn.ReturnType);

        var next = typeof(Game).GetMethod(nameof(Game.NextToPlayer), [typeof(Player)]);
        Assert.NotNull(next);
        Assert.True(next.IsStatic);
        Assert.Equal(typeof((int, int)), next.ReturnType);
    }

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
