using GameServer.Core.Projectiles;

/// <summary>
/// Spectral Throw's outbound flight as the client draws its own weapon (Frida 2026-11-02, trace-projectile-path.js): it
/// leaves at the row's speed, slows evenly to a stop 42 cells out over 0.85 s whatever the aim distance, and rests
/// there 0.26 s before it returns. The rest is a slow leg that ends on the profile's leash (<see cref="Leash"/>), so
/// the runtime's Return takes over from it at <see cref="ReturnSpeed"/>.
/// shortcut: the client's return speeds up from rest and reaches the thrower 0.84 s after the turn; this one flies
/// back at that average speed, so a return hit near the far end lands up to ~0.15 s early and near the thrower late.
/// shortcut: distance scales with projectile speed at the same 0.85 s; that is assumed, not traced.
/// shortcut: a wall stops the flight without a return (the runtime returns only before the flight's life ends).
/// </summary>
public sealed class SpectralThrowLegs(double speed) : IProjectileLegs
{
    public const double BaseSpeed = 92, BaseDistance = 42, OutboundSeconds = .85, RestSeconds = .26;
    const int Segments = 10;
    const double RestSpeed = 1;
    public const double ReturnSpeed = 50;

    readonly double distance = BaseDistance * speed / BaseSpeed;
    ProjectileVector start, direction;
    int segment;

    /// <summary>The rest leg ends here; the runtime then returns the projectile.</summary>
    public static double Leash(double speed) => BaseDistance * speed / BaseSpeed + RestSpeed * RestSeconds;

    /// <summary>The rest leg's speed times this is the return's speed.</summary>
    public const double ReturnSpeedMultiplier = ReturnSpeed / RestSpeed;

    public double Lifetime => OutboundSeconds + RestSeconds + 2;

    public ProjectileLeg First(ProjectileVector from, ProjectileVector aim)
    {
        start = from;
        direction = (aim - from).Length > 0 ? (aim - from).Unit : new(1, 0);
        return Next(from, blocked: false);
    }

    public ProjectileLeg Next(ProjectileVector from, bool blocked)
    {
        if (blocked || segment >= Segments)
        {
            segment = Segments;
            return new(start + direction * (distance + RestSpeed * RestSeconds * 2), RestSpeed);
        }
        // Even deceleration: covered(t) = distance × (1 − (1 − t / T)²); each segment flies its share at its average speed.
        static double Covered(double f) => 1 - (1 - f) * (1 - f);
        double share = distance * (Covered((segment + 1d) / Segments) - Covered((double)segment / Segments));
        segment++;
        return new(start + direction * (distance * Covered((double)segment / Segments)), share / (OutboundSeconds / Segments));
    }
}

/// <summary>
/// A monster's thrown weapon as the client flies it (ThrownWeaponProjectile, per-frame 0x141ab59c0 on 0.066 s ticks):
/// out from the release, slowing evenly by <c>a</c> (100 cells/s², or its deceleration stats) from its row's speed until
/// it stops, or turns at 130 cells travelled; then back from rest, speeding up by <c>a</c> × the return factor toward
/// the thrower's live cell, where it ends. A wall on the way out turns it at once; one on the way back ends it. Each leg
/// flies one tick of that curve at the tick's average speed.
/// shortcut: the client keeps separate outbound and return hit lists; this one flight forgets its hits on one window.
/// </summary>
public sealed class ThrownWeaponLegs(double speed, double deceleration, double returnFactor, Func<ProjectileVector> thrower) : IProjectileLegs
{
    public const double Tick = .066, TurnCells = 130;

    ProjectileVector start, direction;
    int tick, back;
    bool returning, home;

    double Covered(double t) => speed * t - deceleration * t * t / 2;
    // When the way out ends: where it stops, or where it has flown 130 cells.
    double OutboundSeconds => Covered(speed / deceleration) <= TurnCells ? speed / deceleration
        : (speed - Math.Sqrt(Math.Max(0, speed * speed - 2 * deceleration * TurnCells))) / deceleration;

    public bool EndsAfterLegs => true;

    public double Lifetime => OutboundSeconds
        + Math.Sqrt(2 * (Covered(OutboundSeconds) + 30) / Math.Max(.001, deceleration * returnFactor)) + 1;

    public ProjectileLeg First(ProjectileVector from, ProjectileVector aim)
    {
        start = from;
        direction = (aim - from).Length > 0 ? (aim - from).Unit : new(1, 0);
        return Next(from, blocked: false);
    }

    public ProjectileLegTurn? NextTurn(ProjectileVector from, bool blocked)
    {
        if (home || blocked && returning) return null;
        if (blocked) returning = true;
        return new ProjectileLegTurn(Next(from, blocked));
    }

    public ProjectileLeg Next(ProjectileVector from, bool blocked)
    {
        if (!returning)
        {
            double end = OutboundSeconds, t0 = Math.Min(tick * Tick, end), t1 = Math.Min(++tick * Tick, end);
            if (t1 >= end) returning = true;
            double before = Covered(t0), after = Covered(t1);
            return new(start + direction * after, Math.Max(.001, (after - before) / Math.Max(1e-6, t1 - t0)));
        }
        double legSpeed = Math.Max(.001, deceleration * returnFactor * (back++ + .5) * Tick);
        var gap = thrower() - from;
        if (gap.Length <= legSpeed * Tick)
        {
            home = true;
            return new(from + gap, legSpeed);
        }
        return new(from + gap.Unit * legSpeed * Tick, legSpeed);
    }
}

