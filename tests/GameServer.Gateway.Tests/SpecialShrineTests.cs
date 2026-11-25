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
            Assert.Null(evolving.Opening);
            Assert.Contains("not been consumed", evolving.Reason);
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
    }
}
