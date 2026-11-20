using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

public static partial class MapStrongboxes
{
    const string VaalFirestormEffect = "ChestVaalFirestorm";
    const string BurningGroundArt = "Metadata/Effects/Spells/ground_effects/fire/fire1.ao";

    // Kept with the box. The actor, due times and ground object ids belong to one cast across runner changes.
    public sealed record VaalFirestorm(long Started, double Angle, string Daemon, uint[] Ground)
    {
        public int Landed { get; set; }
        public uint Actor { get; set; }
    }

    sealed record FirestormSkill(int Effect, ushort Action, int ActorLevel, Dictionary<uint, int> Stats, int CastMs, int DelayMs,
        int GroundMs, int Meteors, int FallRadius, int ImpactRadius, DamagePacket Hit, double GroundDps);

    static FirestormSkill ReadFirestorm(ClientState conn)
    {
        var catalog = SkillsFor(conn);
        int effect = catalog.EffectRow(VaalFirestormEffect), level = AreaLevel(conn);
        var native = GolemData.Skill(catalog, effect, level);
        int Required(string name) => native.Stats[Stats.Key(name)!.Value];
        int Get(string name) => Stats.Key(name) is uint key ? native.Stats.GetValueOrDefault(key) : 0;
        int impactRadius = SkillArea.Radius(Required("active_skill_base_area_of_effect_radius"), Get, spell: true);
        // PoB's VaalFirestorm uses a 20-cell secondary base radius for the fall area and applies this more-area stat to it.
        double fallArea = SkillArea.Modifier(Get, spell: true) * (1 + Required("vaal_firestorm_gem_explosion_area_of_effect_+%_final") / 100.0);
        int fallRadius = (int)Math.Floor(20 * Math.Floor(100 * Math.Sqrt(fallArea)) / 100);
        return new(effect, (ushort)(catalog.ActionHash(effect)!.Value & 0x3fff), level, native.Stats, catalog.CastMs(effect), Required("fire_storm_fireball_delay_ms"),
            Required("firestorm_drop_burning_ground_duration_ms"), Required("vaal_firestorm_number_of_meteors"),
            fallRadius, impactRadius, Packet(native.Stats, "spell").Scale(native.Multiplier), Required("base_fire_damage_to_deal_per_minute") / 60.0);
    }

    static VaalFirestorm? PrepareFirestorm(ClientState conn, Area area, StrongboxCatalog.Definition type, Box box)
    {
        foreach (var (_, daemon, skills) in Daemons(conn, type, box))
        {
            if (!skills.Contains(VaalFirestormEffect)) continue;
            var skill = area.FirestormSkill ??= ReadFirestorm(conn);
            var random = new Random(unchecked((int)conn.Seed ^ box.Index * 9973 ^ box.Cycle * 31));
            var ground = new uint[skill.Meteors];
            for (int i = 0; i < ground.Length; i++) ground[i] = conn.TakeObjectId();
            var storm = new VaalFirestorm(conn.Clock.Elapsed.Ticks, random.NextDouble() * Math.Tau, daemon, ground);
            box.Firestorms.Add(storm);
            return storm;
        }
        return null;
    }

    static Task<uint> ShowFirestorm(ClientState conn, Area area, Box box, VaalFirestorm storm)
    {
        var skill = area.FirestormSkill ??= ReadFirestorm(conn);
        return Show(conn, storm.Daemon, skill.Effect, skill.ActorLevel, skill.Stats, (box.X, box.Y),
            id => { storm.Actor = id; Keep(conn, area); });
    }

    // ActiveSkills describes an inward spiral with the last impact at the target. The box is the target. One revolution
    // with linearly decreasing radius and the first landing at cast completion are server choices.
    internal static (int X, int Y) FirestormImpact((int X, int Y) target, int index, int meteors, int spread, double angle)
    {
        if (index == meteors - 1) return target;
        double progress = index / (double)(meteors - 1), radius = spread * (1 - progress);
        double phase = angle + Math.Tau * progress;
        return (target.X + (int)Math.Round(radius * Math.Cos(phase)), target.Y + (int)Math.Round(radius * Math.Sin(phase)));
    }

    static async Task TickFirestorms(ClientState conn, Area area, Box box, TimeSpan now)
    {
        var skill = area.FirestormSkill ??= ReadFirestorm(conn);
        long interval = skill.DelayMs * TimeSpan.TicksPerMillisecond;
        long duration = skill.GroundMs * TimeSpan.TicksPerMillisecond;
        foreach (var storm in box.Firestorms.ToArray())
        {
            long first = storm.Started + skill.CastMs * TimeSpan.TicksPerMillisecond;
            if (now.Ticks < first)
            {
                if (storm.Actor == 0) await ShowFirestorm(conn, area, box, storm);
                else await PlayDaemon(conn, storm.Actor, skill.Action, (box.X, box.Y), storm.Daemon, shared: true);
                continue;
            }
            int count = (int)Math.Min(storm.Ground.Length, 1 + (now.Ticks - first) / interval);
            (int X, int Y) Impact(int i) => FirestormImpact((box.X, box.Y), i, storm.Ground.Length, skill.FallRadius, storm.Angle);
            // Restore only the ground still burning after a runner change or rules reload. Its expiry belongs to the cast.
            lock (area.Zones)
                for (int i = count - 1; i >= 0; i--)
                {
                    long until = first + i * interval + duration;
                    if (until <= now.Ticks) break;
                    uint art = storm.Ground[i];
                    if (!area.Zones.ContainsKey(art))
                        area.Zones.Add(art, new(new(Impact(i), skill.ImpactRadius, until / (double)TimeSpan.TicksPerSecond,
                            DamageType.Fire, skill.GroundDps, null, art), BurningGroundArt));
                }
            // A delayed tick still lands each due meteor. Each player is eligible only from their own instance entry.
            while (storm.Landed < count)
            {
                int i = storm.Landed++;
                Keep(conn, area);
                await HitPlayers(conn, Impact(i), skill.ImpactRadius, DamageType.Fire, skill.Hit, first + i * interval);
            }
            if (storm.Landed == storm.Ground.Length && storm.Actor != 0)
            {
                await RemoveFirestormActor(conn, storm.Actor);
                storm.Actor = 0;
                Keep(conn, area);
            }
            if (now.Ticks >= first + (storm.Ground.Length - 1) * interval + duration)
            {
                box.Firestorms.Remove(storm);
                Keep(conn, area);
            }
        }
    }

    static async Task RemoveFirestormActor(ClientState conn, uint actor)
    {
        var viewers = Members(conn).ToList();
        lock (conn.Monsters)
        {
            if (conn.Monsters.Remove(actor, out var monster))
            {
                monster.Dead = true; monster.Life = 0; monster.ActionGeneration++;
            }
            conn.Instance.World.MonsterOrigins.TryRemove(actor, out _);
            foreach (var viewer in viewers)
                if (Presented.TryGetValue(viewer, out var view)) view.Casts.Remove(actor);
            viewers.RemoveAll(viewer => !viewer.MonsterViews.Remove(actor));
            foreach (var viewer in viewers) viewer.AsleepMonsters.Remove(actor);
        }
        await Task.WhenAll(viewers.Select(viewer => SendWorldRemoval(viewer.Wire.Remote, viewer.Wire.Stream, viewer.Wire.Ciphers,
            viewer, actor, "strongbox Vaal Firestorm ends")));
    }
}
