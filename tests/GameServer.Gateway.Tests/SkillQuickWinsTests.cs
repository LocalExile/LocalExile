using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class SkillQuickWinsTests
{
    // Bow and wand attacks can use the held weapon's projectile, with skill-specific art where the data provides it.
    [Fact]
    public void Weapon_projectile_gems_fire_the_held_weapons_projectile_and_barrage_is_available()
    {
        foreach (var (weapon, expected) in new[] { ("Crude Bow", "Metadata/Projectiles/FrenzyArrow"),
            ("Driftwood Wand", "Metadata/Projectiles/FrenzyWand") })
        {
            using var f = new Fixture(weapon, "SkillGemFrenzy", "SkillGemBarrage");
            var frenzy = f.Gem("Frenzy");
            Assert.True(Game.ProjectilePathAvailable(f.Conn, frenzy.Id, frenzy.Effect));
            var use = Game.CaptureGemProjectile(f.Conn, f.Session, frenzy.Id, frenzy, (150, 100), new SkillCriticalRoll(0));
            Assert.Equal(expected, use.Profile.Id);
            Assert.False(Assert.IsType<ProjectileOffense>(use.Payload).ClientPredicted);
            var barrage = f.Gem("Barrage");
            Assert.NotNull(Game.ResolveProjectileDefinition(barrage.Effect));
            Assert.True(Game.ProjectilePathAvailable(f.Conn, barrage.Id, barrage.Effect));
            Assert.True(Game.SupportedSkill(f.Conn, barrage.Id).RequirementsMet);
        }
    }

    // A hand-cast Steelskin starts the guard buff through the triggered-guard code: 70% of each hit absorbed up to its
    // limit, 1.5 s, and its 3 s cooldown counted from the buff's end, refused before its cost until then.
    [Fact]
    public async Task Manual_steelskin_absorbs_and_recovers_after_its_buff()
    {
        using var f = new Fixture("Rusted Sword", "SkillGemSteelskin");
        var steelskin = f.Gem("QuickGuard");
        double now = Game.AreaTime(f.Conn);
        Assert.Null(Game.SelfBuffRefused(f.Conn, steelskin.Effect));
        Assert.True(await Game.CastSelfBuff(f.Conn, steelskin.Id, steelskin.Effect));
        Assert.NotNull(Game.SelfBuffRefused(f.Conn, steelskin.Effect));
        // The cast reads the game clock a little after now: the times below leave it half a second.
        Assert.Equal((6, 3, 3), Game.TriggeredGuardHit(f.Conn, 20, 10, 10, now + .1));
        Assert.Equal((3, 0, 0), Game.TriggeredGuardHit(f.Conn, 10, 0, 0, now + 1.4));
        Assert.Equal((50, 0, 0), Game.TriggeredGuardHit(f.Conn, 50, 0, 0, now + 2.5));
        Assert.False(Game.TriggerGuardReady(f.Conn, now + 4.4));
        Assert.True(Game.TriggerGuardReady(f.Conn, now + 5.5));
    }

    sealed class Fixture : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"weapon-projectile-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previous = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly TcpClient writer = new(), reader;
        readonly SemaphoreSlim gate;
        readonly IDisposable requirements;
        public readonly Player Conn;
        public readonly Game.ProjectileSession Session;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();

        public Fixture(string weapon, params string[] gems)
        {
            Saves.Open(path);
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            writer.Connect((IPEndPoint)listener.LocalEndpoint); reader = listener.AcceptTcpClient(); listener.Stop();
            Conn = new(new(1, "weapon-test", 1000, 1000, 100, 100, null, []), 1, 1)
            {
                Character = { Name = "Weapon" + Guid.NewGuid().ToString("N"), Experience = CharacterStats.ExperienceFor(100) },
                Connection = { PlayerSent = true, Session = new("weapon-test", Stream, Ciphers) },
                PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
                Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
            };
            Conn.Clock.Start();
            Conn.Walk.Teleport(100, 100, 0);
            var item = ItemCatalog.Create(Program.Items.Find(weapon)!) with
            {
                Sockets = [.. gems.Select(g => new ItemSocket(SocketColour.White,
                    ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + g)!)))],
                Links = [1, 1],
            };
            Game.ItemsOf(Conn).Add(PlayerInventories.Weapon1, 0, 0, item);
            requirements = TestRequirements.Waive(Conn);
            gate = Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
            Assert.True(gate.Wait(0));
            Session = new(Conn);
            Conn.ProjectileRuntime = Session;
        }

        public (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem(string effect) =>
            Game.GemSkillEffects(Conn).Single(g => Game.SkillsFor(Conn).EffectId(g.Effect) == effect);

        public void Dispose()
        {
            Conn.Closed = true; Session.Invalidate(); gate.Release();
            requirements.Dispose();
            writer.Dispose(); reader.Dispose();
            PlayerInventories.Forget(Conn.Character.Name!); Program.SetGameLogic(previous!);
            lifetime.Stop(_ => { }); Game.CombatLifetime = previousLifetime;
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
