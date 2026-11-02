using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

static partial class Game
{
    // The player stat snapshot (CharacterPlayerStatsSnapshot) in two parts. Its head gathers the stat sources and runs the
    // side effects that go with them (pool rebuilds, the charge, fortification and Antiquarian observations, the leech and
    // fortification configuration, the time-dependent counts); it runs on every call, as before. Its tail (the character
    // panel's derived rows, the default attack's hit and ailment rows, accuracy, critical and off-hand rows) is a pure
    // function of StatSnapshotTailInputs: every value it reads is read once, before it, into the inputs, and the tail reads
    // nothing else of the connection. So a tail whose inputs equal the last one's has the last one's result, which a
    // connection keeps (one entry per stage). Rampage and ailment reconciliation run between the pure stages on every call. Idle, the energy-shield publication alone builds a snapshot on every 16 ms recovery step:
    // most of those reuse the tail. Agents/docs/dev/character-stat-lifecycle.md ("Snapshot cache").

    /// <summary>Everything the tail reads. Equal inputs (Matches) give an equal result, in the same order.</summary>
    internal sealed record StatSnapshotTailInputs
    {
        /// <summary>The head's stats, in their order (a dictionary's order is what the packets built from it list).</summary>
        public required Dictionary<uint, int> Stats;
        public required Dictionary<uint, int> RawOverrides;
        /// <summary>RawOverrides' keys (the stats /set_stat replaces): derived from RawOverrides, which Matches compares, so it
        /// is not compared itself.</summary>
        public required HashSet<uint> DevSet;
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
        public required double LifeRegenerationToEnergyShield;
        /// <summary>Walking when the head was read: the while-moving regeneration in the Recovery rows (PanelStats.WhileMoving).</summary>
        public required bool Moving;
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

        public bool Matches(StatSnapshotTailInputs o) =>
            ReferenceEquals(Skills, o.Skills) && ReferenceEquals(Items, o.Items) && ClassRow == o.ClassRow
            && PlayerLife == o.PlayerLife && PlayerMaxLife == o.PlayerMaxLife
            && EnergyShieldRecharging == o.EnergyShieldRecharging
            && LifeRegenerationToEnergyShield.Equals(o.LifeRegenerationToEnergyShield) && Moving == o.Moving
            && BloodChargesHeld == o.BloodChargesHeld && BloodChargesShown == o.BloodChargesShown
            && SuppressionChance == o.SuppressionChance && AttackEffectId == o.AttackEffectId
            && ActionFactor.Equals(o.ActionFactor) && Ailments.AsSpan().SequenceEqual(o.Ailments)
            && Pools.Equals(o.Pools)
            && SameItems(MainRow, o.MainRow) && SameItems(OffRow, o.OffRow)
            && MainInactive.AsSpan().SequenceEqual(o.MainInactive) && OffInactive.AsSpan().SequenceEqual(o.OffInactive)
            && SameInOrder(RawOverrides, o.RawOverrides) && SameInOrder(MainLocal, o.MainLocal) && SameInOrder(OffLocal, o.OffLocal)
            && SameInOrder(Stats, o.Stats);

        static bool SameItems(IReadOnlyList<InventoryItem> a, IReadOnlyList<InventoryItem> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }
    }

    /// <summary>Same keys and values in the same enumeration order.</summary>
    internal static bool SameInOrder<TKey>(Dictionary<TKey, int> a, Dictionary<TKey, int> b) where TKey : notnull
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

    // One per connection: the last tail's inputs (a copy of the head's stats) and its result. Both are never changed
    // after they are published, so any thread may read them; a caller gets a copy of the result.
    internal sealed record StatSnapshotTail(StatSnapshotTailInputs Inputs, Dictionary<uint, int> Result);
    internal sealed class StatSnapshotTailSlot { public volatile StatSnapshotTail? Last; }
    internal enum StatSnapshotStage { Panels, Offence, Critical }
    internal sealed class StatSnapshotTailSlots
    {
        public readonly StatSnapshotTailSlot[] Stages = [new(), new(), new()];
    }
    internal static readonly ConditionalWeakTable<ClientState, StatSnapshotTailSlots> StatSnapshotTails = new();

    /// <summary>Game_STAT_SNAPSHOT_VERIFY=1 (or a test): every reused tail is also computed again and compared, in order.</summary>
    internal static bool VerifyStatSnapshotTail = Environment.GetEnvironmentVariable("Game_STAT_SNAPSHOT_VERIFY") == "1";
    internal static long StatSnapshotTailHits, StatSnapshotTailMisses, StatSnapshotTailMismatches;

