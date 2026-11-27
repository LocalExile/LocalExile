using System.Buffers.Binary;

namespace GameServer.Modules.Breach;

/// <summary>
/// Breach ("Brequel") S-&gt;C <c>0x02c9</c> ruleset payloads:
/// <list type="bullet">
/// <item><c>ExpandingBrequelEncounterRules</c> (Rulesets#164): Unstable Breach arena ring, kill-driven progress, timer, and state.</item>
/// <item><c>EstablishedBrequelEncounterRules</c> (Rulesets#165): In-map Established Hive &amp; Hive Fortress wave/defense state.</item>
/// <item><c>BrequelBarrierRules</c> (Rulesets#166): Hive barrier / wall burn state.</item>
/// </list>
/// </summary>
public static class BreachRulesets
{
    /// <summary>The ExpandingBrequelEncounterRules body length: 44 bytes (reader 0x141686ef0).</summary>
    public const int Length = 44;

    /// <summary>How many lines the ramp has: 172 (measured: 172 and 173 state-1 lines on the two touched official Breaches).</summary>
    public const int Steps = 172;

    /// <summary>The arena's radius (cells) at the touch and at the full: 39 -&gt; 150.</summary>
    public const float StartRadius = 39, FullRadius = 150;

    /// <summary>Baseline count per ramp step when no kill feed is supplied.</summary>
    public const double CountPerStep = 6.17;

    /// <summary>f32[2] at the full radius and in the enraged line: 4.0.</summary>
    public const float FullValue = 4.0f;

    const float Rate = 0.033f;

    /// <summary>
    /// The progress (f32[0]) for the Breach's kill/count score: 20 n / (n + 190) + 0.086 n.
    /// Reaches ~100 at n ~= 965 (or scaled kill score).
    /// </summary>
    public static double Progress(double count) => count <= 0 ? 0 : 20 * count / (count + 190) + 0.086 * count;

    /// <summary>Original step-based 44-byte ExpandingBrequelEncounterRules body (kept for backward compatibility).</summary>
    public static byte[] Body(byte index, int x, int y, int state, int step)
    {
        var (progress, radius, value) = state switch
        {
            BreachTables.Inactive => (0f, 0f, 0f),
            BreachTables.ClosingEnraged or BreachTables.Closing => (100f, 0f, 5f),
            _ => Ramp(step),
        };
        return Body(index, x, y, state, progress, radius, value, FullRadius);
    }

    /// <summary>Kill-driven 44-byte ExpandingBrequelEncounterRules body (reader 0x141686ef0):
    /// u8 opcode (0), u8 index, i32 x, i32 y, u8 encounter_state, 7 x f32 (progress, radius, value, second, 0, -0.033, 0),
    /// u8 0, f32 maxRadius.</summary>
    public static byte[] Body(byte index, int x, int y, int state, float progress, float radius, float timerValue, float maxRadius = FullRadius)
    {
        if (state is < BreachTables.Inactive or > BreachTables.Closing)
            throw new ArgumentOutOfRangeException(nameof(state), state, "an encounter_state 0 .. 4");
        byte[] body = new byte[Length];
        body[0] = 0; // update opcode
        body[1] = index;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), x);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(6), y);
        body[10] = (byte)state;
        if (state != BreachTables.Inactive)
        {
            float p = state is BreachTables.ClosingEnraged or BreachTables.Closing ? 100f : Math.Clamp(progress, 0f, 100f);
            float r = state is BreachTables.ClosingEnraged or BreachTables.Closing ? 0f : Math.Clamp(radius, 0f, maxRadius);
            float v = state is BreachTables.ClosingEnraged or BreachTables.Closing ? 5f : Math.Max(0f, timerValue);
            float second = state is BreachTables.Enraged or BreachTables.Closing ? v : Rate;
            ReadOnlySpan<float> numbers = [p, r, v, second, 0f, -Rate, 0f];
            for (int i = 0; i < numbers.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(11 + 4 * i), numbers[i]);
        }
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(40), maxRadius);
        return body;
    }

    /// <summary>Computes kill-driven ExpandingBrequelEncounterRules metrics:
    /// <paramref name="growth"/> in [0, 1] is the arena's radial growth; <paramref name="killCount"/> is the weighted
    /// kill score plus baseline ramp contribution.</summary>
    public static (float Progress, float Radius, float Value) KillDrivenMetrics(double growth, double killCount, float maxRadius = FullRadius)
    {
        double g = Math.Clamp(growth, 0.0, 1.0);
        float radius = (float)(StartRadius + (maxRadius - StartRadius) * g);
        float progress = (float)Math.Clamp(Progress(killCount), 0.0, 100.0);
        float value = g >= 0.999 || progress >= 99.9f
            ? FullValue
            : (float)Math.Max(1.0, 8.0 + 43.0 * g - Rate * killCount);
        return (progress, radius, value);
    }

    static (float Progress, float Radius, float Value) Ramp(int step)
    {
        int k = Math.Clamp(step, 0, Steps - 1);
        double growth = (double)k / (Steps - 1), count = CountPerStep * k;
        float radius = (float)(StartRadius + (FullRadius - StartRadius) * growth);
        float value = k == Steps - 1 ? FullValue : (float)(8 + 43 * growth - Rate * count);
        return ((float)Progress(count), radius, value);
    }

    // ---- EstablishedBrequelEncounterRules (#165) & BrequelBarrierRules (#166) ----------------------------------------
    // Guarded builders: only send 0x02c9 payloads when binary-verified wire format is enabled via VerifiedEstablishedWire /
    // VerifiedBarrierWire (updated immediately when @re-protocol Drop 2 lands) so we never send unverified bytes that
    // could desync the client's 0x02c9 reader!

    /// <summary>True once @re-protocol Drop 2 confirms exact 0x02c9 wire layout for Rulesets#165.</summary>
    public static bool EstablishedWireVerified { get; set; }

    /// <summary>True once @re-protocol Drop 2 confirms exact 0x02c9 wire layout for Rulesets#166.</summary>
    public static bool BarrierWireVerified { get; set; }

    /// <summary>Optional custom encoder hook for EstablishedBrequelEncounterRules (#165) once verified.</summary>
    public static Func<int, int, int, int, int, byte[]?>? BuildEstablishedPayload { get; set; }

    /// <summary>Optional custom encoder hook for BrequelBarrierRules (#166) once verified.</summary>
    public static Func<int, int, int, byte[]?>? BuildBarrierPayload { get; set; }

    /// <summary>Encodes an EstablishedBrequelEncounterRules (#165) 0x02c9 message when wire format is verified; returns null otherwise.</summary>
    public static byte[]? EstablishedMessage(int state, int wave, int totalWaves, int hitsTaken, int maxHits)
    {
        if (!EstablishedWireVerified || BreachTables.EstablishedRulesetRow is not { } row) return null;
        if (BuildEstablishedPayload?.Invoke(state, wave, totalWaves, hitsTaken, maxHits) is not { } body) return null;
        return GameServer.Core.Protocol.Instance.InstanceRulesets.Data(row, body);
    }

    /// <summary>Encodes a BrequelBarrierRules (#166) 0x02c9 message when wire format is verified; returns null otherwise.</summary>
    public static byte[]? BarrierMessage(int state, int x, int y)
    {
        if (!BarrierWireVerified || BreachTables.BarrierRulesetRow is not { } row) return null;
        if (BuildBarrierPayload?.Invoke(state, x, y) is not { } body) return null;
        return GameServer.Core.Protocol.Instance.InstanceRulesets.Data(row, body);
    }
}
