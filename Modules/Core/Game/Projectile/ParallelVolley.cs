using GameServer.Core.Projectiles;

public static partial class Game
{
    sealed record ParallelFiringPoints(int PerSide, int XDistance, int YDistance, bool FirstPointPierces, bool ThirdPointReturns)
    {
        public static ParallelFiringPoints? From(Func<string, int> get)
        {
            int points = get("support_parallel_projectile_number_of_points_per_side");
            ArgumentOutOfRangeException.ThrowIfNegative(points);
            if (points == 0) return null;
            bool arrows = get("skill_can_fire_arrows") != 0 && get("main_hand_weapon_type") == WeaponTypeStats.Bow;
            return new(points, get("virtual_additional_projectiles_fire_parallel_x_dist"),
                get("additional_projectiles_fire_parallel_y_dist"), arrows && get("arrows_from_first_firing_point_always_pierce") != 0,
                arrows && points >= 3 && get("arrows_from_third_firing_point_return") != 0);
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
            int sides = Math.Min(PerSide, count / 2);
            int positions = 2 * sides + 1;
            int perPoint = Math.DivRem(count, positions, out int remaining);
            int next = 0;
            Place(0, perPoint + remaining % 2, 0);
            for (int k = 1; k <= sides; k++)
            {
                int groupSize = perPoint + (k <= remaining / 2 ? 1 : 0);
                Place(k, groupSize, 2 * k - 1);
                Place(-k, groupSize, 2 * k);
            }
            return launches;

            void Place(int slot, int size, int visit)
            {
                var offset = side * (slot * (double)XDistance / 10.869565)
                    + forward * (Math.Abs(slot) * (double)YDistance / 10.869565);
                // The shared emitter copies the absolute layout slot to every admitted member (3.29.3c).
                int point = Math.Abs(slot);
                bool pierces = point == 1 && FirstPointPierces;
                bool returns = point == 3 && ThirdPointReturns;
                // Each side visit advances the fourth seed word by 529, even for an empty group.
                var rng = random ? new AnimationVariantSelector.NativeRandom([use.Sequence, 0, use.RepeatsLeft,
                    (uint)(529 * visit), use.Emitter.Id, 0, unchecked((uint)slot)]) : default;
                for (int i = 0; i < size; i++)
                    launches[next++] = new(offset, random ? rng.Range(0, 6.2831855f) : fan.Angle(i, size, aim.Length),
                        CheckSourcePath: slot != 0) { PiercesAllTargets = pierces, GrantsReturn = returns };
            }
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
