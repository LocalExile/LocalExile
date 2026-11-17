namespace GameServer.Gateway.Tests;

/// <summary>The charge ticker gives the charges the character's expiry rate (a full stat build) only when
/// <see cref="ChargeSession.TimesCharges"/> asks, instead of at every step. Whatever way a charge is gained after the
/// rate changed with nothing held (a waiting event, a hit's own admission, a periodic stat), its deadline is the one
/// the every-step ticker gave.</summary>
public class ChargeTickTests
{
    // buff_time_passed_+% over the run: it changes while charges are held and while none are.
    static int Passed(double t) => t < 1 ? 0 : t < 3 ? -40 : t < 24 ? 50 : t < 41.7 ? -50 : t < 43 ? 100 : -20;

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

        // An accepted hit's charges, between two steps, with that hit's stats (Game.CommitAcceptedChargeBatchLocked).
        public void Admit(double now, ChargeEvent gain) =>
            Log.AddRange(Session.Admit(now, [gain], Stats(now), () => default, alive: true, random).Log);
    }

    [Fact]
    public void Skipping_the_expiry_rate_with_nothing_to_time_leaves_every_deadline_as_it_was()
    {
        var old = new Run(everyStep: true);
        var now = new Run(everyStep: false);
        foreach (var run in new[] { old, now }) run.Observe(0);
        double admitted = double.NaN;
        for (int i = 1; i <= 47 * 20; i++)
        {
            double t = i / 20.0;
            foreach (var run in new[] { old, now })
            {
                if (i == 40) run.Session.Enqueue(new ChargeEvent.Grant(ChargeType.Frenzy, 3, "event"));
                if (i == 870) run.Observe(t, powerPerSecond: 1);   // 43.5 s: nothing is held since 42.5 s
                run.Step(t);
                // A hit lands between this step and the next, long after the rate last changed with nothing held.
                if (i == 500) run.Admit(t + .02, new ChargeEvent.Grant(ChargeType.Endurance, 2, "hit"));
            }
            if (i == 500) admitted = now.Session.State.ExpiresAt(ChargeType.Endurance);
            foreach (var type in new[] { ChargeType.Endurance, ChargeType.Frenzy, ChargeType.Power })
            {
                Assert.Equal(old.Session.State.Held(type), now.Session.State.Held(type));
                Assert.Equal(old.Session.State.ExpiresAt(type), now.Session.State.ExpiresAt(type));
            }
        }
        Assert.Equal(old.Log, now.Log);
        // All three ways gained and the first two expired; the hit's charges took the rate of its own stats (half speed).
        Assert.Contains(now.Log, line => line.StartsWith("event: frenzy charges 0 -> 3"));
        Assert.Contains(now.Log, line => line.StartsWith("hit: endurance charges 0 -> 2"));
        Assert.Contains(now.Log, line => line.Contains("power charges 0 -> 1"));
        Assert.Equal((0, 0), (now.Session.State.Held(ChargeType.Frenzy), now.Session.State.Held(ChargeType.Endurance)));
        Assert.True(now.Session.State.Held(ChargeType.Power) > 0);
        Assert.Equal(25.02 + now.Session.Limits(ChargeType.Endurance).DurationMs / 1000.0 / .5, admitted, 6);
        // And the rate was left alone at every step with nothing held, waiting or periodic: over a third of the run.
        Assert.Equal(0, old.Skipped);
        Assert.InRange(now.Skipped, 300, 47 * 20);
    }
}
