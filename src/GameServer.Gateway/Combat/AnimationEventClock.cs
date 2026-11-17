/// <summary>Maps a clip event to elapsed animation time using its duration, optional slow span and attack-time budget.
/// The caller selects the animation and whether the Actor permits slow playback outside that span.</summary>
public static class AnimationEventClock
{
    public static double? Seconds(int lengthMs, int eventMs, double periodSeconds, bool slowAnimationsGoToIdle,
        int? slowBeginMs = null, int? slowEndMs = null, double? budgetSeconds = null)
    {
        if (lengthMs <= 0 || !double.IsFinite(periodSeconds) || periodSeconds <= 0
            || budgetSeconds is { } invalidBudget && (!double.IsFinite(invalidBudget) || invalidBudget <= 0)) return null;
        double length = lengthMs / 1000d, at = Math.Clamp(eventMs, 0, lengthMs) / 1000d;
        bool clamped = slowAnimationsGoToIdle && periodSeconds > length;
        if (slowBeginMs is null && slowEndMs is null)
            return clamped ? at : at * periodSeconds / length;
        if (slowBeginMs is not { } beginMs || slowEndMs is not { } endMs || beginMs < 0
            || endMs <= beginMs || endMs > lengthMs) return null;

        double budget = budgetSeconds ?? length;
        // Native 0x141b0d420 restores linear playback when the requested duration fits the budget, even if Actor's
        // initial setup clamped a long animation. Otherwise only the marked span consumes the extra time first.
        if (periodSeconds <= budget) return at * periodSeconds / length;
        double begin = beginMs / 1000d, span = (endMs - beginMs) / 1000d;
        double budgetedSpan = budget * span / length;
        double playedSpan = Math.Min(8 * budgetedSpan, periodSeconds - budget + budgetedSpan);
        double inside = Math.Clamp(at - begin, 0, span), outside = at - inside;
        // An Actor clamped to natural speed leaves an idle tail after the eight-times cap; it does not distribute
        // the unused duration over the rest of the clip. A whole-clip span has no outside portion to divide by.
        return inside * playedSpan / span + (clamped ? outside
            : length > span ? outside * (periodSeconds - playedSpan) / (length - span) : 0);
    }
}
