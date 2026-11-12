using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class LightningStrikeTests
{
    [Fact]
    public void Strike_and_bolts_resolve_native_metadata_and_separate_damage_contexts()
    {
        using var f = new Fixture();
        var gem = f.Gem;
        var skills = Game.SkillsFor(f.Conn);
        Assert.Equal("Metadata/Projectiles/LightningSlash", skills.ProjectileFor(gem.Effect)!.Value.Id);
        Assert.True(Game.ProjectilePathAvailable("lightning-test", f.Conn, gem.Id, gem.Effect));
        var use = Game.CaptureGemProjectile(f.Conn, f.Session, gem.Id, gem, (150, 100), new SkillCriticalRoll(0));
        Assert.Equal(3, use.Profile.Count);
        Assert.True(Assert.IsType<ProjectileOffense>(use.Payload).ClientPredicted);
        var melee = Game.SupportedHitInputsFor(f.Conn, gem.Id)!;
        var bolt = Game.SupportedHitInputsFor(f.Conn, gem.Id, projectile: true)!;
        Assert.True(melee.Melee); Assert.False(melee.Projectile);
        Assert.False(bolt.Melee); Assert.True(bolt.Projectile);
        // The gem's 50% conversion is shared; its 50% less projectile hit/ailment damage belongs only to bolts.
        Assert.Equal(.5, melee.Conversion.Trace(melee.Raw).Where(p => p.Type == DamageType.Lightning).Sum(p => p.Range.Min) / melee.Raw.MinTotal, 6);
        Assert.Equal(melee.Raw.MinTotal * .5, bolt.Raw.MinTotal, 6);
        double strike = Game.SupportedHit(f.Conn, gem.Id).MinTotal;
        double projectile = Game.SupportedProjectileHit(f.Conn, gem.Id, area: false).MinTotal;
        f.Set("projectile_damage_+%", 100);
        Assert.Equal(strike, Game.SupportedHit(f.Conn, gem.Id).MinTotal, 6);
        Assert.True(Game.SupportedProjectileHit(f.Conn, gem.Id, area: false).MinTotal > projectile);
        f.Set("projectile_damage_+%", 0);
        f.Set("melee_damage_+%", 100);
        Assert.Equal(projectile, Game.SupportedProjectileHit(f.Conn, gem.Id, area: false).MinTotal, 6);
        Assert.True(Game.SupportedHit(f.Conn, gem.Id).MinTotal > strike);
        f.Set("number_of_additional_projectiles", 2);
        Assert.Equal(5, Game.CaptureGemProjectile(f.Conn, f.Session, gem.Id, gem, (150, 100), new SkillCriticalRoll(0)).Profile.Count);
    }

    [Fact]
    public void A_strike_hits_one_melee_victim_and_its_bolts_hit_farther_enemies_without_hitting_it_again()
    {
        using var f = new Fixture();
        var c = f.Conn;
        var near = new SpawnedMonster("near", new(108, 100, 0), 0, 1, 1000000);
        var far = new SpawnedMonster("far", new(140, 100, 0), 0, 1, 1000000);
        var friend = new SpawnedMonster("friend", new(107, 100, 0), 0, 1, 1000000) { Hostile = false };
        c.Monsters[41] = near; c.Monsters[42] = far; c.Monsters[43] = friend;
        Assert.Equal(41u, Game.LightningStrikeTarget(c, (100, 100), (108, 100), 0, 0));
        Assert.Equal(0u, Game.LightningStrikeTarget(c, (100, 100), (140, 100), 42, 0));
        Assert.Equal(0u, Game.LightningStrikeTarget(c, (100, 100), (107, 100), 43, 0));
        var use = Game.ReleaseLightningStrike("lightning-test", f.Stream, f.Ciphers, c, f.Session,
            f.Gem.Id, f.Gem, (100, 100), (140, 100), 41, new SkillCriticalRoll(0), 1);
        uint afterMelee = near.Life;
        Assert.True(afterMelee < near.MaxLife);
        Assert.Equal(far.MaxLife, far.Life);
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.Equal(f.Session.ActorKey(41, near), shot.ExcludedTarget);
        Assert.True(shot.AccuracyProfile!.Value.CannotBeEvaded);
        f.Session.Runtime.Release(use);
        f.Session.AdvanceTo(ProjectileRuntime.Time(1));
        Assert.Equal(afterMelee, near.Life);
        Assert.True(far.Life < far.MaxLife, $"active={f.Session.Active}, now={f.Session.Runtime.Now}, origin={use.Origin}, aim={use.Aim}, speed={use.Profile.Speed}, range={use.Profile.Range}, near={afterMelee}");
        Assert.Equal(friend.MaxLife, friend.Life);
        // Empty or distant ground still releases the volley, without inventing an area or distant melee hit.
        c.Monsters.Remove(41); c.Monsters.Remove(42);
        var empty = Game.ReleaseLightningStrike("lightning-test", f.Stream, f.Ciphers, c, f.Session,
            f.Gem.Id, f.Gem, (100, 100), (150, 100), 0, new SkillCriticalRoll(0), 2);
        Assert.Null(Assert.IsType<ProjectileOffense>(empty.Payload).ExcludedTarget);
        Assert.Equal(3, empty.Profile.Count);
        // A scheduled release is invalidated by a real interruption before contact, with no delayed melee damage.
        c.Monsters[44] = new("interrupted", new(108, 100, 0), 0, 1, 1000000);
        Game.ScheduleLightningStrike("lightning-test", f.Stream, f.Ciphers, c, f.Gem.Id, f.Gem,
            (100, 100), (108, 100), 44, c.ActionInterruptionGeneration, null,
            new SkillCriticalRoll(0), 3, f.Session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond);
        c.FreezeGeneration++;
        f.Session.AdvanceTo(f.Session.Runtime.Now + ProjectileRuntime.Time(2));
        Assert.Equal(c.Monsters[44].MaxLife, c.Monsters[44].Life);
    }

    sealed class Fixture : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"lightning-strike-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previous = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly TcpClient writer = new(), reader;
        readonly SemaphoreSlim gate;
        public readonly ClientState Conn;
        public readonly Game.ProjectileSession Session;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public readonly (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem;

        public Fixture()
        {
            Saves.Open(path);
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
            Conn = new(new(1, "lightning-test", 1000, 1000, 100, 100, null, []), 1, 1)
            {
                Character = "Lightning" + Guid.NewGuid().ToString("N"), PlayerLife = 1000, PlayerMana = 1000,
                PlayerSent = true, Instance = new(),
                Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
            };
            Conn.Clock.Start();
            Conn.Walk.Teleport(100, 100, 0);
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemLightningStrike")!);
            var sword = ItemCatalog.Create(Program.Items.Find("Rusted Sword")!) with
            { Sockets = [new ItemSocket(SocketColour.White, gem)], Links = [1] };
            Game.ItemsOf(Conn).Add(PlayerInventories.Weapon1, 0, 0, sword);
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" }) Set(name, 1000);
            Set("global_always_hit", 1);
            Gem = Game.GemSkillEffects(Conn).Single(g => Game.SkillsFor(Conn).EffectId(g.Effect) == "LightningStrike");
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            writer.Connect((IPEndPoint)listener.LocalEndpoint); reader = listener.AcceptTcpClient(); listener.Stop();
            gate = Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
            Assert.True(gate.Wait(0));
            Session = new("lightning-test", Stream, Ciphers, Conn);
            Conn.ProjectileRuntime = Session;
        }

        public void Set(string name, int value) => Conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;

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