    // Reads the tail's inputs. Called where the tail starts, after the head; stats is the head's (not copied).
    internal static StatSnapshotTailInputs ReadStatSnapshotTailInputs(ClientState conn, Dictionary<uint, int> stats,
        Dictionary<uint, int> rawOverrides, HashSet<uint> devSet, Dictionary<string, int> mainLocal, Dictionary<string, int> offLocal,
        double lifeRegenerationToEnergyShield, bool moving)
    {
        var items = ItemsOf(conn);
        var mainRow = items.Items(PlayerInventories.Weapon1);
        var offRow = items.Items(PlayerInventories.Offhand1);
        var (bloodHeld, bloodShown) = BloodChargePanelInputs(conn);
        return new()
        {
            Stats = stats, RawOverrides = rawOverrides, DevSet = devSet, Pools = conn.Pools,
            Skills = SkillsFor(conn), Items = ItemsFor(conn), ClassRow = conn.ClassRow,
            MainRow = mainRow, OffRow = offRow,
            MainInactive = [.. mainRow.Select(i => IsInactive(conn, i.Id))], OffInactive = [.. offRow.Select(i => IsInactive(conn, i.Id))],
            MainLocal = mainLocal, OffLocal = offLocal,
            PlayerLife = conn.PlayerLife, PlayerMaxLife = conn.PlayerMaxLife,
            EnergyShieldRecharging = conn.EnergyShield.Recharging,
            LifeRegenerationToEnergyShield = lifeRegenerationToEnergyShield, Moving = moving,
            BloodChargesHeld = bloodHeld, BloodChargesShown = bloodShown,
            Ailments = [], SuppressionChance = 0,
            AttackEffectId = SkillEffectId(conn, SkillCatalog.FirstSkillId),
        };
    }

    // The tail on stats (the head's, changed in place): the last result when the inputs are the same, else computed.
    internal static Dictionary<uint, int> StatSnapshotTailFor(ClientState conn, StatSnapshotTailInputs inputs, StatSnapshotStage stage)
    {
        var slot = StatSnapshotTails.GetValue(conn, _ => new()).Stages[(int)stage];
        if (slot.Last is { } last && last.Inputs.Matches(inputs))
        {
            Interlocked.Increment(ref StatSnapshotTailHits);
            if (VerifyStatSnapshotTail)
            {
                var fresh = ComputeStatSnapshotTail(inputs, stage);
                if (!SameInOrder(fresh, last.Result))
                {
                    Interlocked.Increment(ref StatSnapshotTailMismatches);
                    var differs = fresh.Keys.Union(last.Result.Keys)
                        .Where(k => fresh.GetValueOrDefault(k) != last.Result.GetValueOrDefault(k)).Take(8)
                        .Select(k => $"{Stats.Name(k)} {last.Result.GetValueOrDefault(k)} (reused) vs {fresh.GetValueOrDefault(k)}");
                    string why = $"[gateway] stat snapshot: a reused tail differs from a fresh one: {string.Join(", ", differs)}"
                        + (SameInOrder<uint>(new(fresh.OrderBy(kv => kv.Key)), new(last.Result.OrderBy(kv => kv.Key))) ? " (order only)" : "");
                    Console.WriteLine(why);
                    throw new InvalidOperationException(why);
                }
            }
            return new(last.Result);
        }
        Interlocked.Increment(ref StatSnapshotTailMisses);
        var key = new Dictionary<uint, int>(inputs.Stats);
        var result = ComputeStatSnapshotTail(inputs, stage);
        inputs.Stats = key;
        // A reuse returns a copy of the result, so the first use does too: both are the same fresh, compacted dictionary
        // (one order and layout, whatever the tail did to build the result).
        slot.Last = new(inputs, result);
        return new(result);
    }

