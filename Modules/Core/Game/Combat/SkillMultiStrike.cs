/// <summary>Verified same-use multi-strike scheduling for player gem attacks.</summary>
public static class SkillMultiStrike
{
    /// <summary>
    /// The stat also occurs on skills with different hit cadence rules. Only Double Strike's documented same-target
    /// double hit is enabled here; other skills keep their existing path.
    /// </summary>
    public static int StrikeCount(string? effectId, int additionalHits, bool immediate, bool channelled, bool spell, bool wave) =>
        !immediate && !channelled && !spell && !wave
            && effectId is ("DoubleStrike" or "DoubleStrikeAltX" or "DoubleStrikeAltY" or "VaalDoubleStrike")
                ? 1 + Math.Max(0, additionalHits)
                : 1;

    /// <summary>Snapshot caller-level exclusions before a multi-strike use adds its first-strike victims.</summary>
    public static ISet<uint>? SnapshotPriorTargets(ISet<uint>? once, int strikeCount) =>
        strikeCount > 1 ? once?.ToHashSet() : null;

    public static bool WasHitBeforeUse(ISet<uint>? priorTargets, uint targetId) => priorTargets?.Contains(targetId) == true;
}
