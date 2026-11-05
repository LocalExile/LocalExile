using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class BossStageTests
{
    [Fact]
    public void Extraction_uses_its_animation_contact_independently_of_recovery()
    {
        const string hillock = "Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal";
        var clip = new SkillCatalog().MonsterContact(hillock, "Throw", 0);
        Assert.NotNull(clip);
        Assert.Equal("extract", clip.Animation);
        Assert.Equal(1833, clip.LengthMs);
        Assert.Equal(458, clip.ContactMs);
        Assert.InRange(3 * clip.ContactFraction!.Value, .74, .76);

        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            var conn = new ClientState(new(1, "stage", 100, 100, 10, 10, null, []), 1, 1);
            var m = new SpawnedMonster("Hillock", new PlayerWalk(10, 10, 1), 60, 2, maxLife: 311)
            {
                Stage = BossFights.For(hillock), StageRunning = true, Life = 141,
            };
            conn.Monsters[7] = m;
            conn.Clock.Stop();
            Assert.Null(Game.AdvanceMonsterStage(conn, 7, m));
            Assert.Equal(141u, m.Life);
            conn.Clock.Start();
            conn.LeavingArea = true;
            Assert.Null(Game.AdvanceMonsterStage(conn, 7, m));
            conn.LeavingArea = false;
            conn.Monsters.Remove(7);
            Assert.Null(Game.AdvanceMonsterStage(conn, 7, m));
            conn.Monsters[7] = m;
            m.Dead = true;
            Assert.Null(Game.AdvanceMonsterStage(conn, 7, m));
            m.Dead = false;
            Assert.NotNull(Game.AdvanceMonsterStage(conn, 7, m));
            Assert.Equal(311u, m.Life);
            Assert.False(m.StageRunning);
            Assert.Null(Game.AdvanceMonsterStage(conn, 7, m));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            if (previousLogic is not null) Program.SetGameLogic(previousLogic);
        }
    }
}
