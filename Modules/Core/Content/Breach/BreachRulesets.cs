using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Breach;

/// <summary>
/// Binary-verified Breach ("Brequel") S-&gt;C <c>0x02c9</c> ruleset payloads (3.29.3c/d):
/// <list type="bullet">
/// <item><c>ExpandingBrequelEncounterRules</c> (Rulesets#164 / <c>0xa4</c>, reader <c>0x141686ef0</c>): 44B Unstable Breach ring, progress, and state.</item>
/// <item><c>EstablishedBrequelEncounterRules</c> (Rulesets#165 / <c>0xa5</c>, reader <c>0x141686140</c>): 4 sub-opcodes driving the native Hive &amp; Hive Fortress HUD (state, wave counter, Ailith defense/shield bar, overlay blend).</item>
/// <item><c>BrequelBarrierRules</c> (Rulesets#166 / <c>0xa6</c>, reader <c>0x14167e790</c>): 2 sub-opcodes driving the Hive barrier radius &amp; fade.</item>
/// </list>
/// Also decodes <c>C-&gt;S 0x02ca</c> ruleset <c>0xa5</c> (<c>BrequelActivateEncounterSkill</c>, 5B payload: <c>u8 0x00, u32 LE skillObjectEntityId</c>).
/// </summary>
public static class BreachRulesets
{
    /// <summary>The ExpandingBrequelEncounterRules body length: 44 bytes (reader 0x141686ef0).</summary>
    public const int Length = 44;

    /// <summary>How many lines the ramp has: 172.</summary>
    public const int Steps = 172;

    /// <summary>The arena's radius (cells) at the touch and at full: 39 -&gt; 150.</summary>
    public const float StartRadius = 39, FullRadius = 150;

    /// <summary>Baseline count per ramp step when no kill feed is supplied.</summary>
    public const double CountPerStep = 6.17;

    /// <summary>f32[2] at the full radius and in the enraged line: 4.0.</summary>
    public const float FullValue = 4.0f;

    const float Rate = 0.033f;

    /// <summary>Wire states for <c>EstablishedBrequelEncounterRules</c> (#165) subOp 0.</summary>
    public const uint EstablishedInactive = 0;
    public const uint EstablishedActive = 1;
    public const uint EstablishedFortressEscape = 2;
    public const uint EstablishedSucceeded = 3;
    public const uint EstablishedSilentEnd = 4;
    public const uint EstablishedFailed = 5;

    // ---- ExpandingBrequelEncounterRules (#164 = 0xa4) ----------------------------------------------------------------

    /// <summary>The progress (f32[0]) for the Breach's kill/count score: 20 n / (n + 190) + 0.086 n.</summary>
    public static double Progress(double count) => count <= 0 ? 0 : 20 * count / (count + 190) + 0.086 * count;

