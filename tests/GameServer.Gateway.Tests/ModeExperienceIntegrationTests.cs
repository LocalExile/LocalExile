using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public void Ruthless_old_area_gains_scale_from_the_shared_current_XP_level()
    {
        using var game = new GatewayCharacter(level: 80, family: AscendancyFamily.Normal, choice: "Slayer", ruthless: true);
        var oldArea = game.Reload();
        oldArea.Pools = Program.PoolsFor(oldArea, 1);
        oldArea.Experience = CharacterStats.ExperienceFor(1);
        uint before = game.State.Experience;
        var rules = Program.RuthlessSettings.Current;
        const uint gain = 10_000;
        uint scaled = RuthlessRules.Scale(gain, rules.ExperienceMultiplier(80));

        var first = Program.RecordExperienceGain(game.State, gain);
        if (scaled == 0)
        {
            Assert.Null(first);
            Assert.Null(Program.RecordExperienceGain(oldArea, gain));
            Assert.Equal(before, CharacterSaves.Load(game.Character)!.Experience);
            return;
        }
        Assert.NotNull(first);
        Assert.Equal(before, first.Before);
        Assert.Equal(before + scaled, first.After);
        Assert.Equal(80, GameServer.Core.World.Progression.LevelFor(first.After, CharacterStats.LevelStarts));

        var second = Program.RecordExperienceGain(oldArea, gain);
        Assert.NotNull(second);
        Assert.Equal(first.After, second.Before);
        Assert.Equal(first.After + scaled, second.After);
        Assert.Equal(second.After, oldArea.Experience);
        Assert.Equal(second.After, CharacterSaves.Load(game.Character)!.Experience);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(0u, true)]
    public void Hardcore_death_blocks_the_shared_XP_ledger_before_any_gain_is_written(uint life, bool recorded)
    {
        using var game = new GatewayCharacter(level: 2, family: AscendancyFamily.Normal, choice: "Slayer", hardcore: true);
        game.State.PlayerLife = life;
        game.State.HardcoreDeathRecorded = recorded;
        uint xp = game.State.Experience;
        string saved = game.SavedJson;

        Assert.Null(Program.RecordExperienceGain(game.State, 1000));

        Assert.Equal(xp, game.State.Experience);
        Assert.Equal(xp, CharacterSaves.Load(game.Character)!.Experience);
        Assert.Equal(saved, game.SavedJson);
    }

    [Fact]
    public async Task A_queued_predeath_XP_publication_cannot_heal_or_resync_a_dead_Hardcore_character()
    {
        using var game = new GatewayCharacter(level: 2, family: AscendancyFamily.Normal, choice: "Slayer", hardcore: true);
        var gain = Program.RecordExperienceGain(game.State,
            CharacterStats.ExperienceFor(3) - CharacterStats.ExperienceFor(2))!;
        string saved = game.SavedJson;
        var auth = new LevelControl();

        var result = await game.QueuedGain(gain, auth.Call, () =>
        {
            lock (game.State.Monsters)
            {
                game.State.PlayerLife = 0;
                game.State.HardcoreDeathRecorded = true;
            }
            return Task.CompletedTask;
        });

        Assert.Empty(result.Bytes);
        Assert.Empty(auth.Requests);
        Assert.Equal(2, game.State.Pools.Level);
        Assert.Equal(0u, game.State.PlayerLife);
        Assert.Equal(saved, game.SavedJson);
    }

    [Fact]
    public async Task A_development_level_reset_does_not_modify_an_already_dead_Hardcore_character()
    {
        using var game = new GatewayCharacter(level: 2, family: AscendancyFamily.Normal, choice: "Slayer", hardcore: true);
        game.State.PlayerLife = 0;
        game.State.HardcoreDeathRecorded = true;
        uint xp = game.State.Experience;
        string saved = game.SavedJson;

        var result = await game.SetLevel(3);

        Assert.Empty(result.Bytes);
        Assert.Equal(xp, game.State.Experience);
        Assert.Equal(2, game.State.Pools.Level);
        Assert.Equal(0u, game.State.PlayerLife);
        Assert.Equal(saved, game.SavedJson);
    }
}
