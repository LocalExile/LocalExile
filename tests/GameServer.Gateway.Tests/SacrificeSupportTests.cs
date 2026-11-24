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
        var c = fixture.Player;
        c.Clock.Restart();
        var gem = Game.GemSkillEffects(c).Single(g => Game.SkillsFor(c).EffectId(g.Effect) == "VaalFireball");
        bool Admit() => Game.TryBeginSkillCooldownUse(c, gem.Id, gem.Effect, null, false, () => c.ActionCount++, out _);
        Game.RefreshVaalSouls(c);
        Assert.False(Admit());
        for (int i = 0; i < 30; i++) Assert.True(c.VaalSouls.Gain());
        Assert.True(Admit());
        Assert.True(await Game.CastGemSkill(c, gem.Id, (100, 100), (150, 100)));
        Assert.False(await Game.CastGemSkill(c, gem.Id, (100, 100), (150, 100)));
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

    [Fact]
    public async Task Frostblink_hits_and_chills_both_committed_endpoints_with_one_sacrifice()
    {
        using var fixture = new Fixture();
        fixture.Equip("Frostblink");
        var player = fixture.Player;
        var gem = Game.GemSkillEffects(player).Single(g => g.Gem != 0);
        var use = new CooldownUseTicket(gem.Effect, player.ActionCount, player.ProjectileEpoch, Instant: true);
        foreach (var (id, x) in new[] { (101u, 100), (102u, 160), (103u, 130) })
            player.Area.Monsters[id] = new("frostblink target", new(x, 100, 0), 0, 0, 100000) { Hostile = true };
        await Game.FrostblinkAreas(player, gem, (100, 100), (160, 100), use);
        Assert.True(player.Area.Monsters[101].Life < 100000);
        Assert.True(player.Area.Monsters[102].Life < 100000);
        Assert.Equal(100000u, player.Area.Monsters[103].Life);
        Assert.Equal(800u, player.PlayerLife);
        Assert.Collection(player.Reservations.Trail,
            ice => { Assert.Equal((100, 100), ice.At); Assert.Equal(16, ice.Radius); },
            ice => { Assert.Equal((160, 100), ice.At); Assert.Equal(16, ice.Radius); });
        Assert.Equal(player.Reservations.Trail[0].Until, player.Reservations.Trail[1].Until);
        await Game.FrostblinkAreas(player, gem, (0, 0), (20, 0),
            use with { ProjectileEpoch = use.ProjectileEpoch - 1 });
        Assert.Equal(2, player.Reservations.Trail.Count);
    }

    [Fact]
    public async Task Frostblink_of_Wintry_Blast_clamps_landing_uses_20_radius_and_consumes_chill_for_more_damage()
    {
        using var fixture = new Fixture();
        var c = fixture.Player;
        c.Connection.PlayerSent = true;
        c.Use(new(1, "wintry-blast", 200, 200, 100, 100, (x, y) => x >= 0 && y >= 0 && x < 200 && y < 200, []));
        var found = Program.Items.FindGem("Frostblink of Wintry Blast")!.Value;
        var gemItem = ItemCatalog.Create(found.Type) with { GemVariant = found.Variant.Index };
        var wand = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!) with
        {
            Mods = [],
            Sockets = [new(SocketColour.White, gemItem)],
            Links = [1]
        };
        Game.ItemsOf(c).Add(PlayerInventories.Weapon1, 0, 0, wand);
        var gem = Game.GemSkillEffects(c).Single(g => g.Gem != 0);
        Assert.Equal("FrostblinkAltX", Game.SkillsFor(c).EffectId(gem.Effect));
        Assert.Equal(20, SkillArea.BaseRadius("FrostblinkAltX", _ => 0));
        Assert.False(Game.LandsAtAim(c, gem.Effect));
        Assert.Equal((142, 100), Game.MovementLanding(c, gem.Effect, (100, 100), (190, 100)));
        Assert.Equal((132, 100), Game.MovementLanding(c, gem.Effect, (100, 100), (110, 100)));
        double now = Game.ProjectileWorldTime(c);
        var unchilled = new SpawnedMonster("target-a", new(105, 100, 0), 0, 0, 10000) { Hostile = true };
        var chilled = new SpawnedMonster("target-b", new(105, 100, 0), 0, 0, 10000) { Hostile = true };
        chilled.Ailments.Proliferated(MonsterElementalAilments.Kind.Chill,
            new(MonsterElementalAilments.Kind.Chill, now - 1, now + 5, 30), _ => 0);
        Assert.Equal(30, chilled.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, now));
        c.Area.Monsters[0x80000001u] = unchilled;
        c.Area.Monsters[0x80000002u] = chilled;
        await Game.AreaSkillHit(c, gem.Id, gem, (100, 100), (105, 100), immediate: true);
        uint unchilledLost = 10000 - unchilled.Life;
        uint chilledLost = 10000 - chilled.Life;
        Assert.True(unchilledLost > 0);
        Assert.True(chilledLost > unchilledLost, $"expected chilled damage ({chilledLost}) > unchilled ({unchilledLost})");
        double afterHit = Game.ProjectileWorldTime(c);
        Assert.Equal(0, chilled.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, afterHit));
        Assert.Equal(0, unchilled.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, afterHit));
        Assert.False(unchilled.SourceMarked(c.Id, "inflicted_chill", afterHit));
    }

    [Fact]
    public async Task Lightning_warp_moves_at_release_and_hits_both_endpoints()
    {
        using var fixture = new Fixture();
        fixture.Equip("LightningWarp");
        var c = fixture.Player;
        var gem = Game.GemSkillEffects(c).Single(g => g.Gem != 0);
        c.Walk.Teleport(100, 100, 0);
        c.Walk.SetSpeed(40, 0);
        foreach (var (id, x) in new[] { (301u, 100), (302u, 160), (303u, 130) })
            c.Area.Monsters[id] = new("warp target", new(x, 100, 0), 0, 0, 100000) { Hostile = true };
        Assert.False(Game.LandsAtAim(c, gem.Effect));
        Assert.True(await Game.CastGemSkill(c, gem.Id, (100, 100), (160, 100)));
        Assert.Equal((100, 100), c.Walk.Position(0));
        double start = Math.Max(c.Clock.Elapsed.TotalSeconds, Game.ProjectileWorldTime(c));
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(start + 4));
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.Enqueue(() => { sent.SetResult(); return Task.CompletedTask; });
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((160, 100), c.Walk.Position(start + 4));
        Assert.True(c.Area.Monsters[301].Life < 100000);
        Assert.True(c.Area.Monsters[302].Life < 100000);
        Assert.Equal(100000u, c.Area.Monsters[303].Life);
    }

    [Fact]
    public async Task Unleash_seals_release_distinct_discounted_fireballs_and_reset_on_source_replacement()
    {
        using var fixture = new Fixture();
        var player = fixture.Player;
        player.Clock.Reset();
        fixture.Equip("Fireball", support: "SupportGemHandcastAnticipation");
        var skill = Game.GemSkillEffects(player).Single(g => g.Gem != 0);
        var source = Game.SupportedSkill(player, skill.Id);
        var session = fixture.Session;
        double now = Game.ProjectileWorldTime(player);
        Assert.Equal(0, Game.UnleashSeals(player, skill.Id, source, now));
        Assert.Equal(0, Game.UnleashSeals(player, skill.Id, source, now + .89));
        Assert.Equal(1, Game.UnleashSeals(player, skill.Id, source, now + .9));
        session.AdvanceTo(ProjectileRuntime.Time(now + 3));
        Assert.Equal(3, Game.UnleashSeals(player, skill.Id, source, Game.ProjectileWorldTime(player)));
        double start = Game.ProjectileWorldTime(player);
        await Game.LaunchProjectile(player, skill.Id, (100, 100),
            (200, 100), player.ActionInterruptionGeneration, swingAt: start);
        Assert.Equal(0, Game.UnleashSeals(player, skill.Id, source, start));
        session.AdvanceTo(ProjectileRuntime.Time(start + .9));
        var hits = session.Runtime.Projectiles.Select(p => ((ProjectileOffense)p.Use.Payload!).Hit[DamageType.Fire].Min)
            .OrderDescending().ToArray();
        Assert.Equal(4, hits.Length);
        Assert.True(hits[0] > 0);
        Assert.All(hits.Skip(1), hit => Assert.Equal(hits[0] * .55, hit, 6));
        fixture.Equip("Fireball", support: "SupportGemHandcastAnticipation");
        var replacement = Game.SupportedSkill(player, skill.Id);
        Assert.Equal(0, Game.UnleashSeals(player, skill.Id, replacement, Game.ProjectileWorldTime(player)));
    }

    sealed class Fixture : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"sacrifice-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previous = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        public Player Player { get; }
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
            Player = new(new(1, "sacrifice", 1000,1000,100,100,null,[]),1,1)
            {
                Character = { Name = "Sacrifice" + Guid.NewGuid().ToString("N"), Experience = CharacterStats.ExperienceFor(100) },
                PlayerLife = 1000,
                PlayerMana = 1000,
                Instance = new(),
                Pools = new(100,1000,1000,int.MaxValue,0,Strength:1000,Dexterity:1000,Intelligence:1000),
                LeagueModule = new(new League("test","Standard",false,false),new HashSet<string>(["Game"]))
            };
            Player.Clock.Restart();
            Player.Connection.Session = new("test", Stream, Ciphers);
            TestRequirements.Waive(Player);
            ResetSession();
        }
        public void ResetSession()
        {
            Session?.Invalidate();
            Player.PlayerLife = 1000;
            Session = new(Player);
            Player.ProjectileRuntime = Session;
        }
        public void Equip(string spell, bool echo = false, string support = "SupportGemSacrifice")
        {
            if (Host != 0) Game.ItemsOf(Player).Remove(PlayerInventories.Weapon1, Host);
            ResetSession();
            var sockets = new List<ItemSocket>();
            foreach (string id in echo ? new[] { "SkillGem" + (spell == "Reap" ? "Bloodreap" : spell), support, "SupportGemSpellEcho" }
                                      : new[] { "SkillGem" + (spell == "Reap" ? "Bloodreap" : spell), support })
                sockets.Add(new(SocketColour.White,ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + id)!)));
            var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with { Mods = [], Sockets = sockets.ToArray(), Links = [(byte)sockets.Count] };
            Host = Game.ItemsOf(Player).Add(PlayerInventories.Weapon1,0,0,wand).Id;
        }
        public void Dispose()
        {
            Session.Invalidate(); Reader.Dispose(); writer.Dispose(); listener.Stop(); lifetime.Stop(ex => throw ex); Game.CombatLifetime = previousLifetime; PlayerInventories.Forget(Player.Character.Name!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path);
        }
    }
}
