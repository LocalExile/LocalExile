namespace GameServer.Gateway.Tests;

public sealed class GuardSkillTests
{
    [Fact]
    public async Task Casting_immortal_call_applies_its_buff_and_holds_the_shared_guard_cooldown()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Immortal Call", 1));
        var gem = Game.GemSkillEffects(f.Conn).Single(g => Game.SkillsFor(f.Conn).EffectId(g.Effect) == "ImmortalCall");
        var charges = Game.ChargesOf(f.Conn);
        var limits = charges.Limits(ChargeType.Endurance);
        charges.State.Set(ChargeType.Endurance, limits.Minimum + 3, 0, limits);
        double now = f.Conn.Clock.Elapsed.TotalSeconds;
        uint mana = f.Conn.PlayerMana;
        await Game.UseInstantSkill("test", f.Stream, f.Ciphers, f.Conn, gem.Id, 0, f.Conn.Walk.Position(now));
        double castAt = f.Conn.Clock.Elapsed.TotalSeconds;
        Assert.Equal(limits.Minimum, charges.Effective(ChargeType.Endurance));
        Assert.True(f.Conn.PlayerMana < mana);
        Assert.NotEmpty(Game.TriggeredGuardPresentation(f.Conn, castAt));
        // Level 1 with three charges: 25% less Physical and Elemental Damage, then 15% less Physical per charge.
        Assert.Equal((412, 750, 1000), Game.TriggeredGuardHit(f.Conn, 1000, 1000, 1000, castAt));
        // The buff is 1 s + 20% per charge, and the 3 s cooldown does not recover during it.
        double ended = double.NaN, ready = double.NaN;
        for (double t = castAt; t < castAt + 8; t += 0.01)
        {
            if (double.IsNaN(ended) && Game.TriggeredGuardHit(f.Conn, 1000, 0, 0, t).Physical >= 1000) ended = t;
            if (double.IsNaN(ready) && Game.TriggerGuardReady(f.Conn, t)) { ready = t; break; }
        }
        Assert.InRange(ended - castAt, 1.2, 1.8);
        Assert.InRange(ready - ended, 2.9, 3.1);
        // A second cast is refused by the shared guard cooldown before any cost, so nothing is spent.
        mana = f.Conn.PlayerMana;
        await Game.UseInstantSkill("test", f.Stream, f.Ciphers, f.Conn, gem.Id, 0, f.Conn.Walk.Position(now));
        Assert.Equal(mana, f.Conn.PlayerMana);
        Assert.Equal(limits.Minimum, charges.Effective(ChargeType.Endurance));
    }
}
