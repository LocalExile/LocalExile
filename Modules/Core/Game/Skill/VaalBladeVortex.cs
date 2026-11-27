using GameServer.Core.Projectiles;

public static partial class Game
{
    const string VaalBladeVortexEffect = "VaalBladeVortex";

    public sealed partial class ProjectileSession
    {
        internal void ReleaseVaalBladeVortex(SupportedGem source, IReadOnlyDictionary<uint, int> caster,
            (int X, int Y) origin, Func<bool> current, Action<(int X, int Y), int> hit)
        {
            int Own(uint stat) => source.Stats.GetValueOrDefault(stat);
            var area = SkillAreaStats(Player, source.Stats, source.Effect, caster);
            int radius = SkillArea.Radius(SkillArea.BaseRadius(VaalBladeVortexEffect, area.GetValueOrDefault),
                area.GetValueOrDefault, spell: true);
            double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration),
                caster.GetValueOrDefault, Own, DurationKinds(Player, source.Effect));
            int baseInterval = Own(Stat.BaseBladeVortexHitRateMs);
            if (radius <= 0 || duration <= 0 || baseInterval <= 0) return;
            // The ten blades are intrinsic to this effect. They never join the player's orbiting blades.
            double rate = Math.Max(.01, 1 + (Own(Stat.BladeVortexHitRatePlusPctPerBlade)
                + (double)caster.GetValueOrDefault(Stat.BladeVortexHitRatePlusPctPerBlade)) * .1);
            long spacing = Math.Max(1, ProjectileRuntime.Time(baseInterval / (1000 * rate)));
            long lifetime = ProjectileRuntime.Time(duration);
            long expires = Runtime.Now + lifetime;
            var position = Vector(origin);
            Action? remove = null;
            Action<int, int, float>? move = null;
            Effect(origin.X, origin.Y, "Metadata/Effects/Spells/dancing_daggers/sword_combined.ao", lifetime, radius,
                captureRemoval: action => remove = action, captureMovement: action => move = action);
            Advance(Runtime.Now + spacing);

            void Advance(long due)
            {
                if (due > expires) return;
                float seconds = (float)((due - Runtime.Now) / (double)ProjectileRuntime.QuantaPerSecond);
                var next = SeekVaalBladeVortexTarget(position, seconds);
                var at = ((int)Math.Round(next.X), (int)Math.Round(next.Y));
                if (at != ((int)Math.Round(position.X), (int)Math.Round(position.Y)))
                    move?.Invoke(at.Item1, at.Item2, seconds);
                Runtime.Schedule(due, () =>
                {
                    if (!current() || Player.PlayerLife == 0)
                    {
                        remove?.Invoke();
                        return;
                    }
                    position = next;
                    if (!Player.Paused) hit(at, radius);
                    Advance(Runtime.Now + spacing);
                });
            }
        }

        ProjectileVector SeekVaalBladeVortexTarget(ProjectileVector position, double seconds)
        {
            // Local pursuit tuning. The gem data supplies damage radius and hit cadence, but no pursuit values.
            const double seekRange = 55, speed = 23;
            double now = ProjectileWorldTime(Player);
            double distance = seekRange;
            uint nearest = 0;
            ProjectileVector next = position;
            foreach (var (id, monster) in Player.Area.MonstersNear((position.X, position.Y), seekRange))
            {
                if (monster is not { Hostile: true, Dead: false, Life: > 0, OwnerId: null,
                    Invulnerable: false, Asleep: false, WorldHidden: false }
                    || MonsterAsleep(Player, id) || ArenaHazards.Untargetable(Player, monster)) continue;
                var aim = Vector(monster.Walk.Position(now));
                var direction = aim - position;
                double length = direction.Length;
                if (length > distance || length == distance && nearest != 0 && id > nearest
                    || TraceTerrain(position, aim) is not null) continue;
                var step = length == 0 ? position : position + direction.Unit * Math.Min(length, speed * seconds);
                var cell = ((int)Math.Round(step.X), (int)Math.Round(step.Y));
                if (!Player.Layout.WalkableFor(cell.Item1, cell.Item2, 0) || Player.Barrier.Contains(cell)) continue;
                distance = length;
                nearest = id;
                next = step;
            }
            return next;
        }
    }
}
