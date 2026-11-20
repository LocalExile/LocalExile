using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

// The Allflame run in an encounter area (DeepwaterRun): made at the first entry of an instance, its lanterns shown again
// at every later entry, placements from the client's ruleset actions, light and drowning each tick (DeepwaterLight).
public sealed partial class DeepwaterModule
{
    DeepwaterRun? RunOf(ClientState conn) =>
        conn.Instance is { } instance && _runs.TryGetValue(instance, out var run) && DeepwaterGrid.IsEncounter(PlayerOf(conn).AreaId) ? run : null;

    async Task StartRun(IModuleContext module, ClientState conn)
    {
        var player = PlayerOf(conn);
        var (remote, stream, ciphers) = conn.Wire;
        // Dredge left by a run that ended another way (a crash, a relog): gone, unless this is that run, still going.
        if (RunOf(conn) is not { Phase: not DeepwaterRun.RunPhase.Ended } && DeepwaterDredge.Ids(conn).Count > 0)
            await DeepwaterDredge.Remove(module, conn, live: true, "found at the next entry");
        if (!DeepwaterGrid.IsEncounter(player.AreaId))
        {
            // A crash or a relog in a run left its flags on.
            var on = DeepwaterConstants.RunFlags.Where(conn.HasQuestFlag).ToList();
            // The Sovereign's owner keeps IsMapOwner (DeepwaterHub).
            if (player.AreaId == HubId) await SyncHub(module, conn, on);
            if (on.Count > 0) await SetQuestFlags(remote, stream, ciphers, conn, on, on: false);
            // A chart left active by a crash or a relog (no run waits for it on this connection) goes back (G).
            if (ItemsOf(conn).Items(DeepwaterLocker.ActiveRow).Count > 0 && (!_states.TryGetValue(player, out var deck) || deck.Armed is null))
                await ReturnActive(module, conn, "no run is waiting for it");
            return;
        }
        if (conn.Instance is not { } instance) return;
        bool made = false;
        DeepwaterRun? run;
        DeepwaterRun.Lantern? first = null;
        List<MapStrongboxes.SpawnRequest>? pendingStrongboxes = null;
        List<MapEssences.MonolithRequest>? pendingMonoliths = null;
        lock (_runs)
        {
            if (!_runs.TryGetValue(instance, out run))
            {
                if (DeepwaterBoard.Of(conn.Layout.Level, module.Log) is not { } board)
                {
                    module.Log($"{player.Character}: {player.AreaId}/{player.Seed} has no generated Deepwater board; no run");
                    return;
                }
                bool voyage = player.AreaId == DeepwaterConstants.VoyageArea;
                // A run started from the deck plays its plan (DeepwaterStart): its owner holds the lanterns, at its level.
                var planned = PlanOf(instance.Map);
                if (planned is not null && planned.Size != board.Size)
                {
                    module.Log($"{player.Character}: the plan's {planned.Size}×{planned.Size} board generated as {board.Size}×{board.Size}; its cell effects are not applied");
                }
                int level = planned?.Level ?? Math.Clamp((int)conn.Pools.Level, 1, DeepwaterMonsters.MaxLevel);
                var cursedEffects = planned is not null
                    ? DeepwaterCursedDucats.EffectsFor(planned.Global, new Random((int)(conn.Seed ^ 0x43555253u)))
                    : DeepwaterCursedDucats.EffectsFor(new Dictionary<string, int>());
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
                    Voyage = voyage, Board = board, Controller = planned?.Owner ?? player.Character,
                    Total = planned is { LanternTotal: > 0 } ? planned.LanternTotal : DeepwaterLanterns.Total(conn, voyage),
                    Level = level, Plan = planned,
                    CellMods = effects.Mods, CellQuantity = effects.Quantity, CellRarity = effects.Rarity,
                    CursedQuantityFactor = cursedEffects.QuantityFactor, CursedRarityFactor = cursedEffects.RarityFactor,
                    RunMods = runMods, CellRareDucat = cellRareDucat, CellRareDucatChance = cellRareDucatChance,
                    CellRules = cellRules.Rules, HasAnyCellDrops = cellRules.HasDrops,
                    HasAnyExpBonus = cellRules.HasExp, HasAnyRareSulphur = cellRules.HasRareSulphur,
                };
                if (planned is not null) run.OnEnd = (members, success) => Finish(module, planned, members, success);
                var plan = DeepwaterMonsters.Plan(conn.Layout, conn.Seed, run, module.Log);
                run.Planned.AddRange(plan);
                run.PlannedPacks = plan.Count;
                (pendingStrongboxes, pendingMonoliths) = DeepwaterLoot.Plan(conn, run, module.Log);
                // The first lantern burns at the start Bathysphere (cell 0's "bathysphere" point; the wiki: placed on entry)
                // and is not one of the run's (G: the tutorial's count starts at 6 with it already lit). Without an anchor
                // the client allows no placement.
                var start = board.Cells[0].Point("bathysphere") ?? board.Cells[0].Point("default") ?? (conn.Layout.SpawnX, conn.Layout.SpawnY);
                first = DeepwaterLanterns.TryAdd(conn, run, start.X, start.Y, counts: false, out _, out _);
                DeepwaterExtraction.Plan(conn, run, start, module.Log);
                DeepwaterVelka.Plan(conn, run, module.Log);
                _runs.Add(instance, run);
                made = true;
            }
        }
        if (made)
        {
            module.Log($"{player.Character}: Allflame {(run.Voyage ? "Voyage" : "Chart")} in {player.AreaId}/{player.Seed}, {run.Board.Size}×{run.Board.Size}, {run.Total} lanterns, level {run.Level}"
                + (run.Plan is null ? " (no plan: a random grid)" : $" (planned by {run.Plan.Owner})"));
            if (pendingStrongboxes is { Count: > 0 })
                await MapStrongboxes.SpawnAt(conn, pendingStrongboxes, new Random(unchecked((int)(conn.Seed ^ 0x44575342))));
            if (pendingMonoliths is { Count: > 0 })
                await MapEssences.SpawnMonolithsAt(conn, pendingMonoliths, new Random(unchecked((int)(conn.Seed ^ 0x44574553))));
        }
        foreach (var lantern in run.Snapshot()) await DeepwaterLanterns.Show(module, conn, run, lantern, dropping: made);
        await DeepwaterLanterns.SendCount(conn, run);
        if (run.CollapseState != 0 && DeepwaterRuleset.Collapse(run.CollapseState) is { } collapse)
            await player.Send(collapse, $"0x02c9 ruleset DeepwaterRules mode 3: {run.CollapseState} (sync)");
        await DeepwaterLoot.Show(conn, run);
        await DeepwaterExtraction.Show(conn, run);
        await DeepwaterVelka.Show(conn, run);
        // The first lantern's light wakes the packs around the start and collects the Sulphur there.
        if (first is not null)
        {
            await DeepwaterMonsters.Release(module, conn, run, first);
            await DeepwaterLoot.Collect(module, conn, run, first);
        }
        // The run's owner holds the lanterns (QuestFlags 3550, the client's placement gate) and is its map owner (2020,
        // the capsule window's gate; G that the owner is the one). Other players get neither (G: the
        // TransferLanternControlObject that hands the lanterns over is not built).
        if (player.Character.Equals(run.Controller, StringComparison.OrdinalIgnoreCase) && run.Phase != DeepwaterRun.RunPhase.Ended)
        {
            await SetQuestFlags(remote, stream, ciphers, conn, [DeepwaterConstants.LanternController, DeepwaterConstants.MapOwner]);
            bool firstOwnerEntry;
            lock (run.Gate)
            {
                firstOwnerEntry = !run.OwnerEntered;
                run.OwnerEntered = true;
            }
            // The Maiden Voyage's tracker: a Chart or a Voyage from the deck, at its owner's first entry (G: the triggers, R1 §6).
            if (firstOwnerEntry && run.Plan is { } planned)
            {
                if (planned.Voyage) await SetQuestFlags(remote, stream, ciphers, conn, [DeepwaterConstants.VoyageExplore]);
                else
                {
                    await SetQuestFlags(remote, stream, ciphers, conn, [DeepwaterConstants.PlaceLanterns]);
                    await SetQuestFlags(remote, stream, ciphers, conn, [DeepwaterConstants.EnterTheChart], on: false);
                    if (!HasLeagueFlag(conn, DeepwaterConstants.FirstChartEnteredLeagueFlag))
                        await SetLeagueQuestFlags(remote, stream, ciphers, conn, [DeepwaterConstants.FirstChartEnteredLeagueFlag]);
                }
            }
        }
        await DeepwaterExtraction.SyncFlags(conn, run);
        await DeepwaterCursedDucats.SyncFlags(conn);
        await DeepwaterProgression.SyncQuestItemFlags(conn);
    }

    /// <summary>What each cell of a Voyage adds on its own, precomputed once so the per-kill and per-spawn paths only index
    /// arrays: the monsters' map mods with the level's more life and Cursed Ducat stats, and the drops' quantity and rarity
    /// factors (the cell's increased quantity and rarity, and the border mods' more rarity, G).</summary>
    static (MonsterModSet?[]? Mods, double[]? Quantity, double[]? Rarity) CellEffects(DeepwaterPlan plan, int level,
        IReadOnlyDictionary<string, int>? cursedMonsterStats)
    {
        int count = plan.CellStats.Count;
        var mods = new MonsterModSet?[count];
        var quantity = new double[count];
        var rarity = new double[count];
        for (int i = 0; i < count; i++)
        {
            var map = new MapMods(DeepwaterPlan.Named(plan.CellStats[i]));
            mods[i] = DeepwaterMonsters.ModsFor(level, global::Game.MapMonsterEffects(map), cursedMonsterStats);
            quantity[i] = Math.Max(0, 100 + map.Quantity) / 100.0;
            rarity[i] = Math.Max(0, 100 + map.Rarity) / 100.0 * Math.Max(0, 100 + map.Get("chart_item_drop_rarity_+%_final_from_border_modifier")) / 100.0;
        }
        return (mods, quantity, rarity);
    }

    async Task<bool> RulesetAction(IModuleContext module, ClientState conn, byte[] bytes)
    {
        (DeepwaterRuleset.Op Op, int X, int Y)? action;
        try { action = DeepwaterRuleset.Read(bytes); }
        catch (InvalidDataException) { return false; }
        if (action is not { } a) return false;
        var player = PlayerOf(conn);
        if (RunOf(conn) is not { } run)
        {
            module.Log($"{player.Character} <= 0x02ca DeepwaterRules {a.Op}: no run here");
            return true;
        }
        switch (a.Op)
        {
            case DeepwaterRuleset.Op.PlaceLantern:
                if (DeepwaterLanterns.Refusal(conn, run, (a.X, a.Y), out var at, out string note) is { } why)
                {
                    // The count is sent again so the client stays in step with the server (it changes only with mode 2).
                    module.Log($"{player.Character} <= 0x02ca DeepwaterRules PlaceLantern ({a.X}, {a.Y}) refused: {why}");
                    await DeepwaterLanterns.SendCount(conn, run);
                    return true;
                }
                await PlaceLantern(module, conn, run, at, $"<= 0x02ca DeepwaterRules PlaceLantern cursor ({a.X}, {a.Y}) -> ({at.X}, {at.Y})", note, manual: true);
                return true;
            case DeepwaterRuleset.Op.SendItemsToSurface:
                await DeepwaterExtraction.SendToSurface(module, conn, run);
                return true;
            case DeepwaterRuleset.Op.RequestExtraction:
                await DeepwaterExtraction.CallBathysphere(module, conn, run);
                return true;
            default:
                module.Log($"{player.Character} <= 0x02ca DeepwaterRules op {(byte)a.Op}: unknown");
                return true;
        }
    }

    async Task PlaceLantern(IModuleContext module, ClientState conn, DeepwaterRun run, (int X, int Y) at, string source, string note = "", bool manual = false)
    {
        if (DeepwaterLanterns.TryAdd(conn, run, at.X, at.Y, counts: true, out int placed, out int total) is not { } lantern)
        {
            if (manual) await DeepwaterLanterns.SendCount(conn, run);
            return;
        }
        module.Log($"{PlayerOf(conn).Character} {source}: lantern {placed}/{total}{note}");
        foreach (var member in DeepwaterLoot.Members(conn))
        {
            await DeepwaterLanterns.Show(module, member, run, lantern, dropping: true);
            await DeepwaterLanterns.SendCount(member, run);
        }
        await DeepwaterMonsters.Release(module, conn, run, lantern);
        await DeepwaterLoot.Collect(module, conn, run, lantern);
        if (placed == 1 && !conn.HasQuestFlag(DeepwaterConstants.FirstLanternPlaced))
        {
            var (remote, stream, ciphers) = conn.Wire;
            await SetQuestFlags(remote, stream, ciphers, conn, [DeepwaterConstants.FirstLanternPlaced]);
        }
        if (placed == total)
        {
            await DeepwaterUpgrades.GrantFinalLanternAdrenaline(module, conn);
            // A Chart's last lantern: the lanterns collapse and its Bathysphere can be used (QuestFlags 3562).
            if (!run.Voyage) await ChartCollapse(module, conn, run);
        }
    }

    async Task TickAutoLantern(IModuleContext module, ClientState conn, DeepwaterRun run)
    {
        if (DeepwaterLanterns.TryAutoLantern(conn, run, conn.Clock.Elapsed.TotalSeconds, out string note) is not { } at) return;
        await PlaceLantern(module, conn, run, at, $"Auto-Lanterns -> ({at.X}, {at.Y})", note, manual: false);
    }

    // The Chart's collapse: every lantern shrinks, the Bathysphere's own one (the first) only to the floor radius.
    static async Task ChartCollapse(IModuleContext module, ClientState conn, DeepwaterRun run)
    {
        uint? first;
        lock (run.Gate)
        {
            if (run.Phase != DeepwaterRun.RunPhase.Exploring) return;
            run.Phase = DeepwaterRun.RunPhase.Collapsing;
            first = run.Lanterns.FirstOrDefault(l => l.Order == 0)?.Id;
        }
        await DeepwaterCollapse.Start(module, conn, run, _ => true, first);
        foreach (var member in DeepwaterLoot.Members(conn))
        {
            var (remote, stream, ciphers) = member.Wire;
            // The tracker's "return and extract" (QuestFlags 3570) for whoever had its "place lanterns".
            await SetQuestFlags(remote, stream, ciphers, member, member.HasQuestFlag(DeepwaterConstants.PlaceLanterns)
                ? [DeepwaterConstants.CanExtract, DeepwaterConstants.ReturnToSurface] : [DeepwaterConstants.CanExtract]);
        }
    }

    static DeepwaterExtraction.Device[] DevicesOf(DeepwaterRun run) { lock (run.Gate) return [.. run.Devices]; }

    // A run that failed (a death, drowning, leaving another way) for everyone in it.
    Task RunFailed(IModuleContext module, ClientState conn, string reason) =>
        RunOf(conn) is { } run ? DeepwaterExtraction.Fail(module, conn, run, reason) : Task.CompletedTask;

    static JsonObject PackState(DeepwaterRun run)
    {
        lock (run.Gate)
            return new JsonObject
            {
                ["planned"] = run.PlannedPacks, ["released"] = run.ReleasedPacks, ["waiting"] = run.Planned.Count,
                ["monstersReleased"] = run.ReleasedMonsters,
            };
    }

    void RunState(ClientState conn, JsonObject json)
    {
        if (RunOf(conn) is not { } run) return;
        double now = conn.Clock.Elapsed.TotalSeconds;
        var at = conn.Walk.Position(now);
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
            ["drowningLeft"] = _light.Drowning(conn, out double left) ? Math.Round(left, 2) : null,
            ["collapse"] = run.CollapseState,
            ["outcome"] = run.Outcome,
            ["landIn"] = run.LandAt > 0 ? Math.Round(Math.Max(0, run.LandAt - now), 1) : null,
            ["dropStage"] = run.LandAt > 0 ? run.DropStage : null,
            ["devices"] = new JsonArray([.. DevicesOf(run).Select(d => (JsonNode)new JsonObject
            {
                ["id"] = d.Id, ["kind"] = d.Kind.ToString(), ["x"] = d.X, ["y"] = d.Y, ["landed"] = d.Landed, ["sent"] = d.Sent, ["called"] = d.Called, ["entry"] = d.Entry, ["retired"] = d.Retired,
            })]),
            ["capsule"] = ItemsOf(conn).Items(DeepwaterExtraction.CapsuleRow).Count,
            ["collection"] = ItemsOf(conn).Items(DeepwaterExtraction.CollectionRow).Count,
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
