using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>The Voyage planner's server side (ruleset 172 ops 4-7): the charted charts in inventory 104 moved, turned and
/// deleted, and the 3 × 3 board's 12 border modifiers with their rerolls. The board itself is the client's until a start
/// (op 0) names its charts. The border and the reroll count are saved per character (module data
/// <see cref="SaveKey"/>), so a relog does not reroll for free.</summary>
internal static class DeepwaterPlanner
{
    public const string SaveKey = "deepwater.plan";
    /// <summary>Mods domain 40, generation type 3: the 66 border modifiers. They carry no spawn weights.</summary>
    const int BorderDomain = 40, BorderGeneration = 3;
    const string HardModeOnly = "HardMode";

    sealed record SavedMod(string Id, int[] Values);
    sealed record Saved(List<SavedMod> Border, int Rerolls);

    /// <summary>The plan as saved: the 12 border modifiers (none before the 3 × 3 board) and the rerolls made on them.</summary>
    public sealed record Plan(IReadOnlyList<ItemMod> Border, int Rerolls);

    public static Plan Load(ClientState conn)
    {
        var mods = ItemsFor(conn).Mods;
        try
        {
            if (CharacterSaves.ModuleData(CharacterName(conn), SaveKey) is { } json && JsonSerializer.Deserialize<Saved>(json) is { } saved)
            {
                var border = saved.Border.Select(s => mods.ById(s.Id) is { } mod ? new ItemMod(ModSection.Implicit, mod.Hash, s.Values) : null).ToList();
                if (border.All(b => b is not null)) return new([.. border!], saved.Rerolls);
            }
        }
        catch (JsonException) { }
        return new([], 0);
    }

    /// <summary>Saves a plan as it was (a Voyage whose entry failed gets its border back).</summary>
    public static void Put(ClientState conn, Plan plan) => Save(conn, plan);

    static void Save(ClientState conn, Plan plan)
    {
        var mods = ItemsFor(conn).Mods;
        var saved = new Saved([.. plan.Border.Select(m => new SavedMod(mods.ByHash(m.Hash)?.Id ?? "", [.. m.Values]))], plan.Rerolls);
        CharacterSaves.ChangeModuleData(CharacterName(conn), SaveKey, _ => JsonSerializer.Serialize(saved));
    }

    /// <summary>The 3 × 3 board is open (league quest flag 1893 Deepwater3x3VoyagesEnabled; the window's side is 2 + the
    /// flag, H).</summary>
    public static bool Board3x3(ClientState conn)
    {
        lock (conn.Monsters) return conn.LeagueQuestFlags.Contains(DeepwaterConstants.VoyagesEnabledLeagueFlag);
    }

    /// <summary>The cost of the next reroll after <paramref name="rerolls"/>: RerollBorderCostBase × (GrowthPercent / 100)^k
    /// (3000, 6000 … with the 3.29 values); <see cref="DeepwaterHubRuleset.NoReroll"/> once RerollBorderMax were made.</summary>
    public static uint Cost(int rerolls)
    {
        if (rerolls >= DeepwaterConstants.RerollBorderMax) return DeepwaterHubRuleset.NoReroll;
        double cost = DeepwaterConstants.RerollBorderCostBase * Math.Pow(DeepwaterConstants.RerollBorderCostGrowthPercent / 100.0, rerolls);
        return cost >= uint.MaxValue ? DeepwaterHubRuleset.NoReroll : (uint)cost;
    }

    /// <summary>The plan, with border modifiers rolled and saved when the 3 × 3 board is open and it has none.</summary>
    public static Plan Current(ClientState conn)
    {
        var plan = Load(conn);
        if (!Board3x3(conn) || plan.Border.Count == 12) return plan;
        plan = new(Roll(conn, Random.Shared), 0);
        Save(conn, plan);
        return plan;
    }

    /// <summary>New border modifiers and no rerolls (after a started Voyage).</summary>
    public static Plan Renew(ClientState conn)
    {
        var plan = new Plan(Board3x3(conn) ? Roll(conn, Random.Shared) : [], 0);
        Save(conn, plan);
        return plan;
    }