    /// <summary>Original step-based 44-byte ExpandingBrequelEncounterRules body.</summary>
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
    /// u8 0, u8 breachIndex, i32 LE x, i32 LE y, u8 state, ExpandingRadius (29B), f32 LE maxRadius.</summary>
    public static byte[] Body(byte index, int x, int y, int state, float progress, float radius, float timerValue, float maxRadius = FullRadius)
    {
        if (state is < BreachTables.Inactive or > BreachTables.Closing)
            throw new ArgumentOutOfRangeException(nameof(state), state, "an encounter_state 0 .. 4");
        byte[] body = new byte[Length];
        body[0] = 0;
        body[1] = index;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), x);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(6), y);
        body[10] = (byte)state;
        if (state != BreachTables.Inactive)
        {
            float p = state is BreachTables.ClosingEnraged or BreachTables.Closing ? 100f : Math.Max(0f, progress);
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

    // ---- Shared 29-byte ExpandingRadius struct (0x141fbe050) ---------------------------------------------------------

    /// <summary>Writes the 29-byte <c>ExpandingRadius</c> struct (<c>0x141fbe050</c>):
    /// <c>f32 startRadius, f32 targetRadius, f32 duration, f32 elapsed, i32 LE speedPercent, f32 holdTimer, f32 param14, u8 flag</c>.</summary>
    public static void WriteExpandingRadius(Span<byte> dest, float startRadius, float targetRadius, float duration,
        float elapsed, int speedPercent = 0, float holdTimer = 0f, float param14 = 0f, byte flag = 0)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dest[0..4], startRadius);
        BinaryPrimitives.WriteSingleLittleEndian(dest[4..8], targetRadius);
        BinaryPrimitives.WriteSingleLittleEndian(dest[8..12], duration);
        BinaryPrimitives.WriteSingleLittleEndian(dest[12..16], elapsed);
        BinaryPrimitives.WriteInt32LittleEndian(dest[16..20], speedPercent);
        BinaryPrimitives.WriteSingleLittleEndian(dest[20..24], holdTimer);
        BinaryPrimitives.WriteSingleLittleEndian(dest[24..28], param14);
        dest[28] = flag;
    }

    // ---- EstablishedBrequelEncounterRules (#165 = 0xa5, reader 0x141686140) ------------------------------------------

    /// <summary>Sub-opcode 0 (14B): encounter state (0=Inactive, 1=Active, 2=FortressEscape, 3=Succeeded, 4=SilentEnd,
    /// 5=Failed), arena centre (HUD shown within 210 grid units), and <paramref name="isSkyFortress"/>.</summary>
    public static byte[]? EstablishedState(uint encounterState, int centerX, int centerY, bool isSkyFortress)
    {
        if (BreachTables.EstablishedRulesetRow is not { } row) return null;
        byte[] body = new byte[14];
        body[0] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1, 4), encounterState);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5, 4), centerX);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(9, 4), centerY);
        body[13] = isSkyFortress ? (byte)1 : (byte)0;
        return InstanceRulesets.Data(row, body);
    }

    /// <summary>Sub-opcode 1 (3B): wave counter (<c>Wave {currentWave}/{totalWaves}</c>; hidden when totalWaves == 0).</summary>
    public static byte[]? EstablishedWave(int currentWave, int totalWaves)
    {
        if (BreachTables.EstablishedRulesetRow is not { } row) return null;
        byte[] body = [1, (byte)Math.Clamp(currentWave, 0, 255), (byte)Math.Clamp(totalWaves, 0, 255)];
        return InstanceRulesets.Data(row, body);
    }

    /// <summary>Sub-opcode 2 (10B): Ailith defense bar (<c>clamp(remainingHits / maxHits, 0, 1)</c>) and shield state.</summary>
    public static byte[]? EstablishedHealth(int remainingHits, int maxHits, bool isShielded)
    {
        if (BreachTables.EstablishedRulesetRow is not { } row) return null;
        byte[] body = new byte[10];
        body[0] = 2;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1, 4), Math.Max(0, remainingHits));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5, 4), Math.Max(1, maxHits));
        body[9] = isShielded ? (byte)1 : (byte)0;
        return InstanceRulesets.Data(row, body);
    }

    /// <summary>Sub-opcode 3 (2B): Hive environment overlay blend (1 = active, 0 = clear).</summary>
    public static byte[]? EstablishedOverlay(bool active)
    {
        if (BreachTables.EstablishedRulesetRow is not { } row) return null;
        return InstanceRulesets.Data(row, [3, active ? (byte)1 : (byte)0]);
    }

    /// <summary>Decodes <c>C-&gt;S 0x02ca</c> ruleset <c>0xa5</c> (<c>BrequelActivateEncounterSkill</c>):
    /// 5B payload <c>u8 0x00, u32 LE skillObjectEntityId</c>.</summary>
    public static bool TryReadActivateSkillAction(ReadOnlySpan<byte> payload, out uint skillObjectEntityId)
    {
        if (payload.Length >= 5 && payload[0] == 0x00)
        {
            skillObjectEntityId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
            return true;
        }
        skillObjectEntityId = 0;
        return false;
    }

    // ---- BrequelBarrierRules (#166 = 0xa6, reader 0x14167e790) -------------------------------------------------------

    /// <summary>Sub-opcode 0 (38B): barrier centre and 29B <c>ExpandingRadius</c>.</summary>
    public static byte[]? BarrierRadius(int centerX, int centerY, float startRadius, float targetRadius,
        float duration = 1.0f, float elapsed = 0f, int speedPercent = 0)
    {
        if (BreachTables.BarrierRulesetRow is not { } row) return null;
        byte[] body = new byte[38];
        body[0] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1, 4), centerX);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5, 4), centerY);
        WriteExpandingRadius(body.AsSpan(9, 29), startRadius, targetRadius, duration, elapsed, speedPercent);
        return InstanceRulesets.Data(row, body);
    }

    /// <summary>Sub-opcode 1 (2B): barrier fade float active toggle (1 = visible, 0 = fade out).</summary>
    public static byte[]? BarrierActive(bool active)
    {
        if (BreachTables.BarrierRulesetRow is not { } row) return null;
        return InstanceRulesets.Data(row, [1, active ? (byte)1 : (byte)0]);
    }
}
