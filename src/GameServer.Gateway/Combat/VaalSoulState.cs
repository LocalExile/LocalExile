using GameServer.Core.Protocol.Instance;

/// <summary>Connection-owned, nonpersistent souls. Caller serializes all access, including consumption, in the area's frame.</summary>
public sealed class VaalSoulState
{
    readonly SortedDictionary<uint, VaalSoulBudget> budgets = [];
    uint? lastGranted;
    public VaalSoulBudget[] Read() => [.. budgets.Values];

    /// <summary>Bumped by every change a client can see (a gained or spent soul, a new capacity, a cleared balance), so
    /// a presentation can tell that there is nothing to send without walking the sources or the player's stats.</summary>
    public int Revision { get; private set; }
    int presentedRevision = int.MinValue;
    public bool Unpresented => Revision != presentedRevision;
    public void Presented() => presentedRevision = Revision;

    public void Reconcile(IEnumerable<(uint Row, uint Capacity)> sources)
    {
        // Distinct equipped instances share one row. Differing source capacities use the largest cap by policy.
        var wanted = sources.GroupBy(s => s.Row).ToDictionary(g => g.Key, g => g.Max(s => s.Capacity));
        foreach (uint row in budgets.Keys.Except(wanted.Keys).ToArray()) budgets.Remove(row);
        foreach (var (row, capacity) in wanted)
            budgets[row] = new(row, capacity, Math.Min(capacity, budgets.GetValueOrDefault(row).Current));
        Revision++;
    }

    /// <summary>One soul per eligible kill, round-robin over sorted distinct nonfull rows, regardless of rarity.</summary>
    public bool Gain()
    {
        uint? next = null, first = null;
        foreach (var (row, budget) in budgets)
        {
            if (budget.Current >= budget.Capacity) continue;
            first ??= row;
            if (lastGranted is null || row > lastGranted.Value) { next = row; break; }
        }
        if ((next ??= first) is not uint chosen) return false;
        budgets[chosen] = budgets[chosen] with { Current = budgets[chosen].Current + 1 };
        lastGranted = chosen;
        Revision++;
        return true;
    }

    /// <summary>Later executors can atomically check and pay a source's effective requirement under the caller's lock.
    /// No currently unsupported Vaal cast calls this seam.</summary>
    public bool TryConsume(uint row, uint required)
    {
        if (!budgets.TryGetValue(row, out var budget) || budget.Current < required) return false;
        budgets[row] = budget with { Current = budget.Current - required };
        Revision++;
        return true;
    }

    public void ClearBalance()
    {
        if (budgets.Count == 0) return;
        foreach (var budget in Read()) budgets[budget.ActiveSkillsRow] = budget with { Current = 0 };
        lastGranted = null;
        Revision++;
    }

    public sealed record Carried(VaalSoulBudget[] Budgets, uint? LastGranted);
    public Carried Carry()
    {
        var result = new Carried(Read(), lastGranted);
        budgets.Clear(); lastGranted = null;
        presentedRevision = Revision;
        return result;
    }

    public void Restore(Carried carried)
    {
        budgets.Clear();
        foreach (var budget in carried.Budgets)
            budgets.Add(budget.ActiveSkillsRow, budget with { Current = Math.Min(budget.Current, budget.Capacity) });
        lastGranted = carried.LastGranted;
        Revision++;
    }
}