    /// <summary>Mode 1 for this player: the border and the next reroll's cost, or no mods and no reroll before the 3 × 3
    /// board (the first Voyage has none, the wiki).</summary>
    public static byte[]? Message(ClientState conn, Plan plan) => Board3x3(conn)
        ? DeepwaterHubRuleset.Border(Cost(plan.Rerolls), plan.Border)
        : DeepwaterHubRuleset.Border(DeepwaterHubRuleset.NoReroll, []);

    public static async Task Send(ClientState conn, Plan plan)
    {
        if (Message(conn, plan) is not { } message) return;
        await PlayerOf(conn).Send(message, $"0x02c9 DeepwaterHubRules mode 1: {plan.Border.Count} border mods, reroll cost {(Board3x3(conn) ? Cost(plan.Rerolls) : DeepwaterHubRuleset.NoReroll)}");
    }

    static string BorderFamily(string id)
    {
        if (id.EndsWith(HardModeOnly, StringComparison.Ordinal))
            id = id[..^HardModeOnly.Length];
        return id.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
    }

    /// <summary>12 border modifiers: uniform over the pool, no family (the Id without its HardMode suffix or tier digit)
    /// twice on the board; in Ruthless any family with a HardMode variant uses only that variant (all G: the client data
    /// has no weights).</summary>
    static List<ItemMod> Roll(ClientState conn, Random random)
    {
        bool hard = LeagueFor(conn).HardMode;
        var allBorder = ItemsFor(conn).Mods.All.Where(m => m.Domain == BorderDomain && m.GenerationType == BorderGeneration).ToList();
        HashSet<string>? hardModeFamilies = hard
            ? allBorder.Where(m => m.Id.EndsWith(HardModeOnly, StringComparison.Ordinal)).Select(m => BorderFamily(m.Id)).ToHashSet(StringComparer.Ordinal)
            : null;
        var pool = allBorder.Where(m => hard
            ? m.Id.EndsWith(HardModeOnly, StringComparison.Ordinal) || !hardModeFamilies!.Contains(BorderFamily(m.Id))
            : !m.Id.EndsWith(HardModeOnly, StringComparison.Ordinal)).ToList();
        var chosen = new List<ItemMod>();
        var families = new HashSet<string>(StringComparer.Ordinal);
        while (chosen.Count < 12 && pool.Count > 0)
        {
            var mod = pool[random.Next(pool.Count)];
            pool.Remove(mod);
            if (!families.Add(BorderFamily(mod.Id))) continue;
            chosen.Add(mod.Roll(ModSection.Implicit, random));
        }
        return chosen;
    }

    // ---- the ops ----

    static InventoryItem? At(ClientState conn, int x) => ItemsOf(conn).Items(DeepwaterLocker.ChartedRow).FirstOrDefault(i => i.X == x);

