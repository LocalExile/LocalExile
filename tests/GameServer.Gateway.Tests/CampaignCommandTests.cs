using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>The campaign driver's dev commands (Modules/Core/Player/Commands: CampaignState.cs, Rules.Campaign.cs):
/// /state's JSON and reply lines, /quest_preset's flags.</summary>
public class CampaignCommandTests
{
    [Fact]
    public void State_is_one_json_object_whose_reply_lines_join_back()
    {
        var conn = new ClientState(new AreaLayout(0x1234, "1_1_3", 200, 200, 70, 70, null, []), 1, 1)
        {
            Character = $"CampaignState{Guid.NewGuid():N}"[..23], RefundPoints = 20, Immune = true,
        };
        conn.QuestFlags.Add("a1q1-KilledHillock");

        var brief = CommandsRules.StateJson(conn, brief: true);
        Assert.Equal(1, (int)brief["v"]!);
        Assert.Equal("1_1_3", (string)brief["area"]!);
        Assert.Equal([70, 70], brief["pos"]!.AsArray().Select(c => (int)c!));
        Assert.Contains("a1q1-KilledHillock", brief["flags"]!.AsArray().Select(f => (string)f!));
        Assert.Equal(20, (int)brief["passives"]!["refund"]!);
        Assert.True((bool)brief["immune"]!);
        Assert.Null(brief["stash"]);
        Assert.Null(brief["reward"]);
        // brief leaves out the area's things; the full reply has them (the inventory from the save), a Stash once the
        // area has one.
        Assert.False(brief.ContainsKey("monsters"));
        conn.StashObject = 0x1234;
        conn.StashAt = (319, 272);
        JsonObject full;
        string path = Path.Combine(Path.GetTempPath(), $"localexile-state-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        try { full = CommandsRules.StateJson(conn, brief: false); }
        finally
        {
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
        Assert.Equal(0x1234u, (uint)full["stash"]!["id"]!);
        foreach (string list in new[] { "monsters", "npcs", "objects", "items", "transitions" })
            Assert.IsType<JsonArray>(full[list]);
        Assert.Equal(12, (int)full["inventory"]!["w"]!);

        // The reply: "STATE " and the JSON, the rest on "STATE+ " lines; no line ends inside a surrogate pair.
        string json = JsonSerializer.Serialize(new { names = Enumerable.Range(0, 400).Select(i => $"monster {i} é😀").ToArray() });
        var lines = CampaignState.Lines(json, 100);
        Assert.StartsWith("STATE {", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("STATE+ ", l));
        Assert.Equal(json, lines[0]["STATE ".Length..] + string.Concat(lines.Skip(1).Select(l => l["STATE+ ".Length..])));
        Assert.All(lines, l => Assert.False(char.IsHighSurrogate(l[^1])));
        // A line takes at least one character: a lone high surrogate at length 1 still moves on.
        Assert.Equal(["STATE \ud83d", "STATE+ \ude00"], CampaignState.Lines("\ud83d\ude00", 1));
        Assert.Equal(["STATE " + full.ToJsonString()], CampaignState.Lines(full.ToJsonString(), int.MaxValue));
    }

    static readonly CampaignState.Preset[] Presets =
    [
        new("Act1Complete", ["a1q1-done", "shared"]),
        new("Act2CompleteEramir", ["a2-eramir", "shared"]),
        new("Act2CompleteOak", ["a2-oak"]),
        new("Act3Complete", ["a3-done"]),
        new("ConsoleMapDemo", ["demo"]),
    ];

    [Fact]
    public void Quest_presets_bring_the_earlier_acts_first_presets_and_demo_presets_stand_alone()
    {
        Assert.Equal(["a1q1-done", "shared"], CampaignState.PresetFlags("act1complete", out _, Presets));
        Assert.Equal(["a1q1-done", "shared", "a2-oak"], CampaignState.PresetFlags("Act2CompleteOak", out _, Presets));
        Assert.Equal(["a1q1-done", "shared", "a2-eramir", "a3-done"], CampaignState.PresetFlags("Act3Complete", out _, Presets));
        Assert.Equal(["demo"], CampaignState.PresetFlags("ConsoleMapDemo", out _, Presets));
        Assert.Null(CampaignState.PresetFlags("Act9Complete", out string? error, Presets));
        Assert.Contains("Act1Complete", error);

        // The game's own presets also complete their quests: Act1Complete's quests reach their "complete" rows.
        if (CampaignState.LoadPresets().Count == 0) return;   // no extracted data in this checkout
        var flags = CampaignState.PresetFlags("Act1Complete", out _)!.ToHashSet();
        Assert.Contains("a1q5-NessaCongratulated", flags);
        var states = QuestStates.Default.Value;
        Assert.All(states.FindPreset("Act1Complete")!.Quests.Where(q => states.Of(q).Count > 0),
            q => Assert.True(states.Complete(q, flags.Contains), $"Act1Complete: {q} not complete"));
    }
}
