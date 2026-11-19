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
                _runs.Add(instance, run);
                made = true;
            }
        }
        if (made) module.Log($"{player.Character}: Allflame {(run.Voyage ? "Voyage" : "Chart")} in {player.AreaId}/{player.Seed}, {run.Board.Size}×{run.Board.Size}, {run.Total} lanterns");
        foreach (var lantern in run.Snapshot()) await DeepwaterLanterns.Show(module, conn, lantern, dropping: made);
        await DeepwaterLanterns.SendCount(conn, run);
        await DeepwaterLoot.Show(conn, run);
        // The first lantern's light wakes the packs around the start and collects the Sulphur there.
        if (first is not null)
        {
            await DeepwaterMonsters.Release(module, conn, run, first);
            await DeepwaterLoot.Collect(module, conn, run, first);
        }
        // The run's owner holds the lanterns (QuestFlags 3550, the client's placement gate) and is its map owner (2020,
        // the capsule window's gate; G that the owner is the one). Other players get neither (G: the
        // TransferLanternControlObject that hands the lanterns over is not built).
        if (player.Character.Equals(run.Controller, StringComparison.OrdinalIgnoreCase))
            await SetQuestFlags(remote, stream, ciphers, conn, [DeepwaterConstants.LanternController, DeepwaterConstants.MapOwner]);
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
                    // Nothing is answered: the client's count changes only with mode 2.
                    module.Log($"{player.Character} <= 0x02ca DeepwaterRules PlaceLantern ({a.X}, {a.Y}) refused: {why}");
                    return true;
                }
                var lantern = DeepwaterLanterns.Add(conn, run, a.X, a.Y, counts: true);
                int placed, total;
                lock (run.Gate) (placed, total) = (run.Placed, run.Total);
                module.Log($"{player.Character} <= 0x02ca DeepwaterRules PlaceLantern ({a.X}, {a.Y}): lantern {placed}/{total}");
                await DeepwaterLanterns.Show(module, conn, lantern, dropping: true);
                await DeepwaterLanterns.SendCount(conn, run);
                await DeepwaterMonsters.Release(module, conn, run, lantern);
                await DeepwaterLoot.Collect(module, conn, run, lantern);
                if (placed == 1)
                {
                    var (remote, stream, ciphers) = conn.Wire;
                    await SetQuestFlags(remote, stream, ciphers, conn, [DeepwaterConstants.FirstLanternPlaced]);
                }
                return true;
            default:
                // Send to Surface (2) and Call Bathysphere (3) belong to the capsules and the extraction, not built yet.
                module.Log($"{player.Character} <= 0x02ca DeepwaterRules {a.Op}: not built yet");
                return true;
        }
    }

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
            ["lanterns"] = new JsonArray([.. run.Snapshot().Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.Id, ["x"] = l.X, ["y"] = l.Y, ["radius"] = Math.Round(l.Radius(now), 1),
            })]),
        };
    }
}
