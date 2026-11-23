namespace GameServer.Modules.Incursion;

/// <summary>
/// One instance's incursions as plain state: the spots Alva can stand at, which one is next, how far she and her portal
/// are shown, and the run that is going (its monsters and architects, the clock, the kills). It belongs to the area
/// instance, so a second party member, a portal re-entry and a module reload all read the same one; the messages each
/// client needs follow from it (<see cref="IncursionEncounter"/>). Every member is safe to call from any connection.
/// </summary>
public sealed class IncursionRun(IReadOnlyList<(int X, int Y)> spots, byte level, int seed)
{
    /// <summary>How far the next spot has got: nothing shown, her portal asleep on the ground, Alva beside it, the portal
    /// open, the run going, or every spot used.</summary>
    public enum Stage { Hidden, PortalShown, Shown, Open, Inside, Over }

    readonly object gate = new();
    readonly HashSet<uint> monsters = [];
    readonly Dictionary<uint, int> architects = [];
    readonly List<int> killedArchitects = [];
    Stage stage = Stage.Hidden;
    int used, total, killed, sentSeconds = -1, sentKilled = -1;
    double? left;

    public IReadOnlyList<(int X, int Y)> Spots { get; } = spots;
    public byte Level { get; } = level;
    public int Seed { get; } = seed;

    public Stage Current { get { lock (gate) return stage; } }
    public int Used { get { lock (gate) return used; } }

    /// <summary>The spot Alva stands at next, null once every spot is used.</summary>
    public (int X, int Y)? Spot { get { lock (gate) return used < Spots.Count ? Spots[used] : null; } }

    /// <summary>Seconds left; null until the start delay is over.</summary>
    public double? Left { get { lock (gate) return left; } }
    public int Total { get { lock (gate) return total; } }
    public int Killed { get { lock (gate) return killed; } }
    public IReadOnlyList<int> KilledArchitects { get { lock (gate) return [.. killedArchitects]; } }
    public IReadOnlyList<uint> RoomMonsters { get { lock (gate) return [.. monsters]; } }

    /// <summary>Moves a spot's stage on by one step (Hidden → PortalShown → Shown → Open); false when it is not at
    /// <paramref name="from"/>.</summary>
    public bool Step(Stage from, Stage to)
    {
        lock (gate)
        {
            if (stage != from) return false;
            stage = to;
            return true;
        }
    }

    /// <summary>The run starts: its monsters (the architects among them, by object id with their IncursionArchitect row)
    /// and its start seconds. The progress bar counts the monsters that are not architects. False when the portal was
    /// not open.</summary>
    public bool Start(IEnumerable<uint> monsterIds, IReadOnlyDictionary<uint, int> architectRows, int startSeconds)
    {
        lock (gate)
        {
            if (stage != Stage.Open) return false;
            stage = Stage.Inside;
            monsters.Clear();
            architects.Clear();
            killedArchitects.Clear();
            foreach (var (id, row) in architectRows) architects[id] = row;
            foreach (uint id in monsterIds) monsters.Add(id);
            total = monsters.Count(id => !architects.ContainsKey(id));
            killed = 0;
            sentSeconds = sentKilled = -1;
            left = startSeconds;
            return true;
        }
    }

    /// <summary>A kill of one of the run's monsters (by the character or a minion): it adds <paramref name="seconds"/> and
    /// moves the bar; an architect's kill adds the same and is counted for the temple, not in the bar. False for anything
    /// that is not a live monster of the run.</summary>
    public bool Kill(uint id, double seconds)
    {
        lock (gate)
        {
            if (stage != Stage.Inside || !monsters.Remove(id)) return false;
            if (architects.TryGetValue(id, out int row))
            {
                if (!killedArchitects.Contains(row)) killedArchitects.Add(row);
            }
            else killed++;
            left += seconds;
            return true;
        }
    }

    /// <summary>A cursed treasure costs seconds off the clock.</summary>
    public bool Cost(double seconds)
    {
        lock (gate)
        {
            if (stage != Stage.Inside || left is null) return false;
            left -= seconds;
            return true;
        }
    }

    /// <summary>The clock runs. True once it has reached 0.</summary>
    public bool Tick(double seconds)
    {
        lock (gate)
        {
            if (stage != Stage.Inside || left is null) return false;
            left -= seconds;
            return left <= 0;
        }
    }

    /// <summary>The whole second to tell the client when it changed since the last time, else null.</summary>
    public int? SecondsToSend()
    {
        lock (gate)
        {
            if (left is not { } l) return null;
            int seconds = Math.Max(0, (int)l);
            if (seconds == sentSeconds) return null;
            sentSeconds = seconds;
            return seconds;
        }
    }

    /// <summary>The bar's killed / monsters when it changed since the last time, else null.</summary>
    public float? ProgressToSend()
    {
        lock (gate)
        {
            if (killed == sentKilled) return null;
            sentKilled = killed;
            return total == 0 ? 0 : (float)killed / total;
        }
    }

    /// <summary>Forces the next <see cref="SecondsToSend"/> and <see cref="ProgressToSend"/> to answer (a client that
    /// joins the run).</summary>
    public void ResendAll()
    {
        lock (gate) sentSeconds = sentKilled = -1;
    }

    /// <summary>The run is over (the clock reached 0): the next spot is next, or none. The monsters are the caller's to
    /// remove.</summary>
    public IReadOnlyList<uint> Finish()
    {
        lock (gate)
        {
            var rest = monsters.ToList();
            Clear();
            used++;
            stage = used < Spots.Count ? Stage.Hidden : Stage.Over;
            return rest;
        }
    }

    /// <summary>The run was dropped (its owner left): nothing is counted, the spot stays and its portal stays open for the
    /// next time.</summary>
    public IReadOnlyList<uint> Abandon()
    {
        lock (gate)
        {
            if (stage != Stage.Inside) return [];
            var rest = monsters.ToList();
            Clear();
            stage = Stage.Open;
            return rest;
        }
    }

    void Clear()
    {
        monsters.Clear();
        architects.Clear();
        left = null;
        total = killed = 0;
        sentSeconds = sentKilled = -1;
    }
}
