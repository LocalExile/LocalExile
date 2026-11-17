namespace GameServer.Gateway.Tests;

/// <summary>The charge ticker gives the charges the character's expiry rate (a full stat build) only when
/// <see cref="ChargeSession.TimesCharges"/> asks, instead of at every step. Whatever way a charge is gained after the
/// rate changed with nothing held (an event the ticker rolls, a projectile hit's own step, an accepted hit's
/// admission, a periodic stat), its deadline is the one the every-step ticker gave.</summary>
public class ChargeTickTests
{
    // buff_time_passed_+% over the run: it changes while charges are held and while none are.
    static int Passed(double t) => t < 1 ? 0 : t < 3 ? -40 : t < 12 ? 50 : t < 24 ? 20 : t < 41.7 ? -50 : t < 43 ? 100 : -20;

    sealed class Run(bool everyStep)
    {
        public readonly ChargeSession Session = new();
        public readonly List<string> Log = [];
        public int Skipped;
        readonly Random random = new(7);
        int periodic;

        Func<string, int> Stats(double now) => name =>
            name == "buff_time_passed_+%" ? Passed(now) : name == "gain_X_power_charges_per_second" ? periodic : 0;

        /// <summary>The character's stats were worked out (an item, an aura): the session sees them, as in WithCharges.</summary>
        public void Observe(double now, int powerPerSecond = 0)
        {
            periodic = powerPerSecond;
            Session.Observe(Stats(now));
        }

        // Game.RunCharges' step: the events' stats only while events wait, then the rate, then the step.
        public void Step(double now)
        {
            var stats = Session.Pending ? Stats(now) : null;
            if (everyStep || Session.TimesCharges) Session.State.SetExpiryRate(now, RecipientEffectTime.Rate(3, Stats(now)));
            else Skipped++;
            Log.AddRange(Session.Step(now, stats, () => default, alive: true, moving: false, inTown: false, random).Log);
        }

        // A projectile hit steps the session itself with the stats in hand, before the ticker sees its events
        // (Game.CommitProjectileHit).
        public void ProjectileHit(double now, ChargeEvent gain)
        {
            Session.Enqueue(gain);
            Log.AddRange(Session.Step(now, Stats(now), () => default, alive: true, moving: false, inTown: false, random).Log);
        }

        // An accepted hit's charges, with that hit's stats (Game.CommitAcceptedChargeBatchLocked).
        public void Admit(double now, ChargeEvent gain) =>
            Log.AddRange(Session.Admit(now, [gain], Stats(now), () => default, alive: true, random).Log);
    }

    [Fact]
    public void Skipping_the_expiry_rate_with_nothing_to_time_leaves_every_deadline_as_it_was()
    {
        var old = new Run(everyStep: true);
        var now = new Run(everyStep: false);
        foreach (var run in new[] { old, now }) run.Observe(0);
        double projectile = double.NaN, admitted = double.NaN;
        for (int i = 1; i <= 47 * 20; i++)
        {
            double t = i / 20.0;
            foreach (var run in new[] { old, now })
            {
                // 2 s: an event waits for the ticker; the rate changed at 1 s with nothing held.
                if (i == 40) run.Session.Enqueue(new ChargeEvent.Grant(ChargeType.Frenzy, 3, "event"));
                // 43.5 s: a periodic stat appears; nothing is held since 42.5 s and the rate changed at 43 s.
                if (i == 870) run.Observe(t, powerPerSecond: 1);
                run.Step(t);
                // Between two steps, after the rate changed with nothing held (at 12 s, at 24 s):
                if (i == 300) run.ProjectileHit(t + .02, new ChargeEvent.Grant(ChargeType.Power, 1, "projectile"));
                if (i == 500) run.Admit(t + .02, new ChargeEvent.Grant(ChargeType.Endurance, 2, "hit"));
            }
            if (i == 300) projectile = now.Session.State.ExpiresAt(ChargeType.Power);
            if (i == 500) admitted = now.Session.State.ExpiresAt(ChargeType.Endurance);
            foreach (var type in new[] { ChargeType.Endurance, ChargeType.Frenzy, ChargeType.Power })
            {
                Assert.Equal(old.Session.State.Held(type), now.Session.State.Held(type));
                Assert.Equal(old.Session.State.ExpiresAt(type), now.Session.State.ExpiresAt(type));
            }
        }
        Assert.Equal(old.Log, now.Log);
        // Every way gained, and the gains between steps took the rate of their own stats (1.2 and 0.5 times).
        Assert.Contains(now.Log, line => line.StartsWith("event: frenzy charges 0 -> 3"));
        Assert.Contains(now.Log, line => line.StartsWith("projectile: power charges 0 -> 1"));
        Assert.Contains(now.Log, line => line.StartsWith("hit: endurance charges 0 -> 2"));
        Assert.Contains(now.Log, line => line.StartsWith("gain_X_power_charges_per_second: power charges 0 -> 1"));
        double seconds = now.Session.Limits(ChargeType.Endurance).DurationMs / 1000.0;
        Assert.Equal(15.02 + seconds / 1.2, projectile, 6);
        Assert.Equal(25.02 + seconds / .5, admitted, 6);
        Assert.Equal((0, 0), (now.Session.State.Held(ChargeType.Frenzy), now.Session.State.Held(ChargeType.Endurance)));
        Assert.True(now.Session.State.Held(ChargeType.Power) > 0);
        // And the rate was left alone at every step with nothing held, waiting or periodic: a good part of the run.
        Assert.Equal(0, old.Skipped);
        Assert.InRange(now.Skipped, 200, 47 * 20);
    }
}
