using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using Xunit;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public class DormantMonsterTests
{
    [Fact]
    public async Task An_emerge_watch_ends_with_its_rules_and_leaves_the_waiting_monsters_to_the_next()
    {
        // A game module reload: the old rules' watch stops at once, and what still waits is the instance's, unwatched, for
        // the new rules' SyncInstanceMonsters to watch.
        var previous = Game.CombatLifetime;
        var rules = new ModuleLifetime("Game");
        var instance = new InstanceState();
        var player = new Player(new(1, "emerge-reload", 300, 300, 100, 100, null, []), 1, 1)
        {
            Instance = instance,
            Connection = { PlayerSent = true },
            PlayerLife = 100
        };
        try
        {
            Game.CombatLifetime = rules;
            Assert.True(MonsterEmerge.AddLocked(player, 0x3050, new("lurking", new PlayerWalk(60, 60, 8), 60, 2, 23), EmergeKind.Lurking, unrisen: true));
            Assert.True(MonsterEmerge.StartWatch(player));
            rules.Stop(ex => throw ex);
            await Game.RunEmergeWatch(player);
            Assert.Contains(0x3050u, instance.World.Area.EmergeGround.Lurking);
            Assert.True(MonsterEmerge.Waiting(player, 0x3050));
            Assert.True(MonsterEmerge.Unwatched(player));
        }
        finally { Game.CombatLifetime = previous; }
    }

}
