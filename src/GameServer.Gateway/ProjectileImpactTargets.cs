/// <summary>The living hostile monsters whose bodies overlap a projectile's explosion when it lands. Body radius
/// uses the same StopRange approximation as the server's Righteous Fire area check.</summary>
static class ProjectileImpactTargets
{
    public static IReadOnlyList<(uint Id, SpawnedMonster Monster)> Explosion(
        IReadOnlyDictionary<uint, SpawnedMonster> monsters, (int X, int Y) impact, int radius, double at)
    {
        var result = new List<(uint, SpawnedMonster)>();
        foreach (var (id, monster) in monsters)
        {
            if (monster.Dead || monster.Life == 0 || !monster.Hostile) continue;
            var (x, y) = monster.Walk.Position(at);
            double dx = x - (double)impact.X, dy = y - (double)impact.Y;
            double reach = Math.Max(0, radius) + Math.Max(0, monster.StopRange);
            if (dx * dx + dy * dy <= reach * reach) result.Add((id, monster));
        }
        return result;
    }
}
