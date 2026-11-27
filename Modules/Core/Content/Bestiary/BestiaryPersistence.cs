using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameServer.Modules.Bestiary;

public sealed partial class BestiaryEncounter
{
    public void Describe(Player player, JsonObject state)
    {
        if (LiveOf(player) is not { } live) return;
        lock (live.Gate)
            state["bestiary"] = new JsonObject
            {
                ["mission"] = live.Run.MissionId, ["key"] = live.Run.Key,
                ["received"] = live.Run.Received, ["complete"] = live.Run.Complete, ["einhar"] = live.Einhar,
                ["stored"] = BestiaryCaptures.Load(WorldOf(PlayerOf(player)).AccountData).Count,
                ["beasts"] = new JsonArray(live.Run.Beasts.Select(b => (JsonNode)new JsonObject
                {
                    ["id"] = b.Id, ["variety"] = b.Variety, ["red"] = b.Red, ["captured"] = b.Captured,
                }).ToArray()),
                ["waves"] = new JsonArray(live.Waves.Select(w => (JsonNode)new JsonObject
                {
                    ["beast"] = w.BeastId, ["total"] = w.Total, ["dispatched"] = w.Done,
                }).ToArray()),
            };
    }

    const string StateKey = "Bestiary/Mission";
    sealed record KeptBeast(uint Id, ushort Variety, bool Red, bool Captured);
    sealed record KeptWave(uint Beast, ushort Kind, int X, int Y, int Seed, int Attempts, bool Started, int Total, int Done, double Clock);
    sealed record KeptMission(string Key, string Mission, byte Level, KeptBeast[] Beasts, KeptWave[] Waves,
        bool Received, bool Complete, uint Einhar, int Npc, bool DialogueSent, bool Left, bool Done,
        double Clock, double? CompletedAt, (uint Id, double RemoveAt)[] Effects);
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    static void Keep(InstanceState instance, Live live, bool stopping = false)
    {
        KeptMission kept;
        lock (live.Gate)
        {
            if (live.Stopped && !stopping) return;
            kept = new(live.Run.Key, live.Run.MissionId, live.Run.Level,
                [.. live.Run.Beasts.Select(b => new KeptBeast(b.Id, b.Variety, b.Red, b.Captured))],
                [.. live.Waves.Select(w => new KeptWave(w.BeastId, w.Kind.Row, w.Cell.X, w.Cell.Y, w.Seed, w.Attempts,
                    w.Started, w.Total, w.Done, w.Clock))],
                live.Run.Received, live.Run.Complete, live.Einhar, live.EinharNpc, live.EinharDialogueSent,
                live.Left, live.Done, live.Clock, live.CompletedAt, [.. live.Effects]);
            live.KeptAt = live.Clock;
            instance.ContentState[StateKey] = JsonSerializer.Serialize(kept, Json);
        }
    }

    static Live? Restore(Player player)
    {
        if (!player.Instance.ContentState.TryGetValue(StateKey, out string? text)) return null;
        var kept = JsonSerializer.Deserialize<KeptMission>(text, Json)!;
        var run = new BestiaryRun { Key = kept.Key, MissionId = kept.Mission, Level = kept.Level };
        foreach (var saved in kept.Beasts)
        {
            object body = (object?)player.Area.Monsters.GetValueOrDefault(saved.Id)
                ?? player.Area.DormantMonsters.GetValueOrDefault(saved.Id) ?? (object)saved.Id;
            var beast = run.Add(saved.Id, body, saved.Red);
            beast.Variety = saved.Variety;
            beast.Captured = saved.Captured;
        }
        run.RestoreProgress(kept.Received, kept.Complete);
        var live = new Live(run, new Random(unchecked((int)(player.Seed ^ 0x42657374u))))
        {
            Einhar = kept.Einhar, EinharNpc = kept.Npc, EinharDialogueSent = kept.DialogueSent,
            Left = kept.Left, Done = kept.Done, Clock = kept.Clock, KeptAt = kept.Clock, CompletedAt = kept.CompletedAt,
        };
        foreach (var wave in kept.Waves)
            if (BestiaryTables.RedKinds.FirstOrDefault(r => r.Row == wave.Kind) is { } kind)
                live.Waves.Add(new(kind, wave.Beast, (wave.X, wave.Y), wave.Seed)
                    { Attempts = wave.Attempts, Started = wave.Started, Total = wave.Total, Done = wave.Done, Clock = wave.Clock });
        live.Effects.AddRange(kept.Effects);
        return live;
    }
}
