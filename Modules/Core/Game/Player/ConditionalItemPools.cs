using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public readonly record struct LifeReservationConditionSnapshot(int MaximumLife, int EfficiencyPct,
        int? ItemPercentOverride, int AdditionalSkillReserved, AuraCatalog.Aura[] Auras)
    {
        public LifeReservation.Value WithItemPercent(int itemPercent) =>
            LifeReservation.FromInputs(MaximumLife, itemPercent, EfficiencyPct, Auras, AdditionalSkillReserved);
    }

    public static IReadOnlyDictionary<string, bool> ItemConditionFlags(Player player, double now,
        IEnumerable<Item>? activeItems = null) =>
        ItemConditionFlags(player, now, activeItems, out _);

    public static IReadOnlyDictionary<string, bool> ItemConditionFlags(Player player, double now,
        IEnumerable<Item>? activeItems, out LifeReservationConditionSnapshot reservationSnapshot)
    {
        // The active items are worked out once: every check below would otherwise ask each worn item again.
        activeItems = activeItems?.ToArray() ?? EquipmentForStats(player)
            .Where(e => !IsInactive(player, e.Item.Id)).Select(e => e.Item.Item).ToArray();
        var ailments = player.ElementalAilments.Snapshot(now);
        var leech = LeechOf(player).Active(now);
        bool moving = player.Walk.Moving(now), alive = player.PlayerLife > 0;
        var lifeReservation = LifeReservationForConditions(player, activeItems, out reservationSnapshot);
        long manaRequired = (long)AuraCatalog.RequiredMana(player.Pools, player.Auras.Values)
            + PetrifiedBlood.RequiredMana(player.Pools, player.PetrifiedBlood)
            + PlacedSkills.Reserved(player.Pools, player.Placed.All.Values, false);
        int manaReserved = (int)Math.Min(player.Pools.Mana, Math.Min(int.MaxValue, manaRequired));
        var lowLifeSources = SelfCurseConditionInputs(player, activeItems, conditionTime: now);
        bool lowLife = alive && CriticalOnLowLife(player, lowLifeSources);
        var physicalAegis = PhysicalAegisStatus(player, now);
        string stance = StanceCatalog.Selected(player.Auras.Values);
        bool blood = stance == StanceCatalog.BloodSelection, sand = stance == StanceCatalog.SandSelection;
        bool changedStance = player.RecentEvents.Happened(StatConditions.ChangedStance, now);
        bool catAspect = CatAspectRootActiveLocked(player);
        bool catsStealth = catAspect && player.Reservations.Cat == ReservationState.CatPhase.Stealth;
        bool catsAgility = catAspect && player.Reservations.Cat == ReservationState.CatPhase.Agility;
        var flags = new ConditionFlags
        {
            [RecentSavageHitItemFlag] = alive && !player.Closed && !player.LeavingArea
                && player.RecentEvents.Happened(StatConditions.TakenSavageHit, now),
            ["_while_on_full_life"] = CountsAsFullLife(player.PlayerLife, player.PlayerMaxLife, lowLifeSources),
            ["_while_on_low_life"] = lowLife,
            ["_while_not_low_life"] = alive && !lowLife,
            ["_while_not_on_low_life"] = alive && !lowLife,
            ["_while_on_full_mana"] = alive && player.PlayerMana >= player.Pools.Mana,
            ["_while_no_life_is_reserved"] = lifeReservation.Reserved == 0,
            ["_while_no_mana_reserved"] = manaReserved == 0,
            ["_while_on_low_mana"] = alive && (long)player.PlayerMana * 2 <= player.Pools.Mana,
            ["_while_not_on_low_mana"] = alive && (long)player.PlayerMana * 2 > player.Pools.Mana,
            ["_while_on_full_energy_shield"] = alive && player.EnergyShield.Full,
            ["_while_es_full"] = alive && player.EnergyShield.Full,
            ["_on_full_energy_shield"] = alive && player.EnergyShield.Full,
            ["_while_no_energy_shield"] = player.EnergyShield.Current == 0,
            [HaveEnergyShieldCondition] = alive && player.EnergyShield.Current > 0,
            ["_while_moving"] = moving, ["_while_stationary"] = !moving, ["_while_not_moving"] = !moving,
            ["_while_phasing"] = player.IsPhasing,
            ["_while_affected_by_elusive"] = player.IsElusive,
            ["_while_elusive"] = player.IsElusive,
            ["_while_you_have_elusive"] = player.IsElusive,
            ["_while_not_ignited_frozen_shocked"] = !ailments.Ignite && !ailments.Freeze && ailments.Shock == 0,
            ["_while_ignited"] = ailments.Ignite, ["_while_you_are_ignited"] = ailments.Ignite,
            [BurningItemCondition] = alive && (ailments.Ignite
                || player.RighteousFire is { } rf && RighteousFireOwnerCurrentLocked(player, rf)
                || FlameTouchedActive(player)),
            ["_while_chilled"] = ailments.Chill > 0,
            ["_while_frozen"] = ailments.Freeze, ["_while_shocked"] = ailments.Shock > 0,
            ["_while_blinded"] = alive && player.ResourceEffectsState is ResourceEffectsState blindOwner
                && ResourceEffectsCurrent(player, blindOwner) && blindOwner.PlayerBlind.Read(now).Less > 0,
            ["_while_fortified"] = EvaluateFortificationLocked(player, now, lowLifeSources).Value.Current > 0,
            ["_while_life_leeching"] = alive && leech.Life,
            ["_while_mana_leeching"] = alive && leech.Mana,
            ["_while_energy_shield_leeching"] = alive && leech.EnergyShield,
            ["_while_es_leeching"] = alive && leech.EnergyShield,
            ["_while_leeching"] = alive && (leech.Life || leech.Mana || leech.EnergyShield),
        };
        var activeRings = activeItems?.ToArray() ?? EquipmentForStats(player)
            .Where(e => !IsInactive(player, e.Item.Id)).Select(e => e.Item.Item).ToArray();
        foreach (var (key, value) in ConditionalRings(EquipmentForStats(player), activeRings).Flags) flags[key] = value;
        flags["_while_you_have_fortify"] = flags["_while_fortified"];
        flags[StatConditions.UsingPride] = reservationSnapshot.Auras.Any(static aura => aura.EffectId == "Pride");
        // The uniques spell the life/mana states with "when"; the same state answers both keys.
        foreach (var (when, whileKey) in WhenFlags) flags[when] = flags[whileKey];
        var weapons = WeaponProfileFor(player);
        flags["_while_unarmed"] = weapons.Unarmed;
        flags["_while_wielding_staff"] = weapons.Staff;
        flags["_while_wielding_sword"] = weapons.Sword;
        bool flask = player.FlaskEffects.Any || player.UtilityFlaskEffects.ActiveAt(now)
            || MagebloodBelt(player) is { } belt
                && MagebloodFlasks(ItemsOf(player).Items(PlayerInventories.Flask), MagebloodFlaskCount(belt)).Count > 0;
        flags["_while_using_flask"] = flags["_during_flask_effect"] = flask;
        flags["_if_affected_by_no_flasks"] = !flask;
        flags["_while_bleeding"] = player.IncomingAilments.Bleeding(now);
        flags["_while_poisoned"] = player.IncomingAilments.PoisonStacks(now) > 0;
        flags["_while_on_consecrated_ground"] = player.FlaskGrounds.Consecrated;
        bool onslaught = HasIndependentOnslaught(player);
        flags["independent_onslaught"] = onslaught;
        // "while a Pinnacle Boss is in your Presence": the monster scan behind this flag ran on every
        // item-stat lookup (~450 a second in a fight), so work it out only when a worn item carries a mod
        // with the condition stat (LocalInfluenceModRequiresCelestialBossPresence, Stats 15802). The
        // per-item answer is remembered by reference (ItemModStats.LocalValue), so repeat lookups pay
        // only a cache hit per worn item. Otherwise the flag stays absent, which ItemModStats reads as
        // the mod counting as before — and no such mod is worn anyway. The flag rides the stat-sum cache
        // key, so a pinnacle boss entering or leaving presence recomputes the sources.
        if (WornHasCelestialPresenceMod(player, activeItems))
            flags[StatConditions.CelestialBossInPresence] = alive && CelestialBossInPresenceLocked(player, now);
        flags["_while_onslaught_is_active"] = onslaught;
        flags["_while_have_onslaught"] = onslaught;
        flags["_while_channelling"] = player.ChannelSkill != 0;
        flags["_while_not_channelling"] = player.ChannelSkill == 0;
        flags["_while_you_have_active_physical_aegis"] = physicalAegis.Active;
        flags["_while_you_have_depleted_physical_aegis"] = physicalAegis.Depleted;
        bool unhinged = alive && Unhinged(player);
        flags["_while_unhinged"] = unhinged;
        flags["_while_not_unhinged"] = alive && !unhinged;
        flags["_if_you_have_beast_minion"] = HasSourceValidBeastMinion(player);
        flags["_if_golem_summoned_in_past_8_seconds"] =
            GolemSummonedInPastEightSeconds(player, AreaTime(player));
        flags["_in_blood_stance"] = flags["_while_in_blood_stance"] = blood;
        flags["_in_sand_stance"] = flags["_while_in_sand_stance"] = sand;
        flags["_if_changed_stance_recently"] = flags["_while_changed_stance_recently"] = changedStance;
        flags["has_cat_aspect"] = catAspect;
        flags["cursed_by_vulnerability"] = lowLifeSources.GetValueOrDefault(Stat.CursedByVulnerability) != 0;
        flags["_while_cursed"] = alive && flags["cursed_by_vulnerability"];
        flags["_while_not_cursed"] = alive && !flags["_while_cursed"];
        flags["has_cats_stealth"] = catsStealth;
        flags["has_cats_agility"] = catsAgility;
        flags["_while_you_have_cat_aspect"] = catAspect;
        flags["_while_have_cat_aspect"] = catAspect;
        flags["_if_you_have_cat_aspect"] = catAspect;
        flags["_while_you_have_cats_stealth"] = catsStealth;
        flags["_while_have_cats_stealth"] = catsStealth;
        flags["_if_you_have_cats_stealth"] = catsStealth;
        flags["_while_you_have_cat_stealth"] = catsStealth;
        flags["_while_you_have_cats_agility"] = catsAgility;
        flags["_while_have_cats_agility"] = catsAgility;
        flags["_if_you_have_cats_agility"] = catsAgility;
        var charges = ChargesOf(player);
        Func<uint, int> observed = charges.ObservedStat;   // one delegate for the three types
        foreach (var type in Charges.Types)
        {
            SetChargeConditionFlags(flags, type, charges.Effective(type), charges.Limits(type).Maximum,
                charges.Converted(type), alive, ChargeScaling.CountsAsMaximum(type, observed));
        }
        var equipmentFlags = QualifiedEquipmentItemFlags(flags, EquipmentForStats(player), ItemsFor(player), activeRings);
        var avatarFlags = RequalifyAvatarOfFire(player, RequalifyElusive(player,
            RequalifyHerEmbrace(player, equipmentFlags, activeItems, lowLifeSources), lowLifeSources, activeItems), lowLifeSources, activeItems);
        var result = RequalifySourcePhasing(player, avatarFlags, lowLifeSources, player.RecentEvents.Snapshot(now),
            StatConditions.AuraSet(AffectingAuras(player).Select(a => a.EffectId)), now);
        // Sealed: whoever keeps it may pass it to a requalifying step again, which must copy it then.
        if (result is ConditionFlags own) own.Open = false;
        return result;
    }

    /// <summary>The flags one lookup builds (ItemConditionFlags): its requalifying steps set theirs in it while it is open,
    /// instead of each copying all ~170 (8 MB a second of a busy instance's 40, PerfBench busy 2026-11-20).</summary>
    static readonly (string When, string While)[] WhenFlags = [.. new[] { "_on_full_life", "_on_low_life", "_not_on_low_life",
        "_on_full_mana", "_on_low_mana" }.Select(state => ("_when" + state, "_while" + state))];

    sealed class ConditionFlags() : Dictionary<string, bool>(256)
    {
        public bool Open = true;
    }

    /// <summary>The flags with these values: the open map being built, set in place; any other the same map when it already
    /// has them, else a copy (a caller may keep the map it passed).</summary>
    static IReadOnlyDictionary<string, bool> WithFlags(IReadOnlyDictionary<string, bool> flags, params ReadOnlySpan<(string Key, bool Value)> changes)
    {
        if (flags is ConditionFlags { Open: true } building)
        {
            foreach (var (key, value) in changes) building[key] = value;
            return building;
        }
        Dictionary<string, bool>? copy = null;
        foreach (var (key, value) in changes)
            if (copy is not null) copy[key] = value;
            else if (!flags.TryGetValue(key, out bool had) || had != value) (copy = new(flags))[key] = value;
        return copy ?? flags;
    }

    // Qualification can change the active item set after the coherent condition snapshot. Recompute only the
    // reservation predicate from that set and the captured pool/aura inputs; never re-read a timer or expiry owner.
    public static IReadOnlyDictionary<string, bool> RequalifyLifeReservation(
        IReadOnlyDictionary<string, bool> captured, Player player, IEnumerable<Item> activeItems,
        LifeReservationConditionSnapshot snapshot)
    {
        int itemPercent = snapshot.ItemPercentOverride
            ?? ItemReservationPercent(player, activeItems);
        var flags = new Dictionary<string, bool>(captured)
        {
            ["_while_no_life_is_reserved"] = snapshot.WithItemPercent(itemPercent).Reserved == 0,
        };
        return flags;
    }

    static LifeReservation.Value LifeReservationForConditions(Player player, IEnumerable<Item>? activeItems,
        out LifeReservationConditionSnapshot snapshot)
    {
        int? itemPercentOverride = null;
        int itemPercent = itemPercentOverride ?? ItemReservationPercent(player, activeItems);
        int maximumLife = (int)Math.Min(int.MaxValue, player.Pools.Life);
        int efficiency = player.Pools.LifeReservationEfficiencyPct;
        var auras = player.Auras.Values.ToArray();
        int additionalSkillReserved = AdditionalLifeReservation(player);
        snapshot = new(maximumLife, efficiency, itemPercentOverride, additionalSkillReserved, auras);
        return LifeReservation.FromInputs(maximumLife, itemPercent, efficiency, auras, additionalSkillReserved);
    }

    static int ItemReservationPercent(Player player, IEnumerable<Item>? activeItems)
    {
        var active = activeItems?.ToArray() ?? EquipmentForStats(player)
            .Where(e => !IsInactive(player, e.Item.Id)).Select(e => e.Item.Item).ToArray();
        return WornItemSum(player, active).GetValueOrDefault(LifeReservation.ItemPercentStat);
    }

    // A named event is answerable only when a production commit records it. Other mechanics retain their dedicated
    // stat id until their owning subsystem supplies the event; an absent unsupported event is not evidence of false.
    // Each entry needs a production Record call; the slice that adds an event's recorder adds it here, never earlier.
    public static readonly IReadOnlySet<string> TrackedItemRecentEvents = new HashSet<string>(TrackedCurseCastEvents())
    {
        StatConditions.HitEnemy, StatConditions.KilledEnemy, StatConditions.Crit, StatConditions.BeenHit,
        StatConditions.Blocked, StatConditions.BlockedAttack, StatConditions.BlockedSpell,
        StatConditions.SuppressedSpell, StatConditions.TakenSavageHit, StatConditions.BeenStunned,
        StatConditions.BeenFrozen,
        StatConditions.FrozeEnemy, StatConditions.ShockedEnemy, StatConditions.IgnitedEnemy,
        StatConditions.ShatteredEnemy, StatConditions.UsedWarcry, StatConditions.ConsumedCorpse,
        // Accepted-use, mana, flask, stance and charge recorders.
        StatConditions.SpentMana, StatConditions.UsedSkill, StatConditions.UsedMovementSkill, StatConditions.UsedDash,
        StatConditions.UsedFireSkill, StatConditions.UsedColdSkill, StatConditions.UsedVaalSkill,
        StatConditions.UsedRubyFlask, StatConditions.UsedSapphireFlask, StatConditions.UsedTopazFlask,
        StatConditions.UsedAmethystFlask, StatConditions.ChangedStance,
        StatConditions.GainedFrenzyCharge, StatConditions.GainedPowerCharge, StatConditions.GainedEnduranceCharge,
        // Hit outcomes (RecordHit/RecordCriticalOutcome), mine detonation; the curse casts come from the constructor.
        StatConditions.NonCrit, StatConditions.Crit8Seconds, StatConditions.DetonatedMine,
    };
    sealed class ConditionalItemPoolState
    {
        public StatSourcesResult? Applied;
        public bool Building;
        public long Revision, Presented;
        public (int Movement, int Avoid) AppliedElusive;
        public int AppliedOnslaughtEffect;
        public long CheckedInventoryMutations = -1;
        public int CheckedPassivesCount = -1;
        public int CheckedTattooRevision = -1;
        public int CheckedLevel;
        public bool CheckedGod;
        public bool HasConditionalMods;
        public bool HasBeastCompanions;
        public long ItemStatRevision = GameEvents.ItemStatSources.Revision;
    }

    static readonly Store<Player, ConditionalItemPoolState> ConditionalItemPools = new();

    /// <summary>Whether any of the given items carries a mod with a nonzero
    /// <c>local_influence_mod_requires_celestial_boss_presence</c> (Stats 15802, IsLocal): the one
    /// question the pinnacle-presence flag needs, answered per item from the local-sum cache
    /// (<see cref="ItemModStats.LocalValue"/>), never a fresh mod scan or <c>ItemModStats.Sum</c>.</summary>
    static bool WornHasCelestialPresenceMod(Player player, IEnumerable<Item>? activeItems)
    {
        if (activeItems is null) return false;
        var mods = ItemsFor(player).Mods;
        foreach (var item in activeItems)
            if (ItemModStats.LocalValue(item, mods, Stat.LocalInfluenceModRequiresCelestialBossPresence) != 0)
                return true;
        return false;
    }

    static bool WornHasConditionalMods(Player player, IReadOnlyList<ItemModStats.Worn> worn)
    {
        var mods = ItemsFor(player).Mods;
        for (int i = 0; i < worn.Count; i++)
        {
            var item = worn[i].Item;
            for (int m = 0; m < item.Mods.Count; m++)
            {
                var def = mods.ByHash(item.Mods[m].Hash);
                if (def is null) continue;
                for (int s = 0; s < def.Stats.Count; s++)
                {
                    var st = def.Stats[s];
                    if (st.Local) continue;
                    if (StatConditions.Of(st.Name, default) != StatConditions.State.None
                        || StatConditions.IsActiveNonVaalSkillMinionCoefficient(st.Name)
                        || OwnedCountItemStats.IsCoefficient(st.Id)
                        || st.Id == HerEmbraceGain
                        || st.Id == Stat.OnslaughtEffectPlusPct)
                        return true;
                }
            }
        }
        return false;
    }

    static bool HasAnyBeastCompanionState(Player player) =>
        player.Beasts.Active is not null
        || player.Beasts.Pending is not null
        || player.Beasts.SourceGenerations.Count > 0;

    // A changed condition must reach the final pools as well as the raw stat dictionary. Compare effective sources:
    // another hit can extend the same four-second window without requiring another pool build.
    public static bool EnsureConditionalItemPools(Player player)
    {
        var state = ConditionalItemPools.GetOrCreateValue(player);
        if (state.Building || player.Closed || player.LeavingArea) return false;
        var items = ItemsOf(player);
        long invMutations = items.Mutations;
        int passivesCount = player.Character.Passives.Count
            + player.Character.MainAscendancy.Allocated.Count
            + player.Character.DescendancyNodes.Count
            + player.Character.ClusterPassives.Count;
        int tattooRevision = player.Character.TattooRevision;
        int level = player.Pools.Level;
        bool god = player.God;
        var cached = player.StatSourcesCache;
        var elusive = ElusivePanelState(player);
        long itemStatRevision = GameEvents.ItemStatSources.Revision;
        bool providersChanged = state.ItemStatRevision != itemStatRevision;
        // Fast path: when equipment, passives, level, god mode, and StatSourcesCache are unchanged, and no worn item
        // carries a conditional mod or beast companion, skip rebuilding EquipmentStatSources on every incoming hit.
        if (!providersChanged && cached is not null && ReferenceEquals(state.Applied, cached)
            && state.CheckedInventoryMutations == invMutations
            && state.CheckedPassivesCount == passivesCount
            && state.CheckedTattooRevision == tattooRevision
            && state.CheckedLevel == level
            && state.CheckedGod == god
            && !state.HasConditionalMods
            && !state.HasBeastCompanions
            && !HasAnyBeastCompanionState(player)
            && state.AppliedElusive == elusive
            && GameEvents.ExemptItems(player).Count == 0
            && player.IncomingAilments.PoisonStacks(AreaTime(player)) == cached.Key.PoisonStacks)
            return false;
        state.ItemStatRevision = itemStatRevision;
        EquipmentStatSources(player, EquipmentForStats(player));
        ReconcileBeastCompanionsLocked(player);
        var current = player.StatSourcesCache;
        if (current is not null && (providersChanged || state.CheckedInventoryMutations != invMutations || !ReferenceEquals(state.Applied, current)))
        {
            state.CheckedInventoryMutations = invMutations;
            state.CheckedPassivesCount = passivesCount;
            state.CheckedTattooRevision = tattooRevision;
            state.CheckedLevel = level;
            state.CheckedGod = god;
            state.HasConditionalMods = current.Key.Flags?.ContainsKey(StatConditions.CelestialBossInPresence) == true
                || WornHasConditionalMods(player, current.Worn);
            state.HasBeastCompanions = HasAnyBeastCompanionState(player)
                || AllWornGrantedSkillsOf(player).Any(g => BeastSourceEligible(player, g));
        }
        else if (HasAnyBeastCompanionState(player)) state.HasBeastCompanions = true;
        var before = state.Applied;
        bool sameElusive = state.AppliedElusive == elusive;
        int onslaughtEffect = Stat.OnslaughtEffectPlusPct is var effectKey
            ? current?.Sources.GetValueOrDefault(effectKey) ?? 0 : 0;
        bool sameOnslaughtEffect = state.AppliedOnslaughtEffect == onslaughtEffect;
        if (current is null || !providersChanged && ReferenceEquals(before, current) && sameElusive && sameOnslaughtEffect) return false;
        state.Applied = current;
        state.AppliedElusive = elusive;
        state.AppliedOnslaughtEffect = onslaughtEffect;
        // After a Game-module reload the source marker is new but the connection's old final pools survive.
        // Rebuild conditional equipment on first observation instead of accepting an already-expired old bonus.
        bool conditionalEquipment = before is null && (current.Key.Flags?.ContainsKey(StatConditions.CelestialBossInPresence) == true
            || current.Worn.Any(w => w.Item.Mods.Any(m =>
            ItemsFor(player).Mods.ByHash(m.Hash)?.Stats.Any(s => !s.Local
                && (s.Name.Contains("_if_", StringComparison.Ordinal)
                    || s.Name.Contains("_while_", StringComparison.Ordinal)
                    || StatConditions.IsActiveNonVaalSkillMinionCoefficient(s.Name)
                    || OwnedCountItemStats.IsCoefficient(s.Id))) == true)));
        bool sameHerEmbrace = (before?.Key.Flags?.GetValueOrDefault("_while_in_her_embrace") == true)
            == (current.Key.Flags?.GetValueOrDefault("_while_in_her_embrace") == true);
        if (!providersChanged && sameElusive && sameHerEmbrace && sameOnslaughtEffect
            && (before is null && !conditionalEquipment && current.Key.PoisonStacks == 0
            || before is not null && before.Attributes == current.Attributes
            && before.Inactive.SetEquals(current.Inactive)
            && before.Key.PoisonStacks == current.Key.PoisonStacks
            && before.Sources.Count == current.Sources.Count
            && before.Sources.All(p => current.Sources.TryGetValue(p.Key, out int value) && value == p.Value))) return false;
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        player.PlayerLife = LifeReservation.Clamp(player.PlayerLife, LifeReservationOf(player));
        player.PlayerMana = Math.Min(player.PlayerMana, (uint)UnreservedMana(player));
        state.Revision++;
        return true;
    }

    static async Task SendConditionalItemStats(Player player)
    {
        long revision;
        ConditionalItemPoolState state;
        EnsureConditionalItemPools(player);
        state = ConditionalItemPools.GetOrCreateValue(player);
        revision = state.Revision;
        if (revision == state.Presented) return;
        await SendCharacterStats(player, PlayerStatChange.Sources);
        await SendPlayerMana(player);
        // Remember the revision sampled before the await; a later change still needs its own publication.
        state.Presented = revision;
    }
}
