using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class EvasionEntropyTests
{
    sealed class Draws(params int[] values) : Random
    {
        public int Calls;
        public override int Next(int maxValue)
        {
            Assert.Equal(100, maxValue);
            Assert.True(Calls < values.Length, "Unexpected accuracy draw");
            return values[Calls++];
        }
    }

    [Fact]
    public void Attacks_share_the_defenders_sequence_without_a_random_draw_per_attack()
    {
        var random = new Draws(0);
        var state = new EvasionEntropy();
        Assert.False(state.Hits(40, 0, random));
        Assert.True(state.Hits(70, 1, random));
        Assert.Equal(10, state.Value);
        Assert.False(state.Hits(40, 2, random));
        Assert.True(state.Hits(70, 3, random));
        Assert.Equal(20, state.Value);
        Assert.Equal(1, random.Calls);
    }

    [Theory]
    [InlineData(3.33, false, 1)]
    [InlineData(3.331, true, 2)]
    [InlineData(-1, true, 2)]
    public void Only_inactivity_or_a_restarted_clock_resets_the_sequence(double later, bool hit, int calls)
    {
        var random = new Draws(0, 99);
        var state = new EvasionEntropy();
        Assert.False(state.Hits(5, 0, random));
        Assert.Equal(hit, state.Hits(5, later, random));
        Assert.Equal(calls, random.Calls);
    }

    [Fact]
    public void Critical_confirmation_uses_an_independent_draw_and_never_advances_entropy()
    {
        var random = new Draws(99, 49, 50);
        var state = new EvasionEntropy();
        Assert.True(state.Hits(50, 0, random));
        Assert.Equal(49, state.Value);
        Assert.True(EvasionEntropy.ConfirmsCritical(50, random));
        Assert.False(EvasionEntropy.ConfirmsCritical(50, random));
        Assert.Equal(49, state.Value);
        Assert.Equal(0, state.LastAt);
        Assert.True(EvasionEntropy.ConfirmsCritical(100, random));
        Assert.Equal(3, random.Calls);
    }

    [Fact]
    public void Separate_defenders_have_separate_sequences()
    {
        var random = new Draws(99, 0);
        var first = new EvasionEntropy();
        var second = new EvasionEntropy();
        Assert.True(first.Hits(50, 0, random));
        Assert.False(second.Hits(50, 0, random));
        Assert.False(first.Hits(50, 1, random));
        Assert.True(second.Hits(50, 1, random));
        Assert.Equal(2, random.Calls);
    }
}
