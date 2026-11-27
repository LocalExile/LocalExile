using GameServer.Core;

namespace GameServer.Modules.Legion;

/// <summary>
/// Wires Legion Generals, Sergeants, and soldiers into <see cref="MonsterKitHooks"/> (S6):
/// filters out Hyrri rider teleports and Fish rocket-jump parents so the Fish drives movement while the rider fires,
/// and supplies explicit Tier-1
/// clip-event → child-GE combo bindings for Legion <c>EmptyAction*</c> parents.
/// </summary>
static class LegionSkillHooks
{
    sealed class Table
    {
        public Dictionary<int, MonsterKitHooks.ComboEventBinding[]> Combos { get; } = [];
        public HashSet<int> HyrriRiderRows { get; } = [];
        public HashSet<int> HyrriBodyRows { get; } = [];
        public HashSet<int> HyrriRiderBlockedEffects { get; } = [];
        public int CommanderFlingEffect { get; set; } = -1;
        public int HyrriRocketJumpEffect { get; set; } = -1;
        public int HyrriWaterCloneMortarEffect { get; set; } = -1;
    }

    const int CommanderRow = 10492;

    static Table? Loaded;

    /// <summary>The GrantedEffects row of Hyrri's water clone mortar (-1: none); the rider's frame watches its cooldown.</summary>
    public static int WaterCloneMortarEffect => Loaded?.HyrriWaterCloneMortarEffect ?? -1;

    public static void LoadData()
    {
        var table = new Table();
        var data = LegionData.Current;
        foreach (var (_, rows) in data.Riders)
        {
            table.HyrriBodyRows.Add(rows.Body);
            table.HyrriRiderRows.Add(rows.Rider);
        }

        var skills = Program.Skills;
        int Ge(string id) => skills.EffectRow(id);

        void BlockRider(string id) { if (Ge(id) is int r and >= 0) table.HyrriRiderBlockedEffects.Add(r); }
        BlockRider("LegionHyrriTeleport");
        BlockRider("LegionHyrriTeleportSpinningDeath");
        table.HyrriRocketJumpEffect = Ge("EmptyActionLegionHyrrisFishRocketJump");
        table.HyrriWaterCloneMortarEffect = Ge("LegionHyrriWaterCloneMortar");

        void Bind(string parentId, params (string EventName, string ChildId)[] pairs)
        {
            int parent = Ge(parentId);
            if (parent < 0) return;
            var list = new List<MonsterKitHooks.ComboEventBinding>(pairs.Length);
            foreach (var (ev, childId) in pairs)
                if (Ge(childId) is int child and >= 0)
                    list.Add(new MonsterKitHooks.ComboEventBinding(ev, child));
            if (list.Count > 0) table.Combos[parent] = [.. list];
        }

        // Karui soldiers, Sergeants & Queen Hyrri Ngamaku (rider 7702)
        Bind("EmptyActionLegionKaruiMelee",
            ("combo2_cleaveright", "LegionKaruiMeleeCombo1"),
            ("combo2_cleaveleft", "LegionKaruiMeleeCombo2"),
            ("combo2_slam", "LegionKaruiMeleeCombo3"));
        Bind("EmptyActionLegionKaruiAxeCombo",
            ("wave1", "LegionKaruiAxeCleave1"),
            ("wave2", "LegionKaruiAxeCleave2"));
        Bind("EmptyActionLegionKaruiSnipeRain",
            ("rain", "LegionKaruiSergeantRainOfArrows"),
            ("snipe", "LegionKaruiSergeantArcherTriggerBeam"));
        Bind("EmptyActionLegionHyrrisFishRocketJump",
            ("trigger_proj", "LegionHyrriRocketJumpProjectile"));

        // Eternal Empire Sergeants & General Marceus Lioneye (7723)
        Bind("EmptyActionLegionMultiShieldBash",
            ("cone", "LegionEternalEmpireShieldBashCone"),
            ("circle", "LegionEternalEmpireShieldBashCircle"));
        Bind("EmptyActionEternalStampedeDaemon",
            ("contact", "LegionEternalSummonStampede"));
        Bind("EmptyActionLegionEternalEmpireCombo",
            ("combo1", "LegionEmpireSpearCombo1"),
            ("combo2", "LegionEmpireSpearCombo2"),
            ("combo3", "LegionEmpireSpearCombo3"),
            ("combo4", "LegionEmpireSpearCombo4"));
        Bind("EmptyActionLegionLioneyeCircleOfDeath",
            ("circle1", "LegionLioneyeDeathCircleLeft"),
            ("circle2", "LegionLioneyeDeathCircleRight"),
            ("spawn_fire", "LegionLioneyeFireCircleFire"),
            ("circle3", "LegionLioneyeFireCircleBombard"));
        Bind("EmptyActionLegionLioneyeAttackLeft", ("trigger_proj", "LioneyeTriggeredProjectile"));
        Bind("EmptyActionLegionLioneyeAttackRight", ("trigger_proj", "LioneyeTriggeredProjectile"));
        Bind("EmptyActionLegionLioneyeAttackBack", ("trigger_projs", "LioneyeTriggeredProjectiles"));

        // Maraketh Sergeants & Aukuna, the Black Sekhema (mounted 7753 & on foot 7754)
        Bind("EmptyActionLegionMarakethSandstorm",
            ("whirl", "LegionMarakethSandstorm"),
            ("sandstorm", "LegionMarakethSandstorm2"));
        Bind("EmptyActionMarakethRangedDashLeft", ("contact", "LegionMarakethRangedDashProjectile"));
        Bind("EmptyActionMarakethRangedDashRight", ("contact", "LegionMarakethRangedDashProjectile"));
        Bind("EmptyActionMarakethRangedMultiShot", ("trigger_proj", "LegionMarakethRangedDashProjectile"));
        Bind("EmptyActionLegionMultipleShoot", ("trigger_proj", "LegionMarakethGeneralMultishot"));
        Bind("EmptyActionMarakethGeneralScreech", ("screech", "LegionMarakethGeneralScreechGeometrySpell"));
        Bind("LegionMarakethGeneralEmptyActionCleave",
            ("contact", "LegionMarakethGeneralCleave"),
            ("lacerateright", "LegionMarakethGeneralLacerateRight"));
        Bind("LegionMarakethGeneralEmptyActionDash",
            ("contact", "LegionMarakethGeneralLacerateLeft"),
            ("reave", "LegionMarakethGeneralTriggerReave"),
            ("dash", "LegionMarakethGeneralDash"),
            ("hit", "LegionMarakethGeneralFlurrySlice"));

        // Templar Sergeant & Cardinal Sanctus Vox (7783)
        Bind("EmptyActionLegionTemplarDash",
            ("bolt", "LegionTemplarShieldShards"),
            ("shieldbash", "LegionTemplarDashImpact"));
        Bind("EmptyActionLegionTemplarGeneralComboDash",
            ("left", "LegionTemplarGeneralSwipeLeft"),
            ("right", "LegionTemplarGeneralSwipeRight"),
            ("dash", "LegionTemplarBossDash"));
        Bind("EmptyActionLegionTemplarHolyRuckus",
            ("start_ruckus", "LegionTemplarBossMassBolt"),
            ("start_beam", "LegionTemplarBossHolyBlastBeam"));

        // Vaal Sergeant & Viper Napuatzi (7816)
        Bind("EmptyActionVaalSergeantSoulrendCleave",
            ("rend", "LegionVaalSoulrend"),
            ("contact", "LegionVaalSergeantSlam"));
        Bind("EmptyActionVaalSergeantMortarSpam",
            ("contact", "LegionVaalWeaponThrow"));
        Bind("EmptyActionVaalViperLeftRightCombo",
            ("right1", "LegionVaalViperDualImpact"),
            ("left1", "LegionVaalViperDualImpact"),
            ("right2", "LegionVaalViperDualImpact"),
            ("left2", "LegionVaalViperDualImpact"),
            ("release", "LegionVaalViperQuadBlast"));
        Bind("EmptyActionVaalViperSpinningSlam", ("contact", "LegionVaalViperSpinningSlam"));
        Bind("EmptyActionLegionVaalReversePulse", ("contact", "LegionVaalViperRetractingPulse"));
        Bind("EmptyActionVaalViperWaveCombo",
            ("left", "LegionVaalViperDualImpact"),
            ("right", "LegionVaalViperDualImpact"));
        // Sweeping barrage (10x contact): alternate physical and chaos/poison projectiles across the clip.
        if (Ge("EmptyActionLegionVaalViperProjectileSpam") is int spamParent and >= 0
            && Ge("LegionVaalViperProjectilePhys") is int phys and >= 0
            && Ge("LegionVaalViperProjectilePoison") is int poison and >= 0)
        {
            var alternating = new MonsterKitHooks.ComboEventBinding[10];
            for (int i = 0; i < alternating.Length; i++)
                alternating[i] = new MonsterKitHooks.ComboEventBinding("contact", (i & 1) == 0 ? phys : poison);
            table.Combos[spamParent] = alternating;
        }

        // Faridun Legion Commander (variety row 10492 only: Ridan 10489 and the Forgotten Afarud Commander 10490 share the parent).
        Bind("EAAFaridunWildbossFling", ("contact", "MPWFaridunMinibossFleshProjectile"));
        table.CommanderFlingEffect = Ge("EAAFaridunWildbossFling");

        Loaded = table;
    }

