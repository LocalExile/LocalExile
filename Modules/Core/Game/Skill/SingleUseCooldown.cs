

/// <summary>One accepted use, reserved before ordinary action side effects. Async continuations keep this original
/// witness; a later action may cancel it, but cancellation never refunds an accepted use.</summary>
public sealed record CooldownUseTicket(int Effect, int ActionToken, int ProjectileEpoch, bool Instant, bool Reserved = true,
    SingleUseCooldownState? State = null, int UseVersion = 0, Game.StanceUseReceipt? Stance = null)
{
    bool consumed;
    public IReadOnlyList<(SingleUseCooldownState State, int Version, ushort Skill)> SharedUses { get; init; } = [];
    // True when an instant cooldown reset/prevention roll (0x50cd/0x52e2) kept the use unspent and needs a
    // PlayerSingleUseCooldowns sync after action acknowledgement to clear client cooldown prediction.
    public bool CooldownReset { get; init; }
    public int ResetBaseMilliseconds { get; init; }
    // The cost paid when the use was accepted (mana, life), as the client pays it at the start of the use; null when the
    // admission paid nothing and the cast pays when its effect starts.
    public (uint Mana, uint Life)? Paid { get; init; }
    /// <summary>The attack window reserved with this use, before movement or the native action echo.</summary>
    public (ushort Skill, double At, double Seconds)? Attack { get; init; }
    internal int? EnduranceChargesAfterCost { get; set; }
    public bool Current(Player player)
    {
        return player.ProjectileActive(ProjectileEpoch) && player.PlayerLife > 0
            && (Instant || player.ActionCount == ActionToken && player.ElementalAilments.ActionFactor(Game.AreaTime(player)) > 0 && !player.HitRecovery.Active(Game.AreaTime(player)))
            && (State is null || !Reserved || State.Uses > 1 || State.UseVersion == UseVersion)
            && (Stance is null || Stance.CurrentLocked(player));
    }
    public bool CommitNativeUse(Player player, ushort skillId)
    {
        if (!Current(player)) return false;
        if (Reserved)
        {
            if (CooldownReset)
                State?.SynchronizeNativeReset(skillId, ResetBaseMilliseconds);
            else
                State?.ConfirmNativeUse(UseVersion, skillId, synchronizeProgress: Instant && !player.Connection.LockstepClient);
            foreach (var (state, version, skill) in SharedUses)
                state.ConfirmNativeUse(version, skill, synchronizeProgress: true);
        }
        return true;
    }
    public bool TryExecute(Player player, int effect)
    {
        if (!Reserved || consumed || Effect != effect || !Current(player)) return false;
        consumed = true;
        return true;
    }
}
