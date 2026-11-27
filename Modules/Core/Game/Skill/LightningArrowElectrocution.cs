using GameServer.Core.Projectiles;

public static partial class Game
{
    internal sealed record ElectrocutionSpec(int Limit, int Strikes, long Interval);

    static ElectrocutionSpec? CaptureElectrocution(Func<uint, int> get)
    {
        int limit = Math.Clamp(get(Stat.LightningArrowStackLimit), 0, 256);
        int strikes = Math.Clamp(get(Stat.LightningArrowAltAdditionalStrikes), 0, 256);
        int intervalMs = get(Stat.LightningArrowAltStrikeFrequencyMs);
        return limit > 0 && strikes > 0 && intervalMs > 0
            ? new(limit, strikes, ProjectileRuntime.Time(intervalMs / 1000d)) : null;
    }

    sealed class ElectrocutionArrow(SpawnedMonster target)
    {
        public readonly SpawnedMonster Target = target;
        public volatile bool Retired;
    }

    static readonly RuleTable<SpawnedMonster, List<ElectrocutionArrow>> ElectrocutionByEnemy = new();

    public sealed partial class ProjectileSession
    {
        HashSet<ElectrocutionArrow>? ownedElectrocutionArrows;

        void ClearElectrocutionArrows()
        {
            if (ownedElectrocutionArrows is not { Count: > 0 } arrows) return;
            foreach (var arrow in arrows) ReleaseElectrocutionArrow(arrow);
            arrows.Clear();
        }

        static void ReleaseElectrocutionArrow(ElectrocutionArrow arrow)
        {
            arrow.Retired = true;
            if (ElectrocutionByEnemy.TryGetValue(arrow.Target, out var arrows))
                lock (arrows) arrows.Remove(arrow);
        }

        void ElectrocutionAtEnd(ProjectileEvent impact)
        {
            var flight = impact.Projectile;
            if (flight.Use.Payload is not ProjectileOffense { Electrocution: { } spec } shot
                || impact.HitTarget is not { } key || Actor(key, impact.At) is null
                || !Player.Area.Monsters.TryGetValue(key.Id, out var target) || target.Invulnerable) return;

            var arrow = new ElectrocutionArrow(target);
            var arrows = ElectrocutionByEnemy.GetOrCreateValue(target);
            lock (arrows)
            {
                while (arrows.Count >= spec.Limit)
                {
                    arrows[0].Retired = true;
                    arrows.RemoveAt(0);
                }
                arrows.Add(arrow);
            }
            (ownedElectrocutionArrows ??= []).Add(arrow);
            int remaining = spec.Strikes;
            Runtime.Schedule(impact.At + spec.Interval, Strike);

            void Strike()
            {
                if (!arrow.Retired && Active && Player.PlayerLife > 0 && shot.Valid?.Invoke() != false
                    && Actor(key, Runtime.Now) is { } enemy && enemy.Team != flight.Use.Team)
                {
                    Runtime.RepeatContact(flight, enemy, "electrocution");
                    if (--remaining > 0 && !target.Dead && target.Life > 0)
                    {
                        Runtime.Schedule(Runtime.Now + spec.Interval, Strike);
                        return;
                    }
                }
                ReleaseElectrocutionArrow(arrow);
                ownedElectrocutionArrows?.Remove(arrow);
            }
        }
    }
}
