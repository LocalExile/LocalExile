using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class DamageConversionPathTests
{
    const DamageTypeHistory P = DamageTypeHistory.Physical;
    const DamageTypeHistory L = DamageTypeHistory.Lightning;
    const DamageTypeHistory C = DamageTypeHistory.Cold;
    const DamageTypeHistory F = DamageTypeHistory.Fire;
    const DamageTypeHistory H = DamageTypeHistory.Chaos;

    static void Path(IReadOnlyList<DamageConversionPath> paths, DamageType type, DamageTypeHistory history,
        double min, double max)
    {
        var path = Assert.Single(paths, p => p.Type == type && p.History == history);
        Assert.Equal(min, path.Range.Min, 10);
        Assert.Equal(max, path.Range.Max, 10);
    }

    static DamagePacket Collapse(IReadOnlyList<DamageConversionPath> paths) =>
        paths.Aggregate(DamagePacket.Empty, (packet, path) => packet.Add(path.Type, path.Range));

    [Theory]
    [InlineData(DamageType.Physical, 1)]
    [InlineData(DamageType.Lightning, 2)]
    [InlineData(DamageType.Cold, 4)]
    [InlineData(DamageType.Fire, 8)]
    [InlineData(DamageType.Chaos, 16)]
    public void History_bits_identify_the_five_forward_types(DamageType type, int mask)
    {
        Assert.Equal((DamageTypeHistory)mask, DamageTypes.HistoryOf(type));
        Assert.Equal((DamageTypeHistory)14, DamageTypeHistory.Elemental);
    }

    [Fact]
    public void An_unknown_type_cannot_create_an_undefined_history()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DamageTypes.HistoryOf((DamageType)5));
    }

    [Fact]
    public void No_conversion_keeps_original_ranges_and_omits_absent_components()
    {
        var packet = DamagePacket.Of(DamageType.Physical, 1.25, 2.5).Add(DamageType.Fire, new(3.75, 4.5));
        var paths = new DamageConversion().Trace(packet);

        Assert.Equal(2, paths.Count);
        Path(paths, DamageType.Physical, P, 1.25, 2.5);
        Path(paths, DamageType.Fire, F, 3.75, 4.5);
        Assert.Equal(packet.Components.ToArray(), Collapse(paths).Components.ToArray());
    }

    [Fact]
    public void Empty_input_has_no_paths_even_with_conversion_and_gain_sources()
    {
        var conversion = new DamageConversion().Add(DamageType.Physical, DamageType.Fire, 50)
            .AddGain(DamageType.Physical, DamageType.Chaos, 100);

        Assert.Empty(conversion.Trace(DamagePacket.Empty));
    }

    [Fact]
    public void Partial_conversion_preserves_source_and_destination_histories()
    {
        var paths = new DamageConversion().Add(DamageType.Physical, DamageType.Fire, 50)
            .Trace(DamagePacket.Of(DamageType.Physical, 100, 200));

        Assert.Equal(2, paths.Count);
        Path(paths, DamageType.Physical, P, 50, 100);
        Path(paths, DamageType.Fire, P | F, 50, 100);
    }

    [Fact]
    public void Native_destination_and_converted_damage_keep_separate_histories()
    {
        var packet = DamagePacket.Of(DamageType.Physical, 100, 200).Add(DamageType.Fire, new(10, 20));
        var paths = new DamageConversion().Add(DamageType.Physical, DamageType.Fire, 100).Trace(packet);

        Assert.Equal(2, paths.Count);
        Assert.All(paths, p => Assert.Equal(DamageType.Fire, p.Type));
        Path(paths, DamageType.Fire, P | F, 100, 200);
        Path(paths, DamageType.Fire, F, 10, 20);
        Assert.Equal(new DamageRange(110, 220), Collapse(paths)[DamageType.Fire]);
    }

    [Fact]
    public void A_partial_chain_retains_each_intermediate_type_and_fraction()
    {
        var conversion = new DamageConversion().Add(DamageType.Physical, DamageType.Lightning, 50)
            .Add(DamageType.Lightning, DamageType.Cold, 50).Add(DamageType.Cold, DamageType.Fire, 50);
        var paths = conversion.Trace(DamagePacket.Of(DamageType.Physical, 100, 200));

        Assert.Equal(4, paths.Count);
        Path(paths, DamageType.Physical, P, 50, 100);
        Path(paths, DamageType.Lightning, P | L, 25, 50);
        Path(paths, DamageType.Cold, P | L | C, 12.5, 25);
        Path(paths, DamageType.Fire, P | L | C | F, 12.5, 25);
    }

    [Fact]
    public void Gain_preserves_the_source_and_can_continue_through_conversion()
    {
        var conversion = new DamageConversion().AddGain(DamageType.Physical, DamageType.Lightning, 50)
            .Add(DamageType.Lightning, DamageType.Cold, 50);
        var paths = conversion.Trace(DamagePacket.Of(DamageType.Physical, 100, 200));

        Assert.Equal(3, paths.Count);
        Path(paths, DamageType.Physical, P, 100, 200);
        Path(paths, DamageType.Lightning, P | L, 25, 50);
        Path(paths, DamageType.Cold, P | L | C, 25, 50);
    }

    [Fact]
    public void Conversion_and_gain_merge_only_when_their_history_and_final_type_match()
    {
        var conversion = new DamageConversion().Add(DamageType.Physical, DamageType.Fire, 50)
            .AddGain(DamageType.Physical, DamageType.Fire, 25);
        var paths = conversion.Trace(DamagePacket.Of(DamageType.Physical, 100, 200));

        Assert.Equal(2, paths.Count);
        Path(paths, DamageType.Physical, P, 50, 100);
        Path(paths, DamageType.Fire, P | F, 75, 150);
    }

    [Fact]
    public void Skill_conversion_retains_priority_over_global_conversion()
    {
        var conversion = new DamageConversion().AddSkill(DamageType.Physical, DamageType.Fire, 80)
            .AddGlobal(DamageType.Physical, DamageType.Cold, 80);
        var paths = conversion.Trace(DamagePacket.Of(DamageType.Physical, 100, 100));

        Assert.Equal(2, paths.Count);
        Path(paths, DamageType.Cold, P | C, 20, 20);
        Path(paths, DamageType.Fire, P | F, 80, 80);
    }

    [Fact]
    public void Overallocated_skill_conversion_discards_global_conversion()
    {
        var conversion = new DamageConversion().AddSkill(DamageType.Physical, DamageType.Lightning, 60)
            .AddSkill(DamageType.Physical, DamageType.Fire, 60).AddGlobal(DamageType.Physical, DamageType.Cold, 80);
        var paths = conversion.Trace(DamagePacket.Of(DamageType.Physical, 100, 100));

        Assert.Equal(2, paths.Count);
        Path(paths, DamageType.Lightning, P | L, 50, 50);
        Path(paths, DamageType.Fire, P | F, 50, 50);
    }

    [Fact]
    public void Normalized_whole_percentages_and_independent_gain_match_existing_quantities()
    {
        var conversion = new DamageConversion().Add(DamageType.Physical, DamageType.Fire, 80)
            .Add(DamageType.Physical, DamageType.Cold, 40).AddGain(DamageType.Physical, DamageType.Fire, 20);
        var paths = conversion.Trace(DamagePacket.Of(DamageType.Physical, 100, 100));

        Assert.Equal(2, paths.Count);
        Path(paths, DamageType.Cold, P | C, 33, 33);
        Path(paths, DamageType.Fire, P | F, 87, 87);
    }

    [Fact]
    public void Per_destination_rounding_preserves_the_existing_rounded_total_boundary()
    {
        var conversion = new DamageConversion().Add(DamageType.Physical, DamageType.Cold, 101)
            .Add(DamageType.Physical, DamageType.Fire, 99);
        var packet = DamagePacket.Of(DamageType.Physical, 100, 100);
        var paths = conversion.Trace(packet);

        // Existing Apply rounds 50.5/49.5 to 51/50 independently and retains no physical remainder.
        // Trace preserves that local quantity rule; it does not introduce a new normalization correction.
        Path(paths, DamageType.Cold, P | C, 51, 51);
        Path(paths, DamageType.Fire, P | F, 50, 50);
        Assert.Equal(101, Collapse(paths).MinTotal);
        Assert.Equal(101, conversion.Apply(packet).MinTotal);
    }

    [Fact]
    public void A_chaos_endpoint_retains_all_elemental_visits_as_one_composite_qualification()
    {
        var conversion = new DamageConversion().Add(DamageType.Physical, DamageType.Lightning, 100)
            .Add(DamageType.Lightning, DamageType.Cold, 100).Add(DamageType.Cold, DamageType.Fire, 100)
            .Add(DamageType.Fire, DamageType.Chaos, 100);
        var path = Assert.Single(conversion.Trace(DamagePacket.Of(DamageType.Physical, 100, 100)));

        Assert.Equal(DamageType.Chaos, path.Type);
        Assert.Equal(P | L | C | F | H, path.History);
        // A caller tests overlap once; three elemental visits do not represent three elemental modifiers.
        Assert.Equal(DamageTypeHistory.Elemental, path.History & DamageTypeHistory.Elemental);
        Assert.Equal(new DamageRange(100, 100), path.Range);
    }

    [Fact]
    public void All_forward_gain_routes_are_bounded_by_the_31_distinct_nonempty_histories()
    {
        var conversion = new DamageConversion();
        var packet = DamagePacket.Empty;
        foreach (var from in DamageTypes.ConversionOrder)
        {
            packet = packet.Add(from, new(1, 1));
            foreach (var to in DamageTypes.ConversionOrder.Where(to => DamageTypes.CanConvert(from, to)))
                conversion.AddGain(from, to, 100);
        }
        var paths = conversion.Trace(packet);

        Assert.Equal(31, paths.Count);
        Assert.Equal(Enumerable.Range(1, 31), paths.Select(p => (int)p.History).Order());
        Assert.Equal(31, paths.Select(p => (p.Type, p.History)).Distinct().Count());
        Assert.All(paths, p =>
        {
            Assert.Equal(new DamageRange(1, 1), p.Range);
            Assert.True(p.History.HasFlag(DamageTypes.HistoryOf(p.Type)));
            Assert.True((int)p.History < 2 * (int)DamageTypes.HistoryOf(p.Type));
        });
    }

    [Fact]
    public void Tracing_does_not_mutate_the_packet_coefficients_or_an_earlier_result()
    {
        var conversion = new DamageConversion().Add(DamageType.Physical, DamageType.Fire, 50)
            .AddGain(DamageType.Physical, DamageType.Fire, 25);
        var packet = DamagePacket.Of(DamageType.Physical, 100, 200);
        var before = conversion.Apply(packet);
        var paths = conversion.Trace(packet);

        Assert.Equal(new DamageRange(100, 200), packet[DamageType.Physical]);
        Assert.True(packet[DamageType.Fire].IsZero);
        Assert.Equal(50, conversion.ConversionPercent(DamageType.Physical, DamageType.Fire));
        Assert.Equal(25, conversion.GainPercent(DamageType.Physical, DamageType.Fire));
        Assert.Equal(before.Components.ToArray(), conversion.Apply(packet).Components.ToArray());
        Assert.Equal(paths.ToArray(), conversion.Trace(packet).ToArray());
        var collection = Assert.IsAssignableFrom<IList<DamageConversionPath>>(paths);
        Assert.Throws<NotSupportedException>(() => collection[0] = default);

        conversion.AddGain(DamageType.Physical, DamageType.Fire, 25);
        Path(paths, DamageType.Fire, P | F, 75, 150);
    }

    [Fact]
    public void Collapsed_paths_match_quantity_only_apply_for_mixed_fractional_inputs()
    {
        var random = new Random(271029);
        for (int example = 0; example < 100; example++)
        {
            var packet = DamagePacket.Empty;
            var conversion = new DamageConversion();
            foreach (var from in DamageTypes.ConversionOrder)
            {
                packet = packet.Add(from, new(random.Next(100) + .125, random.Next(100, 200) + .375));
                foreach (var to in DamageTypes.ConversionOrder.Where(to => DamageTypes.CanConvert(from, to)))
                {
                    conversion.AddGlobal(from, to, random.Next(151));
                    conversion.AddSkill(from, to, random.Next(101));
                    conversion.AddGain(from, to, random.Next(101));
                }
            }
            var expected = conversion.Apply(packet);
            var paths = conversion.Trace(packet);
            var actual = Collapse(paths);
            Assert.InRange(paths.Count, 1, 31);
            foreach (var type in DamageTypes.ConversionOrder)
            {
                // Separately retained histories can regroup floating additions; quantities agree within roundoff.
                Assert.InRange(Math.Abs(expected[type].Min - actual[type].Min), 0, 1e-9);
                Assert.InRange(Math.Abs(expected[type].Max - actual[type].Max), 0, 1e-9);
            }
        }
    }
}
