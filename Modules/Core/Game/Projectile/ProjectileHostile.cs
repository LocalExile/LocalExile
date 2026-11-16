using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Development fallback for lobbed skills without a radius stat, in cells. The Sand Spitter's native straight
    // projectile uses direct contact and never this fallback.
    public const double MonsterMortarRadiusCells = 10;

    public static double MonsterMortarRadius() =>
        double.TryParse(Environment.GetEnvironmentVariable("Game_MONSTER_MORTAR_RADIUS"), out double radius) && radius > 0
            ? radius : MonsterMortarRadiusCells;

    /// <summary>The use for a monster's ranged attack released now. A shot the client draws (its record carries the
    /// count, MonsterShots.CountStats; the release 0x141ba7c30 fires nothing without one) keeps the order's aim and flies
    /// hidden: a mortar to its scattered landing cells, any other straight shot by the client's own range, contact and
    /// volley (MonsterShots.ClientDrawnProfile). Any other shot carries no count, targets the player's cell at release,
    /// and the server's object is the projectile the player sees. A lobbed shot
    /// lands and damages the area there; a straight shot flies on past its aim. Team 2, so only the player (and friendly
    /// actors) are in its way, never the other monsters.</summary>
    public static ProjectileUse MonsterProjectileUse(ProjectileSession session, uint id, SpawnedMonster m, MonsterAttack a,
        SkillCatalog.MonsterProjectileInfo projectile, int action, uint? aimedActor = null,
        (int X, int Y)? orderedAim = null, ushort sequence = MonsterActionSequence)
    {
        var conn = session.Conn;
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        var from = ProjectileSession.Vector(m.Walk.Position(now));
        var aim = ProjectileSession.Vector(orderedAim ?? (aimedActor is uint targetId && OwnedFighter(conn, targetId) is { } golem
            ? golem.Monster.Walk.Position(now) : conn.Walk.Position(now)));
        // The row's speed with the shot's speed stats (the skill's base_projectile_speed_+%: the Root Spider's spit flies
        // twice as fast; the monster's projectile_speed_+%), as the client's speed getter reads them (0x141bb6360).
        int SkillStat(string name) => Stats.Key(name) is uint key && projectile.SkillStats is { } own ? own.GetValueOrDefault(key) : 0;
        double speed = ProjectileSpeed.CellsPerSecond(projectile.Speed, SkillStat, SkillStat,
            Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
        double distance = Math.Max(1, (aim - from).Length);
        // A mortar's, knife's or spray's range in cells (the range getter, 0x141ba8d60): projectile_maximum_range_override, else 150.
        double MortarRange() => SkillStat("projectile_maximum_range_override") is > 0 and var most ? most : 150;
        var toward = (aim - from).Unit;
        // The release point: the clip's contact keyframe along the line to the aim (the Spitter's lies 4.2 cells behind a
        // body that faces away from its target, so toward the player, 9 steps up), else 1.5 cells ahead at the default
        // height; never past half way to the aim, so a close target is not behind the shot. The shot still flies from the
        // monster toward the aim (maintain_projectile_direction_when_using_contact_position). Native straight mortar
        // attacks also rotate the lateral contact offset below.
        var release = a.Release;
        // The seed byte of projectile i of this release (the emitter's counter, which runs on across the action's releases,
        // 0x141423e30): 2 + its place among every projectile the action has fired, past any earlier draws.
        uint ordinal = (uint)(release?.Ordinal ?? 0);
        uint Derived(int i, uint earlier = 0) => (uint)(2 + (earlier + ordinal * projectile.Count + i) % 254);
        double ahead = release?.Point is { } point ? point.ForwardCells * (release.FacesAway ? -1 : 1) : OrdinaryRangedAttack.ReleaseAheadCells;
        ahead = Math.Min(ahead, distance / 2);
        var origin = from + toward * ahead;
        // projectile_uses_contact_direction: the shot flies from its launch along the contact's direction, as far as the
        // aim was (at least 60 units).
        var heading = toward;
        void TurnAim()
        {
            if (release?.Turn is { } turn && MonsterShots.ContactTurn(turn, toward, release.FacesAway) is { } h)
            {
                heading = h;
                aim = origin + h * Math.Max(distance, 60 / WorldUnits.PerCell);
            }
        }
        // The release (0x141ba7c30) holds the aim at least 60 units (5.52 cells) from the caster's cell and, on a
        // UseTargetLocation row with a range of its own, no farther than that range.
        ProjectileVector Held(ProjectileVector point)
        {
            var reach = point - from;
            double held = reach.Length < 60 / WorldUnits.PerCell ? 60 / WorldUnits.PerCell
                : projectile.EndsAtTarget && MortarRange() < 150 && reach.Length > MortarRange() ? MortarRange()
                : reach.Length;
            if (held == reach.Length) return point;
            var moved = from + (reach.Length > 0 ? reach.Unit : new ProjectileVector(1, 0)) * held;
            return new(Math.Floor(moved.X + .5), Math.Floor(moved.Y + .5));
        }
        ProjectileVector[]? nativeTargets = null;
        double[]? nativeSpeeds = null;
        string contactRow = projectile.Metadata;
        bool mortar = projectile.Target == SkillCatalog.MonsterShotTarget.MortarScatter;
        // Weapon jitter retains its seeded targets while the server draws the authoritative flight; its native
        // endpoint agreement is incomplete. Do not let the presentation fallback discard spread or piercing.
        bool seededWeapon = projectile.Recipe == "WeaponJitter";
        bool seededFlight = (projectile.ClientDraws || seededWeapon) && (!projectile.Lobbed || mortar);
        if (seededFlight)
        {
            // The contact offset is rotated with the facing model, then quantized to Positioned cells.
            // maintain_projectile_direction_when_using_contact_position translates the aim by the same offset.
            double lateral = release?.Point is { } contact ? contact.X / WorldUnits.PerCell * (release.FacesAway ? 1 : -1) : 0;
            origin += new ProjectileVector(toward.Y, -toward.X) * lateral;
            origin = new(Math.Floor(origin.X + .5), Math.Floor(origin.Y + .5));
            TurnAim();
            // A table column moves the aim on along the shot (aim + N × unit(aim − release), rounded half up).
            if (!mortar && projectile.AimOffset != 0 && (aim - origin).Length > 0)
            {
                var moved = aim + (aim - origin).Unit * projectile.AimOffset;
                aim = new(Math.Floor(moved.X + .5), Math.Floor(moved.Y + .5));
            }
            var translated = aim + origin - from;
            if (mortar)
            {
                // A spell mortar's aim moves with the release only by the stat that says so. Its volley fans about the
                // launch as any other (the shared emitter's fan 0x141bbc1c0 turns each target), and a row without
                // UseTargetLocation puts it at the skill's range (0x141bac460); each target is rounded to its cell. Then
                // each projectile's own launch moves __col9 cells toward its own target, from which its mover measures the
                // distance override and the scatter (0x141a13760).
                ProjectileNativeVolley? fan = null;
                var start = origin;
                if (projectile.Lobbed)
                {
                    if (!projectile.AimTranslates) translated = aim;
                    translated = Held(translated);
                    if (projectile.Count > 1) fan = NativeVolley(new ProjectileProfile(projectile.Metadata), SkillStat).NativeVolley;
                }
                var arm = translated - start;
                double reach = projectile.EndsAtTarget ? arm.Length : MortarRange();
                ProjectileVector TargetOf(int i)
                {
                    if (arm.Length <= 0 || fan is null && projectile.EndsAtTarget) return translated;
                    var turned = start + arm.Unit.Rotate(fan?.Angle(i, projectile.Count, arm.Length) ?? 0) * reach;
                    return new(Math.Floor(turned.X + .5), Math.Floor(turned.Y + .5));
                }
                ProjectileVector LaunchOf(ProjectileVector target)
                {
                    if (!projectile.Lobbed || projectile.StartOffset == 0) return start;
                    // The whole column, along the actor's line to the target (+x where the two meet).
                    var line = (target - from).Length > 0 ? (target - from).Unit : new ProjectileVector(1, 0);
                    var moved = start + line * projectile.StartOffset;
                    return new(Math.Floor(moved.X + .5), Math.Floor(moved.Y + .5));
                }
                // The use flies from the unturned launch; each landing is its own projectile's.
                origin = LaunchOf(translated);
                // The minimum range grows by the start offset only where the skill has one (0x141a13760).
                int minimum = projectile.MinimumRange != 0 ? projectile.MinimumRange + projectile.StartOffset : 0;
                var draws = new float[projectile.Count];
                nativeTargets = new ProjectileVector[projectile.Count];
                for (int i = 0; i < projectile.Count; i++)
                {
                    var turned = TargetOf(i);
                    nativeTargets[i] = MonsterMortarTarget(from, LaunchOf(turned), turned, minimum, projectile.SpreadRadius,
                        sequence, id, (int)Derived(i) - 2, projectile.DistanceOverride, out draws[i]);
                }
                // A spell mortar lands a fixed time after its release, at any distance: each projectile at its own speed.
                if (projectile.FixedFlight)
                    nativeSpeeds = [.. nativeTargets.Select((t, i) => Math.Max(.001, (t - origin).Length) / MonsterShots.MortarFlightSeconds(projectile, draws[i]))];
            }
            else if (projectile.AimTranslates) aim = translated;
            if (projectile.Recipe is "WeaponJitter" or "IceElementalSpear")
            {
                // A weapon shot's or an ice spear's target is put at its range from the launch along its fanned line, on
                // its cell (0x141bac460); then its mover turns it by its seed (SeededEnd, IceElementalSpearEnd).
                // Without a contact position the shot leaves 30 units ahead of the caster's cell's centre toward the aim
                // (0x141ba7c30), on that cell: the launch its mover measures from.
                if (release?.Point is null)
                {
                    var centre = (new ProjectileVector(Math.Floor(from.X), Math.Floor(from.Y)) + new ProjectileVector(.5, .5)) * WorldUnits.PerCell;
                    var aimWorld = (aim + new ProjectileVector(.5, .5)) * WorldUnits.PerCell;
                    var spawn = centre + (aimWorld - centre).Unit * 30;
                    origin = new(Math.Floor(spawn.X * .092), Math.Floor(spawn.Y * .092));
                }
                var fan = NativeVolley(new ProjectileProfile(projectile.Metadata), SkillStat).NativeVolley;
                var arm = aim - origin;
                float turn = SkillStat("monster_projectile_variation") == 8 ? .5f : .2f;
                nativeTargets = new ProjectileVector[projectile.Count];
                for (int i = 0; i < projectile.Count; i++)
                {
                    var turned = origin + arm.Unit.Rotate(fan?.Angle(i, projectile.Count, arm.Length) ?? 0) * MortarRange();
                    var cell = new ProjectileVector(Math.Floor(turned.X + .5), Math.Floor(turned.Y + .5));
                    uint[] key = [sequence, Derived(i), 0, ordinal];
                    nativeTargets[i] = projectile.Recipe == "IceElementalSpear" ? IceElementalSpearEnd(cell, origin, key)
                        : SeededEnd(cell, origin, key, 150, turn: turn).End;
                }
            }
            // A water vortex ends on its diamond, whatever the aim's distance.
            if (projectile.Recipe == "WaterVortex")
                nativeTargets = [WaterVortexTarget(from, aim, (int)(128 * (100 + SkillStat("spell_maximum_action_distance_+%")) / 100.0))];
            if (projectile.Recipe is "Knives" or "Spray" or "Flames" or "FigureheadFlames")
            {
                // Ethereal knives (0x1419f4be0) hold the aim 60 cells from the caster's cell, a fire spray (0x141c6b980) 50
                // cells toward its target's cell at release, flames 18 (0x141b85cd0); every projectile leaves from 30 units
                // ahead of the cell's centre (0x141ba7c30), not from the clip's contact, and fans about there to the aim's
                // distance (a nova rings), a flame's target then pushed out to the range (its row has no
                // UseTargetLocation). Each one's mover then jitters and stretches its end (SeededEnd), a spray's at its own
                // speed; the counter that seeds it runs on across the action's releases.
                bool spray = projectile.Recipe == "Spray", flames = projectile.Recipe is "Flames" or "FigureheadFlames";
                var cell = new ProjectileVector(Math.Floor(from.X), Math.Floor(from.Y));
                var line = (aim - cell).Unit;
                double hold = spray ? 50 : flames ? 18 : 60;
                var held = cell + new ProjectileVector(Math.Floor(line.X * hold + .5), Math.Floor(line.Y * hold + .5));
                var centre = (cell + new ProjectileVector(.5, .5)) * WorldUnits.PerCell;
                var heldWorld = (held + new ProjectileVector(.5, .5)) * WorldUnits.PerCell;
                var spawn = centre + (heldWorld - centre).Unit * 30;
                var arm = heldWorld - spawn;
                ProjectileVector Cell(ProjectileVector world) => new(Math.Floor(world.X * .092), Math.Floor(world.Y * .092));
                origin = Cell(spawn);
                var fan = NativeVolley(new ProjectileProfile(projectile.Metadata), SkillStat).NativeVolley!;
                nativeTargets = new ProjectileVector[projectile.Count];
                var speeds = new double[projectile.Count];
                // A flame's stage (0x141b85cd0): a cast within 0.75 s of the last release adds one, up to 100; a later one
                // starts again at 1 and makes a stage tracker, which takes the action's next seed before the flames (the
                // Figurehead's tracker counts its own, 0x141b81eb0). The stage's tier picks the row by which it touches.
                int stage = 0;
                uint trackers = 0;
                float turn = 0;
                if (flames)
                {
                    var (last, until, made) = m.FlameStages.GetValueOrDefault(a.SkillId);
                    if (ordinal == 0) made = 0;
                    if (now >= until) (last, made) = (0, made + 1);
                    stage = Math.Min(100, last + 1);
                    m.FlameStages[a.SkillId] = (stage, now + .75, made);
                    trackers = projectile.Recipe == "FigureheadFlames" ? 0u : (uint)made;
                    int tier = stage < 4 ? 1 : stage < 8 ? 2 : stage < 12 ? 3 : 4;
                    turn = FlameTurn(stage);
                    if (projectile.Metadata.EndsWith("Flamethrower1") || projectile.Metadata.EndsWith("FlamethrowerBlue1"))
                        contactRow = projectile.Metadata[..^1] + tier;
                }
                for (int i = 0; i < projectile.Count; i++)
                {
                    uint derived = Derived(i, trackers);
                    var turned = arm.Rotate(fan.Angle(i, projectile.Count, arm.Length / WorldUnits.PerCell));
                    if (flames) turned = turned.Unit * MortarRange() * WorldUnits.PerCell;
                    (nativeTargets[i], float factor) = SeededEnd(Cell(spawn + turned), origin,
                        spray ? [sequence, derived, 0, ordinal] : [sequence, derived, 0], flames ? 150 : (float)MortarRange(),
                        speedDraw: spray, turn: turn);
                    speeds[i] = speed * factor;
                }
                if (spray) nativeSpeeds = speeds;
            }
        }
        // The server's own object turns by its contact too.
        if (!seededFlight) TurnAim();
        if (projectile is { ClientDraws: false, Lobbed: true } && projectile.Recipe is "FireMortar" or "InLine" or "SpiritMortar" or "AroundCaster" or "GroundMortar"
            or "SummonMortar")
        {
            // The server's object lands each projectile where its class's mover would (MonsterShots' landing recipes),
            // fanned about the line to the aim as the shared emitter fans it, and flies the fixed time that mover gives it.
            var fan = projectile.Count > 1 ? NativeVolley(new ProjectileProfile(projectile.Metadata), SkillStat).NativeVolley : null;
            var line = aim - from;
            ProjectileVector Fanned(int i) => fan is null || line.Length <= 0 ? aim
                : from + line.Unit.Rotate(fan.Angle(i, projectile.Count, line.Length)) * line.Length;
            int spread = SkillStat("projectile_spread_radius") + (projectile.Count - 1) * SkillStat("projectile_spread_radius_per_additional_projectile");
            var draws = Enumerable.Repeat(1f, projectile.Count).ToArray();
            if (projectile.Recipe == "SpiritMortar") origin = SpiritMortarLaunch(origin, aim);
            nativeTargets = projectile.Recipe == "AroundCaster"
                ? AroundCasterTargets(from, projectile.Count, SkillStat("projectile_minimum_range"), spread, sequence, id, ordinal)
                : [.. Enumerable.Range(0, projectile.Count).Select(i => projectile.Recipe switch
                {
                    "InLine" => InLineMortarTarget(from, Fanned(i), SkillStat("mortar_distance_in_front_of_target"), spread * .092f,
                        sequence, id, i, out draws[i]),
                    "SpiritMortar" => SpiritMortarTarget(from, origin, Fanned(i), SkillStat("projectile_minimum_range"), spread,
                        sequence, id, i, out draws[i]),
                    "GroundMortar" => GroundMortarTarget(conn, from, origin, Fanned(i), spread, SkillStat("projectile_distance_override"),
                        sequence, id, i, out draws[i]),
                    // A summoning mortar's mover (0x141c6a340) is the mortar attack's: pushed out to the minimum range, scattered.
                    "SummonMortar" => MonsterMortarTarget(from, origin, Fanned(i), SkillStat("projectile_minimum_range"), spread,
                        sequence, id, i, 0, out draws[i]),
                    // A fire mortar lands on its held aim (0x141a10bf0 with no scatter).
                    _ => Held(Fanned(i)),
                })];
            if (projectile.FixedFlight)
                nativeSpeeds = [.. nativeTargets.Select((t, i) => Math.Max(.001, (t - origin).Length) / MonsterShots.MortarFlightSeconds(projectile, draws[i]))];
        }
        if (projectile.Recipe == "BreachBarrage")
        {
            // The server's barrage spreads as the client's: 150 cells from the caster's cell in the seeded cone about the
            // line to its target (BreachBarrageEnd), which stands in for the caster's turning facing.
            var line = aim - from;
            float facing = (float)(Math.Atan2(line.Y, line.X) + Math.PI / 2);
            var cell = new ProjectileVector(Math.Floor(from.X), Math.Floor(from.Y));
            nativeTargets = [.. Enumerable.Range(0, projectile.Count)
                .Select(i => BreachBarrageEnd(cell, facing, [sequence, Derived(i), 0, ordinal]))];
        }
        if (projectile.Recipe == "Riptide")
        {
            // A riptide's wave leaves the caster's cell for its end (RiptideEnd), whatever its clip's contact.
            origin = from;
            aim = RiptideEnd(conn, from, aim, SkillStat("skill_behaviour_variation") == 1,
                [sequence, Derived(0), id, ordinal, 0, 0, 0, 0], LivingCollision.Size(m) + 1);
        }
        long orbEvery = 0;
        if (projectile.Recipe == "StormBurst")
        {
            // A storm burst lets its first orb go at the release and one more every attack / (n + 1) (0x141c67820) while
            // the action lasts; at its end (0x141c67d80) orb k bursts 0.05 × k s later. Each orb flies its heading at a
            // constant speed, so it ends, and bursts, where that time finds it (no storm burst reaches its range by then).
            // shortcut: the attack's own time, not one a chill slowed.
            double left = a.Seconds * (1 - (release?.Fraction ?? .5)), every = a.Seconds / (projectile.Count + 1);
            int orbs = Math.Max(1, Enumerable.Range(0, projectile.Count).Count(k => k * every < left));
            var facing = aim - from;
            var headings = StormBurstHeadings(MathF.Atan2(-(float)facing.Y, (float)facing.X), projectile.Count,
                SkillStat("projectiles_nova") != 0, sequence, id);
            nativeTargets = [.. Enumerable.Range(0, orbs).Select(k => origin + new ProjectileVector(Math.Cos(headings[k]), -Math.Sin(headings[k]))
                * Math.Min(MortarRange(), speed * Math.Max(.001, left - k * every + .05 * k)))];
            orbEvery = ProjectileRuntime.Time(every);
        }
        // A server-created lobbed shot lands at its aim, no nearer the actor than its minimum range (cells). A spell
        // mortar the server draws lands where its mover would, measured along the line to the aim: the aim (capped at
        // its range) or, without UseTargetLocation, its range from the release (0x141bac460); then (0x141a13760) pushed
        // out to the minimum range plus __col9 from the actor, or its distance override from the launch __col9 cells out
        // (a Doedre nova's ring around her, not on the player). Native mortars use their scattered destinations above.
        double landing = Math.Max(0.5, Math.Max((aim - origin).Length, projectile.MinimumRange - ahead));
        if (projectile is { Lobbed: true, ClientDraws: false } && mortar)
        {
            double offset = projectile.StartOffset, most = MortarRange();
            landing = projectile.EndsAtTarget ? Math.Min((aim - origin).Length, most) : most;
            if (projectile.MinimumRange != 0) landing = Math.Max(landing, projectile.MinimumRange + offset - ahead);
            if (projectile.DistanceOverride > 0) landing = offset + projectile.DistanceOverride;
            landing = Math.Max(0.5, landing);
        }
        if (projectile.Recipe == "RollingMagma") landing = Math.Max(0.5, MonsterShots.MagmaLanding(projectile, landing + ahead) - ahead);
        var target = projectile.Lobbed ? origin + heading * landing : aim;
        // A straight shot the client draws flies by the client's own rules (MonsterShots.ClientDrawnProfile: its range,
        // contact, volley and count), hidden; a mortar keeps its scattered landings; anything else is the server's object.
        // A spark's wander starts from the monster's own cell (0x141b384a0).
        if (projectile.Recipe == "Spark") origin = from;
        // A UseTargetLocation shot ends at the aimed cell; any other straight one flies past it.
        double straightRange = projectile.EndsAtTarget ? Math.Max(.001, (aim - origin).Length) : Math.Max(distance, a.Range) * 1.5 + 5;
        var profile = (projectile.ClientDraws || seededWeapon) && !projectile.Lobbed && !mortar
            ? MonsterShots.ClientDrawnProfile(conn, projectile, attack: !a.Spell, aim - origin, sequence, id,
                thrower: () => ProjectileSession.Vector(m.Walk.Position(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond)),
                fromActor: aim - from) with
                {
                    // A lantern reaches its aim its clip's contact after the release, at any distance.
                    Speed = projectile.FixedFlight ? Math.Max(.001, (aim - origin).Length) / projectile.ContactSeconds : speed,
                    HideVisual = projectile.ClientDraws,
                }
            : projectile.Recipe is not (null or "BreachBarrage" or "FireMortar" or "InLine" or "SpiritMortar" or "AroundCaster" or "GroundMortar"
                or "SummonMortar" or "Flames" or "FigureheadFlames")
                ? MonsterShots.RecipeProfile(projectile, speed, landing, attack: !a.Spell)
            : MonsterProjectileProfile(projectile, speed, landing, straightRange);
        if (nativeTargets is not null)
            profile = profile with { ContactCells = SkillsFor(conn).ProjectileContactCells(contactRow) };
        // A summoning mortar brings its monsters where it lands (SummonLanded); whether its landing also hurts is not
        // known, so it hits nothing, as the summon did before it flew.
        if (projectile.Recipe == "SummonMortar") profile = profile with { EndArea = null, EndAreaReasons = null, DirectDamage = false };
        if (orbEvery > 0) profile = profile with { Count = nativeTargets!.Length, SequentialDelay = orbEvery };
        if (projectile is { ClientDraws: false, Recipe: "FireMortar" } && SkillStat("number_of_chains") > 0
            && nativeTargets is [var landed, ..] && (landed - origin).Length > 0)
        {
            // A fire mortar that chains hops once on along its line (0x141a10f00), in the same fixed time: 2S + 3 cells,
            // else back toward S cells to the first walkable cell, else not at all. Its hop hits for less (HostileHit).
            var (hop, least) = MonsterShots.FireMortarHop(projectile);
            var way = (landed - origin).Unit;
            bool Walkable(int x, int y) => conn.Layout.Walkable is not { } floor || floor(x, y) && !conn.Barrier.Contains((x, y));
            int reach = Enumerable.Range(least, hop - least + 1).Reverse().FirstOrDefault(k =>
                Walkable((int)Math.Floor(landed.X + way.X * k + .5), (int)Math.Floor(landed.Y + way.Y * k + .5)));
            if (reach > 0)
                profile = profile with
                {
                    GroundChains = 1, GroundBounceDistance = reach,
                    GroundBounceSeconds = projectile.FixedFlight ? MonsterShots.MortarFlightSeconds(projectile, 1) : 0,
                };
        }
        var owner = session.ActorKey(id, m);
        double flightCells = projectile.Lobbed ? landing : (aim - origin).Length;
        double flightSeconds = nativeSpeeds is [var firstSpeed, ..] && nativeTargets is [var firstTarget, ..]
            ? (firstTarget - origin).Length / firstSpeed : flightCells / speed;
        var presentation = MonsterProjectilePresentation(SkillsFor(conn).Clips(projectile.Metadata), flightSeconds);
        if (release?.Point is { } launch) presentation = (presentation ?? new ProjectilePresentation()) with { LaunchHeight = launch.HeightSteps };
        Console.WriteLine($"[gateway] {session.remote} monster 0x{id:x} ({m.Label}) releases {projectile.Metadata} from {origin}"
            + $" ({ahead:0.0} cells toward the aim, {(release?.Point is { } up ? up.HeightSteps.ToString() : "default")} steps up"
            + $"{(release is null ? "" : $", clip {release.Clip} at {release.Fraction:0.000} of the attack{(release.FacesAway ? ", facing away" : "")}")})"
            + $" aim={aim}{(nativeTargets is { } landings ? $" lands at {string.Join(" ", landings.Select(t => $"({t.X:0},{t.Y:0})"))}"
                : projectile.Lobbed ? $" lands at {target} ({landing:0.0} cells)" : "")}");
        return new ProjectileUse(profile, owner, owner, 2, origin, target)
            { ProjectileTargets = nativeTargets, ProjectileSpeeds = nativeSpeeds, ReturnAnchor = profile.Returns ? owner : null,
                Payload = new HostileProjectileOffense(id, m, a, action, $"{m.Label} {projectile.Metadata}")
                    { Presentation = presentation, Sequence = sequence } };
    }

    /// <summary>The flight the server simulates for a monster's shot. A lobbed shot's range is its landing distance; a
    /// straight one flies <paramref name="straightRange"/> cells. A mortar attack the client draws (its record carries the
    /// row and count, MonsterProjectileSkillStats) gets no object, so the player sees one mortar, as from GGG, which creates
    /// none; the flight still lands its hit.</summary>
    public static ProjectileProfile MonsterProjectileProfile(SkillCatalog.MonsterProjectileInfo projectile, double speed,
        double landing, double straightRange) => new(projectile.Metadata)
    {
        Speed = speed, Radius = projectile.CollisionRadius, Count = Math.Max(1, projectile.Count), Lobbed = projectile.Lobbed,
        SpreadRadians = projectile.SpreadRadians,
        // A ring at the client's nova angles, i × 2π / count from the aim (0x141bbbf90).
        NativeVolley = projectile.SpreadRadians >= 2 * Math.PI ? new() { Nova = true } : null,
        Range = projectile.Lobbed ? landing : straightRange,
        DirectDamage = !projectile.Lobbed,
        EndArea = projectile.Lobbed ? new ProjectileAreaRecipe("monster mortar", MonsterShots.AreaRadius(projectile, MonsterMortarRadius())) : null,
        EndAreaReasons = projectile.Lobbed
            ? new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing, ProjectileEndReason.Terrain } : null,
        HideVisual = projectile.ClientDraws,
        Pierce = projectile.PiercesAll ? -1 : 0,
    };

    /// <summary>A server-created projectile object plays nothing by itself. Start the rig's flight clip at spawn even
    /// when the .ao declares the same default animation: the explicit 0x1b2 is what guarantees the server object is
    /// drawn and animated (live 2026-10-31: the King's default-animation projectiles were static/invisible). A once-only
    /// clip is stretched over the flight (the mortar's 1 s <c>proj</c> over its arc:
    /// <c>mortar_projectile_scale_animation_speed_instead_of_projectile_speed</c>), and the impact clip plays where it
    /// stops, keeping the object for the clip's length. Null without clip data: the object then flies as its .ao
    /// presents it by itself.</summary>
    public static ProjectilePresentation? MonsterProjectilePresentation(SkillCatalog.ProjectileClips? clips, double flightSeconds)
    {
        if (clips is null || clips is { Flight: null, Impact: null }) return null;
        var presentation = new ProjectilePresentation();
        if (clips.Flight is { } flight)
            presentation = presentation with
            {
                FlightClip = flight.Index,
                FlightClipSpeed = flight.Once && flightSeconds > 0 ? (float)Math.Clamp(flight.Ms / 1000.0 / flightSeconds, 0.1, 10) : 1,
            };
        if (clips.Impact is { } impact)
            presentation = presentation with
            {
                ImpactClip = impact.Index,
                ImpactLifetime = ProjectileRuntime.Time(Math.Max(0.1, impact.Ms / 1000.0)),
            };
        return presentation;
    }

    // The release at the attack's contact: the flight owns contact and damage (HostileHit).
    // The AI action must still be current when the runtime releases it.
    public static void LaunchMonsterProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint id, SpawnedMonster m, MonsterAttack a, SkillCatalog.MonsterProjectileInfo projectile, int action,
        (int X, int Y)? orderedAim = null, double? releaseAt = null, ushort sequence = MonsterActionSequence)
    {
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        // A spiral the client draws: one step at a time from the monster's cell then, each turned from the aim the
        // first step took (MonsterShots.Spiral); the steps run on once released.
        if (projectile is { ClientDraws: true } && MonsterShots.Spiral(projectile) is { } spiral)
        {
            double first = releaseAt ?? session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
            ProjectileVector? line = null;
            for (int k = 0; k < spiral.Steps; k++)
            {
                int step = k;
                double turn = spiral.Offset + step * spiral.Step;
                // The first step needs the action; the rest follow it once it has flown.
                // A lockstep client counts the steps down by its frames: each fires on the frame that crosses its time.
                double stepAt = step == 0 ? first : conn.LockstepClient ? LockstepShotAt(first, step * spiral.Interval) : first + step * spiral.Interval;
                session.Schedule(ProjectileRuntime.Time(stepAt),
                    () => (step == 0 ? MonsterActionCurrent(conn, id, m, action) : line is not null) && !InGracePeriod(conn),
                    () =>
                    {
                        var use = MonsterProjectileUse(session, id, m, a, projectile, action, orderedAim: orderedAim, sequence: sequence);
                        line ??= use.Aim - use.Origin;
                        IReadOnlyList<double> arms = spiral.Both ? [turn, -turn] : [turn];
                        return use with
                        {
                            Aim = use.Origin + line.Value,
                            Profile = use.Profile with { Count = arms.Count, NativeVolley = new() { Angles = arms } },
                        };
                    });
            }
            return;
        }
        // A riptide's wave sets off 1.2 s after its release (ctor 0x141b39320: +0x524 = -1.2), whatever its caster does
        // by then: only its caster's presence is checked then (its action was, at the release).
        double delay = projectile.Recipe == "Riptide" ? 1.2 : 0;
        session.Schedule((releaseAt is { } at ? ProjectileRuntime.Time(at) : session.Runtime.Now) + ProjectileRuntime.Time(delay),
            () => (delay > 0 ? MonsterOutlivesAction(conn, id, m) : MonsterActionCurrent(conn, id, m, action)) && !InGracePeriod(conn),
            () => MonsterProjectileUse(session, id, m, a, projectile, action, orderedAim: orderedAim, sequence: sequence));
    }

    // Called under the XP -> Monsters lock by the runtime. Only the player takes a monster's projectile; a friendly monster in
    // its way is passed through. The monster's damage pipeline (CommitMonsterHitLocked) owns evasion, block, armour,
    // ailments, recovery and the hit packet; the presentation follows through the session's ordered outbox.
    public static ProjectileHitResult HostileHit(ProjectileSession session, ProjectileDamage contact, HostileProjectileOffense shot)
    {
        var conn = session.Conn;
        // The flight's own damage multiplier (an ice spear's second form, a burst's or a bounce's) scales the attack's hit,
        // and a fire mortar's hop hits by fire_mortar_second_hit_damage_+%_final (0x141a10f00).
        double multiplier = 1; // Contact scaling belongs to the shared player/minion hit commit below.
        if (contact.Projectile.GroundBounces > 0 && shot.Attack.Projectile is { } fired)
            multiplier *= Math.Max(0, 1 + MonsterShots.Stat(fired, "fire_mortar_second_hit_damage_+%_final") / 100.0);
        if (multiplier != 1) shot = shot with { Attack = shot.Attack with { Damage = shot.Attack.Damage.Scale(multiplier) } };
        if (OwnedFighter(conn, contact.Target.Id) is { } golem)
        {
            bool landed = GolemTakeHitLocked(conn, golem, shot.MonsterId, shot.Monster, shot.Attack with { Damage = shot.Attack.Damage.Scale(contact.Multiplier * shot.MinionDamageFactor) },
                Math.Max(AreaTime(conn), contact.At / (double)ProjectileRuntime.QuantaPerSecond), area: contact.IsArea || shot.Attack.Projectile?.Lobbed == true);
            session.Enqueue(() => PublishMinionOutcomes(session.remote, session.stream, session.ciphers, conn));
            return landed ? ProjectileHitResult.Hit : ProjectileHitResult.Miss;
        }
        if (contact.Target.Id != PlayerId || conn.PlayerLife == 0 || InGracePeriod(conn)) return ProjectileHitResult.Miss;
        var outcome = CommitHostilePlayerHitLocked(session, contact, shot);
        if (outcome is null) return ProjectileHitResult.Miss;
        session.World(shot.MonsterId, outcome.HitPacket, $"0x01af monster 0x{shot.MonsterId:x} projectile hit on the player ({shot.Label})");
        session.Enqueue(() => PresentMonsterHit(session.remote, session.stream, session.ciphers, conn, shot.MonsterId, outcome));
        return outcome.Blocked ? ProjectileHitResult.Blocked : ProjectileHitResult.Hit;
    }

    public static MonsterHitOutcome? CommitHostilePlayerHitLocked(ProjectileSession session, ProjectileDamage contact, HostileProjectileOffense shot)
    {
        var conn = session.Conn;
        // The hit pipeline's timers (fortification, recent hits, ailments, evasion entropy) run on the connection clock and
        // refuse to go backwards. The contact's simulated instant lies a few milliseconds behind that clock, which other
        // reads (a stats refresh, a melee swing) may already have advanced, so the hit lands at whichever is later. The
        // runtime's fixtures run their simulation ahead of the clock; there the contact time wins.
        double now = Math.Max(contact.At / (double)ProjectileRuntime.QuantaPerSecond, conn.Clock.Elapsed.TotalSeconds);
        var here = conn.Walk.Position(now);
        var from = ((int)Math.Round(contact.Position.X), (int)Math.Round(contact.Position.Y));
        return CommitMonsterHitLocked(session.remote, conn, shot.MonsterId, shot.Monster, shot.Attack with { Damage = shot.Attack.Damage.Scale(contact.Multiplier) }, now, here, from,
            checkRange: false, sequence: shot.Sequence);
    }
}

