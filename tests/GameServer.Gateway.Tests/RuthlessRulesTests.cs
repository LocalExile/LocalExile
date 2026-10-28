using System.Text.Json;

public class RuthlessRulesTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "localexile-ruthless-rules-" + Guid.NewGuid());
    string Settings => Path.Combine(folder, "ruthless.json");
    readonly Clock clock = new();
    int version;

    sealed class Clock : TimeProvider
    {
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now = now.AddSeconds(2);
    }

    void Write(string json)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Settings, json);
        File.SetLastWriteTimeUtc(Settings, new DateTime(2026, 1, 1).AddSeconds(++version));
        clock.Advance();
    }

    [Fact]
    public void Valid_edits_replace_the_snapshot_without_restarting_the_reader()
    {
        Write("""{"itemDropMultiplier":0.15,"gemExperienceMultiplier":0.4}""");
        var file = new RuthlessRulesFile(Settings, time: clock);
        var before = file.Current;
        Assert.Equal(0.15, before.ItemDropMultiplier);
        Assert.Equal(0.4, before.GemExperienceMultiplier);
        Write("""{"itemDropMultiplier":0.6,"gemExperienceMultiplier":0.8}""");
        var after = file.Current;
        Assert.Equal(0.6, after.ItemDropMultiplier);
        Assert.Equal(0.8, after.GemExperienceMultiplier);
        Assert.Equal(0.15, before.ItemDropMultiplier); // Existing operations keep their complete snapshot.
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"itemDropMultiplier\":2}")]
    [InlineData("{\"itemDropMultiplier\":-1}")]
    [InlineData("{\"experiencePenaltyStartLevel\":101}")]
    [InlineData("{\"itemDropsMultipler\":0.1}")]
    public void Invalid_or_partial_edits_keep_the_entire_last_valid_configuration(string invalid)
    {
        Write("""{"itemDropMultiplier":0.5,"goldMultiplier":0.75}""");
        var messages = new List<string>();
        var file = new RuthlessRulesFile(Settings, messages.Add, clock);
        var valid = file.Current;
        Write(invalid);
        Assert.Same(valid, file.Current);
        Assert.Contains(messages, m => m.Contains("keeping previous settings"));
        Write("""{"itemDropMultiplier":0.25}""");
        Assert.Equal(0.25, file.Current.ItemDropMultiplier);
    }

    [Fact]
    public void Missing_file_uses_defaults_and_a_later_created_file_is_picked_up()
    {
        var file = new RuthlessRulesFile(Settings, time: clock);
        Assert.Equal(new RuthlessRules(), file.Current);
        Write("""{"goldMultiplier":0.7}""");
        Assert.Equal(0.7, file.Current.GoldMultiplier);
    }

    [Fact]
    public void Endgame_penalty_compounds_after_configured_level_and_zero_disables_rewards()
    {
        var rules = new RuthlessRules();
        Assert.Equal(1, rules.ExperienceMultiplier(67));
        Assert.Equal(0.9, rules.ExperienceMultiplier(68), 9);
        Assert.Equal(0.81, rules.ExperienceMultiplier(69), 9);
        Assert.Equal(25u, RuthlessRules.Scale(100, rules.GemExperienceMultiplier));
        Assert.Equal(0u, RuthlessRules.Scale(100, 0));
        Assert.Equal(uint.MaxValue, RuthlessRules.Scale(uint.MaxValue, 1));
        Assert.Throws<ArgumentException>(() => new RuthlessRules { ItemDropMultiplier = double.NaN }.Validate());
    }

    [Fact]
    public void Only_Ruthless_reconnects_keep_saved_pools_and_legacy_saves_remain_compatible()
    {
        var save = new CharacterSaves.Save([], Life: 13, Mana: 7);
        Assert.Equal((13u, 7u), Program.InitialVitals(true, save, 50, 40));
        Assert.Equal((10u, 5u), Program.InitialVitals(true, save, 10, 5));
        Assert.Equal((50u, 40u), Program.InitialVitals(false, save, 50, 40));
        Assert.Equal((50u, 40u), Program.InitialVitals(true, null, 50, 40));
        Assert.Equal((50u, 40u), Program.InitialVitals(true, new([], Life: 0, Mana: 0), 50, 40));
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}