    public static void UnloadData() => Loaded = null;

    /// <summary>Registers all Legion <see cref="MonsterKitHooks"/> handlers and returns a composite disposable for clean unload.</summary>
    public static IDisposable Register()
    {
        IDisposable filter = MonsterKitHooks.AddEffectFilter(AllowEffect);
        IDisposable combos = MonsterKitHooks.AddComboBindings(ComboBindings);
        return new Composite(filter, combos);
    }

    static bool AllowEffect(MonsterCatalog.Variety variety, int effect, string? effectId)
    {
        if (Loaded is not { } table) return true;
        if (table.HyrriRiderRows.Contains(variety.Row) && table.HyrriRiderBlockedEffects.Contains(effect))
            return false;
        if (table.HyrriBodyRows.Contains(variety.Row) && effect == table.HyrriRocketJumpEffect)
            return false;
        return true;
    }

    static IReadOnlyList<MonsterKitHooks.ComboEventBinding>? ComboBindings(MonsterCatalog.Variety variety, int parentEffect)
    {
        if (Loaded is not { } table) return null;
        if (parentEffect == table.HyrriRocketJumpEffect && table.HyrriBodyRows.Contains(variety.Row))
            return null;
        if (parentEffect == table.CommanderFlingEffect && variety.Row != CommanderRow) return null;
        return table.Combos.TryGetValue(parentEffect, out var bindings) ? bindings : null;
    }

    sealed class Composite(IDisposable a, IDisposable b) : IDisposable
    {
        int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            a.Dispose();
            b.Dispose();
        }
    }
}
