namespace GameServer.Modules.Deepwater;

/// <summary>One Allflame run: the state of an encounter instance (a Chart in DeepwaterEncounter, a Voyage in
/// DeepwaterEncounterVoyage), shared by everyone in it. Not saved: a run ends with its instance.</summary>
internal sealed class DeepwaterRun
{
    public enum RunPhase { Exploring, Collapsing, Descending, Extractable, Ended }

    /// <summary>A placed lantern: its object, its cell, the <c>DeepwaterLantern</c> task data last sent (a, rate_a, b,
    /// rate_b) and the instance time it was sent at, its drop animation (1-3) and its place in the placing order.</summary>
    public sealed record Lantern(uint Id, int X, int Y, float A, float RateA, float B, float RateB, double SetAt, int Drop, int Order)
    {
        /// <summary>The task data as the client reads it (4 × f32 LE).</summary>
        public byte[] TaskData()
        {
            var b = new byte[16];
            BitConverter.TryWriteBytes(b.AsSpan(0), A);
            BitConverter.TryWriteBytes(b.AsSpan(4), RateA);
            BitConverter.TryWriteBytes(b.AsSpan(8), B);
            BitConverter.TryWriteBytes(b.AsSpan(12), RateB);
            return b;
        }

        /// <summary>The task's a and b at instance time <paramref name="now"/> (the client's countdown).</summary>
        public (float A, float B) At(double now)
        {
            double t = Math.Max(0, now - SetAt);
            return ((float)Math.Max(0, A - RateA * t), (float)Math.Max(0, B - RateB * t));
        }

        /// <summary>The same light restated at <paramref name="now"/>: what a lantern created now must carry to draw the
        /// same circle.</summary>
        public Lantern Restated(double now)
        {
            var (a, b) = At(now);
            return this with { A = a, B = b, SetAt = now };
        }

        /// <summary>The light radius at instance time <paramref name="now"/>, as the client draws it (<c>0x141ebe600</c>
        /// with the task's per-frame countdown <c>0x141642100</c>): a and b fall at their rates (b stops at 0), radius =
        /// lerp(10, BaseLanternRadius, clamp01(a)·(0.6 + 0.4·clamp01(b))); 0 once a reaches 0 (the task ends).</summary>
        public double Radius(double now)
        {
            double t = Math.Max(0, now - SetAt);
            double a = A - RateA * t;
            if (a <= 0) return 0;
            double b = Math.Max(0, B - RateB * t);
            int full = DeepwaterConstants.BaseLanternRadius, min = DeepwaterConstants.MinimumLanternRadius;
            return min + (full - min) * Math.Clamp(a, 0, 1) * (0.6 + 0.4 * Math.Clamp(b, 0, 1));
        }
    }

    public required bool Voyage { get; init; }
    public required DeepwaterBoard Board
    {
        get => _board;
        init
        {
            _board = value;
            // Built here, with the run, so no kill allocates.
            var rects = new int[value.Cells.Count * 4];
            for (int i = 0; i < value.Cells.Count; i++)
                (rects[4 * i], rects[4 * i + 1], rects[4 * i + 2], rects[4 * i + 3]) = (value.Cells[i].X0, value.Cells[i].Y0, value.Cells[i].X1, value.Cells[i].Y1);
            _rects = rects;
        }
    }
    readonly DeepwaterBoard _board = null!;
    /// <summary>The character who holds the lanterns (QuestFlags 3550): the one whose entry made the run.</summary>
    public required string Controller { get; init; }
    public RunPhase Phase { get; set; } = RunPhase.Exploring;
    public readonly List<Lantern> Lanterns = [];
    /// <summary>Lanterns placed by the player (the first, free one is not counted).</summary>
    public int Placed { get; set; }
    public int Total { get; set; }
    /// <summary>The run's monster level: the plan's area level when a chart or a board started it (DeepwaterStart), else
    /// (a /goto) the controller's character level clamped to 1..84 (G; WorldAreas gives the encounter areas level 1).</summary>
    public required int Level { get; init; }
    /// <summary>The chart or board that started the run (null for a /goto run), and per board cell (the board's index) what
    /// a Voyage's cell adds on its own: its monsters' mods (with the level's more life) and its drops' quantity and rarity
    /// factors. Null for a Chart, whose stats are the instance's area stats already, and for a /goto run.</summary>
    public DeepwaterPlan? Plan { get; init; }
    public MonsterModSet?[]? CellMods { get; init; }
    public double[]? CellQuantity { get; init; }
    public double[]? CellRarity { get; init; }
    /// <summary>Precomputed Cursed Ducat IIQ/IIR drop multipliers and run-wide monster mods (level scaling + Cursed Ducat
    /// stats), plus per-cell special-room rare-monster Ducat targets (built once at <c>StartRun</c>, 0 allocs per kill).</summary>
    public double CursedQuantityFactor { get; init; } = 1.0;
    public double CursedRarityFactor { get; init; } = 1.0;
    public MonsterModSet? RunMods { get; init; }
    public string?[]? CellRareDucat { get; init; }
    public int[]? CellRareDucatChance { get; init; }
    /// <summary>Called once when the run ends (DeepwaterExtraction.End or Fail) with the players it ended for and whether it
    /// succeeded.</summary>
    public Func<IReadOnlyList<ClientState>, bool, Task>? OnEnd { get; set; }

