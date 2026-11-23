using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static bool ArrowRain(string? id) => id is "RainOfArrows" or "BlastRain" or "ToxicRain";

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

    // Each arrow's impact radius and how far from the aim the arrows land: Blast Rain's land within its own impact radius.
    public static (int Radius, double Spread) RainArea(Player player, string id, int effect, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> playerStats)
    {
        bool toxic = id == "ToxicRain", blast = id == "BlastRain";
        int radius = SkillRadius(player, toxic ? 18 : blast ? 20 : 10, own, false, effect, playerStats);
        return (radius, blast ? radius * .85 : SkillRadius(player, 24, own, false, effect, playerStats));
    }

    internal static void ReleaseArrowRain(ProjectileSession session, ushort skill,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        SkillCriticalRoll critical, ushort sequence, bool mirage = false,
        Func<ProjectileOffense, ProjectileOffense>? transform = null, double podDamageFactor = 1, (int X, int Y)? from = null)
    {
        var player = session.Player;
        string id = SkillsFor(player).EffectId(gem.Effect)!;
        var use = CaptureGemProjectile(player, session, skill, gem, aim, critical, sequence);
        var shot = (ProjectileOffense)use.Payload!;
        if (mirage) shot = MirageOffense(player, shot);
        if (transform is not null) shot = transform(shot);
        use = use with { Payload = shot with { AreaVisual = null, Presentation = new(), ClientPredicted = true } };
        var own = SupportedSkill(player, skill).Stats;
        var playerStats = CharacterPlayerStats(player);
        int Own(uint stat) => own.GetValueOrDefault(stat);
        int Player(uint stat) => playerStats.GetValueOrDefault(stat);
        int Get(uint key) => Own(key) + Player(key);
        int variation = SkillArtVariation(player, gem.Gem);
        string? ArtOf(string key) => Art.Animated(key, variation) ?? Art.Animated(key, 0);
        bool toxic = id == "ToxicRain", blast = id == "BlastRain";
        int count = Math.Clamp(GatewayProjectileDefaults.Resolve(GroundArrow.Metadata, Get, 1000, true, true).Count
            + (toxic ? Get(Stat.ToxicRainNumOfAdditionalProjectiles) : 0)
            + (mirage ? Get(Stat.MirageArcherNumberOfAdditionalProjectiles) : 0), 1, 256);
        var (radius, spread) = RainArea(player, id, gem.Effect, own, playerStats);
        var enemies = session.Actors(session.Runtime.Now, session.Runtime.Now)
            .Where(a => a.Team != use.Team).Select(a => a.Position(session.Runtime.Now)).ToArray();
        var landings = RainLandings(id, ProjectileSession.Vector(aim), count, spread, enemies, AreaRng(player));
        string? up = toxic ? ArtOf("toxic_rain_arrow_up") : blast ? "Metadata/Effects/Spells/blast_rain/arrow_up.ao" : ArtOf("expanding_rain_of_arrows_up");
        string? down = toxic ? ArtOf("toxic_rain_arrow_down") : blast ? "Metadata/Effects/Spells/blast_rain/arrow_drop.ao" : ArtOf("expanding_rain_of_arrows_arrow");
        string? impact = toxic ? null : blast ? "Metadata/Effects/Spells/blast_rain/blastrainexplosion_aoe.ao" : ArtOf("expanding_rain_of_arrows_explosion");
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
        double dps = toxic && !damageContext.Suppressed
            ? ToxicPodDps(Get, Own) * (mirage ? MirageDamageFactor(own) : 1) * podDamageFactor : 0;
        // The falling arrow's art is one clip (index 0: spore_drop.ao and blast_rain's arrow_drop.ao "animate",
        // rain_of_arrows/rig.ao "rainofarrows", which has no default animation) that reaches the ground part-way in:
        // its impact particles at 0.224 s and 0.218 s, Blast Rain's "contact" at 0.333 s. So it starts that long before
        // the arrow lands.
        double fall = toxic ? .224 : blast ? .333 : .218;
        for (int i = 0; i < landings.Count; i++)
        {
            var at = landings[i];
            long land = session.Runtime.Now + ProjectileRuntime.Time(.35 + i * spacing);
            session.Runtime.Schedule(land - ProjectileRuntime.Time(fall), () =>
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
                        -Get(Stat.RainOfSporesVinesMovementSpeedPlusPctFinal), -Get(Stat.MinimumRainOfSporesMovementSpeedPlusPctFinalCap), ArtOf("toxic_rain_explode"));
                }
            });
        }
    }

    static double ToxicPodDps(Func<uint, int> get, Func<uint, int> own) => ChaosGroundDps(get, own, Stat.ToxicRainDamagePlusPct);

    // A projectile attack's chaos damage over time in an area: Toxic Rain's pods and Caustic Arrow's ground.
    // skillIncrease: the skill's own increased damage stat, if it has one.
    static double ChaosGroundDps(Func<uint, int> get, Func<uint, int> own, uint? skillIncrease)
    {
        if (DamageStats.Prohibited(DamageType.Chaos, get)) return 0;
        double increased = (double)get(Stat.DamagePlusPct) + get(Stat.ChaosDamagePlusPct) + get(Stat.AreaDamagePlusPct)
            + get(Stat.ProjectileDamagePlusPct) + get(Stat.DamageOverTimePlusPct) + get(Stat.ChaosDamageOverTimePlusPct)
            + (skillIncrease is uint key ? get(key) : 0);
        double more = 1;
        foreach (var stat in new[] { Stat.SupportVoidManipulationChaosDamagePlusPctFinal, Stat.SupportRapidDecayDamageOverTimePlusPctFinal,
            Stat.SupportEfficacyDamageOverTimePlusPctFinal, Stat.SupportPhysChaosProjectileChaosDamageOverTimePlusPctFinal,
            Stat.SupportAreaConcentrateAreaDamagePlusPctFinal, Stat.CrueltyDamageOverTimePlusPctFinal }) more *= Math.Max(0, 1 + own(stat) / 100d);
        return Math.Max(0, get(Stat.BaseChaosDamageToDealPerMinute) / 60d) * more * Math.Max(0, 1 + increased / 100)
            * Math.Max(0, 1 + ((double)get(Stat.DotMultiplierPlus) + get(Stat.ChaosDotMultiplierPlus)
                + get(Stat.DotMultiplierPlusWithBowSkills)) / 100);
    }
}

