using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

// The Allflame run in an encounter area (DeepwaterRun): made at the first entry of an instance, its lanterns shown again
// at every later entry, placements from the client's ruleset actions, light and drowning each tick (DeepwaterLight).
public sealed partial class DeepwaterModule
{
    DeepwaterRun? RunOf(Player player) =>
        player.Instance is { } instance && _runs.TryGetValue(instance, out var run) && DeepwaterGrid.IsEncounter(PlayerOf(player).AreaId) ? run : null;

    async Task StartRun(IModuleContext module, Player player)
    {
        var modPlayer = PlayerOf(player);
        // Dredge left by a run that ended another way (a crash, a relog): gone, unless this is that run, still going.
        if (RunOf(player) is not { Phase: not DeepwaterRun.RunPhase.Ended } && DeepwaterDredge.Ids(player).Count > 0)
            await DeepwaterDredge.Remove(module, player, live: true, "found at the next entry");
        if (!DeepwaterGrid.IsEncounter(modPlayer.AreaId))
        {
            // A crash or a relog in a run left its flags on.
            var on = DeepwaterConstants.RunFlags.Where(player.Character.HasQuestFlag).ToList();
            // The Sovereign's owner keeps IsMapOwner (DeepwaterHub).
            if (modPlayer.AreaId == HubId) await SyncHub(module, player, on);
            if (on.Count > 0) await SetQuestFlags(player, on, on: false);
            // A chart left active by a crash or a relog (no run waits for it on this connection) goes back (G).
            if (ItemsOf(player).Items(DeepwaterLocker.ActiveRow).Count > 0 && (!_states.TryGetValue(modPlayer, out var deck) || deck.Armed is null))
                await ReturnActive(module, player, "no run is waiting for it");
            return;
        }
        if (player.Instance is not { } instance) return;
        bool made = false;
        DeepwaterRun? run;
        DeepwaterRun.Lantern? first = null;
        List<MapStrongboxes.SpawnRequest>? pendingStrongboxes = null;
        List<MapEssences.MonolithRequest>? pendingMonoliths = null;
        lock (_runs)
        {
            if (!_runs.TryGetValue(instance, out run))
            {
                if (DeepwaterBoard.Of(player.Layout.Level, module.Log) is not { } board)
                {
                    module.Log($"{modPlayer.Character}: {modPlayer.AreaId}/{modPlayer.Seed} has no generated Deepwater board; no run");
                    return;
                }
                bool voyage = modPlayer.AreaId == DeepwaterConstants.VoyageArea;
                // A run started from the deck plays its plan (DeepwaterStart): its owner holds the lanterns, at its level.
                var planned = PlanOf(instance.Map);
                if (planned is not null && planned.Size != board.Size)
                {
                    module.Log($"{modPlayer.Character}: the plan's {planned.Size}×{planned.Size} board generated as {board.Size}×{board.Size}; its cell effects are not applied");
                }
                int level = planned?.Level ?? Math.Clamp((int)player.Pools.Level, 1, DeepwaterMonsters.MaxLevel);
                var cursedEffects = planned is not null
                    ? DeepwaterCursedDucats.EffectsFor(planned.Global, new Random((int)(player.Seed ^ 0x43555253u)))
                    : DeepwaterCursedDucats.EffectsFor(new Dictionary<uint, int>());
                var effects = planned is { Voyage: true } && planned.Size == board.Size
                    ? CellEffects(planned, level, cursedEffects.MonsterStats)
                    : default;
                var cellRules = planned is not null && planned.Size == board.Size
                    ? DeepwaterChartStats.BuildFor(planned)
                    : default;
                var runMods = DeepwaterMonsters.ModsFor(level, null, cursedEffects.MonsterStats);
                var (cellRareDucat, cellRareDucatChance) = DeepwaterLoot.RoomRareDucats(board);
                run = new DeepwaterRun
                {
                    Voyage = voyage, Board = board, Controller = planned?.Owner ?? modPlayer.Character,
                    Total = planned is { LanternTotal: > 0 } ? planned.LanternTotal : DeepwaterLanterns.Total(player, voyage),
                    Level = level, Plan = planned,
                    CellMods = effects.Mods, CellQuantity = effects.Quantity, CellRarity = effects.Rarity,
                    CursedQuantityFactor = cursedEffects.QuantityFactor, CursedRarityFactor = cursedEffects.RarityFactor,
                    RunMods = runMods, CellRareDucat = cellRareDucat, CellRareDucatChance = cellRareDucatChance,
                    CellRules = cellRules.Rules, HasAnyCellDrops = cellRules.HasDrops,
                    HasAnyExpBonus = cellRules.HasExp, HasAnyRareSulphur = cellRules.HasRareSulphur,
                };
                if (planned is not null) run.OnEnd = (members, success) => Finish(module, planned, members, success);
                var plan = DeepwaterMonsters.Plan(player.Layout, player.Seed, run, module.Log);
                run.Planned.AddRange(plan);
                run.PlannedPacks = plan.Count;
                (pendingStrongboxes, pendingMonoliths) = DeepwaterLoot.Plan(player, run, module.Log);
                // The first lantern burns at the start Bathysphere (cell 0's "bathysphere" point; the wiki: placed on entry)
                // and is not one of the run's (G: the tutorial's count starts at 6 with it already lit). Without an anchor
                // the client allows no placement.
                var start = board.Cells[0].Point("bathysphere") ?? board.Cells[0].Point("default") ?? (player.Layout.SpawnX, player.Layout.SpawnY);
                first = DeepwaterLanterns.TryAdd(player, run, start.X, start.Y, counts: false, out _, out _);
                DeepwaterExtraction.Plan(player, run, start, module.Log);
                DeepwaterVelka.Plan(player, run, module.Log);
                _runs.Add(instance, run);
                made = true;
            }
        }
        if (made)
        {
            module.Log($"{modPlayer.Character}: Allflame {(run.Voyage ? "Voyage" : "Chart")} in {modPlayer.AreaId}/{modPlayer.Seed}, {run.Board.Size}×{run.Board.Size}, {run.Total} lanterns, level {run.Level}"
                + (run.Plan is null ? " (no plan: a random grid)" : $" (planned by {run.Plan.Owner})"));
            if (pendingStrongboxes is { Count: > 0 })
                await MapStrongboxes.SpawnAt(player, pendingStrongboxes, new Random(unchecked((int)(player.Seed ^ 0x44575342))));
            if (pendingMonoliths is { Count: > 0 })
                await MapEssences.SpawnMonolithsAt(player, pendingMonoliths, new Random(unchecked((int)(player.Seed ^ 0x44574553))));
        }
        foreach (var lantern in run.Snapshot()) await DeepwaterLanterns.Show(module, player, run, lantern, dropping: made);
        await DeepwaterLanterns.SendCount(player, run);
        if (run.CollapseState != 0 && DeepwaterRuleset.Collapse(run.CollapseState) is { } collapse)
            await modPlayer.Send(collapse, $"0x02c9 ruleset DeepwaterRules mode 3: {run.CollapseState} (sync)");
        await DeepwaterLoot.Show(player, run);
        await DeepwaterExtraction.Show(player, run);
        await DeepwaterVelka.Show(player, run);
        // The first lantern's light wakes the packs around the start and collects the Sulphur there.
        if (first is not null)
        {
            await DeepwaterMonsters.Release(module, player, run, first);
            await DeepwaterLoot.Collect(module, player, run, first);
        }
        // The run's owner holds the lanterns (QuestFlags 3550, the client's placement gate) and is its map owner (2020,
        // the capsule window's gate; G that the owner is the one). Other players get neither (G: the
        // TransferLanternControlObject that hands the lanterns over is not built).
        if (modPlayer.Character.Equals(run.Controller, StringComparison.OrdinalIgnoreCase) && run.Phase != DeepwaterRun.RunPhase.Ended)
        {
            await SetQuestFlags(player, [DeepwaterConstants.LanternController, DeepwaterConstants.MapOwner]);
            bool firstOwnerEntry;
            lock (run.Gate)
            {
                firstOwnerEntry = !run.OwnerEntered;
                run.OwnerEntered = true;
            }
            // The Maiden Voyage's tracker: a Chart or a Voyage from the deck, at its owner's first entry (G: the triggers, R1 §6).
            if (firstOwnerEntry && run.Plan is { } planned)
            {
                if (planned.Voyage) await SetQuestFlags(player, [DeepwaterConstants.VoyageExplore]);
                else
                {
                    await SetQuestFlags(player, [DeepwaterConstants.PlaceLanterns]);
                    await SetQuestFlags(player, [DeepwaterConstants.EnterTheChart], on: false);
                    if (!HasLeagueFlag(player, DeepwaterConstants.FirstChartEnteredLeagueFlag))
                        await SetLeagueQuestFlags(player, [DeepwaterConstants.FirstChartEnteredLeagueFlag]);
                }
            }
        }
        await DeepwaterExtraction.SyncFlags(player, run);
        await DeepwaterCursedDucats.SyncFlags(player);
        await DeepwaterProgression.SyncQuestItemFlags(player);
    }

