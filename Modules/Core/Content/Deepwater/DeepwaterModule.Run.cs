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
            if (on.Count > 0) await SetQuestFlags(remote, stream, ciphers, conn, on, on: false);
            return;
        }
        if (conn.Instance is not { } instance) return;
        bool made = false;
        DeepwaterRun? run;
        DeepwaterRun.Lantern? first = null;
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
                run = new DeepwaterRun
                {
                    Voyage = voyage, Board = board, Controller = player.Character, Total = DeepwaterLanterns.Total(conn, voyage),
                    Level = Math.Clamp((int)conn.Pools.Level, 1, DeepwaterMonsters.MaxLevel),
                };
                var plan = DeepwaterMonsters.Plan(conn.Layout, conn.Seed, run, module.Log);
                run.Planned.AddRange(plan);
                run.PlannedPacks = plan.Count;
                DeepwaterLoot.Plan(conn, run, module.Log);
                // The first lantern burns at the start Bathysphere (cell 0's "bathysphere" point; the wiki: placed on entry)
                // and is not one of the run's (G: the tutorial's count starts at 6 with it already lit). Without an anchor
                // the client allows no placement.
                var start = board.Cells[0].Point("bathysphere") ?? board.Cells[0].Point("default") ?? (conn.Layout.SpawnX, conn.Layout.SpawnY);
                first = DeepwaterLanterns.Add(conn, run, start.X, start.Y, counts: false);
                DeepwaterExtraction.Plan(conn, run, start, module.Log);
                _runs.Add(instance, run);
                made = true;
            }
        }
        if (made) module.Log($"{player.Character}: Allflame {(run.Voyage ? "Voyage" : "Chart")} in {player.AreaId}/{player.Seed}, {run.Board.Size}×{run.Board.Size}, {run.Total} lanterns");
        foreach (var lantern in run.Snapshot()) await DeepwaterLanterns.Show(module, conn, run, lantern, dropping: made);
        await DeepwaterLanterns.SendCount(conn, run);
        await DeepwaterLoot.Show(conn, run);
        await DeepwaterExtraction.Show(conn, run);
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
            await SetQuestFlags(remote, stream, ciphers, conn, [DeepwaterConstants.LanternController, DeepwaterConstants.MapOwner]);
        await DeepwaterExtraction.SyncFlags(conn, run);
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
                if (DeepwaterLanterns.Refusal(conn, run, a.X, a.Y) is { } why)
                {
                    // The count is sent again so the client stays in step with the server (it changes only with mode 2).
                    module.Log($"{player.Character} <= 0x02ca DeepwaterRules PlaceLantern ({a.X}, {a.Y}) refused: {why}");
                    await DeepwaterLanterns.SendCount(conn, run);
                    return true;
                }
                var lantern = DeepwaterLanterns.Add(conn, run, a.X, a.Y, counts: true);
                int placed, total;
                lock (run.Gate) (placed, total) = (run.Placed, run.Total);
                module.Log($"{player.Character} <= 0x02ca DeepwaterRules PlaceLantern ({a.X}, {a.Y}): lantern {placed}/{total}");
                await DeepwaterLanterns.Show(module, conn, run, lantern, dropping: true);
                await DeepwaterLanterns.SendCount(conn, run);
                await DeepwaterMonsters.Release(module, conn, run, lantern);
                await DeepwaterLoot.Collect(module, conn, run, lantern);
                if (placed == 1)
                {
                    var (remote, stream, ciphers) = conn.Wire;
                    await SetQuestFlags(remote, stream, ciphers, conn, [DeepwaterConstants.FirstLanternPlaced]);
                }
                // A Chart's last lantern: the lanterns collapse and its Bathysphere can be used (QuestFlags 3562).
                if (!run.Voyage && placed >= total) await ChartCollapse(module, conn, run);
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
            await SetQuestFlags(remote, stream, ciphers, member, [DeepwaterConstants.CanExtract]);
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
            ["devices"] = new JsonArray([.. DevicesOf(run).Select(d => (JsonNode)new JsonObject
            {
                ["id"] = d.Id, ["kind"] = d.Kind.ToString(), ["x"] = d.X, ["y"] = d.Y, ["landed"] = d.Landed, ["sent"] = d.Sent, ["called"] = d.Called,
            })]),
            ["capsule"] = ItemsOf(conn).Items(DeepwaterExtraction.CapsuleRow).Count,
            ["collection"] = ItemsOf(conn).Items(DeepwaterExtraction.CollectionRow).Count,
            ["swarmPacks"] = run.SwarmPacks,
            ["lanterns"] = new JsonArray([.. run.Snapshot().Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.Id, ["x"] = l.X, ["y"] = l.Y, ["radius"] = Math.Round(l.Radius(now), 1),
            })]),
        };
    }
}
