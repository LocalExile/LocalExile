using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Delve;

/// <summary>Account/league progress; coordinates describe the chart, not a particular area's terrain cells.</summary>
sealed record DelveMine(int Version, uint Seed, DelveMine.Point Current, IReadOnlyList<DelveMine.Point> Opened, DelveMine.Plan? Pending = null,
    IReadOnlyList<DelveMine.Edge>? Links = null)
{
    public const string Key = "delve.mine.v1";
    public sealed record Edge(Point A, Point B);
    public readonly record struct Point(int X, int Y)
    {
        public int Depth => checked(Y + 1);
        static int Floor8(int n) => (int)Math.Floor(n / 8.0);
        public InstanceDelve.RoutePoint Wire => new(Floor8(X), Floor8(Y), X - Floor8(X) * 8, Y - Floor8(Y) * 8);
        public static Point Read(InstanceDelve.RoutePoint p)
        {
            if (p.CellX is < 0 or > 7 || p.CellY is < 0 or > 7) throw new ArgumentException("Invalid mine cell");
            int y = checked(p.BlockY * 8 + p.CellY);
            if (y < 0 || y == int.MaxValue) throw new ArgumentException("Invalid mine depth");
            return new(checked(p.BlockX * 8 + p.CellX), y);
        }
    }
    public sealed record Plan(Guid Id, IReadOnlyList<Point> Route, int Cost, bool Reward = true)
    {
        public Point From => Route[0];
        public Point To => Route[^1];
        public int Depth => To.Depth;
    }

    public static DelveMine Fresh(uint seed = 0) => new(1, seed, new(2, 0), [new(2, 0)]);
    public static DelveMine Load(ISavedData account) => Parse(account.Get(Key));
    static DelveMine Parse(string? text) => text is null ? Fresh() : JsonSerializer.Deserialize<DelveMine>(text) ?? Fresh();
    public static bool Change(ISavedData account, Func<DelveMine, DelveMine?> change) => account.Change(Key, old =>
        change(Parse(old)) is { } next ? JsonSerializer.Serialize(next) : null);

    public static byte MonsterLevel(int depth) => (byte)(34 + Math.Min(52L, Math.Max(0L, (long)depth - 1) * 52 / 149));
    public static double PackMultiplier(int depth) => 1 + Math.Max(0L, (long)depth - 1) / 100.0;

    public Plan? Select(IReadOnlyList<InstanceDelve.RoutePoint> selected, Func<int, int> price, out string why)
    {
        why = "";
        if (selected.Count < 2) { why = "Choose a destination connected to an opened checkpoint."; return null; }
        Point[] route;
        try { route = selected.Select(Point.Read).ToArray(); }
        catch (Exception e) when (e is OverflowException or ArgumentException) { why = "Invalid mine coordinates."; return null; }
        // Older builds could save a corridor cell. Resume the selected native path from that cell, without a loop.
        int currentIndex = Array.IndexOf(route, Current);
        if (currentIndex > 0) route = route[currentIndex..];
        if (route.Length < 2) { why = "Choose another destination."; return null; }
        if (route[0] != Current)
        {
            // The native chart chooses the opened node nearest the requested branch. Rejoin it from the actual
            // saved Crawler position using only edges that have previously been completed, never guessed links.
            var previous = new Dictionary<Point, Point> { [Current] = Current };
            var links = new Dictionary<Point, List<Point>>();
            foreach (var edge in Links ?? [])
            {
                if (!links.TryGetValue(edge.A, out var a)) links[edge.A] = a = [];
                if (!links.TryGetValue(edge.B, out var b)) links[edge.B] = b = [];
                a.Add(edge.B); b.Add(edge.A);
            }
            var queue = new Queue<Point>(); queue.Enqueue(Current);
            while (queue.TryDequeue(out var p) && !previous.ContainsKey(route[0]))
                foreach (var n in links.GetValueOrDefault(p) ?? [])
                    if (previous.TryAdd(n, p)) queue.Enqueue(n);
            if (!previous.ContainsKey(route[0])) { why = "Choose a route connected to the last completed checkpoint."; return null; }
            var prefix = new List<Point>();
            for (var p = route[0]; p != Current; p = previous[p]) prefix.Add(previous[p]);
            prefix.Reverse(); route = [.. prefix, .. route];
        }
        if (route.Distinct().Count() != route.Length) { why = "A route cannot loop back on itself."; return null; }
        var opened = Opened.ToHashSet(); long cost = 0;
        for (int i = 1; i < route.Length; i++)
        {
            long gap = Math.Abs((long)route[i].X - route[i - 1].X) + Math.Abs((long)route[i].Y - route[i - 1].Y);
            if (gap != 1) { why = "Follow connected mine cells."; return null; }
            if (!opened.Contains(route[i])) cost += Math.Max(0, price(route[i].Depth));
        }
        // Native paths include corridor cells with no checkpoint icon. The chosen final node is one trip's
        // destination; intermediate cells must not become separate arrivals or automatically queued journeys.
        if (cost > ushort.MaxValue) { why = "The route costs more sulphite than can be carried; choose a nearer checkpoint."; return null; }
        if (Pending is { } pending && pending.Route.SequenceEqual(route)) return pending;
        if (Pending is not null) { why = "Finish the current trip before choosing another destination."; return null; }
        return new(Guid.NewGuid(), route, (int)cost, !Opened.Contains(route[^1]));
    }

    public DelveMine Finish(Guid trip) => Pending?.Id != trip ? this : this with
    {
        Current = Pending.To, Opened = [.. Opened.Concat(Pending.Route).Distinct()], Pending = null,
        Links = [.. (Links ?? []).Concat(Pending.Route.Zip(Pending.Route.Skip(1), (a, b) => new Edge(a, b))).Distinct()],
    };

    public IReadOnlyList<InstanceDelve.Cell> Chart()
    {
        var blocks = new Dictionary<(int BlockX, int BlockY), ulong>();
        foreach (var p in Opened)
        {
            var wire = p.Wire; var block = (wire.BlockX, wire.BlockY);
            // The entrance is implicitly open. Marking it completed makes the native block disappear.
            blocks[block] = blocks.GetValueOrDefault(block) | (p == new Point(2, 0) ? 0 : 1UL << (wire.CellY * 8 + wire.CellX));
        }
        foreach (var p in new[] { Current, new(Current.X - 2, Current.Y), new(Current.X + 2, Current.Y), new(Current.X, checked(Current.Y + 2)) })
            blocks.TryAdd((p.Wire.BlockX, p.Wire.BlockY), 0);
        // The wire count is u16: retain the complete account graph, send a bounded window nearest its Crawler.
        var current = Current.Wire;
        return blocks.OrderBy(b => Math.Abs((long)b.Key.BlockX - current.BlockX) + Math.Abs((long)b.Key.BlockY - current.BlockY))
            .Take(ushort.MaxValue).Select(b => new InstanceDelve.Cell(unchecked((ushort)b.Key.BlockX),
                unchecked((ushort)b.Key.BlockY), b.Value, 0, 0)).ToArray();
    }
}
