using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    static Item FamilyGem(string name, int level = 1)
    {
        var gem = Program.Items.FindGem(name)!.Value;
        return ItemCatalog.Create(gem.Type) with
        { GemExperience = Program.Items.Gem(gem.Type)!.ExperienceFor(level), GemVariant = gem.Variant.Index, Quality = 7 };
    }

    static void EquipFamilyGem(GatewayCharacter game, Item gem, Item? support = null)
    {
        game.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets = support is null ? [new(SocketColour.Blue, gem)]
                : [new(SocketColour.Blue, gem), new(SocketColour.Blue, support)], Links = [(byte)(support is null ? 1 : 2)],
        });
    }

    static IReadOnlyList<GrantedSkill> FamilySkills(ClientState conn) =>
        ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", conn);

    static void AssertFamilyLevel(ClientState conn, Item gem, int level)
    {
        var effects = Program.Items.GemEffects(gem).ToArray();
        foreach (int effect in effects)
        {
            var expected = Program.Skills.FindByRow(0, effect, level)!;
            Assert.Contains(FamilySkills(conn), s => s.EffectsPerLevelRow == expected.EffectsPerLevelRow);
        }
    }

    [Theory]
    [InlineData(1, 3, "Herald", 40058, "Herald of Ash", 3)]
    public async Task Family_nodes_raise_actual_native_records_and_paid_refund_reload_restore_intrinsic_levels(
        int classRow, byte baseClass, string choice, ushort node, string name, int bonus)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: classRow, baseClass: baseClass, choice: choice,
            refundPoints: 60, level: 100, god: true);
        var gem = FamilyGem(name, 20);
        EquipFamilyGem(game, gem);
        AssertFamilyLevel(game.State, gem, 20);
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        ushort[] path = PathTo(definition, node);
        Assert.Null((await game.Change(path, allocate: true)).Error);
        AssertFamilyLevel(game.State, gem, 20 + bonus);
        AssertFamilyLevel(game.State, gem, 20 + bonus); // Fresh builds cannot accumulate levels.
        AssertFamilyLevel(game.Reload(), gem, 20 + bonus);
        Assert.Equal(20, Program.Items.GemLevel(gem));
        Assert.Equal((byte)7, gem.Quality);
        ushort before = game.State.RefundPoints;
        Assert.Null((await game.Change([node], allocate: false, refundPoints: true)).Error);
        Assert.Equal(before - 5, game.State.RefundPoints);
        AssertFamilyLevel(game.State, gem, 20);
        AssertFamilyLevel(game.Reload(), gem, 20);
        Assert.Null((await game.Change([node], allocate: true)).Error);
        AssertFamilyLevel(game.State, gem, 20 + bonus);
    }

}
