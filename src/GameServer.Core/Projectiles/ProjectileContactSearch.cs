namespace GameServer.Core.Projectiles;

public sealed partial class ProjectileRuntime
{
    /// <summary>
    /// One simulation step's actors where they are at its start and at its horizon, in the order contacts are decided in
    /// (id, then generation), and a grid over them so that a flight looks only at the actors it can reach. Every flight
    /// used to be tested against every actor at each step, and a step is taken for every contact and every flight's end:
    /// Tornado Shot into 1000 monsters spent 4.5 of 10 seconds there, under the instance's lock (2026-11-13).
    /// <para>The grid leaves out only actors the plain scan would pass over without doing anything, and gives the rest in
    /// the scan's order, so the contacts and their order are the same. A test moves an actor straight from its start to
    /// its horizon position, so it stays inside the box of the two, and a contact needs it within the flight's reach of
    /// the flight's path at one instant: its box, grown by the reach, crosses the path. A box lies in the cells it is
    /// entered in, so one of those cells, grown the same, crosses the path too, and those are the cells looked into.</para>
    /// </summary>
    sealed class ContactSearch
    {
        // Added to every reach: far more than any rounding in the cell arithmetic, far less than a cell.
        const double Slack = .01;

        public ActorMotion[] Motion = [];
        public int Count { get; private set; }
        /// <summary>Whether <see cref="Near"/> can be asked; otherwise every actor is scanned.</summary>
        public bool Indexed { get; private set; }
        /// <summary>The largest <see cref="ProjectileActor.Radius"/> and <see cref="ProjectileActor.Size"/> here.</summary>
        public double MaxRadius { get; private set; }
        public int MaxSize { get; private set; }
        /// <summary>Scratch for the keys a flight may forget (ProjectileRuntime.ForgetLeft).</summary>
        public readonly List<ProjectileActorKey> Keys = [];

        double left, top, cell;
        int columns, rows, query;
        int[] starts = [], cursor = [], items = [], seen = [], found = [];

        static bool Before(ProjectileActorKey a, ProjectileActorKey b) =>
            a.Id != b.Id ? a.Id < b.Id : a.Generation < b.Generation;

        public void Take(IReadOnlyList<ProjectileActor> actors, long now, long horizon, int fewest)
        {
            int before = Count;
            Count = actors.Count;
            if (Motion.Length < Count) Motion = new ActorMotion[Math.Max(Count, Motion.Length * 2)];
            bool ordered = true;
            for (int i = 1; i < Count && ordered; i++) ordered = !Before(actors[i].Key, actors[i - 1].Key);
            if (ordered)
                for (int i = 0; i < Count; i++)
                {
                    var actor = actors[i];
                    Motion[i] = new(actor, actor.Position(now), actor.Position(horizon));
                }
            else
            {
                int i = 0;
                foreach (var actor in actors.OrderBy(a => a.Key.Id).ThenBy(a => a.Key.Generation))
                    Motion[i++] = new(actor, actor.Position(now), actor.Position(horizon));
            }
            if (before > Count) Array.Clear(Motion, Count, before - Count);
            Indexed = false;
            if (Count >= fewest) Index();
        }

        /// <summary>Lets go of the actors: the search is kept between advances, the actors are the host's.</summary>
        public void Release()
        {
            Array.Clear(Motion, 0, Count);
            Count = 0;
            Indexed = false;
        }

        void Index()
        {
            double l = double.PositiveInfinity, t = double.PositiveInfinity, r = double.NegativeInfinity, b = double.NegativeInfinity;
            double radius = 0;
            int size = int.MinValue;
            for (int i = 0; i < Count; i++)
            {
                ref readonly var m = ref Motion[i];
                l = Math.Min(l, Math.Min(m.From.X, m.To.X)); r = Math.Max(r, Math.Max(m.From.X, m.To.X));
                t = Math.Min(t, Math.Min(m.From.Y, m.To.Y)); b = Math.Max(b, Math.Max(m.From.Y, m.To.Y));
                radius = Math.Max(radius, m.Actor.Radius);
                size = Math.Max(size, m.Actor.Size);
            }
            // A position that is not a number has no cell: the plain scan decides, as it always did.
            if (!double.IsFinite(l) || !double.IsFinite(t) || !double.IsFinite(r) || !double.IsFinite(b) || !double.IsFinite(radius)) return;
            cell = Math.Max(4, Math.Max(r - l, b - t) / 48);
            left = l; top = t;
            columns = (int)((r - l) / cell) + 1;
            rows = (int)((b - t) / cell) + 1;
            int cells = columns * rows;
            if (starts.Length < cells + 1) { starts = new int[cells + 1]; cursor = new int[cells + 1]; }
            Array.Clear(starts, 0, cells + 1);
            for (int i = 0; i < Count; i++)
            {
                var (c0, c1, r0, r1) = Cells(in Motion[i]);
                for (int row = r0; row <= r1; row++)
                    for (int column = c0; column <= c1; column++) starts[row * columns + column + 1]++;
            }
            for (int i = 0; i < cells; i++) starts[i + 1] += starts[i];
            int total = starts[cells];
            // Actors that cross most of the area in one step (a teleport) would fill every cell: scan them plainly.
            if (total > 16 * Count + 4096) return;
            if (items.Length < total) items = new int[Math.Max(total, items.Length * 2)];
            Array.Copy(starts, cursor, cells);
            for (int i = 0; i < Count; i++)
            {
                var (c0, c1, r0, r1) = Cells(in Motion[i]);
                for (int row = r0; row <= r1; row++)
                    for (int column = c0; column <= c1; column++) items[cursor[row * columns + column]++] = i;
            }
            if (seen.Length < Count) { seen = new int[Math.Max(Count, seen.Length * 2)]; found = new int[seen.Length]; }
            Array.Clear(seen, 0, Count);
            query = 0;
            (MaxRadius, MaxSize) = (radius, size);
            Indexed = true;
        }

