namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public void Mana_Leech_support_cannot_grant_attack_leech_to_a_spell()
    {
        var spec = LeechSpec("mana", "attack") with { Active = "SkillGemFireball" };
        using var game = new SelectedMiscCharacter(spec);
        game.Socket();
        game.AssertQualified();
        var selected = game.Read();
        Assert.Empty(selected.Applied);
        Assert.Empty(selected.Skill.Supports);
        Assert.Equal(0, selected.Own.GetValueOrDefault(SelectedMiscKey(spec.OwnSource)));
        Assert.Equal(10, selected.Get(spec.Alias));
        Assert.Equal(0, selected.Get("mana_leech_from_spell_damage_permyriad"));
    }

    [Theory]
    [InlineData("life", "spell")]
    [InlineData("mana", "attack")]
    [InlineData("energy_shield", "spell")]
    public void Leech_support_requirement_changes_refresh_selected_sources_without_affecting_other_skills(string pool, string type)
    {
        var spec = LeechSpec(pool, type);
        using var game = new SelectedMiscCharacter(spec);
        game.Socket();
        game.AssertSupported(game.Read());
        Assert.Equal(spec.Actor + spec.Own, game.Read().Get(spec.Alias));
        game.SetQualifier(false);
        game.AssertHostInactive();
        game.SetQualifier(true);
        game.AssertSupported(game.Read());
        Assert.Equal(spec.Actor + spec.Own, game.Read().Get(spec.Alias));
        game.AssertControls(spec.Actor);
    }
}
