namespace GameServer.Gateway.Tests;

public class AnimationVariantSelectorTests
{
    // Independently replayed from 3.29.3b's initializer/transition/tempering instructions. These catch a stock
    // TinyMT32 implementation, the wrong shift direction, missing uint wrap, or the wrong warm-up count.
    [Theory]
    [InlineData(0u, 3762700985u, 711479187u, 552704151u)]
    [InlineData(1u, 2987096583u, 3822264440u, 3314259309u)]
    [InlineData(4097u, 3101734177u, 585665513u, 811100496u)]
    [InlineData(65535u, 2495494237u, 3990337031u, 162047813u)]
    public void Native_random_matches_literal_instruction_vectors(uint seed, uint first, uint second, uint third)
    {
        var random = new AnimationVariantSelector.NativeRandom(seed);
        Assert.Equal(first, random.NextUInt());
        Assert.Equal(second, random.NextUInt());
        Assert.Equal(third, random.NextUInt());
    }

    [Theory]
    [InlineData(4097u, 3)]
    [InlineData(4098u, 1)]
    [InlineData(4099u, 1)]
    [InlineData(4100u, 3)]
    public void Four_entry_pool_matches_native_ordinary_wand_captures(uint sequence, int selected)
    {
        // ACT order: attack1_wand, attack2_wand, attack3_wand, attack4_wand. Ground RMB captures
        // artifacts/wand-ai-validation/wand-choice-click-1..4.json use the same zero-extended input sequences.
        Assert.Equal(selected, AnimationVariantSelector.Select(sequence, [1, 1, 1, 1]));
    }

    [Fact]
    public void Bound_one_does_not_consume_a_random_word()
    {
        var random = new AnimationVariantSelector.NativeRandom(4097);
        Assert.Equal(0u, random.Uniform(1));
        Assert.Equal(3101734177u, random.NextUInt());
    }

    // Keyed initialization (0x1413cbe50): the published TinyMT32 init_by_array with this client's constants, replayed
    // independently in Python (standard initializer, then the native right-shift transition). The decompile's
    // constant-folded first step for a two-word key (state 0x40336050, 0xcfa3723c, 0x3cac5f6f, tmat) agrees with it.
    [Theory]
    [InlineData(0u, 0u, 2216047026u, 2008475589u, 1861335174u)]
    [InlineData(1u, 0u, 1146099097u, 2120761714u, 376439422u)]
    [InlineData(0x2000u, 0xdeadbeefu, 1211711957u, 837265017u, 2112675210u)]
    [InlineData(0x1234u, 0x9f1a5e3cu, 3790756449u, 1809634775u, 2720885823u)]
    public void Keyed_native_random_matches_init_by_array_vectors(uint objectId, uint typeWord, uint first, uint second, uint third)
    {
        var random = new AnimationVariantSelector.NativeRandom([objectId, typeWord]);
        Assert.Equal(first, random.NextUInt());
        Assert.Equal(second, random.NextUInt());
        Assert.Equal(third, random.NextUInt());
    }

    [Fact]
    public void Range_draw_is_the_bounded_draw_plus_the_minimum()
    {
        // 0x1404d03d0: Uniform(max − min + 1) + min; a one-value range consumes nothing.
        var ranged = new AnimationVariantSelector.NativeRandom([0x1234u, 0x9f1a5e3cu]);
        var bounded = new AnimationVariantSelector.NativeRandom([0x1234u, 0x9f1a5e3cu]);
        Assert.Equal(2, ranged.Uniform(-5, 5));
        Assert.Equal(2, (int)bounded.Uniform(11) - 5);
        var degenerate = new AnimationVariantSelector.NativeRandom([0x1234u, 0x9f1a5e3cu]);
        Assert.Equal(7, degenerate.Uniform(7, 7));
        Assert.Equal(3790756449u, degenerate.NextUInt());
    }

    [Fact]
    public void Empty_zero_weight_and_overflowing_pools_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => AnimationVariantSelector.Select(1, []));
        Assert.Throws<ArgumentException>(() => AnimationVariantSelector.Select(1, [0, 0]));
        Assert.Throws<OverflowException>(() => AnimationVariantSelector.Select(1, [uint.MaxValue, 1]));
        var random = new AnimationVariantSelector.NativeRandom(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => random.Uniform(0));
        Assert.Equal(1, AnimationVariantSelector.Select(1, [0, 7, 0]));
    }
}
