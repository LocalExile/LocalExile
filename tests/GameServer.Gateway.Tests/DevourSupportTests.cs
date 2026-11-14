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
        var conn = new ClientState(new(1, "devour", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "DevourChar", PlayerLife = 500, PlayerMana = 100, Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
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
            conn.Monsters[1] = corpse;
            Game.DevourOnKill(conn, corpse, skill);
            Assert.Equal(520u, conn.PlayerLife);
            Assert.Equal(110u, conn.PlayerMana);
            Assert.True(corpse.DevourCorpse);
            Assert.True(Game.ConsumeDevouredCorpseLocked(conn, 1, corpse, 0));
            Assert.True(corpse.CorpseConsumed);
            Assert.False(corpse.DevourCorpse);
            Assert.False(conn.Monsters.ContainsKey(1));
            Assert.False(Game.ConsumeDevouredCorpseLocked(conn, 1, corpse, 0));

            // A shattered corpse is already gone: the support's recovery still applies, but there is no object to remove.
            var shattered = new SpawnedMonster("shattered", new(0, 0, 0), 0, 0, 100)
            {
                Dead = true,
                Life = 0,
                Variety = zombie,
            };
            shattered.Ailments.Shatter();
            conn.Monsters[2] = shattered;
            Game.DevourOnKill(conn, shattered, skill);
            Assert.False(shattered.DevourCorpse);
            Assert.False(Game.ConsumeDevouredCorpseLocked(conn, 2, shattered, 0));
            Assert.True(conn.Monsters.ContainsKey(2));
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
