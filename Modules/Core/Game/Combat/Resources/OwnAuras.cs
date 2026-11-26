using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadOwnAurasData()
    {
        SublimeRestrictions = LoadSublimeRestrictions();
    }

    sealed record AuraReconcileInputs(object? ItemSources, ItemCatalog Items, SkillCatalog Skills,
        CharacterStats.Pools Pools, Dictionary<uint, int> Sources,
        (byte Row, InventoryItem Item)[] Equipment, AuraCatalog.Aura[] Auras,
        string[] Remembered, string? SavedStance, bool SavedBlood, string Stance,
        bool ActiveBlood, ushort BloodBuff, int ManaReserved, int LifeReserved,
        bool CanRestore, bool Town, bool Dirty, int Restricted, bool DisabledSpells)
    {
        public static AuraReconcileInputs Capture(Player player)
        {
            var sources = CharacterStatSources(player).Stats;
            var saved = CharacterSaves.Remembered(CharacterName(player));
            return new(player.StatSourcesCache, ItemsFor(player), SkillsFor(player), player.Pools, sources,
                [.. ItemsOf(player).EquipmentSnapshot()], [.. player.Auras.Values],
                [.. saved.Auras], saved.Stance, saved.PetrifiedBlood, player.Stance,
                player.PetrifiedBlood.Active, player.PetrifiedBlood.Buff,
                PlacedSkills.Reserved(player.Pools, player.Placed.All.Values, false),
                PlacedSkills.Reserved(player.Pools, player.Placed.All.Values, true),
                !player.Closed && !player.LeavingArea && !player.RageRetired && player.PlayerLife > 0,
                InTown(player), player.AuraStatsDirty, OwnAuraRestrictionValue(player, new(sources)), SpellsDisabled(player, sources));
        }

        public bool Matches(AuraReconcileInputs b) => ReferenceEquals(ItemSources, b.ItemSources)
            && ReferenceEquals(Items, b.Items) && ReferenceEquals(Skills, b.Skills) && Pools == b.Pools
            && CanRestore == b.CanRestore && Town == b.Town && Dirty == b.Dirty
            && Restricted == b.Restricted && DisabledSpells == b.DisabledSpells
            && SavedStance == b.SavedStance && SavedBlood == b.SavedBlood && Stance == b.Stance
            && ActiveBlood == b.ActiveBlood && BloodBuff == b.BloodBuff
            && ManaReserved == b.ManaReserved && LifeReserved == b.LifeReserved
            && SameInOrder(Sources, b.Sources)
            && Remembered.AsSpan().SequenceEqual(b.Remembered) && Auras.AsSpan().SequenceEqual(b.Auras)
            && Equipment.Length == b.Equipment.Length && Equipment.Zip(b.Equipment).All(p =>
                p.First.Row == p.Second.Row && p.First.Item.Id == p.Second.Item.Id
                && SameSkillItem(p.First.Item.Item, p.Second.Item.Item, Items));
    }
    sealed class AuraReconcileCache { public AuraReconcileInputs? Inputs; }
    static readonly Store<Player, AuraReconcileCache> AuraReconcileCaches = new();

    static (int ArtVariation, bool HideVisual, ushort? SourceVisual, ushort? BuffVisual) ReservationCosmeticVisuals(
        Player player, ushort skillId, string effectId, bool hasSource)
    {
        uint gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId).Gem;
        return ReservationCosmeticVisualsForGem(player, gem, effectId, hasSource);
    }

    static (int ArtVariation, bool HideVisual, ushort? SourceVisual, ushort? BuffVisual) ReservationCosmeticVisualsForGem(
        Player player, uint gem, string effectId, bool hasSource)
    {
        if (gem == 0) return default;
        int variation = SkillArtVariation(player, gem);
        bool hide = SkillCosmeticStat(player, gem, Stat.SkillHideUserBuffEffects) != 0;
        if (variation == 0 && !hide) return default;
        if (effectId == "TempestShield")
            return (variation, hide, null, null);
        ushort? sourceVisual = null;
        ushort? buffVisual = null;
        if (hasSource)
        {
            sourceVisual = hide ? (ushort)0
                : variation < 0 ? Art.BuffVisual("global_auras", variation)
                : effectId switch
                {
                    "Anger" => Art.BuffVisual("anger", variation),
                    "Hatred" => Art.BuffVisual("hatred", variation),
                    "Wrath" => Art.BuffVisual("wrath", variation),
                    "Haste" => Art.BuffVisual("haste", variation),
                    _ => null,
                };
        }
        else
        {
            string? key = effectId switch
            {
                "HeraldOfAsh" => "herald_of_ash_buff",
                "HeraldOfIce" => "herald_of_ice_buff",
                "HeraldOfThunder" => "herald_of_thunder_buff",
                "HeraldOfPurity" => "herald_of_light_buff",
                "HeraldOfAgony" => "herald_of_agony_buff",
                "ArcticArmour" => "arctic_armour_buff",
                _ => null,
            };
            if (key is not null)
            {
                buffVisual = hide
                    ? (effectId == "ArcticArmour" ? Art.BuffVisual(key, 3) : Art.BuffVisual(key, -1))
                    : variation != 0 ? Art.BuffVisual(key, variation) : null;
            }
        }
        return (variation, hide, sourceVisual, buffVisual);
    }

    // Re-emits active reservation buffs whose gem's skill cosmetic just changed so Tempest Shield, auras, heralds and
    // Arctic Armour take the new art immediately without needing a manual re-toggle.
    public static async Task RefreshReservationCosmetics(Player player, uint? gem = null)
    {
        bool changed = false;
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Auras.Count == 0) return;
            var gems = GemSkillEffects(player).ToDictionary(g => g.Id, g => g.Gem);
            foreach (var aura in player.Auras.Values.ToArray())
            {
                if (IsTemporaryAura(player, aura) || !gems.TryGetValue(aura.SkillId, out uint auraGem) || auraGem == 0
                    || (gem is uint target && auraGem != target)) continue;
                var (artVariation, hideVisual, sourceVisual, buffVisual) =
                    ReservationCosmeticVisualsForGem(player, auraGem, aura.EffectId, aura.SourceBuff is not null);
                if (aura.ArtVariation == artVariation && aura.HideVisual == hideVisual
                    && aura.SourceVisual == sourceVisual && aura.BuffVisual == buffVisual) continue;
                player.Auras[aura.SkillId] = aura with
                {
                    Buff = player.NextBuff++,
                    SourceBuff = aura.SourceBuff is not null ? player.NextBuff++ : null,
                    ArtVariation = artVariation,
                    HideVisual = hideVisual,
                    SourceVisual = sourceVisual,
                    BuffVisual = buffVisual,
                };
                changed = true;
            }
            if (changed) player.AuraStatsDirty = true;
        }
        if (changed) await SendOwnAuraChanges(player);
    }

    // Check the current caster and this instance's link group before native use/cooldown publication.
    // Keep accepted Warcry/RF behavior and each caller's original cancellation/ownership predicate.
    public static bool OwnAuraAcknowledgementAllowed(Player player, int effect, ushort skillId, Func<bool> nativeUse)
    {
        return !OwnAuraSkillRestricted(player, effect, skillId) && nativeUse();
    }

    // Raw caster sources only: never CharacterPlayerStats, PlayerSkills, aura bonuses or PoolsFor.
    public static int OwnAuraRestrictionValue(Player player, Dictionary<uint, int>? sources = null)
    {
        sources ??= CharacterStatSources(player).Stats;
        AddItemUtilityFlaskRawStats(new PlainStatMap(sources),
            player.UtilityFlaskEffects.Stats().Concat(player.FlaskGrounds.PlayerStats()), Stats);
        return OwnAuraRestriction.Value(sources);
    }

    static (string Family, uint Key)[] SublimeRestrictions = default!;

    static (string Family, uint Key)[] LoadSublimeRestrictions() =>
        (from family in new[] { "Anger", "Clarity", "Determination", "Discipline", "Grace", "Haste", "Hatred",
            "Malevolence", "Precision", "Pride", "PurityOfElements", "PurityOfFire", "PurityOfIce",
            "PurityOfLightning", "Vitality", "Wrath", "Zealotry" }
         let name = ReservationStatName(family)
         let key = Stats.Key($"your_auras_except_{name}_are_disabled")
         where key.HasValue
         select (family, key.Value)).ToArray();

    static bool SublimeDisables(Player player, int effect, IReadOnlyDictionary<uint, int> sources)
    {
        string family = AuraCatalog.Family(SkillsFor(player).EffectId(effect) ?? "");
        return SublimeRestrictions.Any(rule => rule.Family != family &&
            sources.GetValueOrDefault(rule.Key) != 0);
    }

    // your_spells_are_disabled (Devout of Gruthkul) from the same raw caster sources.
    // The client greys such a skill itself: skill_disabled_by_stat is 0x1520 AND casting_spell (FUN_1428d6330).
    static bool SpellsDisabled(Player player, Dictionary<uint, int>? sources = null)
    {
        uint key = Stat.YourSpellsAreDisabled;
        return ((sources ?? StatSourceValues(player)).GetValueOrDefault(key)) != 0;
    }

    // What the client counts as a spell for that restriction: the casting_spell flag SkillCatalog publishes.
    static bool CastingSpell(Player player, int effect) => SkillsFor(player).IsSpell(effect) && !SkillsFor(player).IsAttack(effect);

    // Auras contains only the current ordinary self-aura executor's source/recipient pairs.
    // Received buffs, RF, flasks and other temporary effects have separate ownership and are untouched.
    // Local policy: suspend runtime effects, retaining ActiveAuras intent; this is not a proven GGG lifecycle.
    // Disabled spells suspend the auras that are spells (every supported one) the same way.
    public static bool SuspendRestrictedOwnAuras(Player player, Dictionary<uint, int>? sources = null)
    {
        if (player.Auras.Count == 0) return false;
        sources ??= CharacterStatSources(player).Stats;
        bool all = OwnAuraRestrictionValue(player, sources) != 0, spells = SpellsDisabled(player, sources);
        var gone = player.Auras.Values.Where(a => (all || SublimeDisables(player, a.Effect, sources!)) && SkillsFor(player).IsAura(a.Effect)
            || spells && CastingSpell(player, a.Effect)).ToArray();
        if (gone.Length == 0) return false;
        foreach (var aura in gone) { player.Auras.Remove(aura.SkillId); EndReservationRuntimeLocked(player, aura); }
        if (gone.Any(a => StanceCatalog.IsStance(a.EffectId))) ReconcileStanceSelectionLocked(player, persist: false);
        player.AuraStatsDirty = true;
        return true;
    }

    sealed record ReservationInputs(StatSourcesResult? Sources, CharacterStats.Pools Pools,
        (byte Row, InventoryItem Item)[] Equipment, AuraCatalog.Aura[] Auras);
    sealed class ReservationInputCache
    {
        public bool Building;
        public ReservationInputs? Inputs;
        // When an own aura's state next changes on its own (AreaTime): the Stats phase reconciles them then.
        public double NextAt = double.PositiveInfinity;
    }
    static readonly Store<Player, ReservationInputCache> ReservationInputCaches = new();
    static ReservationInputs CaptureReservationInputs(Player player) => new(player.StatSourcesCache, player.Pools,
        [.. ItemsOf(player).EquipmentSnapshot()], [.. player.Auras.Values]);

    // Pool readers only check immutable inputs. Skill pricing, source qualification and persistence run on change,
    // while the normal player tick reconciles deadlines. This also handles an equipment edit before publication.
    static void EnsureReservationInputs(Player player)
    {
        if (player.Auras.Count == 0) return;
        var cache = ReservationInputCaches.GetOrCreateValue(player);
        if (cache.Building) return;
        var current = CaptureReservationInputs(player);
        if (cache.Inputs is { } old && ReferenceEquals(old.Sources, current.Sources) && old.Pools == current.Pools
            && old.Auras.AsSpan().SequenceEqual(current.Auras)
            && old.Equipment.Length == current.Equipment.Length && old.Equipment.Zip(current.Equipment).All(p =>
                p.First.Row == p.Second.Row && p.First.Item.Id == p.Second.Item.Id
                && ReferenceEquals(p.First.Item.Item, p.Second.Item.Item))) return;
        EnsureOwnAuraPools(player);
    }

    public static void EnsureOwnAuraPools(Player player)
    {
        var cache = ReservationInputCaches.GetOrCreateValue(player);
        bool building = cache.Building;
        cache.Building = true;
        try
        {
            var raw = player.Auras.Count > 0 ? CharacterStatSources(player).Stats : new Dictionary<uint, int>();
            bool changed = CatAspectReconcileLocked(player, AreaTime(player), raw);
            changed |= IntimidatingCryPoolsChangedLocked(player);
            changed |= AvianReconcileLocked(player, AreaTime(player), raw);
            changed |= CrabReconcileLocked(player, AreaTime(player), raw);
            SpiderReconcileOwnerLocked(player, AreaTime(player));
            changed |= player.Reservations.CrabPoolsDirty;
            changed |= MadnessReconcileLocked(player, AreaTime(player));
            changed |= SuspendRestrictedOwnAuras(player, raw);
            changed |= RepriceOwnAuras(player, out bool poolsRebuilt);
            if (changed)
            {
                if (!poolsRebuilt) player.Pools = PoolsFor(player, player.Pools.Level);
                player.EnergyShield.SetMaximum(player.Pools.EnergyShield);
                player.ApplyMovementSpeed();
                player.PlayerMana = Math.Min(player.PlayerMana, (uint)UnreservedMana(player));
            }
            var life = LifeReservationOf(player);
            player.PlayerLife = LifeReservation.Clamp(player.PlayerLife, life);
        }
        finally
        {
            cache.Building = building;
            if (!building) cache.Inputs = CaptureReservationInputs(player);
            cache.NextAt = OwnAurasNextChangeAt(player);
        }
    }

    // A Cat or Avian phase ends, a Crab barrier is due, a Glorious Madness touch lands or ends.
    static double OwnAurasNextChangeAt(Player player)
    {
        var state = player.Reservations;
        double next = double.PositiveInfinity;
        if (state.CatOwner is not null && state.Cat != ReservationState.CatPhase.None) next = Math.Min(next, state.CatPhaseDeadline);
        if (state.AvianOwner is not null) next = Math.Min(next, state.AvianDeadline);
        if (state.CrabOwner is not null && state.CrabBarriers < state.CrabMaximum) next = Math.Min(next, state.NextCrabBarrier);
        var madness = player.Madness;
        if (madness.Active)
        {
            next = Math.Min(next, madness.NextTouch);
            for (int i = 0; i < 4; i++) if (madness.Touches[i] > 0) next = Math.Min(next, madness.Until[i]);
        }
        return next;
    }

    /// <summary>The Stats phase: the own auras' timed changes (<see cref="OwnAurasNextChangeAt"/>) once due.</summary>
    internal static void StepOwnAuras(Player player)
    {
        ExpireWarcryBuffs(player);
        if (ReservationInputCaches.TryGetValue(player, out var cache) && AreaTime(player) >= cache.NextAt)
            EnsureOwnAuraPools(player);
    }

    // Reservation exemptions and support cost multipliers can change while an aura stays on, including before any
    // equipment/stat notification. Reprice the active owner from current sources here; unchanged auras are left alone.
    sealed record AuraPriceInputs(StatSourcesResult? Qualified, CharacterStats.Pools Pools,
        string Stance, AuraCatalog.Aura[] Auras, Dictionary<uint, int> Raw);
    sealed class AuraPriceCache { public AuraPriceInputs? Last; }
    static readonly Store<Player, AuraPriceCache> AuraPrices = new();

    static bool RepriceOwnAuras(Player player, out bool poolsRebuilt)
    {
        poolsRebuilt = false;
        if (player.Auras.Count == 0) return false;
        var input = new AuraPriceInputs(player.StatSourcesCache, player.Pools, player.Stance,
            [.. player.Auras.Values], CharacterStatSources(player).Stats);
        var cache = AuraPrices.GetOrCreateValue(player);
        if (cache.Last is { } old && ReferenceEquals(old.Qualified, input.Qualified)
            && old.Pools == input.Pools && old.Stance == input.Stance
            && old.Auras.AsSpan().SequenceEqual(input.Auras)
            && SameInOrder(old.Raw, input.Raw)) return false;
        cache.Last = input;
        bool changed = false;
        bool stanceTouched = false;
        bool blessingsDisabled = input.Raw.GetValueOrDefault(Stat.DisableBlessingSkillsAndDisplaySocketedAuraGemsReserveNoMana) != 0;
        var changedAuras = new List<AuraCatalog.Aura>();
        foreach (var aura in player.Auras.Values.ToArray())
        {
            if (IsTemporaryAura(player, aura)) continue;
            var gem = SupportedSkill(player, aura.SkillId);
            bool wasBlessing = IsBlessingAura(aura);
            if (IsBlessing(gem) || IsBlessingSkill(gem) != wasBlessing || wasBlessing && blessingsDisabled
                || AuraHasStat(aura, Stat.SkillAuraAlsoDisablesNonBlessingManaReservationSkills)
                    != (gem.Stats.GetValueOrDefault(Stat.SkillAuraAlsoDisablesNonBlessingManaReservationSkills) != 0))
            {
                CharacterSaves.SetAuraActive(CharacterName(player), aura.EffectId, false);
                player.Auras.Remove(aura.SkillId); EndReservationRuntimeLocked(player, aura);
                changed = true; continue;
            }
            var reservation = SupportedReservationCost(player, aura.SkillId);
            if (reservation is null)
            {
                // A support/source that no longer supplies the reservation cannot remain active.
                player.Auras.Remove(aura.SkillId);
                EndReservationRuntimeLocked(player, aura);
                stanceTouched |= StanceCatalog.IsStance(aura.EffectId);
                changed = true;
                continue;
            }
            var support = AuraSupport(player, aura.SkillId);
            var context = ReservationContext(player, aura.SkillId);
            string? stance = StanceCatalog.IsStance(aura.EffectId)
                ? StanceCatalog.SelectionOf(aura, player.Stance) : null;
            var repriced = aura with
            {
                ManaFlat = reservation.Value.ManaFlat, ManaPercent = reservation.Value.ManaPercent,
                LifeFlat = reservation.Value.LifeFlat, LifePercent = reservation.Value.LifePercent,
                Level = gem.Level, Quality = gem.Quality,
                EffectId = stance is null ? aura.EffectId : StanceCatalog.RuntimeEffectId(aura.EffectId, stance),
                BuffRow = AuraCatalog.BuffRow(stance is null ? aura.EffectId
                    : StanceCatalog.RuntimeEffectId(aura.EffectId, stance), stance),
                SupportEffectPct = support.EffectPct, AffectsSelf = support.AffectsSelf, Stance = stance,
                ReservesLife = context.Life, ReservationEfficiencyPct = context.Efficiency,
                FixedManaReservation = reservation.Value.FixedMana,
            };
            repriced = WithCasterAuraStamps(player, repriced, input.Raw);
            if (repriced == aura) continue;
            player.Auras[aura.SkillId] = repriced;
            changedAuras.Add(repriced);
            changed = true;
        }
        // An exemption can come from an item that also changes the current maximum Mana. Rebuild before deciding
        // affordability so the checked pool and the source snapshot that supplied the new reservation agree.
        if (changedAuras.Count > 0)
        {
            player.Pools = PoolsFor(player, player.Pools.Level);
            poolsRebuilt = true;
        }
        while (!ReservationBudgetFits(player) && player.Auras.Count > 0)
        {
            // Existing unchanged auras keep their admission. Of the reservations whose price just rose, the latest
            // source loses admission first, keeping its saved intent for when it is affordable again. If the
            // percentage did not change, a pool/efficiency input made the whole active set unaffordable, so trim the
            // active set from its latest source in the same deterministic order.
            var overCapacity = changedAuras.Count > 0
                ? changedAuras.Where(a => player.Auras.ContainsKey(a.SkillId)).OrderByDescending(a => a.SkillId)
                : player.Auras.Values.OrderByDescending(a => a.SkillId);
            if (!overCapacity.Any())
                overCapacity = player.Auras.Values.OrderByDescending(a => a.SkillId);
            if (overCapacity.FirstOrDefault() is not { } aura) break;
            if (!player.Auras.Remove(aura.SkillId)) break;
            EndReservationRuntimeLocked(player, aura);
            stanceTouched |= StanceCatalog.IsStance(aura.EffectId);
            changed = true;
            // A trimmed aura may have supplied pool stats. Rebuild before the next capacity decision and make sure
            // EnsureOwnAuraPools does not publish the pre-removal pool.
            player.Pools = PoolsFor(player, player.Pools.Level);
            poolsRebuilt = true;
        }
        if (!poolsRebuilt && changed)
        {
            player.Pools = PoolsFor(player, player.Pools.Level);
            poolsRebuilt = true;
        }
        if (stanceTouched) ReconcileStanceSelectionLocked(player, persist: true);
        if (changed) player.AuraStatsDirty = true;
        return changed;
    }

    public static void ReconcileOwnAuras(Player player, bool validateSkills = false)
    {
        ExpireTemporaryAuras(player);
        ReconcileGuardianBlessingLocked(player);
        // Raw sources remain live (charges, flasks, temporary buffs and their expiry included).
        // Cache only the lifecycle decision after comparing its complete effective inputs. A full
        // replay bypasses it; module reload discards these connection-weak entries automatically.
        var inputs = AuraReconcileInputs.Capture(player);
        var cached = AuraReconcileCaches.GetOrCreateValue(player);
        if (!validateSkills && cached.Inputs is { } previous && previous.Matches(inputs)) return;
        // Each aura's own named effect term, re-stamped from the caster's raw sources whenever they change:
        // the reconcile already holds the fresh sources (inputs), so this adds no new stat read, and the
        // stamped value rides the record to every consumer (Bonus, RawStats, ResistanceStats, Pride, allies
        // and minions) without another lookup. Received auras live in player.ReceivedAuras, not player.Auras,
        // so only the caster's own auras are re-stamped here.
        // Commander of Darkness: the caster's current values, stamped onto each running aura so the allies
        // and minions it reaches learn them (AuraCatalog.Aura's Commander* fields). The reconcile cache
        // compares Sources, so allocating or removing the node re-runs this loop.
        List<AuraCatalog.Aura>? restamped = null;
        foreach (var aura in player.Auras.Values)
        {
            var next = WithCasterAuraStamps(player, aura, inputs.Sources);
            if (!ReferenceEquals(next, aura)) (restamped ??= []).Add(next);
        }
        if (restamped is not null)
        {
            foreach (var updated in restamped) player.Auras[updated.SkillId] = updated;
            player.AuraStatsDirty = true;
        }
        EnsureOwnAuraPools(player);
        // Revalidate the source skill as well as the restriction before queued activation/entry adds.
        // Dirty runtime or presented instances are owned by this lifecycle; pure unpresented pool inputs are not adds.
        if (player.Auras.Count > 0 && (validateSkills || player.AuraStatsDirty))
        {
            var available = GemSkillEffects(player).ToDictionary(g => g.Id, g => g.Effect);
            var gone = player.Auras.Values.Where(a => InTown(player)
                || !available.TryGetValue(a.SkillId, out int effect) || effect != a.Effect
                || GemSkillInstanceDisabled(player, a.Effect, a.SkillId) is not null).ToArray();
            // Worn grants are equipment sources, not resumable gem intent. A removed/inactive host must not restore its
            // aura from stale family intent when another item or area publication follows. A still-valid gem or worn
            // host of the same family keeps the intent and takes ownership at the restore step below.
            var availableFamilies = available.Values
                .Select(effect => AuraCatalog.Family(SkillsFor(player).EffectId(effect) ?? ""))
                .Where(family => family.Length > 0).ToHashSet(StringComparer.Ordinal);
            if (!InTown(player))
                foreach (var aura in gone.Where(a => a.WornGrant is not null
                             && !availableFamilies.Contains(AuraCatalog.Family(a.EffectId)))
                         .GroupBy(a => AuraCatalog.Family(a.EffectId)))
                    CharacterSaves.SetAuraActive(CharacterName(player), aura.First().EffectId, false);
            foreach (var aura in gone) { player.Auras.Remove(aura.SkillId); EndReservationRuntimeLocked(player, aura); }
            if (gone.Length > 0) player.AuraStatsDirty = true;
        }
        if (player.AuraStatsDirty)
        {
            player.Pools = PoolsFor(player, player.Pools.Level);
            player.EnergyShield.SetMaximum(player.Pools.EnergyShield);
            player.ApplyMovementSpeed();
            player.PlayerMana = Math.Min(player.PlayerMana, (uint)UnreservedMana(player));
            player.PlayerLife = LifeReservation.Clamp(player.PlayerLife, LifeReservationOf(player));
        }
        if (!player.Closed && !player.LeavingArea && !player.RageRetired && player.PlayerLife > 0 && !InTown(player))
            RestoreRememberedAuras(player, CharacterSaves.Remembered(CharacterName(player)).Auras);
        SuspendPetrifiedBloodIfUnavailable(player);
        if (!player.Closed && !player.LeavingArea && !player.RageRetired && player.PlayerLife > 0 && !InTown(player))
            RestoreRememberedPetrifiedBlood(player);
        // Remember the inputs actually checked. If reconciliation mutated auras/pools, the next
        // invocation validates the resulting state once rather than blessing an unchecked result.
        cached.Inputs = inputs;
    }

    // Called under the send gate and Monsters lock. Diff against what actually reached this object,
    // rather than prebuilding adds/removes across an await. Old instances end once; new ones start once.
    // Reconcile before constructing any player/Actor/mana snapshot, including another mechanic's refresh.
    public static byte[] WithOwnAuraChanges(Player player, Func<byte[]> snapshot, bool replay = false) =>
        [.. WithOwnAuraRecords(player, () => [snapshot()], replay).SelectMany(p => p)];

    public static byte[][] WithOwnAuraRecords(Player player, Func<byte[][]> snapshot, bool replay = false)
    {
        // At-gate builders are synchronous and hold Monsters. Do not inherit the input handler's
        // older scope across its await; resolve each supported skill once for this snapshot.
        using var skillScope = new PlayerStatScope(player, fresh: true);
        ReconcileOwnAuras(player, validateSkills: replay);
        // Reconciliation can replace pools, remove a source or switch stance. The packet must see
        // the resulting state, not supported gems or character stats captured before that mutation.
        using var packetScope = new PlayerStatScope(player, fresh: true);
        // LoadProgress can restore intent before the object exists. Keep aura publication pending;
        // in particular never prefix a creation snapshot with buffs, Stats, Actor or Life updates.
        if (!player.Connection.PlayerObjectCreated && !player.Connection.PlayerSent) return snapshot();
        var packets = new List<byte[]>();
        packets.AddRange(ReconcileReceivedAuraRecordsLocked(player));
        ClampReservedResources(player);
        // The player's own aura buffs are its auras now (both buffs of each, Generosity's too: its received one only has
        // its icon suppressed); the Buffs component writes what changed.
        player.Area.Buffs.Replace(player.Id, b => b.Source == player.Id && b.Tag is 0 or (ushort)BuffTag.BloodAndSand or (ushort)BuffTag.VaalAura
                && (AuraCatalog.IsAuraRow(b.Row) || TimedVaalAuraBuffRows.ContainsValue(b.Row)),
            player.Auras.Values.SelectMany(aura => AuraBuffs(player.Id, aura, TemporaryAuraSecondsLeft(player, aura))));
        PresentAvianPhaseLocked(player);
        PresentCrabLocked(player);
        PresentSelfVulnerabilityLocked(player);
        PresentMadnessLocked(player);
        // Reconciliation changes Cat phase and earned charges after the caller's validation/pricing scope was built.
        // Publish every following 1b3/Actor payload from a fresh post-change snapshot, then restore that outer scope.
        using var publicationStats = new PlayerStatScope(player, fresh: true);
        if (player.AuraStatsDirty)
        {
            player.AuraStatsDirty = false;
            packets.Add(PlayerStatsOffFrames(player));
            packets.AddRange(PlayerSkillsBatchesIfChanged(player, PlayerSkills(player)));
            player.Frames.Area?.Life.Changed(player);
        }
        var petrified = player.PetrifiedBlood;
        if (petrified.Active)
            player.Area.Buffs.Keep(player.Id, new(PetrifiedBlood.BuffRow, player.Id, petrified.SkillId,
                ActiveSkill: SkillsFor(player).ActionHash(petrified.Effect) is ushort hash ? (petrified.SkillId, hash) : null));
        else player.Area.Buffs.Clear(player.Id, PetrifiedBlood.BuffRow, player.Id);
        if (Lifetap.PresentationRecords(player) is { } lifetap) packets.AddRange(lifetap);
        packets.AddRange(snapshot());
        return [.. packets.Where(p => p.Length > 0)];
    }

    public static Task SendOwnAuraChanges(Player player,
        bool replay = false) => SendWorldRecords(player, player.Id, () =>
        {
            return player.Closed || player.LeavingArea || player.RageRetired
                ? null : WithOwnAuraRecords(player, () => [], replay);
        }, "own aura reconciliation");
}
