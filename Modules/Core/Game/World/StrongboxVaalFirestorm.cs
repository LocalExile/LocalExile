using GameServer.Core.World;
using static Program;

public static partial class MapStrongboxes
{
    const string VaalFirestormEffect = "ChestVaalFirestorm";
    const string BurningGroundArt = "Metadata/Effects/Spells/ground_effects/fire/fire1.ao";

    // Kept with the box. The actor, due times and ground object ids belong to one cast across runner changes.
    public sealed record VaalFirestorm(long Started, double Angle, string Daemon, uint[] Ground) : VesselCast(Started, Daemon);

    sealed record FirestormSkill(VesselSkill Spell, int DelayMs, int GroundMs, int Meteors, int FallRadius,
        VesselImpact Impact, double GroundDps);

    static FirestormSkill ReadFirestorm(ClientState conn)
    {
        var spell = ReadVesselSpell(conn, VaalFirestormEffect);
        int impactRadius = SkillArea.Radius(spell.Required("active_skill_base_area_of_effect_radius"), spell.Get, spell: true);
        // PoB's VaalFirestorm uses a 20-cell secondary base radius for the fall area and applies this more-area stat to it.
        double fallArea = SkillArea.Modifier(spell.Get, spell: true) * (1 + spell.Required("vaal_firestorm_gem_explosion_area_of_effect_+%_final") / 100.0);
        int fallRadius = (int)Math.Floor(20 * Math.Floor(100 * Math.Sqrt(fallArea)) / 100);
        return new(spell, spell.Required("fire_storm_fireball_delay_ms"), spell.Required("firestorm_drop_burning_ground_duration_ms"),
            spell.Required("vaal_firestorm_number_of_meteors"), fallRadius, spell.Impact(impactRadius), spell.Required("base_fire_damage_to_deal_per_minute") / 60.0);
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
            if (!await PublishVesselHits(conn, area, storm)) return;
            long first = storm.Started + skill.Spell.Timing!.Value.DurationMilliseconds!.Value * TimeSpan.TicksPerMillisecond;
            if (now.Ticks < first)
            {
                await ShowVesselCast(conn, area, box, storm, skill.Spell);
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
                        area.Zones.Add(art, new(new(Impact(i), (int)skill.Impact.Attack.Range, until / (double)TimeSpan.TicksPerSecond,
                            DamageType.Fire, skill.GroundDps, null, art), BurningGroundArt));
                }
            // A delayed tick still lands each due meteor. Each player is eligible only from their own instance entry.
            while (storm.Landed < count)
            {
                int i = storm.Landed;
                if (!await LandVesselImpact(conn, area, box, storm, skill.Spell, skill.Impact, Impact(i), first + i * interval)) return;
            }
            if (storm.Landed == storm.Ground.Length && storm.Actor != 0)
            {
                if (!await RemoveVesselActor(conn, area, box, storm)) return;
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
}
