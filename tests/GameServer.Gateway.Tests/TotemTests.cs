using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Totems other than Rejuvenation Totem could not be summoned and did nothing when used: the gate asked
/// <c>TotemAuras.ContainsKey(effect)</c>, which held only RejuvenationTotem, and no use was ever converted into a hit.
/// The first fact covers the gate against the real data; the second drives a placed totem's use through the tick.
/// </summary>
public sealed class TotemTests
{
    // The GrantedEffects rows the data has behind the totem skills that were broken (their ActiveSkills ids:
    // shockwave_totem, holy_flame_totem, ancestor_totem_slash, totem_fire_spray, searing_bond). SkillTotemId cannot
    // name them: every ActiveSkills row carries one.
    static readonly string[] TotemEffects =
        ["ShockwaveTotem", "HolyFlameTotem", "AncestorTotemSlash", "SearingBond", "WbFlameTotem"];

    [Fact]
    public async Task Holy_flame_placement_does_not_start_a_paid_player_channel()
    {
        using var f = new AnimatedAdmissionTests.Fixture();
        var conn = f.Conn;
        var clock = new PlacementClock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            var gem = ItemCatalog.Create(Program.Items.Find("Holy Flame Totem")!);
            var host = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!, [new(SocketColour.White, gem)], [1]);
            var items = Game.ItemsOf(conn);
            items.Remove(PlayerInventories.Weapon1, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id);
            items.Add(PlayerInventories.Weapon1, 0, 0, host);
            var source = Assert.Single(Game.GemSkillEffects(conn));

