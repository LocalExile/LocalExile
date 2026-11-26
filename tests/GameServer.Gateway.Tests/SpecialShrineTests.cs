using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;
using GameServer.Modules.Shrines;

namespace GameServer.Gateway.Tests;

public sealed partial class ShrineTests
{
    [Fact]
    public async Task Special_shrine_rooms_and_buffs_use_their_own_rows_and_keep_instance_ownership()
    {
        ShrineRegistry.RegisterDefaults();
        var rooms = Enumerable.Range(1, 12).Select(i => new PlacedTileObject(ShrinePlacement.Type,
            ShrinePlacement.DefaultModel, i * 30, 100, 0, 0)).ToArray();
        var mods = new MapMods(new Dictionary<uint, int>
        {
            [Stat.MapNumExtraEvolvingShrines] = 2, [Stat.MapNumExtraApparitionShrines] = 4,
            [Stat.MapShrinesChanceToBeGreedShrinePct] = 100,
        });
        var seen = new HashSet<string>();
        for (uint seed = 0; seed < 64; seed++)
        {
            var plan = MapShrines.Plan(rooms, (_, _) => true, seed, mods);
            Assert.Equal(rooms.Length, plan.Count);
            Assert.Equal(2, plan.Count(p => p.Shrine.Id == "UpgradeOnKill"));
            Assert.Equal(4, plan.Count(p => ShrineCatalog.ApparitionIds.Contains(p.Shrine.Id)));
            Assert.Equal(6, plan.Count(p => p.Shrine.Id == "Greed"));
            Assert.Equal(plan, MapShrines.Plan(rooms, (_, _) => true, seed, mods));
            foreach (var p in plan) seen.Add(p.Shrine.Id);
        }
        Assert.All(ShrineCatalog.ApparitionIds, id => Assert.Contains(id, seen));
        Assert.Equal(ShrinePlacement.Plan(rooms, (_, _) => true, 7), MapShrines.Plan(rooms, (_, _) => true, 7, MapMods.None));
        Assert.Equal(rooms.Length, MapShrines.Plan(rooms, (_, _) => true, 7,
            new MapMods(new Dictionary<uint, int> { [Stat.MapNumExtraApparitionShrines] = int.MaxValue })).Count);

        await using var wire = await ShrineWire.Open(new InstanceState());
        var player = wire.Player;
        using var sources = GameEvents.PlayerStatSources.Add("shrine-regression", ShrineManager.AddStats);
        try
        {
            var evolving = new MapOpening(player, [], false, new(new(1, "test", "Test", 1), mods), "");
            ShrineMapAdmission.Check(evolving);
            Assert.NotNull(evolving.Opening);
            Assert.Contains("Shrines.Evolving", evolving.HandledContent);
            Assert.Contains("Shrines.Apparitions", evolving.HandledContent);
            foreach (string id in ShrineCatalog.ApparitionIds)
            {
                var row = ShrineCatalog.Find(id)!;
                Assert.Equal(45, row.Duration);
                Assert.NotEmpty(row.GuardPacks);
                Assert.NotNull(row.Monster);
                bool conqueror = id is "Warlord" or "Crusader" or "Redeemer" or "Hunter";
                Assert.Equal(conqueror, row.MonsterSummon.HasValue);
                Assert.Equal(conqueror, row.PlayerSummon.HasValue);
                var monster = new SpawnedMonster("shrine guard", new(112, 100, 10, (_, _) => true, 1000, 1000), 20, 5, 1000);
                player.Area.Monsters[0x7070] = monster;
                var shrine = new ShrineInstance { Definition = row, At = (112, 100), Before = ShrineRegistry.Before(row) };
                var ctx = new ShrineContext(player, 0);
                foreach (var effect in shrine.Before) await effect.OnNearbyMonsterEnter(ctx, shrine, 0x7070);
                foreach (var (key, value) in row.Monster!.Stats) Assert.Equal(value, monster.Effects[key]);
                foreach (var effect in shrine.Before) await effect.OnActivation(ctx, shrine);
                Assert.Empty(monster.TemporaryStats);
                await ShrineManager.SpawnAroundPlayer(player, [id]);
                var at = player.Walk.Position(0);
                uint objectId = uint.Parse(player.Instance.ContentState[$"Shrines/id/{at.X + 12}/{at.Y}"]);
                player.Walk.Teleport(at.X + 12, at.Y, 0);
                Assert.True(await ShrineManager.Use(player, new(objectId, 0, 1, 0), 0));
                Assert.True(ShrineManager.HasBuff(player, row.BuffRow));
                var stats = ShrineManager.AddStats(player, []);
                foreach (var (key, value) in row.Stats) Assert.Equal(value, stats.GetValueOrDefault(key));
                await ShrineManager.LeaveArea(player);
                Assert.False(ShrineManager.HasBuff(player, row.BuffRow));
                Assert.Empty(ShrineManager.AddStats(player, []));
            }
            ShrineRegistry.Register(new ShrineDefinition { Id = "BrineKing", Duration = 0 });
            var position = player.Walk.Position(0);
            await ShrineManager.SpawnAroundPlayer(player, ["BrineKing"]);
            uint expired = uint.Parse(player.Instance.ContentState[$"Shrines/id/{position.X + 12}/{position.Y}"]);
            player.Walk.Teleport(position.X + 12, position.Y, 0);
            await ShrineManager.Use(player, new(expired, 0, 2, 0), 0);
            Assert.False(ShrineManager.HasBuff(player, ShrineCatalog.Find("BrineKing")!.BuffRow));
            Assert.Empty(ShrineManager.AddStats(player, [])); // Before a cleanup tick too.
        }
        finally { await ShrineManager.LeaveArea(player); ShrineRegistry.RegisterDefaults(); }
    }

