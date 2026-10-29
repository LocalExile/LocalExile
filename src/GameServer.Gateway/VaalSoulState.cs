using GameServer.Core.Protocol.Instance;

/// <summary>Connection-owned, nonpersistent souls. Caller serializes all access, including consumption, under Monsters.</summary>
sealed class VaalSoulState
{
    readonly SortedDictionary<uint, VaalSoulBudget> budgets = [];
    uint? lastGranted;
    public VaalSoulBudget[] Read() => [.. budgets.Values];

    public void Reconcile(IEnumerable<(uint Row, uint Capacity)> sources)
    {
        // Distinct equipped instances share one row. Differing source capacities use the largest cap by policy.
        var wanted = sources.GroupBy(s => s.Row).ToDictionary(g => g.Key, g => g.Max(s => s.Capacity));
        foreach (uint row in budgets.Keys.Except(wanted.Keys).ToArray()) budgets.Remove(row);
        foreach (var (row, capacity) in wanted)
            budgets[row] = new(row, capacity, Math.Min(capacity, budgets.GetValueOrDefault(row).Current));
    }

    /// <summary>One soul per eligible kill, round-robin over sorted distinct nonfull rows, regardless of rarity.</summary>
    public bool Gain()
    {
        var eligible = budgets.Values.Where(b => b.Current < b.Capacity).ToArray();
        if (eligible.Length == 0) return false;
        var next = eligible.FirstOrDefault(b => lastGranted is null || b.ActiveSkillsRow > lastGranted.Value);
        if (next.Capacity == 0) next = eligible[0];
        budgets[next.ActiveSkillsRow] = next with { Current = next.Current + 1 };
        lastGranted = next.ActiveSkillsRow;
        return true;
    }

    /// <summary>Later executors can atomically check and pay a source's effective requirement under the caller's lock.
    /// No currently unsupported Vaal cast calls this seam.</summary>
    public bool TryConsume(uint row, uint required)
    {
        if (!budgets.TryGetValue(row, out var budget) || budget.Current < required) return false;
        budgets[row] = budget with { Current = budget.Current - required };
        return true;
    }

    public void ClearBalance()
    {
        foreach (var budget in Read()) budgets[budget.ActiveSkillsRow] = budget with { Current = 0 };
        lastGranted = null;
    }

    public sealed record Carried(VaalSoulBudget[] Budgets, uint? LastGranted);
    public Carried Carry()
    {
        var result = new Carried(Read(), lastGranted);
        budgets.Clear(); lastGranted = null;
        return result;
    }

    public void Restore(Carried carried)
    {
        budgets.Clear();
        foreach (var budget in carried.Budgets)
            budgets.Add(budget.ActiveSkillsRow, budget with { Current = Math.Min(budget.Current, budget.Capacity) });
        lastGranted = carried.LastGranted;
    }
}
