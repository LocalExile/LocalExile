using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>A Chart or a Voyage started from the deck: its owner, its board (cell y·N + x, y up, cell 0 the start), the
/// area level, the border modifiers it took, each cell's stats (by client stat key) and the Voyage-wide ones, the chart
/// waiting in DeepwaterActiveMission (109) for a Chart, and a Voyage's charts in 104: the ones on its board, and the ones
/// its entry took (with the planner's border from before that entry, given back if the transfer fails).</summary>
internal sealed class DeepwaterPlan
{
    public required bool Voyage { get; init; }
    public required string Owner { get; init; }
    public required int Size { get; init; }
    public required IReadOnlyList<DeepwaterBoardRules.Cell?> Cells { get; init; }
    public required int Level { get; init; }
    public int LanternTotal { get; init; }
    public IReadOnlyList<ItemMod> Border { get; init; } = [];
    public required IReadOnlyList<Dictionary<uint, int>> CellStats { get; init; }
    public required Dictionary<uint, int> Global { get; init; }
    public uint ChartItem { get; init; }
    public IReadOnlyList<InventoryItem?> Picked { get; init; } = [];
    public IReadOnlyList<InventoryItem> Consumed { get; set; } = [];
    public DeepwaterPlanner.Plan? PreviousBorder { get; set; }
    /// <summary>The run's end was handled (DeepwaterModule.Finish).</summary>
    public bool Finished { get; set; }

    /// <summary>A cell's own stats with the Voyage-wide ones (what the cell's packs are planned with).</summary>
    public Dictionary<uint, int> Effective(int cell)
    {
        var combined = new Dictionary<uint, int>();
        var source = cell >= 0 && cell < CellStats.Count ? CellStats[cell].Concat(Global) : Global;
        foreach (var (key, value) in source)
            if (value != 0) combined[key] = combined.GetValueOrDefault(key) + value;
        return combined;
    }
}

// Starting a run from the deck (ruleset 172 ops 0, 1 and 8): the chart device's Descend and the Voyage planner's Set Forth
// build a plan, the deck's Bathysphere opens (ChartPortal ready 1, then 2) and its one entry takes the owner into a new
// instance of the encounter area, opened as a map run (MapDevice.Run, not an Atlas map) at the plan's level with its stats.
// The plan is found again by that run object: the grid (InstanceEvents.TerrainPluginStates) and StartRun read it.
public sealed partial class DeepwaterModule
{
    readonly ConditionalWeakTable<MapDevice.Run, DeepwaterPlan> _plans = new();
    /// <summary>ClientStrings 8426: no room in the charted inventory for the chart once charted.</summary>
    const int NoChartedRoom = 8426;
    /// <summary>Seconds from the Bathysphere's opening (ready 1) to open (ready 2): the opening plays and it is targetable
    /// after 1.3 s (G: 1.5 s).</summary>
    const double PortalOpening = 1.5;

    DeepwaterPlan? PlanOf(MapDevice.Run? run) => run is not null && _plans.TryGetValue(run, out var plan) ? plan : null;

