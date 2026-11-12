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

    public static IReadOnlyDictionary<string, bool> ItemConditionFlags(ClientState conn, double now,
        IEnumerable<Item>? activeItems = null) =>
        ItemConditionFlags(conn, now, activeItems, out _);

    public static IReadOnlyDictionary<string, bool> ItemConditionFlags(ClientState conn, double now,
        IEnumerable<Item>? activeItems, out LifeReservationConditionSnapshot reservationSnapshot)
    {
        var ailments = conn.ElementalAilments.Snapshot(now);
        var leech = LeechOf(conn).Active(now);
        bool moving = conn.Walk.Moving(now), alive = conn.PlayerLife > 0;
        var lifeReservation = LifeReservationForConditions(conn, activeItems, out reservationSnapshot);
        long manaRequired = (long)AuraCatalog.RequiredMana(conn.Pools, conn.Auras.Values)
            + PetrifiedBlood.RequiredMana(conn.Pools, conn.PetrifiedBlood)
            + PlacedSkills.Reserved(conn.Pools, conn.Placed.All.Values, false);
        int manaReserved = (int)Math.Min(conn.Pools.Mana, Math.Min(int.MaxValue, manaRequired));
        var lowLifeSources = SelfCurseConditionInputs(conn, activeItems, conditionTime: now);
        bool lowLife = alive && CriticalOnLowLife(conn, lowLifeSources);
        var physicalAegis = PhysicalAegisStatus(conn, now);
        string stance = StanceCatalog.Selected(conn.Auras.Values);
        bool blood = stance == StanceCatalog.BloodSelection, sand = stance == StanceCatalog.SandSelection;
        bool changedStance = conn.RecentEvents.Happened(StatConditions.ChangedStance, now);
        bool catAspect = CatAspectRootActiveLocked(conn);
        bool catsStealth = catAspect && conn.Reservations.Cat == ReservationState.CatPhase.Stealth;
        bool catsAgility = catAspect && conn.Reservations.Cat == ReservationState.CatPhase.Agility;
        var flags = new Dictionary<string, bool>
        {
            ["_while_on_full_life"] = CountsAsFullLife(conn.PlayerLife, conn.PlayerMaxLife, lowLifeSources),
            ["_while_on_low_life"] = lowLife,
            ["_while_not_low_life"] = alive && !lowLife,
            ["_while_not_on_low_life"] = alive && !lowLife,
            ["_while_on_full_mana"] = alive && conn.PlayerMana >= conn.Pools.Mana,
            ["_while_no_life_is_reserved"] = lifeReservation.Reserved == 0,
            ["_while_no_mana_reserved"] = manaReserved == 0,
            ["_while_on_low_mana"] = alive && (long)conn.PlayerMana * 2 <= conn.Pools.Mana,
            ["_while_not_on_low_mana"] = alive && (long)conn.PlayerMana * 2 > conn.Pools.Mana,
            ["_while_on_full_energy_shield"] = alive && conn.EnergyShield.Full,
            ["_while_es_full"] = alive && conn.EnergyShield.Full,
            ["_on_full_energy_shield"] = alive && conn.EnergyShield.Full,
            ["_while_no_energy_shield"] = conn.EnergyShield.Current == 0,
            ["_while_moving"] = moving, ["_while_stationary"] = !moving, ["_while_not_moving"] = !moving,
            ["_while_phasing"] = conn.IsPhasing,
            ["_while_not_ignited_frozen_shocked"] = !ailments.Ignite && !ailments.Freeze && ailments.Shock == 0,
            ["_while_ignited"] = ailments.Ignite, ["_while_you_are_ignited"] = ailments.Ignite,
            ["_while_chilled"] = ailments.Chill > 0,
            ["_while_frozen"] = ailments.Freeze, ["_while_shocked"] = ailments.Shock > 0,
            ["_while_blinded"] = alive && conn.ResourceEffectsState is ResourceEffectsState blindOwner
                && ResourceEffectsCurrent(conn, blindOwner) && blindOwner.PlayerBlind.Read(now).Less > 0,
            ["_while_fortified"] = FortificationOf(conn).Read(now).Current > 0,
            ["_while_life_leeching"] = alive && leech.Life,
            ["_while_mana_leeching"] = alive && leech.Mana,
            ["_while_energy_shield_leeching"] = alive && leech.EnergyShield,
            ["_while_es_leeching"] = alive && leech.EnergyShield,
            ["_while_leeching"] = alive && (leech.Life || leech.Mana || leech.EnergyShield),
        };
        var activeRings = activeItems?.ToArray() ?? ItemsOf(conn).EquipmentSnapshot()
            .Where(e => !IsInactive(conn, e.Item.Id)).Select(e => e.Item.Item).ToArray();
        foreach (var (key, value) in ConditionalRings(ItemsOf(conn).EquipmentSnapshot(), activeRings).Flags) flags[key] = value;
        flags["_while_you_have_fortify"] = flags["_while_fortified"];
        // The uniques spell the life/mana states with "when"; the same state answers both keys.
        foreach (string state in new[] { "_on_full_life", "_on_low_life", "_on_full_mana", "_on_low_mana" })
            flags["_when" + state] = flags["_while" + state];
        var weapons = WeaponProfileFor(conn);
        flags["_while_unarmed"] = weapons.Unarmed;
        flags["_while_wielding_staff"] = weapons.Staff;
        flags["_while_wielding_sword"] = weapons.Sword;
        bool flask = conn.FlaskEffects.Any || conn.UtilityFlaskEffects.ActiveAt(now)
            || MagebloodBelt(conn) is { } belt
                && MagebloodFlasks(ItemsOf(conn).Items(PlayerInventories.Flask), MagebloodFlaskCount(belt)).Count > 0;
        flags["_while_using_flask"] = flags["_during_flask_effect"] = flask;
        flags["_if_affected_by_no_flasks"] = !flask;
        flags["_while_bleeding"] = conn.IncomingAilments.Bleeding(now);
        flags["_while_poisoned"] = conn.IncomingAilments.PoisonStacks(now) > 0;
        flags["_while_on_consecrated_ground"] = conn.FlaskGrounds.Consecrated;
        bool onslaught = HasIndependentOnslaught(conn);
        flags["independent_onslaught"] = onslaught;
        flags["_while_onslaught_is_active"] = onslaught;
        flags["_while_have_onslaught"] = onslaught;
        flags["_while_channelling"] = conn.ChannelSkill != 0;
        flags["_while_not_channelling"] = conn.ChannelSkill == 0;
        flags["_while_you_have_active_physical_aegis"] = physicalAegis.Active;
        flags["_while_you_have_depleted_physical_aegis"] = physicalAegis.Depleted;
        flags["_while_unhinged"] = alive && Unhinged(conn);
        flags["_while_not_unhinged"] = alive && !Unhinged(conn);
        flags["_if_you_have_beast_minion"] = HasSourceValidBeastMinion(conn);
        flags["_if_golem_summoned_in_past_8_seconds"] =
            GolemSummonedInPastEightSeconds(conn, AreaTime(conn));
        flags["_in_blood_stance"] = flags["_while_in_blood_stance"] = blood;
        flags["_in_sand_stance"] = flags["_while_in_sand_stance"] = sand;
        flags["_if_changed_stance_recently"] = flags["_while_changed_stance_recently"] = changedStance;
        flags["has_cat_aspect"] = catAspect;
        flags["cursed_by_vulnerability"] = Stats.Key("cursed_by_vulnerability") is uint curseKey
            && lowLifeSources.GetValueOrDefault(curseKey) != 0;
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
        var charges = ChargesOf(conn);
        foreach (var type in Charges.Types)
        {
            SetChargeConditionFlags(flags, type, charges.Effective(type), charges.Limits(type).Maximum,
                charges.Converted(type), alive);
        }
        var avatarFlags = RequalifyAvatarOfFire(conn, RequalifyElusive(conn,
            RequalifyHerEmbrace(conn, flags, activeItems), lowLifeSources, activeItems), lowLifeSources, conn.PlayerStats, activeItems);
        return RequalifySourcePhasing(conn, avatarFlags, lowLifeSources, conn.PlayerStats, conn.RecentEvents.Snapshot(now),
            StatConditions.AuraSet(AffectingAuras(conn).Select(a => a.EffectId)), now);
    }

    // Qualification can change the active item set after the coherent condition snapshot. Recompute only the
    // reservation predicate from that set and the captured pool/aura inputs; never re-read a timer or expiry owner.
    public static IReadOnlyDictionary<string, bool> RequalifyLifeReservation(
        IReadOnlyDictionary<string, bool> captured, ClientState conn, IEnumerable<Item> activeItems,
        LifeReservationConditionSnapshot snapshot)
    {
        int itemPercent = snapshot.ItemPercentOverride
            ?? ItemReservationPercent(conn, activeItems);
        var flags = new Dictionary<string, bool>(captured)
        {
            ["_while_no_life_is_reserved"] = snapshot.WithItemPercent(itemPercent).Reserved == 0,
        };
        return flags;
    }

    static LifeReservation.Value LifeReservationForConditions(ClientState conn, IEnumerable<Item>? activeItems,
        out LifeReservationConditionSnapshot snapshot)
    {
        int? itemPercentOverride = null;
        if (Stats.Key(LifeReservation.ItemPercentStat) is uint key && conn.PlayerStats.TryGetValue(key, out int set))
            itemPercentOverride = set;
        int itemPercent = itemPercentOverride ?? ItemReservationPercent(conn, activeItems);
        int maximumLife = (int)Math.Min(int.MaxValue, conn.Pools.Life);
        int efficiency = conn.Pools.LifeReservationEfficiencyPct;
        var auras = conn.Auras.Values.ToArray();
        int additionalSkillReserved = AdditionalLifeReservation(conn);
        snapshot = new(maximumLife, efficiency, itemPercentOverride, additionalSkillReserved, auras);
        return LifeReservation.FromInputs(maximumLife, itemPercent, efficiency, auras, additionalSkillReserved);
    }

    static int ItemReservationPercent(ClientState conn, IEnumerable<Item>? activeItems)
    {
        var active = activeItems?.ToArray() ?? ItemsOf(conn).EquipmentSnapshot()
            .Where(e => !IsInactive(conn, e.Item.Id)).Select(e => e.Item.Item).ToArray();
        return Stats.Key(LifeReservation.ItemPercentStat) is uint key
            && conn.PlayerStats.TryGetValue(key, out int replacement) ? replacement
            : ItemModStats.Sum(active, ItemsFor(conn).Mods).GetValueOrDefault(LifeReservation.ItemPercentStat);
    }

    // A named event is answerable only when a production commit records it. Other mechanics retain their dedicated
    // stat id until their owning subsystem supplies the event; an absent unsupported event is not evidence of false.
    public static readonly IReadOnlySet<string> TrackedItemRecentEvents = CreateTrackedItemRecentEvents();
    static HashSet<string> CreateTrackedItemRecentEvents()
    {
        var tracked = new HashSet<string>
        {
            StatConditions.HitEnemy, StatConditions.KilledEnemy, StatConditions.Crit, StatConditions.BeenHit,
            StatConditions.Blocked, StatConditions.BlockedAttack, StatConditions.BlockedSpell,
            StatConditions.SuppressedSpell, StatConditions.TakenSavageHit, StatConditions.BeenStunned,
            StatConditions.BeenFrozen,
            StatConditions.SpentMana,
            StatConditions.FrozeEnemy, StatConditions.ShockedEnemy, StatConditions.IgnitedEnemy,
            StatConditions.ShatteredEnemy, StatConditions.UsedWarcry, StatConditions.ConsumedCorpse,
            StatConditions.UsedMovementSkill, StatConditions.UsedFireSkill, StatConditions.UsedColdSkill,
            StatConditions.UsedSkill, StatConditions.UsedVaalSkill,
            StatConditions.UsedDash, StatConditions.UsedRubyFlask, StatConditions.UsedSapphireFlask,
            StatConditions.UsedTopazFlask, StatConditions.UsedAmethystFlask,
            StatConditions.NonCrit, StatConditions.Crit8Seconds,
            StatConditions.ChangedStance,
            StatConditions.DetonatedMine,
            StatConditions.GainedFrenzyCharge, StatConditions.GainedPowerCharge, StatConditions.GainedEnduranceCharge,
        };
        tracked.UnionWith(TrackedCurseCastEvents());
        return tracked;
    }
    sealed class ConditionalItemPoolState
    {
        public StatSourcesResult? Applied;
        public bool Building;
        public long Revision, Presented;
        public (int Movement, int Avoid) AppliedElusive;
        public int AppliedOnslaughtEffect;
    }

    static readonly ConditionalWeakTable<ClientState, ConditionalItemPoolState> ConditionalItemPools = new();

    // A changed condition must reach the final pools as well as the raw stat dictionary. Compare effective sources:
    // another hit can extend the same four-second window without requiring another pool build.
    public static bool EnsureConditionalItemPools(ClientState conn)
    {
        lock (conn.Monsters)
        {
            var state = ConditionalItemPools.GetOrCreateValue(conn);
            if (state.Building || conn.Closed || conn.LeavingArea) return false;
            EquipmentStatSources(conn, ItemsOf(conn).EquipmentSnapshot());
            ReconcileBeastCompanionsLocked(conn);
            var current = conn.StatSourcesCache;
            var before = state.Applied;
            var elusive = ElusivePanelState(conn);
            bool sameElusive = state.AppliedElusive == elusive;
            int onslaughtEffect = Stats.Key("onslaught_effect_+%") is uint effectKey
                ? conn.PlayerStats.TryGetValue(effectKey, out int replacement) ? replacement
                    : current?.Sources.GetValueOrDefault(effectKey) ?? 0 : 0;
            bool sameOnslaughtEffect = state.AppliedOnslaughtEffect == onslaughtEffect;
            if (current is null || ReferenceEquals(before, current) && sameElusive && sameOnslaughtEffect) return false;
            state.Applied = current;
            state.AppliedElusive = elusive;
            state.AppliedOnslaughtEffect = onslaughtEffect;
            // After a Game-module reload the source marker is new but the connection's old final pools survive.
            // Rebuild conditional equipment on first observation instead of accepting an already-expired old bonus.
            bool conditionalEquipment = before is null && current.Worn.Any(w => w.Item.Mods.Any(m =>
                ItemsFor(conn).Mods.ByHash(m.Hash)?.Stats.Any(s => !s.Local
                    && (s.Id.Contains("_if_", StringComparison.Ordinal)
                        || s.Id.Contains("_while_", StringComparison.Ordinal)
                        || StatConditions.IsActiveNonVaalSkillMinionCoefficient(s.Id))) == true));
            bool sameHerEmbrace = (before?.Key.Flags?.GetValueOrDefault("_while_in_her_embrace") == true)
                == (current.Key.Flags?.GetValueOrDefault("_while_in_her_embrace") == true);
            if (sameElusive && sameHerEmbrace && sameOnslaughtEffect
                && (before is null && !conditionalEquipment && current.Key.PoisonStacks == 0
                || before is not null && before.Attributes == current.Attributes
                && before.Inactive.SetEquals(current.Inactive)
                && before.Key.PoisonStacks == current.Key.PoisonStacks
                && before.Sources.Count == current.Sources.Count
                && before.Sources.All(p => current.Sources.TryGetValue(p.Key, out int value) && value == p.Value))) return false;
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
            conn.PlayerLife = LifeReservation.Clamp(conn.PlayerLife, LifeReservationOf(conn));
            conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
            state.Revision++;
            return true;
        }
    }

    static async Task SendConditionalItemStats(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        long revision;
        ConditionalItemPoolState state;
        lock (conn.Monsters)
        {
            EnsureConditionalItemPools(conn);
            state = ConditionalItemPools.GetOrCreateValue(conn);
            revision = state.Revision;
            if (revision == state.Presented) return;
        }
        await SendCharacterStats(remote, stream, ciphers, conn, "conditional item stats changed");
        await SendPlayerMana(remote, stream, ciphers, conn);
        await Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            lock (conn.Monsters)
                return conn.Closed || conn.LeavingArea ? null
                    : PlayerLifeUpdate(conn);
        });
        // Remember the revision sampled before the await; a later change still needs its own publication.
        lock (conn.Monsters) state.Presented = revision;
    }
}
