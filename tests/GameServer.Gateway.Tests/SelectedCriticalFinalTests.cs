namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Theory]
    [InlineData("SkillGemFireball", false)]
    [InlineData("SkillGemHeavyStrike", true)]
    public void Real_Controlled_Destruction_reduces_selected_critical_chance_and_tracks_support_lifecycle(string active, bool attack)
    {
        string alias = attack ? "main_hand_critical_strike_chance" : "spell_critical_strike_chance";
        var spec = new SelectedMiscCase(active, "SupportGemControlledDestruction", "SupportControlledDestruction",
            "critical_strike_chance_+%", "support_controlled_destruction_critical_strike_chance_+%_final", alias, 100, -80, 0);
        using var game = new SelectedMiscCharacter(spec);
        int other = game.Read(other: true).Get(alias);
        void Check(int chance, bool supported)
        {
            var view = game.Read();
            Assert.Equal(chance, view.Get(alias));
            Assert.Equal(chance, ProgramValue<int>("SupportedCriticalChance", game.State, view.Skill.Id));
            Assert.Equal(chance, game.Read().Get(alias));
            Assert.Equal(1000, Program.CharacterPlayerStats(game.State)[SelectedMiscKey(alias)]);
            Assert.Equal(other, game.Read(other: true).Get(alias));
            if (supported) game.AssertSupported(view);
            else Assert.Empty(view.Applied);
        }
        Check(1000, false);
        game.Socket();
        Check(200, true); // (5% base * 200% increased bucket) * 20% less-chance factor
        game.Socket(linked: false);
        Check(1000, false);
        game.Socket();
        Check(200, true);
        game.SetQualifier(false);
        game.AssertHostInactive();
        game.SetQualifier(true);
        Check(200, true);
        game.Socket(support: false);
        Check(1000, false);
    }
}
