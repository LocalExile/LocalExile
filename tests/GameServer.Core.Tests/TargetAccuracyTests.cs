using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class TargetAccuracyTests
{
    [Fact]
    public void Close_range_is_the_20_unit_radius_and_is_inclusive()
    {
        // ReminderText ReminderTextCloseRange "up to 2 metres", 10 units per metre: exactly 2.0 m is still close.
        Assert.Equal(20.0, TargetAccuracy.CloseRangeCells);
        Assert.True(TargetAccuracy.InCloseRange(0));
        Assert.True(TargetAccuracy.InCloseRange(19.9));
        Assert.True(TargetAccuracy.InCloseRange(20.0));
        Assert.False(TargetAccuracy.InCloseRange(20.0001));
        Assert.False(TargetAccuracy.InCloseRange(21));
    }

    [Fact]
    public void Unique_targets_take_the_40_percent_more()
    {
        Assert.Equal(140, TargetAccuracy.AgainstTarget(100, 40, 0, targetIsUnique: true, targetInCloseRange: false));
        // 64% more accuracy on the mastery's own row pair (60 base * 1.4 = 84).
        Assert.Equal(84, TargetAccuracy.AgainstTarget(60, 40, 50, targetIsUnique: true, targetInCloseRange: false));
    }

    [Fact]
    public void Close_targets_take_the_50_percent_more()
    {
        Assert.Equal(150, TargetAccuracy.AgainstTarget(100, 40, 50, targetIsUnique: false, targetInCloseRange: true));
        Assert.Equal(75, TargetAccuracy.AgainstTarget(50, 40, 50, targetIsUnique: false, targetInCloseRange: true));
    }

    [Fact]
    public void Unique_and_close_range_multiply()
    {
        Assert.Equal(210, TargetAccuracy.AgainstTarget(100, 40, 50, targetIsUnique: true, targetInCloseRange: true));
        Assert.Equal(21, TargetAccuracy.AgainstTarget(10, 40, 50, targetIsUnique: true, targetInCloseRange: true));
    }

    [Fact]
    public void Other_targets_keep_the_pools_accuracy_exactly()
    {
        Assert.Equal(331, TargetAccuracy.AgainstTarget(331, 40, 50, targetIsUnique: false, targetInCloseRange: false));
        // A magic far-away monster: no mastery multiplier.
        Assert.Equal(48, TargetAccuracy.AgainstTarget(48, 40, 50, targetIsUnique: false, targetInCloseRange: false));
    }

    [Fact]
    public void The_result_is_floored_like_the_increased_pools()
    {
        // 101 x 1.4 = 141.4 -> 141, and 101 x 2.1 = 212.1 -> 212.
        Assert.Equal(141, TargetAccuracy.AgainstTarget(101, 40, 50, targetIsUnique: true, targetInCloseRange: false));
        Assert.Equal(212, TargetAccuracy.AgainstTarget(101, 40, 50, targetIsUnique: true, targetInCloseRange: true));
    }

    [Fact]
    public void Accuracy_stays_whole_and_negative_values_clamp_to_zero()
    {
        Assert.Equal(0, TargetAccuracy.AgainstTarget(0, 40, 50, targetIsUnique: true, targetInCloseRange: true));
        Assert.Equal(0, TargetAccuracy.AgainstTarget(-5, 40, 50, targetIsUnique: true, targetInCloseRange: true));
    }

    [Fact]
    public void Without_the_mastery_effects_nothing_changes()
    {
        Assert.Equal(222, TargetAccuracy.AgainstTarget(222, 0, 0, targetIsUnique: true, targetInCloseRange: true));
    }
}
