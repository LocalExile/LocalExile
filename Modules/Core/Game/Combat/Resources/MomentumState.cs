// One player's ordinary Momentum uses. Owned and retired by ResourceEffectsState, with explicit game times.
sealed class MomentumState
{
    internal readonly record struct Settings(int Maximum, double SwiftSeconds, int MovementPerStack);
    internal readonly record struct PaidUse(int Action, ushort Skill, Settings Settings);
    internal int Count { get; private set; }
    internal double SwiftUntil { get; private set; }
    internal int SwiftMovement { get; private set; }
    internal long Revision, SentRevision;
    internal ushort Buff, SwiftBuff;
    internal int SentCount, SentMovement;
    internal double SentSwiftUntil;
    internal PaidUse? Pending;
    int acknowledgedAction = -1, consumedAction = -1;
    ushort acknowledgedSkill, consumedSkill;

    internal void Paid(PaidUse use, double now)
    {
        if (consumedAction == use.Action && consumedSkill == use.Skill) return;
        Pending = use;
        if (acknowledgedAction == use.Action && acknowledgedSkill == use.Skill) Commit(now);
    }

    internal void Acknowledge(int action, ushort skill, double now)
    {
        acknowledgedAction = action; acknowledgedSkill = skill;
        if (Pending is { } paid && paid.Action == action && paid.Skill == skill) Commit(now);
        else Pending = null;
    }

    void Commit(double now)
    {
        var use = Pending!.Value;
        Pending = null;
        consumedAction = use.Action; consumedSkill = use.Skill;
        if (use.Settings.Maximum <= 0) return;
        Count++;
        if (Count >= use.Settings.Maximum)
        {
            SwiftMovement = Count * use.Settings.MovementPerStack;
            SwiftUntil = now + use.Settings.SwiftSeconds;
            Count = 0;
        }
        Revision++;
    }

    internal void Moved()
    {
        Pending = null;
        if (Count == 0) return;
        Count = 0; Revision++;
    }

    internal int MovementAt(double now)
    {
        if (SwiftUntil != 0 && now >= SwiftUntil)
        { SwiftUntil = 0; SwiftMovement = 0; Revision++; }
        return SwiftUntil > now ? SwiftMovement : 0;
    }

    internal void Clear()
    {
        Count = 0; SwiftUntil = 0; SwiftMovement = 0; Pending = null;
        acknowledgedAction = consumedAction = -1; acknowledgedSkill = consumedSkill = 0;
        Buff = SwiftBuff = 0; SentCount = SentMovement = 0; SentSwiftUntil = 0;
        Revision++;
    }
}
