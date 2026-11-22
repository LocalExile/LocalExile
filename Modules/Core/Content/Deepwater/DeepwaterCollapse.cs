using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>The lanterns' collapse (a Chart: after its last lantern; a Voyage: once the Bathysphere is called) and the
/// swarm that comes with it. The HUD's collapse state goes to 1 (0x2c9 mode 3, DeepwaterCollapseStart), the lanterns
/// shrink newest first (H order, the wiki): each gets <c>shrinking</c> = 1 (its flicker) and a new light task (0x2c3);
/// when its light reaches 0 it is <c>extinguished</c> and its task ends (0x2c5). The first to go out moves the HUD to
/// state 2 (M). The server's light follows the same floats, so drowning tracks the client's circle.
/// <para>G timings (3.29.0b: a slower first stage, a faster collapse after): the newest lantern's a falls at 1/20 per
/// second and its b from 1 to 0 over 4 s; each older lantern starts 10 s after the one before at 1/8 per second. A
/// Chart's own Bathysphere lantern stops at a = 1/3 (radius 30 once b is 0), so the Bathysphere stays reachable. The
/// swarm: one pack every 4 s (DeepwaterMonsters.Swarm), at most 30 per run.</para></summary>
internal static class DeepwaterCollapse
{
    const float FirstRate = 1 / 20f, LaterRate = 1 / 8f, FlickerRate = 1 / 4f;
    /// <summary>The held lantern's a: radius 10 + 100 · a · 0.6 = 30 once b is 0.</summary>
    const float FloorA = 1 / 3f;
    const double Between = 10, SwarmEvery = 4;
    const int MaxSwarmPacks = 30;

    /// <summary>Remaining seconds on the active collapse/descent mission timer, or 0 when inactive or ended.</summary>
    public static uint RemainingTimerSeconds(DeepwaterRun run, double now)
    {
        lock (run.Gate)
        {
            if (run.Phase == DeepwaterRun.RunPhase.Ended || run.CollapseState == 0 || run.CollapseEndAt <= now) return 0;
            return (uint)Math.Ceiling(run.CollapseEndAt - now);
        }
    }

