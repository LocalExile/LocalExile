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

    static byte? row;
    static bool looked;

    /// <summary>The Rulesets row of LegionRules, from the client tables (94); null when the table has none.</summary>
    static byte? Row
    {
        get
        {
            if (!looked)
            {
                row = ClientTables.TableOrEmpty("Rulesets").FirstOrDefault(r => r.Text("Id") == "LegionRules") is { Table: not null } r && r.Index <= byte.MaxValue
                    ? (byte)r.Index : null;
                looked = true;
            }
            return row;
        }
    }

    /// <summary>S->C 0x1f: adds the ruleset to the client's world; null without the row.</summary>
    public static byte[]? Add() => Row is { } r ? InstanceProtocol.AddRuleset(r) : null;

    /// <summary>0x2c9 mode 0 (0x141731020): the encounter at (<paramref name="x"/>, <paramref name="y"/>) in grid cells, its area level, not endless, then
    /// its blob (0x141eb4750): alert mode 0, the monolith's id, active 1, state 2 (timer), the seconds elapsed, the gauge 0, an unknown float 0 and the timer's length.</summary>
    // LIVE CHECK: the state is 2 (timer) as R11 says for a running encounter; state 1 is the pre-timer one.
    public static byte[]? Begin(int x, int y, uint level, uint monolith, float elapsed, float timerSeconds)
    {
        if (Row is not { } r) return null;
        var w = new BlobWriter().U8(0).I32(x).I32(y).U32(level).U8(0)
            .U8(0).U32(monolith).U8(Running).U8(Timer).F32(elapsed).F32(0).F32(0).F32(timerSeconds);
        return InstanceRulesets.Data(r, w.ToArray());
    }

    /// <summary>0x2c9 mode 5: the encounter is finished (active 2, state 4): the HUD and the area audio go.</summary>
    public static byte[]? End(uint monolith)
    {
        if (Row is not { } r) return null;
        return InstanceRulesets.Data(r, new BlobWriter().U8(5).U32(monolith).U8(Finished).U8(Done).ToArray());
    }
}
