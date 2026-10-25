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
public sealed class AscendancyGatewayIntegrationTests : IDisposable
{
    readonly string _previousDirectory = Directory.GetCurrentDirectory();

    public AscendancyGatewayIntegrationTests()
    {
        // Program's catalogs intentionally use repository-relative paths. xUnit starts in the test binary folder.
        // This collection runs outside every parallel collection, so moving CWD cannot affect another running test.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "data", AscendancyCatalog.GraphFile)))
            {
                Directory.SetCurrentDirectory(directory.FullName);
                break;
            }
    }

    public void Dispose() => Directory.SetCurrentDirectory(_previousDirectory);

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

    [Fact]
    public async Task Aristocrat_changes_the_equipped_gems_effective_level_and_quality_and_restores_them_on_refund()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(refundPoints: 60);
        var (fireballType, variant) = Program.Items.FindGem("Fireball")!.Value;
        var fireball = ItemCatalog.Create(fireballType) with { GemExperience = 0, Quality = 0, GemVariant = variant.Index };
        var helmetType = Program.Items.Find("Iron Hat")!;
        var helmet = game.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(helmetType) with
        {
            Sockets = [new ItemSocket(SocketColour.Blue, fireball)], Links = [0],
        });
        var before = game.GemEffects().Single(g => g.Effect == Program.Skills.EffectRow("Fireball"));
        Assert.Equal((1, 0), (before.Level, before.Quality));
        Assert.Equal(new DamageRange(9, 14), Program.Skills.HitDamage(before.Effect, before.Level, before.Quality)[DamageType.Fire]);
        Assert.Equal(10, game.SkillRadius(before));

        ushort[] path = [54114, 48846, 24607, 46836, 27473, 30851];
        Assert.Null((await game.Change(path, allocate: true)).Error);
        var enhanced = game.GemEffects().Single(g => g.Effect == before.Effect);
        Assert.Equal((2, 20), (enhanced.Level, enhanced.Quality)); // Family Heirlooms +1, Centerpiece +20.
        Assert.Equal(new DamageRange(10, 16), Program.Skills.HitDamage(enhanced.Effect, enhanced.Level, enhanced.Quality)[DamageType.Fire]);
        Assert.Equal(13, game.SkillRadius(enhanced)); // 150 per mille per quality: 20 * 150 / 1000 = +3 cells.
        var intrinsic = game.Items.Items(PlayerInventories.Helmet).Single(i => i.Id == helmet.Id).Item.Sockets.Single().Gem!;
        Assert.Equal(1, Program.Items.GemLevel(intrinsic));
        Assert.Equal((byte)0, intrinsic.Quality);
        var savedGem = CharacterSaves.Load(game.Character)!.Inventories[PlayerInventories.Helmet].Single().Item.Sockets!.Single().Gem!;
        Assert.Equal(0, savedGem.GemExperience);
        Assert.Equal((byte)0, savedGem.Quality);

        Assert.Null((await game.Change(path, allocate: false, refundPoints: true)).Error);
        var restored = game.GemEffects().Single(g => g.Effect == before.Effect);
        Assert.Equal((1, 0), (restored.Level, restored.Quality));
        Assert.Equal(new DamageRange(9, 14), Program.Skills.HitDamage(restored.Effect, restored.Level, restored.Quality)[DamageType.Fire]);
        Assert.Equal(10, game.SkillRadius(restored));
        Assert.Equal((byte)0, game.Items.Items(PlayerInventories.Helmet).Single().Item.Sockets.Single().Gem!.Quality);
    }

    [Fact]
    public async Task Already_running_Clarity_and_Precision_refresh_when_ascendancy_gem_modifiers_change()
    {
        if (!DataAvailable()) return;
        // The ordinary /god state supplies attributes/mana so this fixture can activate both level-one gems legally.
        // Their aura bonuses remain independent of that fixed background and are checked against exact client rows.
        using var game = new GatewayCharacter(refundPoints: 60, level: 100, god: true);
        var clarity = Program.Items.FindGem("Clarity")!.Value;
        var precision = Program.Items.FindGem("Precision")!.Value;
        game.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets =
            [
                new ItemSocket(SocketColour.Blue, ItemCatalog.Create(clarity.Type) with { GemVariant = clarity.Variant.Index }),
                new ItemSocket(SocketColour.Green, ItemCatalog.Create(precision.Type) with { GemVariant = precision.Variant.Index }),
            ],
            Links = [0, 1],
        });
        await game.ToggleAura("Clarity");
        await game.ToggleAura("Precision");
        Assert.Equal(2, game.State.Auras.Count);
        var identities = game.State.Auras.Values.ToDictionary(a => a.EffectId, a => (a.SkillId, a.Buff, a.SourceBuff));
        ushort nextBuff = game.State.NextBuff;
        var savedIntent = CharacterSaves.Load(game.Character)!.ActiveAuras!.Order().ToArray();
        Assert.Equal(new[] { "Clarity", "Precision" }, savedIntent);

        void CheckRunningAuras(int level, int quality, int clarityMana, int precisionMana, int clarityRegen, int precisionAccuracy, int criticalChance)
        {
            Assert.Equal(2, game.State.Auras.Count);
            Assert.Equal(nextBuff, game.State.NextBuff);
            foreach (var aura in game.State.Auras.Values)
            {
                Assert.Equal((level, quality), (aura.Level, aura.Quality));
                Assert.Equal(identities[aura.EffectId], (aura.SkillId, aura.Buff, aura.SourceBuff));
                Assert.Equal(aura.EffectId == "Clarity" ? clarityMana : precisionMana, aura.ManaFlat);
                Assert.Equal(0, aura.ManaPercent);
            }
            Assert.Equal(clarityMana + precisionMana, AuraCatalog.RequiredMana(game.State.Pools, game.State.Auras.Values));
            Assert.Equal(2 * game.State.Pools.Level + 2 * game.State.Pools.Dexterity + precisionAccuracy, game.State.Pools.Accuracy);
            Assert.Equal(criticalChance, game.State.Pools.CriticalStrikeChancePct);
            int flatRegen = Program.GodRegenPerSecond * 60 + clarityRegen;
            Assert.Equal(flatRegen, game.PlayerStat("base_mana_regeneration_rate_per_minute"));
            Assert.Equal((game.State.Pools.Mana * 1.05 + flatRegen) / 60, game.ManaRegenPerSecond(), precision: 6);
            Assert.Equal(savedIntent, CharacterSaves.Load(game.Character)!.ActiveAuras!.Order());
        }

        CheckRunningAuras(1, 0, 34, 22, 176, 93, 20);
        Assert.Null((await game.Change([54114, 48846, 24607, 46836], allocate: true)).Error);
        CheckRunningAuras(2, 0, 48, 32, 266, 128, 22);
        Assert.Null((await game.Change([27473, 30851], allocate: true)).Error);
        CheckRunningAuras(2, 20, 48, 32, 266, 128, 22);
        Assert.Null((await game.Change([54114, 48846, 24607, 46836, 27473, 30851], allocate: false, refundPoints: true)).Error);
        CheckRunningAuras(1, 0, 34, 22, 176, 93, 20);
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
    public async Task Running_Righteous_Fire_refreshes_its_cached_radius_without_replacing_its_state()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(refundPoints: 60, level: 100, god: true);
        var rfGem = Program.Items.FindGem("Righteous Fire")!.Value;
        game.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets = [new ItemSocket(SocketColour.Blue, ItemCatalog.Create(rfGem.Type) with
            {
                GemExperience = Program.Items.Gem(rfGem.Type)!.ExperienceFor(19), GemVariant = rfGem.Variant.Index,
            })],
            Links = [0],
        });
        var intrinsic = game.GemEffects().Single(g => g.Name == "Righteous Fire");
        Assert.Equal(19, intrinsic.Level);
        Assert.Equal(38, game.SkillRadius(intrinsic)); // base 22 cells, with /god's fixed 200% increased area.
        var running = new RighteousFireState(7, 8, null, intrinsic.Id, 4200, 5400, 38);
        running.Auras[0x4000] = 9;
        game.State.RighteousFire = running;
        game.State.NextBuff = 10;
        var ringGate = running.RingGate;

        ushort[] path = [54114, 48846, 24607, 46836, 27473, 30851];
        Assert.Null((await game.Change(path[..4], allocate: true)).Error);
        Assert.Same(running, game.State.RighteousFire);
        Assert.Equal(39, running.Radius); // intrinsic gem 19 gains level 20's base radius 23.
        Assert.Null((await game.Change(path[4..], allocate: true)).Error);
        Assert.Same(running, game.State.RighteousFire);
        Assert.Equal(44, running.Radius); // quality's +3 base radius is scaled by the same fixed area modifier.
        Assert.Equal((20, 20), game.GemEffects().Where(g => g.Name == "Righteous Fire").Select(g => (g.Level, g.Quality)).Single());
        Assert.Equal(4200, running.DamagePctPerMinute);
        Assert.Equal(5400, running.BurnPctPerMinute);
        Assert.Equal((ushort)7, running.Buff);
        Assert.Equal((ushort)8, running.SelfAura);
        Assert.Equal((ushort)9, running.Auras[0x4000]);
        Assert.Same(ringGate, running.RingGate);
        Assert.Equal((ushort)10, game.State.NextBuff);

        Assert.Null((await game.Change(path, allocate: false, refundPoints: true)).Error);
        Assert.Same(running, game.State.RighteousFire);
        Assert.Equal(38, running.Radius);
        Assert.Equal((19, 0), game.GemEffects().Where(g => g.Name == "Righteous Fire").Select(g => (g.Level, g.Quality)).Single());
        Assert.Equal((ushort)9, running.Auras[0x4000]);
        Assert.Null(running.Ring); // no visual/background loops are started by this retained-state regression.
    }

    [Fact]
    public async Task Centerpiece_expands_the_actual_Fireball_impact_targets_without_hitting_the_primary_twice()
    {
        if (!DataAvailable()) return;
        using var projectileSettings = new ProjectileTestSettings();
        using var game = new GatewayCharacter(refundPoints: 60);
        var fireball = Program.Items.FindGem("Fireball")!.Value;
        game.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets = [new ItemSocket(SocketColour.Blue, ItemCatalog.Create(fireball.Type) with { GemVariant = fireball.Variant.Index })], Links = [0],
        });
        var primary = new SpawnedMonster("primary", new WalkEstimator(100, 100, 0), 0, 0, 100);
        var outer = new SpawnedMonster("outer", new WalkEstimator(96, 112, 0), 0, 0, 100);
        game.State.Monsters[0x4000] = primary;
        game.State.Monsters[0x4001] = outer;
        var hits = new Dictionary<SpawnedMonster, int>();
        (int, bool) Damage(SpawnedMonster target)
        {
            hits[target] = hits.GetValueOrDefault(target) + 1;
            return (1, false);
        }
        async Task CheckTargets(int radius, bool hitsOuter)
        {
            primary.Life = outer.Life = 100;
            hits.Clear();
            await game.FireballImpact(radius, Damage);
            Assert.Equal(1, hits.GetValueOrDefault(primary));
            Assert.Equal(hitsOuter ? 1 : 0, hits.GetValueOrDefault(outer));
            Assert.Equal(99u, primary.Life);
            Assert.Equal(hitsOuter ? 99u : 100u, outer.Life);
        }

        var gem = game.GemEffects().Single(g => g.Name == "Fireball");
        Assert.Equal(10, game.SkillRadius(gem));
        await CheckTargets(game.SkillRadius(gem), hitsOuter: false);
        ushort[] path = [54114, 48846, 24607, 46836, 27473, 30851];
        Assert.Null((await game.Change(path, allocate: true)).Error);
        gem = game.GemEffects().Single(g => g.Name == "Fireball");
        Assert.Equal(13, game.SkillRadius(gem));
        await CheckTargets(game.SkillRadius(gem), hitsOuter: true);
        Assert.Null((await game.Change(path, allocate: false, refundPoints: true)).Error);
        gem = game.GemEffects().Single(g => g.Name == "Fireball");
        await CheckTargets(game.SkillRadius(gem), hitsOuter: false);
    }

    [Theory]
    [InlineData(0, 1, "Behemoth", 49937)]
    [InlineData(1, 3, "Herald", 20602)]
    [InlineData(6, 5, "Polytheist", 5266)]
    public async Task Ascendancy_skill_restrictions_suspend_and_restore_active_auras_without_losing_saved_intent(
        int classRow, int baseClass, string ascendancy, int notable)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: classRow, baseClass: (byte)baseClass, choice: ascendancy,
            refundPoints: 60, level: 100, god: true);
        EquipBlueGems(game, "Clarity");
        await game.ToggleAura("Clarity");
        Assert.Single(game.State.Auras);
        Assert.Equal(new[] { "Clarity" }, CharacterSaves.Load(game.Character)!.ActiveAuras);
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        ushort[] path = PathTo(definition, (ushort)notable);
        Assert.Null((await game.Change(path, allocate: true)).Error);
        Assert.Empty(game.State.Auras);
        Assert.Equal(new[] { "Clarity" }, CharacterSaves.Load(game.Character)!.ActiveAuras);
        uint mana = game.State.PlayerMana;
        Assert.False((await game.Cast("Clarity")).Accepted);
        Assert.Equal(mana, game.State.PlayerMana);
        Assert.Empty(game.State.SkillReadyAt); // refusal precedes reservation, cooldown and payment.
        await game.ToggleAura("Clarity"); // even the lower-level entry point must not bypass the restriction.
        Assert.Empty(game.State.Auras);
        Assert.Equal(new[] { "Clarity" }, CharacterSaves.Load(game.Character)!.ActiveAuras);

        Assert.Null((await game.Change(path, allocate: false, refundPoints: true)).Error);
        var restored = Assert.Single(game.State.Auras.Values);
        Assert.Equal("Clarity", restored.EffectId);
        Assert.Equal((1, 0, 34), (restored.Level, restored.Quality, restored.ManaFlat));
        Assert.Equal(new[] { "Clarity" }, CharacterSaves.Load(game.Character)!.ActiveAuras);
    }

    [Fact]
    public async Task Devout_of_Gruthkul_refuses_actual_spell_casts_before_cost_or_projectile_creation()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 6, baseClass: 5, choice: "Polytheist", refundPoints: 60, level: 100, god: true);
        EquipBlueGems(game, "Fireball", "Clarity");
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        ushort[] path = PathTo(definition, 5266);
        Assert.Null((await game.Change(path, allocate: true)).Error);
        uint mana = game.State.PlayerMana, nextObject = game.State.NextObjectId;
        Assert.False((await game.Cast("Fireball")).Accepted);
        Assert.Equal(mana, game.State.PlayerMana);
        Assert.Equal(nextObject, game.State.NextObjectId);
        Assert.Null(game.State.RighteousFire);
        Assert.Empty(game.State.SkillReadyAt);

        Assert.Null((await game.Change(path, allocate: false, refundPoints: true)).Error);
        Assert.True((await game.Cast("Clarity")).Accepted); // the real cast path works again after refund.
        Assert.Equal("Clarity", Assert.Single(game.State.Auras.Values).EffectId);
        Assert.Equal(new[] { "Clarity" }, CharacterSaves.Load(game.Character)!.ActiveAuras);
    }

    [Fact]
    public async Task Rampager_allows_spell_casts_and_costs_but_their_actual_projectiles_deal_no_damage()
    {
        if (!DataAvailable()) return;
        using var projectileSettings = new ProjectileTestSettings();
        using var game = new GatewayCharacter(classRow: 0, baseClass: 1, choice: "Behemoth", refundPoints: 60, level: 100, god: true);
        EquipBlueGems(game, "Fireball");
        var primary = new SpawnedMonster("spell target", new WalkEstimator(52, 32, 0), 0, 0, 1000);
        game.State.Monsters[0x4000] = primary;
        uint mana = game.State.PlayerMana;
        Assert.True((await game.Cast("Fireball")).Accepted);
        Assert.Equal(mana - 5, game.State.PlayerMana);
        Assert.True(primary.Life < 1000); // the fixture hits through the real LaunchProjectile damage closure.

        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        ushort[] path = PathTo(definition, 49937);
        Assert.Null((await game.Change(path, allocate: true)).Error);
        primary.Life = 1000;
        mana = game.State.PlayerMana;
        Assert.True((await game.Cast("Fireball")).Accepted); // "deal no damage" does not disable utility spell casts.
        Assert.Equal(mana - 5, game.State.PlayerMana);
        Assert.Equal(1000u, primary.Life);

        Assert.Null((await game.Change(path, allocate: false, refundPoints: true)).Error);
        mana = game.State.PlayerMana;
        Assert.True((await game.Cast("Fireball")).Accepted);
        Assert.Equal(mana - 5, game.State.PlayerMana);
        Assert.True(primary.Life < 1000);
    }

    // These integration tests are in a nonparallel collection. Preserve developer projectile overrides while using a
    // short deterministic flight and the known collision geometry; the real damage/target paths still execute.
    sealed class ProjectileTestSettings : IDisposable
    {
        readonly Dictionary<string, string?> _previous = new();
        public ProjectileTestSettings()
        {
            foreach (var (key, value) in new[]
            {
                ("Game_PROJECTILE_SPEED", "100000"), ("Game_PROJECTILE_RANGE", "120"), ("Game_PROJECTILE_AHEAD", "4"),
                ("Game_PROJECTILE_HIT_RADIUS", "4"), ("Game_PROJECTILE_IMPACT_AO", "none"),
            })
            {
                _previous[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }
        public void Dispose()
        {
            foreach (var (key, value) in _previous) Environment.SetEnvironmentVariable(key, value);
        }
    }

    static void EquipBlueGems(GatewayCharacter game, params string[] names)
    {
        game.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets = names.Select(name =>
            {
                var gem = Program.Items.FindGem(name)!.Value;
                return new ItemSocket(SocketColour.Blue, ItemCatalog.Create(gem.Type) with { GemVariant = gem.Variant.Index });
            }).ToArray(),
            Links = names.Select((_, i) => checked((byte)i)).ToArray(),
        });
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

    sealed record TreePacket(ushort RefundPoints, ushort QuestPoints, ushort AscendancyPoints, byte Level, byte ClassByte, ushort[] Nodes);

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

    sealed class GatewayCharacter : IDisposable
    {
        static readonly MethodInfo Load = GatewayMethod("LoadProgress");
        static readonly MethodInfo ChangeMethod = GatewayMethod("ChangePassives");
        static readonly MethodInfo UseMethod = GatewayMethod("UseItem");
        static readonly MethodInfo EffectsMethod = GatewayMethod("GemSkillEffects");
        static readonly MethodInfo RadiusMethod = GatewayMethod("SkillRadius");
        static readonly MethodInfo ToggleAuraMethod = GatewayMethod("ToggleAura");
        static readonly MethodInfo PlayerStatsMethod = GatewayMethod("CharacterPlayerStats");
        static readonly MethodInfo ManaRegenMethod = GatewayMethod("ManaRegenPerSecond");
        static readonly MethodInfo FlyProjectileMethod = GatewayMethod("FlyProjectile");
        static readonly MethodInfo CastMethod = GatewayMethod("CastGemSkill");
        readonly int _classRow;
        readonly bool _god;
        public string Character { get; } = $"AscInt{Guid.NewGuid():N}"[..22];
        public ClientState State { get; }
        public byte[] LastBytes { get; private set; } = [];
        public string SavedJson => Saves.Get(GameServer.Storage.SaveDatabase.Table.Characters, Character)!;
        public PlayerInventories Items => PlayerInventories.For(Character, Program.Items);

        static MethodInfo GatewayMethod(string name) => typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == name || m.Name.Contains($"g__{name}|", StringComparison.Ordinal));

        public GatewayCharacter(IReadOnlyList<string>? awards = null, IReadOnlyList<ushort>? nodes = null,
            int classRow = 4, byte baseClass = 4, string choice = "Aristocrat", ushort refundPoints = 20, int level = 1, bool god = false)
        {
            TestSaves.Ensure();
            _classRow = classRow;
            _god = god;
            var definition = Program.Ascendancies.Find(baseClass, AscendancyFamily.Phrecia, choice)!;
            Assert.NotNull(definition);
            CharacterSaves.Write(Character, new([], Experience: CharacterStats.ExperienceFor(level), RefundPoints: refundPoints,
                MainAscendancy: new(definition.Identity, nodes ?? [], awards ?? AscendancyRules.AwardKeys)));
            State = Reload();
        }

        public ClientState Reload()
        {
            var state = new ClientState(new(0, "AscendancyIntegration", 128, 128, 32, 32, null, []), 0, 1)
            { Character = Character, ClassRow = _classRow, God = _god };
            try { Load.Invoke(null, [state, 1]); }
            catch (TargetInvocationException e) when (e.InnerException is not null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
            (state.PlayerLife, state.PlayerMana) = (state.Pools.Life, state.Pools.Mana);
            return state;
        }

        public IReadOnlyList<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)> GemEffects() =>
            ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)EffectsMethod.Invoke(null, [State])!).ToArray();

        public int SkillRadius((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem)
        {
            return (int)RadiusMethod.Invoke(null, [State, true, gem.Effect, gem.Level, gem.Quality])!;
        }

        public int PlayerStat(string stat) => ((Dictionary<uint, int>)PlayerStatsMethod.Invoke(null, [State])!)
            .GetValueOrDefault(Program.Stats.Key(stat)!.Value);

        public double ManaRegenPerSecond() => (double)ManaRegenMethod.Invoke(null, [State])!;

        public Task<(string? Error, byte[] Bytes)> ToggleAura(string name) => Exchange(async (stream, cipher) =>
        {
            var gem = GemEffects().Single(g => g.Name == name);
            var reservation = Program.Skills.Reservation(gem.Effect, gem.Level)!.Value;
            await (Task)ToggleAuraMethod.Invoke(null,
                ["ascendancy-integration", stream, cipher, State, gem.Id, gem.Effect, gem.Name, reservation, gem.Level, gem.Quality])!;
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> FireballImpact(int radius, Func<SpawnedMonster, (int Damage, bool Critical)> damage) =>
            Exchange(async (stream, cipher) =>
            {
                await (Task)FlyProjectileMethod.Invoke(null, ["ascendancy-integration", stream, cipher, State,
                    ("Metadata/Projectiles/Fireball", 100_000), (80, 100), (100, 100), damage, false, "none", radius])!;
                return null;
            });

        public async Task<(bool Accepted, byte[] Bytes)> Cast(string name)
        {
            bool accepted = false;
            var result = await Exchange(async (stream, cipher) =>
            {
                var gem = GemEffects().Single(g => g.Name == name);
                accepted = await (Task<bool>)CastMethod.Invoke(null,
                    ["ascendancy-integration", stream, cipher, State, gem.Id, (32, 32), (48, 32), 0u, (ushort)0])!;
                return null;
            });
            return (accepted, result.Bytes);
        }

        public Task<(string? Error, byte[] Bytes)> UseItem(uint itemId) => Exchange(async (stream, cipher) =>
        {
            await (Task)UseMethod.Invoke(null, ["ascendancy-integration", stream, cipher, State,
                new InstanceItems.UseItemRequest(PlayerInventories.InventoryId(PlayerInventories.MainInventory), itemId), "integration test"])!;
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> Change(ushort[] nodes, bool allocate, bool refundPoints = false,
            ClientState? state = null, bool goldRespec = false) => Exchange((stream, cipher) =>
            (Task<string?>)ChangeMethod.Invoke(null,
                ["ascendancy-integration", stream, cipher, state ?? State, nodes, allocate, "integration test", null, goldRespec, refundPoints])!);

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

        public void Dispose() => PlayerInventories.Forget(Character);
    }
}