    [Fact]
    public async Task Shrine_bosses_keep_standalone_skills_and_scaling_and_influence_rewards_only_change_eligible_rares()
    {
        await using var wire = await ShrineWire.Open(new InstanceState());
        var player = wire.Player;
        ApparitionRewards.Load();
        var noBuff = new DropRoll(player, new("ordinary", "ordinary", 70, MonsterRarity.Normal, 100, 100));
        for (int i = 0; i < 100; i++) ApparitionRewards.Drops(noBuff);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) ApparitionRewards.Drops(noBuff);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        watch.Stop();
        Assert.Equal(0, allocated);
        Assert.Empty(noBuff.Changes);
        Console.WriteLine($"Apparition rewards inactive: {watch.Elapsed.TotalMilliseconds:0.###} ms / 10000 kills, {allocated} bytes");
        var modifiers = new MonsterModEffects(new Dictionary<uint, int>
        {
            [Stat.DamagePlusPct] = 30, [Stat.AttackSpeedPlusPct] = 20, [Stat.BaseCastSpeedPlusPct] = 20,
            [Stat.NumberOfAdditionalProjectiles] = 2, [Stat.BaseSkillAreaOfEffectPlusPct] = 44,
        });
        Assert.NotEmpty(MapScarabBosses.Bosses);
        foreach (var variety in MapScarabBosses.Bosses)
        {
            Assert.Contains("Standalone", variety.Id);
            Assert.DoesNotContain("AtlasUber", variety.Id);
            var plain = BossKits.Kit(player, variety, 70, unique: true);
            var scaled = BossKits.Kit(player, variety, 70, -1, null, true, modifiers, 1.15);
            Assert.True(plain.Count > 0 || Game.MavenNucleus.CombatEffect(Game.SkillsFor(player), variety) >= 0,
                $"{variety.Id} has neither a kit nor a default action");
            Assert.Equal(plain.Count, scaled.Count);
            Assert.Equal(scaled.Count, scaled.Select(s => s.Attack.SkillId).Distinct().Count());
            for (int i = 0; i < plain.Count; i++)
            {
                Assert.Contains(plain[i].Effect, variety.GrantedEffects!);
                Assert.Equal(plain[i].Effect, scaled[i].Effect);
                foreach (var type in DamageTypes.ConversionOrder)
                    Assert.Equal(modifiers.Hit(plain[i].Attack.Damage.Scale(1.15), 70)[type], scaled[i].Attack.Damage[type]);
                Assert.Equal(plain[i].Attack.Seconds / 1.2, scaled[i].Attack.Seconds, 8);
                Assert.Equal(plain[i].Attack.AreaRadius * 1.2, scaled[i].Attack.AreaRadius, 8);
                if (plain[i].Attack.Projectile is { } projectile)
                {
                    Assert.Equal(Math.Max(1, projectile.Count + 2), scaled[i].Attack.Projectile!.Count);
                    Assert.Equal(projectile.AreaRadius * 1.2, scaled[i].Attack.Projectile!.AreaRadius, 8);
                }
            }
            Assert.Equal(scaled.Count, BossKits.SkillsOf(player, variety, 70, scaled).Count);
        }
        var row = Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!;
        var rolls = Game.RollsFor(player);
        var item = rolls.Roll(ItemCatalog.Create(row) with { ItemLevel = 80 }, ItemRarity.Rare, new Random(1));
        var drop = new ItemDrops.Drop(item, row);
        foreach (var (stat, influence) in new[]
        {
            (Stat.FoundItemsWarlordInfluenceChancePct, ItemInfluence.Warlord),
            (Stat.FoundItemsCrusaderInfluenceChancePct, ItemInfluence.Crusader),
            (Stat.FoundItemsRedeemerInfluenceChancePct, ItemInfluence.Redeemer),
            (Stat.FoundItemsHunterInfluenceChancePct, ItemInfluence.Hunter),
        })
        {
            var influenced = ApparitionRewards.Apply(drop, key => key == stat ? 100 : 0, rolls, new Random(2));
            Assert.Equal(influence, influenced.Item.Influences);
            Assert.Equal(ItemRarity.Rare, influenced.Item.Rarity);
            Assert.NotEmpty(influenced.Item.Mods);
            Assert.Same(influenced, ApparitionRewards.Apply(influenced, _ => 100, rolls, new Random(2)));
        }
        Assert.Same(drop, ApparitionRewards.Apply(drop, _ => 0, rolls, new Random(2)));
        foreach (var ineligible in new[] { item with { Rarity = ItemRarity.Normal }, item with { Rarity = ItemRarity.Unique },
            item with { Synthesised = true }, item with { FracturedCount = 1 }, item with { Corrupted = true } })
        {
            var original = drop with { Item = ineligible };
            Assert.Same(original, ApparitionRewards.Apply(original, _ => 100, rolls, new Random(2)));
        }
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
            await ApparitionCombat(player);
            await ApparitionPartyVisibility(player);
            TemporaryShrineDamage(player);
            await EvolutionLifecycle(player);
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
        }
    }

    static async Task ApparitionCombat(Player player)
    {
        player.Walk.Teleport(100, 100, 0);
        foreach (string id in new[] { "Warlord", "Crusader", "Hunter", "Redeemer" })
        foreach (bool claimed in new[] { false, true })
        {
            var row = ShrineCatalog.Find(id)!;
            var profile = ApparitionEffect.Describe(player, row, claimed);
            Assert.Equal(12, profile.Interval);
            Assert.Contains(claimed ? "/Player/" : "/AtlasExiles/Atlas", profile.Body.Id);
            Assert.NotEmpty(profile.Payloads);
            foreach (int effect in profile.Payloads)
            {
                var attack = BossKits.AttackFor(player, profile.Body, 70, effect, 0x4000);
                Assert.True(attack.Damage.MaxTotal > 1, $"{id} {claimed} effect {effect}: {attack.Damage}");
                if (id == "Redeemer")
                {
                    Assert.Contains("Tornado", ApparitionEffect.TornadoProjectile(player, effect, 70).Metadata, StringComparison.OrdinalIgnoreCase);
                    Assert.True(ApparitionEffect.Convert(player, effect, 70, attack.Damage)[DamageType.Cold].Max > 1);
                }
            }
            if (!claimed) continue;
            var target = new SpawnedMonster("apparition target", new(110, 100, 10, (_, _) => true, 1000, 1000), 20, 5, 1000000)
                { Life = 1000000, Variety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie"), Level = 1 };
            player.Area.Monsters[0x7071] = target;
            player.Area.Grid.Set(new(0x7071), (110, 100));
            var effectController = new ApparitionEffect();
            var shrine = new ShrineInstance { Definition = row, At = (100, 100), State = ShrineState.Used, ObjectId = 0x9090 };
            await effectController.Tick(new(player, 0), shrine);
            var actors = player.Area.Monsters.Where(p => p.Value.NoRewards && p.Value.Variety?.Id == profile.Body.Id).ToArray();
            Assert.True(actors.Length == 1, $"{id}: expected one apparition; alive={player.PlayerLife}, closed={player.Closed}, monsters={string.Join(';', player.Area.Monsters.Select(m => m.Value.Label))}");
            var actor = Assert.Single(actors);
            Assert.True(actor.Value.Invulnerable);
            Assert.Null(actor.Value.OwnerId);
            Assert.Null(actor.Value.Attack);
            await effectController.Tick(new(player, profile.CastSeconds + .7), shrine);
            if (id == "Redeemer")
            {
                var session = Game.ProjectilesFor(player);
                Assert.NotEmpty(session.Runtime.Projectiles);
                session.AdvanceTo(session.Runtime.Now + GameServer.Core.Projectiles.ProjectileRuntime.Time(1.5));
            }
            Assert.True(target.Life < target.MaxLife, id + " did not land a contact");
            await effectController.OnExpired(new(player, 7), shrine);
            Assert.DoesNotContain(actor.Key, player.Area.Monsters.Keys);
            await effectController.Tick(new(player, 9), shrine);
            Assert.DoesNotContain(player.Area.Monsters.Values, m => m.Variety?.Id == profile.Body.Id);
            await effectController.Tick(new(player, 12), shrine);
            var leaving = Assert.Single(player.Area.Monsters, m => m.Value.Variety?.Id == profile.Body.Id);
            if (id == "Redeemer")
            {
                await effectController.Tick(new(player, 12 + profile.CastSeconds + .7), shrine);
                Assert.NotEmpty(Game.ProjectilesFor(player).Runtime.Projectiles);
            }
            var area = player.Area;
            var sessionAtExit = player.ProjectileRuntime;
            area.Driven = true;
            player.LeavingArea = true;
            try
            {
                await effectController.OnMapUnload(new(player, 12), shrine);
                Assert.False(area.Objects.Has(leaving.Key));
                Assert.Contains(leaving.Key, area.Monsters.Keys); // Shared state waits for its owning frame.
                if (sessionAtExit is not null) Assert.Empty(sessionAtExit.Runtime.Projectiles);
                area.Step(1, 13);
            }
            finally { player.LeavingArea = false; area.Driven = false; }
            Assert.DoesNotContain(leaving.Key, player.Area.Monsters.Keys);
            Assert.True(leaving.Value.Dead);
            Assert.DoesNotContain(area.MonstersNear(leaving.Value.Walk.Position(13), 10), m => m.Key == leaving.Key);
            Assert.DoesNotContain(leaving.Key, player.MinionOrigins.Keys);
            Assert.Contains(area.Delta.Take(), r => r.Entity == leaving.Key && r.Kind == AreaDelta.Kind.Remove);
            player.Area.Monsters.Remove(0x7071);
        }
    }

    static async Task ApparitionPartyVisibility(Player driver)
    {
        await using var peer = await ShrineWire.Open(driver.Instance!);
        await ShrineManager.SpawnArea(driver);
        await ShrineManager.SpawnArea(peer.Player);
        driver.Walk.Teleport(700, 700, 0);
        peer.Player.Walk.Teleport(100, 100, 0);
        peer.Player.Immune = true;
        var row = ShrineCatalog.Find("Redeemer")!;
        var shrine = new ShrineInstance { Definition = row, At = (100, 100), ObjectId = 0x9090 };
        var effect = new ApparitionEffect();
        try
        {
            await effect.Tick(new(driver, 0), shrine);
            await effect.Tick(new(driver, ApparitionEffect.Describe(driver, row, false).CastSeconds + .7), shrine);
            var session = Game.ProjectilesFor(peer.Player);
            Assert.NotEmpty(session.Runtime.Projectiles);
            Assert.All(session.Runtime.Projectiles, p => Assert.False(p.Profile.HideVisual));
            await effect.OnMapUnload(new(driver, 7), shrine);
            Assert.Empty(session.Runtime.Projectiles);
        }
        finally
        {
            await effect.OnMapUnload(new(driver, 7), shrine);
            await ShrineManager.LeaveArea(peer.Player);
            await ShrineManager.LeaveArea(driver);
            driver.Walk.Teleport(100, 100, 0);
        }
    }

    static void TemporaryShrineDamage(Player player)
    {
        var inactive = new SpawnedMonster("inactive", new(100, 100, 10, (_, _) => true, 1000, 1000), 20, 5, 1000);
        var original = DamagePacket.Of(DamageType.Physical, 100, 100);
        var random = new Random(1);
        for (int i = 0; i < 100; i++) Game.MonsterTemporaryHit(inactive, original, random);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) Game.MonsterTemporaryHit(inactive, original, random);
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        watch.Stop();
        Assert.Equal(0, bytes);
        Console.WriteLine($"Temporary monster offence inactive: {watch.Elapsed.TotalMilliseconds:0.###} ms / 10000 hits, {bytes} bytes");
        var damage = Game.TemporaryMonsterDamage(DamagePacket.Of(DamageType.Physical, 100, 100),
            new Dictionary<uint, int> { [Stat.PhysicalDamagePctToAddAsChaos] = 10, [Stat.ChaosDamagePlusPct] = 50 }, new Random(1));
        Assert.Equal(100, damage[DamageType.Physical].Min);
        Assert.Equal(15, damage[DamageType.Chaos].Min);
        damage = Game.TemporaryMonsterDamage(DamagePacket.Of(DamageType.Physical, 100, 100),
            new Dictionary<uint, int> { [Stat.BaseConvertPctPhysicalDamageToRandomElement] = 50 }, new Random(1));
        Assert.Equal(50, damage[DamageType.Physical].Min);
        Assert.Equal(100, damage.MinTotal);
        var monster = new SpawnedMonster("aura target", new(100, 100, 10, (_, _) => true, 1000, 1000), 20, 5, 1000);
        monster.SetTemporaryStats("shrine", new Dictionary<uint, int>
            { [Stat.ElementalDamageTakenPlusPct] = -50, [Stat.DegenEffectPlusPct] = -50,
              [Stat.BaseAvoidProjectilesPctChance] = 100, [Stat.BaseReduceEnemyColdResistancePct] = 10 });
        Assert.Equal(50, Game.MonsterTakesMitigated(monster, DamagePacket.Of(DamageType.Cold, 100, 100), true, 0));
        Assert.Equal(.5, Game.MonsterDamageTaken(monster, true, 0));
        Assert.True(Game.MonsterAvoidsProjectile(monster, new Random(1)));
        Assert.Equal(10, Game.MonsterPenetration(monster.Effects, DamageType.Cold));
        monster.SetTemporaryStats("hexproof", new Dictionary<uint, int> { [Stat.Hexproof] = 1 });
        var hex = new Game.SupportedGem(Game.SkillsFor(player).EffectRow("ElementalWeakness"), 1, 0, [], new Dictionary<uint, int>());
        Assert.False(Game.ApplyCurseLocked(player, monster, 0, 0, false, captured: hex));
    }

    static async Task EvolutionLifecycle(Player player)
    {
        ShrineRegistry.RegisterDefaults();
        EvolutionShrine.Load();
        var inactive = new KillContext(player, new("inactive", player.Walk, 20, 5, 1000), "inactive", (100, 100));
        for (int i = 0; i < 100; i++) EvolutionShrine.OnKill(inactive);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) EvolutionShrine.OnKill(inactive);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        watch.Stop();
        Assert.Equal(0, allocated);
        Assert.Empty(inactive.Afterwards);
        Console.WriteLine($"Evolution inactive: {watch.Elapsed.TotalMilliseconds:0.###} ms / 10000 kills, {allocated} bytes");
        using var sources = GameEvents.PlayerStatSources.Add("shrine-regression", (p, stats) =>
        {
            ShrineManager.AddStats(p, stats);
            stats[Stat.ChanceToUpgradeMonstersOnKillPermyriad] = int.MaxValue; // Saturates the roll to certainty after decline too.
            return stats;
        });
        var position = player.Walk.Position(0);
        await ShrineManager.SpawnAroundPlayer(player, ["UpgradeOnKill"]);
        uint shrineId = uint.Parse(player.Instance!.ContentState[$"Shrines/id/{position.X + 12}/{position.Y}"]);
        player.Walk.Teleport(position.X + 12, position.Y, 0);
        await ShrineManager.Use(player, new(shrineId, 0, 1, 0), 0);
        var variety = Game.Monsters.All.First(v => v.Supported && v.Id.Contains("/Zombies/Zombie"));
        var dead = new SpawnedMonster("evolving corpse", new(100, 100, 10, (_, _) => true, 1000, 1000), 20, 5, 1000)
            { Variety = variety, Dead = true, Life = 0, Level = 70, Rarity = MonsterRarity.Normal };
        var kill = new KillContext(player, dead, variety.Id, (100, 100)) { PlayerKill = false };
        EvolutionShrine.OnKill(kill);
        Assert.Single(kill.Afterwards); // Owned kills are eligible too.
        foreach (var send in kill.Afterwards) await send();
        Assert.Contains(player.Area.Monsters.Values, m => ReferenceEquals(m.Variety, variety) && m.Rarity == MonsterRarity.Magic);
        var magic = player.Area.Monsters.Values.Single(m => ReferenceEquals(m.Variety, variety) && m.Rarity == MonsterRarity.Magic);
        magic.Dead = true; magic.Life = 0;
        var second = new KillContext(player, magic, variety.Id, (100, 100));
        EvolutionShrine.OnKill(second);
        Assert.Single(second.Afterwards);
        foreach (var send in second.Afterwards) await send();
        Assert.Contains(player.Area.Monsters.Values, m => ReferenceEquals(m.Variety, variety) && m.Rarity == MonsterRarity.Rare);
        var duplicate = new KillContext(player, dead, variety.Id, (100, 100));
        EvolutionShrine.OnKill(duplicate);
        Assert.Empty(duplicate.Afterwards);
        Assert.False(EvolutionShrine.Eligible(new("Breach", dead.Walk, 20, 5, 1000) { Variety = variety, SummonerId = 123 }));
        Assert.False(EvolutionShrine.Eligible(new("rare", dead.Walk, 20, 5, 1000) { Variety = variety, Rarity = MonsterRarity.Rare }));
        Assert.Equal(2500, EvolutionShrine.Chance(2500, 0, 100));
        Assert.True(EvolutionShrine.Chance(2500, 100, 100) < 2500);
        Assert.True(EvolutionShrine.Chance(2500, int.MaxValue, int.MaxValue) >= 0);
        await ShrineManager.LeaveArea(player);
        Assert.False(ShrineManager.HasBuff(player, ShrineCatalog.Find("UpgradeOnKill")!.BuffRow));
    }
}
