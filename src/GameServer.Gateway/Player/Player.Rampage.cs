public sealed partial class Player
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
    public bool ElusiveCritPending;
    // The movement speed the last sent movement stat held, so the decaying effect rebuilds the pools only when it changes.
    public int ElusiveMovementPct;
    // The effect percentage last put in the buff's +0x82, -1 while no buff is shown.
    public int ElusiveEffectPercent = -1;
    public bool IsElusive => ElusiveUntil > Clock.Elapsed.TotalSeconds;
    public bool SourcePhasing;
    public bool IsPhasing => SourcePhasing || Phasing || (PhasingUntil > 0 && PhasingUntil > Clock.Elapsed.TotalSeconds) || Reservations.CatPhasing;
    public ITransientCombatState? OwnedCombatState;
    public ConnectionWorker OwnedCombatWorker { get; } = new("owned-combat");
    public HashSet<uint> RetiredCombatObjects { get; } = [];
    public RampageState.Value SentRampage { get; set; }
    // The stat-driving part of the last published streak: the full stats go out only when one of these changes,
    // while current_rampage_stacks (every kill) goes as one partial stat update.
    public (int Active, int Damage, int Movement, int Rarity, int Area, int AttackSpeed)? SentRampageStat;
    public int SentRampageStacks;
    // Explosion art budget: a burst of kill explosions should not create an effect object for every corpse.
    public double ExplosionArtWindow;
    public int ExplosionArtCount;
    public ushort RampageBuff { get; set; }
    public double? RampageTransferAt;
    public double VaalPreventionUntil
    {
        get => vaalPreventionUntil;
        set { if (value != vaalPreventionUntil) { vaalPreventionUntil = value; MarkStatsDirty(StatInput.VaalPrevention); } }
    }
    double vaalPreventionUntil;
    public bool SentVaalPrevention;
    public object? VaalUseState;
    // Close Combat's Combat Rush: more Attack Speed for a travel skill (GemSupports.Rules.CombatRushOnHit).
    public double CombatRushUntil;
    public int CombatRushAttackSpeed;
    public (int Action, ushort Skill, int Speed)? CombatRushUse;
    // Fist of War: per skill, when its next use counts as the big hit (Player.cs).
    public readonly Dictionary<ushort, double> FistOfWarAt = [];

    public void RetireOwnedCombat()
    {
        OwnedCombatWorker.Retire();
        if (OwnedCombatState is { } old)
        {
            old.Clear();
            RetiredCombatObjects.UnionWith(old.RetiredObjects);
            foreach (uint actor in old.RetiredObjects)
                if (Area.Monsters.Remove(actor, out var removed)) removed.Dead = true;
            // Its blinds end (Blinded, row 179, on the monsters it blinded).
            foreach (var (actor, _) in old.RetiredBuffs)
            {
                if (Area.Monsters.TryGetValue(actor, out var monster)) monster.RampageBlindUntil = 0;
                Area.Buffs.ClearRow(actor, 179);
            }
        }
        OwnedCombatState = null;
        // The buffs the Buffs component shows on the player end with it (each system states its own again).
        Frames.Area?.Buffs.ClearAll(Id);
        SourcePhasing = false;
        VaalUseState = null;
        // Winter Orb's orb ends with the area, the death or the load that retires the connection's combat work.
        WinterOrbState = null;
        CombatRushUntil = 0;
        CombatRushAttackSpeed = 0;
        CombatRushUse = null;
        RampageBuff = 0;
        ElusiveUntil = 0;
        ElusiveStart = 0;
        ElusiveMovementPct = 0;
        ElusiveEffectPercent = -1;
        SentRampage = default;
        SentRampageStat = null;
        SentRampageStacks = 0;
        ExplosionArtWindow = 0;
        ExplosionArtCount = 0;
        // Soul Eater's souls end with the area (a new one starts empty) and at death (its buff is CancelOnDeath).
        SoulEater.Buff = 0;
        SoulEater.AuraBuff = 0;
        SoulEater.SentStacks = 0;
        SoulEater.Souls.Clear();
        SoulEater.Clear();
    }

    public void ApplyMovementSpeedAt(double at)
    {
        Walk.SetSpeed(Math.Max(1, Program.WalkSpeed * (100L + Pools.MovementVelocityPct
            + Rampage.Current.MovementPercent) / 100.0 * (Program.GameLogicOrNull?.PlayerActionFactor(this)
                ?? ElementalAilments.ActionFactor(at))
            * (ChannelMovement.Skill != 0 && ChannelMovement.Skill == ChannelSkill ? (100.0 + ChannelMovement.Percent) / 100 : 1)), at);
    }
}
