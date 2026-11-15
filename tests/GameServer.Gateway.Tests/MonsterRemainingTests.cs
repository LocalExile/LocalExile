using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using Xunit;

namespace GameServer.Gateway.Tests;

// The map overlay's "X monsters remain" line: S→C 0x1a0 is its only input (InstanceProtocol.MonstersRemaining has the
// client's side), and the count is the area's living monsters. Live /raw checks pinned the byte's meaning; these are the
// server's half.
public class MonsterRemainingTests
{
    [Fact]
    public void The_monsters_remaining_packet_is_the_overlays_byte_and_clamps_at_255()
    {
        Assert.Equal("01A000", Convert.ToHexString(InstanceProtocol.MonstersRemaining(0)));
        Assert.Equal("01A001", Convert.ToHexString(InstanceProtocol.MonstersRemaining(1)));
        Assert.Equal("01A005", Convert.ToHexString(InstanceProtocol.MonstersRemaining(5)));
        // 0..50 read "{0} monsters remain", 51 and up "More than 50 monsters remain": an area of 349 is sent as 255.
        Assert.Equal("01A032", Convert.ToHexString(InstanceProtocol.MonstersRemaining(50)));
        Assert.Equal("01A033", Convert.ToHexString(InstanceProtocol.MonstersRemaining(51)));
        Assert.Equal("01A0FF", Convert.ToHexString(InstanceProtocol.MonstersRemaining(255)));
        Assert.Equal("01A0FF", Convert.ToHexString(InstanceProtocol.MonstersRemaining(349)));
        Assert.Equal("01A000", Convert.ToHexString(InstanceProtocol.MonstersRemaining(-1)));
    }

    [Fact]
    public async Task The_overlay_counts_the_living_monsters_and_is_resent_only_when_it_changes()
    {
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        var instance = new InstanceState();
        var conn = runtime.Connection(instance, wire);
        static SpawnedMonster Monster(int x) => new("area monster", new PlayerWalk(x, 100, 30), 60, 8, 100);
        // The roster keeps the corpses (a dead monster stays in it until it is consumed), so only the living count.
        instance.World.Monsters[0x08000000] = Monster(110);
        instance.World.Monsters[0x08000001] = Monster(120);
        instance.World.Monsters[0x08000002] = new SpawnedMonster("area corpse", new PlayerWalk(130, 100, 30), 60, 8, 100) { Dead = true, Life = 0 };
        // The packet history keeps the messages, not the log text: "12:34:56.789 => 0x01a0     3 B 01A002".
        List<string> SentSince(int index) => [.. Program.HistoryFor(wire.Ciphers).Lines()
            .Where(line => line.Contains("=> 0x01a0 ", StringComparison.Ordinal))
            .Select(line => line[(line.LastIndexOf(' ') + 1)..]).Skip(index)];
        Task Send() => Game.SendRemainingMonsters("remaining", wire.Stream, wire.Ciphers, conn);

        await Send();
        Assert.Equal(["01A002"], SentSince(0));
        // Nothing changed: the client is not sent the same byte again.
        await Send();
        Assert.Equal(["01A002"], SentSince(0));

        // A kill: the corpse stays in the roster and the overlay counts one fewer.
        var victim = instance.World.Monsters[0x08000001];
        (victim.Dead, victim.Life) = (true, 0);
        await Game.MonsterKilled("remaining", wire.Stream, wire.Ciphers, conn, 0x08000001, victim);
        Assert.Equal(["01A002", "01A001"], SentSince(0));

        // The area's last one, then a monster that joins it (a summon): the count follows both ways.
        var last = instance.World.Monsters[0x08000000];
        (last.Dead, last.Life) = (true, 0);
        await Send();
        instance.World.Monsters[0x08000003] = Monster(140);
        await Send();
        Assert.Equal(["01A002", "01A001", "01A000", "01A001"], SentSince(0));
    }
}
