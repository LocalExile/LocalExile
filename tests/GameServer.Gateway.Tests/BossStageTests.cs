using GameServer.Core.World;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using System.Net;
using System.Net.Sockets;

namespace GameServer.Gateway.Tests;

public sealed class BossStageTests
{
    [Fact]
    public async Task Extraction_uses_its_animation_contact_independently_of_recovery()
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
                Stage = BossFights.For(hillock), StageRunning = true, Life = 141, BusyUntil = 13,
                StageEffectAt = 10 + BossFights.For(hillock)!.EffectSeconds(clip),
            };
            conn.Monsters[7] = m;
            Assert.Null(Game.AdvanceMonsterStage(conn, 7, m, 10.74));
            Assert.Equal(141u, m.Life);
            conn.Clock.Stop();
            Assert.Null(Game.AdvanceMonsterStage(conn, 7, m, 10.75));
            Assert.Equal(141u, m.Life);
            conn.Clock.Start();
            conn.LeavingArea = true;
            Assert.Null(Game.AdvanceMonsterStage(conn, 7, m, 10.75));
            conn.LeavingArea = false;
            conn.Monsters.Remove(7);
            Assert.Null(Game.AdvanceMonsterStage(conn, 7, m, 10.75));
            conn.Monsters[7] = m;
            m.Dead = true;
            Assert.Null(Game.AdvanceMonsterStage(conn, 7, m, 10.75));
            m.Dead = false;
            Assert.NotNull(Game.AdvanceMonsterStage(conn, 7, m, 10.75));
            Assert.Equal(311u, m.Life);
            Assert.True(m.StageRunning);
            Assert.True(m.StageEffectApplied);
            m.Life = 200;
            Assert.Null(Game.AdvanceMonsterStage(conn, 7, m, 12.99));
            Assert.NotNull(Game.AdvanceMonsterStage(conn, 7, m, 13));
            Assert.Equal(200u, m.Life); // Recovery ends without a second heal.
            Assert.False(m.StageRunning);
            Assert.Null(Game.AdvanceMonsterStage(conn, 7, m, 14));

            // Ordinary hits and projectiles use the chronological runtime, rather than the async stage worker.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            using var writer = new TcpClient();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var reader = await accepting;
            var session = new Game.ProjectileSession("stage-test", writer.GetStream(), new ConnectionCiphers(new byte[64], false), conn);
            var chronological = new SpawnedMonster("Hillock", new PlayerWalk(10, 10, 1), 60, 2, maxLife: 311)
            {
                Stage = BossFights.For(hillock), StageRunning = true, Life = 141,
                Variety = new MonsterCatalog().Find(hillock),
            };
            conn.Monsters[8] = chronological;
            Game.CommitProjectileBossStage(session, 8, chronological, ProjectileRuntime.Time(10));
            session.AdvanceTo(ProjectileRuntime.Time(10.74));
            Assert.Equal(141u, chronological.Life);
            session.AdvanceTo(ProjectileRuntime.Time(10.75));
            Assert.Equal(311u, chronological.Life);
            Assert.True(chronological.StageRunning);
            chronological.Life = 200;
            session.AdvanceTo(ProjectileRuntime.Time(13));
            Assert.False(chronological.StageRunning);
            Assert.Equal(200u, chronological.Life);
            session.Invalidate();
            await session.OutputCompleted;

            m.StageRunning = true;
            m.StageEffectApplied = false;
            lifetime.Stop(ex => throw ex);
            Assert.Null(Game.AdvanceMonsterStage(conn, 7, m, 14));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            if (previousLogic is not null) Program.SetGameLogic(previousLogic);
        }
    }
}
