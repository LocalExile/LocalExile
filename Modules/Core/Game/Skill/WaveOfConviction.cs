using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    static bool IsWaveOfConviction(string? effect) => effect is "WaveOfConviction" or "WaveOfConvictionAltX"
        or "WaveOfConvictionAltY" or "WaveOfConvictionRoyale";

    public sealed partial class ProjectileSession
    {
        readonly Dictionary<uint, object> convictionWaves = [];

        internal void ReleaseConvictionWave(uint actor, int effect, IReadOnlyDictionary<uint, int> own,
            IReadOnlyDictionary<uint, int> caster, (int X, int Y) from, (int X, int Y) aim, Func<bool> current,
            Action<(int X, int Y), int, Func<uint, (int X, int Y), bool>> hit, int repeat = 0)
        {
            var area = SkillAreaStats(Player, own, effect, caster);
            int extra = repeat * own.GetValueOrDefault(SkillArea.IncreasedPerRepeat);
            if (extra != 0) area[SkillArea.Increase] = area.GetValueOrDefault(SkillArea.Increase) + extra;
            int radiusPct = SkillArea.RadiusPercent(area.GetValueOrDefault, spell: true);
            double duration = SkillDurationModifiers.Seconds(own.GetValueOrDefault(Stat.BaseSkillEffectDuration),
                caster.GetValueOrDefault, own.GetValueOrDefault, DurationKinds(Player, effect)) + .15;
            // Purge (3.29.3c 0x141b3ef70, 0x141b4022a): the duration and radius percentage give the wave's
            // bounds. Shorten unusually long waves at the client's 125-cell cap.
            double fullReach = Math.Floor(16 + duration * (6 + 43 * (1 + radiusPct / 100d)));
            int reach = (int)Math.Min(125, fullReach);
            duration *= reach / fullReach;
            bool returning = SkillsFor(Player).BehaviourVariation(effect) == 1;
            var origin = returning && caster.GetValueOrDefault(Stat.NovaSpellsCastAtTargetLocation) != 0 ? aim : from;
            var direction = GroundSlamWave.Direction(origin, aim);
            if (direction == default) direction = (1, 0);
            var token = new object();
            convictionWaves[actor] = token;
            long began = Runtime.Now;
            long outward = Math.Max(1, ProjectileRuntime.Time(returning ? duration / 2 : duration));
            long step = Math.Max(1, ProjectileRuntime.Time(FrameSeconds));
            var seen = new HashSet<uint>();
            var start = Vector(origin);
            Sweep(began, 0, inward: false);

            void Sweep(long legStart, double previous, bool inward)
            {
                if (convictionWaves.GetValueOrDefault(actor) != token) return;
                if (!Active || !current())
                {
                    convictionWaves.Remove(actor);
                    return;
                }

                double fraction = Math.Clamp((Runtime.Now - legStart) / (double)outward, 0, 1);
                double front = reach * (inward ? 1 - fraction : fraction);
                double inner = Math.Min(previous, front), outer = Math.Max(previous, front);
                double now = ProjectileWorldTime(Player);
                int scan = (int)Math.Ceiling(outer);
                HashSet<uint>? contacts = null;
                foreach (var (id, monster) in Player.Area.MonstersNear(origin, scan))
                {
                    if (seen.Contains(id) || monster is not { Hostile: true, Dead: false, Life: > 0,
                        OwnerId: null, Invulnerable: false, Asleep: false, WorldHidden: false } || MonsterAsleep(Player, id)) continue;
                    var at = monster.Walk.Position(now);
                    double distance = Dist(at, origin);
                    if (distance < inner || distance > outer
                        || !returning && !GroundSlamWave.Covers(direction, origin, at, 45, outer)
                        || TraceTerrain(start, Vector(at)) is not null) continue;
                    seen.Add(id);
                    (contacts ??= []).Add(id);
                }
                if (contacts is { } admitted)
                    hit(origin, scan, (id, _) => admitted.Contains(id));

                if (fraction < 1)
                {
                    Runtime.Schedule(Math.Min(Runtime.Now + step, legStart + outward), () => Sweep(legStart, front, inward));
                }
                else if (returning && !inward)
                {
                    // Each pass gets one contact per enemy. The return starts after the outer edge, including
                    // when a target stood on the turnaround during the outgoing pass.
                    seen.Clear();
                    long turn = Runtime.Now;
                    Runtime.Schedule(turn + Math.Min(step, outward), () => Sweep(turn, reach, inward: true));
                }
                else convictionWaves.Remove(actor);
            }
        }

        void ClearConvictionWaves() => convictionWaves.Clear();
    }
}
