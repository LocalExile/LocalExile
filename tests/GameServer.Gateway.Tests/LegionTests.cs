using System.Buffers.Binary;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Legion;

namespace GameServer.Gateway.Tests;

public sealed class LegionTests
{
    [Fact]
    public async Task The_encounters_records_and_an_actors_current_action_are_written_as_the_client_reads_them()
    {
        {
        var layout = new AreaLayout(0x1e9, "MapWorldsReef", 600, 600, 300, 300, (_, _) => true, []);
        var instance = new InstanceState();
        Player Make()
        {
            var player = new Player(layout, 1, 1) { Instance = instance, Connection = { PlayerSent = true } };
            instance.AddPlayer(player);
            player.Connection.WorldOutput.Begin(1);
            return player;
        }
        Player first = Make(), second = Make();
        Assert.Same(first.Area, second.Area);
        const uint id = 0x500;
        var owner = GameArea.Own(first.Area);
        try
        {
            // A per-player loop would put one copy per player in the frame that every client is written.
            await LegionOutput.ToArea(first, p => Program.Send(p, InstanceProtocol.EnterWakeZone(id), null));
            Assert.Null(Assert.Single(first.Area.Delta.Take()).Only);
            await LegionOutput.ToOne(second, () => Program.Send(second, InstanceProtocol.EnterWakeZone(id), null));
            Assert.Same(second, Assert.Single(first.Area.Delta.Take()).Only);
        }
        finally { GameArea.Own(owner); }
        }
        {
        const uint id = 0x08000123;
        var action = new ActorAction(0x18fe, 7) { Animation = new AnimationControllerState(3, 0f), Payload = [0, 100, 0, 0, 0] };
        // 0x2bc to the Actor (index 9): f0 0x04, f1 0; then little-endian u16 skill, the action record (u16 sequence, u32 target
        // id, u32 x, u32 y, u16 flags), u8, the animation block (u32 path length, i32 clip, f32 time, f32 speed, f32, i32 queued)
        // and the class's payload as is.
        byte[] action4 =
        [
            0x04, 0x00, 0xfe, 0x18,
            0x07, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x00, 0x00,
            0, 0, 0, 0, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 0xff, 0xff,
            0, 100, 0, 0, 0,
        ];
        Assert.Equal((byte[])[0x02, 0xbc, 0x08, 0x00, 0x01, 0x23, 0, 0, 0, 0, 0, 0, 0x00, (byte)(1 + action4.Length), 0x09, .. action4],
            InstanceEntities.ActorCurrentAction(id, 9, action));
        // A monster created with it carries the same bytes after its Actor's flags (here no skill list), from its first frame.
        var monster = new MonsterObject(10, 10, 100, MonsterObject.BaseComponents) { CurrentAction = action };
        var blob = monster.Blob();
        Assert.True(blob.AsSpan().IndexOf(action4) > 0);
        Assert.Equal(-1, new MonsterObject(10, 10, 100, MonsterObject.BaseComponents).Blob().AsSpan().IndexOf(action4.AsSpan(0, 4)));
        // S→C 0x2c8: big-endian object header, action type, skill id, sequence, u8 sub-id, u16 length, the payload as is.
        Assert.Equal((byte[])[0x02, 0xc8, 0x08, 0x00, 0x01, 0x23, 0, 0, 0, 0, 0, 0, 0xd8, 0xfe, 0x18, 0xfe, 0x00, 0x07, 0x00, 0x00, 0x05, 2, 100, 0, 0, 0],
            InstanceProtocol.ActionData(id, 0xd8fe, 0x18fe, 7, 0, [2, 100, 0, 0, 0]));
        // Non-lethal life floor in TakeDamageLocked: lowers life normally, stops at 1 and invokes callback once without killing.
        var frozen = new SpawnedMonster("frozen", new(100, 100, 0), 40, 4, 100);
        int floorHits = 0;
        frozen.OnNonLethalZero = target =>
        {
            floorHits++;
            target.OnNonLethalZero = null;
            target.EntranceGrace = true;
        };
        Assert.Equal((60u, false), Game.TakeDamageLocked(frozen, 40));
        frozen.Life = 60;
        Assert.Equal(0, floorHits);
        Assert.Equal((1u, false), Game.TakeDamageLocked(frozen, 200));
        Assert.Equal(1u, frozen.Life);
        Assert.False(frozen.Dead);
        Assert.Equal(1, floorHits);
        Assert.True(frozen.EntranceGrace);
        Assert.Equal((1u, false), Game.TakeDamageLocked(frozen, 50));
        Assert.Equal(1, floorHits);
        // Capture-verified wire primitives: LegionRules 0x2c9 mode 0/1/5, 0xdf environments, LegionFlashLight (46 bytes) and chest symbols.
        var data = LegionData.Current;
        Assert.True(data.RulesetRow is not null && data.EnvWorld > 0 && data.EnvBattle > 0 && data.EnvPostBattle > 0);
        byte[] mode0 = LegionRules.Begin(1234, -5678, 68, 0x00abcdefu, 0f, 15f, stage: 0, step: 0, angle: 0f)!;
        byte[] officialMode0 = Convert.FromHexString("5e25004a0200002d040000440000000000f7070000000000000000000000000000000000007041");
        BinaryPrimitives.WriteInt32LittleEndian(officialMode0.AsSpan(3), 1234);
        BinaryPrimitives.WriteInt32LittleEndian(officialMode0.AsSpan(7), -5678);
        BinaryPrimitives.WriteUInt32LittleEndian(officialMode0.AsSpan(17), 0x00abcdefu);
        Assert.Equal(officialMode0, mode0[2..]);
        Assert.Equal((byte[])[0x02, 0xc9, data.RulesetRow.Value, 5, 1, 0xef, 0xcd, 0xab, 0x00], LegionRules.Activated(0x00abcdefu));
        Assert.Equal((byte[])[0x02, 0xc9, data.RulesetRow.Value, 7, 5, 0xef, 0xcd, 0xab, 0x00, 2, 4], LegionRules.State(0x00abcdefu, 2, 4));
        byte[] envEnding = Convert.FromHexString("00df02049700000000000498003e4ccccd");
        BinaryPrimitives.WriteUInt16BigEndian(envEnding.AsSpan(3), data.EnvBattle);
        BinaryPrimitives.WriteUInt16BigEndian(envEnding.AsSpan(10), data.EnvPostBattle);
        Assert.Equal(envEnding, LegionRules.Environments((data.EnvBattle, 0.0), (data.EnvPostBattle, 0.2)));
        Assert.Equal(46, LegionMonolith.FlashLightBlob(589, 792, 0x400006fu).Length);
        // Phase 3 & 4 wire primitives: LegionRules endless & Timeless Conflict alert flags, Domain objects, Scarab*New* kind fix.
        byte[] endlessMode0 = LegionRules.Begin(333, 333, 80, 0x100u, 0f, 315f, stage: 1, step: 2, endless: true)!;
        Assert.Equal(1, endlessMode0[17]);   // endless byte (after 0x02c9, ruleset, len, mode, x, y, level)
        Assert.Equal(0, endlessMode0[18]);   // alertMode = 0
        byte[] alertMode0 = LegionRules.Begin(100, 200, 75, 0x101u, 0f, 15f, stage: 1, step: 2, alertMode: true, gauge: 0.5f)!;
        Assert.Equal(0, alertMode0[17]);     // endless = 0
        Assert.Equal(1, alertMode0[18]);     // alertMode = 1
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(alertMode0.AsSpan(29)));
        Assert.True(data.EnvDomain > 0 && data.DomainAreas.Count == 4 && data.EmblemsByRow.Count == 10 && data.DomainAtlasNodeHash > 0);
        Assert.True(LegionDomain.BuildInitiatorObject(333, 333, null, 0, true)!.Blob().Length > 20);
        Assert.True(LegionDomain.BuildCrystalObject(333, 333, null, false)!.Blob().Length > 10);
        // MapRunCatalog strips trailing "New" from Scarab*New* BaseItemTypes so map_legion_scarab_count resolves cleanly.
        var runCatalog = MapRunCatalog.Load("data");
        var scarabNew1 = Program.Items.Find("Metadata/Items/Scarabs/ScarabLegionNew1");
        Assert.NotNull(scarabNew1);
        var scarabMods = runCatalog.Of([new MapDevice.Slot(scarabNew1!.Hash, scarabNew1.Class, scarabNew1.Name, scarabNew1.Row, [], 0) { BaseId = scarabNew1.Id }], Program.Items.Mods);
        Assert.Equal(1, scarabMods.ScarabKinds.GetValueOrDefault("legion"));
        // Phase 3 Map Device opening: 2-5 normal & Unrelenting Emblems, rejection of 1 / duplicate / mixed, and "Legion" scarab claim.
        var dummyLayout = new AreaLayout(0x1e9, "Hideout", 200, 200, 100, 100, (_, _) => true, []);
        var opener = new Player(dummyLayout, 1, 1);
        static MapDevice.Slot EmblemSlot(string id)
        {
            var bt = Program.Items.Find(id)!;
            return new MapDevice.Slot(bt.Hash, bt.Class, bt.Name, bt.Row, [], 0) { BaseId = bt.Id };
        }
        var normalMapReq = new MapOpening(opener, [new MapDevice.Slot(1, MapDevice.MapClass, "Map", 0, [], 0)], false, null, "");
        LegionDomain.ResolveOpening(normalMapReq);
        Assert.Contains("Legion", normalMapReq.HandledContent);
        string[] normalIds =
        [
            "Metadata/Items/MapFragments/CurrencyLegionFragmentKarui",
            "Metadata/Items/MapFragments/CurrencyLegionFragmentEternal",
            "Metadata/Items/MapFragments/CurrencyLegionFragmentVaal",
            "Metadata/Items/MapFragments/CurrencyLegionFragmentTemplar",
            "Metadata/Items/MapFragments/CurrencyLegionFragmentMaraketh",
        ];
        string[] uberIds =
        [
            "Metadata/Items/MapFragments/CurrencyLegionFragmentKaruiUber",
            "Metadata/Items/MapFragments/CurrencyLegionFragmentEternalUber",
            "Metadata/Items/MapFragments/CurrencyLegionFragmentVaalUber",
            "Metadata/Items/MapFragments/CurrencyLegionFragmentTemplarUber",
            "Metadata/Items/MapFragments/CurrencyLegionFragmentMarakethUber",
        ];
        string[] expectedDomainAreas = ["LegionLeague", "LegionLeague2", "LegionLeague3", "LegionLeague4"];
        for (int n = 2; n <= 5; n++)
        {
            var req = new MapOpening(opener, [.. normalIds.Take(n).Select(EmblemSlot)], false, null, "");
            LegionDomain.ResolveOpening(req);
            Assert.NotNull(req.Opening);
            Assert.Equal(expectedDomainAreas[n - 2], req.Opening!.Area.Id);
            Assert.Equal(80, req.Opening.Area.Level);
            Assert.False(req.Opening.Area.UsesAtlas);
            Assert.True(req.ReturnToSource);
            Assert.Equal(15.0 + 60.0 * n, LegionDomain.TimerSecondsFor(req.Opening.Mods.Stats, n));
            Assert.Equal(n, req.Opening.Mods.Get(Stat.MapNumLegionFragments));
            Assert.Equal(10 * n * 3, LegionDomain.RevivalToughness(req.Opening.Mods.Stats, 3));
            Assert.True(LegionDomain.UberRevivalStats(req.Opening.Mods.Stats, 3)!.ContainsKey(Stat.PhysicalDamagePctToAddAsChaos) || n < 5);
        }
        var uberReq = new MapOpening(opener, [.. uberIds.Select(EmblemSlot)], false, null, "");
        LegionDomain.ResolveOpening(uberReq);
        Assert.NotNull(uberReq.Opening);
        Assert.Equal("LegionLeague4", uberReq.Opening!.Area.Id);
        Assert.Equal(84, uberReq.Opening.Area.Level);
        Assert.Equal(5, uberReq.Opening.Mods.Get(Stat.MapNumLegionUberFragments));
        var revivalStats = LegionDomain.UberRevivalStats(uberReq.Opening.Mods.Stats, 2);
        Assert.NotNull(revivalStats);
        Assert.True(revivalStats!.GetValueOrDefault(Stat.DamagePlusPct) > 0);
        Assert.True(revivalStats.GetValueOrDefault(Stat.AttackAndCastSpeedPlusPct) > 0);
        var oneReq = new MapOpening(opener, [EmblemSlot(normalIds[0])], false, null, "");
        LegionDomain.ResolveOpening(oneReq);
        Assert.Null(oneReq.Opening);
        Assert.NotEmpty(oneReq.Reason);
        var dupReq = new MapOpening(opener, [EmblemSlot(normalIds[0]), EmblemSlot(uberIds[0])], false, null, "");
        LegionDomain.ResolveOpening(dupReq);
        Assert.Null(dupReq.Opening);
        Assert.NotEmpty(dupReq.Reason);
        }
    }

    static string Describe(LegionArmy.ArmyPlan plan) => string.Join("|", plan.Factions.Select(f => f.Id
        + ":" + string.Join(",", f.Squads.Select(q => $"{q.Tier}x{q.Count}[{string.Join(";", (q.Rewards ?? []).Select(r => string.Join("+", r)))}]"))
        + ":" + string.Join(",", f.Chests.Select(c => $"{c.WarHoard}[{string.Join("+", c.Rewards ?? [])}]"))));

    [Fact]
    public void The_reward_plan_and_pools_use_only_the_allowed_rows_and_give_the_kinds_their_items()
    {
        var data = LegionData.Current;
        Assert.NotEmpty(data.RewardTypes);
        var stats = new Dictionary<uint, int>();
        int sergeants = 0, rewarded = 0, chests = 0, hoards = 0;
        for (int seed = 1; seed <= 300; seed++)
        {
            var plan = LegionArmy.Plan(data, 75, "MapWorldsReef", stats, new Random(seed));
            Assert.Equal(Describe(plan), Describe(LegionArmy.Plan(data, 75, "MapWorldsReef", stats, new Random(seed))));
            foreach (var f in plan.Factions)
            {
                foreach (var squad in f.Squads)
                {
                    if (squad.Tier != 2) { Assert.Null(squad.Rewards); continue; }
                    Assert.Equal(squad.Count, squad.Rewards!.Length);
                    foreach (var r in squad.Rewards) { sergeants++; Assert.True(r.Length <= 1); rewarded += r.Length; Assert.DoesNotContain(r, row => row is 11 or 17); }
                }
                foreach (var chest in f.Chests)
                {
                    chests++;
                    Assert.NotNull(chest.Rewards);
                    Assert.DoesNotContain(chest.Rewards, row => row is 11 or 17);
                    Assert.Equal(chest.Rewards.Length, chest.Rewards.Distinct().Count());
                    Assert.True(chest.HardmodeChest >= 0);
                    if (chest.WarHoard) { hoards++; Assert.InRange(chest.Rewards.Length, 2, 3); Assert.Equal((35, 100), (chest.FromPct, chest.ToPct)); }
                    else { Assert.Single(chest.Rewards); Assert.Equal((20, 70), (chest.FromPct, chest.ToPct)); }
                }
            }
        }
        Assert.True(sergeants > 0 && rewarded > 0 && chests > 0 && hoards > 0, "the seeds must include a War Hoard");
        Assert.True(rewarded < sergeants, "a sergeant carries a reward with a chance, not always");
        // Table columns: faction orderliness (__col3), variety spawn weights (__col6), prop counts (__col15), HardmodeChest and chest serialization.
        Assert.True(data.Factions.First(f => f.Id == "EternalEmpire").Orderliness >= 0.9);
        Assert.True(data.Factions.First(f => f.Id == "Karui").Orderliness < 0.9);
        Assert.Contains(data.MonsterVarieties, v => v.SpawnWeight > 1);
        Assert.All(data.MonsterVarieties.Where(v => v.MiscAnimated.Length > 0), v => Assert.NotEmpty(v.PropCounts!));
        Assert.All(data.RewardTypes, rt => Assert.True(rt.MiscAnimatedKey >= 0 && data.MiscAnimatedModels.ContainsKey(rt.MiscAnimatedKey) && rt.Scale > 0));
        Assert.NotEmpty(data.ChestsByRow);
        foreach (var (_, (chestType, _)) in data.ChestsByRow)
        {
            var info = LegionMonolith.ResolveChestInfo(chestType);
            Assert.NotNull(info);
            var box = new LegionMonolith.Box(1, chestType, 100, 100, 0, null, null, false, false, [0], 0, [2u]);
            Assert.True(LegionMonolith.BuildChestObject(box, info).Blob().Length > 30);
        }
        // Low-level campaign monoliths never roll endgame/league symbols whose pools are empty at that level.
        for (int seed = 1; seed <= 40; seed++)
        {
            var climb = LegionArmy.Plan(data, 7, "1_1_6", stats, new Random(seed));
            foreach (var f in climb.Factions)
            {
                foreach (var q in f.Squads)
                    if (q.Rewards is { } qr)
                        foreach (var rs in qr) Assert.DoesNotContain(rs, row => row is 3 or 7 or 8 or 9 or 12 or 13 or 14);
                foreach (var c in f.Chests)
                    Assert.DoesNotContain(c.Rewards!, row => row is 3 or 7 or 8 or 9 or 12 or 13 or 14);
            }
        }
        var catalog = Program.Items;
        foreach (int row in LegionRewards.RewardRows)
            if (LegionRewards.Pool(catalog, row, 100) is { } pool) Assert.NotEmpty(pool);
        var harbinger = LegionRewards.Pool(catalog, 8, 100)!;
        var mirror = Assert.Single(harbinger, t => t.Id.EndsWith("/CurrencyDuplicateShard", StringComparison.Ordinal));
        Assert.All(harbinger.Where(t => t != mirror), t => Assert.True(LegionRewards.HarbingerWeight(t) > LegionRewards.HarbingerWeight(mirror)));
        Assert.Contains(harbinger, t => t.Id.EndsWith("/CurrencyUpgradeMapTier", StringComparison.Ordinal));
        Assert.Contains(harbinger, t => t.Id.EndsWith("/CurrencyUpgradeMapTierShard", StringComparison.Ordinal));
        Assert.DoesNotContain(harbinger, t => t.Id.Contains("CurrencyHarbingerBlessing", StringComparison.Ordinal));
        var essences100 = LegionRewards.Pool(catalog, 10, 100)!;
        Assert.DoesNotContain(essences100, t => t.Name.StartsWith("Deafening ", StringComparison.Ordinal) || t.Id.Contains("Hysteria1") || t.Id.Contains("Delirium1") || t.Id.Contains("Affliction"));
        Assert.Contains(essences100, t => t.Name.StartsWith("Shrieking ", StringComparison.Ordinal));
        Assert.DoesNotContain(LegionRewards.Pool(catalog, 10, 50)!, t => t.Name.StartsWith("Shrieking ", StringComparison.Ordinal));
        Assert.All(LegionRewards.Pool(catalog, 9, 100)!, t => Assert.DoesNotContain("Breachstone", t.Name));
        var fragments = LegionRewards.Pool(catalog, 12, 100)!;
        Assert.Contains(fragments, t => t.Id.EndsWith("/CurrencyVaalFragment1_2", StringComparison.Ordinal));
        Assert.Contains(fragments, t => t.Id.EndsWith("/CurrencyShaperFragment1", StringComparison.Ordinal));
        Assert.DoesNotContain(fragments, t => t.Id.Contains("Complete", StringComparison.Ordinal) || t.Id.Contains("Uber", StringComparison.Ordinal) || t.Id.Contains("Quest", StringComparison.Ordinal) || t.Name.Contains("[DNT", StringComparison.Ordinal));
        var abyss = LegionRewards.Pool(catalog, 7, 100)!;
        Assert.Contains(abyss, t => t.Id.EndsWith("/BeltAbyss", StringComparison.Ordinal));
        Assert.DoesNotContain(abyss, t => t.Id.EndsWith("/JewelAbyssConglomerate", StringComparison.Ordinal));
        // Phase 4: Scarabs, Atlas passives, Timeless Conflict alert values & Emblematic splinter upgrades.
        var basePlan = LegionArmy.Plan(data, 80, "MapWorldsReef", stats, new Random(42));
        var officersStats = new Dictionary<uint, int>
        {
            [Stat.MapLegionAdditionalNumberOfSergeants] = 7,
            [Stat.MapLegionContainsMarakethFaction] = 1,
            [Stat.MapLegionLeagueForceGeneral] = 1,
            [Stat.MapLegionGeneralsSpawnBothGenerals] = 1,
            [Stat.MapLegionArmyAdditionalWarChest] = 1,
            [Stat.MapLegionChestsContainsAnAdditionalReward] = 1,
            [Stat.MapLegionSergeantsRewardAdditionalChancePct] = 100,
            [Stat.MapLegionMonstersWithRewardsHavePctChanceToGainTwoAdditionalRewards] = 100,
        };
        var juicedPlan = LegionArmy.Plan(data, 80, "MapWorldsReef", officersStats, new Random(42));
        Assert.True(juicedPlan.Factions.Sum(f => f.Squads.Where(q => q.Tier == 2).Sum(q => q.Count))
            > basePlan.Factions.Sum(f => f.Squads.Where(q => q.Tier == 2).Sum(q => q.Count)));
        Assert.Contains(juicedPlan.Factions, f => f.Id == "Maraketh");
        // Sekhema scarab forces Maraketh even below its natural MinLevel (68).
        Assert.Contains(LegionArmy.Plan(data, 40, "MapWorldsReef", new Dictionary<uint, int> { [Stat.MapLegionContainsMarakethFaction] = 1 }, new Random(42)).Factions, f => f.Id == "Maraketh");
        Assert.All(juicedPlan.Factions, f =>
        {
            Assert.True(f.Squads.Where(q => q.Tier == 2).Sum(q => q.Count) >= 7);
            Assert.Equal(1, f.Squads.Where(q => q.Tier == 3).Sum(q => q.Count)); // Face Off: both armies spawn a General
            Assert.True(f.Chests.Count(c => c.WarHoard) >= 1);                   // War Supplies: +1 War Hoard
            Assert.All(f.Chests, c => Assert.True(c.Rewards!.Length >= (c.WarHoard ? 3 : 2))); // Pillaged Treasures
            Assert.All(f.Squads.Where(q => q.Tier == 2), q => Assert.All(q.Rewards!, r => Assert.Equal(3, r.Length))); // Logistical Support +2
        });
        var faridunPlan = LegionArmy.Plan(data, 80, "MapWorldsReef", new Dictionary<uint, int>
        {
            [Stat.MapLegionContainsMarakethFaction] = 1,
            [Stat.MapLegionFaridunReplaceMarakethFaction] = 1,
        }, new Random(7));
        Assert.Contains(faridunPlan.Factions, f => f.Id == "Faridun");
        Assert.Equal(2.5, LegionStasis.InitialLifeMultiplier(3, 50, 0), 3);
        Assert.Equal(1000, LegionArmy.AlertMax(data, 80));
        Assert.Equal((5, 18, 40, 350, 40, 80), (
            LegionArmy.RankAlertValue(data, 80, 0, false, false),
            LegionArmy.RankAlertValue(data, 80, 1, false, false),
            LegionArmy.RankAlertValue(data, 80, 2, false, false),
            LegionArmy.RankAlertValue(data, 80, 3, false, false),
            LegionArmy.RankAlertValue(data, 80, 0, true, false),
            LegionArmy.RankAlertValue(data, 80, 0, true, true)));
        var splinterStacks = new List<(string Item, int Count)>();
        LegionRewards.AddSplinters(splinterStacks, "Karui", 10, new Dictionary<uint, int>
        {
            [Stat.MapLegionGeneralsSplinterDropAmountPlusPct] = 100,
            [Stat.MapLegionMonsterOnlySplinterDropsDuplicatedChancePct] = 100,
            [Stat.MapReplaceLegionSplinterWithLegionFragmentChancePermyriad] = 10000,
        }, isChest: false, isGeneral: true, new Random(1));
        var upgraded = Assert.Single(splinterStacks);
        Assert.Equal("Metadata/Items/MapFragments/CurrencyLegionFragmentKarui", upgraded.Item);
        Assert.Equal(40, upgraded.Count);
        // Phase 3: Endless army planning (MinLevel=101, *Endless Tier 1 varieties, 6s/16s/24s revival CDs) & Timeless Jewels / Stasis Prison.
        string[] fiveFactions = ["Karui", "EternalEmpire", "Vaal", "Templar", "Maraketh"];
        var endlessPlan = LegionArmy.PlanEndless(data, fiveFactions, 84, 0, 50, new Random(123));
        Assert.Equal(5, endlessPlan.Factions.Length);
        Assert.All(endlessPlan.Factions, f =>
        {
            Assert.Equal(4, f.Squads.Where(q => q.Tier == 2).Sum(q => q.Count));
            Assert.Equal(1, f.Squads.Where(q => q.Tier == 3).Sum(q => q.Count));
            Assert.All(f.Squads.Where(q => q.Tier == 0), q => Assert.Equal(6000, q.RevivalMs));
            Assert.All(f.Squads.Where(q => q.Tier == 1), q => Assert.Equal(6000, q.RevivalMs));
            Assert.All(f.Squads.Where(q => q.Tier == 2), q => { Assert.Equal(16000, q.RevivalMs); Assert.All(q.Rewards!, r => Assert.NotEmpty(r)); });
            Assert.All(f.Squads.Where(q => q.Tier == 3), q => { Assert.Equal(24000, q.RevivalMs); Assert.All(q.Rewards!, r => Assert.True(r.Length >= 2)); });
        });
        foreach (string fid in fiveFactions)
        {
            var jewel = LegionRewards.CreateTimelessJewel(catalog, fid, 84, new Random(99));
            Assert.NotNull(jewel);
            Assert.False(jewel!.Identified);
            Assert.Equal(84, jewel.ItemLevel);
            Assert.NotNull(TimelessJewelCatalog.Read(jewel, catalog.Mods));
        }
        var domainDummy = new Player(new AreaLayout(0x1e9, "LegionLeague4", 400, 400, 200, 200, (_, _) => true, []), 1, 1);
        var stasis = LegionRewards.CreateStasisPrison(domainDummy, 84, new Random(1));
        Assert.NotNull(stasis);
        Assert.False(stasis!.Identified);
        Assert.Equal(84, stasis.ItemLevel);
        // 5-way Domain end-burst drops faction Timeless Jewels and can drop Unrelenting Timeless Emblems from killed Generals.
        bool sawUberEmblem = false, sawTimelessJewel = false;
        var fiveKills = fiveFactions.ToDictionary(f => f, _ => 3);
        for (int seed = 1; seed <= 40 && !(sawUberEmblem && sawTimelessJewel); seed++)
        {
            var burst = LegionRewards.RollDomainRewards(domainDummy, [0, 1, 15], fiveKills, totalKills: 120, factionCount: 5, uberCount: 0, level: 80, new Random(seed));
            foreach (var item in burst)
            {
                string? bid = catalog.ByHash(item.TypeHash)?.Id;
                if (bid == TimelessJewelCatalog.BaseId) sawTimelessJewel = true;
                if (bid is not null && bid.StartsWith("Metadata/Items/MapFragments/CurrencyLegionFragment", StringComparison.Ordinal) && bid.EndsWith("Uber", StringComparison.Ordinal))
                    sawUberEmblem = true;
            }
        }
        Assert.True(sawTimelessJewel, "5-way Domain must drop Timeless Jewels from defeated Generals");
        Assert.True(sawUberEmblem, "4/5-way Domain Generals must be able to drop Unrelenting Timeless Emblems");
    }
}
