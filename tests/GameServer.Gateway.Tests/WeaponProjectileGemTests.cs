using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class WeaponProjectileGemTests
{
    // A bow/wand attack gem with no projectile row of its own fires the held weapon's (its own art by weapon where the
    // data has it); a Barrage volley still has no recipe and stays refused before its cost.
    [Fact]
    public void Weapon_projectile_gems_fire_the_held_weapons_projectile_and_barrage_stays_refused()
    {
        foreach (var (weapon, expected) in new[] { ("Crude Bow", "Metadata/Projectiles/FrenzyArrow"),
            ("Driftwood Wand", "Metadata/Projectiles/FrenzyWand") })
        {
            using var f = new Fixture(weapon, "SkillGemFrenzy", "SkillGemBarrage");
            var frenzy = f.Gem("Frenzy");
            Assert.True(Game.ProjectilePathAvailable("weapon-test", f.Conn, frenzy.Id, frenzy.Effect));
            var use = Game.CaptureGemProjectile(f.Conn, f.Session, frenzy.Id, frenzy, (150, 100), new SkillCriticalRoll(0));
            Assert.Equal(expected, use.Profile.Id);
            Assert.False(Assert.IsType<ProjectileOffense>(use.Payload).ClientPredicted);
            var barrage = f.Gem("Barrage");
            Assert.Null(Game.ResolveProjectileDefinition(barrage.Effect));
            Assert.False(Game.ProjectilePathAvailable("weapon-test", f.Conn, barrage.Id, barrage.Effect));
        }
    }

    sealed class Fixture : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"weapon-projectile-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previous = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly TcpClient writer = new(), reader;
        readonly SemaphoreSlim gate;
        public readonly ClientState Conn;
        public readonly Game.ProjectileSession Session;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();

        public Fixture(string weapon, params string[] gems)
        {
            Saves.Open(path);
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
            Conn = new(new(1, "weapon-test", 1000, 1000, 100, 100, null, []), 1, 1)
            {
                Character = "Weapon" + Guid.NewGuid().ToString("N"), PlayerLife = 1000, PlayerMana = 1000,
                Experience = CharacterStats.ExperienceFor(100), PlayerSent = true, Instance = new(),
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
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) Conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            writer.Connect((IPEndPoint)listener.LocalEndpoint); reader = listener.AcceptTcpClient(); listener.Stop();
            gate = Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
            Assert.True(gate.Wait(0));
            Session = new("weapon-test", Stream, Ciphers, Conn);
            Conn.ProjectileRuntime = Session;
        }

        public (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem(string effect) =>
            Game.GemSkillEffects(Conn).Single(g => Game.SkillsFor(Conn).EffectId(g.Effect) == effect);

        public void Dispose()
        {
            Conn.Closed = true; Session.Invalidate(); gate.Release();
            writer.Dispose(); reader.Dispose();
            PlayerInventories.Forget(Conn.Character!); Program.SetGameLogic(previous!);
            lifetime.Stop(_ => { }); Game.CombatLifetime = previousLifetime;
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