    /// <summary>What each cell of a Voyage adds on its own, precomputed once so the per-kill and per-spawn paths only index
    /// arrays: the monsters' map mods with the level's more life and Cursed Ducat stats, and the drops' quantity and rarity
    /// factors (the cell's increased quantity and rarity, and the border mods' more rarity, G).</summary>
    static (MonsterModSet?[]? Mods, double[]? Quantity, double[]? Rarity) CellEffects(DeepwaterPlan plan, int level,
        IReadOnlyDictionary<uint, int>? cursedMonsterStats)
    {
        int count = plan.CellStats.Count;
        var mods = new MonsterModSet?[count];
        var quantity = new double[count];
        var rarity = new double[count];
        for (int i = 0; i < count; i++)
        {
            var map = new MapMods(plan.CellStats[i]);
            mods[i] = DeepwaterMonsters.ModsFor(level, global::Game.MapMonsterEffects(map), cursedMonsterStats);
            quantity[i] = Math.Max(0, 100 + map.Quantity) / 100.0;
            rarity[i] = Math.Max(0, 100 + map.Rarity) / 100.0 * Math.Max(0, 100 + map.Get(Stat.ChartItemDropRarityPlusPctFinalFromBorderModifier)) / 100.0;
        }
        return (mods, quantity, rarity);
    }

