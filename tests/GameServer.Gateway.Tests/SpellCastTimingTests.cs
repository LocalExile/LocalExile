namespace GameServer.Gateway.Tests;

public class SpellCastTimingTests
{
    static readonly Lazy<SkillCatalog> Skills = new(() => new(Data()));
    static readonly Lazy<StatCatalog> Stats = new(() => new(Data()));

    [Theory]
    [InlineData(0, 750, 133)]
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

    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }
}
