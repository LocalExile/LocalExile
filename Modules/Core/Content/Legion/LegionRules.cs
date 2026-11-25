using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Legion;

/// <summary>
/// The client's LegionRules ruleset (Rulesets row 94; R7 section 2.2, R11 section 6): the encounter's HUD and its area audio (MiscAnimated 3355)
/// and the check that reveals a soldier created while the encounter runs. Added once per client by 0x1f, then its data in S->C 0x2c9, little-endian.
/// The encounter is named by the monolith's object id.
/// </summary>
static class LegionRules
{
    /// <summary>Encounter states and activity (R11): the state 1 is the pre-timer one; active 1 running, 2 finished; state 4 done.</summary>
    const byte Running = 1, Finished = 2, Timer = 2, Done = 4;

    /// <summary>The Rulesets row of LegionRules, from eagerly loaded <see cref="LegionData"/>; null when absent.</summary>
    static byte? Row => LegionData.Current.RulesetRow;

    /// <summary>S->C 0x1f: adds the ruleset to the client's world; null without the row.</summary>
    public static byte[]? Add() => Row is { } r ? InstanceProtocol.AddRuleset(r) : null;

    /// <summary>0x2c9 mode 0 (0x141731020): the encounter at (<paramref name="x"/>, <paramref name="y"/>) in grid cells, its area level, not endless, then
    /// its blob (0x141eb4750): alert mode 0, the monolith's id, active/stage, state/step, seconds elapsed, gauge 0, angle (f32) and the timer's length.</summary>
    public static byte[]? Begin(int x, int y, uint level, uint monolith, float elapsed, float timerSeconds,
        byte stage = Running, byte step = Timer, float angle = 0)
    {
        if (Row is not { } r) return null;
        var w = new BlobWriter().U8(0).I32(x).I32(y).U32(level).U8(0)
            .U8(0).U32(monolith).U8(stage).U8(step).F32(elapsed).F32(0).F32(angle).F32(timerSeconds);
        return InstanceRulesets.Data(r, w.ToArray());
    }

    /// <summary>0x2c9 mode 1: monolith activated (official capture format: 0x01, u32 monolith id).</summary>
    public static byte[]? Activated(uint monolith)
    {
        if (Row is not { } r) return null;
        return InstanceRulesets.Data(r, new BlobWriter().U8(1).U32(monolith).ToArray());
    }

    /// <summary>0x2c9 mode 5: encounter stage/step transition (1,1..1,3 during timer/release; 2,4 battle; 2,5 battle ended).</summary>
    public static byte[]? State(uint monolith, byte stage, byte step)
    {
        if (Row is not { } r) return null;
        return InstanceRulesets.Data(r, new BlobWriter().U8(5).U32(monolith).U8(stage).U8(step).ToArray());
    }

    /// <summary>0x2c9 mode 5: the encounter moved past its timer (stage 2, step 4).</summary>
    public static byte[]? End(uint monolith) => State(monolith, Finished, Done);

    /// <summary>S->C 0xdf environment override for the encounter; null when any requested row is missing from client.db.</summary>
    public static byte[]? Environments(params (ushort Row, double Seconds)[] entries)
    {
        if (entries.Length == 0 || entries.Any(e => e.Row == 0)) return null;
        return InstanceEnvironments.Data(entries);
    }
}
