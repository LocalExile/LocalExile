using GameServer.Core.World;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// Devour Support: a killing blow recovers its flat life/mana and consumes the corpse, so the kill removes the corpse
// object; a shattered corpse has nothing left to consume.
public sealed class DevourSupportTests
{
    [Fact]
    public void A_devour_kill_consumes_the_corpse_and_removes_its_object()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-devour-corpse-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var player = new Player(new(1, "devour", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "DevourChar" },
            PlayerLife = 500,
            PlayerMana = 100,
            Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        player.Clock.Stop();
        double now = player.Clock.Elapsed.TotalSeconds;
        try
        {
            var skill = new Dictionary<uint, int>();
            foreach (var (name, value) in new[]
                     {
                         ("killing_blow_consumes_corpse_restore_x_life", 20),
                         ("killing_blow_consumes_corpse_restore_x_mana", 10),
                     })
                skill[Program.Stats.Key(name)!.Value] = value;
            var zombie = Game.Monsters.Find("Metadata/Monsters/Zombies/ZombieBite")
                ?? throw new InvalidOperationException("no zombie variety");
            Assert.True(zombie.LifeData.IsUsableAsCorpse(0));

            var corpse = new SpawnedMonster("corpse", new(0, 0, 0), 0, 0, 100)
            {
                Dead = true,
                Life = 0,
                Variety = zombie,
            };
            player.Area.Monsters[1] = corpse;
            Game.DevourOnKill(player, corpse, skill);
            Assert.Equal(520u, player.PlayerLife);
            Assert.Equal(110u, player.PlayerMana);
            Assert.True(corpse.DevourCorpse);
            Game.ChargesOnKill(player, corpse);
            Assert.Equal(1L, player.RecentEvents.Amounts(now)[StatConditions.KilledEnemy]);
            Assert.True(Game.ConsumeDevouredCorpseLocked(player, 1, corpse, now));
            Assert.True(corpse.CorpseConsumed);
            Assert.False(corpse.DevourCorpse);
            Assert.False(player.Area.Monsters.ContainsKey(1));
            Assert.True(player.RecentEvents.Happened(StatConditions.ConsumedCorpse, now));
            Assert.Equal(1L, player.RecentEvents.Amounts(now)[StatConditions.KilledEnemy]);
            Assert.False(Game.ConsumeDevouredCorpseLocked(player, 1, corpse, now));
            Assert.False(player.RecentEvents.Happened(StatConditions.ConsumedCorpse, now + 4));

            // A shattered corpse is already gone: the support's recovery still applies, but there is no object to remove.
            var shattered = new SpawnedMonster("shattered", new(0, 0, 0), 0, 0, 100)
            {
                Dead = true,
                Life = 0,
                Variety = zombie,
            };
            shattered.Ailments.Shatter();
            player.Area.Monsters[2] = shattered;
            Game.DevourOnKill(player, shattered, skill);
            Assert.False(shattered.DevourCorpse);
            Assert.False(Game.ConsumeDevouredCorpseLocked(player, 2, shattered, 0));
            Assert.True(player.Area.Monsters.ContainsKey(2));
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!);
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
