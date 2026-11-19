using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // The tail of the player's stats (PlayerStatSteps.cs: TailPanels, TailOffence, TailCritical): the character panel's
    // derived rows, the default attack's hit and ailment rows, accuracy, critical and off-hand rows. Each stage reads only
    // StatSnapshotTailInputs: the stats before it and the state read into the inputs.

    /// <summary>Everything the tail reads.</summary>
    public sealed record StatSnapshotTailInputs
    {
        /// <summary>The stats before the tail, which it changes in place.</summary>
        public required StatMap Stats;
        public required CharacterStats.Pools Pools;
        public required SkillCatalog Skills;
        public required ItemCatalog Items;
        public required int ClassRow;
        /// <summary>The active set's hands, rows 2 and 3 (immutable item records), and which of them are inactive.</summary>
        public required IReadOnlyList<InventoryItem> MainRow, OffRow;
        public required bool[] MainInactive, OffInactive;
        public required Dictionary<string, int> MainLocal, OffLocal;
        public required uint PlayerLife, PlayerMaxLife;
        public required bool EnergyShieldRecharging;
        /// <summary>Petrified Blood runs and Life is above Low Life: the client's own recovery step must not raise it
        /// above the Low Life boundary, so <c>life_recovery_per_minute</c> is published as 0.</summary>
        public required bool PetrifiedBloodRecoveryCapped;
        public required double LifeRegenerationToEnergyShield;
        /// <summary>Walking when the head was read: the while-moving regeneration in the Recovery rows (PanelStats.WhileMoving).</summary>
        public required bool Moving;
        public int BlindLess;
        public required int BloodChargesHeld;
        public required bool BloodChargesShown;
        /// <summary>is_ignited, is_chilled, is_frozen, is_shocked, after reconciliation.</summary>
        public required int[] Ailments;
        public double ActionFactor;
        public required int SuppressionChance;
        public required string? AttackEffectId;

        public IReadOnlyList<InventoryItem> Row(byte row) => row switch
        {
            PlayerInventories.Weapon1 => MainRow,
            PlayerInventories.Offhand1 => OffRow,
            _ => throw new InvalidOperationException($"the stat snapshot's tail reads rows 2 and 3, not {row}"),
        };

        public bool Inactive(uint id)
        {
            for (int i = 0; i < MainRow.Count; i++) if (MainRow[i].Id == id) return MainInactive[i];
            for (int i = 0; i < OffRow.Count; i++) if (OffRow[i].Id == id) return OffInactive[i];
            throw new InvalidOperationException($"the stat snapshot's tail asked about item {id}, outside rows 2 and 3");
        }
    }

    /// <summary>Same keys and values in the same enumeration order.</summary>
    public static bool SameInOrder<TKey>(Dictionary<TKey, int> a, Dictionary<TKey, int> b) where TKey : notnull
    {
        if (a.Count != b.Count) return false;
        using var x = a.GetEnumerator();
        using var y = b.GetEnumerator();
        while (x.MoveNext())
        {
            if (!y.MoveNext()) return false;
            if (!EqualityComparer<TKey>.Default.Equals(x.Current.Key, y.Current.Key) || x.Current.Value != y.Current.Value) return false;
        }
        return !y.MoveNext();
    }

    public enum StatSnapshotStage { Panels, Offence, Critical }

    /// <summary>Game_STAT_SNAPSHOT_VERIFY=1 (or a test): a reused result is also computed again and compared.</summary>
    public static bool VerifyStatSnapshotTail = Environment.GetEnvironmentVariable("Game_STAT_SNAPSHOT_VERIFY") == "1";
    // Reads the tail's inputs. Called where the tail starts, after the head; stats is the head's (not copied).
    public static StatSnapshotTailInputs ReadStatSnapshotTailInputs(Player conn, StatMap stats,
        Dictionary<string, int> mainLocal, Dictionary<string, int> offLocal,
        double lifeRegenerationToEnergyShield, bool moving)
    {
        var items = ItemsOf(conn);
        var mainRow = items.Items(PlayerInventories.Weapon1);
        var offRow = items.Items(PlayerInventories.Offhand1);
        var (bloodHeld, bloodShown) = BloodChargePanelInputs(conn);
        var (lifeRegenPerMinute, _) = PanelStats.LifeRegeneration(
            name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0, conn.Pools.Life);
        uint recoveryMargin = PetrifiedBlood.RecoveryMargin(lifeRegenPerMinute / 60);
        return new()
        {
            Stats = stats, Pools = conn.Pools with { Evasion = PlayerEvasion(conn, stats) },
            Skills = SkillsFor(conn), Items = ItemsFor(conn), ClassRow = conn.ClassRow,
            MainRow = mainRow, OffRow = offRow,
            MainInactive = [.. mainRow.Select(i => IsInactive(conn, i.Id))], OffInactive = [.. offRow.Select(i => IsInactive(conn, i.Id))],
            MainLocal = mainLocal, OffLocal = offLocal,
            PlayerLife = conn.PlayerLife, PlayerMaxLife = conn.PlayerMaxLife,
            EnergyShieldRecharging = conn.EnergyShield.Recharging,
            PetrifiedBloodRecoveryCapped = conn.PetrifiedBlood.Active && conn.PlayerLife > 0
                && (long)conn.PlayerLife + recoveryMargin
                    >= (uint)((long)conn.PlayerMaxLife * LowLifeThresholdPct(stats) / 100),
            LifeRegenerationToEnergyShield = lifeRegenerationToEnergyShield, Moving = moving, BlindLess = PlayerBlindValue(conn, stats).Less,
            BloodChargesHeld = bloodHeld, BloodChargesShown = bloodShown,
            Ailments = [], SuppressionChance = 0,
            AttackEffectId = SkillEffectId(conn, SkillCatalog.FirstSkillId),
        };
    }

    // The tail: reads only inputs (and the game data). Changes inputs.Stats in place and returns it.
    static StatMap ComputeStatSnapshotTail(StatSnapshotTailInputs inputs, StatSnapshotStage stage)
    {
        var stats = inputs.Stats;
        var pools = inputs.Pools;
        var mainLocal = inputs.MainLocal;
        var offLocal = inputs.OffLocal;
        int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        (Item Item, ItemCatalog.BaseType? Type)? ActiveMainHand() => ActiveHandIn(inputs.MainRow, inputs.Inactive, inputs.Items);
        (Item Item, ItemCatalog.BaseType? Type)? ActiveOffHand() => ActiveHandIn(inputs.OffRow, inputs.Inactive, inputs.Items);
        SkillCatalog.Unarmed WeaponFor() => WeaponIn(inputs.Skills, inputs.Items, inputs.ClassRow, ActiveMainHand())
            ?? inputs.Skills.UnarmedFor(inputs.ClassRow);
        SkillCatalog.Unarmed? OffhandWeaponFor() => WeaponIn(inputs.Skills, inputs.Items, inputs.ClassRow, ActiveMainHand()) is null
            ? null : WeaponIn(inputs.Skills, inputs.Items, inputs.ClassRow, ActiveOffHand());
        WeaponProfile Profile() => WeaponProfileFrom(inputs.Row, inputs.Inactive, inputs.Items, activeSet: true,
            () => throw new InvalidOperationException("the active set's profile does not look at the stored off hands"));
        bool MeleeAttack() => Profile().Melee;
        bool OnLowLife(IReadOnlyDictionary<uint, int> player) => CriticalOnLowLife(inputs.PlayerLife, inputs.PlayerMaxLife, player);
        int AttackMultiplier(IReadOnlyDictionary<uint, int> player) =>
            AttackCriticalMultiplier(pools, Profile().Unarmed, MeleeAttack(), spell: false, player);

        if (stage == StatSnapshotStage.Panels)
        {
            bool projectileAttack = OrdinaryRangedAttack.ProjectileId(Profile()) is not null;
            foreach (var (stat, value) in PanelStats.Compute(Get, n => mainLocal.GetValueOrDefault(n), n => offLocal.GetValueOrDefault(n),
                playerLevel: pools.Level, mainArmed: WeaponIn(inputs.Skills, inputs.Items, inputs.ClassRow, ActiveMainHand()) is not null,
                offArmed: OffhandWeaponFor() is not null, mainProjectileAttack: projectileAttack,
                offProjectileAttack: projectileAttack && ActiveOffHand() is { Type.Class: "Wand" })
                .Concat(PanelStats.FromPools(Get, pools))
                .Concat(PanelStats.Defence(Get, pools, Monsters.BaseDamage(pools.Level), Monsters.BaseAccuracy(pools.Level)))
                .Concat(PanelStats.Recovery(PanelStats.WhileMoving(Get, inputs.Moving), pools.Life, pools.Mana))
                // Leech limits and the client display use the full maximum pool. Petrified Blood caps only the
                // recovery actually applied, not the instance's contribution or its displayed cap.
                .Concat(LeechLimits.Compute(Get, pools.Life, pools.Mana, pools.EnergyShield, OnLowLife(stats)))
                .Concat(FlaskCharges.GainStats(Get))
                .Concat(new[] { ("energy_shield_recovery_per_minute", (int)Math.Round(EnergyShield.PredictionPerMinute(pools.EnergyShield, Get, inputs.EnergyShieldRecharging) + inputs.LifeRegenerationToEnergyShield)),
                    ("total_energy_shield_recovery_per_minute_from_regeneration", (int)Math.Round(EnergyShield.RegenPerMinute(pools.EnergyShield, Get) + inputs.LifeRegenerationToEnergyShield)),
                    ("energy_shield_recharge_rate_per_minute", (int)Math.Round(EnergyShield.RechargePerMinute(pools.EnergyShield, Get))),
                    ("energy_shield_recharge_active", 0) })
                .Concat(ChargePanelStats.Compute(Get)))
                if (Stats.Key(stat) is uint key) stats[key] = value;
            // Special maxima mirror completed standard maxima, including explicit final replacements.
            foreach (var (stat, value) in ChargePanelStats.MirroredMaximums(Get))
                if (Stats.Key(stat) is uint key) stats[key] = value;
            // Reap's Blood Charges: the count held, the maximum and the rows' gate (BloodChargePanelRows).
            foreach (var (stat, value) in BloodChargePanelRows(inputs.BloodChargesHeld, inputs.BloodChargesShown, Get))
                if (Stats.Key(stat) is uint key) stats[key] = value;
            // Petrified Blood: the client's Life step reads life_recovery_per_minute, not the display total, and it
            // only clamps to the maximum pool. Publish 0 while the buff stops non-flask recovery at Low Life.
            if (inputs.PetrifiedBloodRecoveryCapped && Stat.LifeRecoveryPerMinute is var lifeRecovery)
                stats[lifeRecovery] = 0;
            // Derive once from the resolved final rating, after pools and explicit stat replacements. Only this
            // temporary dictionary changes; repeated builds and removal cannot feed the bonus back into raw sources.
            if ((Get(ArmourAttackDamage.Per450Armour) != 0 || Get(ArmourAttackDamage.Per450Evasion) != 0)
                && Stat.AttackDamagePlusPct is var attackDamage)
                stats[attackDamage] = ArmourAttackDamage.Total(Get("attack_damage_+%"),
                    Get("physical_damage_reduction_rating"), Get(ArmourAttackDamage.Per450Armour),
                    Get("evasion_rating"), Get(ArmourAttackDamage.Per450Evasion));
            return stats;
        }
        if (stage == StatSnapshotStage.Offence)
        {
            // Release calculations and the panel use the same active-hand types, including after weapon swaps.
            if (Stat.MainHandWeaponType is var mainType)
                stats.TryAdd(mainType, WeaponTypeStats.Of(ActiveMainHand()?.Type?.Class));
            if (Stat.OffHandWeaponType is var offType)
                stats.TryAdd(offType, WeaponTypeStats.Of(ActiveOffHand()?.Type?.Class));
            // Whaletooth Bangle uses complete groups of final Strength. Derive from this fresh snapshot once; never
            // write back into raw sources. An explicit final-duration /set_stat keeps its usual replacement semantics.
            if (Stats.Key(SkillDurationModifiers.IncreasedDuration) is uint durationKey)
                stats[durationKey] = SkillDurationModifiers.Total(Get(SkillDurationModifiers.IncreasedDuration),
                    Get("strength"), Get(SkillDurationModifiers.PerTenStrength));
            // Dreadful Attitude: increased damage per complete 5 of the lowest final attribute, from this snapshot only.
            if (Get(LowestAttributeDamage) is int perFive and not 0 && Stat.DamagePlusPct is var damageKey)
                stats[damageKey] = (int)Math.Clamp(Get("damage_+%") + (long)perFive
                    * (Math.Min(Get("strength"), Math.Min(Get("dexterity"), Get("intelligence"))) / 5), int.MinValue, int.MaxValue);
            // Aristocrat: "cannot be X if A is higher than B" becomes the plain cannot_be_X while it holds (strictly higher),
            // which the Defence tab shows and the ailment consumer reads.
            foreach (var (conditional, plain, higher, lower) in AttributeAilmentConditions)
                if (Get(conditional) != 0 && Get(higher) > Get(lower) && Stats.Key(plain) is uint plainKey)
                    stats[plainKey] = 1;
            // The Offence tab's per-hand damage rows (issue #75): the hit the default attack actually swings with, so the panel
            // agrees with the damage the server applies. Only the main hand: the server attacks with it. Those rows are gated by
            // skill_uses_main_hand, which nothing sent before, so the numbers alone would leave them blank.
            if (Stat.SkillUsesMainHand is var usesMainHand) stats[usesMainHand] = 1;
            var attackConversion = DamageStats.Conversion(stats, Stats, attack: true);
            var attack = AttackHitConverted(WeaponFor(), stats, MeleeAttack(), attackConversion);
            foreach (var (stat, value) in PanelStats.HandDamage(attack, "main_hand"))
                if (Stats.Key(stat) is uint key) stats[key] = value;
            // The default attack's damaging-ailment rows per hand (AilmentPanelStats), from the same raw packet and
            // conversion as its hit; an off hand that is not swung keeps no stale rows.
            var ailmentContext = new AilmentPanelStats.Context("main_hand", Spell: false, Melee: MeleeAttack());
            var ailmentRows = AilmentPanelStats.Compute(ailmentContext, SupportedAttackDamage.Raw(WeaponFor(), Get), attackConversion, Get);
            if (OffhandWeaponFor() is { } offWeapon)
                ailmentRows.AddRange(AilmentPanelStats.Compute(ailmentContext with { Hand = "off_hand" },
                    SupportedAttackDamage.Raw(offWeapon, Get), attackConversion, Get));
            foreach (var (stat, value) in ailmentRows)
                if (Stats.Key(stat) is uint key) stats[key] = value;
            return stats;
        }
        // The native walk (0x141e827a0), action clip (0x1419fd730) and animation rate (0x141ea4760) read the final
        // virtual_action_speed_+%, derived natively from the intermediary (0x142822e80) only in a calculator; network
        // stats hold what is sent (04 §4.1a), so it is sent too. Only a clamp to -100 applies, so a reversed chill's
        // positive value goes through. A freeze stays on is_frozen: -100 there would divide the native clip by zero.
        foreach (var (name, value) in new[] { "is_ignited", "is_chilled", "is_frozen", "is_shocked" }.Zip(inputs.Ailments))
            if (Stats.Key(name) is uint key && value != 0) stats[key] = stats.GetValueOrDefault(key) + value;
        if (Stat.ActionSpeedMinusPct is var actionSpeedKey)
            stats[actionSpeedKey] = (int)Math.Round(100 * (1 - inputs.ActionFactor));
        if (Stat.IntermediaryCombinedActionSpeedPlusPct is var actionCombinedKey)
            stats[actionCombinedKey] = (int)Math.Round(100 * (inputs.ActionFactor - 1));
        if (Stat.VirtualActionSpeedPlusPct is var virtualActionKey && inputs.ActionFactor > 0)
            stats[virtualActionKey] = (int)Math.Round(100 * (inputs.ActionFactor - 1));
        // The conditions as they are now: full life, full Energy Shield and moving are stat conditions, and "hit an
        // enemy recently" marks and times the step (StatInput.EnemyHit).
        foreach (uint key in SuppressionChanceKeys) stats[key] = inputs.SuppressionChance;
        if (Stat.MainHandCriticalStrikeMultiplier is var multiplier)
            stats[multiplier] = AttackMultiplier(stats);
        var accuracy = OrdinaryAccuracy(inputs.Skills, inputs.AttackEffectId, pools, stats, mainLocal) with { Rating = TimedBlind.Apply(pools.Accuracy, inputs.BlindLess) };
        foreach (var (name, evasion) in new[]
        {
            ("main_hand_chance_to_hit_%", Monsters.BaseEvasion(pools.Level) ?? 0),
            ("main_hand_chance_to_hit_evasive_monsters_%", Monsters.BaseEvasiveEvasion(pools.Level) ?? 0),
        })
            if (Stats.Key(name) is uint key)
                stats[key] = accuracy.Chance(evasion, false, false);
        // The Off Hand rows of a dual-wielded default attack; the flag clears when nothing is swung with the off hand.
        if (Stat.SkillUsesOffHand is var usesOffHand) stats[usesOffHand] = 0;
        foreach (var (name, value) in OffHandPanelStats(OffhandWeaponFor, ActiveOffHand, () => pools, Profile, inputs.Items,
            () => AttackMultiplier(stats), stats, mainLocal, offLocal, stats.GetValueOrDefault(Stat.MainHandAttackSpeedPlusPct), own: null, blindLess: inputs.BlindLess) ?? [])
            if (Stats.Key(name) is uint key) stats[key] = value;
        if (Stat.VirtualOnLowLife is var lowLife)
            stats[lowLife] = OnLowLife(stats) ? 1 : 0;
        if (Stat.VirtualExtraCriticalRolls is var rolls)
            stats[rolls] = CriticalExtraRollsFor(OnLowLife(stats), stats);
        // The Offence tab's effective (and twice-) critical chance for spells: the pools' chance after the luck and
        // bifurcation policies; a selected spell gets its own (GemSkills).
        foreach (var (stat, value) in SpellEffectiveCritical(() => OnLowLife(stats), stats, pools.SpellCriticalStrikeChance, own: null))
            if (Stats.Key(stat) is uint key) stats[key] = value;
        return stats;
    }
}
