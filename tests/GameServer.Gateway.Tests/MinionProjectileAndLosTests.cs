using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MinionProjectileAndLosTests
{
    sealed class Clock : IPlayerUpdateSchedule
    {
        internal double Time;
        public double Now(Player player) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.CompletedTask;
    }

    [Fact]
    public async Task Golem_step_respects_line_of_sight_ignores_undamageable_and_unstucks_wedged_minions()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var clock = new Clock { Time = 1 };
        var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            f.Player.Use(new AreaLayout(1, "minion-los-test", 200, 200, 100, 100,
                (x, y) => x >= 10 && x < 190 && y >= 10 && y < 190, [])
            {
                Sight = (x, _) => x == 110 ? 0 : 1,
            });
            f.Player.Walk.Teleport(90, 100, 0);

            ushort spectreSource = f.Equip(("Raise Spectre", 20)).Single();
            var revenant = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Revenant/RevenantMapBossStandalone_AtlasUber"));
            uint spectreId = await Game.SummonFighter(f.Player, spectreSource,
                Game.SummonData.Find("RaiseSpectre")!, (100, 100), restoredVariety: revenant.Id);
            var golem = f.Player.Summons.Fighters[spectreId];
            var spellAbility = Assert.Single(golem.Abilities, a => a.Projectile is not null && !a.Projectile.Lobbed);
            Assert.True(spellAbility.Attack.Ranged);
            Assert.True(spellAbility.Attack.Range > 10);

            var enemyVariety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie"));
            // 0. An enemy with BaseCannotBeDamaged (encased essence / Divine Shrine) must be ignored by summon targeting.
            var undamageable = new SpawnedMonster("EncasedPreview", new(102, 100, 20), 60, 2, 5000)
            {
                Hostile = true,
                Variety = enemyVariety,
                Level = 70,
                Walk = f.Player.NewMonsterWalk(6000, 102, 100, 20, 2, false),
                EncounterStats = new Dictionary<uint, int> { [Stat.BaseCannotBeDamaged] = 1 },
            };
            f.Player.Area.Monsters[6000] = undamageable;
            Game.GolemStepLocked(f.Player, golem, 1.0);
            Assert.Null(golem.Pending);
            Assert.Equal(0u, golem.Target);
            f.Player.Area.Monsters.Remove(6000);
            golem.NextMove = 0;
            golem.Monster.LastTarget = null;

            var enemy = new SpawnedMonster("Target", new(120, 100, 20), 60, 2, 5000)
            {
                Hostile = true,
                Variety = enemyVariety,
                Level = 70,
                Walk = f.Player.NewMonsterWalk(6001, 120, 100, 20, 2, false),
            };
            f.Player.Area.Monsters[6001] = enemy;

            // 1. Wall at x=110 blocks LOS: ranged spectre must not reserve its projectile spell through the wall;
            //    instead it walks toward the target.
            Game.GolemStepLocked(f.Player, golem, 1.0);
            Assert.Null(golem.Pending);
            Assert.Equal((120, 100), golem.Monster.LastTarget);

            // 1b. A pit-anchored large boss whose centre is on blocked terrain (x >= 110) but whose hitbox edge
            //     (ObjectSize 15 -> radius 14, edge at x = 106) protrudes into clear arena cells must be attackable.
            f.Player.Use(new AreaLayout(1, "minion-pit-boss-test", 200, 200, 100, 100,
                (x, y) => x >= 10 && x < 190 && y >= 10 && y < 190, [])
            {
                Sight = (x, _) => x >= 110 ? 0 : 1,
            });
            f.Player.Area.Monsters.Remove(6001);
            var pitBoss = new SpawnedMonster("PitBoss", new(120, 100, 20), 60, 15, 5000)
            {
                Hostile = true,
                Variety = enemyVariety with { ObjectSize = 15 },
                Level = 70,
                Walk = f.Player.NewMonsterWalk(6002, 120, 100, 20, 14, false),
            };
            f.Player.Area.Monsters[6002] = pitBoss;
            golem.NextMove = 0;
            golem.Monster.Walk.Teleport(100, 100, 1.5);
            clock.Time = 1.5;
            Game.GolemStepLocked(f.Player, golem, 1.5);
            Assert.NotNull(golem.Pending);
            golem.Pending = null;
            golem.Monster.BusyUntil = 0;
            // At one cell outside the hitbox, a wall in that last cell still blocks the shot.
            f.Player.Use(f.Player.Layout with { Sight = (x, _) => x == 106 ? 0 : 1 });
            golem.Monster.Walk.Teleport(105, 100, 1.6);
            golem.NextMove = 0;
            clock.Time = 1.6;
            Game.GolemStepLocked(f.Player, golem, 1.6);
            Assert.Null(golem.Pending);
            f.Player.Area.Monsters.Remove(6002);
            f.Player.Area.Monsters[6001] = enemy;

            // 2. Clear LOS, but all abilities are on cooldown while distance (20) < range - 2:
            //    pursuit must clamp f <= 1.0 and never push the minion backward past its current position (x < 100).
            f.Player.Use(new AreaLayout(1, "minion-clear-test", 200, 200, 100, 100,
                (x, y) => x >= 10 && x < 190 && y >= 10 && y < 190, [])
            {
                Sight = (_, _) => 1,
            });
            f.Player.Walk.Teleport(90, 100, 2.0);
            foreach (var a in golem.Abilities)
                golem.Charges[a.Effect] = new Queue<double>(Enumerable.Repeat(20.0, a.Uses));
            golem.NextMove = 0;
            golem.Monster.Walk.Teleport(100, 100, 2.0);
            golem.Monster.LastTarget = null;
            clock.Time = 2.0;
            Game.GolemStepLocked(f.Player, golem, 2.0);
            Assert.Null(golem.Pending);
            Assert.NotNull(golem.Monster.LastTarget);
            Assert.InRange(golem.Monster.LastTarget!.Value.X, 100, 120);

            // Waiting at the desired firing distance for a cooldown is not a failed move order.
            golem.Abilities = [spellAbility];
            f.Player.Walk.Teleport(70, 100, 3);
            golem.Monster.Walk.Teleport(100, 100, 3);
            for (int tick = 0; tick < 12; tick++)
            {
                clock.Time = 3 + tick * .2;
                Game.GolemStepLocked(f.Player, golem, clock.Time);
            }
            Assert.Equal((100, 100), golem.Monster.Walk.Position(clock.Time));

            // A minion stranded on a separate island does need to recover to its owner.
            f.Player.Area.Monsters.Remove(6001);
            f.Player.Use(new AreaLayout(1, "minion-islands", 200, 200, 70, 100,
                (x, y) => y >= 80 && y <= 120 && (x >= 55 && x <= 80 || x >= 95 && x <= 105), []));
            f.Player.Walk.Teleport(70, 100, 6);
            golem.Monster.Walk = f.Player.NewMonsterWalk(golem.Id, 100, 100, 20, 2, false);
            for (int tick = 0; tick < 16; tick++)
            {
                clock.Time = 6 + tick * .2;
                Game.GolemStepLocked(f.Player, golem, clock.Time);
            }
            Assert.InRange(golem.Monster.Walk.Position(clock.Time).X, 55, 80);
        }
        finally
        {
            Game.AreaUpdateContext.Value = outer;
        }
    }

    [Fact]
    public async Task Returning_projectiles_support_grants_return_to_defiler_spectre()
    {
        using var f = new TriggeredSummonTests.Fixture();
        ushort spectreSource = f.Equip(("Raise Spectre", 20), ("Returning Projectiles Support", 20)).First();
        var revenant = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Revenant/RevenantMapBossStandalone_AtlasUber"));
        var area = f.Player.Area;
        var previous = GameArea.Own(area);
        Task<uint> summon;
        try
        {
            summon = Game.SummonFighter(f.Player, spectreSource, Game.SummonData.Find("RaiseSpectre")!,
                (100, 100), restoredVariety: revenant.Id);
        }
        finally { GameArea.Own(previous); }
        var golem = f.Player.Summons.Fighters[await summon];
        var registration = GameServer.Core.Protocol.Instance.InstanceEntities.MonsterTypes([((ushort)revenant.Row, golem.Monster.Level)]);
        Assert.Contains(area.Delta.Take(), record => record.Kind == AreaDelta.Kind.Message
            && record.Only is null && record.Bytes.SequenceEqual(registration));
        var spell = Assert.Single(golem.Abilities, a => a.Id == "RevenantBossSpellProjectile");
        var mods = golem.Monster.Mods.Effects;
        var profile = GatewayProjectileModifiers.Resolve(
            GatewayProjectileDefaults.Create(spell.Projectile!.Metadata, k => mods[k], spell.Projectile.Speed),
            k => mods[k], attack: false);
        Assert.True(profile.Returns);
        Assert.True(profile.PierceAllOnReturn);
    }

    [Fact]
    public async Task Minion_projectile_crosses_uphill_and_downhill_terrain_steps()
    {
        var clock = new Clock { Time = 1 };
        using var f = new TriggeredSummonTests.Fixture(clock);
        var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        string areaId = $"minion-cliff-{Guid.NewGuid():N}";
        using var walkability = new CliffWalkability(areaId, f.Player.Seed, 200, 200, (x, _) => (short)(x >= 110 ? 30 : 0));
        try
        {
            f.Player.Use(new AreaLayout(1, areaId, 200, 200, 100, 100,
                (x, y) => x >= 10 && x < 190 && y >= 10 && y < 190, [])
            {
                Sight = (_, _) => 1,
            });
            f.Session.AdvanceTo(ProjectileRuntime.Time(clock.Time));
            var zombie = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie"));
            var uphillTarget = new SpawnedMonster("UphillTarget", new(120, 100, 20), 60, 2, 5000)
            {
                Hostile = true,
                Variety = zombie,
                Level = 70,
                Walk = f.Player.NewMonsterWalk(6101, 120, 100, 20, 2, false),
            };
            f.Player.Area.Monsters[6101] = uphillTarget;

            var hits = new List<uint>();
            var profile = new ProjectileProfile("Metadata/Projectiles/IceElementalProjectile")
                { Speed = 40, Radius = 2, Range = 30 };
            var owner = new ProjectileActorKey(7001);
            var offense = new ObjectProjectileOffense("minion-cliff-test", (_, contact) =>
            {
                hits.Add(contact.Target.Id);
                return ProjectileHitResult.Hit;
            }) { Quiet = true };

            // Uphill: height 0 (x=100) -> 30-step cliff at x=110 -> target at (120, 100).
            f.Session.Runtime.Release(new ProjectileUse(profile, owner, owner, 1, new(100, 100), new(120, 100)) { Payload = offense });
            clock.Time += 0.4;
            f.Session.AdvanceTo(ProjectileRuntime.Time(clock.Time));
            // Halfway across x=110 (at x=116) before reaching x=120: still in flight, not stopped by Terrain.
            Assert.Contains(f.Session.Runtime.Projectiles, p => p.Position.X > 110);
            clock.Time += 0.4;
            f.Session.AdvanceTo(ProjectileRuntime.Time(clock.Time));
            Assert.Equal([6101u], hits);

            // Downhill: height 30 (x=120) -> drop at x=110 -> target at (100, 100).
            f.Player.Area.Monsters.Remove(6101);
            f.Player.Area.Monsters[6102] = new SpawnedMonster("DownhillTarget", new(100, 100, 20), 60, 2, 5000)
            {
                Hostile = true,
                Variety = zombie,
                Level = 70,
                Walk = f.Player.NewMonsterWalk(6102, 100, 100, 20, 2, false),
            };
            f.Session.Runtime.Release(new ProjectileUse(profile, owner, owner, 1, new(120, 100), new(100, 100)) { Payload = offense });
            clock.Time += 0.8;
            f.Session.AdvanceTo(ProjectileRuntime.Time(clock.Time));
            Assert.Equal([6101u, 6102u], hits);
        }
        finally
        {
            Game.AreaUpdateContext.Value = outer;
        }
    }

    [Fact]
    public async Task Minion_projectile_glide_adjusts_end_height_for_terrain_elevation()
    {
        var clock = new Clock { Time = 1 };
        using var f = new TriggeredSummonTests.Fixture(clock);
        var player = f.Player;
        player.Connection.LockstepClient = false;
        player.Connection.PlayerSent = player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = f.Session;
        string areaId = $"minion-glide-{Guid.NewGuid():N}";
        using var walkability = new CliffWalkability(areaId, player.Seed, 200, 200, (x, _) => (short)(x >= 110 ? 30 : 0));
        var packets = new List<byte[]>();
        var outgoing = Program.Outgoing;
        Program.Outgoing = (p, bytes) => { if (ReferenceEquals(p, player)) lock (packets) packets.Add(bytes.ToArray()); };
        try
        {
            player.Use(new AreaLayout(1, areaId, 200, 200, 100, 100,
                (x, y) => x >= 10 && x < 190 && y >= 10 && y < 190, [])
            {
                Sight = (_, _) => 1,
            });
            f.Session.AdvanceTo(ProjectileRuntime.Time(clock.Time));

            async Task Flush()
            {
                var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Session.Enqueue(() => { drained.TrySetResult(); return Task.CompletedTask; });
                await drained.Task;
            }

            static (int X, int Y, int Marker) SingleMoveTo(List<byte[]> sent)
            {
                lock (sent)
                {
                    uint objectId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                        sent.First(b => b.Length >= 6 && b[0] == 0x02 && b[1] == 0xbb).AsSpan(2, 4));
                    Span<byte> prefix = [0x01, 0xba, (byte)(objectId >> 24), (byte)(objectId >> 16), (byte)(objectId >> 8), (byte)objectId];
                    foreach (var pkt in sent)
                        for (int idx = 0; idx + 29 <= pkt.Length; idx++)
                            if (pkt.AsSpan(idx, 6).SequenceEqual(prefix))
                                return (
                                    System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(pkt.AsSpan(idx + 12, 4)),
                                    System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(pkt.AsSpan(idx + 16, 4)),
                                    System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(pkt.AsSpan(idx + 20, 4)));
                }
                throw new InvalidOperationException("No 0x01ba MoveTo packet found.");
            }

            var profile = new ProjectileProfile("Metadata/Projectiles/IceElementalProjectile")
                { Speed = 40, Radius = 2, Range = 20 };
            var owner = new ProjectileActorKey(7002);
            var offense = new ObjectProjectileOffense("minion-glide-test", (_, _) => ProjectileHitResult.Miss)
            {
                Quiet = true,
                Presentation = new() { LaunchHeight = 9 },
            };

            // Uphill glide: start ground 0 -> end ground 30 (+9 launch height = 39).
            f.Session.Runtime.Release(new ProjectileUse(profile, owner, owner, 1, new(100, 100), new(120, 100)) { Payload = offense });
            await Flush();
            Assert.Equal((120, 100, 39), SingleMoveTo(packets));
            clock.Time += 1.0;
            f.Session.AdvanceTo(ProjectileRuntime.Time(clock.Time));
            await Flush();

            // Downhill glide: start ground 30 -> end ground 0 (+9 launch height = 9).
            lock (packets) packets.Clear();
            f.Session.Runtime.Release(new ProjectileUse(profile, owner, owner, 1, new(120, 100), new(100, 100)) { Payload = offense });
            await Flush();
            Assert.Equal((100, 100, 9), SingleMoveTo(packets));
            clock.Time += 1.0;
            f.Session.AdvanceTo(ProjectileRuntime.Time(clock.Time));
            await Flush();
        }
        finally
        {
            Program.Outgoing = outgoing;
        }
    }

    sealed class CliffWalkability : IDisposable
    {
        readonly string areaId;
        readonly uint seed;
        readonly string[] files;

        public CliffWalkability(string areaId, uint seed, int w, int h, Func<int, int, short> heightAt)
        {
            this.areaId = areaId;
            this.seed = seed;
            const string folder = "data/walkability";
            Directory.CreateDirectory(folder);
            string stem = Path.Combine(folder, $"{areaId}_seed{seed}");
            string meta = $"{{\"w\":{w},\"h\":{h}}}";
            var heights = new short[w * h];
            var projectile = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    heights[idx] = heightAt(x, y);
                    projectile[idx] = (byte)(x >= 10 && x < w - 10 && y >= 10 && y < h - 10 ? 1 : 0);
                }
            var heightBytes = new byte[heights.Length * 2];
            Buffer.BlockCopy(heights, 0, heightBytes, 0, heightBytes.Length);
            files = [stem + ".heights.bin", stem + ".heights.json", stem + ".projectile.bin", stem + ".json"];
            File.WriteAllBytes(files[0], heightBytes);
            File.WriteAllText(files[1], meta);
            File.WriteAllBytes(files[2], projectile);
            File.WriteAllText(files[3], meta);
        }

        public void Dispose()
        {
            TerrainHeights.Forget(areaId, seed);
            foreach (var file in files)
                if (File.Exists(file)) File.Delete(file);
        }
    }
}
