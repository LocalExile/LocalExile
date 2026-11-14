/// <summary>
/// Holy Sweep's holy hammers (GrantedEffectStatSets rows 121 Sweep, 122 SweepAltX). Holy Sweep calls down
/// <c>holy_sweep_number_of_holy_bolts_to_create</c> hammers (3 at gem level 1, 5 at 20) "to land randomly on a number of
/// enemies struck"; Holy Sweep of Hammerfalls calls down "waves of holy hammers for a duration": 3 every
/// <c>holy_sweep_hammerfall_rate_ms</c> (700) for <c>base_skill_effect_duration</c> (3000). Each hammer hits every enemy
/// within <c>active_skill_base_secondary_area_of_effect_radius</c> (12 cells, grown by area of effect) of where it lands,
/// for the skill's hit with <c>holy_sweep_hammerfall_damage_+%_final</c> (-35, Hammerfalls -50) more. Holy Sweep's are
/// "Hits ... cannot be evaded" (its ActiveSkills description; Hammerfalls' says nothing). A hammer lands when its art
/// (MiscAnimated HolySweepHammerFall, falling_hammer_sweep.ao) slams: holy_slam.pet at 0.2497 s.
/// The client draws its own hammers with these rules and seeds (11-skills.md §9a), so the server's draws are its draws.
/// </summary>
public static class HolySweepHammers
{
    /// <summary>Seconds from the hammer's appearance to its slam (falling_hammer_sweep.ao's holy_slam.pet event; the
    /// client's delay for Holy Sweep's hammers, 0x141b19250, is 0.25 s too).</summary>
    public const double FallSeconds = 0.25;

    /// <param name="Count">Hammers per sweep (Holy Sweep) or per wave (Hammerfalls).</param>
    /// <param name="BaseRadius">The hammer's own base radius in cells, before area of effect.</param>
    /// <param name="More">The hammers' damage multiplier.</param>
    /// <param name="RateSeconds">Hammerfalls' base time between waves; 0 for Holy Sweep, whose hammers fall once on struck enemies.</param>
    /// <param name="DurationMs">Hammerfalls' base duration; its waves fall while it lasts.</param>
    public sealed record Settings(int Count, int BaseRadius, double More, bool CannotBeEvaded, double RateSeconds, int DurationMs)
    {
        public bool Hammerfall => RateSeconds > 0;
    }

    /// <summary>The skill's hammers, from its stats; null for a skill without them.</summary>
    public static Settings? For(Func<string, int> get)
    {
        int count = get("holy_sweep_number_of_holy_bolts_to_create");
        int radius = get("active_skill_base_secondary_area_of_effect_radius");
        if (count <= 0 || radius <= 0) return null;
        double more = Math.Max(0, 1 + get("holy_sweep_hammerfall_damage_+%_final") / 100.0);
        int rate = Math.Max(0, get("holy_sweep_hammerfall_rate_ms"));
        return new(count, radius, more, CannotBeEvaded: rate == 0, rate / 1000.0, rate > 0 ? Math.Max(0, get("base_skill_effect_duration")) : 0);
    }

    /// <summary>Hammerfalls' time between waves as the client's controller holds it (0x141b1a270, +0x350): the rate over
    /// 1 + <c>combined_main_hand_attack_speed_+%</c> / 100, in single precision.</summary>
    public static double WaveSeconds(double rateSeconds, int mainHandAttackSpeedPct) =>
        (float)rateSeconds / (mainHandAttackSpeedPct / 100f + 1f);