    /// <summary>Starts the collapse of the lanterns that <paramref name="collapses"/> picks (a Voyage keeps the light at
    /// the called Bathysphere); <paramref name="floor"/> is the lantern that stops at the floor radius, if any. Once per
    /// run; false when it had started already.</summary>
    public static async Task<bool> Start(IModuleContext module, ClientState conn, DeepwaterRun run, Func<DeepwaterRun.Lantern, bool> collapses, uint? floor)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        List<(DeepwaterRun.Lantern Lantern, DeepwaterRun.Shrink Shrink)> order;
        uint timerSeconds;
        lock (run.Gate)
        {
            if (run.CollapseState != 0) return false;
            run.CollapseState = 1;
            order = [];
            int i = 0;
            foreach (var lantern in run.Lanterns.Where(collapses).OrderByDescending(l => l.Order))
            {
                var shrink = new DeepwaterRun.Shrink { Start = now + i * Between, Rate = i == 0 ? FirstRate : LaterRate, Floor = lantern.Id == floor };
                run.Shrinks[lantern.Id] = shrink;
                order.Add((lantern, shrink));
                i++;
            }
            double endAt = run.LandAt;
            bool hasExtinguishing = order.Any(o => !o.Shrink.Floor);
            foreach (var (lantern, shrink) in order)
            {
                if (hasExtinguishing && shrink.Floor) continue;
                var (a, _) = lantern.At(now);
                double targetA = shrink.Floor ? FloorA : 0;
                double duration = shrink.Rate > 0 ? Math.Max(0, a - targetA) / shrink.Rate : 0;
                endAt = Math.Max(endAt, shrink.Start + duration);
            }
            run.CollapseEndAt = endAt;
            timerSeconds = endAt > now ? (uint)Math.Ceiling(endAt - now) : 0;
            run.NextSwarm = now + SwarmEvery;
        }
        module.Log($"{CharacterName(conn)}: Allflame collapse: {order.Count} lanterns shrink newest first ("
            + string.Join(", ", order.Select(o => $"{o.Lantern.Order} at +{o.Shrink.Start - now:0} s{(o.Shrink.Floor ? " to the floor" : "")}")) + ")");
        foreach (var member in DeepwaterLoot.Members(conn))
        {
            if (DeepwaterRuleset.Collapse(1) is { } state)
                await PlayerOf(member).Send(state, "0x02c9 ruleset DeepwaterRules mode 3: collapse 1 (DeepwaterCollapseStart)");
            if (timerSeconds > 0)
                await PlayerOf(member).Send(InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Deepwater, timerSeconds),
                    $"0x0163 mission timer Deepwater: {timerSeconds} s");
        }
        await Tick(module, conn, run);
        return true;
    }

    /// <summary>One tick of a collapsing run: lanterns whose time came start shrinking, a held lantern stops at the floor,
    /// lanterns whose light reached 0 go out, and the swarm's next pack. Nothing while the run explores or after it ended;
    /// a few comparisons per lantern otherwise.</summary>
    public static async Task Tick(IModuleContext module, ClientState conn, DeepwaterRun run)
    {
        if (run.CollapseState == 0 || run.Phase == DeepwaterRun.RunPhase.Ended || conn.Closed || conn.LeavingArea) return;
        double now = conn.Clock.Elapsed.TotalSeconds;
        List<(DeepwaterRun.Lantern Lantern, string Kind, bool Collected)>? changes = null;
        bool firstOut = false, swarm = false;
        lock (run.Gate)
        {
            for (int i = 0; i < run.Lanterns.Count; i++)
            {
                var lantern = run.Lanterns[i];
                if (!run.Shrinks.TryGetValue(lantern.Id, out var shrink) || shrink.Out || shrink.Held || now < shrink.Start) continue;
                bool collected = run.Flared.Contains(lantern.Id);
                if (!shrink.Started)
                {
                    shrink.Started = true;
                    var (a, _) = lantern.At(now);
                    lantern = run.Lanterns[i] = lantern with { A = a, RateA = shrink.Rate, B = 1, RateB = FlickerRate, SetAt = now };
                    (changes ??= []).Add((lantern, "shrink", collected));
                    continue;
                }
                var (nowA, nowB) = lantern.At(now);
                if (shrink.Floor && nowA <= FloorA)
                {
                    shrink.Held = true;
                    lantern = run.Lanterns[i] = lantern with { A = FloorA, RateA = 0, B = nowB, RateB = nowB > 0 ? FlickerRate : 0, SetAt = now };
                    (changes ??= []).Add((lantern, "hold", collected));
                }
                else if (!shrink.Floor && nowA <= 0)
                {
                    shrink.Out = true;
                    lantern = run.Lanterns[i] = lantern with { A = 0, RateA = 0, B = 0, RateB = 0, SetAt = now };
                    (changes ??= []).Add((lantern, "out", collected));
                    if (run.CollapseState == 1)
                    {
                        run.CollapseState = 2;
                        firstOut = true;
                    }
                }
            }
            if (run.Phase is DeepwaterRun.RunPhase.Collapsing or DeepwaterRun.RunPhase.Descending or DeepwaterRun.RunPhase.Extractable
                && now >= run.NextSwarm && run.SwarmPacks < MaxSwarmPacks && conn.PlayerLife > 0)
            {
                run.NextSwarm = now + SwarmEvery;
                run.SwarmPacks++;
                swarm = true;
            }
        }
        if (changes is { Count: > 0 } || firstOut)
            foreach (var member in DeepwaterLoot.Members(conn))
            {
                if (changes is not null)
                    foreach (var (lantern, kind, collected) in changes)
                        switch (kind)
                        {
                            case "shrink":
                                await DeepwaterLanterns.SendStates(member, lantern, collected, shrinking: true, extinguished: false, "shrinking = 1");
                                await DeepwaterLanterns.SendTask(member, lantern, "the collapse");
                                break;
                            case "hold":
                                await DeepwaterLanterns.SendTask(member, lantern, "held at the floor (G)");
                                break;
                            default:
                                await DeepwaterLanterns.SendStates(member, lantern, collected, shrinking: true, extinguished: true, "extinguished = 1");
                                await DeepwaterLanterns.RemoveTask(member, lantern);
                                break;
                        }
                if (firstOut && DeepwaterRuleset.Collapse(2) is { } state)
                    await PlayerOf(member).Send(state, "0x02c9 ruleset DeepwaterRules mode 3: collapse 2 (the first lantern went out)");
            }
        if (changes is not null)
            foreach (var (lantern, kind, _) in changes)
                module.Log($"{CharacterName(conn)}: Allflame lantern {lantern.Order} 0x{lantern.Id:x} {kind} at +{now:0.0} s");
        if (swarm) await DeepwaterMonsters.Swarm(module, conn, run);
    }
}
