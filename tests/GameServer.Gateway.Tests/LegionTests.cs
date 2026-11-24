using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Legion;

namespace GameServer.Gateway.Tests;

public sealed class LegionTests
{
    [Fact]
    public async Task In_the_areas_frame_a_record_goes_once_to_every_client_and_a_joiners_to_it_alone()
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

    [Fact]
    public void An_actors_current_action_and_its_action_data_are_written_as_the_client_reads_them()
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
    }

    static string Describe(LegionArmy.ArmyPlan plan) => string.Join("|", plan.Factions.Select(f => f.Id
        + ":" + string.Join(",", f.Squads.Select(q => $"{q.Tier}x{q.Count}[{string.Join(";", (q.Rewards ?? []).Select(r => string.Join("+", r)))}]"))
        + ":" + string.Join(",", f.Chests.Select(c => $"{c.WarHoard}[{string.Join("+", c.Rewards ?? [])}]"))));

    [Fact]
    public void The_reward_plan_uses_only_the_allowed_rows_gives_hoards_two_or_three_and_is_the_same_for_a_seed()
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
                    if (chest.WarHoard) { hoards++; Assert.InRange(chest.Rewards.Length, 2, 3); }
                    else Assert.Single(chest.Rewards);
                }
            }
        }
        Assert.True(sergeants > 0 && rewarded > 0 && chests > 0 && hoards > 0, "the seeds must include a War Hoard");
        Assert.True(rewarded < sergeants, "a sergeant carries a reward with a chance, not always");
    }

    [Fact]
    public void Every_reward_row_has_a_kind_and_the_pool_kinds_have_items_with_the_mirror_shard_the_rarest_harbinger_drop()
    {
        var catalog = Program.Items;
        foreach (int row in LegionRewards.RewardRows)
            if (LegionRewards.Pool(catalog, row, 100) is { } pool) Assert.NotEmpty(pool);
        var harbinger = LegionRewards.Pool(catalog, 8, 100)!;
        var mirror = Assert.Single(harbinger, t => t.Id.EndsWith("/CurrencyDuplicateShard", StringComparison.Ordinal));
        Assert.All(harbinger.Where(t => t != mirror), t => Assert.True(LegionRewards.HarbingerWeight(t) > LegionRewards.HarbingerWeight(mirror)));
        Assert.DoesNotContain(LegionRewards.Pool(catalog, 10, 100)!, t => t.Id.Contains("Hysteria1") || t.Id.Contains("Delirium1") || t.Id.Contains("Affliction"));
        Assert.All(LegionRewards.Pool(catalog, 9, 100)!, t => Assert.DoesNotContain("Breachstone", t.Name));
        Assert.Contains(harbinger, t => t.Id.EndsWith("/CurrencyUpgradeMapTier", StringComparison.Ordinal));
    }
}
