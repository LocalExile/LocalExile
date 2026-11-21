

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
    /// <summary>The attack window reserved with this use, before movement or the native action echo.</summary>
    public (ushort Skill, double At, double Seconds)? Attack { get; init; }
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
