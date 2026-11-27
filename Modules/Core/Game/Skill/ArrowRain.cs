using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string RainOfArrowsArtillery = "RainOfArrowsAltX";
    const string RainOfArrowsSaturation = "RainOfArrowsAltY";

    static bool IsRainOfArrowsVariant(string? id) => id is RainOfArrowsArtillery or RainOfArrowsSaturation;

    static bool ArrowRain(string? id) => IsRainOfArrows(id) || IsBlastRain(id) || id is TrarthusBlastRain or ConflagrationEffect
        || IsRainOfArrowsVariant(id) || IsToxicRain(id) || IsKineticRain(id) || IsStormRain(id);

    static bool IsRainOfArrows(string? id) => id is "RainOfArrows" or "RainOfArrowsRoyale";
    static bool IsBlastRain(string? id) => id is "BlastRain" or "BlastRainRoyale";
    static bool IsToxicRain(string? id) => id is "ToxicRain" or "ToxicRainAltX" or "ToxicRainAltY" or "ToxicRainRoyale";

    static bool IsArtilleryBallista(string? id) =>
        id is "ArtilleryBallista" or "ArtilleryBallistaAltX" or "ArtilleryBallistaAltY" or "ArtilleryBallistaRoyale";

    // These bow attacks have area hit stats but no travelling Projectiles row. Capture the normal
    // offensive snapshot at release; each arrow resolves its own area at the landing cell.
    static readonly ProjectileSkillDefinition GroundArrow = new("Metadata/Projectiles/DefaultArrow",
        new((get, speed, _) => GatewayProjectileDefaults.Resolve("Metadata/Projectiles/DefaultArrow", get, speed, attack: true, arrows: true)));

    public static IReadOnlyList<ProjectileVector> RainLandings(string effect, ProjectileVector aim, int count,
        double spread, IReadOnlyList<ProjectileVector> enemies, Random random)
    {
        bool rainOfArrows = IsRainOfArrows(effect);
        var points = new List<ProjectileVector>();
        var available = enemies.Where(p => (p - aim).Length <= spread).OrderBy(p => (p - aim).Length).ToArray();
        for (int i = 0; i < count; i++)
        {
            if (rainOfArrows && i % 2 == 1 && i / 2 < available.Length)
                points.Add(available[i / 2]);
            else
            {
                double radius = spread * Math.Sqrt(random.NextDouble());
                if (rainOfArrows) radius *= Math.Sqrt(i / (double)count);
                points.Add(aim + new ProjectileVector(1, 0).Rotate(random.NextDouble() * Math.Tau) * radius);
            }
        }
        return points;
    }

    // The 3.29.3c line starts at the archer and extends 90 cells along the aim. The lateral scatter does not
    // shorten the line. Keep each arrow's index even if terrain prevents a landing.
    static IEnumerable<(ProjectileVector At, int Step)> RainArtilleryLine(ProjectileVector origin, ProjectileVector aim,
        int count, IReadOnlyList<ProjectileVector> enemies, Func<ProjectileVector, bool> clear, Random random)
    {
        const int length = 90;
        if (aim == origin)
        {
            aim = enemies.Where(p => p != origin && (p - origin).Length <= length && clear(p))
                .OrderBy(p => (p - origin).Length).FirstOrDefault(origin);
            if (aim == origin)
                aim += new ProjectileVector(1, 0).Rotate(random.NextDouble() * Math.Tau);
        }
        var along = (aim - origin).Unit;
        var across = new ProjectileVector(-along.Y, along.X);
        var end = along * length;
        end = new(Math.Round(end.X), Math.Round(end.Y));
        for (int i = 0; i < count; i++)
        {
            var centre = end * (i / (double)count);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                double scatter = random.NextDouble();
                double side = random.NextDouble() < .5 ? -1 : 1;
                var offset = centre + across * (10 * scatter * scatter * side);
                var point = origin + new ProjectileVector(Math.Truncate(offset.X), Math.Truncate(offset.Y));
                if (!clear(point)) continue;
                yield return (point, i);
                break;
            }
        }
    }

    // Saturation lands a pair at a time. From the second pair onward one arrow can choose an unused enemy
    // inside the current annulus; the other samples the ground. Later pairs expand the annulus by less.
    static IEnumerable<(ProjectileVector At, int Step)> RainSaturationRings(ProjectileVector aim, int count,
        double areaScale, IReadOnlyList<ProjectileVector> enemies, Func<ProjectileVector, bool> clear, Random random)
    {
        yield return (aim, 0);
        double inner = 0, band = 7 * areaScale, advance = 3 * areaScale;
        var targeted = new HashSet<ProjectileVector> { aim };
        var candidates = enemies.Select(p => (At: p, Distance: (p - aim).Length)).ToArray();
        for (int i = 1; i < count; i++)
        {
            double outer = inner + band;
            ProjectileVector? point = null;
            if (i % 2 == 0)
            {
                int eligible = 0, farther = 0;
                ProjectileVector? fallback = null;
                foreach (var target in candidates)
                {
                    if (target.Distance < inner || target.Distance > outer || targeted.Contains(target.At) || !clear(target.At))
                        continue;
                    if (target.Distance <= inner + 3)
                    {
                        if (random.Next(++eligible) == 0) point = target.At;
                    }
                    else if (eligible == 0 && random.Next(++farther) == 0)
                        fallback = target.At;
                }
                point ??= fallback;
                if (point is { } at) targeted.Add(at);
            }
            if (point is null)
            {
                double distance = inner + band * random.NextDouble();
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    var offset = new ProjectileVector(distance, 0).Rotate(random.NextDouble() * Math.Tau);
                    var landing = aim + new ProjectileVector(Math.Floor(offset.X + .5), Math.Floor(offset.Y + .5));
                    if (!clear(landing)) continue;
                    point = landing;
                    break;
                }
            }
            if (point is { } landingPoint) yield return (landingPoint, i / 2);
            if (i % 2 != 0) inner += advance * (1 - .6 * i / count);
        }
    }

    static IReadOnlyList<ProjectileVector> ArtilleryLandings(ProjectileVector origin, ProjectileVector aim, int count,
        bool cross, bool focus, double range, IReadOnlyList<ProjectileVector> enemies)
    {
        if (focus)
            return enemies.Where(p => (p - origin).Length <= range).OrderBy(p => (p - aim).Length).Take(count).ToArray();

        var forward = (aim - origin).Unit;
        if (forward.Length == 0) forward = new(1, 0);
        var across = new ProjectileVector(-forward.Y, forward.X);
        // Use a fixed cell interval for this landing line. Only each impact's coverage scales with area modifiers.
        const double spacing = 10;
        var points = new ProjectileVector[count];
        for (int i = 0; i < count; i++)
        {
            bool side = cross && i % 2 != 0;
            int axisCount = cross ? side ? count / 2 : (count + 1) / 2 : count;
            int index = cross ? i / 2 : i;
            points[i] = aim + (side ? across : forward) * ((index - (axisCount - 1) / 2d) * spacing);
        }
        return points;
    }

    // Each arrow's impact radius and how far from the aim the arrows land: Blast Rain's land within its own impact radius.
    public static (int Radius, double Spread) RainArea(Player player, string id, int effect, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> playerStats)
    {
        bool toxic = IsToxicRain(id), blast = IsBlastRain(id);
        int radius = SkillRadius(player, toxic ? 18 : blast ? 20 : 10, own, false, effect, playerStats);
        return (radius, blast ? radius * .85 : SkillRadius(player, 24, own, false, effect, playerStats));
    }

    internal static void ReleaseArrowRain(ProjectileSession session, ushort skill,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        SkillCriticalRoll critical, ushort sequence, bool mirage = false,
        Func<ProjectileOffense, ProjectileOffense>? transform = null, double podDamageFactor = 1, (int X, int Y)? from = null,
        double? actionStart = null)
        => ReleaseArrowRain(session, skill, gem, aim, critical, sequence, mirage, transform, podDamageFactor,
            totem: null, from, actionStart);

    static void ReleaseArrowRain(ProjectileSession session, ushort skill,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        SkillCriticalRoll critical, ushort sequence, bool mirage, Func<ProjectileOffense, ProjectileOffense>? transform,
        double podDamageFactor, TotemState.Totem? totem, (int X, int Y)? from = null, double? actionStart = null)
    {
        var player = session.Player;
        string id = SkillsFor(player).EffectId(gem.Effect)!;
        // from: where the volley leaves when it is not the player or a totem (a trap or mine).
        var use = CaptureGemProjectile(player, session, skill, gem, aim, critical, sequence, from: from, totem: totem);
        var shot = (ProjectileOffense)use.Payload!;
        var supported = SupportedSkill(player, skill);
        var own = supported.Stats;
        var playerStats = CharacterPlayerStats(player);
        var placedKind = from is not null ? PlacedKind(supported) : default;
        bool fromPlaced = from is not null && (placedKind.Trap || placedKind.Mine);
        if (fromPlaced)
            shot = shot with
            {
                ActualCaster = shot.ActualCaster ?? TotemCasterStats(playerStats),
                MirageSource = null,
                CriticalTrigger = null,
                Leech = new(DamagePacket.Empty, DamagePacket.Empty, DamagePacket.Empty),
            };
        if (mirage) shot = MirageOffense(player, shot);
        if (transform is not null) shot = transform(shot);
        use = use with { Payload = shot with { AreaVisual = null, Presentation = new(), ClientPredicted = true,
            FromTrapOrMine = shot.FromTrapOrMine || from is not null,
            FromMine = shot.FromMine || placedKind.Mine } };
        if (id == ConflagrationEffect)
        {
            ReleaseConflagration(session, use, skill, gem.Effect, own, playerStats,
                podDamageFactor, placedKind.Trap, placedKind.Mine, actionStart);
            return;
        }
        if (IsStormRain(id))
        {
            ReleaseStormRain(session, use, id, skill, gem.Effect, own, playerStats, actionStart);
            return;
        }
        if (IsKineticRain(id))
        {
            ReleaseKineticRain(session, use, gem, id, aim, own, playerStats, critical, sequence);
            return;
        }
        if (id == TrarthusBlastRain)
        {
            ReleaseTrarthusBlastRain(session, use, own, playerStats, skill, gem.Effect,
                podDamageFactor, placedKind.Trap, placedKind.Mine, actionStart);
            return;
        }
        int Own(uint stat) => own.GetValueOrDefault(stat);
        int Player(uint stat) => playerStats.GetValueOrDefault(stat);
        int Get(uint key) => Own(key) + Player(key);
        int variation = SkillArtVariation(player, gem.Gem);
        string? ArtOf(string key) => Art.Animated(key, variation) ?? Art.Animated(key, 0);
        bool toxic = IsToxicRain(id), blast = IsBlastRain(id), artillery = IsArtilleryBallista(id);
        int extraRain = Get(Stat.ModifiersToProjectileCountDoNotApply) != 0 ? 0
            : (toxic ? Get(Stat.ToxicRainNumOfAdditionalProjectiles) : 0)
            + (artillery ? Get(Stat.ArtilleryBallistaNumAdditionalArrows) : 0)
            + (mirage ? Get(Stat.MirageArcherNumberOfAdditionalProjectiles) : 0);
        int count = Math.Clamp(GatewayProjectileDefaults.Resolve(GroundArrow.Metadata, Get, 1000, true, true).Count
            + extraRain, 1, 256);
        bool variant = IsRainOfArrowsVariant(id);
        long now = session.Runtime.Now;
        double releasedAt = now / (double)ProjectileRuntime.QuantaPerSecond;
        double radiusScale = 1;
        int radius;
        double spread = 0;
        if (variant)
        {
            var area = SkillAreaStats(player, own, gem.Effect, playerStats);
            radiusScale = (100d + SkillArea.RadiusPercent(area.GetValueOrDefault, spell: false)) / 100;
            radius = (int)(10 * radiusScale);
        }
        else (radius, spread) = RainArea(player, id, gem.Effect, own, playerStats);
        var enemies = session.Actors(now, now).Where(a => a.Team != use.Team).Select(a => a.Position(now)).ToArray();
        var target = ProjectileSession.Vector(aim);
        var random = AreaRng(player);
        var landings = VariantOrStandardLandings();

        IEnumerable<(ProjectileVector At, int Step)> VariantOrStandardLandings()
        {
            if (!variant)
            {
                var points = artillery
                    ? ArtilleryLandings(use.Origin, target, count, Get(Stat.ArtilleryBallistaCrossStrafePattern) != 0,
                        Get(Stat.ArtilleryBallistaNumberOfArrowsIsEqualToNumberOfNearbyTargets) != 0,
                        TotemTargetRange(player, skill), enemies)
                    : RainLandings(id, target, count, spread, enemies, random);
                return points.Select((point, index) => (point, index));
            }

            var source = totem?.At ?? from ?? player.Walk.Position(actionStart ?? releasedAt);
            use = use with { Origin = ProjectileSession.Vector(source) };
            var layout = player.Layout;
            var barriers = player.SightBarrier;
            bool Clear(ProjectileVector point)
            {
                var cell = ((int)point.X, (int)point.Y);
                return cell.Item1 >= 0 && cell.Item1 < layout.GridW && cell.Item2 >= 0 && cell.Item2 < layout.GridH
                    && (layout.Sight is not { } sight || !ClientPathfinder.LineBelow(source, cell, sight, 1))
                    && (barriers.Count == 0 || ClientPathfinder.Line(source, cell).All(p => !barriers.Contains(p)));
            }

            return id == RainOfArrowsArtillery ? RainArtilleryLine(use.Origin, target, count, enemies, Clear, random)
                : RainSaturationRings(target, count, radiusScale, enemies, Clear, random);
        }
        string? up = toxic ? ArtOf("toxic_rain_arrow_up") : blast ? "Metadata/Effects/Spells/blast_rain/arrow_up.ao" : ArtOf("expanding_rain_of_arrows_up");
        string? down = artillery ? "Metadata/Effects/Spells/mortar_totem/arrow_down.ao"
            : toxic ? ArtOf("toxic_rain_arrow_down") : blast ? "Metadata/Effects/Spells/blast_rain/arrow_drop.ao" : ArtOf("expanding_rain_of_arrows_arrow");
        string? impact = artillery ? "Metadata/Effects/Spells/mortar_totem/impact.ao"
            : toxic ? null : blast ? "Metadata/Effects/Spells/blast_rain/blastrainexplosion_aoe.ao" : ArtOf("expanding_rain_of_arrows_explosion");
        // The volley leaves towards the aim and each arrow comes down leaning the way it was shot (an object's
        // rotation: atan2(x, -y), as a projectile's).
        static float Facing(ProjectileVector d) => d.Length < .001 ? 0 : (float)Math.Atan2(d.X, -d.Y);
        // A server effect does not start its .ao's default_animation: Toxic Rain's shoot-up art (spores_shootup.ao,
        // one clip "animate" that carries its vine trails) drew nothing until the clip was sent (live 2026-11-17).
        // Rain of Arrows' and Blast Rain's are not drawn: expanding_rain_of_arrows_up is one fixed arrow mesh
        // (rain_of_arrows/arrow_to_rain.ao) that rides a per-class clip of rain_of_arrows_anim/rig.ao on the archer, and
        // blast_rain/arrow_up.ao has the same per-class clips; made as an effect at the release either one is a still
        // arrow lying at the archer's feet (live 2026-11-19).
        if (toxic) session.RainArt(use.Origin, up, 1, rotation: Facing(ProjectileSession.Vector(aim) - use.Origin), clip: 0);
        // shortcut: the client flies the skill's arrow model up itself (toxic_rain_projectile, rain_of_spores/arrow.ao,
        // a fixed mesh); how is not read. Its streak and flash at the archer are stood in for by the falling arrow's
        // art, played once where the shot leaves.
        if (toxic) session.RainArt(use.Origin, down, 1, rotation: Facing(ProjectileSession.Vector(aim) - use.Origin), clip: 0);
        double spacing = blast ? Math.Max(0, Get(Stat.BlastRainArrowDelayMs)) / 1000d : toxic ? 0 : .04;
        double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Player, Own);
        var damageContext = new SkillDamageContext(SkillHitKind.Attack, shot.ActualCaster ?? playerStats);
        bool recentMine = placedKind.Mine && player.RecentEvents.Happened(StatConditions.DetonatedMine, AreaTime(player));
        double dps = toxic && id != "ToxicRainAltX" && !damageContext.Suppressed
            ? ToxicPodDps(Get, Own, totem is not null, placedKind.Trap, placedKind.Mine, recentMine)
                * (mirage ? MirageDamageFactor(own) : 1) * podDamageFactor : 0;
        if (totem is not null && dps > 0)
            dps *= Math.Max(0, 1 + Get(Stat.SupportTotemDamagePlusPctFinal) / 100d);
        double? withered = Own(Stat.ToxicRainSporesApplyWithered) != 0
            ? WitheredPct * Math.Max(0, 1 + Get(Stat.WitheredEffectPlusPct) / 100d) : null;
        // The falling arrow's art is one clip (index 0: spore_drop.ao and blast_rain's arrow_drop.ao "animate",
        // rain_of_arrows/rig.ao "rainofarrows", which has no default animation) that reaches the ground part-way in:
        // its impact particles at 0.224 s and 0.218 s, Blast Rain's "contact" at 0.333 s. So it starts that long before
        // the arrow lands.
        double fall = toxic ? .224 : blast ? .333 : .218;
        double travel = artillery ? .35 * (SkillsFor(player).Projectile(GroundArrow.Metadata)?.Speed ?? 1000)
            / (WorldUnits.PerCell * Math.Max(1, use.Profile.Speed)) : .35;
        if (variant)
        {
            // Expanding Rain of Arrows uses a 500 ms initial fall, then 35 ms per line arrow or 210 ms per ring pair.
            int fallSpeed = RainOfArrowsFallSpeed(player, skill, playerStats, own, totem is not null);
            double fallTime = 100d / (100 + fallSpeed);
            travel = Math.Max(0, .5 * Math.Min(2.4, fallTime) - (releasedAt - (actionStart ?? releasedAt)));
            spacing = (id == RainOfArrowsArtillery ? .035 : .21) * Math.Clamp(fallTime, .5, 2);
        }
        foreach (var (at, step) in landings)
        {
            long land = now + ProjectileRuntime.Time(travel + step * spacing);
            if (variant && ServerTrace.Projectiles)
                Console.WriteLine($"[rain-landing] {id} step={step} origin={use.Origin} at={at} due={land} start={actionStart:F6}");
            session.Runtime.Schedule(Math.Max(session.Runtime.Now, land - ProjectileRuntime.Time(fall)), () =>
            {
                if (session.Active && player.PlayerLife != 0) session.RainArt(at, down, 1, rotation: Facing(at - use.Origin), clip: 0);
            });
            session.Runtime.Schedule(land, () =>
            {
                if (!session.Active || player.PlayerLife == 0) return;
                session.Runtime.Impact(use, at, new(id, radius));
                if (!toxic) session.RainArt(at, impact, 1, radius);
                else if (duration > 0)
                {
                    // Each pod and its roots face their own way.
                    float turn = (float)(Random.Shared.NextDouble() * Math.Tau);
                    session.RainArt(at, ArtOf("toxic_rain_place"), duration, rotation: turn);
                    // The pod is its own object type (rain_of_spores/BloomPod); only its model is drawn here, with the
                    // clips BloomPod.ot plays: 1 (grow_slow_01) when it is made and one of 2-4 (grow_slow_explode_01-03,
                    // 0.966 s) when it bursts. Every skin's pod extends that type, so the indices hold for all.
                    session.RainArt(at, Art.ObjectModel("toxic_rain_spore", variation) ?? Art.ObjectModel("toxic_rain_spore", 0), duration,
                        rotation: turn, clip: 1, endClip: ((uint)Random.Shared.Next(2, 5), .966));
                    session.PlaceToxicPod(use, at, radius, duration, dps,
                        -Get(Stat.RainOfSporesVinesMovementSpeedPlusPctFinal), -Get(Stat.MinimumRainOfSporesMovementSpeedPlusPctFinalCap),
                        ArtOf("toxic_rain_explode"), withered);
                }
            });
        }
    }

    static int RainOfArrowsFallSpeed(Player player, ushort skill, IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int> own, bool proxy = false) =>
        ArrowRainTiming(player, skill, playerStats, own, proxy).FallSpeed;

    // The client's derived fall speed applies attack speed, half the combined arrow speed increase and
    // half the final projectile speed percent. Action speed only affects the release animation.
    static (int FallSpeed, double AttackSeconds) ArrowRainTiming(Player player, ushort skill,
        IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int> own, bool proxy = false)
    {
        int Get(uint stat) => playerStats.GetValueOrDefault(stat) + own.GetValueOrDefault(stat);
        var (attackSeconds, attackIncrease, attackMore) = SupportedAttackTiming(player, skill, null, clampToTick: false, proxy);
        int arrowIncrease = playerStats.GetValueOrDefault(Stat.ProjectileSpeedPlusPct)
            + own.GetValueOrDefault(Stat.BaseProjectileSpeedPlusPct) + Get(Stat.BaseArrowSpeedPlusPct);
        float projectileMore =
            Math.Max(0, 1 + Get(Stat.ActiveSkillProjectileSpeedPlusPctFinal) / 100f)
            * Math.Max(0, 1 + Get(Stat.SupportSlowerProjectilesProjectileSpeedPlusPctFinal) / 100f)
            * Math.Max(0, 1 + Get(Stat.UltimatumProjectileSpeedPlusPctFinal) / 100f)
            * Math.Max(0, 1 + Get(Stat.PassiveMasteryMoreProjectileSpeedPlusPctFinal) / 100f)
            * Math.Max(0, 1 + Get(Stat.PassiveMasteryLessProjectileSpeedPlusPctFinal) / 100f);
        int projectileMorePct = (int)MathF.Round(100 * projectileMore - 100, MidpointRounding.AwayFromZero);
        double localAttackMore = Math.Max(0, 1 + WeaponProfileFor(player, activeSet: true).LocalAttackSpeedPct / 100d);
        int fallSpeed = SkillAttackTiming.FinalSpeedPercent(attackIncrease + arrowIncrease / 2,
            attackMore * localAttackMore * Math.Max(0, 1 + projectileMorePct / 2 / 100d));
        return (fallSpeed, attackSeconds);
    }

    static readonly uint[] ChaosGroundMoreStats =
    [
        Stat.SupportVoidManipulationChaosDamagePlusPctFinal, Stat.SupportRapidDecayDamageOverTimePlusPctFinal,
        Stat.SupportEfficacyDamageOverTimePlusPctFinal, Stat.SupportPhysChaosProjectileChaosDamageOverTimePlusPctFinal,
        Stat.SupportAreaConcentrateAreaDamagePlusPctFinal, Stat.CrueltyDamageOverTimePlusPctFinal,
    ];

    static readonly uint[] PlacedChaosGroundMoreStats =
    [
        Stat.SupportTrapDamagePlusPctFinal, Stat.SupportTrapAndMineDamagePlusPctFinal,
        Stat.SupportClustertrapDamagePlusPctFinal, Stat.SupportMultithrowDamagePlusPctFinal,
        Stat.SupportGemMineDamagePlusPctFinal, Stat.SupportRemoteMine2DamagePlusPctFinal,
        Stat.SupportLocusMineDamagePlusPctFinal,
    ];

    static double ToxicPodDps(Func<uint, int> get, Func<uint, int> own, bool totem = false,
        bool trap = false, bool mine = false, bool recentMine = false) =>
        ChaosGroundDps(get, own, Stat.ToxicRainDamagePlusPct, totem, trap, mine, recentMine);

    // A projectile attack's chaos damage over time in an area: Toxic Rain's pods and Caustic Arrow's ground.
    // skillIncrease: the skill's own increased damage stat, if it has one.
    static double ChaosGroundDps(Func<uint, int> get, Func<uint, int> own, uint? skillIncrease, bool totem = false,
        bool trap = false, bool mine = false, bool recentMine = false)
    {
        if (DamageStats.Prohibited(DamageType.Chaos, get)) return 0;
        double increased = (double)get(Stat.DamagePlusPct) + get(Stat.ChaosDamagePlusPct) + get(Stat.AreaDamagePlusPct)
            + get(Stat.ProjectileDamagePlusPct) + get(Stat.DamageOverTimePlusPct) + get(Stat.ChaosDamageOverTimePlusPct)
            + (totem ? get(Stat.TotemDamagePlusPct) : 0)
            + (trap || mine ? (double)get(mine ? Stat.MineDamagePlusPct : Stat.TrapDamagePlusPct)
                + get(Stat.TrapOrMineDamagePlusPct) + (recentMine ? get(Stat.MineAreaDamagePlusPctIfDetonatedMineRecently) : 0) : 0)
            + (skillIncrease is uint key ? get(key) : 0);
        double more = 1;
        foreach (var stat in ChaosGroundMoreStats) more *= Math.Max(0, 1 + own(stat) / 100d);
        if (trap || mine)
        {
            more *= Math.Max(0, 1 + get(Stat.SkitterbotsTrapMineDamagePlusPctFinal) / 100d);
            foreach (var stat in PlacedChaosGroundMoreStats) more *= Math.Max(0, 1 + own(stat) / 100d);
        }
        return Math.Max(0, get(Stat.BaseChaosDamageToDealPerMinute) / 60d) * more * Math.Max(0, 1 + increased / 100)
            * Math.Max(0, 1 + ((double)get(Stat.DotMultiplierPlus) + get(Stat.ChaosDotMultiplierPlus)
                + get(Stat.DotMultiplierPlusWithBowSkills)) / 100);
    }
}

