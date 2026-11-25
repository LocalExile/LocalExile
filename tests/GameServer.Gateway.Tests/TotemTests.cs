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
    static async Task ShockwaveTotemSpellHitGrantsArcaneSurge()
    {
        var mod = Program.Items.Mods.ById("AnimalCharmArcaneSurgeOnHit")!;
        var jewel = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("That Which Was Taken", new Random(17)));
        var plain = await Hit(jewel with { Mods = [] });
        var active = await Hit(jewel with { Mods = [new(ModSection.Explicit, mod.Hash, [1])] });
        Console.WriteLine($"Shockwave Totem spell hit: damage {plain.Damage} / {active.Damage}, Arcane Surge {plain.Surge} -> {active.Surge}");
        Assert.False(plain.Surge);
        Assert.True(active.Surge);

        async Task<(uint Damage, bool Surge)> Hit(Item equipped)
        {
            var clock = new TotemHitClock();
            using var fixture = new TriggeredSummonTests.Fixture(clock);
            var player = fixture.Player;
            player.ProjectileRuntimeState = fixture.Session;
            player.Walk.Teleport(100, 100, clock.Now(player));
            const byte slot = 1;
            player.Character.Passives.Add(Game.PassivesFor(player).JewelNodeForSlot(slot)!.Value);
            player.Character.Passives.Add(31703);
            Game.ItemsOf(player).Add(PlayerInventories.PassiveJewels, slot, 0, equipped);
            fixture.Equip(("Shockwave Totem", 18), ("Ice Nova", 18));
            ushort skill = Game.GemSkillEffects(player).Single(g => g.Name == "Shockwave Totem").Id;
            var previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            try
            {
                uint mana = player.PlayerMana;
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (108, 100)));
                Assert.True(player.PlayerMana < mana);
                var totem = Assert.Single(player.Totems.All);
                Game.TickTotemUses(player);
                Assert.False(player.SupportGems.ArcaneSurgeActiveAt(clock.Now(player)));
                const uint id = 901;
                var target = new SpawnedMonster("Shockwave Totem target", new(112, 100, 0), 0, 0, 100000) { ObjectId = id };
                player.Area.Monsters[id] = target;
                player.Area.Grid.Set(new(id), (112, 100));
                Game.TickTotemUses(player);
                var strike = Assert.IsType<TotemState.Strike>(totem.Strike);
                Assert.True(strike.NextCast > clock.Now(player));
                clock.At = strike.NextCast;
                var session = Game.ProjectilesFor(player);
                using (Game.ProjectileMutation(player, advance: false))
                    session.AdvanceTo(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(clock.At)));
                Assert.InRange(target.Life, 1u, target.MaxLife - 1);
                return (target.MaxLife - target.Life, player.SupportGems.ArcaneSurgeActiveAt(clock.At));
            }
            finally { Game.AreaUpdateContext.Value = previous; }
        }
    }

    sealed class TotemHitClock : IPlayerUpdateSchedule
    {
        public double At = 2;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }

    // The GrantedEffects rows the data has behind the totem skills that were broken (their ActiveSkills ids:
    // shockwave_totem, holy_flame_totem, ancestor_totem_slash, totem_fire_spray, searing_bond). SkillTotemId cannot
    // name them: every ActiveSkills row carries one.
    static readonly string[] TotemEffects =
        ["ShockwaveTotem", "HolyFlameTotem", "AncestorTotemSlash", "SearingBond", "WbFlameTotem"];

    [Fact]
    public async Task Holy_flame_placement_does_not_start_a_paid_player_channel()
    {
        using var f = new AnimatedAdmissionTests.Fixture();
        var conn = f.Player;
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
            Assert.True(await Game.CastGemSkill(conn, source.Id,
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
        public double Now(Player conn) => 0;
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
        var player = new Player(new(1, "totem-catalog", 1000, 1000, 100, 100, null, []), 1, 1);
        int rejuvenation = skills.EffectRow("RejuvenationTotem");
        Assert.True(rejuvenation >= 0);
        // The old gate's only key: TotemAuras held RejuvenationTotem and nothing else.
        Assert.DoesNotContain(rejuvenation, TotemEffects.Select(skills.EffectRow));
        foreach (string effectId in TotemEffects)
        {
            int row = skills.EffectRow(effectId);
            Assert.True(row >= 0, $"missing GrantedEffects row: {effectId}");
            Assert.True(skills.HasType(row, Game.TotemSkillType), $"{effectId} (row {row}) is not {Game.TotemSkillType}");
            var variety = Game.TotemVariety(player, row);
            Assert.True(variety is not null, $"{effectId} (row {row}) summons no totem variety");
            Assert.True(variety!.Supported, $"{effectId} variety {variety.Id} ({variety.Row}) is not supported");
            Assert.NotNull(monsters.Find(variety.Row.ToString()));
        }
        // Rejuvenation Totem keeps working, so the fix is a widening of the gate rather than a replacement.
        Assert.NotNull(Game.TotemVariety(player, rejuvenation));
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
        var player = new Player(new(1, "totem-runtime", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "TotemRuntime", Experience = CharacterStats.ExperienceFor(100) },
            Connection = { Session = new("totem-test", wire.Stream, wire.Ciphers), PlayerSent = true },
            Instance = new(),
            PlayerLife = 10000,
            PlayerMana = 10000,
            Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("totem-test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        try
        {
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemShockwaveTotem")!);
            var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
            { Sockets = [new(SocketColour.White, gem)] };
            Game.ItemsOf(player).Add(PlayerInventories.Weapon1, 0, 0, weapon);
            var source = Game.GemSkillEffects(player).Single(g => g.Name == "Shockwave Totem");
            var variety = Game.TotemVariety(player, source.Effect);
            Assert.NotNull(variety);
            uint? placed = await Game.CreateTotem(player, source.Id, source.Effect,
                source.Level, variety!, (50, 50));
            Assert.NotNull(placed);
            var totem = player.Totems.All.Single();
            // The hybrid boundary: this is a dedicated totem skill, not an adopted projectile spell, so it keeps the
            // local contact path (a Strike) and is never ticked by the spell executor (Effect stays -1). The lab reads
            // the effect row it was summoned from through EffectRow, which is the deliberately different fact.
            Assert.True(totem.Effect < 0, "a dedicated totem skill must not be an adopted projectile spell totem");
            Assert.Equal(source.Effect, totem.EffectRow);
            // Shockwave Totem is a nova (ActiveSkillType Nova, radius 24): it casts around itself, not at a target.
            Assert.NotNull(totem.Strike);

            // A hostile outside its totem range (base_totem_range 80) starts no cast.
            player.Area.Monsters[0x08000002] = new SpawnedMonster("far", new(50, 140, 0), 100, 8, 1000);
            Game.TickTotemUses(player);
            Assert.Equal(0, totem.Strike!.NextCast);

            // One inside the range starts it; at the contact only what stands within the radius of the totem is hit.
            var target = new SpawnedMonster("near", new(55, 50, 0), 100, 8, 100000);
            var outside = new SpawnedMonster("outside", new(50, 90, 0), 100, 8, 100000);
            player.Area.Monsters[0x08000001] = target; player.Area.Monsters[0x08000003] = outside;
            uint before = target.Life, outsideBefore = outside.Life;
            Game.TickTotemUses(player);
            Assert.True(totem.Strike.NextCast > 0);

            // The contact is on the projectile runtime (a kill takes the XP gate before the area's frame): advance it to
            // the end of the cast instead of waiting on real time.
            var session = Game.ProjectilesFor(player);
            using (Game.ProjectileMutation(player, advance: false))
                session.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(totem.Strike.NextCast));
            Assert.True(target.Life < before, $"the nova dealt no damage: {target.Life} of {target.MaxLife}");
            Assert.Equal(outsideBefore, outside.Life);

            // /totems reads the same collection through the same call the command makes, so the listing carries the
            // fields the task needs — a totem the lab can see at all. (The Monsters array of /state cannot list one:
            // a totem never enters Player.Monsters.)
            string listing = Game.TotemsListing(player);
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
            player.Closed = true; PlayerInventories.Forget(player.Character.Name!);
            lifetime.Stop(ex => throw ex); Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!); TestEnvironment.ReleaseSaves(path);
        }

        await ShockwaveTotemSpellHitGrantsArcaneSurge();
    }

    [Fact]
    public async Task Decoy_totem_is_a_monster_target_until_it_dies()
    {
        using var f = new AnimatedAdmissionTests.Fixture();
        var conn = f.Player;
        var gem = ItemCatalog.Create(Program.Items.Find("Decoy Totem")!);
        var host = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!, [new(SocketColour.White, gem)], [1]);
        var items = Game.ItemsOf(conn);
        items.Remove(PlayerInventories.Weapon1, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id);
        items.Put(PlayerInventories.Weapon1, new(1, 0, 0, host));
        var source = Game.GemSkillEffects(conn).Single(g => g.Name == "Decoy Totem");
        var variety = Game.TotemVariety(conn, source.Effect);
        Assert.NotNull(variety);
        Assert.True(Game.TotemPathAvailable(conn, source.Id, source.Effect));
        Assert.NotNull(await Game.CreateTotem(conn, source.Id, source.Effect,
            source.Level, variety, (110, 100)));
        var totem = Assert.Single(conn.Totems.All);
        Assert.Equal(60, totem.DecoyRadius);
        Assert.NotEqual(0, totem.GrantedId);

        var attack = new MonsterAttack(0x3188, 1, 8, DamagePacket.Of(DamageType.Physical, 100, 100));
        SpawnedMonster Enemy(int x) => new("enemy", new(x, 100, 8), 100, 2, 100) { Attack = attack, Target = conn };
        var near = Enemy(116);
        var far = Enemy(171);
        // Two out of reach on the same side: each gets its own cell around the totem, not one on the line to it.
        var packA = Enemy(150);
        var packB = new SpawnedMonster("enemy", new(150, 102, 8), 100, 2, 100) { Attack = attack, Target = conn };
        conn.Area.Monsters[0x3100] = near;
        conn.Area.Monsters[0x3101] = far;
        conn.Area.Monsters[0x3102] = packA;
        conn.Area.Monsters[0x3103] = packB;
        Game.TickTotemUses(conn);
        Assert.NotNull(totem.TauntAt);
        double now = totem.TauntAt.Value;
        Game.ApplyDecoyTauntLocked(conn, totem, now);
        Assert.Same(totem, Game.DecoyTarget(near, now)?.Source);
        Assert.Null(Game.DecoyTarget(far, now));
        var plan = Game.PlanMonsterAi(conn, new(), now);
        var swing = Assert.Single(plan.DecoySwings);
        Assert.Same(totem, swing.Totem);
        Assert.Same(near, swing.Monster);
        var chases = plan.Moves.Where(m => m.Monster == packA || m.Monster == packB).Select(m => m.To).ToArray();
        Assert.Equal(2, chases.Length);
        Assert.NotEqual(chases[0], chases[1]);

        uint life = conn.PlayerLife;
        Assert.True(Game.DecoyTakeHitLocked(conn, totem, near, attack, now, 100));
        Assert.Equal(0u, totem.Life);
        Assert.Equal(life, conn.PlayerLife);
        Assert.Null(Game.DecoyTarget(near, now));
    }

    [Fact]
    public async Task A_placed_totem_is_listed_in_the_owners_minion_roster_until_it_dies()
    {
        using var f = new AnimatedAdmissionTests.Fixture();
        using var wire = new CaptureWire();
        var conn = f.Player;
        conn.Connection.Session = new("totem-roster", wire.Stream, wire.ServerCiphers);
        var gem = ItemCatalog.Create(Program.Items.Find("Decoy Totem")!);
        var host = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!, [new(SocketColour.White, gem)], [1]);
        var items = Game.ItemsOf(conn);
        items.Remove(PlayerInventories.Weapon1, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id);
        items.Put(PlayerInventories.Weapon1, new(1, 0, 0, host));
        var source = Game.GemSkillEffects(conn).Single(g => g.Name == "Decoy Totem");
        var variety = Game.TotemVariety(conn, source.Effect);
        Assert.NotNull(variety);
        uint id = Assert.IsType<uint>(await Game.CreateTotem(conn, source.Id, source.Effect, source.Level, variety, (110, 100)));
        // A second one past the limit replaces the first: its removal rides in the batch that lists the successor.
        uint next = Assert.IsType<uint>(await Game.CreateTotem(conn, source.Id, source.Effect, source.Level, variety, (130, 100)));
        Assert.NotNull(conn.Totems.All.Single(t => t.Id == id).DiedAt);
        byte[] Roster(uint who, ushort skill) =>
            InstanceEntities.PlayerMinionsChange(conn.Id, [new(who, skill, 5)], Game.CanDetonateMines(conn));
        bool Has(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

        // Source 0 is the same entry taking it off the roster; it is written once, with the death.
        conn.Totems.KillAll(Game.AreaTime(conn));
        byte[]? death = Game.TotemPresentation(conn);
        Assert.NotNull(death);
        Assert.True(Has(death, Roster(next, 0)), "the death carries no roster removal");
        Assert.False(Has(death, Roster(id, 0)), "the replaced totem's removal was sent again");
        byte[]? again = Game.TotemPresentation(conn);
        Assert.False(again is not null && Has(again, Roster(next, 0)), "the removal was sent every frame");

        byte[] sent = wire.FinishAndDecrypt();
        Assert.True(Has(sent, Roster(id, source.Id)) && Has(sent, Roster(next, source.Id)), "a placement never listed its totem");
        Assert.True(Has(sent, Roster(id, 0)), "the replaced totem stayed listed");
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