    // The board's cell rectangles as x0, y0, x1, y1 per cell, for CellIndex (built with Board).
    readonly int[] _rects = [];

    /// <summary>The board cell at (x, y), or −1: a plain loop over at most 9 rectangles in one array, for the per-kill
    /// drop path (measured in the pull request).</summary>
    public int CellIndex(int x, int y)
    {
        var r = _rects;
        for (int i = 0; i + 3 < r.Length; i += 4)
            if ((uint)(x - r[i]) < (uint)(r[i + 2] - r[i]) && (uint)(y - r[i + 1]) < (uint)(r[i + 3] - r[i + 1])) return i >> 2;
        return -1;
    }
    /// <summary>The league's packs not yet woken by a lantern (DeepwaterMonsters), and how many were planned and woken.</summary>
    public readonly List<DeepwaterMonsters.Pack> Planned = [];
    public int PlannedPacks { get; set; }
    public int ReleasedPacks { get; set; }
    public int ReleasedMonsters { get; set; }
    /// <summary>The run's Dead Man's Sulphur nodes, chests and interactables (DeepwaterLoot), the Sulphur its lanterns
    /// collected and the most it can collect, and each character's Golden Lantern stacks.</summary>
    public readonly List<DeepwaterLoot.Thing> Things = [];
    public int Sulphur { get; set; }
    public int SulphurCap { get; set; }
    public readonly Dictionary<string, int> GoldenLanterns = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The lanterns whose light collected Sulphur (their StateMachine keeps <c>collected_resource</c> = 1).</summary>
    public readonly HashSet<uint> Flared = [];

    /// <summary>A lantern's part in the collapse (DeepwaterCollapse): when it starts to shrink and at what rate, whether it
    /// stops at the floor radius (the Bathysphere's own lantern in a Chart) and where it is now.</summary>
    public sealed class Shrink
    {
        public required double Start { get; init; }
        public required float Rate { get; init; }
        public bool Floor { get; init; }
        public bool Started, Held, Out;
    }

    /// <summary>The collapse: per lantern its shrink, the HUD's collapse state (0x2c9 mode 3) and the swarm's next pack.</summary>
    public readonly Dictionary<uint, Shrink> Shrinks = [];
    public byte CollapseState { get; set; }
    public double NextSwarm { get; set; }
    public int SwarmPacks { get; set; }

    /// <summary>The run's Bathyspheres and capsules (DeepwaterExtraction), and the capsule window each character has open.</summary>
    public readonly List<DeepwaterExtraction.Device> Devices = [];
    public readonly Dictionary<string, uint> OpenCapsule = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When a called Bathysphere lands (instance seconds), 0 when none is coming.</summary>
    public double LandAt { get; set; }
    /// <summary>When the called Bathysphere started its descent (instance seconds), and the last stage of its
    /// <c>DeepwaterBathysphereDrop</c> task sent to the run's members.</summary>
    public double DescentStart { get; set; }
    public uint DropStage { get; set; }
    /// <summary>How the run ended (null while it runs): "success", "failure: …".</summary>
    public string? Outcome { get; set; }
    /// <summary>The run failed (a death, drowning, leaving another way; DeepwaterExtraction.Fail): its instance is dead.
    /// Nothing in it opens, pickups stay Dredge, and a resurrect goes to The Sovereign (G: the research's "resurrect at
    /// the hub, not in the encounter").</summary>
    public bool Failed { get; set; }
    /// <summary>The deck's CollectionChest, once someone rode up.</summary>
    public uint DeckChest { get; set; }
    public readonly object Gate = new();

    public Lantern[] Snapshot() { lock (Gate) return [.. Lanterns]; }

    /// <summary>Whether the cell is inside a lantern's light (<c>dx² + dy² ≤ r²</c>, the client's test <c>0x141684a00</c>)
    /// or inside Velka's boss arena safe zone (<c>InvisibleLantern</c>).</summary>
    public bool InLight((int X, int Y) at, double now)
    {
        if (DeepwaterVelka.InArenaLight(this, at)) return true;
        lock (Gate)
            foreach (var l in Lanterns)
            {
                double r = l.Radius(now);
                long dx = at.X - l.X, dy = at.Y - l.Y;
                if (r > 0 && dx * dx + dy * dy <= r * r) return true;
            }
        return false;
    }
}
