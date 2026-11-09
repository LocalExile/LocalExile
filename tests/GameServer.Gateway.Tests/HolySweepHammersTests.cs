namespace GameServer.Gateway.Tests;

// Holy Sweep's hammers come from its stat set (GrantedEffectStatSets 121 Sweep, 122 SweepAltX): Holy Sweep drops
// holy_sweep_number_of_holy_bolts_to_create hammers on different struck enemies, 35% less damage, unevadable;
// Hammerfalls drops waves of 3 every 700 ms for 3000 ms at 50% less, on the monsters around the swing. Each hammer's base radius is the 12-cell secondary area.
public sealed class HolySweepHammersTests
{
    static HolySweepHammers.Settings? Hammers(SkillCatalog skills, StatCatalog stats, string effectId, int level)
    {
        int effect = skills.EffectRow(effectId);
        Assert.True(effect >= 0);
        var own = skills.EffectStats(effect, level).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        return HolySweepHammers.For(name => stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0);
    }

    [Fact]
    public void Holy_sweep_drops_its_hammers_on_different_struck_enemies()
    {
        var skills = new SkillCatalog();
        var stats = new StatCatalog();
        var sweep = Hammers(skills, stats, "Sweep", 20);
        Assert.NotNull(sweep);
        Assert.Equal((5, 12, false, true), (sweep.Count, sweep.BaseRadius, sweep.Hammerfall, sweep.CannotBeEvaded));
        Assert.Equal(0.65, sweep.More, 6);
        Assert.Equal(3, Hammers(skills, stats, "Sweep", 1)!.Count);
        Assert.Null(Hammers(skills, stats, "GroundSlam", 20));

        var random = new Random(7);
        var struck = Enumerable.Range(1, 8).ToList();
        var picked = HolySweepHammers.Pick(struck, sweep.Count, random);
        Assert.Equal(5, picked.Count);
        Assert.Equal(5, picked.Distinct().Count());
        Assert.All(picked, p => Assert.Contains(p, struck));
        // Fewer struck enemies than hammers: one hammer each, never two on the same enemy.
        Assert.Equal([2, 4], HolySweepHammers.Pick([2, 4], sweep.Count, random).Order());
        Assert.Empty(HolySweepHammers.Pick(new List<int>(), sweep.Count, random));
    }

    [Fact]
    public void Hammerfalls_drops_waves_on_the_monsters_around_the_swing()
    {
        var falls = Hammers(new SkillCatalog(), new StatCatalog(), "SweepAltX", 20);
        Assert.NotNull(falls);
        Assert.Equal((3, 12, true, false, 3000), (falls.Count, falls.BaseRadius, falls.Hammerfall, falls.CannotBeEvaded, falls.DurationMs));
        Assert.Equal(0.5, falls.More, 6);
        Assert.Equal(0.7, falls.RateSeconds, 6);

        var waves = HolySweepHammers.WaveTimes(falls.RateSeconds, falls.DurationMs / 1000.0);
        Assert.Equal([0.7, 1.4, 2.1, 2.8], waves.Select(t => Math.Round(t, 6)));
        Assert.Equal(5, HolySweepHammers.WaveTimes(0.7, 3.5).Count);
        Assert.Empty(HolySweepHammers.WaveTimes(0, 3));

        // A wave: one hammer per monster in reach (radius plus size), fresh ones first; with fewer monsters than hammers,
        // the rest fall on walkable points within the radius.
        var random = new Random(11);
        var taken = new Dictionary<uint, int>();
        (uint, (int, int), int)[] monsters = [(1, (110, 200), 0), (2, (100, 220), 0), (3, (131, 200), 3), (4, (140, 200), 0)];
        var first = HolySweepHammers.Wave(monsters, taken, (100, 200), 29, 3, (_, _) => true, random);
        Assert.Equal([1u, 2u, 3u], first.Select(h => h.Id!.Value).Order());
        var second = HolySweepHammers.Wave([monsters[0]], taken, (100, 200), 29, 3, (_, _) => true, random);
        Assert.Equal(3, second.Count);
        Assert.All(second.Where(h => h.Id is null), h => Assert.True(Math.Sqrt((h.At.X - 100) * (h.At.X - 100)
            + (h.At.Y - 200) * (h.At.Y - 200)) <= 29.75));
        // No walkable ground and no monster: no hammer.
        Assert.Empty(HolySweepHammers.Wave([], taken, (100, 200), 29, 3, (_, _) => false, random));
    }
}