    /// <summary>Op 4: the chart at <paramref name="from"/> in 104 goes to <paramref name="to"/>, swapping with a chart
    /// there; one 0xbf with both.</summary>
    public static async Task Move(IModuleContext module, ClientState conn, int from, int to)
    {
        var items = ItemsOf(conn);
        string what = $"{CharacterName(conn)} <= 0x02ca DeepwaterHubRules MoveStoredChart {from} → {to}";
        int width = InstanceInventories.PlayerDefaults[DeepwaterLocker.ChartedRow].Width;
        if (At(conn, from) is not { } moving || to < 0 || to >= width || from == to)
        {
            module.Log($"{what}: refused (no chart there, or no such cell)");
            return;
        }
        var other = At(conn, to);
        var added = new List<InventoryItem>();
        Saves.Atomically(() =>
        {
            if (items.Remove(DeepwaterLocker.ChartedRow, moving.Id) is not { } a) return;
            var b = other is null ? null : items.Remove(DeepwaterLocker.ChartedRow, other.Id);
            added.Add(items.Put(DeepwaterLocker.ChartedRow, a with { X = (byte)to }));
            if (b is not null) added.Add(items.Put(DeepwaterLocker.ChartedRow, b with { X = (byte)from }));
        });
        if (added.Count == 0) return;
        module.Log($"{what}{(other is null ? "" : $", swapped with item {other.Id}")}");
        await PlayerOf(conn).Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(DeepwaterLocker.ChartedRow), items.NextRevision(DeepwaterLocker.ChartedRow),
            [.. added.Select(a => a.Id)], added), $"0x00bf inventory {PlayerInventories.InventoryId(DeepwaterLocker.ChartedRow)}: charts moved");
    }

    /// <summary>Op 5: the chart at <paramref name="x"/> in 104 turns one quarter (rotation + 1 mod 4, G: the direction) and
    /// is sent again (remove and add of the same id), as the client changes nothing itself.</summary>
    public static async Task Rotate(IModuleContext module, ClientState conn, int x)
    {
        var items = ItemsOf(conn);
        string what = $"{CharacterName(conn)} <= 0x02ca DeepwaterHubRules RotateStoredChart {x}";
        if (At(conn, x) is not { Item.Chart: { } chart } it
            || items.Change(DeepwaterLocker.ChartedRow, it.Id, i => i with { Chart = chart with { Rotation = (byte)((chart.Rotation + 1) & 3) } }) is not { } turned)
        {
            module.Log($"{what}: refused (no chart there)");
            return;
        }
        module.Log($"{what}: item {it.Id} rotation {chart.Rotation} → {turned.Item.Chart!.Value.Rotation}");
        await PlayerOf(conn).Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(DeepwaterLocker.ChartedRow), items.NextRevision(DeepwaterLocker.ChartedRow),
            [turned.Id], [turned]), $"0x00bf inventory {PlayerInventories.InventoryId(DeepwaterLocker.ChartedRow)}: item {turned.Id} turned");
    }

    /// <summary>Op 6: the chart at <paramref name="x"/> in 104 is destroyed (G: the client asks first).</summary>
    public static async Task Delete(IModuleContext module, ClientState conn, int x)
    {
        var items = ItemsOf(conn);
        string what = $"{CharacterName(conn)} <= 0x02ca DeepwaterHubRules DeleteStoredChart {x}";
        if (At(conn, x) is not { } it || items.Remove(DeepwaterLocker.ChartedRow, it.Id) is null)
        {
            module.Log($"{what}: refused (no chart there)");
            return;
        }
        module.Log($"{what}: item {it.Id} destroyed");
        await PlayerOf(conn).Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(DeepwaterLocker.ChartedRow), items.NextRevision(DeepwaterLocker.ChartedRow),
            [it.Id], []), $"0x00bf inventory {PlayerInventories.InventoryId(DeepwaterLocker.ChartedRow)}: remove item {it.Id}");
    }

    /// <summary>Op 7: new border modifiers for <see cref="Cost"/> Sulphur (locker first, then the main inventory, G); none
    /// before the 3 × 3 board or after RerollBorderMax. Mode 1 is sent again either way.</summary>
    public static async Task Reroll(IModuleContext module, ClientState conn)
    {
        var plan = Current(conn);
        string what = $"{CharacterName(conn)} <= 0x02ca DeepwaterHubRules RerollBorderModifiers";
        uint cost = Cost(plan.Rerolls);
        string? why = !Board3x3(conn) ? "no border modifiers before the 3 × 3 board"
            : cost == DeepwaterHubRuleset.NoReroll ? $"{plan.Rerolls} rerolls made already"
            : DeepwaterLocker.SulphurHeld(conn) < cost ? $"{cost} Sulphur needed, {DeepwaterLocker.SulphurHeld(conn)} held"
            : !await DeepwaterLocker.PaySulphur(conn, (int)cost) ? "the Sulphur could not be taken" : null;
        if (why is null)
        {
            plan = new(Roll(conn, Random.Shared), plan.Rerolls + 1);
            Save(conn, plan);
            module.Log($"{what}: paid {cost} Sulphur, reroll {plan.Rerolls}: {string.Join(", ", plan.Border.Select(m => ItemsFor(conn).Mods.ByHash(m.Hash)?.Id))}");
        }
        else module.Log($"{what}: refused ({why})");
        await Send(conn, plan);
    }
}
