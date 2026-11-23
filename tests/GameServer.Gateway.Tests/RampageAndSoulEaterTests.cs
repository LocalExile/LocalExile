using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// The two kill-driven effects of the belt: Rampage's rarity per streak and Soul Eater's eaten souls.
public sealed class RampageAndSoulEaterTests
{
    [Fact]
    public void Rampage_rarity_counts_whole_groups_of_kills()
    {
        Assert.Equal(0, Game.RampageRarity(0, 15));
        Assert.Equal(0, Game.RampageRarity(14, 15));
        Assert.Equal(1, Game.RampageRarity(15, 15));
        Assert.Equal(6, Game.RampageRarity(100, 15));
        Assert.Equal(66, Game.RampageRarity(1000, 15));
        Assert.Equal(0, Game.RampageRarity(100, 0));

        // A tier crossing feeds both the tier executor and the "on Rampage threshold" item mods once.
        var session = new RampageSession();
        for (int i = 0; i < 15; i++)
            session.Kill(i * 0.1, new object(), enabled: true, alive: true, active: true, hostile: true);
        Assert.Equal(new[] { 15 }, session.TakeThresholds());
        Assert.Empty(session.TakeThresholds());
        Assert.Equal(new[] { 15 }, session.TakeTiers());
    }

    // The full stat and skill snapshots follow a streak only when something they carry changes (Game.RampagePresentation).
    [Fact]
    public void Rampage_snapshots_follow_every_stack_count_a_stat_or_skill_reads()
    {
        static object Signature(int stacks, int rarityPer = 0, int areaPer25 = 0, int attackSpeedPer10 = 0) =>
            Game.RampageStatSignature(new(stacks, ExpiresAt: 5), rarityPer, areaPer25, attackSpeedPer10);
        // Damage and movement: every 20 stacks, for everyone.
        Assert.Equal(Signature(1), Signature(19));
        Assert.NotEqual(Signature(19), Signature(20));
        Assert.NotEqual(Signature(0), Signature(1));
        // Chain Hook's and Shield Charge's attack speed: every 10 stacks, only with the stat.
        Assert.Equal(Signature(9), Signature(10));
        Assert.NotEqual(Signature(9, attackSpeedPer10: 2), Signature(10, attackSpeedPer10: 2));
        Assert.NotEqual(Signature(29, attackSpeedPer10: 2), Signature(30, attackSpeedPer10: 2));
        Assert.Equal(Signature(10, attackSpeedPer10: 2), Signature(19, attackSpeedPer10: 2));
        // Rarity per X kills and area per 25: at their own group sizes.
        Assert.NotEqual(Signature(14, rarityPer: 15), Signature(15, rarityPer: 15));
        Assert.NotEqual(Signature(24, areaPer25: 5), Signature(25, areaPer25: 5));
        Assert.Equal(Signature(24), Signature(25));
    }

    [Fact]
    public void Soul_eater_gains_caps_and_decays_after_four_seconds()
    {
        var souls = new SoulEaterState();
        Assert.Equal(0, souls.Stacks(0));
        Assert.Equal(1, souls.Soul(0));
        Assert.Equal(2, souls.Soul(0.1));
        Assert.Equal(2, souls.Stacks(4.0));   // still Recently
        Assert.Equal(1, souls.Stacks(4.6));   // one lost after 4.1 s idle
        Assert.Equal(1, souls.Stacks(5.0));

        var full = new SoulEaterState();
        for (int i = 0; i < 60; i++) full.Soul(10);
        Assert.Equal(SoulEaterState.MaximumStacks, full.Stacks(10));
        Assert.Equal(SoulEaterState.MaximumStacks - 1, full.Stacks(14.5));

        souls.Clear();
        Assert.Equal(0, souls.Stacks(20));
        Assert.False(souls.Enabled);
    }

    // Soul Ascension: a hit on a rare or unique enemy eats one soul, at most once per the item's 500 ms cooldown,
    // without the flask source's kill radius; the shared pool still caps and decays.
    [Fact]
    public void Soul_ascension_hit_souls_respect_the_cooldown_and_the_cap()
    {
        var souls = new SoulEaterState { HitFed = true, HitCooldownSeconds = 0.5 };
        Assert.Equal(0, souls.Stacks(0));
        Assert.True(souls.TryHitSoul(10));
        Assert.False(souls.TryHitSoul(10.4));
        Assert.True(souls.TryHitSoul(10.5));
        Assert.Equal(2, souls.Stacks(10.5));

        // At the cap the cooldown still passes (the item's "no more than once"), but no soul is added.
        var capped = new SoulEaterState { HitFed = true, HitCooldownSeconds = 0.5 };
        capped.SetMaximum(SoulEaterState.MaximumStacks - 10);
        for (int i = 0; i < 60; i++) capped.TryHitSoul(i * 0.5);
        Assert.Equal(SoulEaterState.MaximumStacks - 10, capped.Stacks(30));

        // Without the hit source a hit eats nothing (the flask source feeds on kills instead).
        Assert.False(new SoulEaterState { HitCooldownSeconds = 0.5 }.TryHitSoul(10));

        souls.Clear();
        Assert.False(souls.TryHitSoul(20));
    }

    // The unique's own stat enables the source: equipping the gloves turns on the hit-fed soul pool, 500 ms apart,
    // and its -10..10 roll moves the 45-soul base maximum.
}
