namespace GameServer.Gateway.Tests;

public sealed class MinionPathRecoveryTests
{
    [Fact]
    public async Task A_follower_with_no_path_does_not_send_a_walking_action_and_can_retry_when_a_path_opens()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Raise Zombie", 20));
        var variety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!;
        f.Player.Area.Monsters[901] = new("corpse", new(100, 100, 0), 0, 0, 1000)
            { Dead = true, Life = 0, Hostile = true, Variety = variety, Level = 70 };
        var id = await f.Summon("RaiseZombie", 901);
        var fighter = f.Player.Summons.Fighters[id];
        f.Player.Use(f.Player.Layout with { Walkable = (_, _) => true });
        f.Player.Walk.Teleport(150, 100, 0);
        fighter.Monster.Walk = new(100, 100, 8, (x, y) => x == 100 && y == 100);
        f.Player.Minions.OutputQueue.Clear();
        Game.GolemStepLocked(f.Player, fighter, 10);
        Assert.Equal((100, 100), fighter.Monster.Walk.Destination);
        Assert.DoesNotContain(f.Player.Minions.OutputQueue, o => o.Packet.Length > 1 && o.Packet[0] == 1 && o.Packet[1] == 0xab);
        fighter.Monster.Walk = new(100, 100, 8);
        f.Player.Minions.OutputQueue.Clear();
        Game.GolemStepLocked(f.Player, fighter, 11);
        Assert.NotEqual((100, 100), fighter.Monster.Walk.Destination);
        Assert.Contains(f.Player.Minions.OutputQueue, o => o.Packet.Length > 1 && o.Packet[0] == 1 && o.Packet[1] == 0xab);
    }

}
