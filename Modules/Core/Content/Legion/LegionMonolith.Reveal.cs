using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>
/// The reveal: the army stands hidden (LegionBreakout state 0, no body) until the monolith shatters; then a 0x2c8 state 2 shows each
/// member, nearest the monolith first (R10 section 1), and the LegionRules ruleset gives the client its HUD and area audio.
/// </summary>
static partial class LegionMonolith
{
    /// <summary>GUESS (live check, R10 L1): the seconds the reveal takes from the nearest member to the farthest, standing for the pulse wave
    /// (the pulse clip is 0.791 s).</summary>
    const double RevealSeconds = 0.8;
    /// <summary>How many steps the reveal has: the members are grouped by distance, one area timer per step, not one per member.</summary>
    const int RevealSteps = 8;

    /// <summary>The shatter: every member standing is revealed in steps by its distance from the monolith, over <see cref="RevealSeconds"/>.</summary>
    static async Task RevealArmy(IModuleContext module, Player player, Encounter e)
    {
        LegionStasis.Member[] army;
        lock (e) army = e.Army ?? [];
        double now = global::Game.AreaTime(player);
        var distances = new List<(LegionStasis.Member Member, double Distance)>();
        foreach (var m in army)
        {
            if (m.Revealed || !player.Area.Monsters.TryGetValue(m.Id, out var monster) || monster.Dead) continue;
            var (x, y) = monster.Walk.Position(now);
            distances.Add((m, Math.Sqrt((double)(x - e.X) * (x - e.X) + (double)(y - e.Y) * (y - e.Y))));
        }
        if (distances.Count == 0) return;
        double farthest = Math.Max(1, distances.Max(d => d.Distance));
        var steps = distances.GroupBy(d => Math.Min(RevealSteps - 1, (int)(d.Distance / farthest * RevealSteps))).OrderBy(g => g.Key).ToList();
        foreach (var step in steps)
        {
            var members = step.Select(d => d.Member).ToList();
            double delay = RevealSeconds * step.Key / RevealSteps;
            if (delay <= 0) await RevealMembers(player, e, members);
            else global::Game.SendAfter(player, delay, () => RevealMembers(player, e, members));
        }
        module.Log($"army reveal: {distances.Count} members in {steps.Count} steps over {RevealSeconds} s");
    }

    /// <summary>Reveals members (once each): the body is theirs again, hits count (the break-out handler), and every client gets the 0x2c8 state 2; the origin stays in state 0 for a joiner (LegionStasis.Present).
    static async Task RevealMembers(Player player, Encounter e, IReadOnlyList<LegionStasis.Member> members, bool keep = true)
    {
        if (player.Instance is null) return;
        var done = new List<LegionStasis.Member>(members.Count);
        foreach (var m in members)
        {
            if (!player.Area.Monsters.TryGetValue(m.Id, out var monster) || monster.Dead || !Holds.TryGetValue(monster, out var hold)) continue;
            lock (hold) { if (hold.Revealed) continue; hold.Revealed = true; }
            monster.NonBlocking = false;
            // The origin stays in state 0: a joiner is created hidden and gets the 0 -> 2 change as a 0x2c8 (LegionStasis.Present), as live viewers did.
            done.Add(m);
        }
        if (done.Count == 0) return;
        lock (e)
        {
            if (e.Army is { } army)
            {
                var ids = done.Select(m => m.Id).ToHashSet();
                e.Army = [.. army.Select(m => ids.Contains(m.Id) ? m with { Revealed = true } : m)];
                if (keep) Keep(player, e);   // the spawn path (Standing) skips it: BeginArmy keeps once after Spawn
            }
        }
        await LegionOutput.ToArea(player, async p =>
        {
            foreach (var m in done) await LegionStasis.SendState(p, m.Id, m.Sequence, LegionStasis.Revealed, 100, null);
        });
    }

    /// <summary>The ruleset's start for the clients: its elapsed seconds since the shatter and the timer's length from it.</summary>
    static byte[]? RulesBegin(Player player, Encounter e)
    {
        double now = global::Game.AreaTime(player);
        float elapsed = (float)Math.Max(0, now - e.ChargeEnd), length = (float)Math.Max(0, e.EndsAt - e.ChargeEnd);
        return LegionRules.Begin(e.X, e.Y, (uint)e.Level, e.Id, elapsed, length);
    }

    static Task SendRules(Player to, byte[]? message, string what) => message is null ? Task.CompletedTask : PlayerOf(to).Send(message, what);

    /// <summary>A player who enters while the encounter runs: the ruleset (0x1f), and from the shatter its data (mode 0 with the elapsed time),
    /// to that player alone; the client's own check then reveals a member created after it.</summary>
    static async Task ShowRules(Player player, Encounter e)
    {
        Phase phase;
        lock (e) phase = e.Phase;
        if (phase is not (Phase.Charging or Phase.Running)) return;
        await SendRules(player, LegionRules.Add(), "0x001f ruleset LegionRules (joiner)");
        if (phase == Phase.Running) await SendRules(player, RulesBegin(player, e), "0x02c9 LegionRules mode 0 (joiner)");
    }
}