    /// <summary>
    /// Holy Sweep's hammers, as the client's 0x141b18a90 picks them at the swing's contact. <paramref name="ordered"/> are
    /// the enemies struck, in the client's grid order (ClientEntityGrid). With more of them than hammers it draws an index
    /// over 0 … n (n itself counting as 0) until <paramref name="count"/> different ones are flagged, from a TinyMT keyed
    /// [sequence, 0, player id, repeats left, 0, 0, 0, 0]; with as many or fewer, all. The hammers fall in the order of each enemy's
    /// angle round the player, counted from 45° before his facing (<paramref name="angle"/>), sorted as the client sorts
    /// them. <paramref name="repeatsLeft"/> is the swings the use still repeats after this one (Multistrike): the action's
    /// +0x2d0, set to the repeat count (0x1419e5460) and lowered by each repeat (0x1419e6370).
    /// </summary>
    public static List<T> Pick<T>(IReadOnlyList<T> ordered, int count, ushort sequence, uint playerId, Func<T, float> angle,
        int repeatsLeft = 0)
    {
        int n = ordered.Count;
        var flagged = new bool[n];
        if (count >= n) Array.Fill(flagged, true);
        else if (count > 0)
        {
            // Its key is 8 words, the last four zero (0x1419c73c0 reads a 32-byte buffer).
            var random = new AnimationVariantSelector.NativeRandom([sequence, 0u, playerId, (uint)repeatsLeft, 0u, 0u, 0u, 0u]);
            for (int left = count; left > 0;)
            {
                int i = random.Uniform(0, n);
                if (i >= n) i -= n;
                if (flagged[i]) continue;
                flagged[i] = true;
                left--;
            }
        }
        var entries = ordered.Select((item, i) => (Item: item, Flagged: flagged[i], Angle: angle(item))).ToList();
        ClientSort(entries, (a, b) => a.Angle < b.Angle);
        return [.. entries.Where(e => e.Flagged).Select(e => e.Item)];
    }

    /// <summary>An enemy's angle in Holy Sweep's hammer order (0x141b237c0): atan2 of its offset from the player, less the
    /// facing's (the line to the swing's target), plus 45°, in 0 … 2π.</summary>
    public static float SweepAngle((int X, int Y) player, (int X, int Y) target, (int X, int Y) enemy)
    {
        double a = Math.Atan2(enemy.Y - player.Y, enemy.X - player.X) - Math.Atan2(target.Y - player.Y, target.X - player.X) + Math.PI / 4;
        a %= 2 * Math.PI;
        return (float)(a < 0 ? a + 2 * Math.PI : a);
    }

    /// <summary>Hammerfalls' waves, in seconds after the sweep: one every <paramref name="waveSeconds"/> while the duration
    /// lasts, the first one a wave's time after the sweep (3000 / 700: 0.7, 1.4, 2.1 and 2.8 s).</summary>
    public static List<double> WaveTimes(double waveSeconds, double durationSeconds)
    {
        var times = new List<double>();
        if (waveSeconds <= 0) return times;
        for (int wave = 1; wave * waveSeconds <= durationSeconds + 1e-9 && wave <= 1000; wave++) times.Add(wave * waveSeconds);
        return times;
    }

    /// <summary>Seconds from a Hammerfalls wave to its hammers' hits: each hammer is an action of its own that hits
    /// 0.1 s after the wave sets it (+0x348, then its hit 0x141b1a610).</summary>
    public const double HammerfallHitSeconds = 0.1;

    /// <summary>The largest angle of a Hammerfalls point (DAT_1436ae464, a little under 2π).</summary>
    const float PointAngleMax = 6.283180236816406f;

