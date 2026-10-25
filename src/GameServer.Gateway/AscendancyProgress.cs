using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// One main ascendancy and its own allocated nodes/point awards. A missing selection is an unascended character.
/// Nodes exclude the implicit, free start; ordinary passive points remain in CharacterSaves.Save.Passives.
/// </summary>
sealed record AscendancyProgress(AscendancyIdentity? Selection = null, IReadOnlyList<ushort>? Nodes = null, IReadOnlyList<string>? Awards = null)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; init; }
    [JsonIgnore] public IReadOnlyList<ushort> Allocated => Nodes ?? [];
    [JsonIgnore] public IReadOnlyList<string> EarnedAwards => Awards ?? [];
    [JsonIgnore] public int EarnedPoints => AscendancyRules.PointsFor(EarnedAwards);
}

/// <summary>Rejected input or a saved ascendancy this build cannot safely restore; callers must keep the old save.</summary>
sealed class AscendancyRuleException(string message) : ArgumentException(message);

/// <summary>A connection tried to overwrite passive progress already changed by another connection.</summary>
sealed class PassiveProgressConflictException() : InvalidOperationException("Passive progress changed in another connection; reconnect before changing it again");

/// <summary>Pure validation/transitions: construct a candidate, save it, then publish it to the connection.</summary>
sealed class AscendancyRules(AscendancyCatalog catalog)
{
    // Every Labyrinth tier awards two points once. Stable keys make retries/relogins idempotent.
    public static IReadOnlyList<string> AwardKeys { get; } = Array.AsReadOnly(new[] { "normal", "cruel", "merciless", "eternal" });
    public const int RefundPointsPerAscendancyPoint = 5;

