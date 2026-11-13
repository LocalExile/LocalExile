using GameServer.Core.Protocol.Instance;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Projectiles;
using GameServer.Core.World;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public class SacrificeSupportTests
{
    [Fact]
    public async Task Vaal_fireball_pays_once_and_emits_a_spiral_over_two_seconds()
    {
        using var fixture = new Fixture();
        fixture.Equip("VaalFireball");
        var c = fixture.Conn;
        c.Clock.Restart();
        var gem = Game.GemSkillEffects(c).Single(g => Game.SkillsFor(c).EffectId(g.Effect) == "VaalFireball");
        bool Admit() => Game.TryBeginSkillCooldownUse(c, gem.Id, gem.Effect, null, false, () => c.ActionCount++, out _);
        Game.RefreshVaalSouls(c);
        Assert.False(Admit());
        for (int i = 0; i < 30; i++) Assert.True(c.VaalSouls.Gain());
        Assert.True(Admit());
        Assert.True(await Game.CastGemSkill("spiral", fixture.Stream, fixture.Ciphers, c, gem.Id, (100, 100), (150, 100)));
        Assert.False(await Game.CastGemSkill("duplicate", fixture.Stream, fixture.Ciphers, c, gem.Id, (100, 100), (150, 100)));
        long start = fixture.Session.Runtime.Now;
        var seen = new Dictionary<long, ProjectileVector>();
        for (int i = 0; i <= 64; i++)
        {
            fixture.Session.AdvanceTo(start + ProjectileRuntime.Time(i * .05));
            foreach (var shot in fixture.Session.Runtime.Projectiles) seen.TryAdd(shot.Id, shot.Use.Aim - shot.Use.Origin);
        }
        Assert.Equal(32, seen.Count);
        Assert.Contains(seen.Values, v => v.X > 0);
        Assert.Contains(seen.Values, v => v.X < 0);
        Assert.Contains(seen.Values, v => v.Y > 0);
        Assert.Contains(seen.Values, v => v.Y < 0);
        Assert.Equal(0u, c.VaalSouls.Read().Single().Current);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(20, 102)]
    public async Task Vaal_spark_releases_one_timed_wandering_stream(int quality, int count)
    {
        using var fixture = new Fixture();
        fixture.Equip("VaalSpark");
        var c = fixture.Conn;
        Game.ItemsOf(c).Change(PlayerInventories.Weapon1, fixture.Host, host => host with
        {
            Sockets = host.Sockets.Select((socket, index) => index == 0
                ? socket with { Gem = socket.Gem! with { Quality = (byte)quality } } : socket).ToArray()
        });
        c.Clock.Restart();
        var gem = Game.GemSkillEffects(c).Single(g => Game.SkillsFor(c).EffectId(g.Effect) == "VaalSpark");
        c.PlayerStats[Program.Stats.Key("skill_effect_duration_+%")!.Value] = 20;
        bool Admit() => Game.TryBeginSkillCooldownUse(c, gem.Id, gem.Effect, null, false, () => c.ActionCount++, out _);
        Game.RefreshVaalSouls(c);
        Assert.False(Admit());
        for (int i = 0; i < 30; i++) Assert.True(c.VaalSouls.Gain());
        double at = Game.ProjectileWorldTime(c);
        Assert.True(Admit());
        Assert.InRange(c.VaalPreventionUntil - at, 5.99, 6.2);
        Assert.True(await Game.CastGemSkill("spiral", fixture.Stream, fixture.Ciphers, c, gem.Id, (100, 100), (150, 100)));
        Assert.False(await Game.CastGemSkill("duplicate", fixture.Stream, fixture.Ciphers, c, gem.Id, (100, 100), (150, 100)));
        long start = fixture.Session.Runtime.Now;
        var seen = new Dictionary<long, ProjectileVector>();
        var groups = new HashSet<long>();
        for (int i = 0; i <= 85; i++)
        {
            fixture.Session.AdvanceTo(start + ProjectileRuntime.Time(i * .05));
            foreach (var shot in fixture.Session.Runtime.Projectiles)
            {
                Assert.NotNull(shot.Profile.Legs);
                groups.Add(shot.EmissionId);
                seen.TryAdd(shot.Id, shot.Direction);
            }
            if (i == 20) Assert.InRange(seen.Count, 1, count - 1);
        }
        Assert.Equal(count, seen.Count);
        Assert.Single(groups);
        Assert.Contains(seen.Values, v => v.X > 0);
        Assert.Contains(seen.Values, v => v.X < 0);
        Assert.Contains(seen.Values, v => v.Y > 0);
        Assert.Contains(seen.Values, v => v.Y < 0);
        Assert.Equal(0u, c.VaalSouls.Read().Single().Current);
        Assert.Equal(ProjectileRuntime.Time(3d / count), fixture.Session.Runtime.Projectiles.Last().Profile.SequentialDelay);
    }

    [Fact]
    public async Task Projectile_cast_sacrifices_once_at_release_and_added_chaos_uses_common_damage_rules()
    {
        foreach (string spell in new[] { "Fireball", "Spark" })
        {
            using var fixture = new Fixture();
            fixture.Equip(spell, echo: true);
            var conn = fixture.Conn;
            var gem = Game.GemSkillEffects(conn).First(g => g.Gem != 0 && (spell == "Reap" || Game.SkillsFor(conn).EffectId(g.Effect) == spell));
            var session = fixture.Session;
            await Game.LaunchProjectile("sacrifice", fixture.Stream, fixture.Ciphers, conn, gem.Id, (100,100), (120,100), conn.ActionInterruptionGeneration);
            Assert.Equal(1000u, conn.PlayerLife);
            _ = Game.CommitGemExperience(conn, 1);
            Game.ItemsOf(conn).Add(PlayerInventories.BodyArmour, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Plate Vest")!));
            session.AdvanceTo(ProjectileRuntime.Time(.6));
            Assert.Equal(800u, conn.PlayerLife);
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Enqueue(() => { drained.SetResult(); return Task.CompletedTask; });
            Assert.Same(drained.Task, await Task.WhenAny(drained.Task, session.OutputCompleted));
            await drained.Task;
            Assert.True(fixture.Reader.Available > 0, "Sacrifice must publish current life without waiting for recovery");
            var expectedLife = InstanceProtocol.LifeUpdate(Program.PlayerId, 800, conn.PlayerReservedLife);
            byte[] published = new byte[expectedLife.Length];
            await fixture.Reader.GetStream().ReadExactlyAsync(published);
            fixture.Decrypt.Receive.Process(published);
            Assert.Equal(expectedLife, published);
            var hit = Assert.IsType<ProjectileOffense>(session.Runtime.Projectiles.First().Use.Payload).Hit;
            Assert.True(hit[DamageType.Chaos].Min > 0);
            session.AdvanceTo(ProjectileRuntime.Time(1.5));
            Assert.Equal(800u, conn.PlayerLife);

            // A separate original cast pays again, with live life, and shares increased/more and prohibitions.
            fixture.ResetSession();
            conn.PlayerStats[Program.Stats.Key("chaos_damage_+%")!.Value] = 200;
            await Game.LaunchProjectile("sacrifice", fixture.Stream, fixture.Ciphers, conn, gem.Id, (100,100), (120,100), conn.ActionInterruptionGeneration);
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(.6));
            var increased = Assert.IsType<ProjectileOffense>(fixture.Session.Runtime.Projectiles.First().Use.Payload).Hit;
            Assert.Equal(hit[DamageType.Chaos].Min * 3, increased[DamageType.Chaos].Min, 6);
            fixture.ResetSession();
            conn.PlayerStats[Program.Stats.Key("base_deal_no_chaos_damage")!.Value] = 1;
            await Game.LaunchProjectile("sacrifice", fixture.Stream, fixture.Ciphers, conn, gem.Id, (100,100), (120,100), conn.ActionInterruptionGeneration);
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(.6));
            Assert.Equal(0, Assert.IsType<ProjectileOffense>(fixture.Session.Runtime.Projectiles.First().Use.Payload).Hit[DamageType.Chaos].Min);
            conn.PlayerStats[StatKeys.SpellSkillsDealNoDamage] = 1;
            Assert.Equal(0, Game.SupportedHitFor(conn, gem.Id, suppress: true, addedChaos: 70).MinTotal);
            conn.PlayerStats.Clear();
        }
    }

    [Fact]
    public async Task Cancelled_or_removed_sources_never_sacrifice_player_life()
    {
        using var fixture = new Fixture();
        foreach (string spell in new[] { "Fireball", "Spark", "Reap" })
        {
            fixture.Equip(spell);
            var conn = fixture.Conn;
            var gem = Game.GemSkillEffects(conn).First(g => g.Gem != 0 && (spell == "Reap" || Game.SkillsFor(conn).EffectId(g.Effect) == spell));
            await Game.LaunchProjectile("cancelled", null!, null!, conn, gem.Id, (100,100), (120,100), conn.ActionInterruptionGeneration, actionToken: conn.ActionCount - 1);
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(2));
            Assert.Equal(1000u, conn.PlayerLife);
            Assert.Empty(fixture.Session.Runtime.Projectiles);
            if (spell == "Reap") continue;
            fixture.ResetSession();
            await Game.LaunchProjectile("replacement", null!, null!, conn, gem.Id, (100,100), (120,100), conn.ActionInterruptionGeneration);
            Game.ItemsOf(conn).Change(PlayerInventories.Weapon1, fixture.Host, host => host with
            {
                Sockets = host.Sockets.Select((socket, index) => index == 0 ? socket with
                { Gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGem" + spell)!) } : socket).ToArray()
            });
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(2));
            Assert.Equal(1000u, conn.PlayerLife);
            Assert.Empty(fixture.Session.Runtime.Projectiles);
            fixture.ResetSession();
            await Game.LaunchProjectile("removed", null!, null!, conn, gem.Id, (100,100), (120,100), conn.ActionInterruptionGeneration);
            Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, fixture.Host);
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(2));
            Assert.Equal(1000u, conn.PlayerLife);
            Assert.Empty(fixture.Session.Runtime.Projectiles);
        }
        fixture.Equip("Reap");
        var player = fixture.Conn;
        player.Pools = player.Pools with { Life = 100000 };
        player.PlayerLife = 100000;
        player.PlayerStats[Program.Stats.Key("base_chance_to_poison_on_hit_%")!.Value] = 100;
        var reap = Game.GemSkillEffects(player).First(g => g.Gem != 0);
        var victim = new SpawnedMonster("sacrifice poison", new(120,100,0), 0,0,1000000) { Hostile = true };
        player.Monsters[123] = victim;
        await Game.AreaSkillHit("poison", fixture.Stream, fixture.Ciphers, player, reap.Id, reap, (100,100),(120,100), immediate: true);
        Assert.True(victim.Poisons.TotalDps(victim.Poisons.Until - .5) > 1000,
            $"Sacrificed life must reach area poison base damage: life={player.PlayerLife}, target={victim.Life}, dps={victim.Poisons.TotalDps(victim.Poisons.Until - .5)}, until={victim.Poisons.Until}");
    }

    [Fact]
    public async Task Frostblink_hits_and_chills_both_committed_endpoints_with_one_sacrifice()
    {
        using var fixture = new Fixture();
        fixture.Equip("Frostblink");
        var conn = fixture.Conn;
        var gem = Game.GemSkillEffects(conn).Single(g => g.Gem != 0);
        var use = new CooldownUseTicket(gem.Effect, conn.ActionCount, conn.ProjectileEpoch, Instant: true);
        foreach (var (id, x) in new[] { (101u, 100), (102u, 160), (103u, 130) })
            conn.Monsters[id] = new("frostblink target", new(x, 100, 0), 0, 0, 100000) { Hostile = true };
        await Game.FrostblinkAreas("blink", fixture.Stream, fixture.Ciphers, conn, gem, (100, 100), (160, 100), use);
        Assert.True(conn.Monsters[101].Life < 100000);
        Assert.True(conn.Monsters[102].Life < 100000);
        Assert.Equal(100000u, conn.Monsters[103].Life);
        Assert.Equal(800u, conn.PlayerLife);
        Assert.Collection(conn.Reservations.Trail,
            ice => { Assert.Equal((100, 100), ice.At); Assert.Equal(16, ice.Radius); },
            ice => { Assert.Equal((160, 100), ice.At); Assert.Equal(16, ice.Radius); });
        Assert.Equal(conn.Reservations.Trail[0].Until, conn.Reservations.Trail[1].Until);
        await Game.FrostblinkAreas("stale", fixture.Stream, fixture.Ciphers, conn, gem, (0, 0), (20, 0),
            use with { ProjectileEpoch = use.ProjectileEpoch - 1 });
        Assert.Equal(2, conn.Reservations.Trail.Count);
    }

    [Fact]
    public async Task Lightning_warp_moves_at_release_and_hits_both_endpoints()
    {
        using var fixture = new Fixture();
        fixture.Equip("LightningWarp");
        var c = fixture.Conn;
        var gem = Game.GemSkillEffects(c).Single(g => g.Gem != 0);
        c.Walk.Teleport(100, 100, 0);
        c.Walk.SetSpeed(40, 0);
        foreach (var (id, x) in new[] { (301u, 100), (302u, 160), (303u, 130) })
            c.Monsters[id] = new("warp target", new(x, 100, 0), 0, 0, 100000) { Hostile = true };
        Assert.False(Game.LandsAtAim(c, gem.Effect));
        Assert.True(await Game.CastGemSkill("warp", fixture.Stream, fixture.Ciphers, c, gem.Id, (100, 100), (160, 100)));
        Assert.Equal((100, 100), c.Walk.Position(0));
        double start = Math.Max(c.Clock.Elapsed.TotalSeconds, Game.ProjectileWorldTime(c));
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(start + 4));
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.Enqueue(() => { sent.SetResult(); return Task.CompletedTask; });
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((160, 100), c.Walk.Position(start + 4));
        Assert.True(c.Monsters[301].Life < 100000);
        Assert.True(c.Monsters[302].Life < 100000);
        Assert.Equal(100000u, c.Monsters[303].Life);
    }

    [Fact]
    public async Task Unleash_seals_release_distinct_discounted_fireballs_and_reset_on_source_replacement()
    {
        using var fixture = new Fixture();
        var conn = fixture.Conn;
        conn.Clock.Reset();
        fixture.Equip("Fireball", support: "SupportGemHandcastAnticipation");
        var skill = Game.GemSkillEffects(conn).Single(g => g.Gem != 0);
        var source = Game.SupportedSkill(conn, skill.Id);
        var session = fixture.Session;
        double now = Game.ProjectileWorldTime(conn);
        Assert.Equal(0, Game.UnleashSeals(conn, skill.Id, source, now));
        Assert.Equal(0, Game.UnleashSeals(conn, skill.Id, source, now + .89));
        Assert.Equal(1, Game.UnleashSeals(conn, skill.Id, source, now + .9));
        session.AdvanceTo(ProjectileRuntime.Time(now + 3));
        Assert.Equal(3, Game.UnleashSeals(conn, skill.Id, source, Game.ProjectileWorldTime(conn)));
        double start = Game.ProjectileWorldTime(conn);
        await Game.LaunchProjectile("unleash", fixture.Stream, fixture.Ciphers, conn, skill.Id, (100, 100),
            (200, 100), conn.ActionInterruptionGeneration, swingAt: start);
        Assert.Equal(0, Game.UnleashSeals(conn, skill.Id, source, start));
        session.AdvanceTo(ProjectileRuntime.Time(start + .9));
        var hits = session.Runtime.Projectiles.Select(p => ((ProjectileOffense)p.Use.Payload!).Hit[DamageType.Fire].Min)
            .OrderDescending().ToArray();
        Assert.Equal(4, hits.Length);
        Assert.True(hits[0] > 0);
        Assert.All(hits.Skip(1), hit => Assert.Equal(hits[0] * .55, hit, 6));
        fixture.Equip("Fireball", support: "SupportGemHandcastAnticipation");
        var replacement = Game.SupportedSkill(conn, skill.Id);
        Assert.Equal(0, Game.UnleashSeals(conn, skill.Id, replacement, Game.ProjectileWorldTime(conn)));
    }

    sealed class Fixture : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"sacrifice-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previous = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        public ClientState Conn { get; }
        public Game.ProjectileSession Session { get; private set; } = null!;
        public uint Host;
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        public TcpClient Reader { get; }
        public NetworkStream Stream => writer.GetStream();
        public ConnectionCiphers Ciphers { get; } = new(new byte[64], false);
        public ConnectionCiphers Decrypt { get; } = new(new byte[64], true);
        public Fixture()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
            Saves.Open(path);
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            Conn = new(new(1, "sacrifice", 1000,1000,100,100,null,[]),1,1)
            {
                Character = "Sacrifice" + Guid.NewGuid().ToString("N"), PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
                Experience = CharacterStats.ExperienceFor(100),
                Pools = new(100,1000,1000,int.MaxValue,0,Strength:1000,Dexterity:1000,Intelligence:1000),
                LeagueModule = new(new League("test","Standard",false,false),new HashSet<string>(["Game"]))
            };
            Conn.Clock.Restart();
            ResetSession();
        }
        public void ResetSession()
        {
            Session?.Invalidate();
            Conn.PlayerLife = 1000;
            Session = new("sacrifice",Stream,Ciphers,Conn);
            Conn.ProjectileRuntime = Session;
        }
        public void Equip(string spell, bool echo = false, string support = "SupportGemSacrifice")
        {
            if (Host != 0) Game.ItemsOf(Conn).Remove(PlayerInventories.Weapon1, Host);
            ResetSession();
            var sockets = new List<ItemSocket>();
            foreach (string id in echo ? new[] { "SkillGem" + (spell == "Reap" ? "Bloodreap" : spell), support, "SupportGemSpellEcho" }
                                      : new[] { "SkillGem" + (spell == "Reap" ? "Bloodreap" : spell), support })
                sockets.Add(new(SocketColour.White,ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + id)!)));
            var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with { Mods = [], Sockets = sockets.ToArray(), Links = [(byte)sockets.Count] };
            Host = Game.ItemsOf(Conn).Add(PlayerInventories.Weapon1,0,0,wand).Id;
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                Conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
        }
        public void Dispose()
        {
            Session.Invalidate(); Reader.Dispose(); writer.Dispose(); listener.Stop(); lifetime.Stop(ex => throw ex); Game.CombatLifetime = previousLifetime; PlayerInventories.Forget(Conn.Character!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path);
        }
    }
}
