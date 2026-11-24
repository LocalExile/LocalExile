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
    /// <summary>The pulse ring's clip (monolith_pulse.ao, 0.791 s) and the radius it reaches at scale 1 (R10: the mesh is +-267 units, about 24.6 cells).
    /// GUESS: a larger scale stretches the ring, not the clip's duration (live check).</summary>
    const double PulseSeconds = 0.791, PulseRadiusCells = 24.6;
    /// <summary>The largest scale the ring is sent with (a ring of 8 x 24.6 = 197 cells).</summary>
    const float PulseMaxScale = 8;
    /// <summary>How many steps the reveal has: members are grouped by when the ring reaches them (one area timer per step; a step is a server frame, 33 ms, at the finest).</summary>
    const int RevealSteps = 16;

    /// <summary>The shatter: the pulse ring is created, scaled to reach the farthest member, and each member is revealed as the ring reaches it:
    /// after distance / ring radius x <see cref="PulseSeconds"/>. The pulse object and the first step go out in the same frame.</summary>
    static async Task RevealArmy(IModuleContext module, Player player, Encounter e, uint pulse)
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
        double farthest = distances.Count == 0 ? 0 : distances.Max(d => d.Distance);
        float scale = (float)Math.Clamp(farthest / PulseRadiusCells, 1, PulseMaxScale);
        lock (e) { e.PulseScale = scale; Keep(player, e); }
        if (InArea(player, e.Id)) await ShowPulse(player, e, pulse);
        if (distances.Count == 0) return;
        double ring = PulseRadiusCells * scale;
        var steps = distances.GroupBy(d => Math.Min(RevealSteps - 1, (int)(d.Distance / ring * RevealSteps))).OrderBy(g => g.Key).ToList();
        foreach (var step in steps)
        {
            var members = step.Select(d => d.Member).ToList();
            int k = step.Key;
            double delay = PulseSeconds * k / RevealSteps;
            async Task Run()
            {
                await RevealMembers(player, e, members);
                module.Log($"reveal step {k} at +{(global::Game.AreaTime(player) - now):0.00}s: {members.Count} members");
            }
            if (delay <= 0) await Run();
            else global::Game.SendAfter(player, delay, Run);   // seconds of the area's time, rounded up to whole 33 ms frames
        }
        module.Log($"army reveal: {distances.Count} members in {steps.Count} steps over {PulseSeconds} s, pulse scale {scale:0.00} (farthest {farthest:0.0} cells)");
    }

    /// <summary>Reveals members (once each): the body is theirs again, hits count (the break-out handler), and every client gets the 0x2c8 state 2; the origin stays in state 0 for a joiner (LegionStasis.Present).</summary>
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
