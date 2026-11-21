using GameServer.Core.Projectiles;

public static partial class Game
{
    internal sealed record ParallelFiringPoints(int PerSide, int XDistance, int YDistance, bool FirstPointPierces,
        int SecondPointForks, bool ThirdPointReturns, int FourthPointChains)
    {
        public static ParallelFiringPoints? From(Func<string, int> get)
        {
            int points = get("support_parallel_projectile_number_of_points_per_side");
            ArgumentOutOfRangeException.ThrowIfNegative(points);
            if (points == 0) return null;
            bool arrows = get("skill_can_fire_arrows") != 0 && get("main_hand_weapon_type") == WeaponTypeStats.Bow;
            return new(points, get("virtual_additional_projectiles_fire_parallel_x_dist"),
                get("additional_projectiles_fire_parallel_y_dist"), arrows && get("arrows_from_first_firing_point_always_pierce") != 0,
                arrows && points >= 2 ? get("arrows_from_second_firing_point_fork") : 0,
                arrows && points >= 3 && get("arrows_from_third_firing_point_return") != 0,
                arrows && points >= 4 ? get("arrows_from_fourth_firing_point_additional_chains") : 0);
        }

        // The simultaneous layout (3.29.3c, 0x141bbb460) visits the centre, then +k and -k.
        // Each point translates both ends of the ray before running its own fan, nova or random group.
        public ProjectileLaunch[] Groups(ProjectileUse use, ProjectileNativeVolley fan, bool random)
        {
            int count = use.Profile.Count;
            var aim = use.Aim - use.Origin;
            if (count == 1)
                return [new(default, random ? RandomVolleyAngles(use.Sequence, 1, use.Emitter.Id, aim, use.RepeatsLeft)[0] : 0)];
            var launches = new ProjectileLaunch[count];
            var forward = aim.Unit;
            var side = new ProjectileVector(forward.Y, -forward.X);
            int next = 0;
            foreach (var (slot, size) in Slots(count))
            {
                var offset = side * (slot * (double)XDistance / 10.869565)
                    + forward * (Math.Abs(slot) * (double)YDistance / 10.869565);
                var launch = Launch(slot) with { Offset = offset };
                var rng = random ? GroupRandom(use, 0, slot) : default;
                for (int i = 0; i < size; i++)
                    launches[next++] = launch with { Angle = random ? rng.Range(0, 6.2831855f) : fan.Angle(i, size, aim.Length) };
            }
            return launches;
        }

        public IEnumerable<(int Slot, int Size)> Slots(int count)
        {
            int sides = Math.Min(PerSide, count / 2);
            int perPoint = Math.DivRem(count, 2 * sides + 1, out int remaining);
            yield return (0, perPoint + remaining % 2);
            for (int k = 1; k <= sides; k++)
            {
                int size = perPoint + (k <= remaining / 2 ? 1 : 0);
                yield return (k, size);
                yield return (-k, size);
            }
        }

        // Blocked members retain their layout slot for placement. Only admitted projectiles get a flight ordinal.
        public ProjectileLaunch Launch(int slot) => new(default, 0, CheckSourcePath: slot != 0)
        {
            PiercesAllTargets = Math.Abs(slot) == 1 && FirstPointPierces,
            AdditionalForks = Math.Abs(slot) == 2 ? SecondPointForks : 0,
            GrantsReturn = Math.Abs(slot) == 3 && ThirdPointReturns,
            AdditionalChains = Math.Abs(slot) == 4 ? FourthPointChains : 0
        };

        public static AnimationVariantSelector.NativeRandom GroupRandom(ProjectileUse use, uint derived, int slot)
        {
            int visit = 2 * Math.Abs(slot) - (slot > 0 ? 1 : 0);
            return new([use.Sequence, derived, use.RepeatsLeft, (uint)(529 * visit), use.Emitter.Id, 0, unchecked((uint)slot)]);
        }

        // The Barrage firing actor rounds one perpendicular step to whole cells before applying the slot.
        public ProjectileVector GridStep(ProjectileVector aim)
        {
            float length = (float)aim.Length;
            float scale = length > 0 ? XDistance * .092f / length : 0;
            return new(MathF.Floor((float)aim.Y * scale + .5f), MathF.Floor(-(float)aim.X * scale + .5f));
        }

        // 0x141bb3c90 recentres the last, shorter pass. An even pass skips zero. The separate
        // simultaneous endpoint volleys, when granted, sit outside the sweep.
        public int SweepSlot(int shot, int count, bool extraEndVolleys)
        {
            if (extraEndVolleys)
            {
                if (shot == 0 || shot == count - 1) return 0;
                shot--;
                count -= 2;
            }
            int width = 2 * Math.Min(PerSide, count / 2) + 1;
            int first = shot / width * width;
            int size = Math.Min(width, count - first);
            int slot = shot - first - size / 2;
            return size % 2 == 0 && slot >= 0 ? slot + 1 : slot;
        }
    }
}