    static Task SendRow(Player player, byte row, IReadOnlyList<uint> removed, IReadOnlyList<InventoryItem> added, string what) =>
        PlayerOf(player).Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), ItemsOf(player).NextRevision(row), removed, added),
            $"0x00bf inventory {PlayerInventories.InventoryId(row)}: {what}");

    /// <summary>Why the deck cannot start a run now, or null: on The Sovereign, as its map owner, with no run armed.</summary>
    string? DeckRefusal(Player player, State state) =>
        PlayerOf(player).AreaId != HubId ? "not on The Sovereign"
        : !HubOwner(player) ? "not the deck's map owner"
        : state.Armed is not null ? "a run is waiting at the Bathysphere already" : null;

    /// <summary>The collection (114) is not empty: the HUD asks first (mode 2) and the start waits for op 8.</summary>
    async Task<bool> AskFirst(IModuleContext module, Player player, State state, Func<Task> start)
    {
        if (ItemsOf(player).Items(DeepwaterExtraction.CollectionRow).Count == 0) return false;
        state.Confirm = start;
        if (DeepwaterHubRuleset.ConfirmEnter() is { } ask)
            await PlayerOf(player).Send(ask, "0x02c9 DeepwaterHubRules mode 2: the collection will be emptied (ClientStrings 8445)");
        module.Log($"{PlayerOf(player).Character}: the collection holds items; the start waits for the confirmation (op 8)");
        return true;
    }

    /// <summary>Op 8: the player said yes. The collection's items go to the main inventory (G: 8445 says "remove"; what
    /// does not fit stays in 114), then the start goes on.</summary>
    async Task ConfirmStart(IModuleContext module, Player player)
    {
        var state = _states.GetOrCreateValue(PlayerOf(player));
        if (state.Confirm is not { } start)
        {
            module.Log($"{PlayerOf(player).Character} <= 0x02ca DeepwaterHubRules ConfirmEnterMission: nothing waits for it");
            return;
        }
        state.Confirm = null;
        await DeepwaterExtraction.TakeFromCollection(module, player, null, requireWindow: false);
        await start();
    }

    // ---- op 1: a Chart ----

    /// <summary>Op 1 (the Chart window's Descend): an identified chart in 103 and a free cell in 104 for it once charted
    /// (the client's own checks, <c>0x1403ff0a0</c>). The chart moves to 109 and the Bathysphere opens.</summary>
    async Task StartChart(IModuleContext module, Player player, bool asked = false)
    {
        var modPlayer = PlayerOf(player);
        var state = _states.GetOrCreateValue(modPlayer);
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        string what = $"{modPlayer.Character} <= 0x02ca DeepwaterHubRules Open1x1Mission";
        var chart = items.Items(DeepwaterLocker.UnchartedRow).FirstOrDefault();
        string? why = DeckRefusal(player, state)
            ?? (chart is null || !DeepwaterCharts.IsChart(catalog, chart.Item) ? "no chart in the device"
            : !chart.Item.Identified ? "the chart is unidentified"
            : chart.Item.Chart is null ? "the chart has no room"
            : items.Items(DeepwaterLocker.ActiveRow).Count > 0 ? "a chart is active already"
            : items.FreeCell(DeepwaterLocker.ChartedRow, 1, 1, ShapeOf) is null ? $"no room in the charted inventory (ClientStrings {NoChartedRoom})" : null);
        if (why is not null)
        {
            module.Log($"{what}: refused ({why})");
            return;
        }
        if (!asked && await AskFirst(module, player, state, () => StartChart(module, player, asked: true))) return;
        InventoryItem? active = null;
        Saves.Atomically(() =>
        {
            if (items.Remove(DeepwaterLocker.UnchartedRow, chart!.Id) is { } taken)
                active = items.Put(DeepwaterLocker.ActiveRow, taken with { X = 0, Y = 0 });
        });
        if (active is null)
        {
            module.Log($"{what}: refused (the chart could not be moved)");
            return;
        }
        await SendRow(player, DeepwaterLocker.UnchartedRow, [active.Id], [], $"remove item {active.Id} (the chart descends)");
        await SendRow(player, DeepwaterLocker.ActiveRow, [], [active], $"add item {active.Id} (the active chart)");
        var cell = new DeepwaterBoardRules.Cell(active.Item.Chart!.Value, active.Item.Mods);
        DeepwaterBoardRules.Cell?[] cells = [cell];
        var mods = catalog.Mods;
        var global = DeepwaterBoardRules.GlobalStats(mods, cells);
        int cursed = DeepwaterCursedDucats.InjectPlanStats(player, global);
        int upgrades = DeepwaterUpgrades.InjectPlanStats(player, global);
        var plan = new DeepwaterPlan
        {
            Voyage = false, Owner = modPlayer.Character, Size = 1, Cells = cells,
            Level = Math.Clamp((int)cell.Chart.AreaLevel, 1, 100),
            LanternTotal = DeepwaterLanterns.Total(player, voyage: false),
            CellStats = [DeepwaterBoardRules.CellStats(mods, cells, 1, 0, [])],
            Global = global,
            ChartItem = active.Id,
        };
        module.Log($"{what}: item {active.Id} ({catalog.ByHash(active.Item.TypeHash)?.Name}, room {cell.Chart.Room}, rotation {cell.Chart.Rotation}) at level {plan.Level}"
            + (cursed > 0 ? $", {cursed} Cursed Ducats" : "")
            + (upgrades > 0 ? $", {upgrades} ship upgrades" : ""));
        // The tracker's "enter the Bathysphere" (QuestFlags 3568, G: set by the accepted Descend).
        await SetQuestFlags(player, [DeepwaterConstants.EnterTheChart]);
        await Arm(module, player, state, plan, active.Item.Rarity);
    }

    // ---- op 0: a Voyage ----

    /// <summary>Op 0 (Set Forth): u8 n, n × u8 (per board slot y·3 + x the chart's x in 104, 0xff empty). The board is
    /// 2 × 2 before the 3 × 3 board is open (slots 0, 1, 3, 4, or n = 4 in board order) and 3 × 3 after it (G for the 2 × 2
    /// wire form). The client's validator decides; the level and the border mods carried into the run. The charts stay in
    /// 104 until the Bathysphere's entry (TakeCharts), so a run that is never entered costs nothing.</summary>
    async Task StartVoyage(IModuleContext module, Player player, byte[] args, bool asked = false)
    {
        var modPlayer = PlayerOf(player);
        var state = _states.GetOrCreateValue(modPlayer);
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        string what = $"{modPlayer.Character} <= 0x02ca DeepwaterHubRules OpenMission ({Convert.ToHexString(args)})";
        int n = DeepwaterPlanner.Board3x3(player) ? 3 : 2;
        int count = args.Length > 0 ? args[0] : 0;
        byte[] slots = args.Length >= 1 + count ? args[1..(1 + count)] : [];
        byte[]? board = (slots.Length, n) switch
        {
            (9, 3) => slots,
            (9, 2) => slots.Where((_, i) => i % 3 == 2 || i >= 6).All(s => s == 0xff) ? [slots[0], slots[1], slots[3], slots[4]] : null,
            (4, 2) => slots,
            _ => null,
        };
        string? why = DeckRefusal(player, state) ?? (board is null ? $"{count} slots for a {n} × {n} board" : null);
        var stored = items.Items(DeepwaterLocker.ChartedRow);
        var picked = new InventoryItem?[board?.Length ?? 0];
        for (int i = 0; why is null && i < picked.Length; i++)
        {
            if (board![i] == 0xff) continue;
            picked[i] = stored.FirstOrDefault(s => s.X == board[i]);
            if (picked[i] is not { Item.Chart: not null }) why = $"no chart at {board[i]} in the charted inventory";
            else if (Array.IndexOf(board, board[i]) != i) why = $"the chart at {board[i]} twice";
        }
        var cells = picked.Select(p => p is null ? (DeepwaterBoardRules.Cell?)null : new DeepwaterBoardRules.Cell(p.Item.Chart!.Value, p.Item.Mods)).ToArray();
        if (why is null && DeepwaterBoardRules.Validate(cells, n) is int error) why = $"the board is not valid (ClientStrings {error})";
        if (why is not null)
        {
            module.Log($"{what}: refused ({why})");
            return;
        }
        if (!asked && await AskFirst(module, player, state, () => StartVoyage(module, player, args, asked: true))) return;
        var mods = catalog.Mods;
        int level = DeepwaterBoardRules.Level(cells);
        bool velkaArena = false;
        if (DeepwaterVelka.CanEnterArena(player, level))
        {
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] is { } c && c.Chart.Room == DeepwaterVelka.BarrierRoom)
                {
                    cells[i] = c with { Chart = c.Chart with { Room = DeepwaterVelka.ArenaRoom } };
                    velkaArena = true;
                }
            }
        }
        var border = n == 3 ? DeepwaterPlanner.Current(player).Border : [];
        var global = DeepwaterBoardRules.GlobalStats(mods, cells);
        int cursed = DeepwaterCursedDucats.InjectPlanStats(player, global);
        int upgrades = DeepwaterUpgrades.InjectPlanStats(player, global);
        var plan = new DeepwaterPlan
        {
            Voyage = true, Owner = modPlayer.Character, Size = n, Cells = cells, Border = border,
            Level = level,
            LanternTotal = DeepwaterLanterns.Total(player, voyage: true),
            CellStats = [.. Enumerable.Range(0, cells.Length).Select(i => DeepwaterBoardRules.CellStats(mods, cells, n, i, border))],
            Global = global,
            Picked = picked,
        };
        module.Log($"{what}: {n}×{n} at level {plan.Level}, {border.Count} border mods"
            + (cursed > 0 ? $", {cursed} Cursed Ducats" : "")
            + (upgrades > 0 ? $", {upgrades} ship upgrades" : "")
            + (velkaArena ? ", Kishara's Rest -> Boss Arena (#30)" : ""));
        await Arm(module, player, state, plan, ItemRarity.Normal);
    }

    // ---- the Bathysphere ----

    /// <summary>The plan becomes a map run of the encounter area (not an Atlas map: only its own stats apply) and the
    /// deck's Bathysphere opens for it. Without a Bathysphere on the deck the owner goes straight in (G).</summary>
    async Task Arm(IModuleContext module, Player player, State state, DeepwaterPlan plan, ItemRarity rarity)
    {
        string id = plan.Voyage ? DeepwaterConstants.VoyageArea : DeepwaterConstants.ChartArea;
        if (DeepwaterGrid.AreaOf(id) is not { } area)
        {
            module.Log($"{plan.Owner}: no WorldAreas row {id}; the run is not started");
            await Restore(module, player, plan, "no area");
            return;
        }
        // A Chart's own stats and its global implicits are the area's; a Voyage's area gets the global stats and each cell
        // adds its own (DeepwaterRun.CellMods and the drop factors).
        var stats = plan.Voyage ? plan.Global : plan.Effective(0);
        var mapMods = new MapMods(stats) { MapRarity = rarity };
        var at = state.PortalAt;
        var run = new MapDevice.Run(plan.Owner, area with { Level = plan.Level }, mapMods, 0,
            new MapDevice.Origin(player.Layout.Hash, player.InstanceNumber, at.X, at.Y)) { Atlas = false };
        _plans.AddOrUpdate(run, plan);
        state.Armed = run;
        state.ArmedPlan = plan;
        if (state.Portal == 0)
        {
            module.Log($"{plan.Owner}: the deck has no Bathysphere; going straight in (G)");
            await Enter(module, player, state);
            return;
        }
        state.PortalOpenAt = AreaTime(player) + PortalOpening;
        await SetPortalReady(player, 1);
    }

    /// <summary>The one entry: the owner goes into a new instance of the run's area, and the Bathysphere closes. A refused
    /// transfer gives the chart or the board's charts back.</summary>
    async Task Enter(IModuleContext module, Player player, State state)
    {
        if (state.Armed is not { } run || state.ArmedPlan is not { } plan) return;
        (state.Armed, state.ArmedPlan, state.PortalOpenAt) = (null, null, 0);
        await SetPortalReady(player, 0);
        if (plan.Voyage && !await TakeCharts(module, player, plan)) return;
        // The Chart's chart is in its run from here: a run left by a crash or a relog loses it (ReturnActive).
        if (!plan.Voyage) CharacterSaves.ChangeModuleData(plan.Owner, EnteredChartKey, _ => plan.ChartItem.ToString());
        module.Log($"{plan.Owner}: Allflame {(plan.Voyage ? "Voyage" : "Chart")} enters {run.Area.Id} at level {plan.Level}");
        var transfer = new PendingTransfer(run.Area.Hash16, 0, New: true)
        {
            Map = run,
            Done = r =>
            {
                if (r.Ok) return;
                _ = Task.Run(async () =>
                {
                    try { await Restore(module, player, plan, r.Error ?? "the transfer was refused"); }
                    catch (Exception ex) { module.Log($"{plan.Owner}: failed to restore Allflame run after transfer refusal: {ex.Message}"); }
                });
            },
        };
        await ChangeArea(player, transfer, "Allflame Bathysphere");
    }

    /// <summary>Character module data: the id of the chart whose run was entered, until the run ends ("" for none).</summary>
    const string EnteredChartKey = "deepwater.entered-chart";

    /// <summary>A Voyage's entry: its board's charts must still be in 104 as planned. Each leaves 104 unless its border
    /// mods' keep chance succeeds (G: rolled at the entry; the wiki's "chance to not consume" gives no timing), and the
    /// planner gets new border modifiers and no rerolls. False (nothing taken) when a chart moved, turned or left.</summary>
    async Task<bool> TakeCharts(IModuleContext module, Player player, DeepwaterPlan plan)
    {
        var items = ItemsOf(player);
        var mods = ItemsFor(player).Mods;
        var stored = items.Items(DeepwaterLocker.ChartedRow);
        if (plan.Picked.OfType<InventoryItem>().FirstOrDefault(p => !stored.Any(s => s.Id == p.Id && s.Item.Chart == p.Item.Chart)) is { } gone)
        {
            module.Log($"{plan.Owner}: the Voyage is not entered (chart {gone.Id} moved, turned or left the charted inventory)");
            return false;
        }
        var consumed = new List<InventoryItem>();
        var kept = new List<string>();
        var rng = AreaRng(player);
        for (int i = 0; i < plan.Picked.Count; i++)
        {
            if (plan.Picked[i] is not { } chart) continue;
            int keep = DeepwaterBoardRules.KeepChance(mods, i, plan.Size, plan.Border);
            if (keep > 0 && rng.Next(100) < keep) kept.Add($"cell {i} ({keep}%)");
            else consumed.Add(chart);
        }
        var removed = new List<uint>();
        Saves.Atomically(() =>
        {
            foreach (var it in consumed)
                if (items.Remove(DeepwaterLocker.ChartedRow, it.Id) is not null) removed.Add(it.Id);
        });
        plan.Consumed = [.. consumed.Where(c => removed.Contains(c.Id))];
        if (removed.Count > 0) await SendRow(player, DeepwaterLocker.ChartedRow, removed, [], $"{removed.Count} charts taken by the Voyage");
        // New border modifiers for the next Voyage, and no rerolls made on them.
        if (plan.Size == 3)
        {
            plan.PreviousBorder = DeepwaterPlanner.Current(player);
            await DeepwaterPlanner.Send(player, DeepwaterPlanner.Renew(player));
        }
        module.Log($"{plan.Owner}: the Voyage takes {removed.Count} charts" + (kept.Count > 0 ? $", kept {string.Join(", ", kept)}" : ""));
        return true;
    }

    /// <summary>A run that did not start: a Chart's chart goes back from 109 to 103, a Voyage's charts back to 104 with the
    /// border it had.</summary>
    async Task Restore(IModuleContext module, Player player, DeepwaterPlan plan, string reason)
    {
        var items = ItemsOf(player);
        if (!plan.Voyage)
        {
            CharacterSaves.ChangeModuleData(plan.Owner, EnteredChartKey, _ => "");
            if (!player.Closed && player.Character.HasQuestFlag(DeepwaterConstants.EnterTheChart))
                await SetQuestFlags(player, [DeepwaterConstants.EnterTheChart], on: false);
            await ReturnActive(module, player, $"the run did not start: {reason}", forceSend: true);
            return;
        }
        var back = new List<InventoryItem>();
        Saves.Atomically(() =>
        {
            foreach (var it in plan.Consumed)
            {
                bool free = !items.Items(DeepwaterLocker.ChartedRow).Any(s => s.X == it.X);
                var cell = free ? ((byte X, byte Y)?)(it.X, 0) : items.FreeCell(DeepwaterLocker.ChartedRow, 1, 1, ShapeOf);
                if (cell is { } c) back.Add(items.Put(DeepwaterLocker.ChartedRow, it with { X = c.X, Y = c.Y }));
            }
        });
        module.Log($"{plan.Owner}: the Voyage did not start ({reason}); {back.Count}/{plan.Consumed.Count} charts back in the charted inventory");
        if (back.Count > 0 && !player.Closed) await SendRow(player, DeepwaterLocker.ChartedRow, [], back, "the Voyage's charts back");
        if (plan.PreviousBorder is { } previous)
        {
            DeepwaterPlanner.Put(player, previous);
            if (!player.Closed) await DeepwaterPlanner.Send(player, previous);
        }
    }

    /// <summary>The chart in 109 goes back to 103 (or the main inventory when 103 is taken) when its run never started
    /// (G). One whose run was entered and left by a crash, a relog or a death is lost, as a failed Chart (the research's
    /// "Failure → lost"), and counts as the tutorial's failed first Chart.</summary>
    async Task ReturnActive(IModuleContext module, Player player, string reason, bool forceSend = false)
    {
        var items = ItemsOf(player);
        var left = items.Items(DeepwaterLocker.ActiveRow).ToList();
        string me = CharacterName(player);
        string entered = CharacterSaves.ModuleData(me, EnteredChartKey) ?? "";
        foreach (var it in left)
        {
            if (entered == it.Id.ToString())
            {
                // Its run has not ended yet (the player relogged while it goes on, or rides up without a deck): it decides.
                if (RunningChart(me, it.Id)) continue;
                CharacterSaves.ChangeModuleData(me, EnteredChartKey, _ => "");
                if (items.Remove(DeepwaterLocker.ActiveRow, it.Id) is null) continue;
                module.Log($"{CharacterName(player)}: the active chart {it.Id}'s run was left unfinished; the chart is lost (G)");
                bool live = !player.Closed && (forceSend || !player.LeavingArea);
                if (!HasLeagueFlag(player, DeepwaterConstants.FirstChartLeagueFlag))
                    await SetTutorialFlags(module, player, live, DeepwaterConstants.FailedFirstChartLeagueFlag);
                if (live) await SendRow(player, DeepwaterLocker.ActiveRow, [it.Id], [], $"remove item {it.Id} (the chart is lost)");
                continue;
            }
            InventoryItem? placed = null;
            byte row = DeepwaterLocker.UnchartedRow;
            Saves.Atomically(() =>
            {
                var shape = ShapeOf(it.Item) ?? new(1, 1, "");
                var cell = items.Items(DeepwaterLocker.UnchartedRow).Count == 0 ? ((byte X, byte Y)?)(0, 0)
                    : items.FreeCell(row = PlayerInventories.MainInventory, shape.Width, shape.Height, ShapeOf);
                if (cell is { } c && items.Remove(DeepwaterLocker.ActiveRow, it.Id) is { } taken) placed = items.Put(row, taken with { X = c.X, Y = c.Y });
            });
            module.Log($"{CharacterName(player)}: the active chart {it.Id} {(placed is null ? "stays in 109 (no room)" : $"goes back to row {row}")} ({reason})");
            if (placed is null || player.Closed || (!forceSend && player.LeavingArea)) continue;
            await SendRow(player, DeepwaterLocker.ActiveRow, [it.Id], [], $"remove item {it.Id}");
            await SendRow(player, row, [], [placed], $"add item {placed.Id} (the chart back)");
        }
    }

    /// <summary>A Chart run of this owner and chart whose end is not handled yet.</summary>
    bool RunningChart(string owner, uint chart)
    {
        lock (_runs)
            foreach (var (_, run) in _runs)
                if (run.Plan is { Voyage: false, Finished: false } p && p.ChartItem == chart && p.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    /// <summary>A planned run's end: a Chart's success charts its chart (109 → 104 with a rolled implicit), its failure
    /// loses it (G); a Voyage took its charts at its entry. The owner must be among the players it ended for; otherwise
    /// the chart waits in 109 and is lost at the owner's next entry (ReturnActive).</summary>
    async Task Finish(IModuleContext module, DeepwaterPlan plan, IReadOnlyList<Player> members, bool success)
    {
        plan.Finished = true;
        var owner = members.FirstOrDefault(m => CharacterName(m).Equals(plan.Owner, StringComparison.OrdinalIgnoreCase));
        if (owner is null)
        {
            module.Log($"{plan.Owner}: the {(plan.Voyage ? "Voyage" : "Chart")} ended {(success ? "well" : "badly")} without its owner"
                + (plan.Voyage ? "" : "; the chart stays in 109"));
            return;
        }
        var items = ItemsOf(owner);
        bool live = !owner.Closed && !owner.LeavingArea;
        if (!plan.Voyage) CharacterSaves.ChangeModuleData(plan.Owner, EnteredChartKey, v => v == plan.ChartItem.ToString() ? "" : null);
        if (plan.Voyage)
        {
            // The Maiden Voyage's end: the first Voyage that succeeded completes it and opens the 3 × 3 board (G: the trigger).
            if (success)
            {
                await SetTutorialFlags(module, owner, live, DeepwaterConstants.FirstVoyageLeagueFlag, DeepwaterConstants.VoyagesEnabledLeagueFlag);
                await DeepwaterUpgrades.TryGrantRunCompletionPiece(module, owner, voyage: true, live);
            }
            return;
        }
        // The tutorial's first Chart: a failure before any success is the pity path ("saved from drowning", Valerie's 3928),
        // a success its "completed your first Chart" (G: the triggers, R1 §6).
        if (!HasLeagueFlag(owner, DeepwaterConstants.FirstChartLeagueFlag))
            await SetTutorialFlags(module, owner, live, success ? DeepwaterConstants.FirstChartLeagueFlag : DeepwaterConstants.FailedFirstChartLeagueFlag);
        if (items.Items(DeepwaterLocker.ActiveRow).FirstOrDefault(i => i.Id == plan.ChartItem) is not { } active) return;
        if (!success)
        {
            if (items.Remove(DeepwaterLocker.ActiveRow, active.Id) is null) return;
            module.Log($"{plan.Owner}: the Chart failed; chart {active.Id} is lost (G)");
            if (live) await SendRow(owner, DeepwaterLocker.ActiveRow, [active.Id], [], $"remove item {active.Id} (the chart is lost)");
            return;
        }
        var catalog = ItemsFor(owner);
        var charted = DeepwaterCharts.Charted(catalog, active.Item, AreaRng(owner)) ?? active.Item;
        InventoryItem? placed = null;
        Saves.Atomically(() =>
        {
            if (items.FreeCell(DeepwaterLocker.ChartedRow, 1, 1, ShapeOf) is { } cell && items.Remove(DeepwaterLocker.ActiveRow, active.Id) is { } taken)
                placed = items.Put(DeepwaterLocker.ChartedRow, taken with { X = cell.X, Y = cell.Y, Item = charted });
        });
        if (placed is null)
        {
            module.Log($"{plan.Owner}: the Chart succeeded but the charted inventory is full; chart {active.Id} stays in 109");
            return;
        }
        module.Log($"{plan.Owner}: the Chart succeeded; chart {placed.Id} is charted ({string.Join(", ", charted.Mods.Where(m => m.Section == ModSection.Implicit).Select(m => catalog.Mods.ByHash(m.Hash)?.Id))}) at {placed.X} in 104");
        if (live)
        {
            await SendRow(owner, DeepwaterLocker.ActiveRow, [active.Id], [], $"remove item {active.Id} (charted)");
            await SendRow(owner, DeepwaterLocker.ChartedRow, [], [placed], $"add item {placed.Id} (charted)");
        }
        await FourCharts(module, owner, live);
        await DeepwaterUpgrades.TryGrantRunCompletionPiece(module, owner, voyage: false, live);
    }

    static bool HasLeagueFlag(Player player, int flag) =>
        player.Character.LeagueQuestFlags.Contains(flag);

    /// <summary>Sets the Maiden Voyage's league quest flags the character lacks; for a closed connection only in its save.</summary>
    static async Task SetTutorialFlags(IModuleContext module, Player player, bool live, params int[] flags)
    {
        var missing = flags.Where(f => !HasLeagueFlag(player, f)).ToList();
        if (missing.Count == 0) return;
        module.Log($"{CharacterName(player)}: the Maiden Voyage's league quest flag(s) {string.Join(", ", missing)}");
        if (live)
        {
            await SetLeagueQuestFlags(player, missing);
            return;
        }
        PersonalStashOf(player).ChangeLeagueQuestFlags(missing, true);
        player.Character.LeagueQuestFlags.UnionWith(missing);
    }

    /// <summary>League quest flag 1822 (the tracker's "plot a Voyage", Valerie's "Plan Voyage"): the charted inventory holds
    /// the tutorial Voyage's 4 charts (G: the count, R1 §6). Checked after each charting and at each entry of the deck, which
    /// covers the pity path and charts charted before this check existed.</summary>
    static Task FourCharts(IModuleContext module, Player player, bool live) =>
        !HasLeagueFlag(player, DeepwaterConstants.FourChartsLeagueFlag)
        && ItemsOf(player).Items(DeepwaterLocker.ChartedRow).Count >= DeepwaterConstants.ChartsForFirstVoyage
            ? SetTutorialFlags(module, player, live, DeepwaterConstants.FourChartsLeagueFlag) : Task.CompletedTask;

    /// <summary>A click on the deck's Bathysphere: the armed run's one entry, for the owner, once it is open.</summary>
    async Task<bool> UsePortal(IModuleContext module, Player player, State state, double at)
    {
        var modPlayer = PlayerOf(player);
        if (state.Armed is null)
        {
            module.Log($"{modPlayer.Character} <= 0x0024 the deck's Bathysphere 0x{state.Portal:x}: no chart run is armed");
            return true;
        }
        if (state.PortalOpenAt > 0)
        {
            module.Log($"{modPlayer.Character} <= 0x0024 the deck's Bathysphere 0x{state.Portal:x}: still opening");
            return true;
        }
        if (!await WorldOf(player).WalkUpTo(state.PortalAt.X, state.PortalAt.Y, state.PortalReach, at)) return true;
        await Enter(module, player, state);
        return true;
    }

    /// <summary>The Bathysphere's opening ends: ready 2 (G: the use of 2).</summary>
    async Task TickPortal(Player player, State state)
    {
        if (state.PortalOpenAt <= 0 || AreaTime(player) < state.PortalOpenAt) return;
        state.PortalOpenAt = 0;
        await SetPortalReady(player, 2);
    }
}
