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
    }
}
