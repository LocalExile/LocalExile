using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    // Inherited Fortune and every small node grant ordinary passive points; the notables carry the class stats.
    static readonly ushort[] AristocratGems = [54114, 48846, 24607, 46836, 61147, 35022, 27473, 30851];
    static readonly ushort[] AristocratAttributes = [54114, 48846, 64905, 11154, 55065, 2748, 35432, 2688];
    static readonly ushort[] AristocratTuition = [54114, 48846, 24607, 46836, 34482, 50743, 21905, 2688];

    static int AristocratStat(ClientState state, string name) =>
        Program.CharacterPlayerStats(state).GetValueOrDefault(Program.Stats.Key(name)!.Value);

    static void ReplaceHelmet(GatewayCharacter game, Item gem, Item? support = null)
    {
        if (game.Items.Items(PlayerInventories.Helmet).SingleOrDefault() is { } host)
            Assert.NotNull(game.Items.Remove(PlayerInventories.Helmet, host.Id));
        EquipFamilyGem(game, gem, support);
    }

    // A skill record also carries character stats: the nodes' own stats and the gear's armour (pools refresh on
    // allocation) differ from the class-less baseline without being skill values.
    static readonly string[] CharacterOnly = ["all_skill_gem_level_+", "non_exceptional_support_gem_level_+",
        "all_skill_gem_quality_+", "global_item_attribute_requirements_+%", "physical_damage_reduction_rating"];

    static (uint Row, string Stats)[] SkillRecords(ClientState state) => [.. FamilySkills(state)
        .Select(s => (s.EffectsPerLevelRow, string.Join(",", (s.Stats ?? [])
            .Where(v => !CharacterOnly.Any(n => Program.Stats.Key(n) == v.Key)).Select(v => $"{v.Key}={v.Value}"))))];

    static uint[] SupportRecords(ClientState state) =>
        [.. FamilySkills(state).SelectMany(s => s.Supports).Select(s => s.EffectsPerLevelRow)];

    [Fact]
    public void Aristocrat_checklist_is_the_actual_graph_and_three_builds_cover_every_node()
    {
        if (!DataAvailable()) return;
        var definition = Program.Ascendancies.Find(4, AscendancyFamily.Phrecia, "Aristocrat")!;
        Assert.Equal(new ushort[] { 33795, 54114, 61147, 24607, 27473, 34482, 3952, 46836, 48846, 35022, 30851, 50743,
            11154, 35432, 2748, 64905, 55065, 2688, 21905 }.Order(), definition.Nodes.Keys.Order());
        Assert.Equal(definition.Nodes.Keys.Where(n => n != definition.StartNode).Order(),
            AristocratGems.Concat(AristocratAttributes).Concat(AristocratTuition).Append((ushort)3952).Distinct().Order());
        Assert.Equal(5, definition.Nodes[48846].PointsGranted);
        Assert.All(definition.Nodes.Values.Where(n => n.Name == "Passive Point"), n => Assert.Equal(1, n.PointsGranted));
    }

}
