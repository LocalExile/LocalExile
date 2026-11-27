using GameServer.Core.Projectiles;

public static partial class Game
{
    static bool IsCracklingLance(string? effect) =>
        effect is "CracklingLance" or "CracklingLanceAltX" or "CracklingLanceAltY";

    public sealed partial class ProjectileSession
    {
        internal void ReleaseCracklingLance(int effect, IReadOnlyDictionary<uint, int> own,
            IReadOnlyDictionary<uint, int> caster, (int X, int Y) from, (int X, int Y) aim,
            Action<int, Func<uint, (int X, int Y), bool>, double> hit, int stacks = 0, int repeat = 0)
        {
            int geometryIntensity = SkillsFor(Player).EffectId(effect) switch
            {
                "CracklingLanceAltX" => 0,
                "CracklingLanceAltY" => 3,
                _ => stacks,
            };
            var area = SkillAreaStats(Player, own, effect, caster);
            int Get(uint key) => area.GetValueOrDefault(key);
            if (repeat != 0) area[SkillArea.Increase] = Get(SkillArea.Increase) + repeat * Get(SkillArea.IncreasedPerRepeat);
            // Disintegrate (3.29.3c): width and fan angle at 0x141d27d60, terrain-clipped 15-cell segments at 0x141d28560.
            int halfWidth = Math.Min(20, SkillArea.Radius(12 + geometryIntensity * Get(Stat.DisintegrateBaseRadiusPlusPerIntensify),
                Get, spell: true));
            if (halfWidth <= 0) return;
            double angle = Math.Clamp(1 + Get(Stat.DisintegrateSecondaryBeamAnglePlusPct) / 100d, 0, 4)
                * Math.PI / 4 * Math.Max(0, 3 - geometryIntensity) / 3;
            const int step = 15, segments = 6, reach = step * segments;
            var origin = Vector(from);
            var forward = (Vector(aim) - origin).Unit;
            var paths = new (ProjectileVector Start, ProjectileVector Direction, double Length)[angle > 0 ? segments : 1];
            double centralLength = Length(origin, forward, reach);
            paths[0] = (origin, forward, centralLength);
            int count = 1;
            for (int index = 1; index < paths.Length && index * step < centralLength; index++)
            {
                var start = origin + forward * (index * step);
                var direction = forward.Rotate(index % 2 == 1 ? angle : -angle);
                paths[count++] = (start, direction, Length(start, direction, (segments - index) * step));
            }
            double more = Math.Max(0, 1 + (long)stacks * own.GetValueOrDefault(Stat.DisintegrateDamagePlusPctFinalPerIntensity) / 100d);
            hit(reach + halfWidth, Covers, more);

            double Length(ProjectileVector start, ProjectileVector direction, int length) =>
                length * (TraceTerrain(start, start + direction * length)?.Fraction ?? 1);

            bool Covers(uint _, (int X, int Y) point)
            {
                var target = Vector(point);
                for (int i = 0; i < count; i++)
                {
                    var (start, direction, length) = paths[i];
                    if (length <= 0) continue;
                    var offset = target - start;
                    double along = offset.Dot(direction);
                    if (along >= 0 && along <= length
                        && Math.Abs(offset.X * direction.Y - offset.Y * direction.X) <= halfWidth
                        && TraceTerrain(start, target) is null)
                        return true;
                }
                return false;
            }
        }
    }
}
