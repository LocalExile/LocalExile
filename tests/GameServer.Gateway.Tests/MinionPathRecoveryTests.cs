namespace GameServer.Gateway.Tests;

public sealed class MinionPathRecoveryTests
{
    [Fact]
    public async Task A_follower_with_no_path_does_not_send_a_walking_action_and_can_retry_when_a_path_opens()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Raise Zombie", 20));
        var variety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!;
        f.Conn.Monsters[901] = new("corpse", new(100, 100, 0), 0, 0, 1000)
            { Dead = true, Life = 0, Hostile = true, Variety = variety, Level = 70 };
        var id = await f.Summon("RaiseZombie", 901);
        var fighter = f.Conn.Summons.Fighters[id];
        f.Conn.Use(f.Conn.Layout with { Walkable = (_, _) => true });
        f.Conn.Walk.Teleport(150, 100, 0);
        fighter.Monster.Walk = new(100, 100, 8, (x, y) => x == 100 && y == 100);
        f.Conn.Minions.OutputQueue.Clear();
        Game.GolemStepLocked("no-path", f.Stream, f.Ciphers, f.Conn, fighter, 10);
        Assert.Equal((100, 100), fighter.Monster.Walk.Destination);
        Assert.DoesNotContain(f.Conn.Minions.OutputQueue, o => o.Packet.Length > 1 && o.Packet[0] == 1 && o.Packet[1] == 0xab);
        fighter.Monster.Walk = new(100, 100, 8);
        f.Conn.Minions.OutputQueue.Clear();
        Game.GolemStepLocked("path-ready", f.Stream, f.Ciphers, f.Conn, fighter, 11);
        Assert.NotEqual((100, 100), fighter.Monster.Walk.Destination);
        Assert.Contains(f.Conn.Minions.OutputQueue, o => o.Packet.Length > 1 && o.Packet[0] == 1 && o.Packet[1] == 0xab);
    }

}
