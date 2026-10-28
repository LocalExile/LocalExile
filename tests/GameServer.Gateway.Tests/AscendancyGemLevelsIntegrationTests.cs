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
    [InlineData(6, 5, "Architect of Chaos", 45399, "Vaal Fireball", 3)]
    [InlineData(4, 4, "Paladin", 34448, "Soul Link", 5)]
    [InlineData(6, 5, "Puppeteer", 18817, "Raise Zombie of Slamming", 5)]
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

    [Fact]
    public async Task Family_dev_replacements_and_Empower_compose_without_raising_support_levels_or_changing_gem_storage()
    {
        using var game = new GatewayCharacter(level: 100, god: true);
        var gem = FamilyGem("Vaal Fireball", 20);
        var support = FamilyGem("Empower Support", 3);
        EquipFamilyGem(game, gem, support);
        var baseline = FamilySkills(game.State);
        await game.Command("/set_stat vaal_skill_gem_level_+ 3");
        AssertFamilyLevel(game.State, gem, 25); // Intrinsic 20 + Empower 2 + family 3.
        Assert.Equal(20, Program.Items.GemLevel(gem));
        Assert.Equal(3, Program.Items.GemLevel(support));
        var baselineSupports = baseline.SelectMany(s => s.Supports).Select(s => s.EffectsPerLevelRow).ToArray();
        Assert.NotEmpty(baselineSupports);
        Assert.Equal(baselineSupports, FamilySkills(game.State).SelectMany(s => s.Supports).Select(s => s.EffectsPerLevelRow));
        await game.Command("/set_stat vaal_skill_gem_level_+ -30");
        AssertFamilyLevel(game.State, gem, 1);
        await game.Command("/set_stat vaal_skill_gem_level_+ off");
        Assert.Equal(baseline.Select(s => s.EffectsPerLevelRow), FamilySkills(game.State).Select(s => s.EffectsPerLevelRow));
        Assert.Equal(gem, game.Items.SocketedGemPlaces().Single(g => !Program.Items.Gem(Program.Items.ByHash(g.Gem.TypeHash)!)!.IsSupport).Gem);
    }

    [Fact]
    public async Task Effective_levels_do_not_raise_intrinsic_requirements_or_activate_an_ineligible_host()
    {
        using var game = new GatewayCharacter(level: 1, god: true);
        var gem = FamilyGem("Vaal Fireball");
        EquipFamilyGem(game, gem);
        await game.Command("/set_stat vaal_skill_gem_level_+ 3");
        Assert.True(Program.Skills.PlayerLevelReq(Program.Skills.EffectRow("Fireball"), 4) > 1);
        AssertFamilyLevel(game.State, gem, 4); // Requirements still read intrinsic level 1, not effective 4.
        var host = game.Items.Items(PlayerInventories.Helmet).Single();
        Assert.NotNull(game.Items.Remove(PlayerInventories.Helmet, host.Id));
        game.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Hubris Circlet")!) with
        { Sockets = [new(SocketColour.Blue, gem)], Links = [1] });
        game.State.Pools = Program.PoolsFor(game.State, 1);
        var vaal = Program.Skills.FindByRow(0, Program.Skills.EffectRow("VaalFireball"), 4)!;
        Assert.DoesNotContain(FamilySkills(game.State), s => s.EffectsPerLevelRow == vaal.EffectsPerLevelRow);
    }
}
