using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static bool ArrowRain(string? id) => id is "RainOfArrows" or "BlastRain" || IsToxicRain(id) || IsKineticRain(id);

    static bool IsToxicRain(string? id) => id is "ToxicRain" or "ToxicRainAltX" or "ToxicRainAltY";

    static bool IsArtilleryBallista(string? id) =>
        id is "ArtilleryBallista" or "ArtilleryBallistaAltX" or "ArtilleryBallistaAltY";

    // These bow attacks have area hit stats but no travelling Projectiles row. Capture the normal
    // offensive snapshot at release; each arrow resolves its own area at the landing cell.
    static readonly ProjectileSkillDefinition GroundArrow = new("Metadata/Projectiles/DefaultArrow",
        new((get, speed, _) => GatewayProjectileDefaults.Resolve("Metadata/Projectiles/DefaultArrow", get, speed, attack: true, arrows: true)));

    public static IReadOnlyList<ProjectileVector> RainLandings(string effect, ProjectileVector aim, int count,
        double spread, IReadOnlyList<ProjectileVector> enemies, Random random)
    {
        var points = new List<ProjectileVector>();
        var available = enemies.Where(p => (p - aim).Length <= spread).OrderBy(p => (p - aim).Length).ToArray();
        for (int i = 0; i < count; i++)
        {
            if (effect == "RainOfArrows" && i % 2 == 1 && i / 2 < available.Length)
                points.Add(available[i / 2]);
            else
            {
                double radius = spread * Math.Sqrt(random.NextDouble());
                if (effect == "RainOfArrows") radius *= Math.Sqrt(i / (double)count);
                points.Add(aim + new ProjectileVector(1, 0).Rotate(random.NextDouble() * Math.Tau) * radius);
            }
        }
        return points;
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
        bool toxic = IsToxicRain(id), blast = id == "BlastRain";
        int radius = SkillRadius(player, toxic ? 18 : blast ? 20 : 10, own, false, effect, playerStats);
        return (radius, blast ? radius * .85 : SkillRadius(player, 24, own, false, effect, playerStats));
    }

    internal static void ReleaseArrowRain(ProjectileSession session, ushort skill,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        SkillCriticalRoll critical, ushort sequence, bool mirage = false,
        Func<ProjectileOffense, ProjectileOffense>? transform = null, double podDamageFactor = 1, (int X, int Y)? from = null)
        => ReleaseArrowRain(session, skill, gem, aim, critical, sequence, mirage, transform, podDamageFactor, totem: null, from);

    static void ReleaseArrowRain(ProjectileSession session, ushort skill,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        SkillCriticalRoll critical, ushort sequence, bool mirage, Func<ProjectileOffense, ProjectileOffense>? transform,
        double podDamageFactor, TotemState.Totem? totem, (int X, int Y)? from = null)
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
            FromMine = shot.FromMine || placedKind.Mine,
            Penetration = placedKind.Mine && !shot.FromMine
                ? Penetration.WithMine(shot.Penetration, shot.ActualCaster ?? playerStats, own) : shot.Penetration } };
        if (IsKineticRain(id))
        {
            ReleaseKineticRain(session, use, gem, id, aim, own, playerStats, critical, sequence);
            return;
        }
        int Own(uint stat) => own.GetValueOrDefault(stat);
        int Player(uint stat) => playerStats.GetValueOrDefault(stat);
        int Get(uint key) => Own(key) + Player(key);
        int variation = SkillArtVariation(player, gem.Gem);
        string? ArtOf(string key) => Art.Animated(key, variation) ?? Art.Animated(key, 0);
        bool toxic = IsToxicRain(id), blast = id == "BlastRain", artillery = IsArtilleryBallista(id);
        int extraRain = Get(Stat.ModifiersToProjectileCountDoNotApply) != 0 ? 0
            : (toxic ? Get(Stat.ToxicRainNumOfAdditionalProjectiles) : 0)
            + (artillery ? Get(Stat.ArtilleryBallistaNumAdditionalArrows) : 0)
            + (mirage ? Get(Stat.MirageArcherNumberOfAdditionalProjectiles) : 0);
        int count = Math.Clamp(GatewayProjectileDefaults.Resolve(GroundArrow.Metadata, Get, 1000, true, true).Count
            + extraRain, 1, 256);
        var (radius, spread) = RainArea(player, id, gem.Effect, own, playerStats);
        var enemies = session.Actors(session.Runtime.Now, session.Runtime.Now)
            .Where(a => a.Team != use.Team).Select(a => a.Position(session.Runtime.Now)).ToArray();
        var landings = artillery
            ? ArtilleryLandings(use.Origin, ProjectileSession.Vector(aim), count,
                Get(Stat.ArtilleryBallistaCrossStrafePattern) != 0,
                Get(Stat.ArtilleryBallistaNumberOfArrowsIsEqualToNumberOfNearbyTargets) != 0,
                TotemTargetRange(player, skill), enemies)
            : RainLandings(id, ProjectileSession.Vector(aim), count, spread, enemies, AreaRng(player));
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
        for (int i = 0; i < landings.Count; i++)
        {
            var at = landings[i];
            long land = session.Runtime.Now + ProjectileRuntime.Time(travel + i * spacing);
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

