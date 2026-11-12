

/// <summary>One accepted use, reserved before ordinary action side effects. Async continuations keep this original
/// witness; a later action may cancel it, but cancellation never refunds an accepted use.</summary>
public sealed record CooldownUseTicket(int Effect, int ActionToken, int ProjectileEpoch, bool Instant, bool Reserved = true,
    SingleUseCooldownState? State = null, int UseVersion = 0, Game.StanceUseReceipt? Stance = null)
{
    bool consumed;
    public IReadOnlyList<(SingleUseCooldownState State, int Version, ushort Skill)> SharedUses { get; init; } = [];
    // The cost paid when the use was accepted (mana, life), as the client pays it at the start of the use; null when the
    // admission paid nothing and the cast pays when its effect starts.
    public (uint Mana, uint Life)? Paid { get; init; }
    public bool Current(ClientState conn)
    {
        lock (conn.Monsters) return conn.ProjectileActive(ProjectileEpoch) && conn.PlayerLife > 0
            && (Instant || conn.ActionCount == ActionToken && conn.ElementalAilments.ActionFactor(conn.Clock.Elapsed.TotalSeconds) > 0 && !conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds))
            && (State is null || !Reserved || State.Uses > 1 || State.UseVersion == UseVersion)
            && (Stance is null || Stance.CurrentLocked(conn));
    }
    public bool CommitNativeUse(ClientState conn, ushort skillId)
    {
        lock (conn.Monsters)
        {
            if (!Current(conn)) return false;
            if (Reserved)
            {
                State?.ConfirmNativeUse(UseVersion, skillId, synchronizeProgress: Instant && !conn.LockstepClient);
                foreach (var (state, version, skill) in SharedUses)
                    state.ConfirmNativeUse(version, skill, synchronizeProgress: true);
            }
            return true;
        }
    }
    public bool TryExecute(ClientState conn, int effect)
    {
        lock (conn.Monsters)
        {
            if (!Reserved || consumed || Effect != effect || !Current(conn)) return false;
            consumed = true;
            return true;
        }
    }
}
