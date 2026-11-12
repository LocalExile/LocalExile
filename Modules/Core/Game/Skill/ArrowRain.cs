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

    internal static void ReleaseArrowRain(ProjectileSession session, ushort skill,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        SkillCriticalRoll critical, ushort sequence, bool mirage = false)
    {
        var conn = session.Conn;
        string id = SkillsFor(conn).EffectId(gem.Effect)!;
        var use = CaptureGemProjectile(conn, session, skill, gem, aim, critical, sequence, groundArrow: true);
        var shot = (ProjectileOffense)use.Payload!;
        if (mirage) shot = MirageOffense(conn, shot);
        use = use with { Payload = shot with { AreaVisual = null, Presentation = new(), ClientPredicted = true } };
        var own = SupportedSkill(conn, skill).Stats;
        var player = CharacterPlayerStats(conn);
        int Own(string key) => Stats.Key(key) is uint stat ? own.GetValueOrDefault(stat) : 0;
        int Player(string key) => Stats.Key(key) is uint stat ? player.GetValueOrDefault(stat) : 0;
        int Get(string key) => Own(key) + Player(key);
        int variation = SkillArtVariation(conn, gem.Gem);
        string? ArtOf(string key) => Art.Animated(key, variation) ?? Art.Animated(key, 0);
        bool toxic = id == "ToxicRain", blast = id == "BlastRain";
        int count = Math.Clamp(GatewayProjectileDefaults.Resolve(GroundArrow.Metadata, Get, 1000, true, true).Count
            + (toxic ? Get("toxic_rain_num_of_additional_projectiles") : 0)
            + (mirage ? Get("mirage_archer_number_of_additional_projectiles") : 0), 1, 256);
        int radius = SkillArea.Radius(toxic ? 18 : blast ? 20 : 10, Get, false);
        double spread = blast ? radius * .85 : SkillArea.Radius(24, Get, false);
        var enemies = session.Actors(session.Runtime.Now, session.Runtime.Now)
            .Where(a => a.Team != use.Team).Select(a => a.Position(session.Runtime.Now)).ToArray();
        var landings = RainLandings(id, ProjectileSession.Vector(aim), count, spread, enemies, Random.Shared);
        string? up = toxic ? ArtOf("toxic_rain_arrow_up") : blast ? "Metadata/Effects/Spells/blast_rain/arrow_up.ao" : ArtOf("expanding_rain_of_arrows_up");
        string? down = toxic ? ArtOf("toxic_rain_arrow_down") : blast ? "Metadata/Effects/Spells/blast_rain/arrow_drop.ao" : ArtOf("expanding_rain_of_arrows_arrow");
        string? impact = toxic ? null : blast ? "Metadata/Effects/Spells/blast_rain/blastrainexplosion_aoe.ao" : ArtOf("expanding_rain_of_arrows_explosion");
        session.RainArt(use.Origin, up, 1);
        double spacing = blast ? Math.Max(0, Get("blast_rain_arrow_delay_ms")) / 1000d : toxic ? 0 : .04;
        double duration = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player, Own);
        double dps = toxic ? ToxicPodDps(Get, Own) * (mirage ? MirageDamageFactor(own) : 1) : 0;
        for (int i = 0; i < landings.Count; i++)
        {
            var at = landings[i];
            long land = session.Runtime.Now + ProjectileRuntime.Time(.35 + i * spacing);
            session.Runtime.Schedule(land, () =>
            {
                if (!session.Active || conn.PlayerLife == 0) return;
                session.RainArt(at, down, 1);
                session.Runtime.Impact(use, at, new(id, radius));
                if (!toxic) session.RainArt(at, impact, 1, radius);
                else if (duration > 0)
                {
                    session.RainArt(at, ArtOf("toxic_rain_place"), duration);
                    session.PlaceToxicPod(use, at, radius, duration, dps,
                        -Get("rain_of_spores_vines_movement_speed_+%_final"), -Get("minimum_rain_of_spores_movement_speed_+%_final_cap"), ArtOf("toxic_rain_explode"));
                }
            });
        }
    }

    static double ToxicPodDps(Func<string,int> get, Func<string,int> own)
    {
        if (DamageStats.Prohibited(DamageType.Chaos, get)) return 0;
        double increased = (double)get("damage_+%") + get("chaos_damage_+%") + get("area_damage_+%")
            + get("projectile_damage_+%") + get("damage_over_time_+%") + get("chaos_damage_over_time_+%") + get("toxic_rain_damage_+%");
        double more = 1;
        foreach (var stat in new[] { "support_void_manipulation_chaos_damage_+%_final", "support_rapid_decay_damage_over_time_+%_final",
            "support_efficacy_damage_over_time_+%_final", "support_phys_chaos_projectile_chaos_damage_over_time_+%_final",
            "support_concentrated_effect_skill_area_damage_+%_final" }) more *= Math.Max(0, 1 + own(stat) / 100d);
        return Math.Max(0, get("base_chaos_damage_to_deal_per_minute") / 60d) * more * Math.Max(0, 1 + increased / 100)
            * Math.Max(0, 1 + ((double)get("dot_multiplier_+") + get("chaos_dot_multiplier_+")) / 100);
    }
}

