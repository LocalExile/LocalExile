using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public async Task Accuracy_commands_replace_precision_and_restore_sources_without_relog()
    {
        using var game = LevelPanelCharacter(40);
        var state = game.State;
        int baseline = state.Pools.Accuracy;
        state.Auras[0x103] = new(0x103, Program.Skills.EffectRow("Precision"), "Precision", "Precision", 1, 1293, 22, 0);
        state.Pools = Program.PoolsFor(state, 40);
        int sourced = state.Pools.Accuracy;
        Assert.True(sourced > baseline);

        Assert.Null((await game.Command("/set_stat accuracy_rating 0")).Error);
        Assert.Equal(baseline, state.Pools.Accuracy); // Explicit zero removes the whole flat source, including Precision.
        Assert.Null((await game.Command("/set_stat accuracy_rating 100")).Error);
        Assert.Equal(baseline + 100, state.Pools.Accuracy);
        Assert.Null((await game.Command("/set_stat accuracy_rating_+% 33")).Error);
        Assert.Equal((baseline + 100) * 133 / 100, state.Pools.Accuracy);
        Assert.Null((await game.Command("/set_stat accuracy_rating off")).Error);
        Assert.Equal(sourced * 133 / 100, state.Pools.Accuracy);
        Assert.Null((await game.Command("/set_stat accuracy_rating_+% off")).Error);
        Assert.Equal(sourced, state.Pools.Accuracy);
        state.Auras.Clear();
        state.Pools = Program.PoolsFor(state, 40);
        Assert.Equal(baseline, state.Pools.Accuracy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public void Real_Additional_Accuracy_recomputes_selected_rating_and_reference_hit_chances(int quality)
    {
        var spec = new SelectedMiscCase("SkillGemHeavyStrike", "SupportGemAdditionalAccuracy", "SupportAdditionalAccuracy",
            "accuracy_rating_+%", "accuracy_rating", "main_hand_accuracy_rating", 0, 74, quality);
        using var game = new SelectedMiscCharacter(spec);
        int basis = game.State.Pools.Accuracy;
        game.SetActor(33);
        Assert.Equal(33, game.State.Pools.AccuracyPct);
        int actor = (int)((long)basis * 133 / 100);
        Assert.Equal(actor, game.State.Pools.Accuracy);
        game.Socket();
        Check(true);
        game.Socket(linked: false);
        Check(false);
        game.Socket();
        Check(true);
        game.SetQualifier(false);
        Check(true); // Level-one Additional Accuracy still qualifies at 14 Dexterity; the base rating changes.
        game.SetQualifier(true);
        Check(true);
        game.Socket(support: false);
        Check(false);

        void Check(bool supported)
        {
            // Recalculate the current zero-increase base without reconstructing it from a floored final rating.
            game.SetActor(0);
            int currentBase = game.State.Pools.Accuracy;
            game.SetActor(33);
            int ordinary = (int)((long)currentBase * 133 / 100);
            int selected = supported ? (int)((long)(currentBase + 74) * (133 + quality) / 100) : ordinary;
            var view = game.Read();
            Assert.Equal(selected, view.Get("main_hand_accuracy_rating"));
            Assert.Equal(selected, ProgramValue<AttackAccuracyProfile>("SupportedAccuracy", game.State, view.Skill.Id).Rating);
            Assert.Equal(DamageModel.AccuracyHitChance(13445, selected), view.Get("main_hand_chance_to_hit_%"));
            Assert.Equal(DamageModel.AccuracyHitChance(19964, selected), view.Get("main_hand_chance_to_hit_evasive_monsters_%"));
            Assert.Equal(ordinary, game.State.Pools.Accuracy);
            Assert.Equal(ordinary, game.Read(other: true).Get("main_hand_accuracy_rating"));
            Assert.Equal(ordinary, Program.CharacterPlayerStats(game.State)[SelectedMiscKey("main_hand_accuracy_rating")]);
            if (supported) game.AssertSupported(view);
            else Assert.Empty(view.Applied);
        }
    }
}
