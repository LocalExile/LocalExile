using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // Check the current caster and this instance's link group before native use/cooldown publication.
    // Keep accepted Warcry/RF behavior and each caller's original cancellation/ownership predicate.
    internal static bool OwnAuraAcknowledgementAllowed(ClientState conn, int effect, ushort skillId, Func<bool> nativeUse)
    {
        lock (conn.Monsters)
            return !OwnAuraSkillRestricted(conn, effect, skillId) && nativeUse();
    }

    // Raw caster sources only: never CharacterPlayerStats, PlayerSkills, aura bonuses or PoolsFor.
    internal static int OwnAuraRestrictionValue(ClientState conn, Dictionary<uint, int>? sources = null)
    {
        lock (conn.Monsters)
        {
            sources ??= CharacterStatSources(conn).Stats;
            UtilityFlasks.AddRawStats(sources,
                conn.UtilityFlaskEffects.Stats().Concat(conn.FlaskGrounds.PlayerStats()), Stats);
            return OwnAuraRestriction.Value(sources, conn.PlayerStats);
        }
    }

    // your_spells_are_disabled (Devout of Gruthkul) from the same raw caster sources; a developer value replaces them.
    // The client greys such a skill itself: skill_disabled_by_stat is 0x1520 AND casting_spell (FUN_1428cf770).
    static bool SpellsDisabled(ClientState conn, Dictionary<uint, int>? sources = null)
    {
        if (Stats.Key("your_spells_are_disabled") is not uint key) return false;
        lock (conn.Monsters)
            return (conn.PlayerStats.TryGetValue(key, out int set) ? set
                : (sources ?? CharacterStatSources(conn).Stats).GetValueOrDefault(key)) != 0;
    }

    // What the client counts as a spell for that restriction: the casting_spell flag SkillCatalog publishes.
    static bool CastingSpell(ClientState conn, int effect) => SkillsFor(conn).IsSpell(effect) && !SkillsFor(conn).IsAttack(effect);

    // Auras contains only the current ordinary self-aura executor's source/recipient pairs.
    // Received buffs, RF, flasks and other temporary effects have separate ownership and are untouched.
    // Local policy: suspend runtime effects, retaining ActiveAuras intent; this is not a proven GGG lifecycle.
    // Disabled spells suspend the auras that are spells (every supported one) the same way.
    internal static bool SuspendRestrictedOwnAuras(ClientState conn, Dictionary<uint, int>? sources = null)
    {
        if (conn.Auras.Count == 0) return false;
        lock (conn.Monsters) sources ??= CharacterStatSources(conn).Stats;
        bool all = OwnAuraRestrictionValue(conn, sources) != 0, spells = SpellsDisabled(conn, sources);
        var gone = conn.Auras.Values.Where(a => all && SkillsFor(conn).IsAura(a.Effect)
            || spells && CastingSpell(conn, a.Effect)).ToArray();
        if (gone.Length == 0) return false;
        foreach (var aura in gone) { conn.Auras.Remove(aura.SkillId); EndReservationRuntimeLocked(conn, aura); }
        if (gone.Any(a => StanceCatalog.IsStance(a.EffectId))) ReconcileStanceSelectionLocked(conn, persist: false);
        conn.AuraStatsDirty = true;
        return true;
    }

    internal static void EnsureOwnAuraPools(ClientState conn)
    {
        lock (conn.Monsters)
        {
            bool changed = CatAspectReconcileLocked(conn, AreaTime(conn));
            changed |= AvianReconcileLocked(conn, AreaTime(conn));
            changed |= CrabReconcileLocked(conn, AreaTime(conn));
            changed |= conn.Reservations.CrabPoolsDirty;
            changed |= MadnessReconcileLocked(conn, AreaTime(conn));
            changed |= SuspendRestrictedOwnAuras(conn);
            changed |= RepriceOwnAuras(conn, out bool poolsRebuilt);
            if (changed)
            {
                if (!poolsRebuilt) conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeed();
                conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
            }
            var life = LifeReservationOf(conn);
            conn.PlayerLife = LifeReservation.Clamp(conn.PlayerLife, life);
        }
    }

    // Reservation exemptions and support cost multipliers can change while an aura stays on, including before any
    // equipment/stat notification. Reprice the active owner from current sources here; unchanged auras are left alone.
    static bool RepriceOwnAuras(ClientState conn, out bool poolsRebuilt)
    {
        poolsRebuilt = false;
        bool changed = false;
        bool stanceTouched = false;
        var changedAuras = new List<AuraCatalog.Aura>();
        foreach (var aura in conn.Auras.Values.ToArray())
        {
            var reservation = SupportedReservationCost(conn, aura.SkillId);
            if (reservation is null)
            {
                // A support/source that no longer supplies the reservation cannot remain active.
                conn.Auras.Remove(aura.SkillId);
                EndReservationRuntimeLocked(conn, aura);
                CharacterSaves.SetAuraActive(CharacterName(conn), aura.EffectId, false);
                stanceTouched |= StanceCatalog.IsStance(aura.EffectId);
                changed = true;
                continue;
            }
            var gem = SupportedSkill(conn, aura.SkillId);
            var support = AuraSupport(conn, aura.SkillId);
            var context = ReservationContext(conn, aura.SkillId);
            string? stance = StanceCatalog.IsStance(aura.EffectId)
                ? StanceCatalog.SelectionOf(aura, conn.Stance) : null;
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
            };
            if (repriced == aura) continue;
            conn.Auras[aura.SkillId] = repriced;
            changedAuras.Add(repriced);
            changed = true;
        }
        // An exemption can come from an item that also changes the current maximum Mana. Rebuild before deciding
        // affordability so the checked pool and the source snapshot that supplied the new reservation agree.
        if (changedAuras.Count > 0)
        {
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            poolsRebuilt = true;
        }
        while (!ReservationBudgetFits(conn) && conn.Auras.Count > 0)
        {
            // Existing unchanged auras keep their admission. Of the reservations whose price just rose, the latest
            // source loses admission first; its intent is cleared so the next reconcile cannot resurrect it. If the
            // percentage did not change, a pool/efficiency input made the whole active set unaffordable, so trim the
            // active set from its latest source in the same deterministic order.
            var overCapacity = changedAuras.Count > 0
                ? changedAuras.Where(a => conn.Auras.ContainsKey(a.SkillId)).OrderByDescending(a => a.SkillId)
                : conn.Auras.Values.OrderByDescending(a => a.SkillId);
            if (!overCapacity.Any())
                overCapacity = conn.Auras.Values.OrderByDescending(a => a.SkillId);
            if (overCapacity.FirstOrDefault() is not { } aura) break;
            if (!conn.Auras.Remove(aura.SkillId)) break;
            EndReservationRuntimeLocked(conn, aura);
            CharacterSaves.SetAuraActive(CharacterName(conn), aura.EffectId, false);
            stanceTouched |= StanceCatalog.IsStance(aura.EffectId);
            changed = true;
            // A trimmed aura may have supplied pool stats. Rebuild before the next capacity decision and make sure
            // EnsureOwnAuraPools does not publish the pre-removal pool.
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            poolsRebuilt = true;
        }
        if (!poolsRebuilt && changed)
        {
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            poolsRebuilt = true;
        }
        if (stanceTouched) ReconcileStanceSelectionLocked(conn, persist: true);
        if (changed) conn.AuraStatsDirty = true;
        return changed;
    }

    internal static void ReconcileOwnAuras(ClientState conn, bool validateSkills = false)
    {
        EnsureOwnAuraPools(conn);
        // Revalidate the source skill as well as the restriction before queued activation/entry adds.
        // Dirty runtime or presented instances are owned by this lifecycle; pure unpresented pool inputs are not adds.
        if (conn.Auras.Count > 0 && (validateSkills || conn.AuraStatsDirty || conn.PresentedAuras.Count > 0))
        {
            var available = GemSkillEffects(conn).ToDictionary(g => g.Id, g => g.Effect);
            var gone = conn.Auras.Values.Where(a => InTown(conn)
                || !available.TryGetValue(a.SkillId, out int effect) || effect != a.Effect
                || GemSkillInstanceDisabled(conn, a.Effect, a.SkillId) is not null).ToArray();
            // Worn grants are equipment sources, not resumable gem intent. A removed/inactive host must not restore its
            // aura from stale family intent when another item or area publication follows. A still-valid gem or worn
            // host of the same family keeps the intent and takes ownership at the restore step below.
            var availableFamilies = available.Values
                .Select(effect => AuraCatalog.Family(SkillsFor(conn).EffectId(effect) ?? ""))
                .Where(family => family.Length > 0).ToHashSet(StringComparer.Ordinal);
            if (!InTown(conn))
                foreach (var aura in gone.Where(a => a.WornGrant is not null
                             && !availableFamilies.Contains(AuraCatalog.Family(a.EffectId)))
                         .GroupBy(a => AuraCatalog.Family(a.EffectId)))
                    CharacterSaves.SetAuraActive(CharacterName(conn), aura.First().EffectId, false);
            foreach (var aura in gone) { conn.Auras.Remove(aura.SkillId); EndReservationRuntimeLocked(conn, aura); }
            if (gone.Length > 0 || conn.AuraStatsDirty)
            {
                conn.AuraStatsDirty = true;
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeed();
                conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
                conn.PlayerLife = LifeReservation.Clamp(conn.PlayerLife, LifeReservationOf(conn));
            }
        }
        if (!conn.Closed && !conn.LeavingArea && !conn.RageRetired && conn.PlayerLife > 0 && !InTown(conn))
            RestoreRememberedAuras(conn, CharacterSaves.Load(CharacterName(conn))?.ActiveAuras ?? []);
        SuspendPetrifiedBloodIfUnavailable(conn);
        if (!conn.Closed && !conn.LeavingArea && !conn.RageRetired && conn.PlayerLife > 0 && !InTown(conn))
            RestoreRememberedPetrifiedBlood(conn);
    }

    // Called under the send gate and Monsters lock. Diff against what actually reached this object,
    // rather than prebuilding adds/removes across an await. Old instances end once; new ones start once.
    // Reconcile before constructing any player/Actor/mana snapshot, including another mechanic's refresh.
    internal static byte[] WithOwnAuraChanges(ClientState conn, Func<byte[]> snapshot, bool replay = false)
    {
        ReconcileOwnAuras(conn, validateSkills: replay);
        // LoadProgress can restore intent before the object exists. Keep aura publication pending;
        // in particular never prefix a creation snapshot with buffs, Stats, Actor or Life updates.
        if (!conn.PlayerObjectCreated && !conn.PlayerSent) return snapshot();
        var packets = new List<byte[]>();
        packets.Add(ReconcileReceivedAurasLocked(conn));
        ClampReservedResources(conn);
        bool Same(AuraCatalog.Aura a, AuraCatalog.Aura b) => a.Buff == b.Buff && a.SourceBuff == b.SourceBuff;
        foreach (var old in conn.PresentedAuras.Values)
            if (!conn.Auras.TryGetValue(old.SkillId, out var current) || !Same(old, current))
                packets.AddRange(AuraCatalog.EndPackets(old, PlayerId));
        foreach (var id in conn.PresentedAuras.Keys.ToArray())
            if (!conn.Auras.TryGetValue(id, out var current) || !Same(conn.PresentedAuras[id], current))
                conn.PresentedAuras.Remove(id);
        if (conn.AuraStatsDirty || replay)
            foreach (var aura in conn.Auras.Values)
                if (!conn.PresentedAuras.ContainsKey(aura.SkillId))
                {
                    packets.AddRange(AuraBuffs(aura));
                    conn.PresentedAuras[aura.SkillId] = aura;
                }
        PresentCatPhaseLocked(conn, packets);
        PresentCatOnslaughtLocked(conn, packets);
        PresentAvianPhaseLocked(conn, packets);
        PresentCrabLocked(conn, packets);
        PresentSelfVulnerabilityLocked(conn, packets);
        PresentMadnessLocked(conn, packets);
        // Reconciliation changes Cat phase and earned charges after the caller's validation/pricing scope was built.
        // Publish every following 1b3/Actor payload from a fresh post-change snapshot, then restore that outer scope.
        using var publicationStats = new PlayerStatScope(conn, fresh: true);
        if (conn.AuraStatsDirty)
        {
            conn.AuraStatsDirty = false;
            packets.Add(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
            packets.Add(PlayerSkillsIfChanged(conn, PlayerSkills(conn)));
            packets.Add(InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife, conn.PlayerReservedLife));
            packets.Add(InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMana,
                (uint)ReservedMana(conn), pool: ManaPool));
            packets.Add(PlayerLifeUpdate(conn));
        }
        var petrified = conn.PetrifiedBlood;
        if (petrified.PresentedBuff != 0 && (!petrified.Active || petrified.Buff != petrified.PresentedBuff))
        {
            packets.Add(InstanceEntities.RemoveBuff(PlayerId, petrified.PresentedBuff));
            petrified.PresentedBuff = 0;
        }
        if (petrified.Active && petrified.PresentedBuff == 0)
        {
            packets.Add(InstanceEntities.AddBuff(PlayerId, petrified.Buff, PetrifiedBlood.BuffRow, PlayerId,
                petrified.SkillId, activeSkill: SkillsFor(conn).ActionHash(petrified.Effect) is ushort hash
                    ? (petrified.SkillId, hash) : null));
            petrified.PresentedBuff = petrified.Buff;
        }
        if (Lifetap.Presentation(conn) is { } lifetap) packets.Add(lifetap);
        packets.Add(snapshot());
        return [.. packets.SelectMany(p => p)];
    }

    // Cat's two phase rows are owned by the exact accepted root aura. Build their diff at this send gate, so an add
    // queued for an old owner can never reach the wire after that owner was removed or replaced.
    static void PresentCatPhaseLocked(ClientState conn, List<byte[]> packets)
    {
        var state = conn.Reservations;
        bool active = CatAspectRootActiveLocked(conn);
        IReadOnlyList<uint> phaseValues = [];
        if (active)
        {
            var rawStats = CharacterStatSources(conn).Stats;
            Func<string, int> raw = name => Stats.Key(name) is uint key
                ? conn.PlayerStats.TryGetValue(key, out int replacement) ? replacement
                    : rawStats.GetValueOrDefault(key)
                : 0;
            phaseValues = CatAspectPhaseValues(state.Cat,
                CatAspectPhaseStatsLocked(conn, conn.Pools.AuraEffectPct, raw));
        }
        int value1 = phaseValues.Count > 0 ? unchecked((int)phaseValues[0]) : 0;
        int value2 = phaseValues.Count > 1 ? unchecked((int)phaseValues[1]) : 0;
        int value3 = phaseValues.Count > 2 ? unchecked((int)phaseValues[2]) : 0;
        ushort row = active ? CatAspectPhaseRow(state.Cat) : (ushort)0;
        bool sameValues = state.PresentedCatPhaseRow == row
            && state.PresentedCatValue1 == value1 && state.PresentedCatValue2 == value2
            && state.PresentedCatValue3 == value3;
        if (active && state.PresentedCatPhaseBuff != 0
            && state.CatPhaseBuff == state.PresentedCatPhaseBuff && !sameValues)
            state.CatPhaseBuff = conn.NextBuff++;
        ushort wanted = active ? state.CatPhaseBuff : (ushort)0;
        if (state.PresentedCatPhaseBuff != 0 && (state.PresentedCatPhaseBuff != wanted || !sameValues))
        {
            packets.Add(InstanceEntities.RemoveBuff(PlayerId, state.PresentedCatPhaseBuff));
            state.PresentedCatPhaseBuff = 0;
        }
        if (active && wanted != 0 && (state.PresentedCatPhaseBuff != wanted || !sameValues))
        {
            float remaining = (float)Math.Max(0, state.CatPhaseDeadline - AreaTime(conn));
            // Cat phase rows are not recovery rows, so 0x1c1 carries no GrantedStats values here.
            packets.Add(InstanceEntities.AddBuff(PlayerId, wanted, CatAspectPhaseRow(state.Cat), PlayerId,
                state.CatOwner!.Value.Skill, u68: BitConverter.SingleToUInt32Bits(remaining)));
            state.PresentedCatPhaseBuff = wanted;
            if (!sameValues) conn.AuraStatsDirty = true;
        }
        state.PresentedCatPhaseRow = row;
        state.PresentedCatValue1 = value1;
        state.PresentedCatValue2 = value2;
        state.PresentedCatValue3 = value3;
        state.PresentedCatRevision = state.CatRevision;
    }

    // Cat owns this presentation independently of the kill/flask Onslaught state. Its stats have already composed
    // through UtilityStats; this only keeps one BuffDefinitions row 215 instance on the wire for the current Agility.
    static void PresentCatOnslaughtLocked(ClientState conn, List<byte[]> packets)
    {
        var state = conn.Reservations;
        var onslaught = ItemsFor(conn).Onslaught;
        bool active = onslaught is not null && CatOnslaughtActiveLocked(conn);
        if (active && state.CatOnslaughtBuff == 0) state.CatOnslaughtBuff = conn.NextBuff++;
        if (active && state.PresentedCatOnslaughtBuff != 0
            && state.CatOnslaughtBuff == state.PresentedCatOnslaughtBuff
            && state.PresentedCatOnslaughtPhaseBuff != state.PresentedCatPhaseBuff)
            state.CatOnslaughtBuff = conn.NextBuff++;
        ushort wanted = active ? state.CatOnslaughtBuff : (ushort)0;
        if (state.PresentedCatOnslaughtBuff != 0
            && state.PresentedCatOnslaughtBuff != wanted)
        {
            packets.Add(InstanceEntities.RemoveBuff(PlayerId, state.PresentedCatOnslaughtBuff));
            state.PresentedCatOnslaughtBuff = 0;
            state.PresentedCatOnslaughtPhaseBuff = 0;
        }
        if (active && wanted != 0 && state.PresentedCatOnslaughtBuff != wanted)
        {
            float remaining = (float)Math.Max(0, state.CatPhaseDeadline - AreaTime(conn));
            // Onslaught row 215 is not a recovery row, so 0x1c1 carries no GrantedStats payload.
            packets.Add(InstanceEntities.AddBuff(PlayerId, wanted, onslaught!.BuffRow, PlayerId,
                u68: BitConverter.SingleToUInt32Bits(remaining)));
            state.PresentedCatOnslaughtBuff = wanted;
            state.PresentedCatOnslaughtPhaseBuff = state.PresentedCatPhaseBuff;
        }
    }

    internal static Task SendOwnAuraChanges(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        bool replay = false) => Send(remote, stream, ciphers, [], "own aura reconciliation", atGate: () =>
        {
            lock (conn.Monsters)
                return conn.Closed || conn.LeavingArea || conn.RageRetired
                    ? null : WithOwnAuraChanges(conn, () => [], replay);
        });
}
