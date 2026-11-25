using System.Numerics;

public static partial class Game
{
    // EnduranceChargeSlam (0xba29): the main fissure has four branch slots and one at its end.
    // A detached branch has its own action detail, dimensions, travel time and chance to divide again.
    internal static class TectonicFissures
    {
        internal const ushort Action = 0xba29;
        const double MainSeconds = .4;
        const double BranchSeconds = .5;
        const float Behind = 3;

        internal readonly record struct Path(Vector2 Origin, Vector2 Direction, float Length, float SectionLength,
            int ScanWidth, double Starts, double Seconds, int Level, uint Detail)
        {
            internal Vector2 End => Origin + Length * Direction;
            internal double Ends => Starts + Seconds;
            internal float FirstDistance => Level == 0 ? -Behind : 0;
            internal float DistanceAt(double time) => FirstDistance + (Length - FirstDistance)
                * (float)Math.Clamp((time - Starts) / Seconds, 0, 1);
        }

        internal static List<Path> Plan((int X, int Y) from, (int X, int Y) aim, ushort sequence, uint owner,
            uint repeatsLeft, int chance, bool extraBranchLevel, Func<int, float> size)
        {
            var origin = new Vector2(from.X + 1, from.Y + 1);
            var direction = new Vector2(aim.X + .5f, aim.Y + .5f) - origin;
            direction = direction == Vector2.Zero ? Vector2.UnitX : Vector2.Normalize(direction);
            float scale = size(0);
            var main = new Path(origin, direction, 28 * scale, 7 * scale, (int)(15 * scale), 0, MainSeconds, 0, 2);
            var paths = new List<Path> { main };
            if (scale <= 0) return paths;

            var initial = new AnimationVariantSelector.NativeRandom([sequence, main.Detail, owner, repeatsLeft, 0, 0, 0, 0]);
            bool left = initial.Uniform(0, 1) != 0;
            int first = initial.Uniform(0, 999) % 4;
            int second = initial.Uniform(0, 999) % 3;
            if (second >= first) second++;
            uint detail = main.Detail;
            var pending = new PriorityQueue<(Path Parent, int Slot), (double Time, uint Order)>();
            for (int slot = 0; slot <= 4; slot++)
            {
                double progress = (main.Length * slot / 4 + Behind) / (main.Length + Behind);
                pending.Enqueue((main, slot), (MainSeconds * progress, (uint)slot));
            }
            while (pending.TryDequeue(out var item, out var eventTime))
            {
                Path parent = item.Parent;
                int slot = item.Slot;
                if (parent.Level == 0)
                {
                    var random = new AnimationVariantSelector.NativeRandom([sequence, main.Detail, repeatsLeft, owner, (uint)slot * 8]);
                    if (slot == 4)
                    {
                        AddBranch(parent, 1.1f, 0, left, end: true, eventTime.Time, ref random);
                        left = !left;
                        if (random.Uniform(0, 99) <= chance)
                            AddBranch(parent, 1.1f, 0, left, end: true, eventTime.Time, ref random);
                        continue;
                    }
                    int count = (slot == first || slot == second ? 1 : 0) + (random.Uniform(0, 99) <= chance ? 1 : 0)
                        + (random.Uniform(0, 99) <= chance ? 1 : 0);
                    for (int branch = 0; branch < Math.Min(2, count); branch++)
                    {
                        AddBranch(parent, slot * .25f + .1f, -slot * 10, left, end: false, eventTime.Time, ref random);
                        left = !left;
                    }
                }
                else
                {
                    var random = new AnimationVariantSelector.NativeRandom([sequence, parent.Detail, repeatsLeft, owner, (uint)parent.Level * 0x1af]);
                    if (random.Uniform(0, 99) <= chance || random.Uniform(0, 99) <= chance)
                    {
                        float fraction = random.Range(.5f, .75f);
                        AddBranch(parent, fraction, 0, slot != 0, end: true, eventTime.Time, ref random);
                    }
                }
            }
            return paths;

            void AddBranch(Path parent, float fraction, int angleOffset, bool side, bool end,
                double spawnAt, ref AnimationVariantSelector.NativeRandom random)
            {
                double delay = .035 * (1 + random.Range(0, 1));
                int level = parent.Level + 1;
                if (level > 1) delay += .3 * fraction;
                var normal = new Vector2(-parent.Direction.Y, parent.Direction.X);
                int sideSign = side ? 1 : -1;
                var at = parent.Origin + fraction * parent.Length * parent.Direction
                    + (parent.Level == 0 ? sideSign : -sideSign) * parent.ScanWidth / 4f * normal;
                float spread = level > 1 ? 100 : end ? 35 : 20;
                int angle = level > 1 ? -20 : end ? 10 : 70;
                float turn = sideSign * ((random.Range(0, 1) - .5f) * spread + angle + angleOffset) * (MathF.PI / 180);
                var dir = Vector2.TransformNormal(parent.Direction, Matrix3x2.CreateRotation(turn));
                uint id = ++detail;
                var dimensions = new AnimationVariantSelector.NativeRandom([(uint)sequence * 2, id * 2, repeatsLeft, owner * 4]);
                float factor = size(30 + dimensions.Uniform(-15, 15) - 30 * (level - 1));
                var child = new Path(at, dir, 16 * factor, 7 * factor, (int)(6 * factor), spawnAt + delay, BranchSeconds, level, id);
                paths.Add(child);
                if (level < (extraBranchLevel ? 3 : 2)) pending.Enqueue((child, side ? 1 : 0), (child.Starts, id));
            }
        }
    }