    public static int PointsFor(IEnumerable<string> awards)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string award in awards)
            if (!AwardKeys.Contains(award) || !seen.Add(award))
                throw new AscendancyRuleException($"Unknown or duplicate ascendancy award: {award}");
        return seen.Count * 2;
    }

    /// <summary>Checks the entire allocation on load as well as before every change; never trims/drops saved nodes.</summary>
    public AscendancyCatalog.Definition? Validate(byte baseClass, AscendancyProgress? progress)
    {
        progress ??= new();
        int points = PointsFor(progress.EarnedAwards);
        if (progress.Selection is null)
        {
            if (progress.Allocated.Count != 0) throw new AscendancyRuleException("Ascendancy nodes require a selected main ascendancy");
            return null;
        }
        var definition = catalog.Find(progress.Selection) ?? throw new AscendancyRuleException(
            $"Unknown ascendancy {progress.Selection.Family}/{progress.Selection.Id} at revision {progress.Selection.Revision}");
        if (definition.BaseClass != baseClass) throw new AscendancyRuleException($"{definition.Name} belongs to a different base class");
        var allocated = new HashSet<ushort>();
        foreach (ushort node in progress.Allocated)
        {
            if (!allocated.Add(node)) throw new AscendancyRuleException($"Duplicate ascendancy node {node}");
            if (!definition.Nodes.TryGetValue(node, out var data) || data.IsStart)
                throw new AscendancyRuleException($"Node {node} does not belong to {definition.Name}'s {definition.Identity.Family} tree");
        }
        int spent = allocated.Sum(n => definition.Nodes[n].PointCost);
        if (spent > points) throw new AscendancyRuleException($"Ascendancy points exceeded: {spent} spent, {points} earned");
        var reachable = new HashSet<ushort> { definition.StartNode };
        var queue = new Queue<ushort>();
        queue.Enqueue(definition.StartNode);
        while (queue.TryDequeue(out ushort current))
            foreach (ushort next in definition.Nodes[current].Adjacent)
                if (allocated.Contains(next) && reachable.Add(next)) queue.Enqueue(next);
        if (allocated.Any(n => !reachable.Contains(n))) throw new AscendancyRuleException("Ascendancy nodes must remain connected to their start");
        var groups = new HashSet<ushort>();
        foreach (ushort option in allocated.Where(n => definition.Nodes[n].IsChoiceOption))
        {
            var parents = definition.Nodes[option].Adjacent.Where(n => definition.Nodes[n].IsChoice).ToArray();
            if (parents.Length != 1 || !allocated.Contains(parents[0]))
                throw new AscendancyRuleException($"Choice option {option} requires its allocated choice node");
            if (!groups.Add(parents[0])) throw new AscendancyRuleException("Only one option of an ascendancy choice may be allocated");
        }
        return definition;
    }

    public AscendancyProgress Select(byte baseClass, AscendancyProgress? progress, AscendancyIdentity identity)
    {
        progress ??= new();
        Validate(baseClass, progress);
        bool same = progress.Selection is not null && progress.Selection.Family == identity.Family &&
                    progress.Selection.Id == identity.Id && progress.Selection.Revision == identity.Revision;
        if (progress.Selection is not null && progress.Allocated.Count > 0 && !same)
            throw new AscendancyRuleException("Refund the current ascendancy's points before choosing another");
        var next = progress with { Selection = same ? progress.Selection : identity, Nodes = progress.Allocated.ToArray(), Awards = progress.EarnedAwards.ToArray() };
        Validate(baseClass, next);
        return next;
    }

    /// <summary>Awarding an already completed tier changes nothing; an unknown tier never creates points.</summary>
    public AscendancyProgress Award(byte baseClass, AscendancyProgress? progress, string award)
    {
        progress ??= new();
        Validate(baseClass, progress);
        if (!AwardKeys.Contains(award)) throw new AscendancyRuleException($"Unknown ascendancy award: {award}");
        if (progress.EarnedAwards.Contains(award)) return progress;
        var next = progress with { Awards = AwardKeys.Where(k => k == award || progress.EarnedAwards.Contains(k)).ToArray() };
        Validate(baseClass, next);
        return next;
    }

    /// <summary>Checks a complete proposed node set, including a multi-node request, before any live state changes.</summary>
    public AscendancyProgress Allocate(byte baseClass, AscendancyProgress? progress, IEnumerable<ushort> nodes)
    {
        progress ??= new();
        Validate(baseClass, progress);
        var next = progress with { Nodes = progress.Allocated.Concat(nodes).ToArray() };
        Validate(baseClass, next);
        return next;
    }

    /// <summary>
    /// A refund cannot orphan nodes or leave ordinary allocations spending the ordinary points the removed node granted.
    /// The caller deducts Cost from the shared refund-point currency in the same save as this state.
    /// </summary>
    public (AscendancyProgress Progress, int Cost) Refund(byte baseClass, AscendancyProgress? progress,
        IEnumerable<ushort> nodes, int ordinarySpent, int ordinaryBudgetWithoutAscendancy)
    {
        progress ??= new();
        var definition = Validate(baseClass, progress) ?? throw new AscendancyRuleException("No main ascendancy is selected");
        var remove = nodes.ToHashSet();
        if (remove.Count == 0 || remove.Any(n => !progress.Allocated.Contains(n)))
            throw new AscendancyRuleException("Only allocated ascendancy nodes can be refunded");
        var next = progress with { Nodes = progress.Allocated.Where(n => !remove.Contains(n)).ToArray() };
        Validate(baseClass, next);
        if (ordinarySpent > ordinaryBudgetWithoutAscendancy + OrdinaryPointsGranted(baseClass, next))
            throw new AscendancyRuleException("Refund ordinary passives spending these granted points first");
        int cost = checked(remove.Sum(n => definition.Nodes[n].RefundCost));
        return (next, cost);
    }

    public int SpentPoints(byte baseClass, AscendancyProgress? progress) => Validate(baseClass, progress) is { } definition
        ? progress!.Allocated.Sum(n => definition.Nodes[n].PointCost) : 0;

    public int OrdinaryPointsGranted(byte baseClass, AscendancyProgress? progress) => Validate(baseClass, progress) is { } definition
        ? progress!.Allocated.Sum(n => definition.Nodes[n].PointsGranted) : 0;
}
