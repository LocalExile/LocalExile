sealed partial class ClientState
{
    internal RampageSession Rampage { get; } = new();
    internal SoulEaterState SoulEater { get; } = new();
    // "Gain Physical Damage Immunity for X seconds on Rampage" and "Phasing for X seconds on Rampage" thresholds.
    internal double PhysicalImmunityUntil;
    internal double PhasingUntil;
    internal bool IsPhasing => Phasing || PhasingUntil > Clock.Elapsed.TotalSeconds;
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
    // Close Combat's Combat Rush: more Attack Speed for a travel skill (GemSupports.Rules.CombatRushOnHit).
    internal double CombatRushUntil;
    internal int CombatRushAttackSpeed;
    internal (int Action, ushort Skill, int Speed)? CombatRushUse;
    // Fist of War: per skill, when its next use counts as the big hit (ClientState.cs).
    internal readonly Dictionary<ushort, double> FistOfWarAt = [];

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
            CombatRushUntil = 0;
            CombatRushAttackSpeed = 0;
            CombatRushUse = null;
            if (RampageBuff != 0) RetiredCombatBuffs.Add((Program.PlayerId, RampageBuff));
            RampageBuff = 0;
            SentRampage = default;
            // Soul Eater's souls end with the area (a new one starts empty) and at death (its buff is CancelOnDeath).
            if (SoulEater.Buff != 0) RetiredCombatBuffs.Add((Program.PlayerId, SoulEater.Buff));
            if (SoulEater.AuraBuff != 0) RetiredCombatBuffs.Add((Program.PlayerId, SoulEater.AuraBuff));
            SoulEater.Buff = 0;
            SoulEater.AuraBuff = 0;
            SoulEater.SentStacks = 0;
            SoulEater.Souls.Clear();
            SoulEater.Clear();
        }
    }

    internal void ApplyMovementSpeedAt(double at)
    {
        lock (Monsters)
            Walk.SetSpeed(Math.Max(1, Program.WalkSpeed * (100L + Pools.MovementVelocityPct
                + Rampage.Current.MovementPercent) / 100.0 * (Program.GameLogicOrNull?.PlayerActionFactor(this)
                    ?? ElementalAilments.ActionFactor(at))
                * (ChannelMovement.Skill != 0 && ChannelMovement.Skill == ChannelSkill ? (100.0 + ChannelMovement.Percent) / 100 : 1)), at);
    }
}
