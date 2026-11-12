namespace GameServer.Gateway.Tests;

public class QuestEncounterRestoreTests
{
    [Fact]
    public void Restoring_checks_the_encounters_own_area_and_completion_rules()
    {
        var quest = new Quests.Quest("test")
        {
            new Quests.ThresholdRule("roof", "kitava", 50, ["roof-done"]),
            new Quests.KilledAllRule("bridge", ["sun", "moon"], ["bridge-done"]),
            new Quests.KillRule("pyramid", "oversoul", ["pyramid-done"]),
        };
        var rules = new Quests.RuleSet([quest], quest.ToDictionary(r => r, _ => quest.Id));
        var flags = new HashSet<string> { "delivered" };
        Assert.False(rules.SpawnEncounterFinished("roof", "kitava", flags.Contains));
        flags.UnionWith(["roof-done", "bridge-done", "pyramid-done"]);
        Assert.True(rules.SpawnEncounterFinished("roof", "kitava", flags.Contains));
        Assert.True(rules.SpawnEncounterFinished("bridge", "moon", flags.Contains));
        Assert.True(rules.SpawnEncounterFinished("bridge", "sun", flags.Contains));
        Assert.True(rules.SpawnEncounterFinished("pyramid", "oversoul", flags.Contains));
        Assert.False(rules.SpawnEncounterFinished("another-area", "kitava", flags.Contains));
    }
}
