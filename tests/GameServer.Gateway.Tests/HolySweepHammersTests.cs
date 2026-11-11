namespace GameServer.Gateway.Tests;

// Holy Sweep's hammers come from its stat set (GrantedEffectStatSets 121 Sweep, 122 SweepAltX): Holy Sweep drops
// holy_sweep_number_of_holy_bolts_to_create hammers on different struck enemies, 35% less damage, unevadable;
// Hammerfalls drops waves of 3 every 700 ms for 3000 ms at 50% less, on the monsters around the swing. Each hammer's
// base radius is the 12-cell secondary area. The draws are the client's: the expected hammers below are what the client
// drew in a live session (2026-11-11, Frida on 0x141b18a90 and 0x141b19980): six Drowned spawned in this order around
// the player at (290, 643), all in one grid tile.
public sealed class HolySweepHammersTests
{
    static HolySweepHammers.Settings? Hammers(SkillCatalog skills, StatCatalog stats, string effectId, int level)
    {
        int effect = skills.EffectRow(effectId);
        Assert.True(effect >= 0);
        var own = skills.EffectStats(effect, level).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        return HolySweepHammers.For(name => stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0);
    }

    static readonly (uint Id, (int X, int Y) At, int Size)[] Drowned =
    [
        (0x8000034, (284, 637), 2), (0x8000035, (290, 637), 2), (0x8000036, (286, 642), 2),
        (0x8000037, (281, 642), 2), (0x8000038, (278, 637), 2), (0x8000039, (281, 632), 2),
    ];

    [Fact]
    public void Holy_sweep_drops_its_hammers_as_the_client_picks_them()
    {
        var skills = new SkillCatalog();
        var stats = new StatCatalog();
        var sweep = Hammers(skills, stats, "Sweep", 20);
        Assert.NotNull(sweep);
        Assert.Equal((5, 12, false, true), (sweep.Count, sweep.BaseRadius, sweep.Hammerfall, sweep.CannotBeEvaded));
        Assert.Equal(0.65, sweep.More, 6);
        Assert.Equal(3, Hammers(skills, stats, "Sweep", 1)!.Count);
        Assert.Null(Hammers(skills, stats, "GroundSlam", 20));

        // Live: aimed at 0x8000036, the client's hit roll kept 0x35, 0x37 and 0x39; fewer than its hammers, so all three,
        // falling in the order of their angle from 45° before the facing (0.651, 1.425, 2.111 rad).
        (int, int) player = (290, 643), target = (286, 642);
        var kept = Drowned.Where(d => d.Id is 0x8000035 or 0x8000037 or 0x8000039).ToList();
        Assert.Equal(2.1112f, HolySweepHammers.SweepAngle(player, target, kept[0].At), 3);
        var picked = HolySweepHammers.Pick(kept, sweep.Count, 0xfffe, 0x1000, d => HolySweepHammers.SweepAngle(player, target, d.At));
        Assert.Equal([0x8000037u, 0x8000039u, 0x8000035u], picked.Select(p => p.Id));
        // More struck enemies than hammers: as many different ones as hammers, the same for the same use.
        var all = Drowned.ToList();
        var some = HolySweepHammers.Pick(all, 3, 0x1234, 0x1000, d => HolySweepHammers.SweepAngle(player, target, d.At));
        Assert.Equal(3, some.Select(p => p.Id).Distinct().Count());
        Assert.Equal(some, HolySweepHammers.Pick(all, 3, 0x1234, 0x1000, d => HolySweepHammers.SweepAngle(player, target, d.At)));
        Assert.Empty(HolySweepHammers.Pick(all.Take(0).ToList(), 3, 0x1234, 0x1000, _ => 0f));

        // The client's sort: stable up to 32 entries, a full sort beyond.
        var small = Enumerable.Range(0, 20).Select(i => (Key: i % 3, Order: i)).ToList();
        HolySweepHammers.ClientSort(small, (a, b) => a.Key < b.Key);
        Assert.Equal(small.OrderBy(e => e.Key).ThenBy(e => e.Order), small);
        var large = Enumerable.Range(0, 300).Select(i => (Key: i * 7919 % 13, Order: i)).ToList();
        HolySweepHammers.ClientSort(large, (a, b) => a.Key < b.Key);
        Assert.Equal(large.Select(e => e.Key).Order(), large.Select(e => e.Key));
        Assert.Equal(Enumerable.Range(0, 300), large.Select(e => e.Order).Order());
    }

    [Fact]
    public void Hammerfalls_drops_its_waves_as_the_client_draws_them()
    {
        var falls = Hammers(new SkillCatalog(), new StatCatalog(), "SweepAltX", 20);
        Assert.NotNull(falls);
        Assert.Equal((3, 12, true, false, 3000), (falls.Count, falls.BaseRadius, falls.Hammerfall, falls.CannotBeEvaded, falls.DurationMs));
        Assert.Equal(0.5, falls.More, 6);
        Assert.Equal(0.7, falls.RateSeconds, 6);

        Assert.Equal(0.7, HolySweepHammers.WaveSeconds(falls.RateSeconds, 0), 6);
        Assert.Equal(0.7f / 1.16f, HolySweepHammers.WaveSeconds(falls.RateSeconds, 16), 6);
        var waves = HolySweepHammers.WaveTimes(falls.RateSeconds, falls.DurationMs / 1000.0);
        Assert.Equal([0.7, 1.4, 2.1, 2.8], waves.Select(t => Math.Round(t, 6)));
        Assert.Empty(HolySweepHammers.WaveTimes(0, 3));

        // Live, use 0xfffd: four waves of three on the six (radius 33 around the player); in the last one the third hammer
        // found nobody under its rolls and fell on a point.
        var taken = new Dictionary<uint, int>();
        var drawn = Enumerable.Range(0, 4).Select(wave => HolySweepHammers.Wave(Drowned, taken, (290, 643), 33, 3,
            (_, _) => true, 0xfffd, 0x1000, wave)).ToList();
        uint? Id(uint low) => 0x8000000 | low;
        Assert.Equal([Id(0x34), Id(0x39), Id(0x38)], drawn[0].Select(h => h.Id));
        Assert.Equal([Id(0x36), Id(0x37), Id(0x35)], drawn[1].Select(h => h.Id));
        Assert.Equal([Id(0x36), Id(0x37), Id(0x34)], drawn[2].Select(h => h.Id));
        Assert.Equal([Id(0x39), Id(0x36), null], drawn[3].Select(h => h.Id));
        Assert.Equal((302, 635), drawn[3][2].At);
        // No walkable ground and no monster: no hammer.
        Assert.Empty(HolySweepHammers.Wave([], [], (100, 200), 29, 3, (_, _) => false, 1, 0x1000, 0));
    }
}
