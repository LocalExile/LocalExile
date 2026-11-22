using GameServer.Core.Projectiles;

public static partial class Game
{
    static bool IsFirestorm(ClientState conn, int effect) => SkillsFor(conn).EffectId(effect) == "Firestorm";

    public sealed partial class ProjectileSession
    {
        sealed class FirestormRun { public bool Replaced; }
        readonly List<FirestormRun> firestorms = [];
        const int FirestormSpreadRadius = 25;

        internal bool ScheduleFirestorm((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) skill,
            (int X, int Y) aim, double windup, Func<bool> current, bool triggered)
        {
            var conn = Conn;
            var source = SupportedSkill(conn, skill.Id);
            var sacrifice = new SacrificeUse(conn, skill.Id, skill.Effect, skill.Gem, triggered);
            lock (conn.Monsters)
            {
                if (!Active || !current()) return false;
                long release = triggered ? Runtime.Now
                    : Math.Max(Runtime.Now, ProjectileRuntime.Time(AreaTime(conn))) + ProjectileRuntime.Time(windup);
                Runtime.Schedule(release, () =>
                {
                    if (!Active || !current() || !sacrifice.Current || !SameTriggerSource(source, SupportedSkill(conn, skill.Id))) return;
                    var player = CharacterPlayerStats(conn);
                    int Own(string name) => GolemStat(source.Stats, name);
                    int Player(string name) => GolemStat(player, name);
                    double duration = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player, Own)
                        * Math.Max(0, 1 + Player("firestorm_duration_+%") / 100d);
                    double interval = Own("fire_storm_fireball_delay_ms") / 1000d
                        * Math.Max(.01, 1 + (Own("fire_storm_fireball_delay_+%") + Player("fire_storm_fireball_delay_+%")) / 100d);
                    int limit = Own("firestorm_max_number_of_storms");
                    if (duration <= 0 || interval <= 0 || limit <= 0) return;
                    int radius = SkillRadius(conn, source.Stats, spell: true, skill.Effect);
                    int spread = SkillRadius(conn, FirestormSpreadRadius, source.Stats, spell: true, skill.Effect, player);
                    int firstRadius = (int)Math.Round(radius * SkillArea.RadiusMultiplier(Own("firestorm_initial_impact_area_of_effect_+%_final")));
                    double firstDamage = Math.Max(0, 1 + Own("firestorm_initial_impact_damage_+%_final") / 100d);
                    var critical = CaptureSkillCriticalRoll(conn, skill.Id);
                    var run = new FirestormRun();
                    while (firestorms.Count >= limit)
                    {
                        firestorms[0].Replaced = true;
                        firestorms.RemoveAt(0);
                    }
                    firestorms.Add(run);
                    long start = Runtime.Now, end = start + ProjectileRuntime.Time(duration);
                    long spacing = Math.Max(1, ProjectileRuntime.Time(interval));
                    ScheduleImpact(start + spacing, first: true);
                    Runtime.Schedule(end, () => firestorms.Remove(run));

                    void ScheduleImpact(long due, bool first)
                    {
                        if (due > end) return;
                        Runtime.Schedule(due, () =>
                        {
                            if (run.Replaced || !Active || !sacrifice.Current) return;
                            var at = first ? aim : Impact();
                            Enqueue(() => AreaSkillHit(remote, stream, ciphers, conn, skill.Id, skill, at, at, immediate: true,
                                damageMore: first ? firstDamage : 1, radiusOverride: first ? firstRadius : radius,
                                criticalRoll: critical, sacrifice: sacrifice, triggeredCurrent: () => Active && sacrifice.Current));
                            ScheduleImpact(due + spacing, first: false);
                        });
                    }

                    (int X, int Y) Impact()
                    {
                        double angle = Random.Shared.NextDouble() * Math.Tau;
                        double distance = Math.Sqrt(Random.Shared.NextDouble()) * spread;
                        return (aim.X + (int)Math.Round(Math.Cos(angle) * distance),
                            aim.Y + (int)Math.Round(Math.Sin(angle) * distance));
                    }
                });
            }
            return true;
        }
    }
}
