using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Exercises the Gateway's actual private mutation/load path, durable saves and encrypted network sender. No listening
/// game server or game process is started. The real extracted client data is required, as for the other data-backed tests.
/// </summary>
[CollectionDefinition("Ascendancy Gateway integration", DisableParallelization = true)]
public sealed class AscendancyGatewayIntegrationCollection;

[Collection("Ascendancy Gateway integration")]
public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public async Task Ascendancy_allocation_grants_ordinary_points_and_both_pools_survive_a_new_connection()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter();
        var first = await game.Change([54114, 48846], allocate: true);
        Assert.Null(first.Error);
        Assert.Equal(new ushort[] { 54114, 48846 }, game.State.MainAscendancy.Allocated);
        Assert.Empty(game.State.Passives);
        Assert.Equal(8, game.State.MainAscendancy.EarnedPoints);
        Assert.Equal((ushort)20, game.State.RefundPoints);
        var initialPacket = PassivePacket(first.Bytes, 2);
        Assert.Equal((ushort)8, initialPacket.AscendancyPoints);
        Assert.Equal((ushort)0, initialPacket.QuestPoints);
        Assert.Equal((byte)1, initialPacket.Level);
        Assert.Equal((byte)0x44, initialPacket.ClassByte); // Duelist IntegerId 4, ascendancy ClassNo 1.
        Assert.Equal(new ushort[] { 48846, 54114 }, initialPacket.Nodes);

        ushort[] ordinary = OrdinaryDuelistNodes(6);
        var ordinaryChange = await game.Change(ordinary, allocate: true);
        Assert.Null(ordinaryChange.Error); // level 1 has no points of its own; the ascendancy grants exactly six.
        Assert.Equal(ordinary, game.State.Passives);
        Assert.Equal((ushort)0, game.State.ExtraPassivePoints);
        var packet = PassivePacket(ordinaryChange.Bytes, 8);
        Assert.Equal((ushort)8, packet.AscendancyPoints);
        Assert.Equal((ushort)0, packet.QuestPoints);
        Assert.Equal(ordinary.Concat(new ushort[] { 54114, 48846 }).Order(), packet.Nodes);

        var reconnected = game.Reload();
        Assert.Equal(ordinary, reconnected.Passives);
        Assert.Equal(new ushort[] { 54114, 48846 }, reconnected.MainAscendancy.Allocated);
        Assert.Equal(8, reconnected.MainAscendancy.EarnedPoints);
        Assert.Equal((ushort)20, reconnected.RefundPoints);
        Assert.Equal(AscendancyFamily.Phrecia, reconnected.MainAscendancy.Selection!.Family);
    }

    [Fact]
    public async Task Foreign_family_unknown_id_and_overspending_are_refused_with_the_canonical_tree()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(awards: ["normal"]);
        string before = game.SavedJson;
        foreach (ushort[] request in new ushort[][] { [42293], [0], [54114, 48846, 24607] })
        {
            var result = await game.Change(request, allocate: true);
            Assert.NotNull(result.Error);
            Assert.Equal(before, game.SavedJson);
            Assert.Empty(game.State.MainAscendancy.Allocated);
            Assert.Empty(game.State.Passives);
            var packet = PassivePacket(result.Bytes, 0);
            Assert.Empty(packet.Nodes);
            Assert.Equal((ushort)2, packet.AscendancyPoints);
        }
    }

    [Theory]
    [InlineData(false, "Slayer")]
    [InlineData(true, "Aristocrat")]
    public async Task Disconnected_ordinary_allocations_and_mastery_selections_restore_the_canonical_tree(
        bool phrecia, string choice)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 20,
            family: phrecia ? AscendancyFamily.Phrecia : AscendancyFamily.Normal, choice: choice);
        string before = game.SavedJson;
        var snapshot = game.State.SavedPassiveProgress;
        var pools = game.State.Pools;
        ushort mastery = Program.Passives.EffectsFor(42533)[0].Hash; // Accuracy Mastery, far from the Duelist start.
        foreach (var request in new (ushort Node, ushort? Effect)[] { (24377, null), (42533, mastery) })
        {
            var refused = await game.Change([request.Node], allocate: true, masteryEffect: request.Effect);
            Assert.NotNull(refused.Error);
            Assert.Equal(before, game.SavedJson);
            Assert.Same(snapshot, game.State.SavedPassiveProgress);
            Assert.Equal(pools, game.State.Pools);
            Assert.Empty(game.State.Passives);
            Assert.Empty(game.State.MasteryEffects);
            Assert.Empty(PassivePacket(refused.Bytes, 0).Nodes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refunding_an_ordinary_bridge_is_refused_before_payment_but_a_leaf_can_be_refunded(bool goldRespec)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 10);
        Assert.Null((await game.Change([39725, 24377, 35568], allocate: true)).Error);
        game.Stash.AddGold(1_000_000);
        uint gold = game.Stash.Gold;
        string before = game.SavedJson;
        var snapshot = game.State.SavedPassiveProgress;
        var refused = await game.Change([39725], allocate: false, refundPoints: !goldRespec, goldRespec: goldRespec);
        Assert.NotNull(refused.Error);
        Assert.Equal(before, game.SavedJson);
        Assert.Same(snapshot, game.State.SavedPassiveProgress);
        Assert.Equal(gold, game.Stash.Gold);
        Assert.Equal((ushort)20, game.State.RefundPoints);
        Assert.Equal(new ushort[] { 24377, 35568, 39725 }, PassivePacket(refused.Bytes, 3).Nodes);

        long leafCost = Program.Passives.GoldRespecCost([35568], 10);
        Assert.True(leafCost > 0);
        var refunded = await game.Change([35568], allocate: false, refundPoints: !goldRespec, goldRespec: goldRespec);
        Assert.Null(refunded.Error);
        Assert.Equal(new ushort[] { 39725, 24377 }, game.State.Passives);
        Assert.Equal(new ushort[] { 39725, 24377 }, game.Reload().Passives);
        Assert.Equal(goldRespec ? (ushort)20 : (ushort)19, game.State.RefundPoints);
        Assert.Equal(goldRespec ? gold - (uint)leafCost : gold, game.Stash.Gold);
    }

    [Fact]
    public async Task A_complete_gold_respec_keeps_ascendancy_nodes_and_charges_only_ordinary_nodes()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(nodes: [54114], level: 10);
        ushort[] ordinary = [39725, 24377, 35568];
        Assert.Null((await game.Change(ordinary, allocate: true)).Error);
        game.Stash.AddGold(1_000_000);
        uint before = game.Stash.Gold;
        long cost = Program.Passives.GoldRespecCost(ordinary, 10);
        Assert.True(cost > 0);
        var refunded = await game.Change(ordinary, allocate: false, goldRespec: true);
        Assert.Null(refunded.Error);
        Assert.Empty(game.State.Passives);
        Assert.Equal(new ushort[] { 54114 }, game.State.MainAscendancy.Allocated);
        Assert.Equal((ushort)20, game.State.RefundPoints);
        Assert.Equal(before - (uint)cost, game.Stash.Gold);
        Assert.Equal(new ushort[] { 54114 }, PassivePacket(refunded.Bytes, 1).Nodes);
        Assert.Empty(game.Reload().Passives);
    }

    [Fact]
    public void Loading_a_disconnected_ordinary_tree_refuses_it_without_rewriting_the_saved_json()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 20);
        var originalRuntime = game.State.SavedPassiveProgress;
        var saved = CharacterSaves.Load(game.Character)!;
        uint gold = game.Stash.Gold;
        CharacterSaves.Write(game.Character, saved with { Passives = [24377], Gold = 123 });
        string before = game.SavedJson;
        Assert.Throws<AscendancyRuleException>(() => game.Reload());
        Assert.Equal(before, game.SavedJson);
        Assert.Equal(gold, game.Stash.Gold); // Validation also precedes migration of legacy character gold.
        Assert.Same(originalRuntime, game.State.SavedPassiveProgress);
        Assert.Empty(game.State.Passives);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Level_trimming_preserves_connectivity_when_a_valid_batch_was_allocated_in_reverse_order(bool reload)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 10);
        // A complete candidate is connected regardless of the order the client/dev command sends its nodes.
        Assert.Null((await game.Change([35568, 24377, 39725], allocate: true)).Error);
        ClientState after;
        if (reload)
        {
            CharacterSaves.SetExperience(game.Character, CharacterStats.ExperienceFor(3));
            after = game.Reload();
        }
        else
        {
            await game.SetLevel(3);
            after = game.State;
        }
        Assert.Equal(3, after.Pools.Level);
        Assert.Equal(new ushort[] { 24377, 39725 }, after.Passives.Order());
        Assert.Equal(new ushort[] { 24377, 39725 }, CharacterSaves.Load(game.Character)!.Passives!.Order());
        Assert.Equal(new ushort[] { 24377, 39725 }, game.Reload().Passives.Order());
    }

    [Fact]
    public async Task An_Ascendant_extra_start_cannot_be_refunded_while_it_anchors_ordinary_allocations()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 2, baseClass: 0, choice: "Ascendant",
            family: AscendancyFamily.Normal, level: 20);
        ushort[] path = [193, 33875, 61437, 30690, 24755]; // Path of the Marauder.
        Assert.Null((await game.Change(path, allocate: true)).Error);
        Assert.Null((await game.Change([31628], allocate: true)).Error); // Adjacent to the Marauder's implicit start.
        string before = game.SavedJson;
        var refused = await game.Change([24755], allocate: false, refundPoints: true);
        Assert.NotNull(refused.Error);
        Assert.Equal(before, game.SavedJson);
        Assert.Equal((ushort)20, game.State.RefundPoints);
        Assert.Equal(new ushort[] { 31628 }, game.Reload().Passives);
        Assert.Null((await game.Change([31628], allocate: false, refundPoints: true)).Error);
        Assert.Null((await game.Change([24755], allocate: false, refundPoints: true)).Error);
        Assert.Equal((ushort)14, game.State.RefundPoints); // One ordinary point plus five for the ascendancy node.
        Assert.DoesNotContain((ushort)24755, game.Reload().MainAscendancy.Allocated);
    }

    [Fact]
    public async Task Inherited_Fortune_cannot_be_refunded_while_its_granted_points_are_spent_and_payment_commits_with_nodes()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter();
        Assert.Null((await game.Change([54114, 48846], allocate: true)).Error);
        ushort[] ordinary = OrdinaryDuelistNodes(6);
        Assert.Null((await game.Change(ordinary, allocate: true)).Error);
        string before = game.SavedJson;
        var refused = await game.Change([48846], allocate: false, refundPoints: true);
        Assert.Contains("ordinary passives", refused.Error!);
        Assert.Equal(before, game.SavedJson);
        Assert.Equal((ushort)20, game.State.RefundPoints);
        Assert.Equal(8, PassivePacket(refused.Bytes, 8).Nodes.Length);

        Assert.Null((await game.Change(ordinary[1..], allocate: false, refundPoints: true)).Error);
        Assert.Equal((ushort)15, game.State.RefundPoints); // five ordinary passives, one regret each.
        var refunded = await game.Change([48846], allocate: false, refundPoints: true);
        Assert.Null(refunded.Error);
        Assert.Equal((ushort)10, game.State.RefundPoints); // the ascendancy notable costs five regrets.
        Assert.Equal(new ushort[] { 54114 }, game.State.MainAscendancy.Allocated);
        Assert.Equal(new[] { ordinary[0] }, game.State.Passives);
        var saved = CharacterSaves.Load(game.Character)!;
        Assert.Equal((ushort)10, saved.RefundPoints);
        Assert.Equal(new ushort[] { 54114 }, saved.MainAscendancy!.Allocated);
        Assert.Equal(new[] { ordinary[0] }, saved.Passives);
        var packet = PassivePacket(refunded.Bytes, 2);
        Assert.Equal((ushort)10, packet.RefundPoints);
        Assert.Equal((ushort)8, packet.AscendancyPoints); // earned points, not the unspent count.
    }

    [Fact]
    public async Task A_failed_save_keeps_the_runtime_and_durable_allocation_and_emits_no_success_packets()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(nodes: [54114]);
        string before = game.SavedJson;
        var progress = game.State.MainAscendancy;
        var snapshot = game.State.SavedPassiveProgress;
        var pools = game.State.Pools;
        Saves.FailWrite = name => name == game.Character;
        try { await Assert.ThrowsAsync<IOException>(() => game.Change([48846], allocate: true)); }
        finally { Saves.FailWrite = null; }
        Assert.Equal(before, game.SavedJson);
        Assert.Same(progress, game.State.MainAscendancy);
        Assert.Same(snapshot, game.State.SavedPassiveProgress);
        Assert.Equal(pools, game.State.Pools);
        Assert.Empty(game.State.Passives);
        Assert.Equal((ushort)20, game.State.RefundPoints);
        Assert.Empty(game.LastBytes);
    }

    [Fact]
    public async Task A_stale_area_cannot_publish_an_allocation_over_a_newer_connections_save()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter();
        var stale = game.Reload();
        Assert.Null((await game.Change([54114], allocate: true)).Error);
        string winner = game.SavedJson;
        await Assert.ThrowsAsync<PassiveProgressConflictException>(() => game.Change([54114], allocate: true, state: stale));
        Assert.Equal(winner, game.SavedJson);
        Assert.Empty(stale.MainAscendancy.Allocated);
        Assert.Equal(new ushort[] { 54114 }, game.State.MainAscendancy.Allocated);
        Assert.Empty(game.LastBytes);
    }

    [Fact]
    public async Task A_free_choice_effect_still_costs_five_regrets_to_change_in_the_actual_gateway()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 2, baseClass: 0, choice: "Scavenger");
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        var option = definition.Nodes.Values.First(n => n.IsChoiceOption);
        ushort parent = option.Adjacent.Single(n => definition.Nodes[n].IsChoice);
        ushort[] path = PathTo(definition, option.Id);
        Assert.Null((await game.Change(path, allocate: true)).Error);
        int spent = Program.AscendancyRules.SpentPoints(0, game.State.MainAscendancy);
        Assert.Null((await game.Change([option.Id], allocate: false, refundPoints: true)).Error);
        Assert.Equal((ushort)15, game.State.RefundPoints);
        Assert.Equal(spent, Program.AscendancyRules.SpentPoints(0, game.State.MainAscendancy));
        Assert.Null((await game.Change([parent], allocate: false, refundPoints: true)).Error);
        Assert.Equal((ushort)15, game.State.RefundPoints);
        Assert.Equal(spent - 1, Program.AscendancyRules.SpentPoints(0, game.State.MainAscendancy));
    }

    [Fact]
    public async Task Ascendancy_refunds_require_enough_refund_currency_and_cannot_be_paid_with_gold()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(nodes: [54114], refundPoints: 4);
        string before = game.SavedJson;
        var shortOfRegrets = await game.Change([54114], allocate: false, refundPoints: true);
        Assert.Contains("not enough refund points", shortOfRegrets.Error!);
        Assert.Equal(before, game.SavedJson);
        Assert.Equal((ushort)4, PassivePacket(shortOfRegrets.Bytes, 1).RefundPoints);
        var gold = await game.Change([54114], allocate: false, goldRespec: true);
        Assert.Contains("require refund points", gold.Error!);
        Assert.Equal(before, game.SavedJson);
        var free = await game.Change([54114], allocate: false);
        Assert.Contains("require refund points", free.Error!);
        Assert.Equal(before, game.SavedJson);
        Assert.Equal(new ushort[] { 54114 }, game.State.MainAscendancy.Allocated);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("node")]
    public void Loading_unknown_saved_ascendancy_data_stops_without_dropping_it(string problem)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter();
        var originalRuntime = game.State.MainAscendancy;
        var invalid = problem == "revision"
            ? originalRuntime with { Selection = originalRuntime.Selection! with { Revision = "future-client" } }
            : originalRuntime with { Nodes = [0] };
        CharacterSaves.SetMainAscendancy(game.Character, invalid);
        string before = game.SavedJson;
        Assert.Throws<AscendancyRuleException>(() => game.Reload());
        Assert.Equal(before, game.SavedJson);
        Assert.Same(originalRuntime, game.State.MainAscendancy);
    }

    [Theory]
    [InlineData("Metadata/Items/Currency/CurrencyPassiveRefund", 1)]
    [InlineData("Metadata/Items/QuestItems/SkillBooks/Book-a1q8", 2)]
    public async Task Using_a_regret_item_refreshes_the_snapshot_before_the_next_passive_change(string typeId, int points)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(nodes: [54114], refundPoints: (ushort)(5 - points));
        var type = Program.Items.Find(typeId)!;
        Assert.NotNull(type);
        ushort stack = type.MaxStack > 1 ? (ushort)2 : (ushort)1;
        var item = game.Items.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(type) with { StackSize = stack });
        var use = await game.UseItem(item.Id);
        Assert.Equal((ushort)5, game.State.RefundPoints);
        Assert.Equal((ushort)5, CharacterSaves.Load(game.Character)!.RefundPoints);
        Assert.Equal((ushort)5, PassivePacket(use.Bytes, 1).RefundPoints);
        if (stack == 1) Assert.DoesNotContain(game.Items.Items(PlayerInventories.MainInventory), i => i.Id == item.Id);
        else Assert.Equal((ushort)1, game.Items.Items(PlayerInventories.MainInventory).Single(i => i.Id == item.Id).Item.StackSize);

        // Both types of next request use the refreshed compare-and-swap snapshot. With the old snapshot the first
        // allocation disconnects the player even though the regret item and its points were saved successfully.
        Assert.Null((await game.Change([48846], allocate: true)).Error);
        Assert.Null((await game.Change(OrdinaryDuelistNodes(1), allocate: true)).Error);
        Assert.Null((await game.Change([48846], allocate: false, refundPoints: true)).Error);
        Assert.Equal((ushort)0, game.State.RefundPoints);
        Assert.Equal((ushort)0, CharacterSaves.Load(game.Character)!.RefundPoints);
        Assert.Equal(new ushort[] { 54114 }, game.State.MainAscendancy.Allocated);
    }

    [Theory]
    [InlineData("Metadata/Items/Currency/CurrencyPassiveRefund", 1)]
    [InlineData("Metadata/Items/QuestItems/SkillBooks/Book-a1q8", 2)]
    public async Task An_old_areas_regret_item_adds_to_the_latest_balance_and_cannot_restore_refunded_nodes(string typeId, int points)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(nodes: [54114, 48846]);
        var newArea = game.Reload();
        Assert.Null((await game.Change([54114, 48846], allocate: false, refundPoints: true, state: newArea)).Error);
        Assert.Equal((ushort)10, CharacterSaves.Load(game.Character)!.RefundPoints);
        Assert.Equal((ushort)20, game.State.RefundPoints); // the connection being left still has its old copy.
        var item = game.Items.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(Program.Items.Find(typeId)!));
        await game.UseItem(item.Id);
        var saved = CharacterSaves.Load(game.Character)!;
        Assert.Equal((ushort)(10 + points), saved.RefundPoints);
        Assert.Equal((ushort)(10 + points), game.State.RefundPoints);
        Assert.Empty(saved.MainAscendancy!.Allocated);
        Assert.DoesNotContain(game.Items.Items(PlayerInventories.MainInventory), i => i.Id == item.Id);

        string afterItem = game.SavedJson;
        await Assert.ThrowsAsync<PassiveProgressConflictException>(() => game.Change([24607], allocate: true));
        Assert.Equal(afterItem, game.SavedJson);
        Assert.Empty(CharacterSaves.Load(game.Character)!.MainAscendancy!.Allocated);
    }

    [Fact]
    public async Task Regret_cap_checks_use_the_latest_save_instead_of_an_old_connections_balance()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(refundPoints: ushort.MaxValue);
        var type = Program.Items.Find("Metadata/Items/Currency/CurrencyPassiveRefund")!;
        var orb = game.Items.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(type) with { StackSize = 2 });
        CharacterSaves.SetRefundPoints(game.Character, 10);
        await game.UseItem(orb.Id);
        Assert.Equal((ushort)11, game.State.RefundPoints); // an old maximum must not prevent using a now-needed orb.
        Assert.Equal((ushort)11, CharacterSaves.Load(game.Character)!.RefundPoints);
        Assert.Equal((ushort)1, game.Items.Items(PlayerInventories.MainInventory).Single().Item.StackSize);

        CharacterSaves.SetRefundPoints(game.Character, ushort.MaxValue);
        string atCap = game.SavedJson;
        await game.UseItem(orb.Id);
        Assert.Equal(atCap, game.SavedJson); // an old non-maximum must not consume an orb at the durable cap.
        Assert.Equal((ushort)1, game.Items.Items(PlayerInventories.MainInventory).Single().Item.StackSize);
        Assert.Empty(game.LastBytes);
    }

    [Fact]
    public async Task Failed_regret_payment_rolls_back_consumption_and_does_not_publish_a_new_balance_or_snapshot()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(nodes: [54114, 48846]);
        var newArea = game.Reload();
        Assert.Null((await game.Change([54114, 48846], allocate: false, refundPoints: true, state: newArea)).Error);
        var orb = game.Items.Add(PlayerInventories.MainInventory, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Metadata/Items/Currency/CurrencyPassiveRefund")!) with { StackSize = 2 });
        string before = game.SavedJson;
        var snapshot = game.State.SavedPassiveProgress;
        int writes = 0;
        Saves.FailWrite = name => name == game.Character && Interlocked.Increment(ref writes) == 2;
        try { await Assert.ThrowsAsync<IOException>(() => game.UseItem(orb.Id)); }
        finally { Saves.FailWrite = null; }
        Assert.Equal(before, game.SavedJson);
        Assert.Equal((ushort)10, CharacterSaves.Load(game.Character)!.RefundPoints);
        Assert.Equal((ushort)20, game.State.RefundPoints);
        Assert.Same(snapshot, game.State.SavedPassiveProgress);
        Assert.Equal((ushort)2, game.Items.Items(PlayerInventories.MainInventory).Single().Item.StackSize);
        Assert.Empty(game.LastBytes);
    }

    [Fact]
    public async Task Native_allocate_and_paid_refund_dispatch_use_the_ascendancy_budget()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter();
        await game.Dispatch(new LoginWriter().U16(0x34).U32(54114).U8(0).ToArray());
        Assert.Equal(new ushort[] { 54114 }, PassivePacket(game.LastBytes, 1).Nodes);
        await game.Dispatch(new LoginWriter().U16(0x36).U32(54114).U8(0).U8(0).ToArray());
        Assert.Empty(game.State.MainAscendancy.Allocated);
        Assert.Equal((ushort)15, game.State.RefundPoints);
        Assert.Equal((ushort)15, CharacterSaves.Load(game.Character)!.RefundPoints);
        Assert.Empty(PassivePacket(game.LastBytes, 0).Nodes);
    }

    [Theory]
    [InlineData(1u, (byte)0)]
    [InlineData(0u, (byte)1)]
    public async Task Native_unsupported_allocation_ids_cannot_be_interpreted_as_mastery_effects(uint high, byte expansion)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 10);
        string before = game.SavedJson;
        await game.Dispatch(new LoginWriter().U16(0x34).U32(high << 16 | 39725).U8(expansion).ToArray());
        Assert.Equal(before, game.SavedJson);
        Assert.Empty(PassivePacket(game.LastBytes, 0).Nodes);
    }

    [Fact]
    public async Task Native_mastery_request_does_not_swallow_the_next_allocation_in_the_same_read()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(level: 10);
        ushort effect = Program.Passives.EffectsFor(42533)[0].Hash;
        byte[] mastery = new LoginWriter().U16(0x35).U16(42533).U16(effect).ToArray();
        byte[] allocate = new LoginWriter().U16(0x34).U32(39725).U8(0).ToArray();
        await game.Dispatch([.. mastery, .. allocate]);
        Assert.Empty(game.State.MasteryEffects); // This distant mastery is disconnected.
        Assert.Equal(new ushort[] { 39725 }, game.State.Passives);
        Assert.Equal(new ushort[] { 39725 }, PassivePacket(game.LastBytes, 1).Nodes);
    }

    [Fact]
    public async Task Phrecia_command_and_connection_preflight_keep_the_saved_character_intact()
    {
        if (!DataAvailable()) return;
        using var ordinary = new GatewayCharacter(family: AscendancyFamily.Normal, choice: "Slayer");
        Program.CheckAscendancyClientCompatibility(ordinary.State);
        string before = ordinary.SavedJson;
        await ordinary.Command("/ascendancy phrecia Aristocrat");
        Assert.Equal(before, ordinary.SavedJson);
        Assert.Equal(AscendancyFamily.Normal, ordinary.State.MainAscendancy.Selection!.Family);
        Assert.Null(ordinary.State.PendingTransfer);

        using var phrecia = new GatewayCharacter(); // Lifecycle fixture; does not open a real game connection.
        before = phrecia.SavedJson;
        Assert.Throws<AscendancyRuleException>(() => Program.CheckAscendancyClientCompatibility(phrecia.State));
        Assert.Equal(before, phrecia.SavedJson);
    }

    [Fact]
    public void An_old_unascended_save_loads_without_rewriting_unknown_fields_or_adding_a_selection()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter();
        var saved = CharacterSaves.Load(game.Character)!;
        CharacterSaves.Write(game.Character, saved with { MainAscendancy = null, ExtraPassivePoints = 3,
            Unknown = new() { ["FutureField"] = JsonSerializer.SerializeToElement(new[] { 1, 2 }) } });
        string before = game.SavedJson;
        Program.CheckAscendancyClientCompatibility(game.State);
        var loaded = game.Reload();
        Assert.Null(loaded.MainAscendancy.Selection);
        Assert.Empty(loaded.MainAscendancy.Allocated);
        Assert.Equal((ushort)3, loaded.ExtraPassivePoints);
        Assert.Equal(before, game.SavedJson);
    }

    [Fact]
    public void Native_family_stat_is_absent_for_ordinary_and_unascended_characters()
    {
        Assert.Null(Program.LeagueStatsFor(new()));
        Assert.Null(Program.LeagueStatsFor(new(new(AscendancyFamily.Normal, "Slayer", AscendancyCatalog.Revision))));
        var stats = Program.LeagueStatsFor(new(new(AscendancyFamily.Phrecia, "Slayer", AscendancyCatalog.Revision)))!;
        Assert.Equal(1, Assert.Single(stats).Value);
        Assert.Equal(21146u, Assert.Single(stats).Key);
    }

    static bool DataAvailable()
    {
        // Data-backed tests can run in a fresh clone. When the extraction is present, execute the integration path.
        return File.Exists(Path.Combine("data", AscendancyCatalog.GraphFile)) && File.Exists("data/tables/PassiveSkills.json");
    }

    static ushort[] OrdinaryDuelistNodes(int count)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine("data", AscendancyCatalog.GraphFile)));
        var graph = document.RootElement.GetProperty("normal").GetProperty("nodes").EnumerateArray()
            .ToDictionary(n => checked((ushort)n.GetProperty("id").GetInt32()),
                n => n.GetProperty("adjacent").EnumerateArray().Select(a => checked((ushort)a.GetInt32())).ToArray());
        var result = new List<ushort>();
        var visited = new HashSet<ushort> { 50986 }; // actual Duelist start, shared with Aristocrat's start connection.
        var queue = new Queue<ushort>();
        queue.Enqueue(50986);
        while (queue.TryDequeue(out ushort current) && result.Count < count)
            foreach (ushort next in graph[current])
                if (Program.Passives.Find(next) is { IsAscendancy: false, IsStart: false, MasteryGroup: < 0 } && visited.Add(next))
                {
                    result.Add(next);
                    queue.Enqueue(next);
                    if (result.Count == count) break;
                }
        Assert.Equal(count, result.Count);
        return result.ToArray();
    }

    static ushort[] PathTo(AscendancyCatalog.Definition definition, ushort end)
    {
        var queue = new Queue<(ushort Id, ushort[] Path)>();
        var seen = new HashSet<ushort> { definition.StartNode };
        queue.Enqueue((definition.StartNode, []));
        while (queue.TryDequeue(out var current))
        {
            if (current.Id == end) return current.Path;
            foreach (ushort next in definition.Nodes[current.Id].Adjacent)
                if (seen.Add(next)) queue.Enqueue((next, [.. current.Path, next]));
        }
        throw new InvalidDataException($"No path to {end}");
    }

    static TreePacket PassivePacket(byte[] stream, int nodeCount)
    {
        int length = 22 + nodeCount * 2; // these scenarios have no selected ordinary mastery effects.
        Assert.True(stream.Length >= length);
        var packet = stream.AsSpan(stream.Length - length);
        Assert.Equal((ushort)0x33, BinaryPrimitives.ReadUInt16BigEndian(packet));
        Assert.Equal(nodeCount, BinaryPrimitives.ReadUInt16BigEndian(packet[12..]));
        var nodes = new ushort[nodeCount];
        for (int i = 0; i < nodeCount; i++) nodes[i] = BinaryPrimitives.ReadUInt16LittleEndian(packet[(14 + i * 2)..]);
        return new(BinaryPrimitives.ReadUInt16BigEndian(packet[2..]), BinaryPrimitives.ReadUInt16BigEndian(packet[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(packet[6..]), packet[8], packet[9], nodes);
    }

    sealed record TreePacket(ushort RefundPoints, ushort QuestPoints, ushort AscendancyPoints, byte Level, byte ClassByte, ushort[] Nodes);

    static T ProgramValue<T>(string name, params object?[] args) => (T)typeof(Program)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;

    sealed partial class GatewayCharacter : IDisposable
    {
        static readonly MethodInfo Load = GatewayMethod("LoadProgress");
        static readonly MethodInfo ChangeMethod = GatewayMethod("ChangePassives");
        static readonly MethodInfo UseMethod = GatewayMethod("UseItem");
        static readonly MethodInfo SetLevelMethod = GatewayMethod("SetLevel");
        readonly int _classRow;
        readonly bool _god;
        readonly bool _hardcore;
        readonly bool _ruthless;
        public string Character { get; } = $"AscInt{Guid.NewGuid():N}"[..22];
        public ClientState State { get; }
        public byte[] LastBytes { get; private set; } = [];
        public string SavedJson => Saves.Get(GameServer.Storage.SaveDatabase.Table.Characters, Character)!;
        public PlayerInventories Items => PlayerInventories.For(Character, Program.Items);
        public AccountStash Stash => AccountStash.For(AccountStash.Key(State.Account ?? Character, State.League), Program.Items);

        static MethodInfo GatewayMethod(string name) => typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == name || m.Name.Contains($"g__{name}|", StringComparison.Ordinal));

        public GatewayCharacter(IReadOnlyList<string>? awards = null, IReadOnlyList<ushort>? nodes = null,
            int classRow = 4, byte baseClass = 4, string choice = "Aristocrat", ushort refundPoints = 20, int level = 1,
            bool god = false, AscendancyFamily family = AscendancyFamily.Phrecia,
            bool hardcore = false, bool ruthless = false)
        {
            TestSaves.Ensure();
            _classRow = classRow;
            _god = god;
            _hardcore = hardcore;
            _ruthless = ruthless;
            Assert.NotNull(Program.Stats.Key("active_skill_base_area_of_effect_radius"));
            var definition = Program.Ascendancies.Find(baseClass, family, choice)!;
            Assert.NotNull(definition);
            CharacterSaves.Write(Character, new([], Experience: CharacterStats.ExperienceFor(level), RefundPoints: refundPoints,
                MainAscendancy: new(definition.Identity, nodes ?? [], awards ?? AscendancyRules.AwardKeys)));
            State = Reload();
            Assert.Equal(level, State.Pools.Level);
        }

        public ClientState Reload()
        {
            var state = new ClientState(new(0, "AscendancyIntegration", 128, 128, 32, 32, null, []), 0, 1)
            { Character = Character, ClassRow = _classRow, God = _god, Hardcore = _hardcore, Ruthless = _ruthless };
            try { Load.Invoke(null, [state, 1]); }
            catch (TargetInvocationException e) when (e.InnerException is not null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
            (state.PlayerLife, state.PlayerMana) = (state.Pools.Life, state.Pools.Mana);
            return state;
        }

        public Task<(string? Error, byte[] Bytes)> Dispatch(byte[] bytes) => Exchange(async (stream, cipher) =>
        {
            var messages = InstanceProtocol.SplitClientMessages(bytes, out var partial, out var unknown);
            Assert.Empty(partial);
            Assert.Empty(unknown);
            await Program.OnClientMessages("ascendancy-integration", stream, cipher, messages, unknown, State);
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> Command(string line) => Exchange(async (stream, cipher) =>
        {
            State.Commands = ProgramValue<ChatCommands>("Commands", "ascendancy-integration", stream, cipher, State);
            Assert.True(await State.Commands.RunAsync(line));
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> UseItem(uint itemId) => Exchange(async (stream, cipher) =>
        {
            await (Task)UseMethod.Invoke(null, ["ascendancy-integration", stream, cipher, State,
                new InstanceItems.UseItemRequest(PlayerInventories.InventoryId(PlayerInventories.MainInventory), itemId), "integration test"])!;
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> Change(ushort[] nodes, bool allocate, bool refundPoints = false,
            ClientState? state = null, bool goldRespec = false, ushort? masteryEffect = null) => Exchange((stream, cipher) =>
            (Task<string?>)ChangeMethod.Invoke(null,
                ["ascendancy-integration", stream, cipher, state ?? State, nodes, allocate, "integration test", masteryEffect, goldRespec, refundPoints])!);

        public Task<(string? Error, byte[] Bytes)> SetLevel(int level) => Exchange(async (stream, cipher) =>
        {
            await (Task)SetLevelMethod.Invoke(null, ["ascendancy-integration", stream, cipher, State, level])!;
            return null;
        });

        async Task<(string? Error, byte[] Bytes)> Exchange(Func<NetworkStream, ConnectionCiphers, Task<string?>> work)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using var server = await accept;
            using var capture = new MemoryStream();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reading = client.GetStream().CopyToAsync(capture, timeout.Token);
            byte[] key = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            var serverCipher = new ConnectionCiphers(key, isClient: false);
            var clientCipher = new ConnectionCiphers(key, isClient: true);
            string? error;
            try
            {
                var task = work(server.GetStream(), serverCipher);
                error = await task.WaitAsync(timeout.Token);
            }
            finally
            {
                server.Client.Shutdown(SocketShutdown.Send);
                await reading;
                LastBytes = capture.ToArray();
                clientCipher.Receive.Process(LastBytes);
            }
            return (error, LastBytes);
        }

        public void Dispose()
        {
            PlayerInventories.Forget(Character);
            AccountStash.Forget(AccountStash.Key(State.Account ?? Character, State.League));
        }
    }
}
