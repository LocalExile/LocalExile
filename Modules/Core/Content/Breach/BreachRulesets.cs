using System.Buffers.Binary;

namespace GameServer.Modules.Breach;

/// <summary>
/// The Unstable Breach's 0x2c9 data (the ExpandingBrequelEncounterRules row's own message), written for a Breach of this
/// server. The body is 44 bytes: u8 opcode (0, an update: the reader 0x141686ef0 returns at 0x141687284 for any other),
/// u8 the Breach's index in the area, i32 x, i32 y (its cell), u8 the Initiator's encounter_state, seven f32, a zero
/// byte, and a final f32 150.0 (the arena's largest radius, in every line).
/// <para>The numbers were measured on an official map with three Breaches, two of them touched (172 and 173 state-1
/// lines, then their closing lines), and are computed here from what was measured:</para>
/// <list type="bullet">
/// <item>state 0 (idle): all seven 0.</item>
/// <item>state 1 (expanding), line <c>k</c> of the ramp (one every <see cref="BreachTables.RulesetGap"/> s from the
/// touch, <see cref="Steps"/> of them), with <c>g = min(1, k / (Steps - 1))</c> the arena's growth and <c>n</c> the
/// Breach's count (see <see cref="CountPerStep"/>): f32[0] the progress <see cref="Progress"/>(n), f32[1] the radius
/// 39 + 111 g, f32[2] 8 + 43 g - 0.033 n, then the constants 0.033, 0, -0.033, 0. The line at the full radius carries
/// f32[2] = <see cref="FullValue"/> instead.</item>
/// <item>state 2 (enraged): the latest state-1 line's numbers with f32[3] = f32[2].</item>
/// <item>state 3 (closing enraged): progress 100, radius 0, f32[2] 5, then 0.033, 0, -0.033, 0.</item>
/// <item>state 4 (collapsed): as state 3 with f32[3] = 5.</item>
/// </list>
/// <para>How close it comes to the measured lines (the progress and the count follow the players' kills, which this
/// server does not feed in): the radius within 5.8 cells, the progress within 11.1 (the two touched Breaches differ
/// from each other by up to 15 at the same radius), f32[2] within 4.3, every constant and the state 0 / 3 / 4 lines
/// exactly.</para>
/// </summary>
public static class BreachRulesets
{
    /// <summary>The body's length: 44 bytes.</summary>
    public const int Length = 44;

    /// <summary>How many lines the ramp has: 172 (measured: 172 and 173 state-1 lines on the two touched Breaches; this is
    /// the first one's). The last one is at the full radius.</summary>
    public const int Steps = 172;

    /// <summary>The arena's radius (cells) at the touch and at the full: 39 -&gt; 150 (measured: every first state-1 line
    /// carries 39.0, and the radius grows in steps of 111 / 5600 cell up to 150.16 and 150.02 on the last ones).</summary>
    public const float StartRadius = 39, FullRadius = 150;

    /// <summary>
    /// How much the Breach's count grows per line: 6.17. The count <c>n</c> is what f32[2] loses 0.033 for: every measured
    /// state-1 line before the full one satisfies f32[2] = 8 + 43 g - 0.033 n (g read from its radius) with a whole n to
    /// within 2e-5. It grows unevenly with the players' kills (measured: 986 over 171 lines and 1107 over 172, a jump of
    /// 45 and 51 on the first line after the touch), so this server stands in a steady rate, within 118 of either.
    /// </summary>
    public const double CountPerStep = 6.17;

    /// <summary>f32[2] at the full radius and in the enraged line: 4.0 (measured: 4.41 and 3.59).</summary>
    public const float FullValue = 4.0f;

    const float Rate = 0.033f;

    /// <summary>
    /// The progress (f32[0]) for the Breach's count: 20 n / (n + 190) + 0.086 n, worth less per count as it grows
    /// (measured: 0.161 per count over the first line, 0.087 near 100). It runs 0 -&gt; 107.7 over the ramp (measured:
    /// 0 -&gt; 103.5 and 0 -&gt; 112.2); given the measured lines' own n it is within 0.62 of their progress.
    /// </summary>
    public static double Progress(double count) => 20 * count / (count + 190) + 0.086 * count;

    /// <summary>The 44-byte body for the Breach at <paramref name="index"/> on cell (<paramref name="x"/>,
    /// <paramref name="y"/>) in <paramref name="state"/> (the Initiator's encounter_state); <paramref name="step"/> is the
    /// ramp's line (0 .. <see cref="Steps"/> - 1) for state 1 and the ramp's latest line for state 2.</summary>
    public static byte[] Body(byte index, int x, int y, int state, int step)
    {
        if (state is < BreachTables.Inactive or > BreachTables.Closing)
            throw new ArgumentOutOfRangeException(nameof(state), state, "an encounter_state 0 .. 4");
        byte[] body = new byte[Length];
        body[1] = index;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), x);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(6), y);
        body[10] = (byte)state;
        if (state != BreachTables.Inactive)
        {
            var (progress, radius, value) = state switch
            {
                BreachTables.ClosingEnraged or BreachTables.Closing => (100f, 0f, 5f),
                _ => Ramp(step),
            };
            float second = state is BreachTables.Enraged or BreachTables.Closing ? value : Rate;
            ReadOnlySpan<float> numbers = [progress, radius, value, second, 0, -Rate, 0];
            for (int i = 0; i < numbers.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(11 + 4 * i), numbers[i]);
        }
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(40), FullRadius);
        return body;
    }

    /// <summary>The ramp's line <paramref name="step"/>: progress, radius and f32[2].</summary>
    static (float Progress, float Radius, float Value) Ramp(int step)
    {
        int k = Math.Clamp(step, 0, Steps - 1);
        double growth = (double)k / (Steps - 1), count = CountPerStep * k;
        float radius = (float)(StartRadius + (FullRadius - StartRadius) * growth);
        float value = k == Steps - 1 ? FullValue : (float)(8 + 43 * growth - Rate * count);
        return ((float)Progress(count), radius, value);
    }
}
