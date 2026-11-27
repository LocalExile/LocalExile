using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ProjectileReleaseRegressionTests
{
    [Fact]
    public async Task Gathering_lightning_is_granted_after_a_paid_melee_hit()
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = Prepare(fixture, clock.At, "Gnarled Branch");
        player.Pools = player.Pools with { Accuracy = 10_000 };
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (!ReferenceEquals(owner, player)) return values;
            values[Stat.GlobalCannotCrit] = 1;
            values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 1_000;
            return values;
        });
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            ushort skill = Assert.Single(fixture.Equip(("Static Strike of Gathering Lightning", 1)));
            var primary = Target(941, 108);
            var firstBeam = Target(942, 137);
            var chain = Target(943, 180);
            Assert.Equal(0, WireChains());
            await Swing(94, 0);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 2687));
            Assert.Equal(primary.MaxLife, primary.Life);

            await Swing(108, 941);
            uint firstDamage = primary.MaxLife - primary.Life;
            uint firstBeamDamage = firstBeam.MaxLife - firstBeam.Life;
            Assert.True(firstDamage > 0);
            Assert.True(player.Area.Buffs.HasRow(player.Id, 2687));
            Assert.True(firstBeamDamage > 0);
            Assert.Equal(chain.MaxLife, chain.Life);
            CheckStacks(1);

            uint primaryBefore = primary.Life, beamBefore = firstBeam.Life;
            await Swing(108, 941);
            Assert.InRange((double)(primaryBefore - primary.Life) / firstDamage, 1.06, 1.10);
            Assert.InRange((double)(beamBefore - firstBeam.Life) / firstBeamDamage, 1.06, 1.10);
            Assert.True(chain.Life < chain.MaxLife);
            CheckStacks(2);

            uint chainBefore = chain.Life;
            clock.At += 4.1;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.False(player.Area.Buffs.HasRow(player.Id, 2687));
            Assert.Equal(0, WireChains());
            await Swing(108, 941);
            Assert.Equal(chainBefore, chain.Life);
            CheckStacks(1);

            async Task Swing(int x, uint id)
            {
                uint mana = player.PlayerMana;
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (x, 100), id));
                clock.At += 1;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Assert.True(player.PlayerMana < mana);
            }

            SpawnedMonster Target(uint id, int x)
            {
                var target = new SpawnedMonster("Static Strike target", new(x, 100, 0), 0, 0, 100_000)
                    { ObjectId = id, NoRewards = true };
                player.Area.Monsters.Add(id, target);
                player.Area.Grid.Set(new(id), (x, 100));
                return target;
            }

            void CheckStacks(ushort count)
            {
                ushort instance = player.Area.Buffs.Next;
                player.Area.Buffs.Publish();
                var buff = player.Area.Buffs.Find(player.Id, instance);
                Assert.NotNull(buff);
                Assert.Equal((ushort)2687, buff.Row);
                Assert.Equal(count, buff.Stacks);
                Assert.Equal(count, WireChains());
            }

            int WireChains() => Game.PlayerSkills(player).Single(row => row.Id == skill).Stats!
                .FirstOrDefault(stat => stat.Key == Stat.VirtualNumberOfChainsForBeams).Value;
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Gathering_lightning_uses_the_quality_cap_and_restarts_after_its_buff_is_cleared()
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = Prepare(fixture, clock.At, "Gnarled Branch");
        player.Pools = player.Pools with { Accuracy = 10_000 };
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            ushort skill = Assert.Single(fixture.Equip(("Static Strike of Gathering Lightning", 1)));
            var items = Game.ItemsOf(player);
            var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
            items.Change(PlayerInventories.Weapon1, host.Id, item => item with
            {
                Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with { Quality = 20 } }],
            });
            Assert.Equal(12, Game.SupportedSkill(player, skill).Stats[Stat.StaticStrikeMaximumGatheringLightningStacks]);
            var target = new SpawnedMonster("Static Strike target", new(108, 100, 0), 0, 0, 100_000)
                { ObjectId = 951, NoRewards = true };
            player.Area.Monsters.Add(951, target);
            player.Area.Grid.Set(new(951), (108, 100));
            for (int count = 1; count <= 13; count++)
                await SwingAndCheck((ushort)Math.Min(count, 12));

            player.Area.Buffs.ClearAll(player.Id);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 2687));
            await SwingAndCheck(1);

            async Task SwingAndCheck(ushort count)
            {
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (108, 100), 951));
                clock.At += 1;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                ushort instance = player.Area.Buffs.Next;
                player.Area.Buffs.Publish();
                Assert.Equal(count, player.Area.Buffs.Find(player.Id, instance)?.Stacks);
            }
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Arcing_strike_applies_a_melee_hit_and_makes_its_bolt_unavoidable()
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = Prepare(fixture, clock.At, "Gnarled Branch");
        using var stats = ArcingDamage(player);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            ushort skill = Assert.Single(fixture.Equip(("Lightning Strike of Arcing", 1)));
            var primary = AddArcingTarget(player, 921, 109, 100);
            var distant = AddArcingTarget(player, 922, 130, 100);
            uint mana = player.PlayerMana;
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (109, 100), 921));
            while (fixture.Session.Runtime.Projectiles.Count == 0 && clock.At < 3)
            {
                clock.At += .01;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            }

            Assert.True(player.PlayerMana < mana);
            var bolt = Assert.Single(fixture.Session.Runtime.Projectiles);
            var shot = Assert.IsType<ProjectileOffense>(bolt.Use.Payload);
            Assert.Equal((uint)921, shot.ExcludedTarget?.Id);
            Assert.True(shot.AccuracyProfile?.CannotBeEvaded);
            Assert.Equal(4, bolt.Profile.Chain);
            uint melee = primary.MaxLife - primary.Life;
            Assert.True(melee > 0);

            clock.At += 2;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.Equal(melee, primary.MaxLife - primary.Life);
            Assert.Equal(-1, distant.AttackEvasion.Value);
            Assert.InRange((double)(distant.MaxLife - distant.Life) / melee, .49, .51);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Arcing_bolt_gains_damage_for_each_chain_it_has_already_made()
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = Prepare(fixture, clock.At, "Gnarled Branch");
        using var stats = ArcingDamage(player);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            ushort skill = Assert.Single(fixture.Equip(("Lightning Strike of Arcing", 1)));
            var targets = Enumerable.Range(0, 6)
                .Select(i => AddArcingTarget(player, (uint)(931 + i), 140, 100 + 15 * i)).ToArray();
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (170, 100)));
            clock.At += 3;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));

            var damage = targets.Select(t => t.MaxLife - t.Life).ToArray();
            Assert.True(damage[0] > 0);
            for (int i = 1; i <= 4; ++i)
                Assert.InRange((double)damage[i] / damage[0], .99 + .1 * i, 1.01 + .1 * i);
            Assert.Equal(0u, damage[5]);
            Assert.All(targets.Take(5), t => Assert.NotEqual(-1, t.AttackEvasion.Value));
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    static IDisposable ArcingDamage(Player player)
    {
        player.Pools = player.Pools with { Accuracy = 10_000 };
        return GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (!ReferenceEquals(owner, player)) return values;
            values[Stat.GlobalCannotCrit] = 1;
            values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 10_000;
            return values;
        });
    }

    static SpawnedMonster AddArcingTarget(Player player, uint id, int x, int y)
    {
        var target = new SpawnedMonster("Arcing target", new(x, y, 0), 0, 0, 100_000)
            { ObjectId = id, NoRewards = true };
        player.Area.Monsters.Add(id, target);
        player.Area.Grid.Set(new(id), (x, y));
        return target;
    }

    [Fact]
    public async Task Artillery_ballista_shapes_resolve_after_paid_placement()
    {
        foreach (var (name, crossEnchant, hitsLateralEnemy) in new[]
        {
            ("Artillery Ballista", false, false),
            ("Artillery Ballista", true, true),
            ("Artillery Ballista of Cross Strafe", false, true),
            ("Artillery Ballista of Focus Fire", false, true),
        })
        {
            var clock = new Clock();
            using var fixture = new TriggeredSummonTests.Fixture(clock);
            var player = Prepare(fixture, clock.At, "Crude Bow");
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (!ReferenceEquals(owner, player)) return values;
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 500;
                if (crossEnchant) values[Stat.ArtilleryBallistaCrossStrafePattern] = 1;
                return values;
            });
            var previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            try
            {
                ushort skill = Assert.Single(fixture.Equip((name, 1)));
                var centre = Target(811, 130, 100);
                var side = Target(812, 130, 119);
                var distant = Target(813, 250, 100);
                uint mana = player.PlayerMana;
                var placed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Placed() => placed.TrySetResult();
                player.Totems.Changed += Placed;
                try
                {
                    Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (110, 100)));
                    await placed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { player.Totems.Changed -= Placed; }

                uint afterPlacement = player.PlayerMana;
                Assert.True(afterPlacement < mana);
                var totem = Assert.Single(player.Totems.Living(clock.At));
                Assert.True(totem.Ballista);
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Game.TickSpellTotemsLocked(player, []);
                Assert.True(totem.NextCast > clock.At);
                clock.At = totem.NextCast + .2;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Assert.Equal(centre.MaxLife, centre.Life);
                clock.At = totem.NextCast + 1;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Assert.True(centre.Life < centre.MaxLife);
                Assert.Equal(hitsLateralEnemy, side.Life < side.MaxLife);
                Assert.Equal(distant.MaxLife, distant.Life);
                Assert.Equal(afterPlacement, player.PlayerMana);

                SpawnedMonster Target(uint id, int x, int y)
                {
                    var target = new SpawnedMonster("ballista target", new(x, y, 0), 0, 0, 100_000)
                        { ObjectId = id, Hostile = true, NoRewards = true };
                    player.Area.Monsters.Add(id, target);
                    player.Area.Grid.Set(new(id), (x, y));
                    return target;
                }
            }
            finally { Game.AreaUpdateContext.Value = previous; }
        }
    }

    [Fact]
    public void Split_arrow_variant_splits_at_terrain_and_at_range_end()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        Prepare(fixture, 1, "Crude Bow");
        foreach (var (name, variant) in new[] { ("Split Arrow of Splitting", true), ("Split Arrow", false) })
        {
            ushort skill = Assert.Single(fixture.Equip((name, 17)));
            var gem = Assert.Single(Game.GemSkillEffects(fixture.Player), gem => gem.Id == skill);
            Assert.True(Game.ProjectilePathAvailable(fixture.Player, skill, gem.Effect));
            var use = Game.CaptureGemProjectile(
                fixture.Player, fixture.Session, skill, gem, (195, 100), new SkillCriticalRoll(0));
            use = use with { Profile = use.Profile with { Count = 1, Range = 75 } };
            foreach (bool wall in new[] { true, false })
            {
                var arena = new Arena(wall);
                var runtime = new ProjectileRuntime(arena);
                runtime.Release(use);
                runtime.AdvanceTo(ProjectileRuntime.Time(2));

                if (variant) Assert.True(use.Profile.Split > 0);
                Assert.Equal(variant ? use.Profile.Split : 0, arena.Children.Count);
                foreach (var child in arena.Children)
                {
                    double expectedX = wall ? 124.5 : use.Origin.X + use.Profile.Range;
                    Assert.InRange(Math.Abs(expectedX - child.Position.X), 0, .1);
                    if (wall) Assert.True(child.Projectile.Direction.X <= 0);
                }
            }
        }

        // Locus Mine respects modifiers_to_projectile_count_do_not_apply, scales child volleys, and Arrow Nova children inherit split flags.
        var locked = GatewayProjectileSkillProfiles.Resolve("IceShotAltX", "Metadata/Projectiles/DefaultArrow", s => s switch
        {
            Stat.BaseNumberOfArrows => 5,
            Stat.ModifiersToProjectileCountDoNotApply => 1,
            Stat.NumberOfProjectilesPlusPctFinalFromLocusMineSupport => -30,
            _ => 0
        }, 100, attack: true, arrows: true);
        Assert.Equal(5, locked.Count);
        var cloudburstMine = GatewayProjectileSkillProfiles.Resolve("TornadoShotAltX", "Metadata/Projectiles/TornadoShotArrow", s => s switch
        {
            Stat.BaseNumberOfArrows => 9,
            Stat.ProjectilesRain => 1,
            Stat.NumberOfProjectilesPlusPctFinalFromLocusMineSupport => -30,
            Stat.ProjectilesCanSplitFromTerrain => 1,
            Stat.ProjectilesCanSplitAtEndOfRange => 1,
            _ => 0
        }, 100, attack: true, arrows: true);
        Assert.Equal(6, cloudburstMine.EndEmission!.Child.Count);
        Assert.True(cloudburstMine.EndEmission.Child.SplitOnTerrain);
        Assert.True(cloudburstMine.EndEmission.Child.SplitAtEndOfRange);
    }

    [Fact]
    public async Task Returning_projectiles_emit_orientation_0x01b9_only_when_direction_changes()
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = Prepare(fixture, clock.At, "Gnarled Branch");
        var session = Game.ProjectilesFor(player);
        var packets = new List<byte[]>();
        var outgoing = Program.Outgoing;
        Program.Outgoing = (p, bytes) => { if (ReferenceEquals(p, player)) lock (packets) packets.Add(bytes.ToArray()); };
        try
        {
            async Task Flush()
            {
                var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                session.Enqueue(() => { drained.TrySetResult(); return Task.CompletedTask; });
                await drained.Task;
            }

            // Same-direction lobbed arc segments emit zero 0x01b9 orientation updates.
            var lobbed = new ProjectileProfile("Metadata/Projectiles/FrostBolt")
                { Speed = 40, Radius = 2, Range = 80, Lobbed = true, ArcHeight = 20 };
            session.Runtime.Release(new ProjectileUse(lobbed, new(player.Id), new(player.Id), 1, new(100, 100), new(180, 100)));
            await Flush();
            for (int step = 0; step < 5; step++)
            {
                clock.At += 0.25;
                session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                await Flush();
            }
            static float[] OrientAngles(List<byte[]> sent, uint objectId)
            {
                var found = new List<float>();
                Span<byte> prefix = [0x01, 0xb9, (byte)(objectId >> 24), (byte)(objectId >> 16), (byte)(objectId >> 8), (byte)objectId];
                foreach (var pkt in sent)
                    for (int idx = 0; idx + 17 <= pkt.Length; idx++)
                        if (pkt.AsSpan(idx, 6).SequenceEqual(prefix))
                            found.Add(BitConverter.Int32BitsToSingle(
                                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(pkt.AsSpan(idx + 12, 4))));
                return [.. found];
            }

            lock (packets)
            {
                uint lobbedId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                    packets.First(b => b.Length >= 6 && b[0] == 0x02 && b[1] == 0xbb).AsSpan(2, 4));
                Assert.Empty(OrientAngles(packets, lobbedId));
            }

            // Returning projectile reverses (+X -> -X) at end of range and emits 0x01b9 facing -X.
            lock (packets) packets.Clear();
            var returning = new ProjectileProfile("Metadata/Projectiles/FrostBolt")
                { Speed = 40, Radius = 2, Range = 20, Returns = true };
            session.Runtime.Release(new ProjectileUse(returning, new(player.Id), new(player.Id), 1, new(100, 100), new(120, 100))
                { ReturnPoint = new(100, 100) });
            await Flush();
            for (int step = 0; step < 6; step++)
            {
                clock.At += 0.2;
                session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                await Flush();
            }
            lock (packets)
            {
                uint returnId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                    packets.First(b => b.Length >= 6 && b[0] == 0x02 && b[1] == 0xbb).AsSpan(2, 4));
                float angle = Assert.Single(OrientAngles(packets, returnId));
                Assert.InRange(MathF.Abs(angle - MathF.Atan2(-1, 0)), 0f, 1e-3f);
            }
        }
        finally { Program.Outgoing = outgoing; }
    }

    static Player Prepare(TriggeredSummonTests.Fixture fixture, double now, string weapon)
    {
        var player = fixture.Player;
        player.Use(new(1, "projectile-regression", 350, 350, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, now);
        player.Connection.LockstepClient = false;
        player.Connection.PlayerSent = player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;
        var items = Game.ItemsOf(player);
        items.Change(PlayerInventories.Weapon1, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id,
            _ => ItemCatalog.Create(Program.Items.Find(weapon)!, random: new(19)));
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(now));
        return player;
    }

    sealed class Arena(bool wall) : IProjectileWorld
    {
        public readonly List<ProjectileEvent> Children = [];
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => [];
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) =>
            wall ? ProjectileGeometry.TraceGrid(from, to, (x, _) => x == 125) : null;
        public ProjectileHitResult Hit(ProjectileDamage hit) => ProjectileHitResult.Hit;
        public void Publish(ProjectileEvent value)
        {
            if (value.Kind == ProjectileEventKind.Spawn && value.Projectile.ParentId != 0) Children.Add(value);
        }
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public async Task Wait(int milliseconds, CancellationToken stopping)
        {
            await Task.Yield();
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
        }
    }
}