        int Column(double x) => Math.Clamp((int)((x - left) / cell), 0, columns - 1);
        int Row(double y) => Math.Clamp((int)((y - top) / cell), 0, rows - 1);

        (int C0, int C1, int R0, int R1) Cells(in ActorMotion m) =>
            (Column(Math.Min(m.From.X, m.To.X)), Column(Math.Max(m.From.X, m.To.X)),
             Row(Math.Min(m.From.Y, m.To.Y)), Row(Math.Max(m.From.Y, m.To.Y)));

        /// <summary>The indices in <see cref="Motion"/>, ascending, of every actor that can come within
        /// <paramref name="reach"/> (per axis) of the straight path <paramref name="from"/> to <paramref name="to"/> during
        /// this step, and possibly more. False when the path is not one the grid can place: scan every actor.</summary>
        public bool Near(ProjectileVector from, ProjectileVector to, double reach, out ReadOnlySpan<int> near)
        {
            near = default;
            reach += Slack;
            double x0 = Math.Min(from.X, to.X) - reach, x1 = Math.Max(from.X, to.X) + reach;
            double y0 = Math.Min(from.Y, to.Y) - reach, y1 = Math.Max(from.Y, to.Y) + reach;
            if (!Indexed || !double.IsFinite(x0) || !double.IsFinite(x1) || !double.IsFinite(y0) || !double.IsFinite(y1)) return false;
            if (++query == int.MaxValue) { Array.Clear(seen, 0, Count); query = 1; }
            int c0 = Column(x0), c1 = Column(x1), r0 = Row(y0), r1 = Row(y1), n = 0;
            for (int row = r0; row <= r1; row++)
                for (int column = c0; column <= c1; column++)
                {
                    int at = row * columns + column;
                    if (starts[at] == starts[at + 1]) continue;
                    double cx = left + column * cell, cy = top + row * cell;
                    if (!Crosses(from, to, cx - reach, cy - reach, cx + cell + reach, cy + cell + reach)) continue;
                    for (int k = starts[at]; k < starts[at + 1]; k++)
                    {
                        int i = items[k];
                        if (seen[i] == query) continue;
                        seen[i] = query;
                        found[n++] = i;
                    }
                }
            Array.Sort(found, 0, n);
            near = found.AsSpan(0, n);
            return true;
        }

        /// <summary>Whether the path from <paramref name="a"/> to <paramref name="b"/> touches the rectangle.</summary>
        static bool Crosses(ProjectileVector a, ProjectileVector b, double x0, double y0, double x1, double y1)
        {
            double enter = 0, exit = 1;
            return Slab(a.X, b.X, x0, x1) && Slab(a.Y, b.Y, y0, y1);

            bool Slab(double p, double q, double low, double high)
            {
                double d = q - p;
                if (d == 0) return p >= low && p <= high;
                double t0 = (low - p) / d, t1 = (high - p) / d;
                if (t0 > t1) (t0, t1) = (t1, t0);
                enter = Math.Max(enter, t0); exit = Math.Min(exit, t1);
                return enter <= exit;
            }
        }

        /// <summary>Where the actors with <paramref name="key"/> are in <see cref="Motion"/> (a key is one actor's).</summary>
        public (int First, int Last) Find(ProjectileActorKey key)
        {
            int low = 0, high = Count - 1;
            while (low <= high)
            {
                int middle = (low + high) >>> 1;
                var other = Motion[middle].Actor.Key;
                if (other == key)
                {
                    int first = middle, last = middle;
                    while (first > 0 && Motion[first - 1].Actor.Key == key) first--;
                    while (last < Count - 1 && Motion[last + 1].Actor.Key == key) last++;
                    return (first, last);
                }
                if (Before(other, key)) low = middle + 1; else high = middle - 1;
            }
            return (0, -1);
        }
    }

    // Kept between advances so a step allocates nothing for its actors. An advance that starts inside another (a hit's
    // callback that advances the runtime) takes a search of its own.
    ContactSearch? spareSearch;

    /// <summary>The number of actors from which a step looks for contacts through the grid (ContactSearch); with fewer
    /// the plain scan of every actor is as cheap. Both find the same contacts in the same order.</summary>
    public int IndexActorsFrom { get; init; } = 48;
}
