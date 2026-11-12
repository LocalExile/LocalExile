using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ArrowRainAndMirageTests
{
    sealed class Battle : IDisposable
    {
        readonly IGameLogic? previous = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly SpellTotemExecutionTests.Wire wire = new();
        public readonly ClientState Conn;
        public readonly Game.ProjectileSession Session;
        public readonly SpawnedMonster Enemy = new("target", new(120, 100, 0), 0, 0, 100000) { Hostile = true };
        public readonly SpawnedMonster Ally = new("friendly", new(120, 100, 0), 0, 0, 100000) { Hostile = false };

        public Battle(params string[] gems)
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            Conn = SpellTotemExecutionTests.Connection("Rain" + Guid.NewGuid().ToString("N"));
            Conn.Clock.Stop();
            Conn.Walk.Teleport(100, 100, 0);
            Conn.PlayerStats[Program.Stats.Key("global_always_hit")!.Value] = 1;
            var items = Game.ItemsOf(Conn);
            var sockets = gems.Select(name => new ItemSocket(SocketColour.White, ItemCatalog.Create(Program.Items.FindGem(name)!.Value.Type))).ToArray();
            items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Crude Bow")!) with
            { Sockets = sockets, Links = [(byte)sockets.Length] });
            Conn.Monsters[200] = Enemy;
            Conn.Monsters[201] = Ally;
            Session = new Game.ProjectileSession("rain", wire.Stream, wire.Ciphers, Conn);
            Conn.ProjectileRuntimeState = Session;
        }

        public void Dispose()
        {
            Session.Invalidate();
            Conn.Closed = true;
            Session.OutputCompleted.GetAwaiter().GetResult();
            lifetime.Stop(_ => { });
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previous!);
            PlayerInventories.Forget(Conn.Character!);
            wire.Dispose();
        }
    }

    [Fact]
    public void Rain_lands_after_release_and_toxic_pods_damage_only_enemies()
    {
        var aim = new ProjectileVector(120, 100);
        var points = Game.RainLandings("RainOfArrows", aim, 18, 24, [aim + new ProjectileVector(2, 0)], new Random(3));
        Assert.Equal(18, points.Count);
        Assert.Equal(aim, points[0]);
        Assert.Equal(aim + new ProjectileVector(2, 0), points[1]);
        Assert.All(Game.RainLandings("BlastRain", aim, 6, 17, [], new Random(1)), p => Assert.InRange((p - aim).Length, 0, 20));
        using var b = new Battle("Toxic Rain");
        var gem = Game.GemSkillEffects(b.Conn).Single();
        Game.ReleaseArrowRain(b.Session, gem.Id, gem, (120, 100), new(1), 0);
        Assert.Equal(100000u, b.Enemy.Life);
        b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
        // Put the target under a known pod to verify the fractional DoT and the terminal burst.
        var use = Game.CaptureGemProjectile(b.Conn, b.Session, gem.Id, gem, (120, 100), new(1), groundArrow: true);
        b.Session.PlaceToxicPod(use, aim, 18, 1, 100, 5, 30, null);
        Assert.InRange(Game.ToxicWalkFactor(b.Enemy), .7, .95);
        Assert.Equal(1, Game.ToxicWalkFactor(b.Ally));
        uint landed = b.Enemy.Life;
        b.Session.AdvanceTo(ProjectileRuntime.Time(.95));
        Assert.True(b.Enemy.Life < landed);
        uint ticking = b.Enemy.Life;
        b.Session.AdvanceTo(ProjectileRuntime.Time(1.6));
        Assert.True(b.Enemy.Life < ticking);
        Assert.Equal(1, Game.ToxicWalkFactor(b.Enemy));
        Assert.Equal(100000u, b.Ally.Life);
    }

    [Fact]
    public void Supported_ice_arrow_hit_grants_a_temporary_autonomous_attack()
    {
        using var b = new Battle("Ice Shot", "Mirage Archer Support");
        var gem = Game.GemSkillEffects(b.Conn).Single();
        var use = Game.CaptureGemProjectile(b.Conn, b.Session, gem.Id, gem, (120, 100), new(1));
        var owner = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.NotNull(owner.MirageSource);
        b.Session.Schedule(0, () => true, () => use with { Payload = owner with { AlwaysHits = true } });
        b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
        uint initial = b.Enemy.Life;
        Assert.True(initial < b.Enemy.MaxLife);
        b.Session.AdvanceTo(ProjectileRuntime.Time(4.8));
        Assert.True(b.Enemy.Life < initial);
        uint expired = b.Enemy.Life;
        b.Session.AdvanceTo(ProjectileRuntime.Time(9));
        Assert.Equal(expired, b.Enemy.Life);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
    }
}

