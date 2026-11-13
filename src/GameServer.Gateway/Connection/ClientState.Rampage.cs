public sealed partial class ClientState
{
    public RampageSession Rampage { get; } = new();
    public SoulEaterState SoulEater { get; } = new();
    // "Gain Physical Damage Immunity for X seconds on Rampage" and "Phasing for X seconds on Rampage" thresholds.
    public double PhysicalImmunityUntil;
    public double PhasingUntil;
    // Elusive: the buff starts at 100% + elusive effect and loses 20% of base effect per second; it cannot be
    // refreshed while running.
    public double ElusiveStart;
    public double ElusiveUntil;
    public double ElusiveEffectMultiplier = 1;
    public ushort ElusiveBuff;
    public bool ElusiveCritPending;
    public bool IsElusive => ElusiveUntil > Clock.Elapsed.TotalSeconds;
    public bool IsPhasing => Phasing || PhasingUntil > Clock.Elapsed.TotalSeconds;
    public ITransientCombatState? OwnedCombatState;
    public ConnectionWorker OwnedCombatWorker { get; } = new("owned-combat");
    public HashSet<uint> RetiredCombatObjects { get; } = [];
    public HashSet<(uint Actor, ushort Buff)> RetiredCombatBuffs { get; } = [];
    public RampageState.Value SentRampage { get; set; }
    public ushort RampageBuff { get; set; }
    public double? RampageTransferAt;
    public double VaalPreventionUntil;
    public bool SentVaalPrevention;
    public object? VaalUseState;
    // Close Combat's Combat Rush: more Attack Speed for a travel skill (GemSupports.Rules.CombatRushOnHit).
    public double CombatRushUntil;
    public int CombatRushAttackSpeed;
    public (int Action, ushort Skill, int Speed)? CombatRushUse;
    // Fist of War: per skill, when its next use counts as the big hit (ClientState.cs).
    public readonly Dictionary<ushort, double> FistOfWarAt = [];

    public void RetireOwnedCombat()
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
            // Winter Orb's orb ends with the area, the death or the load that retires the connection's combat work.
            WinterOrbState = null;
            CombatRushUntil = 0;
            CombatRushAttackSpeed = 0;
            CombatRushUse = null;
            if (RampageBuff != 0) RetiredCombatBuffs.Add((Program.PlayerId, RampageBuff));
            RampageBuff = 0;
            if (ElusiveBuff != 0) RetiredCombatBuffs.Add((Program.PlayerId, ElusiveBuff));
            ElusiveBuff = 0;
            ElusiveUntil = 0;
            ElusiveStart = 0;
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

    public void ApplyMovementSpeedAt(double at)
    {
        lock (Monsters)
            Walk.SetSpeed(Math.Max(1, Program.WalkSpeed * (100L + Pools.MovementVelocityPct
                + Rampage.Current.MovementPercent) / 100.0 * (Program.GameLogicOrNull?.PlayerActionFactor(this)
                    ?? ElementalAilments.ActionFactor(at))
                * (ChannelMovement.Skill != 0 && ChannelMovement.Skill == ChannelSkill ? (100.0 + ChannelMovement.Percent) / 100 : 1)), at);
    }
}
