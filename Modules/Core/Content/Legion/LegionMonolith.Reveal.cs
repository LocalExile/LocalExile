using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>
/// The reveal: the army stands hidden (LegionBreakout state 0, present=0, no body) until the monolith shatters; then a 0x2c8
/// state 2 and StateMachine present=1 show each member, nearest the monolith first (R10 section 1), and the LegionRules
/// ruleset gives the client its HUD and area audio.
/// </summary>
static partial class LegionMonolith
{
    /// <summary>The pulse ring's clip (monolith_pulse.ao, 0.791 s) and the radius it reaches at scale 1 (R10: the mesh is +-267 units, about 24.6 cells).
    /// GUESS: a larger scale stretches the ring, not the clip's duration (live check).</summary>
    const double PulseSeconds = 0.791, PulseRadiusCells = 24.6;
    /// <summary>The largest scale the ring is sent with (a ring of 14 x 24.6 = 345 cells). GUESS: the client renders the scaled ring.</summary>
    const float PulseMaxScale = 14;
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
        bool running;
        lock (e)
        {
            running = e.Phase == Phase.Running;   // the encounter may have ended during the awaits before
            if (running) { e.Pulse = pulse; e.PulseScale = scale; Keep(player, e); }   // together: a joiner gets the pulse with its scale
        }
        if (!running) return;
        var instance = player.Instance;
        if (InArea(player, e.Id))
        {
            await ShowPulse(player, e, pulse);
            // Remove the shatter pulse once its clip finishes so late joiners mid-encounter do not replay the shatter wave.
            global::Game.SendAfter(player, PulseSeconds + 0.5, async () =>
            {
                var runner = ResolveRunner(player, instance);
                lock (e) { if (e.Pulse == pulse) { e.Pulse = 0; if (runner is not null) Keep(runner, e); } }
                if (runner is not null && InArea(runner, pulse)) await LegionOutput.Remove(runner, pulse);
            });
        }
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
                var runner = ResolveRunner(player, instance);
                if (runner is null) return;
                lock (e) { if (e.Phase != Phase.Running) return; }   // the encounter ended meanwhile
                await RevealMembers(module, runner, e, members);
                module.Log($"reveal step {k} at +{(global::Game.AreaTime(runner) - now):0.00}s: {members.Count} members");
            }
            if (delay <= 0) await Run();
            else global::Game.SendAfter(player, delay, Run);   // seconds of the area's time, rounded up to whole 33 ms frames
        }
        module.Log($"army reveal: {distances.Count} members in {steps.Count} steps over {PulseSeconds} s, pulse scale {scale:0.00} (farthest {farthest:0.0} cells)");
    }

    /// <summary>Reveals members (once each): the body is theirs again, the non-lethal combat floor is armed so all damage
    /// sources count, StateMachine present=1 is set, and every client gets the 0x2c8 state 2; the action origin stays in
    /// state 0 for a joiner (LegionStasis.Present).</summary>
    static async Task RevealMembers(IModuleContext module, Player player, Encounter e, IReadOnlyList<LegionStasis.Member> members, bool keep = true)
    {
        if (player.Instance is null) return;
        var done = new List<(LegionStasis.Member Member, StateMachineData? States, int StateComponent)>(members.Count);
        foreach (var m in members)
        {
            if (!player.Area.Monsters.TryGetValue(m.Id, out var monster) || monster.Dead || !Holds.TryGetValue(monster, out var hold)) continue;
            bool broken;
            lock (hold) { if (hold.Revealed) continue; hold.Revealed = true; broken = hold.Broken; }
            monster.NonBlocking = false;
            if (!broken) ArmFloor(module, player, hold, monster);
            var states = (monster.States ?? monster.Variety?.StateMachine)?.Set("present", 1);
            int component = -1;
            if (states is not null)
            {
                monster.States = states;
                if (player.Instance.World.MonsterOrigins.TryGetValue(m.Id, out var origin))
                    player.Instance.World.MonsterOrigins[m.Id] = origin with { Object = origin.Object with { StateMachine = states } };
                component = monster.Variety?.ComponentsOrBase.ToList().IndexOf("StateMachine") ?? -1;
            }
            // The action origin stays in state 0: a joiner is created hidden and gets the 0 -> 2 change as a 0x2c8 (LegionStasis.Present), as live viewers did.
            done.Add((m, states, component));
        }
        if (done.Count == 0) return;
        lock (e)
        {
            if (e.Army is { } army)
            {
                var ids = done.Select(d => d.Member.Id).ToHashSet();
                e.Army = [.. army.Select(m => ids.Contains(m.Id) ? m with { Revealed = true } : m)];
                if (keep) Keep(player, e);   // the spawn path (Standing) skips it: BeginArmy keeps once after Spawn
            }
        }
        // The reward symbols and the minimap icon appear with the reveal, not before: the area's components write them to every client
        // (and to one that joins later), once.
        foreach (var (m, _, _) in done) LegionStasis.ShowRewards(player, m.Id, m.Rewards, true);
        await LegionOutput.ToArea(player, async p =>
        {
            foreach (var (m, states, component) in done)
            {
                if (states is not null && component >= 0)
                    await global::Game.SendWorldRecord(PlayerOf(p), m.Id, InstanceEntities.StateMachineUpdate(m.Id, (byte)component, states), null);
                await LegionStasis.SendState(p, m.Id, m.Sequence, LegionStasis.Revealed, 100, null);
            }
        });
    }

    /// <summary>The ruleset's mode 0 message for a client: its elapsed seconds since the shatter (0 before shatter),
    /// timer length, alert mode/gauge (Timeless Conflict) and monolith angle.</summary>
    static byte[]? RulesBegin(Player player, Encounter e, byte stage = 0, byte step = 0)
    {
        double now = global::Game.AreaTime(player);
        double length = double.IsFinite(e.EndsAt) && e.EndsAt > e.ChargeEnd
            ? e.EndsAt - e.ChargeEnd
            : TimerMilliseconds(LegionData.Current, e.Level, AreaStats(player).GetValueOrDefault(Stat.MapLegionDurationPlusPct)) / 1000.0;
        float elapsed = e.Phase == Phase.Running ? (float)Math.Clamp(now - e.ChargeEnd, 0, length) : 0f;
        float gauge = e.AlertMode && e.AlertMax > 0 ? Math.Clamp((float)e.AlertGauge / e.AlertMax, 0f, 1f) : 0f;
        return LegionRules.Begin(e.X, e.Y, (uint)e.Level, e.Id, elapsed, (float)length, stage, step, e.Rotation, alertMode: e.AlertMode, gauge: gauge);
    }

    static Task SendRules(Player to, byte[]? message, string what) => message is null ? Task.CompletedTask : PlayerOf(to).Send(message, what);

    /// <summary>A player who enters while the encounter has not finished: the ruleset (0x1f), mode 0, activated (mode 1),
    /// latest stage/step (mode 5) and active 0xdf environment override, to that player alone.</summary>
    static async Task ShowRules(Player player, Encounter e)
    {
        Phase phase; bool activated; byte ruleStage, ruleStep; ushort[]? envs;
        lock (e) (phase, activated, ruleStage, ruleStep, envs) = (e.Phase, e.RuleActivated, e.RuleStage, e.RuleStep, e.Environments);
        if (phase == Phase.Finished) return;
        await SendRules(player, LegionRules.Add(), "0x001f ruleset LegionRules (joiner)");
        await SendRules(player, RulesBegin(player, e, stage: 0, step: 0), "0x02c9 LegionRules mode 0 (joiner)");
        if (activated) await SendRules(player, LegionRules.Activated(e.Id), "0x02c9 LegionRules mode 1 (joiner)");
        if (ruleStage > 0) await SendRules(player, LegionRules.State(e.Id, ruleStage, ruleStep), $"0x02c9 LegionRules state ({ruleStage},{ruleStep}) (joiner)");
        if (envs is { Length: > 0 } && LegionRules.Environments([.. envs.Select(r => (r, 0.0))]) is { } envPacket)
            await PlayerOf(player).Send(envPacket, "0x00df Legion environment (joiner)");
    }
}