    // The tail: reads only inputs (and the game data). Changes inputs.Stats in place and returns it.
    static Dictionary<uint, int> ComputeStatSnapshotTail(StatSnapshotTailInputs inputs, StatSnapshotStage stage)
    {
        var stats = inputs.Stats;
        var rawOverrides = inputs.RawOverrides;
        var devSet = inputs.DevSet;
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
            foreach (var (stat, value) in PanelStats.Compute(Get, n => mainLocal.GetValueOrDefault(n), n => offLocal.GetValueOrDefault(n),
                playerLevel: pools.Level)
                .Concat(PanelStats.FromPools(Get, pools))
                .Concat(PanelStats.Defence(Get, pools, Monsters.BaseDamage(pools.Level), Monsters.BaseAccuracy(pools.Level)))
                .Concat(PanelStats.Recovery(PanelStats.WhileMoving(Get, inputs.Moving), pools.Life, pools.Mana))
                .Concat(LeechLimits.Compute(Get, pools.Life, pools.Mana, pools.EnergyShield,
                    name => Stats.Key(name) is uint key && rawOverrides.TryGetValue(key, out int value) ? value : null,
                    OnLowLife(stats)))
                .Concat(FlaskCharges.GainStats(Get))
                .Concat(new[] { ("energy_shield_recovery_per_minute", (int)Math.Round(EnergyShield.PredictionPerMinute(pools.EnergyShield, Get, inputs.EnergyShieldRecharging) + inputs.LifeRegenerationToEnergyShield)),
                    ("total_energy_shield_recovery_per_minute_from_regeneration", (int)Math.Round(EnergyShield.RegenPerMinute(pools.EnergyShield, Get) + inputs.LifeRegenerationToEnergyShield)),
                    ("energy_shield_recharge_rate_per_minute", (int)Math.Round(EnergyShield.RechargePerMinute(pools.EnergyShield, Get))),
                    ("energy_shield_recharge_active", 0) })
                .Concat(ChargePanelStats.Compute(Get)))
                if (Stats.Key(stat) is uint key && !devSet.Contains(key)) stats[key] = value;
            // Special maxima mirror completed standard maxima, including explicit final replacements.
            foreach (var (stat, value) in ChargePanelStats.MirroredMaximums(Get))
                if (Stats.Key(stat) is uint key && !devSet.Contains(key)) stats[key] = value;
            // Reap's Blood Charges: the count held, the maximum and the rows' gate (BloodChargePanelRows).
            foreach (var (stat, value) in BloodChargePanelRows(inputs.BloodChargesHeld, inputs.BloodChargesShown, Get))
                if (Stats.Key(stat) is uint key && !devSet.Contains(key)) stats[key] = value;
            // Derive once from the resolved final rating, after pools and explicit stat replacements. Only this
            // temporary dictionary changes; repeated builds and removal cannot feed the bonus back into raw sources.
            if ((Get(ArmourAttackDamage.Per450Armour) != 0 || Get(ArmourAttackDamage.Per450Evasion) != 0)
                && Stats.Key("attack_damage_+%") is uint attackDamage)
                stats[attackDamage] = ArmourAttackDamage.Total(Get("attack_damage_+%"),
                    Get("physical_damage_reduction_rating"), Get(ArmourAttackDamage.Per450Armour),
                    Get("evasion_rating"), Get(ArmourAttackDamage.Per450Evasion));
            return stats;
        }
        if (stage == StatSnapshotStage.Offence)
        {
            // Whaletooth Bangle uses complete groups of final Strength. Derive from this fresh snapshot once; never
            // write back into raw sources. An explicit final-duration /set_stat keeps its usual replacement semantics.
            if (Stats.Key(SkillDurationModifiers.IncreasedDuration) is uint durationKey && !devSet.Contains(durationKey))
                stats[durationKey] = SkillDurationModifiers.Total(Get(SkillDurationModifiers.IncreasedDuration),
                    Get("strength"), Get(SkillDurationModifiers.PerTenStrength));
            // Dreadful Attitude: increased damage per complete 5 of the lowest final attribute, from this snapshot only.
            if (Get(LowestAttributeDamage) is int perFive and not 0 && Stats.Key("damage_+%") is uint damageKey && !devSet.Contains(damageKey))
                stats[damageKey] = (int)Math.Clamp(Get("damage_+%") + (long)perFive
                    * (Math.Min(Get("strength"), Math.Min(Get("dexterity"), Get("intelligence"))) / 5), int.MinValue, int.MaxValue);
            // Aristocrat: "cannot be X if A is higher than B" becomes the plain cannot_be_X while it holds (strictly higher),
            // which the Defence tab shows and the ailment consumer reads.
            foreach (var (conditional, plain, higher, lower) in AttributeAilmentConditions)
                if (Get(conditional) != 0 && Get(higher) > Get(lower) && Stats.Key(plain) is uint plainKey && !devSet.Contains(plainKey))
                    stats[plainKey] = 1;
            // The Offence tab's per-hand damage rows (issue #75): the hit the default attack actually swings with, so the panel
            // agrees with the damage the server applies. Only the main hand: the server attacks with it. Those rows are gated by
            // skill_uses_main_hand, which nothing sent before, so the numbers alone would leave them blank.
            if (Stats.Key("skill_uses_main_hand") is uint usesMainHand && !devSet.Contains(usesMainHand)) stats[usesMainHand] = 1;
            var attackConversion = DamageStats.Conversion(stats, Stats, attack: true);
            var attack = AttackHitConverted(WeaponFor(), stats, MeleeAttack(), attackConversion);
            foreach (var (stat, value) in PanelStats.HandDamage(attack, "main_hand"))
                if (Stats.Key(stat) is uint key && !devSet.Contains(key)) stats[key] = value;
            // The default attack's damaging-ailment rows per hand (AilmentPanelStats), from the same raw packet and
            // conversion as its hit; an off hand that is not swung keeps no stale rows.
            var ailmentContext = new AilmentPanelStats.Context("main_hand", Spell: false, Melee: MeleeAttack(), Projectile: !MeleeAttack());
            var ailmentRows = AilmentPanelStats.Compute(ailmentContext, SupportedAttackDamage.Raw(WeaponFor(), Get), attackConversion, Get);
            if (OffhandWeaponFor() is { } offWeapon)
                ailmentRows.AddRange(AilmentPanelStats.Compute(ailmentContext with { Hand = "off_hand" },
                    SupportedAttackDamage.Raw(offWeapon, Get), attackConversion, Get));
            foreach (var (stat, value) in ailmentRows)
                if (Stats.Key(stat) is uint key && !devSet.Contains(key)) stats[key] = value;
            return stats;
        }
        // The native walk (0x141e7c0b0), action clip (0x1419fad20) and animation rate (0x141e9deb0) read the final
        // virtual_action_speed_+%, derived natively from the intermediary (0x14281c2d0) only in a calculator; network
        // stats hold what is sent (04 §4.1a), so it is sent too. Only a clamp to -100 applies, so a reversed chill's
        // positive value goes through. A freeze stays on is_frozen: -100 there would divide the native clip by zero.
        foreach (var (name, value) in new[] { "is_ignited", "is_chilled", "is_frozen", "is_shocked" }.Zip(inputs.Ailments))
            if (Stats.Key(name) is uint key && value != 0) stats[key] = stats.GetValueOrDefault(key) + value;
        if (Stats.Key("action_speed_-%") is uint actionSpeedKey && !devSet.Contains(actionSpeedKey))
            stats[actionSpeedKey] = (int)Math.Round(100 * (1 - inputs.ActionFactor));
        if (Stats.Key("intermediary_combined_action_speed_+%") is uint actionCombinedKey && !devSet.Contains(actionCombinedKey))
            stats[actionCombinedKey] = (int)Math.Round(100 * (inputs.ActionFactor - 1));
        if (Stats.Key("virtual_action_speed_+%") is uint virtualActionKey && !devSet.Contains(virtualActionKey) && inputs.ActionFactor > 0)
            stats[virtualActionKey] = (int)Math.Round(100 * (inputs.ActionFactor - 1));
        // The conditions as they are now; the player tick sends the chance again when they change.
        foreach (uint key in SuppressionChanceKeys) stats[key] = inputs.SuppressionChance;
        if (Stats.Key("main_hand_critical_strike_multiplier") is uint multiplier)
            stats[multiplier] = AttackMultiplier(stats);
        var accuracy = OrdinaryAccuracy(inputs.Skills, inputs.AttackEffectId, pools, stats, mainLocal);
        foreach (var (name, evasion) in new[]
        {
            ("main_hand_chance_to_hit_%", Monsters.BaseEvasion(pools.Level) ?? 0),
            ("main_hand_chance_to_hit_evasive_monsters_%", Monsters.BaseEvasiveEvasion(pools.Level) ?? 0),
        })
            if (Stats.Key(name) is uint key && !devSet.Contains(key))
                stats[key] = accuracy.Chance(evasion, false, false);
        // The Off Hand rows of a dual-wielded default attack; the flag clears when nothing is swung with the off hand.
        if (Stats.Key("skill_uses_off_hand") is uint usesOffHand && !devSet.Contains(usesOffHand)) stats[usesOffHand] = 0;
        foreach (var (name, value) in OffHandPanelStats(OffhandWeaponFor, ActiveOffHand, () => pools, Profile, inputs.Items,
            () => AttackMultiplier(stats), stats, mainLocal, offLocal, stats.GetValueOrDefault(StatKeys.MainHandAttackSpeedPct), own: null) ?? [])
            if (Stats.Key(name) is uint key && !devSet.Contains(key)) stats[key] = value;
        if (Stats.Key("virtual_on_low_life") is uint lowLife)
            stats[lowLife] = OnLowLife(stats) ? 1 : 0;
        if (Stats.Key("virtual_extra_critical_rolls") is uint rolls)
            stats[rolls] = CriticalExtraRollsFor(OnLowLife(stats), stats);
        // The Offence tab's effective (and twice-) critical chance for spells: the pools' chance after the luck and
        // bifurcation policies; a selected spell gets its own (GemSkills).
        foreach (var (stat, value) in SpellEffectiveCritical(() => OnLowLife(stats), stats, pools.SpellCriticalStrikeChance, own: null))
            if (Stats.Key(stat) is uint key && !devSet.Contains(key)) stats[key] = value;
        return stats;
    }
}