/// <summary>A monster's projectile attack in flight: the monster that released it, its attack and the AI action it belongs
/// to. The player is its only target (<c>Game.HostileHit</c>); the monster's own damage pipeline commits the hit when
/// the flight reaches the player, or, for a mortar, when it lands on them.</summary>
public sealed record HostileProjectileOffense(uint MonsterId, SpawnedMonster Monster, MonsterAttack Attack, int Action, string Label)
{
    public double MinionDamageFactor { get; init; } = 1;
    /// <summary>The action that released this shot, retained for the client's hit lookup (0x141e977f0).</summary>
    public ushort Sequence { get; init; } = Game.MonsterActionSequence;
    /// <summary>The rig clips the object needs to be seen (Game.MonsterProjectilePresentation).</summary>
    public Game.ProjectilePresentation? Presentation { get; init; }
}

/// <summary>A shot of a server object that is no monster (a Trial of Ascendancy's dart trap). Its own rule commits a
/// contact, under the connection's lock as <c>Game.HostileHit</c> is; a miss lets the flight go on.</summary>
public sealed record ObjectProjectileOffense(string Label, Func<Game.ProjectileSession, ProjectileDamage, ProjectileHitResult> Hit)
{
    /// <summary>The rig clips the object needs to be seen (Game.MonsterProjectilePresentation).</summary>
    public Game.ProjectilePresentation? Presentation { get; init; }
    /// <summary>No release, visual or end lines in the log: the shot's own rule logs what matters (its hits).</summary>
    public bool Quiet { get; init; }
}
