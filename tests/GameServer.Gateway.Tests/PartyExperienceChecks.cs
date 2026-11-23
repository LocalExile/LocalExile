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
            var player = runtime.PlayerOf(instance, wire, name);
            player.Character.Experience = CharacterStats.ExperienceFor(2);
            player.Pools = new(2, 1000, 1000, 100, player.Character.Experience);
            player.PlayerMana = 1000;
            player.PartySize = 3;
            player.Clock.Stop();
            player.Walk.Teleport(i == 4 ? 1000 : 100, 100, 0);
            if (i == 3) player.PlayerLife = 0;
            CharacterSaves.Write(name, new([]) { Experience = player.Character.Experience });
            var template = new PlayerObject(name, 0, i == 4 ? 1000 : 100, 100, player.PlayerLife, 1000,
                Experience: player.Character.Experience);
            player.PeerTemplate = template;
            instance.AddPlayer(player);
            return player;
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
            instance.World.Area.Monsters[id] = monster;
            instance.World.MonsterOrigins[id] = new(InstanceEntities.ObjectTypeHash(variety.Id),
                new MonsterObject(100, 100, 1000, variety.ComponentsOrBase, variety.AnimatedObject), (ushort)variety.Row, 2, MonsterRarity.Normal);
            host.Area.Monsters[id] = monster;
            return (id, monster);
        }
        try
        {
            foreach (bool noRewards in new[] { false, true })
            foreach (string route in new[] { "melee", "projectile", "minion" })
            {
                uint[] before = players.Select(p => p.Character.Experience).ToArray();
                var victim = Victim(noRewards);
                if (route == "melee")
                    await Game.MonsterKilled(host, victim.Id, victim.Monster);
                else if (route == "projectile")
                {
                    var session = new Game.ProjectileSession(host);
                    try
                    {
                        using (Game.ProjectileMutation(host, advance: false))
                            Game.CommitProjectileKill(session, victim.Id, victim.Monster, 0, new Random(4));
                    }
                    finally { session.Invalidate(); await session.OutputCompleted; }
                }
                else
                {
                    using (Game.ProjectileMutation(host))
                    {
                        Game.CommitAcceptedKillLocked(host, victim.Id, victim.Monster, 0, playerKill: false);
                        Game.CommitAcceptedKillLocked(host, victim.Id, victim.Monster, 0, playerKill: false);
                    }
                }
                foreach (var member in players.Skip(1))
                    await Game.ApplyKillShares(member);
                for (int i = 0; i < players.Length; i++)
                    Assert.Equal(before[i] + (!noRewards && i < 3 ? expected : 0), players[i].Character.Experience);
                foreach (var member in players.Skip(1))
                    await Game.ApplyKillShares(member);
                for (int i = 0; i < players.Length; i++)
                    Assert.Equal(before[i] + (!noRewards && i < 3 ? expected : 0), players[i].Character.Experience);
            }
        }
        finally
        {
            foreach (var player in players)
            {
                PlayerInventories.Forget(player.Character.Name!);
                Game.LevelUpdates.TryRemove(player.Character.Name!, out _);
            }
            Environment.SetEnvironmentVariable("Game_DROPS", drops);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