/// <summary>
/// Vaal Thrown Weapon's weapon as the client flies it (VaalThrownWeapon, steps 0x141a86380 / 0x141a867f0): 216 steps
/// out from its spawn cell along a spiral of four turns whose radius grows to 48 cells, each step 3/v seconds, the point
/// at (sin θ, cos θ) × r in this class's convention. A wall ends it.
/// </summary>
public sealed class SpiralThrowLegs(double speed) : IProjectileLegs
{
    const int Steps = 216;
    const double Radius = 48, Turns = 4;

    ProjectileVector centre;
    double heading;
    int step;

    public bool EndsAfterLegs => true;

    public double Lifetime => Steps * 3 / Math.Max(.001, speed) + .5;

    public ProjectileLeg First(ProjectileVector from, ProjectileVector aim)
    {
        centre = from;
        var d = aim - from;
        // shortcut: the base angle's arguments (0x140e2b840) are not traced; the throw's own direction, a quarter
        // turn on, as the class adds 1.5707964.
        heading = (d.Length > 0 ? Math.Atan2(d.X, d.Y) : 0) + Math.PI / 2;
        return Next(from, blocked: false);
    }

    public ProjectileLegTurn? NextTurn(ProjectileVector from, bool blocked) =>
        blocked || step >= Steps ? null : new ProjectileLegTurn(Next(from, blocked));

    public ProjectileLeg Next(ProjectileVector from, bool blocked)
    {
        step++;
        double r = Radius * step / Steps, theta = heading + step / (double)Steps * Turns * 2 * Math.PI;
        var point = centre + new ProjectileVector((int)(Math.Sin(theta) * r), (int)(Math.Cos(theta) * r));
        return new(point, Math.Max(.01, (point - from).Length) * speed / 3);
    }
}

/// <summary>
/// When the client lets Spectral Throw's weapon go. Its action plays the stance's Melee (the class ACT: SpectralThrow =
/// "@Melee"); with a one-handed sword, mace or axe and a shield that alternates two views of doublestrike_sword_shield
/// (the class's NoWind .ao), "attack_sword_shield_01a:attack_time=…" and then "01b". A view plays at its length over its
/// attack_time outside the slow_begin..slow_end span, which stretches to fill the rest of the attack (11-skills.md §4),
/// so the release is the attack time less the view's tail after its contact.
/// Frida 2026-11-02: the Scion's views played at 0.886 and 1.686 (length / attack_time) and alternated on all seven
/// throws, the slow span at 0.528 in a 0.574 s attack; the Witch's 01a released 0.543 s into a 0.66 s attack (0.548 here).
/// shortcut: the Witch's 01b played at 1.45, not at its length over attack_time=0.25 (2.8); the measured speed is kept.
/// shortcut: when the client starts the combo over at 01a is not known; the server only alternates.
/// shortcut: other classes and stances keep the attack1 contact (AttackContactDelay); their Melee is not read.
/// </summary>
public static class SpectralThrowRelease
{
    /// <summary>A view's length and contact (seconds into it) and how long its outside of the slow span takes.</summary>
    readonly record struct Swing(double Length, double Contact, double NormalSeconds);
    static readonly Dictionary<string, Swing[]> SwordShield = new(StringComparer.Ordinal)
    {
        // doublestrike_sword_shield 1100 ms, views split at 399; contacts 300 and 766.
        ["Metadata/Characters/Int/Int.ao"] = [new(.399, .300, .45), new(.701, .367, .701 / 1.45)],
        // doublestrike_sword_shield 1033 ms, views split at 443; contacts 333 and 699; attack_time 0.5 and 0.35.
        ["Metadata/Characters/StrDexInt/StrDexInt.ao"] = [new(.443, .333, .5), new(.590, .256, .35)],
    };
    static readonly RuleTable<ClientState,
        System.Runtime.CompilerServices.StrongBox<int>> Swings = new();

    /// <summary>Seconds from the use to the release, or null where the stance's swings are not known.</summary>
    public static double? Delay(ClientState conn, string animatedObject, WeaponProfile weapon, double attackSeconds)
    {
        // A one-handed mace or axe takes the sword's stance (the ACTs have no one-handed mace or axe stance).
        if (!SwordShield.TryGetValue(animatedObject, out var swings) || !weapon.Shield || !weapon.OneHandMelee
            || !(weapon.Sword || weapon.Mace || weapon.Axe))
        {
            Console.WriteLine($"[gateway] Spectral Throw swing unknown for {animatedObject} {weapon}: attack1 contact instead");
            return null;
        }
        var count = Swings.GetOrCreateValue(conn);
        int index = count.Value++ % swings.Length;
        var swing = swings[index];
        // An attack shorter than the view's normal time plays the whole view linearly.
        double delay = attackSeconds >= swing.NormalSeconds
            ? attackSeconds - (swing.Length - swing.Contact) * swing.NormalSeconds / swing.Length
            : attackSeconds * swing.Contact / swing.Length;
        Console.WriteLine($"[gateway] Spectral Throw swing {(index == 0 ? "01a" : "01b")}: release {delay:0.000} s into a {attackSeconds:0.000} s attack");
        return delay;
    }
}
