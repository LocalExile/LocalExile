namespace GameServer.Gateway.Tests;

public class SpellCastTimingTests
{
    static readonly Lazy<SkillCatalog> Skills = new(() => new(Data()));
    static readonly Lazy<StatCatalog> Stats = new(() => new(Data()));

    [Theory]
    [InlineData(0, 750, 133)]
    [InlineData(100, 375, 267)]
    [InlineData(-80, 3750, 27)]
    public void Fireball_native_base_and_server_duration_apply_cast_speed_once(int speed, int duration, int cps)
    {
        int effect = Skills.Value.EffectRow("Fireball");
        var stats = Skills.Value.EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2);
        stats[Stats.Value.Key("cast_speed_+%")!.Value] = speed;
        var timing = SpellCastTiming.ForSkill(Skills.Value, effect, speed)!.Value;
        timing.ApplyTo(stats, Stats.Value);
        Assert.Equal(750, stats[Stats.Value.Key("base_spell_cast_time_ms")!.Value]);
        Assert.Equal(750, stats[Stats.Value.Key("virtual_base_spell_cast_time_ms")!.Value]);
        Assert.Equal(cps, stats[Stats.Value.Key("hundred_times_casts_per_second")!.Value]);
        Assert.Equal(speed, stats[Stats.Value.Key("cast_speed_+%")!.Value]);
        Assert.Equal(duration, timing.DurationMilliseconds);
        // Native 0x1419e7660 reads 0x24b9, divides by 1000, then by (1 + getter+0x498 / 100).
        double nativeSeconds = stats[Stats.Value.Key("virtual_base_spell_cast_time_ms")!.Value] / 1000.0
            / (1 + speed / 100.0);
        Assert.Equal(duration / 1000.0, nativeSeconds, 6);
        Assert.Equal(duration * (599.0 / 1066), nativeSeconds * 1000 * (599.0 / 1066), 6);
    }

    [Fact]
    public void Previously_predivided_virtual_time_reproduces_the_saved_eighteen_second_probe()
    {
        Assert.Equal(18.75, 3750 / 1000.0 / (1 - .8), 6);
        // logs/monster-hit-recovery-cast.json: native clip length / playback speed, independently observed.
        Assert.Equal(18.75, 1.065999984741211 / .05685332790017128, 5);
        Assert.Equal(.1875, 375 / 1000.0 / 2, 6);
    }

    [Theory]
    [InlineData("Haste")]
    [InlineData("RighteousFire")]
    public void Catalog_instant_spells_remain_instant_without_a_fabricated_cast_rate(string effect)
    {
        int row = Skills.Value.EffectRow(effect);
        Assert.True(Skills.Value.IsInstant(row));
        foreach (int speed in new[] { -100, -80, 0, 100 })
        {
            var timing = SpellCastTiming.ForSkill(Skills.Value, row, speed)!.Value;
            Assert.Equal(Skills.Value.CastMs(row), timing.BaseMilliseconds);
            Assert.Equal(0, timing.DurationMilliseconds);
            Assert.Equal(0, timing.HundredTimesCastsPerSecond);
        }
    }

    [Theory]
    [InlineData("ScorchingRay")]
    [InlineData("FreezingPulse")]
    [InlineData("Portal")]
    public void Other_spell_bases_are_preserved_without_changing_channel_tick_mechanics(string effect)
    {
        int row = Skills.Value.EffectRow(effect);
        var timing = SpellCastTiming.ForSkill(Skills.Value, row, 100)!.Value;
        var stats = Skills.Value.EffectStats(row, 1).ToDictionary(s => s.Item1, s => s.Item2);
        timing.ApplyTo(stats, Stats.Value);
        Assert.Equal(Skills.Value.CastMs(row), stats[Stats.Value.Key("virtual_base_spell_cast_time_ms")!.Value]);
        Assert.Equal((int)Math.Round(Skills.Value.CastMs(row) / 2.0), timing.DurationMilliseconds);
    }

    [Theory]
    [InlineData("PlayerMelee")]
    [InlineData("BurningArrow")]
    [InlineData("Cyclone")]
    [InlineData("EnduringCry")]
    public void Attacks_and_nonspell_actions_keep_their_existing_timing_path(string effect) =>
        Assert.Null(SpellCastTiming.ForSkill(Skills.Value, Skills.Value.EffectRow(effect), 100));

    [Theory]
    [InlineData(750, -100)]
    [InlineData(750, -101)]
    [InlineData(750, int.MinValue)]
    [InlineData(int.MaxValue, -99)]
    [InlineData(-1, 0)]
    public void Invalid_or_unrepresentable_noninstant_casts_have_no_server_duration_or_cast_rate(int baseMs, int speed)
    {
        var timing = SpellCastTiming.Calculate(baseMs, speed);
        Assert.Null(timing.DurationMilliseconds);
        Assert.Equal(0, timing.HundredTimesCastsPerSecond);
    }

    [Fact]
    public void Representable_fractional_milliseconds_keep_existing_rounding_with_a_one_ms_numeric_floor()
    {
        Assert.Equal(564, SpellCastTiming.Calculate(750, 33).DurationMilliseconds);
        Assert.Equal(2, SpellCastTiming.Calculate(5, 100).DurationMilliseconds); // 2.5 rounds to even as before.
        Assert.Equal(1, SpellCastTiming.Calculate(750, int.MaxValue).DurationMilliseconds);
        Assert.Equal(100000, SpellCastTiming.Calculate(750, int.MaxValue).HundredTimesCastsPerSecond);
    }

    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }
}
