using GameServer.Core.Protocol.Instance;
using Progression = GameServer.Core.World.Progression;

namespace GameServer.Gateway.Tests;

static class PartyExperienceChecks
{
    internal static async Task Verify()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-party-xp-{Guid.NewGuid():N}.db");
        string? drops = Environment.GetEnvironmentVariable("Game_DROPS");
        Saves.Open(path);
        Environment.SetEnvironmentVariable("Game_DROPS", "0");
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        var instance = new InstanceState();
        var players = Enumerable.Range(0, 5).Select(i =>
        {
            string name = "PartyXP" + Guid.NewGuid().ToString("N");
            var conn = runtime.Connection(instance, wire, name);
            conn.Experience = CharacterStats.ExperienceFor(2);
            conn.Pools = new(2, 1000, 1000, 100, conn.Experience);
            conn.PlayerMana = 1000;
            conn.PartySize = 3;
            conn.Clock.Stop();
            conn.Walk.Teleport(i == 4 ? 1000 : 100, 100, 0);
            if (i == 3) conn.PlayerLife = 0;
            CharacterSaves.Write(name, new([]) { Experience = conn.Experience });
            var template = new PlayerObject(name, 0, i == 4 ? 1000 : 100, 100, conn.PlayerLife, 1000,
                Experience: conn.Experience);
            conn.PeerTemplate = template;
            instance.PublishPlayer(new(conn.PeerId, 1, template, template.GridX, 100, conn.PlayerLife, conn));
            return conn;
        }).ToArray();
        var host = players[0];
        var variety = Game.Monsters.Find("Cannibal")!;
        uint expected = Progression.MonsterExperience(Game.Monsters.BaseExperience(variety, 2)!.Value,
            variety.ExperienceMultiplier, MonsterRarity.Normal, 2, 2);
        Assert.True(expected > 0);
        uint next = 0x08000000;
        (uint Id, SpawnedMonster Monster) Victim(bool noRewards)
        {
            uint id = next++;
            var monster = new SpawnedMonster("party victim", new(100, 100, 30), 60, 8, 1000)
                { Life = 0, Dead = true, Hostile = true, Variety = variety, Level = 2, NoRewards = noRewards };
            instance.World.Monsters[id] = monster;
            instance.World.MonsterOrigins[id] = new(InstanceEntities.ObjectTypeHash(variety.Id),
                new MonsterObject(100, 100, 1000, variety.ComponentsOrBase, variety.AnimatedObject), (ushort)variety.Row, 2, MonsterRarity.Normal);
            host.Monsters[id] = monster;
            return (id, monster);
        }
        try
        {
            foreach (bool noRewards in new[] { false, true })
            foreach (string route in new[] { "melee", "projectile", "minion" })
            {
                uint[] before = players.Select(p => p.Experience).ToArray();
                var victim = Victim(noRewards);
                if (route == "melee")
                    await Game.MonsterKilled("party-xp", wire.Stream, wire.Ciphers, host, victim.Id, victim.Monster);
                else if (route == "projectile")
                {
                    var session = new Game.ProjectileSession("party-xp", wire.Stream, wire.Ciphers, host);
                    try { Game.CommitProjectileKill(session, victim.Id, victim.Monster, 0, new Random(4)); }
                    finally { session.Invalidate(); await session.OutputCompleted; }
                }
                else
                {
                    lock (host.Monsters)
                    {
                        Game.CommitAcceptedKillLocked(host, victim.Id, victim.Monster, 0, playerKill: false);
                        Game.CommitAcceptedKillLocked(host, victim.Id, victim.Monster, 0, playerKill: false);
                    }
                }
                foreach (var member in players.Skip(1))
                    await Game.ApplyKillShares("party-xp", wire.Stream, wire.Ciphers, member);
                for (int i = 0; i < players.Length; i++)
                    Assert.Equal(before[i] + (!noRewards && i < 3 ? expected : 0), players[i].Experience);
                foreach (var member in players.Skip(1))
                    await Game.ApplyKillShares("party-xp", wire.Stream, wire.Ciphers, member);
                for (int i = 0; i < players.Length; i++)
                    Assert.Equal(before[i] + (!noRewards && i < 3 ? expected : 0), players[i].Experience);
            }
        }
        finally
        {
            foreach (var player in players)
            {
                PlayerInventories.Forget(player.Character!);
                Game.LevelUpdates.TryRemove(player.Character!, out _);
            }
            Environment.SetEnvironmentVariable("Game_DROPS", drops);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