    async Task<bool> RulesetAction(IModuleContext module, Player player, byte[] bytes)
    {
        (DeepwaterRuleset.Op Op, int X, int Y)? action;
        try { action = DeepwaterRuleset.Read(bytes); }
        catch (InvalidDataException) { return false; }
        if (action is not { } a) return false;
        var modPlayer = PlayerOf(player);
        if (RunOf(player) is not { } run)
        {
            module.Log($"{modPlayer.Character} <= 0x02ca DeepwaterRules {a.Op}: no run here");
            return true;
        }
        switch (a.Op)
        {
            case DeepwaterRuleset.Op.PlaceLantern:
                if (DeepwaterLanterns.Refusal(player, run, (a.X, a.Y), out var at, out string note) is { } why)
                {
                    // The count is sent again so the client stays in step with the server (it changes only with mode 2).
                    module.Log($"{modPlayer.Character} <= 0x02ca DeepwaterRules PlaceLantern ({a.X}, {a.Y}) refused: {why}");
                    await DeepwaterLanterns.SendCount(player, run);
                    return true;
                }
                await PlaceLantern(module, player, run, at, $"<= 0x02ca DeepwaterRules PlaceLantern cursor ({a.X}, {a.Y}) -> ({at.X}, {at.Y})", note, manual: true);
                return true;
            case DeepwaterRuleset.Op.SendItemsToSurface:
                await DeepwaterExtraction.SendToSurface(module, player, run);
                return true;
            case DeepwaterRuleset.Op.RequestExtraction:
                await DeepwaterExtraction.CallBathysphere(module, player, run);
                return true;
            default:
                module.Log($"{modPlayer.Character} <= 0x02ca DeepwaterRules op {(byte)a.Op}: unknown");
                return true;
        }
    }

    async Task PlaceLantern(IModuleContext module, Player player, DeepwaterRun run, (int X, int Y) at, string source, string note = "", bool manual = false)
    {
        if (DeepwaterLanterns.TryAdd(player, run, at.X, at.Y, counts: true, out int placed, out int total) is not { } lantern)
        {
            if (manual) await DeepwaterLanterns.SendCount(player, run);
            return;
        }
        module.Log($"{PlayerOf(player).Character} {source}: lantern {placed}/{total}{note}");
        foreach (var member in DeepwaterLoot.Members(player))
        {
            await DeepwaterLanterns.Show(module, member, run, lantern, dropping: true);
            await DeepwaterLanterns.SendCount(member, run);
        }
        await DeepwaterMonsters.Release(module, player, run, lantern);
        await DeepwaterLoot.Collect(module, player, run, lantern);
        if (placed == 1 && !player.Character.HasQuestFlag(DeepwaterConstants.FirstLanternPlaced))
            await SetQuestFlags(player, [DeepwaterConstants.FirstLanternPlaced]);
        if (placed == total)
        {
            await DeepwaterUpgrades.GrantFinalLanternAdrenaline(module, player);
            // A Chart's last lantern: the lanterns collapse and its Bathysphere can be used (QuestFlags 3562).
            if (!run.Voyage) await ChartCollapse(module, player, run);
        }
    }