    sealed class TectonicFissureUse
    {
        readonly Player player;
        readonly double start;
        readonly int epoch;
        readonly List<TectonicFissures.Path> paths;
        readonly double duration;
        readonly uint excluded;
        readonly uint? onlyTarget;
        readonly ISet<uint>? once;
        readonly Func<uint, (int X, int Y), bool>? filter;
        readonly HashSet<uint> attempted = [];
        double elapsed = -TickSeconds;

        internal TectonicFissureUse(Player player, List<TectonicFissures.Path> paths, double start, int epoch,
            uint excluded, uint? onlyTarget, ISet<uint>? once, Func<uint, (int X, int Y), bool>? filter)
        {
            this.player = player;
            this.paths = paths;
            this.start = start;
            this.epoch = epoch;
            this.excluded = excluded;
            this.onlyTarget = onlyTarget;
            this.once = once;
            this.filter = filter;
            duration = paths.Max(path => path.Ends);
        }

        internal async Task<bool> AppendContacts(List<(uint Id, SpawnedMonster M)> targets)
        {
            int before = targets.Count;
            while (elapsed < duration)
            {
                double previous = elapsed;
                elapsed = Math.Min(elapsed + TickSeconds, duration);
                if (!CurrentGameWork || !await WaitProjectileTime(player, start + elapsed, epoch)) return false;
                using (ProjectileMutation(player))
                {
                    if (!CurrentGameWork || !player.ProjectileActive(epoch) || player.PlayerLife == 0) return false;
                    double now = ProjectileWorldTime(player);
                    foreach (var path in paths)
                    {
                        if (path.Starts > elapsed || path.Ends < previous || path.ScanWidth <= 0) continue;
                        float beginning = path.DistanceAt(previous) - path.SectionLength / 2;
                        float end = path.DistanceAt(elapsed) + path.SectionLength / 2;
                        float halfWidth = path.ScanWidth / 2f;
                        var query = path.Origin + (beginning + end) / 2 * path.Direction;
                        int search = (int)Math.Ceiling((end - beginning) / 2 + halfWidth);
                        foreach (var (id, monster) in player.Area.MonstersNear(((int)query.X, (int)query.Y), search))
                        {
                            if (id == excluded || (onlyTarget is uint target && id != target)
                                || monster is not { Hostile: true, Dead: false, Life: > 0, OwnerId: null, WorldHidden: false }
                                || attempted.Contains(id) || once?.Contains(id) == true) continue;
                            var at = monster.Walk.Position(now);
                            var offset = new Vector2(at.X + .5f, at.Y + .5f) - path.Origin;
                            float along = Vector2.Dot(offset, path.Direction);
                            double size = LivingCollision.Size(monster);
                            float lateral = MathF.Abs(offset.X * path.Direction.Y - offset.Y * path.Direction.X);
                            if (along < beginning - size || along > end + size || lateral > halfWidth + size
                                || filter?.Invoke(id, at) == false) continue;
                            attempted.Add(id);
                            once?.Add(id);
                            targets.Add((id, monster));
                        }
                    }
                }
                if (targets.Count > before) return true;
            }
            return false;
        }
    }

}
