sealed partial class ClientState
{
    internal RampageSession Rampage { get; } = new();
    internal ITransientCombatState? OwnedCombatState;
    internal ConnectionWorker OwnedCombatWorker { get; } = new("owned-combat");
    internal HashSet<uint> RetiredCombatObjects { get; } = [];
    internal HashSet<(uint Actor, ushort Buff)> RetiredCombatBuffs { get; } = [];
    internal RampageState.Value SentRampage { get; set; }
    internal ushort RampageBuff { get; set; }
    internal double? RampageTransferAt;
    internal double VaalPreventionUntil;
    internal bool SentVaalPrevention;
    internal object? VaalUseState;

    internal void RetireOwnedCombat()
    {
        lock (Monsters)
        {
            OwnedCombatWorker.Retire();
            if (OwnedCombatState is { } old)
            {
                old.Clear();
                RetiredCombatObjects.UnionWith(old.RetiredObjects);
                RetiredCombatBuffs.UnionWith(old.RetiredBuffs);
                foreach (uint actor in old.RetiredObjects)
                    if (Monsters.Remove(actor, out var removed)) removed.Dead = true;
                foreach (var (actor, _) in old.RetiredBuffs)
                    if (Monsters.TryGetValue(actor, out var monster)) monster.RampageBlindUntil = 0;
            }
            OwnedCombatState = null;
            VaalUseState = null;
            if (RampageBuff != 0) RetiredCombatBuffs.Add((Program.PlayerId, RampageBuff));
            RampageBuff = 0;
            SentRampage = default;
        }
    }

    internal void ApplyMovementSpeedAt(double at)
    {
        lock (Monsters)
            Walk.SetSpeed(Math.Max(1, Program.WalkSpeed * (100L + Pools.MovementVelocityPct
                + Rampage.Current.MovementPercent) / 100.0 * (Program.GameLogicOrNull?.PlayerActionFactor(this)
                    ?? ElementalAilments.ActionFactor(at))), at);
    }
}