    async Task TickAutoLantern(IModuleContext module, Player player, DeepwaterRun run)
    {
        if (DeepwaterLanterns.TryAutoLantern(player, run, AreaTime(player), out string note) is not { } at) return;
        await PlaceLantern(module, player, run, at, $"Auto-Lanterns -> ({at.X}, {at.Y})", note, manual: false);
    }

    // The Chart's collapse: every lantern shrinks, the Bathysphere's own one (the first) only to the floor radius.
    static async Task ChartCollapse(IModuleContext module, Player player, DeepwaterRun run)
    {
        uint? first;
        lock (run.Gate)
        {
            if (run.Phase != DeepwaterRun.RunPhase.Exploring) return;
            run.Phase = DeepwaterRun.RunPhase.Collapsing;
            first = run.Lanterns.FirstOrDefault(l => l.Order == 0)?.Id;
        }
        await DeepwaterCollapse.Start(module, player, run, _ => true, first);
        foreach (var member in DeepwaterLoot.Members(player))
        {
            // The tracker's "return and extract" (QuestFlags 3570) for whoever had its "place lanterns".
            await SetQuestFlags(member, member.Character.HasQuestFlag(DeepwaterConstants.PlaceLanterns)
                ? [DeepwaterConstants.CanExtract, DeepwaterConstants.ReturnToSurface] : [DeepwaterConstants.CanExtract]);
        }
    }

    static DeepwaterExtraction.Device[] DevicesOf(DeepwaterRun run) { lock (run.Gate) return [.. run.Devices]; }

    // A run that failed (a death, drowning, leaving another way) for everyone in it.
    Task RunFailed(IModuleContext module, Player player, string reason) =>
        RunOf(player) is { } run ? DeepwaterExtraction.Fail(module, player, run, reason) : Task.CompletedTask;

    static JsonObject PackState(DeepwaterRun run)
    {
        lock (run.Gate)
            return new JsonObject
            {
                ["planned"] = run.PlannedPacks, ["released"] = run.ReleasedPacks, ["waiting"] = run.Planned.Count,
                ["monstersReleased"] = run.ReleasedMonsters,
            };
    }

    void RunState(Player player, JsonObject json)
    {
        if (RunOf(player) is not { } run) return;
        double now = AreaTime(player);
        var at = player.Walk.Position(now);
        int placed, total;
        lock (run.Gate) (placed, total) = (run.Placed, run.Total);
        var cell = run.Board.CellAt(at);
        json["deepwater"] = new JsonObject
        {
            ["kind"] = run.Voyage ? "voyage" : "chart",
            ["phase"] = run.Phase.ToString(),
            ["controller"] = run.Controller,
            ["placed"] = placed,
            ["total"] = total,
            ["cell"] = cell?.Index,
            ["biome"] = cell?.BiomeArea,
            ["onDeck"] = run.Board.OnDeck(at),
            ["inLight"] = run.InLight(at, now),
            ["level"] = run.Level,
            ["packs"] = PackState(run),
            ["loot"] = DeepwaterLoot.State(run),
            ["drowningLeft"] = _light.Drowning(player, out double left) ? Math.Round(left, 2) : null,
            ["collapse"] = run.CollapseState,
            ["outcome"] = run.Outcome,
            ["landIn"] = run.LandAt > 0 ? Math.Round(Math.Max(0, run.LandAt - now), 1) : null,
            ["dropStage"] = run.LandAt > 0 ? run.DropStage : null,
            ["devices"] = new JsonArray([.. DevicesOf(run).Select(d => (JsonNode)new JsonObject
            {
                ["id"] = d.Id, ["kind"] = d.Kind.ToString(), ["x"] = d.X, ["y"] = d.Y, ["landed"] = d.Landed, ["sent"] = d.Sent, ["called"] = d.Called, ["entry"] = d.Entry, ["retired"] = d.Retired,
            })]),
            ["capsule"] = ItemsOf(player).Items(DeepwaterExtraction.CapsuleRow).Count,
            ["collection"] = ItemsOf(player).Items(DeepwaterExtraction.CollectionRow).Count,
            ["swarmPacks"] = run.SwarmPacks,
            ["lanterns"] = new JsonArray([.. run.Snapshot().Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.Id, ["x"] = l.X, ["y"] = l.Y, ["radius"] = Math.Round(l.Radius(now), 1),
            })]),
        };
        if (DeepwaterVelka.State(run) is { } velka && json["deepwater"] is JsonObject dw)
            dw["velka"] = velka;
    }
}