            uint mana = conn.PlayerMana;
            Assert.True(await Game.CastGemSkill("test", f.Stream, f.Ciphers, conn, source.Id,
                (100, 100), (108, 100), actionToken: conn.ActionCount));
            Assert.True(conn.PlayerMana < mana);
            Assert.Null(conn.PaidChannel);
            Assert.True(clock.Waited);
        }
        finally
        {
            conn.Closed = true;
            clock.Release.TrySetResult();
            Game.AreaUpdateContext.Value = previous;
        }
    }

    sealed class PlacementClock : IPlayerUpdateSchedule
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Waited { get; private set; }
        public double Now(ClientState conn) => 0;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            Waited = true;
            return Release.Task;
        }
    }

    [Fact]
    public void EveryTotemSkillCarriesSummonsTotemAndResolvesToASupportedVariety()
    {
        var skills = new SkillCatalog(); var monsters = new MonsterCatalog();
        var conn = new ClientState(new(1, "totem-catalog", 1000, 1000, 100, 100, null, []), 1, 1);
        int rejuvenation = skills.EffectRow("RejuvenationTotem");
        Assert.True(rejuvenation >= 0);
        // The old gate's only key: TotemAuras held RejuvenationTotem and nothing else.
        Assert.DoesNotContain(rejuvenation, TotemEffects.Select(skills.EffectRow));
        foreach (string effectId in TotemEffects)
        {
            int row = skills.EffectRow(effectId);
            Assert.True(row >= 0, $"missing GrantedEffects row: {effectId}");
            Assert.True(skills.HasType(row, Game.TotemSkillType), $"{effectId} (row {row}) is not {Game.TotemSkillType}");
            var variety = Game.TotemVariety(conn, row);
            Assert.True(variety is not null, $"{effectId} (row {row}) summons no totem variety");
            Assert.True(variety!.Supported, $"{effectId} variety {variety.Id} ({variety.Row}) is not supported");
            Assert.NotNull(monsters.Find(variety.Row.ToString()));
        }
        // Rejuvenation Totem keeps working, so the fix is a widening of the gate rather than a replacement.
        Assert.NotNull(Game.TotemVariety(conn, rejuvenation));
    }

    [Fact]
    public async Task AShockwaveTotemCastsForHostilesInRangeAndHitsAroundItself()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"localexile-totems-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull; var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        using var wire = new Wire();
        Saves.Open(path); Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "totem-runtime", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "TotemRuntime", Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("totem-test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemShockwaveTotem")!);
            var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
            { Sockets = [new(SocketColour.White, gem)] };
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, weapon);
            var source = Game.GemSkillEffects(conn).Single(g => g.Name == "Shockwave Totem");
            var variety = Game.TotemVariety(conn, source.Effect);
            Assert.NotNull(variety);
            uint? placed = await Game.CreateTotem("test", wire.Stream, wire.Ciphers, conn, source.Id, source.Effect,
                source.Level, variety!, (50, 50));
            Assert.NotNull(placed);
            var totem = conn.Totems.All.Single();
            // The hybrid boundary: this is a dedicated totem skill, not an adopted projectile spell, so it keeps the
            // local contact path (a Strike) and is never ticked by the spell executor (Effect stays -1). The lab reads
            // the effect row it was summoned from through EffectRow, which is the deliberately different fact.
            Assert.True(totem.Effect < 0, "a dedicated totem skill must not be an adopted projectile spell totem");
            Assert.Equal(source.Effect, totem.EffectRow);
            // Shockwave Totem is a nova (ActiveSkillType Nova, radius 24): it casts around itself, not at a target.
            Assert.NotNull(totem.Strike);

            // A hostile outside its totem range (base_totem_range 80) starts no cast.
            conn.Monsters[0x08000002] = new SpawnedMonster("far", new(50, 140, 0), 100, 8, 1000);
            Game.TickTotemUses("test", wire.Stream, wire.Ciphers, conn);
            Assert.Equal(0, totem.Strike!.NextCast);

            // One inside the range starts it; at the contact only what stands within the radius of the totem is hit.
            var target = new SpawnedMonster("near", new(55, 50, 0), 100, 8, 100000);
            var outside = new SpawnedMonster("outside", new(50, 90, 0), 100, 8, 100000);
            conn.Monsters[0x08000001] = target; conn.Monsters[0x08000003] = outside;
            uint before = target.Life, outsideBefore = outside.Life;
            Game.TickTotemUses("test", wire.Stream, wire.Ciphers, conn);
            Assert.True(totem.Strike.NextCast > 0);

            // The contact is on the projectile runtime (a kill takes the XP gate before the Monsters lock): advance it to
            // the end of the cast instead of waiting on real time.
            var session = Game.ProjectilesFor("test", wire.Stream, wire.Ciphers, conn);
            using (Game.ProjectileMutation(conn, advance: false))
                session.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(totem.Strike.NextCast));
            Assert.True(target.Life < before, $"the nova dealt no damage: {target.Life} of {target.MaxLife}");
            Assert.Equal(outsideBefore, outside.Life);

            // /totems reads the same collection through the same call the command makes, so the listing carries the
            // fields the task needs — a totem the lab can see at all. (The Monsters array of /state cannot list one:
            // a totem never enters ClientState.Monsters.)
            string listing = Game.TotemsListing(conn);
            Assert.Contains("0x", listing);
            Assert.Contains(variety!.Id, listing);
            Assert.Contains($"life {totem.Life}/{totem.MaxLife}", listing);
            Assert.Contains($"at ({totem.At.X},{totem.At.Y})", listing);
            Assert.Contains($"skill {totem.SkillId}", listing);
            Assert.Contains("ShockwaveTotem", listing);
            Assert.EndsWith("1 totem(s).", listing);
        }
        finally
        {
            conn.Closed = true; PlayerInventories.Forget(conn.Character!);
            lifetime.Stop(ex => throw ex); Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!); TestEnvironment.ReleaseSaves(path);
        }
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(); readonly TcpClient reader;
        readonly Task drain;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public Wire()
        {
            listener.Start(); var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            drain = Task.Run(async () =>
            {
                try { var buffer = new byte[65536]; while (await reader.GetStream().ReadAsync(buffer) > 0) { } }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            });
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); }
    }
}
