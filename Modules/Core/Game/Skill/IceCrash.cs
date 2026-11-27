using GameServer.Core.World;

public static partial class Game
{
    // The IceCrash_ contact fixes the impact cell. Its update advances to the next ring every 0.4 seconds.
    sealed class IceCrashUse
    {
        const double Interval = .4;
        internal const ushort Action = 0x53d9;

        readonly Player player;
        readonly (int X, int Y) centre;
        readonly int epoch;
        readonly double start;
        readonly (int Radius, double More)[] stages;
        readonly uint excluded;
        readonly uint? onlyTarget;
        readonly ISet<uint>? once;
        readonly Func<uint, (int X, int Y), bool>? filter;
        readonly HashSet<uint> attempted = [];
        int next;

        internal double DamageMore { get; private set; } = 1;
        internal int Extent => stages[^1].Radius;

        internal static bool Cadence(string? effect) => effect == "IceCrashAltX";

        internal static bool Handles(string? effect) =>
            effect is "IceCrash" or "IceCrashAltX" or "IceCrashAltY" or "IceCrashRoyale";

        internal static int BaseRadius(int phase, IReadOnlyDictionary<uint, int> stats)
        {
            var (key, fallback) = phase switch
            {
                1 => (Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius, 21),
                2 => (Stat.ActiveSkillBaseTertiaryAreaOfEffectRadius, 31),
                _ => (Stat.ActiveSkillBaseAreaOfEffectRadius, 11)
            };
            return stats.GetValueOrDefault(key) is > 0 and var radius ? radius : fallback;
        }

        internal static (int X, int Y) Impact(Player player, (int X, int Y) from, (int X, int Y) aim, int reach)
        {
            if (from == aim) return from;
            var dir = GroundSlamWave.Direction(from, aim);
            var intended = (from.X + (int)Math.Truncate(dir.X * reach), from.Y + (int)Math.Truncate(dir.Y * reach));
            int Floor(int x, int y) => x >= 0 && y >= 0 && x < player.Layout.GridW && y < player.Layout.GridH
                && player.Layout.WalkableFor(x, y, 0) && !player.Barrier.Contains((x, y)) ? 1 : 0;
            return ClientPathfinder.LineBelow(from, intended, Floor, 1, out var last) ? last : intended;
        }

        internal IceCrashUse(Player player, (int X, int Y) centre, int epoch, AttackHand hand, bool cadence,
            IReadOnlyDictionary<uint, int> area, IReadOnlyDictionary<uint, int> skill, double size, int? radiusOverride,
            uint excluded, uint? onlyTarget, ISet<uint>? once, Func<uint, (int X, int Y), bool>? filter)
        {
            this.player = player;
            this.centre = centre;
            this.epoch = epoch;
            this.excluded = excluded;
            this.onlyTarget = onlyTarget;
            this.once = once;
            this.filter = filter;
            start = AreaTime(player);

            int Get(uint key) => area.GetValueOrDefault(key);
            int Radius(int phase) => (int)Math.Round((radiusOverride ?? SkillArea.Radius(BaseRadius(phase, area), Get, spell: false)) * size);
            double More(uint key) => Math.Max(0, 1 + skill.GetValueOrDefault(key) / 100d);
            stages = cadence
                ? [(Radius(hand == AttackHand.Off ? 1 : 0), 1)]
                : [(Radius(0), More(Stat.IceCrashFirstStageDamagePlusPctFinal)),
                    (Radius(1), More(Stat.IceCrashSecondHitDamagePlusPctFinal)),
                    (Radius(2), More(Stat.IceCrashThirdHitDamagePlusPctFinal))];
        }

        internal async Task<bool> AppendContacts(List<(uint Id, SpawnedMonster M)> targets)
        {
            int before = targets.Count;
            while (next < stages.Length)
            {
                if (!CurrentGameWork || !await WaitProjectileTime(player, start + next * Interval, epoch)) return false;
                var (radius, more) = stages[next++];
                if (radius <= 0) continue;
                using (ProjectileMutation(player))
                {
                    if (!CurrentGameWork || !player.ProjectileActive(epoch) || player.PlayerLife == 0) return false;
                    double now = ProjectileWorldTime(player);
                    foreach (var (id, monster) in player.Area.MonstersNear(centre, radius))
                    {
                        if (id == excluded || (onlyTarget is uint target && id != target)
                            || monster is not { Hostile: true, Dead: false, Life: > 0, OwnerId: null, WorldHidden: false }
                            || attempted.Contains(id) || once?.Contains(id) == true) continue;
                        var at = monster.Walk.Position(now);
                        if (Dist(centre, at) > radius + LivingCollision.Size(monster) || filter?.Invoke(id, at) == false) continue;
                        attempted.Add(id);
                        once?.Add(id);
                        targets.Add((id, monster));
                    }
                }
                if (targets.Count > before)
                {
                    DamageMore = more;
                    return true;
                }
            }
            return false;
        }
    }
}
