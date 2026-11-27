using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    readonly record struct ConsecratedPathUse((int X, int Y) Landing, (int X, int Y) Impact);

    static bool IsConsecratedPath(string? effect) => effect is "ConsecratedPath" or "ConsecratedPathAltX";

    // HolyPath searches around the clamped aim. A nearby enemy leads to a stationary slam;
    // a distant enemy puts the character seven cells short of it at the slam's contact.
    static ConsecratedPathUse PlanConsecratedPath(Player player, (int X, int Y) from, (int X, int Y) aim,
        uint targetId, int radius, int travelPercent, double now)
    {
        const int baseRange = 50, warpThreshold = 15, offset = 7;
        double range = Math.Max(0, baseRange * (1 + travelPercent / 100d));
        var direction = GroundSlamWave.Direction(from, aim);
        if (direction == default) direction = (1, 0);

        (int X, int Y) Along((int X, int Y) origin, (double X, double Y) vector, double distance) =>
            (origin.X + (int)Math.Round(vector.X * distance), origin.Y + (int)Math.Round(vector.Y * distance));

        var clampedAim = Dist(from, aim) <= range ? aim : Along(from, direction, range);
        bool InBounds((int X, int Y) cell) => cell.X >= 0 && cell.Y >= 0
            && cell.X < player.Layout.GridW && cell.Y < player.Layout.GridH;
        bool Walkable((int X, int Y) cell) => InBounds(cell)
            && player.Layout.WalkableFor(cell.X, cell.Y, 0) && !player.Barrier.Contains(cell);
        bool PathOpen((int X, int Y) cell) => InBounds(cell) && !player.Barrier.Contains(cell)
            && (player.Layout.Sight is { } sight ? sight(cell.X, cell.Y) >= 1 : Walkable(cell));

        var enemies = player.Area.MonstersNear(clampedAim, radius)
            .Where(pair => pair.Value is { Hostile: true, Dead: false, Life: > 0, OwnerId: null,
                    WorldHidden: false, Asleep: false, Invulnerable: false })
            .Select(pair => (Id: pair.Key, At: pair.Value.Walk.Position(now), Size: LivingCollision.Size(pair.Value)))
            .Where(enemy => Dist(clampedAim, enemy.At) <= radius + enemy.Size
                && Dist(from, enemy.At) <= range)
            .OrderBy(enemy => enemy.Id != targetId).ThenBy(enemy => Dist(clampedAim, enemy.At)).ThenBy(enemy => enemy.Id);
        foreach (var enemy in enemies)
        {
            var enemyDirection = GroundSlamWave.Direction(from, enemy.At);
            if (!ClientPathfinder.Line(from, enemy.At).All(PathOpen)) continue;
            if (Dist(from, enemy.At) <= warpThreshold)
            {
                if (enemyDirection != default) direction = enemyDirection;
                break;
            }
            var landing = Along(enemy.At, enemyDirection, -offset);
            if (Walkable(landing)) return new(landing, enemy.At);
        }

        var impact = from;
        foreach (var cell in ClientPathfinder.Line(from, Along(from, direction, offset)).Skip(1))
        {
            if (!PathOpen(cell)) break;
            if (Walkable(cell)) impact = cell;
        }
        return new(from, impact);
    }

    static void CreateConsecratedPathGroundLocked(Player player, (int X, int Y) at,
        IReadOnlyDictionary<uint, int> skillStats, double now)
    {
        const int groundRadius = 15;
        var stats = CharacterPlayerStats(player);
        int Skill(uint key) => skillStats.GetValueOrDefault(key);
        int PlayerStat(uint key) => stats.GetValueOrDefault(key);
        double seconds = SkillDurationModifiers.Seconds(Skill(Stat.BaseSkillEffectDuration), PlayerStat, Skill);
        if (seconds <= 0) return;

        bool Has(uint key) => Skill(key) != 0 || PlayerStat(key) != 0;
        bool profane = Has(Stat.ConsecratedPathAndPurifyingFlameCreateProfaneGroundInsteadOfConsecratedGround)
            || Has(Stat.CreateProfaneGroundInsteadOfConsecratedGround);
        var kind = FlaskGrounds.Kind.ProfaneGround;
        var ground = profane
            ? new FlaskGrounds.Ground(kind, at.X, at.Y, groundRadius, now + seconds, QueueGroundArtLocked(player, kind, at, groundRadius))
            : CreateOwnConsecratedGroundLocked(player, at, groundRadius, now + seconds, stats);
        player.FlaskGrounds.Add(ground);
        PresentNearby(player);
    }
}
