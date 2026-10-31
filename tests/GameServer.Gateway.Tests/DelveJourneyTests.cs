namespace GameServer.Gateway.Tests;

public class DelveJourneyTests
{
    [Fact]
    public void Escort_boundary_has_hysteresis_and_never_requires_reversing_the_route()
    {
        Assert.True(DelveJourney.EscortMoving(true, 89, false));
        Assert.False(DelveJourney.EscortMoving(true, 91, false));
        Assert.False(DelveJourney.EscortMoving(false, 89, false));
        Assert.True(DelveJourney.EscortMoving(false, 64, false));
        Assert.True(DelveJourney.EscortMoving(false, 120, true));
        var trip = new DelveJourney([(0, 0), (40, 0), (40, 40)], 24);
        Assert.Equal(MathF.PI / 2, trip.Heading(0)); // +X
        Assert.Equal(MathF.PI, trip.Heading(40)); // +Y, immediately after the turn
        Assert.Equal(MathF.PI, trip.Heading(80));
    }

    [Fact]
    public void Victory_requires_arrival_and_dead_encounters_and_is_claimed_once()
    {
        var trip = new DelveJourney([(0, 0), (10, 0)], 24);
        Assert.False(trip.TryComplete(false));
        trip.Advance(10, _ => true);
        Assert.False(trip.TryComplete(true));
        Assert.True(trip.TryComplete(false));
        Assert.False(trip.TryComplete(false));
    }

    [Fact]
    public void Depth_bonuses_scale_and_item_count_is_bounded()
    {
        var policy = new DelveRewards(15, 20, 10, 5, 250);
        for (int seed = 0; seed < 100; seed++)
        {
            var first = policy.Roll(2, new Random(seed));
            Assert.InRange(first.Count, 16, 22);
            Assert.Equal(5, first.Rarity);
            Assert.Equal(10, first.QuantityPercent);
            var next = policy.Roll(3, new Random(seed));
            Assert.True(next.Count >= first.Count);
            Assert.Equal(10, next.Rarity);
            Assert.Equal(20, next.QuantityPercent);
            Assert.Equal(250, policy.Roll(int.MaxValue, new Random(seed)).Count);
        }
    }
    [Theory]
    [InlineData(21)]
    [InlineData(24)]
    public void Unequal_tick_sizes_charge_exact_cost_and_stop_at_endpoint(int cost)
    {
        var trip = new DelveJourney([(0, 0), (30, 0), (30, 40)], cost);
        int paid = 0;
        foreach (double distance in new[] { 0d, .1, 5, 16, 9, 7, 30, 100, 100 })
            Assert.True(trip.Advance(distance, n => { paid += n; return true; }));
        Assert.True(trip.Finished);
        Assert.Equal(cost, paid);
        Assert.Equal(cost, trip.Charged);
        Assert.Equal((30, 40), trip.At(trip.Travelled).At);
    }

    [Fact]
    public void Failed_debit_does_not_move_or_consume_progress()
    {
        var trip = new DelveJourney([(0, 0), (10, 0)], 21);
        Assert.False(trip.Advance(5, _ => false));
        Assert.Equal(0, trip.Travelled);
        Assert.Equal(0, trip.Charged);
        Assert.Throws<IOException>(() => trip.Advance(5, _ => throw new IOException()));
        Assert.Equal(0, trip.Travelled);
        Assert.True(trip.Advance(5, n => n == 10));
        Assert.Equal(10, trip.Charged);
        Assert.True(trip.Advance(5, n => n == 11));
        Assert.Equal(21, trip.Charged);
    }
}