    /// <summary>
    /// One Hammerfalls wave (0-based <paramref name="wave"/>), as the client's 0x141b19980 lays it out. Its candidates are
    /// <paramref name="ordered"/> (the monsters in the client's grid order) whose distance from the swing's centre is at
    /// most the area's radius plus their size. They are shuffled front to back (each place drawn over the rest) from a
    /// TinyMT keyed [sequence, repeats left + 1, player id, repeats left, wave] (the hammerfall copies its swing's repeats
    /// left, 0x1419e5750, and is detached one level below it, 0x141943240), then sorted by the hammers each took in earlier
    /// waves (fewest first, the client's sort). Each hammer, from a TinyMT keyed [the same five, hammer], goes to the first
    /// candidate not hammered yet this wave whose roll of 0-99 is under 100 halved once per hammer it took (100, 50, 25 …),
    /// or else to a point at an angle and a fraction of the radius from the centre, on walkable ground (up to 3 tries).
    /// <paramref name="taken"/> counts each monster's hammers across the waves.
    /// </summary>
    public static List<(uint? Id, (int X, int Y) At)> Wave(IReadOnlyList<(uint Id, (int X, int Y) At, int Size)> ordered,
        Dictionary<uint, int> taken, (int X, int Y) centre, int radius, int count, Func<int, int, bool> walkable,
        ushort sequence, uint playerId, int wave, int repeatsLeft = 0)
    {
        var candidates = ordered.Where(m =>
        {
            long dx = m.At.X - centre.X, dy = m.At.Y - centre.Y, reach = radius + m.Size;
            return dx * dx + dy * dy <= reach * reach;
        }).Select(m => (m.Id, m.At, Taken: taken.GetValueOrDefault(m.Id))).ToList();
        uint level = (uint)(byte)(repeatsLeft + 1), left = (uint)repeatsLeft;
        var shuffle = new AnimationVariantSelector.NativeRandom([sequence, level, playerId, left, (uint)wave]);
        for (int i = 0; i < candidates.Count; i++)
        {
            int j = i + shuffle.Uniform(0, candidates.Count - 1 - i);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        ClientSort(candidates, (a, b) => a.Taken < b.Taken);
        var free = Enumerable.Repeat(true, candidates.Count).ToArray();
        var hammers = new List<(uint? Id, (int X, int Y) At)>();
        for (int h = 0; h < count; h++)
        {
            var random = new AnimationVariantSelector.NativeRandom([sequence, level, playerId, left, (uint)wave, (uint)h]);
            bool placed = false;
            for (int i = 0; i < candidates.Count && !placed; i++)
            {
                if (!free[i]) continue;
                int chance = 100;
                for (int n = taken.GetValueOrDefault(candidates[i].Id); n > 0; n--) chance /= 2;
                if (random.Uniform(0, 99) >= chance) continue;
                free[i] = false;
                taken[candidates[i].Id] = taken.GetValueOrDefault(candidates[i].Id) + 1;
                hammers.Add((candidates[i].Id, candidates[i].At));
                placed = true;
            }
            for (int attempt = 0; attempt < 3 && !placed; attempt++)
            {
                // (0, −distance) turned by the angle (0x140df7830), each axis rounded half up (0x1406654d0).
                float angle = random.Range(0f, PointAngleMax);
                float distance = random.Range(0f, 1f) * Math.Max(0, radius);
                float x = distance * MathF.Sin(angle), y = -distance * MathF.Cos(angle);
                var point = (centre.X + (int)MathF.Floor(x + .5f), centre.Y + (int)MathF.Floor(y + .5f));
                if (!walkable(point.Item1, point.Item2)) continue;
                hammers.Add((null, point));
                placed = true;
            }
        }
        return hammers;
    }

    /// <summary>
    /// The client's std::sort (MSVC: 0x141b22410 by hammers taken, 0x141b21c10 by angle): an insertion sort for 32 or
    /// fewer, else partitions around a median guess (Tukey's ninther above 40), with a heap sort once the depth budget
    /// (the count, × ¾ per level) runs out. Equal keys keep their order only in the insertion sort.
    /// </summary>
    public static void ClientSort<T>(List<T> items, Func<T, T, bool> less) => Sort(items, 0, items.Count, items.Count, less);

    static void Sort<T>(List<T> a, int first, int last, int ideal, Func<T, T, bool> less)
    {
        while (true)
        {
            if (last - first <= 32)
            {
                Insertion(a, first, last, less);
                return;
            }
            if (ideal <= 0)
            {
                Heap(a, first, last, less);
                return;
            }
            var (mid1, mid2) = Partition(a, first, last, less);
            ideal = (ideal >> 1) + (ideal >> 2);
            if (mid1 - first < last - mid2)
            {
                Sort(a, first, mid1, ideal, less);
                first = mid2;
            }
            else
            {
                Sort(a, mid2, last, ideal, less);
                last = mid1;
            }
        }
    }

    static void Insertion<T>(List<T> a, int first, int last, Func<T, T, bool> less)
    {
        for (int next = first + 1; next < last; next++)
        {
            T value = a[next];
            if (less(value, a[first]))
            {
                for (int k = next; k > first; k--) a[k] = a[k - 1];
                a[first] = value;
                continue;
            }
            int hole = next;
            for (; less(value, a[hole - 1]); hole--) a[hole] = a[hole - 1];
            a[hole] = value;
        }
    }

    static void Swap<T>(List<T> a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);

    static void Med3<T>(List<T> a, int first, int mid, int last, Func<T, T, bool> less)
    {
        if (less(a[mid], a[first])) Swap(a, mid, first);
        if (!less(a[last], a[mid])) return;
        Swap(a, last, mid);
        if (less(a[mid], a[first])) Swap(a, mid, first);
    }

    static (int, int) Partition<T>(List<T> a, int first, int last, Func<T, T, bool> less)
    {
        int mid = first + ((last - first) >> 1);
        int lastIn = last - 1, count = lastIn - first;
        if (count > 40)
        {
            int step = (count + 1) >> 3, twoStep = step << 1;
            Med3(a, first, first + step, first + twoStep, less);
            Med3(a, mid - step, mid, mid + step, less);
            Med3(a, lastIn - twoStep, lastIn - step, lastIn, less);
            Med3(a, first + step, mid, lastIn - step, less);
        }
        else Med3(a, first, mid, lastIn, less);
        int pFirst = mid, pLast = pFirst + 1;
        while (first < pFirst && !less(a[pFirst - 1], a[pFirst]) && !less(a[pFirst], a[pFirst - 1])) pFirst--;
        while (pLast < last && !less(a[pLast], a[pFirst]) && !less(a[pFirst], a[pLast])) pLast++;
        int gFirst = pLast, gLast = pFirst;
        while (true)
        {
            for (; gFirst < last; gFirst++)
            {
                if (less(a[pFirst], a[gFirst])) continue;
                if (less(a[gFirst], a[pFirst])) break;
                if (pLast != gFirst) Swap(a, pLast, gFirst);
                pLast++;
            }
            for (; first < gLast; gLast--)
            {
                if (less(a[gLast - 1], a[pFirst])) continue;
                if (less(a[pFirst], a[gLast - 1])) break;
                if (--pFirst != gLast - 1) Swap(a, pFirst, gLast - 1);
            }
            if (gLast == first && gFirst == last) return (pFirst, pLast);
            if (gLast == first)
            {
                if (pLast != gFirst) Swap(a, pFirst, pLast);
                pLast++;
                Swap(a, pFirst, gFirst);
                pFirst++;
                gFirst++;
            }
            else if (gFirst == last)
            {
                if (--gLast != --pFirst) Swap(a, gLast, pFirst);
                Swap(a, pFirst, --pLast);
            }
            else
            {
                Swap(a, gFirst, --gLast);
                gFirst++;
            }
        }
    }

    static void Heap<T>(List<T> a, int first, int last, Func<T, T, bool> less)
    {
        int bottom = last - first;
        for (int hole = bottom >> 1; hole > 0;)
        {
            hole--;
            PopHole(a, first, hole, bottom, a[first + hole], less);
        }
        for (; last - first >= 2; last--)
        {
            T value = a[last - 1];
            a[last - 1] = a[first];
            PopHole(a, first, 0, last - 1 - first, value, less);
        }
    }

    static void PopHole<T>(List<T> a, int first, int hole, int bottom, T value, Func<T, T, bool> less)
    {
        int top = hole, index = hole, maxNonLeaf = (bottom - 1) >> 1;
        while (index < maxNonLeaf)
        {
            index = 2 * index + 2;
            if (less(a[first + index], a[first + index - 1])) index--;
            a[first + hole] = a[first + index];
            hole = index;
        }
        if (index == maxNonLeaf && bottom % 2 == 0)
        {
            a[first + hole] = a[first + bottom - 1];
            hole = bottom - 1;
        }
        for (int parent = (hole - 1) >> 1; top < hole && less(a[first + parent], value); parent = (hole - 1) >> 1)
        {
            a[first + hole] = a[first + parent];
            hole = parent;
        }
        a[first + hole] = value;
    }
}
