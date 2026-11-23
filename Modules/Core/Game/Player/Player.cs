using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using DamageOverTime = GameServer.Core.World.DamageOverTime;

public static partial class Game
{
    /// <summary>Whether a stash tab goes out on area entry, so shop prices see the currency in it: a currency or grid tab
    /// (the only ones that hold shop currency) with currency (class StackableCurrency) in it.</summary>
    public static bool PreloadsForPrices(byte? tabType, IEnumerable<InventoryItem> inside, ItemCatalog catalog) =>
        StashRules.PreloadsForPrices(tabType, inside, catalog);

    // The characters in god mode, by name, for the Gateway's lifetime: every area is a new connection, so without this an
    // area change or a relog silently ended /god.
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> GodCharacters = new(StringComparer.OrdinalIgnoreCase);
    // The characters with /god immune, by name (as GodCharacters).
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ImmuneCharacters = new(StringComparer.OrdinalIgnoreCase);

    // A new connection of a character in god mode is in it again (LoadProgress, before its pools are worked out). It only
    // switches on: a caller may have set it already.
    public static void RestoreGod(Player player)
    {
        if (GodCharacters.ContainsKey(CharacterName(player))) player.God = true;
        if (ImmuneCharacters.ContainsKey(CharacterName(player))) player.Immune = player.God;
    }

    // The mana the running auras reserve, and what is left for casting and regeneration.
    // Restoring an already enabled aura is not a fresh cast: transient mana availability must not discard its saved intent.
    public static IReadOnlyList<AuraCatalog.Aura> RestoreRememberedAuras(Player player, IEnumerable<string> saved)
    {
        if (player.Closed || player.LeavingArea || player.RageRetired || InTown(player)) return [];
        string[] savedList = [.. saved];
        var wanted = savedList.Select(AuraCatalog.Family).ToHashSet(StringComparer.Ordinal);
        var wantedStanceFamilies = savedList.Where(StanceCatalog.IsSource)
            .Select(StanceCatalog.SourceFamily).ToHashSet(StringComparer.Ordinal);
        var savedStance = CharacterSaves.Remembered(CharacterName(player)).Stance;
        string restoredStance = player.Auras.Values.Any(a => StanceCatalog.IsStance(a.EffectId))
            ? player.Stance : StanceCatalog.RestoredSelection(savedStance, savedList);
        wanted.ExceptWith(player.Auras.Values.Select(a => AuraCatalog.Family(a.EffectId)));
        if (wanted.Count == 0) return [];
        var candidates = new List<AuraCatalog.Aura>();
        var sourceFamilies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gem in GemSkillEffects(player))
        {
            if (SkillsFor(player).EffectId(gem.Effect) is not { } effect) continue;
            bool stanceSource = StanceCatalog.IsSource(effect);
            string family = stanceSource ? StanceCatalog.SourceFamily(effect) : AuraCatalog.Family(effect);
            bool requested = stanceSource ? wantedStanceFamilies.Contains(family)
                : wanted.Contains(family) && AuraCatalog.Supported(effect);
            if (!requested) continue;
            if (AuraCatalog.IsAspect(effect) && player.Auras.Values.Any(a => AuraCatalog.IsAspect(a.EffectId))) continue;
            bool owned = stanceSource
                ? player.Auras.Values.Any(a => StanceCatalog.SourceFamily(a.EffectId) == family)
                : player.Auras.Values.Any(a => AuraCatalog.Family(a.EffectId) == family);
            if (owned || GemSkillInstanceDisabled(player, gem.Effect, gem.Id) is not null
                || OwnAuraSkillRestricted(player, gem.Effect, gem.Id)
                || SupportedReservationCost(player, gem.Id) is not { } reservation) continue;
            if (stanceSource && !sourceFamilies.Add(family)) continue;
            {
                string selected = StanceCatalog.IsSource(effect)
                    ? restoredStance : effect;
                var supported = SupportedSkill(player, gem.Id);
                var (supportEffect, affectsSelf) = AuraSupport(player, gem.Id);
                var context = ReservationContext(player, gem.Id);
                string? stance = StanceCatalog.IsSource(effect) || AuraCatalog.IsStance(effect) ? restoredStance : null;
                string effectId = StanceCatalog.IsSource(effect)
                    ? StanceCatalog.RuntimeEffectId(effect, restoredStance) : selected;
                bool hasSource = OwnAuraRestriction.IsAura(supported.Stats);
                var (artVariation, hideVisual, sourceVisual, buffVisual) =
                    ReservationCosmeticVisualsForGem(player, gem.Gem, effectId, hasSource);
                var candidate = new AuraCatalog.Aura(gem.Id, gem.Effect, effectId, gem.Name, player.NextBuff++,
                    AuraCatalog.BuffRow(effectId, stance),
                    reservation.ManaFlat, reservation.ManaPercent,
                    LifeFlat: reservation.LifeFlat, LifePercent: reservation.LifePercent,
                    Level: supported.Level, Quality: supported.Quality,
                    SourceBuff: hasSource ? player.NextBuff++ : null,
                    WornGrant: supported.WornGrant, SkillStats: [.. supported.Stats.Select(kv => (kv.Key, kv.Value))],
                    SupportEffectPct: supportEffect, AffectsSelf: affectsSelf, Stance: stance,
                    ReservesLife: context.Life, ReservationEfficiencyPct: context.Efficiency,
                    IsHerald: IsHeraldAura(effectId),
                    ArtVariation: artVariation, HideVisual: hideVisual, SourceVisual: sourceVisual, BuffVisual: buffVisual);
                candidates.Add(stanceSource ? candidate with { StanceOwner = new object() } : candidate);
            }
        }
        var savedAdmissions = savedList.Select(id => StanceCatalog.IsSource(id)
            ? StanceCatalog.RuntimeEffectId(id, restoredStance) : id).ToArray();
        var admitted = AuraCatalog.Restore(savedAdmissions, candidates, player.Pools, InTown(player));
        var restored = new List<AuraCatalog.Aura>();
        foreach (var aura in admitted.OrderByDescending(EternalBlessingAura))
        {
            if (EternalBlessingDisables(player, aura.SkillId)
                || IsBlessingAura(aura) && player.Auras.Values.Any(IsBlessingAura)) continue;
            player.Auras[aura.SkillId] = aura;
            if (!ReservationBudgetFits(player))
            {
                player.Auras.Remove(aura.SkillId);
                EndReservationRuntimeLocked(player, aura);
            }
            else
            {
                StartGuardianBlessingLocked(player, aura, SupportedSkill(player, aura.SkillId));
                if (EternalBlessingAura(aura)) SuspendManaReservedAurasLocked(player);
                restored.Add(aura);
            }
        }
        if (restored.Count > 0)
        {
            if (restored.Any(a => StanceCatalog.IsStance(a.EffectId)))
                ReconcileStanceSelectionLocked(player, persist: false);
            player.AuraStatsDirty = true;
            player.Pools = PoolsFor(player, player.Pools.Level);
            player.ApplyMovementSpeed();
            // Restoration is a bulk admission: suspend over-capacity auras while retaining their intent.
            RepriceOwnAuras(player, out bool poolsRebuilt);
            if (poolsRebuilt)
            {
                player.ApplyMovementSpeed();
            }
            restored = [.. restored.Where(aura => player.Auras.ContainsKey(aura.SkillId))];
            player.PlayerMana = Math.Min(player.PlayerMana, (uint)UnreservedMana(player));
            player.PlayerLife = LifeReservation.Clamp(player.PlayerLife, LifeReservationOf(player));
            Console.WriteLine($"[gateway] {CharacterName(player)}: restored saved auras: {string.Join(", ", restored.Select(a => a.EffectId))}");
        }
        return restored;
    }

    public static bool InGracePeriod(Player player)
    {
        double now = AreaTime(player);
        return player.EntryProtection.Protects(now, player.Walk.Position(now));
    }

    // The player object is in the new area: what starts on it there, the Grace Period and the passives' self buffs (each
    // area's connection starts without buffs, PassiveBuffAuras).
    public static async Task PlayerEnteredArea(Player player)
    {
        player.Connection.PlayerSent = true;
        if (player.Layout.Hash == StashCatalog.MenagerieHub && !player.Character.LeagueQuestFlags.Contains(Quests.League.VisitedMenagerie))
            await SetLeagueQuestFlags(player, [Quests.League.VisitedMenagerie]);
        await Send(player, [], "0x01c1 passive buffs (entering the area)", atGate: () => PassiveBuffAuras.Presentation(player));
    }

    public static async Task StartGracePeriod(Player player)
    {
        double now = AreaTime(player);
        bool hideout = HideoutCatalog.ByArea(player.Layout.Hash) is not null;
        if (player.EntryProtection.Start(now, player.Walk.Position(now), GracePeriod.BuffRow, InTown(player), hideout))
        {
            player.Area.Buffs.Set(player.Id, GracePeriod.Buff(player.Id));
            Note($"Grace Period: {GracePeriod.Seconds} seconds of entry protection");
        }
        await Task.CompletedTask;
    }

    /// <summary>Commits all unsaved in-memory progress (XP, inventory, vitals, golems, and cached character/stash saves)
    /// to the permanent SQLite database when leaving an area or exiting.</summary>
    public static void FlushAreaProgress(Player player)
    {
        string name = CharacterName(player);
        CharacterSaves.SaveExperience(name);
        ItemsOf(player).SaveUnsaved();
        PersistVitals(player, bestEffort: true);
        SaveGolems(player);
        Saves.FlushSession(name, player.Character.Account, player.Character.League);
    }

    public static async Task EndGracePeriod(Player player, bool action = false)
    {
        double now = AreaTime(player);
        if (player.EntryProtection.End(now, player.Walk.Position(now), action) is ushort)
            player.Area.Buffs.Clear(player.Id, GracePeriod.BuffRow, player.Id);
        await Task.CompletedTask;
    }

    public static bool InTown(Player player) => WaypointCatalog.Areas.Find(player.Layout.Hash) is { IsTown: true };

    public static async Task EndAurasOnDeath(Player player)
    {
        if (player.PlayerLife != 0 || player.AuraDeathHandled || ProjectileOutputGuard.Value is { } before && !before()) return;
        await EndGracePeriod(player, action: true);
        List<AuraCatalog.Aura> ended;
        bool petrifiedEnded;
        if (player.PlayerLife != 0 || player.AuraDeathHandled || ProjectileOutputGuard.Value is { } current && !current()) return;
        player.AuraDeathHandled = true;
        CharacterSaves.ClearActiveAuras(CharacterName(player));
        CharacterSaves.SetPetrifiedBlood(CharacterName(player), false);
        ended = [.. player.Auras.Values];
        petrifiedEnded = player.PetrifiedBlood.Active || player.Area.Buffs.Has(player.Id, PetrifiedBlood.BuffRow, player.Id);
        player.Auras.Clear();
        ReconcileStanceSelectionLocked(player, persist: true);
        player.PetrifiedBlood.Clear();
        player.LeechDisplay.Clear();
        player.Lifetap.Clear();
        player.AuraStatsDirty = ended.Count > 0 || petrifiedEnded || player.AuraStatsDirty;
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        await SendOwnAuraChanges(player);
        if (ended.Count > 0 || petrifiedEnded)
        {
            await SendCharacterStats(player, PlayerStatChange.Auras);
            await SendPlayerMana(player, "aura reservation cleared on death");
        }
    }

    public static Task PlayerDied(Player player, string label, byte deathKind = InstanceProtocol.DeathKindNormal) =>
        PlayerDiedAtEpoch(player, label, deathKind: deathKind);

    public static async Task PlayerDiedAtEpoch(Player player,
        string label, int? expectedEpoch = null, byte deathKind = InstanceProtocol.DeathKindNormal)
    {
        int deathEpoch;
        if (ProjectileOutputGuard.Value is { } guard && !guard()) return;
        if (player.Closed || player.LeavingArea || player.PlayerLife != 0
            || expectedEpoch is int expected && expected != player.IncomingHitEpoch
            || player.DeathNotificationEpoch == player.IncomingHitEpoch) return;
        RecordHardcoreDeath(player);
        player.InvalidateIncomingHits();
        player.DeathNotificationEpoch = deathEpoch = player.IncomingHitEpoch;
        FortificationOf(player).Clear();
        LeechOf(player).Clear(AreaTime(player));
        ClearRageOnDeath(player);
        player.Rampage.Clear();
        // Before RetireOwnedCombat drops the orb: its stage icon must be queued for removal with it, and the channel
        // that held it ends here rather than staying set for the respawned client to keep holding (the loop merely
        // returns on the zero life, so nothing else clears ChannelSkill).
        WinterOrbRetire(player);
        SnipeRetire(player);
        MirageWarriorsRetire(player);
        player.RetireOwnedCombat();
        player.ChannelSkill = 0;
        player.ChannelGeneration++;
        player.PaidChannel = null;
        player.ChannelMovement = default;
        ChargesOnDeath(player);
        ClearBloodChargesOnDeath(player);
        player.WitchbaneImmunityUntil = 0;
        ClearMomentum(player);
        VaalAuraStates.Remove(player); // CancelOnDeath: it cannot return after resurrection.
        ClearAntiquarianOnDeath(player);
        player.StrikeLocations.Clear();   // skill_location_memory is CancelOnDeath
        player.SpiritChieftains.End(AreaTime(player));   // the chieftain minion dies with the player
        player.Totems.KillAll(AreaTime(player));   // and his totems (the tick sends their deaths)
        // Every "Recently" ends at death, the enemies' "hit by you" and "hit you" recently too.
        foreach (var monster in player.Area.Monsters.Values) monster.PlayerHitAt = monster.HitPlayerAt = double.NegativeInfinity;
        foreach (var (id, life) in RecoverBossesOnDeath(player))
            Note($"boss 0x{id:x} recovers to {life} life after player death");
        if (ProjectileOutputGuard.Value is { } current && !current()) return;
        PersistVitals(player, bestEffort: true);
        await SendVaalSouls(player);
        await SendRage(player);
        await EndAurasOnDeath(player);
        await SendPlayerDeathPresentation(player, label, deathEpoch, deathKind);
    }

    public static Task SendPlayerDeathPresentation(Player player, string label, int deathEpoch, byte deathKind = InstanceProtocol.DeathKindNormal) => SendWorldBatch(player, [], admission =>
    {
        if (player.Closed || player.LeavingArea || player.PlayerLife != 0 || player.IncomingHitEpoch != deathEpoch) return null;
        var records = new WorldRecordBuilder(player, admission);
        Note(label);
        SetStats(player, player.Id, [(Stat.HideMiniLifeBar, 1)]);
        records.Immediate(InstanceProtocol.DeathScreen(DeathScreenFlags(player)));
        return records.Build();
    });

    public static int ReservedMana(Player player)
    {
        EnsureReservationInputs(player);
        long reserved = (long)AuraCatalog.RequiredMana(player.Pools, player.Auras.Values)
            + PetrifiedBlood.RequiredMana(player.Pools, player.PetrifiedBlood)
            + PlacedSkills.Reserved(player.Pools, player.Placed.All.Values, false);
        return (int)Math.Min((long)player.Pools.Mana, Math.Min(int.MaxValue, reserved));
    }

    public static int UnreservedMana(Player player) => Math.Max(0, (int)player.Pools.Mana - ReservedMana(player));

    // Pure preview for proposed or current aura sets. Never changes current Life or the advertised cap.
    public static LifeReservation.Value LifeReservationFor(Player player, IEnumerable<AuraCatalog.Aura> auras)
        => LifeReservation.Of(player.Pools, player.QualifiedItemLifeReservationPercent, auras,
            AdditionalLifeReservation(player));

    public static LifeReservation.Value LifeReservationOf(Player player)
    {
        if (player.StatSourcesCache is { } qualified)
            PublishQualifiedItemLifeReservationPercent(player, qualified.Key.QualifiedItemLifeReservationPercent);
        int itemPercent = player.QualifiedItemLifeReservationPercent;
        var value = LifeReservation.Of(player.Pools, itemPercent,
            player.Auras.Values, AdditionalLifeReservation(player));
        player.PlayerLifeCap = value.Unreserved;
        // Authoritative resource paths reconcile the usable cap under the combat lock.
        player.PlayerLife = LifeReservation.Clamp(player.PlayerLife, value);
        return value;
    }
    public static int ReservedLife(Player player) => LifeReservationOf(player).Reserved;
    public static uint LifeCap(Player player) => (uint)LifeReservationOf(player).Unreserved;

    // Every player Life pool update carries the shared owner's reserved amount.
    public static byte[] PlayerLifeUpdate(Player player, byte deathKind = InstanceProtocol.DeathKindNormal) =>
        InstanceProtocol.LifeUpdate(player.Id, player.PlayerLife, player.PlayerReservedLife,
            u2c: player.PlayerLife == 0 ? deathKind : InstanceProtocol.DeathKindNormal);

    // The player's pools go out through the Life component at the frame's end (AreaLife), with their reserved amounts.
    // An explicit send is for a client that changed its own pools ahead of the server (a cost it paid and was given back,
    // Eldritch Battery): they are written even unchanged. The aura records an aura change brings go with it.
    public static async Task SendPlayerMana(Player player,
        string? log = null)
    {
        player.Frames.Area?.Life.Resend(player);
        Note(log);
        if (!player.Closed && !player.LeavingArea && !player.RageRetired)
            await SendWorldRecords(player, player.Id, () => WithOwnAuraRecords(player, () => []), null);
        await SendPlayerEnergyShield(player);
    }

    // A paid skill's Life cost, then its mana update.
    /// <summary>Writes the player's pools at the frame's end even unchanged (the client predicted them); no records now.</summary>
    public static byte[][] PoolsResent(Player player) { player.Frames.Area?.Life.Resend(player); return []; }

    public static Task SendSkillCost(Player player, uint lifeCost, string? log) => SendPlayerMana(player, log);

    // The Lifetap buff's client instance: it is added by the mana publication after a supported skill's cost (the
    // same send gate as the other own buffs) and is removed here when its countdown runs out.
    public static Task SendLifetap(Player player) =>
        SendWorldRecords(player, player.Id, () =>
        {
            if (player.Closed || player.LeavingArea || player.RageRetired
                || (!player.Connection.PlayerObjectCreated && !player.Connection.PlayerSent)) return null;
            if (player.Lifetap.Until > AreaTime(player))
                PresentIn(player, player.Lifetap.Until - AreaTime(player));
            return Lifetap.PresentationRecords(player);
        });

    // The client's Life step reads life_recovery_per_minute and cannot know Petrified Blood's Low Life cap. Send 0
    // while the pool is at or above the boundary and the real modelled rate once it is below, so the local prediction
    // and the server agree. Pool updates themselves remain authoritative whole points.
    public static Task SendPetrifiedBloodRecoveryRate(Player player, bool blocked) => SendWorldRecord(player, player.Id, [], null, atGate: () =>
        {
            if (player.Closed || player.LeavingArea || player.RageRetired) return null;
            uint key = Stat.LifeRecoveryPerMinute;
            int value = blocked ? 0 : CharacterPlayerStats(player).GetValueOrDefault(key);
            SetStats(player, player.Id, [(key, value)]);
            return null;
        });

    // Energy Shield's recovery rate (the client's prediction between pool updates; the pool itself is the Life component).
    public static Task SendPlayerEnergyShield(Player player) =>
        SendWorldRecords(player, player.Id, () =>
        {
            if (player.Closed || player.LeavingArea || player.RageRetired) return null;
            var stats = CharacterPlayerStats(player);
            int Get(uint key) => stats.GetValueOrDefault(key);
            if (player.PlayerLife == 0) player.EnergyShield.Empty();
            // A client paying its costs from Energy Shield (Eldritch Battery) refills only with a recharge rate to predict.
            // The server's recharge runs whenever no damage came within the delay, full or not, so the rate goes out then
            // too (live 2026-11-13: the globe stayed below full after a cast with no rate).
            bool active = player.EnergyShield.Recharging || Get(Stat.KeystoneEldritchBattery) != 0
                && player.EnergyShield.RechargeReady(AreaTime(player), Get);
            int rate = player.PlayerLife == 0 || player.TrapDegenPerSecond > 0 || player.IncomingAilments.Bleeding(AreaTime(player)) ? 0 : (int)Math.Round(
                EnergyShield.PredictionPerMinute(player.Pools.EnergyShield, Get, active));
            SetStats(player, player.Id, [(Stat.EnergyShieldRecoveryPerMinute, rate)]);
            return null;
        }, "0x01b3 player energy shield recovery rate", sync: "energy-shield");

    // The base mana a gem skill costs at its level (its mana_cost stat), before supports: the client spends it at the
    // start of every use, including a use that becomes a walk (attack-move), so the cost the server gives back must be
    // known outside the supported gem (ManaCostTests pins Holy Strike's 6).
    public static uint ManaCost(StatCatalog stats, SkillCatalog skills, int effect, int level, int quality) =>
        Stat.ManaCost is var k ? (uint)skills.EffectStats(effect, level, quality).Where(kv => kv.Item1 == k).Select(kv => kv.Item2).FirstOrDefault() : 0;

    // What a use of the skill costs now (PayMana's rules), and whether the player can pay it. Called in the area's frame.
    // cost is the whole Mana cost; fromEs the part of it Eldritch Battery pays from Energy Shield ("Spend Energy Shield
    // before Mana for Skill Mana Costs"), after the skill's own Energy Shield cost (es).
    static bool SkillCostLocked(Player player, ushort skillId, IReadOnlyDictionary<uint, int> own, out uint cost, out int life,
        out uint es, out uint fromEs, out IReadOnlyDictionary<uint, int> playerStats, int costPercent = 100)
    {
        cost = 0;
        life = 0;
        es = 0;
        fromEs = 0;
        playerStats = new Dictionary<uint, int>();
        if (CombatLifetime is not null && !CurrentGameRules || player.Closed || player.LeavingArea || player.RageRetired || player.PlayerLife == 0)
            return false;
        playerStats = SupportedStats(player, skillId);
        var paying = playerStats;
        int Read(uint stat) => paying.GetValueOrDefault(stat);
        var skills = SkillsFor(player);
        int effect = SupportedSkill(player, skillId).Effect;
        bool channelled = skills.IsChannelled(effect);
        cost = (uint)Math.Max(0, Read(Stat.ManaCost));
        int Own(uint key) => own.GetValueOrDefault(key);
        int nativeLife = Math.Max(0, Read(Stat.LifeCost));
        int convertedLife = 0;
        int petrifiedLife = 0;
        // Blood Magic (the keystone, or an item that grants it): every skill's Mana cost becomes a Life cost.
        // Lifetap Support's conversion and Petrified Blood's share do not stack on top of it, since the Mana cost
        // it would divide no longer exists.
        if (Lifetap.ConvertsCostToLife(own, playerStats))
        {
            // Lifetap pays the whole cost as Life. Petrified Blood's share of a Mana cost that no longer exists is
            // therefore zero: the two conversions do not stack into 140%.
            convertedLife = (int)Math.Min(cost, int.MaxValue);
            cost = 0;
        }
        else
        {
            int lifeBasis;
            if (Read(Stat.MarkSkillsCostNoMana) != 0 && skills.HasType(effect, "Mark"))
            {
                cost = 0;
                lifeBasis = 0;
            }
            else if (Math.Clamp(Read(Stat.BaseAttackSkillCostLifeInsteadOfManaPct), 0, 100) is int split and > 0
                && skills.IsAttack(effect) && own.TryGetValue(Stat.ManaCost, out int baseMana))
            {
                (int remainingMana, lifeBasis, convertedLife) = ManaRecoveryRules.SplitCostToLife(
                    baseMana, Read, channelled, SkillCostMore(player, own), split);
                cost = (uint)remainingMana;
            }
            else
            {
                lifeBasis = (int)Math.Min(cost, int.MaxValue);
                if (player.PetrifiedBlood.Active && Read(ItemChargeCosts.ManaEfficiency) != 0
                    && own.TryGetValue(Stat.ManaCost, out int unsplitBase))
                    lifeBasis = ManaRecoveryRules.Cost(unsplitBase, Read, channelled, SkillCostMore(player, own),
                        applyManaEfficiency: false);
            }
            petrifiedLife = PetrifiedBlood.LifeCost(player, lifeBasis);
        }
        if (nativeLife > 0 && BloodCharges.Uses(Own))
            nativeLife = (int)Math.Min(int.MaxValue, Math.Round(nativeLife
                * BloodCharges.More(Own(BloodCharges.CostPerCharge), player.BloodCharges)));
        life = (int)Math.Min(int.MaxValue, (long)nativeLife + convertedLife + petrifiedLife);
        es = (uint)Math.Max(0, Read(Stat.EsCost));
        if (costPercent != 100)
        {
            cost = (uint)((long)cost * costPercent / 100);
            life = (int)((long)life * costPercent / 100);
            es = (uint)((long)es * costPercent / 100);
        }
        if (es > player.EnergyShield.Current) return false;
        bool Has(uint name) => Read(name) != 0;
        if (Has(Stat.VirtualSpendEnergyShieldForCostsBeforeMana) || Has(Stat.KeystoneEldritchBattery))
            fromEs = Math.Min(cost, player.EnergyShield.Current - es);
        return player.PlayerMana >= cost - fromEs && !(life > 0 && player.PlayerLife <= (uint)life);
    }

    // Whether the player can pay a use of the skill now: checked before a use's admission spends a cooldown use or a
    // bypass charge, or moves the player (TryBeginCooldownUseCore), so a refused payment leaves them as they were.
    public static bool CanPaySkillCost(Player player, ushort skillId)
    {
        var own = SupportedSkill(player, skillId).Stats;
        return SkillCostLocked(player, skillId, own, out _, out _, out _, out _, out _);
    }

    // A gem skill's mana cost at the gem's level (its mana_cost stat), paid when the cast starts, plus Petrified
    // Blood's base life cost while it runs and the player is not on Low Life. Lifetap Support replaces the whole
    // mana cost with Life. An Energy Shield cost (es_cost) is paid from Energy Shield, and Eldritch Battery pays the
    // Mana cost from what Energy Shield is left before Mana. False when the player lacks the pool it actually uses.
    // cost is what Mana paid, and only that is Mana spent (ManaSpentLocked: Ravenous Passion's Rage, recoup): the part
    // Energy Shield paid is not (a player's test, 2025: Indigon did not count it; no official statement found).
    public static bool PayMana(Player player, ushort skillId, out uint cost, out uint lifeCost) =>
        PayMana(player, skillId, out cost, out lifeCost, 100);

    internal static bool PayMana(Player player, ushort skillId, out uint cost, out uint lifeCost, int costPercent, bool useSpell = true)
    {
        var supported = SupportedSkill(player, skillId);
        var own = supported.Stats;
        lifeCost = 0;
        if (!SkillCostLocked(player, skillId, own, out uint manaCost, out int life, out uint es, out uint fromEs, out var playerStats, costPercent))
        {
            cost = 0;
            return false;
        }
        if (es + fromEs > 0)
        {
            player.EnergyShield.Spend(es + fromEs);
            // The client paid it from its own Energy Shield too: the pool goes out even when recharge has already brought
            // it back to the last value written (live 2026-11-13: the globe stayed at 5249/5324 while the server was full).
            player.Frames.Area?.Life.Resend(player);
        }
        cost = manaCost - fromEs;
        player.PlayerMana -= cost;
        if (life > 0)
        {
            player.PlayerLife -= (uint)life;
            lifeCost = (uint)life;
        }
        ManaSpentLocked(player, cost, playerStats);
        ApplyPaidSupportCostsLocked(player, skillId, own, cost, life);
        if (useSpell) lifeCost += SacrificeItemLifeLocked(player, supported.Effect, playerStats);
        if (lifeCost > 0) PresentStats(player);
        ConsumeCombatRush(player, skillId, supported, AreaTime(player));
        return true;
    }

    // Shared supported spell timing, before the new-action frame limit or an integer-ms wait is applied.
    static SpellCastTiming? SpellTimingFor(Player player, int effect, ushort skillId)
    {
        // From the skill's gem and the stats only: kept with the skills (ActiveSkills).
        if (BuildScopeOf(player) is not { Building: true } && CurrentActiveSkills(player) is { } current)
        {
            if (skillId != 0 && SupportedSkill(player, skillId) is { } supported && supported.Effect == effect)
                return current.SkillGraphOf(player, skillId, supported).Timing;
            if (!current.SpellTimings.TryGetValue(effect, out var known))
                current.SpellTimings[effect] = known = SpellTimingNow(player, effect, skillId);
            return known;
        }
        return SpellTimingNow(player, effect, skillId);
    }

    static SpellCastTiming? SpellTimingNow(Player player, int effect, ushort skillId)
    {
        var own = skillId == 0 ? new Dictionary<uint, int>() : SupportedSkill(player, skillId).Stats;
        var playerStats = CharacterPlayerStats(player);
        var merged = MergeSkillStats(playerStats, own);
        return SpellTiming(player, effect, playerStats.GetValueOrDefault, own.GetValueOrDefault, merged.GetValueOrDefault);
    }

    // A spell's cast time from the player's stats, the skill's own and the two merged (a skill graph's rule reads them).
    static SpellCastTiming? SpellTiming(Player player, int effect, Func<uint, int> playerStat, Func<uint, int> own, Func<uint, int> merged)
    {
        int speed = playerStat(Stat.CastSpeedPlusPct) + own(Stat.BaseCastSpeedPlusPct);
        speed += BrandCastSpeedBonusPct(player, effect, playerStat, own)
            + TaggedCastSpeedBonusPct(player, effect, playerStat, own);
        speed = FinalCastSpeedPct(merged, () => HoldingFishingRod(player), speed);
        return own(Stat.BaseSpellCastTimeMsOverride) is int basis and > 0
            ? SpellCastTiming.Calculate(basis, speed) : SpellCastTiming.ForSkill(SkillsFor(player), effect, speed);
    }

    static int TaggedCastSpeedBonusPct(Player player, int effect, Func<uint, int> playerStat, Func<uint, int> own)
    {
        int Total(uint stat) => ActorScale.Saturate((long)playerStat(stat) + own(stat));
        var skills = SkillsFor(player);
        return (skills.HasType(effect, "AppliesCurse") ? Total(Stat.CurseCastSpeedPlusPct) : 0)
            + (skills.HasType(effect, "Mark") ? Total(Stat.MarkSkillCastSpeedPlusPct) : 0)
            + (skills.HasType(effect, "Link") ? Total(Stat.LinkSkillCastSpeedPlusPct) : 0)
            + (Supports.Of(effect)?.Types.Contains(120) == true ? Total(Stat.RetaliationSkillSpeedPlusPct) : 0);
    }

    // A genuine spell's final server wait; native actions instead receive the unadjusted base milliseconds.
    // Null refuses an invalid/unrepresentable cast rather than turning it into an immediate hit.
    public static int? CastMs(Player player, int effect, ushort skillId = 0)
    {
        if (SpellTimingFor(player, effect, skillId)?.DurationMilliseconds is not int duration) return null;
        // A new action starts at most once per server frame (30/s); channelling repeats are exempt and use their own
        // interval. Keep the fractional tick and round the final wait up to whole milliseconds.
        double seconds = Math.Max(TickSeconds, duration / 1000.0 / PlayerActionFactor(player));
        return (int)Math.Min(int.MaxValue, Math.Ceiling(seconds * 1000));
    }

    // cast_speed_+% is the total (PanelStats: base_cast_speed_+% and attack_and_cast_speed_+% are already in it).
    public static int CastSpeedPct(IReadOnlyDictionary<uint, int> stats) =>
        Stat.CastSpeedPlusPct is var k ? stats.GetValueOrDefault(k) : 0;

    // "More" cast speed on the summed increases (the skill's own included), as the client's cast_speed_+% calculator
    // 0x1427101c0: round((increased + 100) × (final + 100) / 100 − 100), at least −99. Its combined final (0x14270ed40)
    // multiplies max(0, 1 + v / 100) of each source below in single precision and in this order, then rounds:
    // Spell Echo's support_multicast_cast_speed_+%_final, Screaming Reels' only for main_hand_weapon_type 15 (a fishing
    // rod), the totem supports' only off a totem, Fanaticism's only for a hand-cast spell, the Frostbolt one at most
    // +200%, the metamorphosis boss's in permyriad. A total of −100% or less keeps its existing refusal.
    static int FinalCastSpeedPct(Player player, IReadOnlyDictionary<uint, int> stats, int increased) =>
        FinalCastSpeedPct(name => name is var k ? stats.GetValueOrDefault(k) : 0, () => HoldingFishingRod(player), increased);

    public static int FinalCastSpeedPct(Func<uint, int> get, Func<bool> holdingFishingRod, int increased)
    {
        if (increased <= -100) return increased;
        static float Factor(int value, float scale = 100) => Math.Max(0, (value + scale) / scale);
        float Final(uint name) => Factor(get(name));
        bool totem = get(Stat.IsTotem) != 0, handCast = get(Stat.CastingSpell) != 0 && get(Stat.IsNotHandCast) == 0;
        int fishing = get(Stat.CastSpeedPlusPctFinalWhileHoldingFishingRod);
        float final = Final(Stat.ActiveSkillCastSpeedPlusPctFinal) * 100 * Final(Stat.RighteousFireCastSpeedPlusPctFinal)
            * Final(Stat.SuppressingFireDebuffCastSpeedPlusPctFinal) * Final(Stat.SupportMulticastCastSpeedPlusPctFinal)
            * Final(Stat.EchoingShrineCastSpeedPlusPctFinal) * Final(Stat.EssenceSupportAttackAndCastSpeedPlusPctFinal)
            * Final(Stat.WeaponTreeCastSpeedPlusPctFinalInMainHand) * Final(Stat.WeaponTreeCastSpeedPlusPctFinalInOffHand)
            * Final(Stat.CoveredInSpidersAttackAndCastSpeedPlusPctFinal)
            * (totem ? 1 : Final(Stat.SupportSpellTotemCastSpeedPlusPctFinal)) * (totem ? 1 : Final(Stat.SupportCrabTotemCastSpeedPlusPctFinal))
            * Factor(Math.Min(200, get(Stat.CastSpeedPlusPctFinalPerFrostboltDestroyedRecently) * get(Stat.NumberOfFrostboltsDestroyedRecently)))
            * (handCast ? Final(Stat.FanaticismHandCastSpeedPlusPctFinal) : 1)
            * (fishing != 0 && holdingFishingRod() ? Factor(fishing) : 1)
            * Factor(get(Stat.MetamorphosisBossCastSpeedPlusPctFinalPermyriad), 10000) * Final(Stat.CastSpeedPlusPctFinalForMinion) - 100;
        int more = (int)MathF.Round(final, MidpointRounding.AwayFromZero);
        if (more == 0) return increased;
        float total = (increased + 100) * Factor(more) - 100;
        return Math.Max(-99, (int)MathF.Round(total, MidpointRounding.AwayFromZero));
    }

    // Righteous Fire (GrantedEffects RighteousFire): casting it puts the righteous_fire buff (BuffDefinitions row 12: the
    // flames around the player, BuffVisuals 15) on the player with S→C 0x1c1. While it lasts the player burns for 90% of
    // maximum life per second, nonlethal (base_nonlethal_fire_damage_%_of_maximum_life_taken_per_minute 5400), and enemies
    // within its radius (active_skill_base_area_of_effect_radius 18 cells at level 1, plus their size) take 70% of the
    // player's maximum life per second as fire damage (base_righteous_fire_%_of_max_life_to_deal_to_nearby_per_minute 4200)
    // and carry the righteous_fire_aura buff (row 13). It ends at death or on leaving the area; casting it again does nothing.
    public static bool IsRighteousFire(int effect) => Skills.EffectId(effect) == "RighteousFire";

    sealed record RighteousFireDetachRecord(uint Id, byte[] Packet, bool RemovesObject = false);
    sealed record RighteousFireDetachPlan(RighteousFireState State, IReadOnlyList<RighteousFireDetachRecord> Packets);

    // The committed owner identity and physical source lease, without an outgoing-work or supported-skill query.
    private static bool RighteousFireOwnerCurrentLocked(Player player, RighteousFireState rf) =>
        ReferenceEquals(player.RighteousFire, rf) && CurrentGameRules && !player.Closed && !player.LeavingArea
        && ReferenceEquals(player.Instance, rf.Instance) && player.PlayerLife != 0
        && rf.SourceLifetime is { } lifetime && player.GemSourceLifetimes.IsCurrent(lifetime);

    // A raw player-stat projection, not another RF owner: read only the committed neutral scalar and owner guards.
    // Calling SupportedSkill/CharacterPlayerStats here would recursively rebuild the sources it is projecting.
    private static Dictionary<uint, int> WithRighteousFireSpellBonus(Player player, Dictionary<uint, int> sources)
    {
        uint key = Stat.RighteousFireSpellDamagePlusPctFinal;
        int more;
        if (player.RighteousFire is not { } rf || !RighteousFireOwnerCurrentLocked(player, rf))
            return sources;
        more = rf.SpellDamageMore;
        var projected = new Dictionary<uint, int>(sources);
        projected[key] = ActorScale.Saturate((long)projected.GetValueOrDefault(key) + more);
        return projected;
    }

    // Resolve the supported skill once for the physical source that owns the active burn.
    private static SupportedGem? CurrentRighteousFireSkillLocked(Player player, RighteousFireState rf)
    {
        if (!RighteousFireOwnerCurrentLocked(player, rf) || rf.SourceLifetime is not { } lifetime) return null;
        using var scope = new PlayerStatScope(player, fresh: true);
        var supported = SupportedSkill(player, rf.Skill);
        var source = CurrentPlayerSkillSourceLocked(player, rf.Skill);
        return supported.Effect >= 0 && supported.RequirementsMet
            && SkillsFor(player).EffectId(supported.Effect) == "RighteousFire"
            && source is { Active: true } && source.Effect == supported.Effect
            && source.Identity == lifetime.Key ? supported : null;
    }

    // The canonical row named by the still-current owner and lease, for profile refreshes that are not output work.
    private static bool RighteousFireSourceCurrentLocked(Player player, RighteousFireState rf) =>
        CurrentRighteousFireSkillLocked(player, rf) is not null;

    // Output and consumer work additionally retain the accepted outgoing-work guard.
    private static bool RighteousFireCurrentLocked(Player player, RighteousFireState rf) =>
        CurrentGameWork && CurrentRighteousFireSkillLocked(player, rf) is not null;

    private static bool RighteousFireRingCurrentLocked(Player player, PlayerMinions.RighteousFireRingReceipt receipt) =>
        receipt.State.Ring == receipt.Ring && receipt.State.RingArt is not null
        && RighteousFireCurrentLocked(player, receipt.State);

    // Activation and retained-profile refreshes use one radius formula: the current supported skill stats over the
    // current area player stats, as the skill is actually burning now.
    private static int RighteousFireRadius(Player player, IReadOnlyDictionary<uint, int> supported)
    {
        var stats = MergeSkillStats(AreaPlayerStats(player), supported);
        int Get(uint name, int fallback) =>
            name is var key && stats.TryGetValue(key, out int value) ? value : fallback;
        return SkillArea.Radius(Get(Stat.ActiveSkillBaseAreaOfEffectRadius, 18),
            name => Get(name, 0), spell: true);
    }

    private static float RighteousFireRingScale(int radius, string art) =>
        (float)(radius * (250.0 / 23) / (Art.ArtRadius(art) ?? 400));

    private static byte[] RighteousFireRingCreate(uint owner, RighteousFireState rf, uint ring, (int X, int Y) at)
    {
        string art = rf.RingArt!;
        // Created awake: the create wakes it in the same frame, with nothing for a frame after it to wait for.
        var ringObject = new EffectObject(at.X, at.Y, art)
        {
            Awake = true,
            Parent = owner,
            Scale = RighteousFireRingScale(rf.Radius, art),
        };
        return InstanceEntities.CreateObject(ring, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), ringObject.Blob());
    }

    private static void QueueRighteousFireRingLocked(Player player, RighteousFireState rf, uint ring, (int X, int Y) at)
    {
        string art = rf.RingArt!;
        byte[] create = RighteousFireRingCreate(player.Id, rf, ring, at);
        var receipt = new PlayerMinions.RighteousFireRingReceipt(rf, ring);
        player.Minions.OutputQueue.Add(new(create,
            $"0x02b9 create effect 0x{ring:x} {art} at ({at.X}, {at.Y}), scale {RighteousFireRingScale(rf.Radius, art):0.###} " +
            $"for radius {rf.Radius}",
            Kind: PlayerMinions.OutputKind.RighteousFireRing, RighteousFireRing: receipt, WorldId: ring, CreatesObject: true));
    }

    // Re-reads the retained RF owner's current effect rows after a gem/passive/equipment profile mutation. The owner,
    // lease, buff ids, burning enemies, pools and owed life loss stay untouched; an invalid source uses the existing
    // detach plan. A radius change replaces only the ring and queues neutral exact-receipt presentation.
    private static void RefreshRighteousFireProfileLocked(Player player)
    {
        if (player.RighteousFire is not { } rf) return;
        if (CurrentRighteousFireSkillLocked(player, rf) is not { } supported)
        {
            if (PlanRighteousFireDetachLocked(player, rf) is { } stale)
                QueueRighteousFireCleanupLocked(player, stale, "invalid Righteous Fire source at profile recompute");
            return;
        }
        using (new PlayerStatScope(player, fresh: true))
        {
            int more = Stat.RighteousFireSpellDamagePlusPctFinal is var key
                ? supported.Stats.GetValueOrDefault(key) : 0;
            int radius = RighteousFireRadius(player, supported.Stats);
            SetRighteousFireSupportProfileLocked(player, rf, supported.Stats);
            bool changed = rf.SpellDamageMore != more || rf.Radius != radius;
            if (!changed) return;
            rf.SpellDamageMore = more;
            if (rf.Radius != radius)
            {
                if (rf.Ring is uint old)
                    player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(old),
                        $"0x02bb remove old Righteous Fire ring 0x{old:x} after radius {rf.Radius} -> {radius}", WorldId: old, RemovesObject: true));
                rf.Radius = radius;
                if (rf.RingArt is not null)
                {
                    uint ring = player.TakeObjectId();
                    rf.Ring = ring;
                    var at = player.Walk.Position(AreaTime(player));
                    QueueRighteousFireRingLocked(player, rf, ring, at);
                }
                else
                {
                    rf.Ring = null;
                }
            }
            player.Minions.StatRevision++;
        }
    }

    // Detach is synchronous and returns immutable old-id packets. The optional expected reference refuses an older
    // snapshot without consulting mutable live RF fields after the boundary.
    private static RighteousFireDetachPlan? PlanRighteousFireDetachLocked(Player player, RighteousFireState? expected)
    {
        if (player.RighteousFire is not { } rf || expected is not null && !ReferenceEquals(rf, expected)) return null;
        player.RighteousFire = null;
        var buffs = player.Area.Buffs;
        buffs.Clear(player.Id, RighteousFireBuffRow, player.Id);
        buffs.Clear(player.Id, RighteousFireAuraBuffRow, player.Id);
        foreach (uint monster in rf.Auras.Keys) buffs.Clear(monster, RighteousFireAuraBuffRow, player.Id);
        rf.Auras.Clear();
        var packets = new List<RighteousFireDetachRecord>();
        if (rf.Ring is uint ring) packets.Add(new(ring, InstanceProtocol.RemoveObject(ring), RemovesObject: true));
        player.Minions.StatRevision++;
        return new(rf, packets);
    }

    private static void QueueRighteousFireCleanupLocked(Player player, RighteousFireDetachPlan plan, string log)
    {
        foreach (var record in plan.Packets)
            player.Minions.OutputQueue.Add(new(record.Packet, log, WorldId: record.Id, RemovesObject: record.RemovesObject));
    }

    // The skill_art_variation of the skill effects worn in a gem type's Cosmetics tab slots (0: none), which picks the
    // skill's art (SkillArt).
    public static int SkillArtVariation(Player player, uint gem)
    {
        // The gem's skill effect cosmetic: kept with the skills (ActiveSkills), not read from the save per use.
        if (CurrentActiveSkills(player) is { } current)
        {
            if (!current.ArtVariations.TryGetValue(gem, out int known)) current.ArtVariations[gem] = known = SkillCosmeticStat(player, gem, Stat.SkillArtVariation);
            return known;
        }
        return SkillCosmeticStat(player, gem, Stat.SkillArtVariation);
    }

    // Another stat of those skill effects (ground_caustic_art_variation: the caustic ground a skin leaves), 0 without one.
    public static int SkillCosmeticStat(Player player, uint gem, uint key) =>
        CharacterSaves.SkillCosmetics(CharacterName(player)).Where(kv => CharacterSaves.SkillCosmeticGem(kv.Key) == gem)
            .SelectMany(kv => Mtx.Resolve(kv.Value) is { } r && MtxEffects.Of(r.MtxTypeHash) is { } fx ? fx.Stats : [])
            .Where(st => st.Stat == key).Select(st => st.Value).FirstOrDefault();

    private static bool SamePlayerSkillSource(PlayerSkillSource source, PlayerSkillSource expected) =>
        source.Id == expected.Id && source.Effect == expected.Effect
        && source.Identity == expected.Identity;

    public static async Task ActivateRighteousFire(Player player, ushort skillId,
        int effect, int level, int quality, uint gem, RighteousFireState? expectedRelight = null,
        PlayerSkillSource? expectedRelightSource = null)
    {
        RighteousFireState? state = null;
        object? area = null;
        int variation = 0;
        ushort? buffVisual = null, auraVisual = null;
        string? ringAo = null;
        string? ringArt = null;
        int initialRadius = 0;
        uint? ringId = null;
        byte[]? ringCreate = null;
        PlayerMinions.RighteousFireRingReceipt? ringReceipt = null;
        (int X, int Y) at = default;
        // The skill's supported stats and the player's are worked out before the mutation, which holds the instance's
        // Monsters lock that the area's frame waits on: under it only the checks and the state change run.
        using var statScope = new PlayerStatScope(player, fresh: true);
        var supported = SupportedSkill(player, skillId);
        var stats = AreaSkillStats(player, skillId, supported);
        int Get(uint name, int fallback) => name is var k && stats.TryGetValue(k, out int v) ? v : fallback;
        int radius = SkillArea.Radius(Get(Stat.ActiveSkillBaseAreaOfEffectRadius, 18), name => Get(name, 0), spell: true);
        using (ProjectileMutation(player, advance: false))
        {
            {
                if (expectedRelight is not null
                    && (expectedRelightSource is null
                        || !ReferenceEquals(player.Instance, expectedRelight.Instance)
                        || expectedRelight.SourceLifetime is not { } expectedLifetime
                        || !player.GemSourceLifetimes.IsCurrent(expectedLifetime)
                        || expectedRelightSource.Identity != expectedLifetime.Key
                        || expectedRelight.Skill != expectedRelightSource.Id))
                    return;
                if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0
                    || player.RighteousFire is not null)
                    return;
                var source = CurrentPlayerSkillSourceLocked(player, skillId);
                if (!supported.RequirementsMet || supported.Effect < 0
                    || SkillsFor(player).EffectId(supported.Effect) != "RighteousFire"
                    || source is not { Active: true } || source.Effect != supported.Effect)
                    return;
                var lifetime = CaptureGemSourceLifetimeLocked(player, source);
                if (!player.GemSourceLifetimes.IsCurrent(lifetime) || source.Identity != lifetime.Key) return;
                if (expectedRelightSource is { } expectedSource
                    && (expectedRelight is null || !SamePlayerSkillSource(source, expectedSource)
                        || !ReferenceEquals(lifetime, expectedRelight.SourceLifetime)))
                    return;

                // A worn skill effect's art and the skill's current supported stats are authoritative here, not the
                // caller's snapshot from before the action reached this mutation.
                variation = SkillArtVariation(player, source.Gem);
                buffVisual = Art.BuffVisual("righteous_fire_buff_visual", variation);
                auraVisual = Art.BuffVisual("righteous_fire_aura_visual", variation);
                ringAo = Art.Animated("righteous_fire_ring", variation)
                    ?? (variation == 0 ? RighteousFireRingAo : null);
                ushort buff = player.NextBuff++, aura = player.NextBuff++;
                uint? ring = null;
                if (ringAo is not null) ring = player.TakeObjectId();
                at = player.Walk.Position(AreaTime(player));
                area = player.Instance;
                state = new RighteousFireState(buff, aura, skillId,
                    DamagePctPerMinute: Get(Stat.BaseRighteousFirePctOfMaxLifeToDealToNearbyPerMinute, 4200),
                    BurnPctPerMinute: Get(Stat.BaseNonlethalFireDamagePctOfMaximumLifeTakenPerMinute, 5400))
                {
                    Ring = ring,
                    Radius = radius,
                    RingArt = ringAo,
                    SpellDamageMore = Stat.RighteousFireSpellDamagePlusPctFinal is var key
                        ? supported.Stats.GetValueOrDefault(key) : 0,
                    AuraVisual = auraVisual,
                    EnergyShieldBurnPctPerMinute = Get(Stat.BaseNonlethalFireDamagePctOfMaximumEnergyShieldTakenPerMinute, 4200),
                    EnergyShieldDamagePctPerMinute = Get(Stat.BaseRighteousFirePctOfMaxEnergyShieldToDealToNearbyPerMinute, 4200),
                    SourceLifetime = lifetime,
                    Instance = player.Instance,
                };
                player.RighteousFire = state;
                SetRighteousFireSupportProfileLocked(player, state, supported.Stats);
                player.Minions.StatRevision++;
                // The initial create/wake/receipt are immutable originals captured in the same admission mutation. A
                // profile writer after this boundary can replace the retained ring but cannot change what this
                // activation will try to publish.
                ringArt = state.RingArt;
                initialRadius = state.Radius;
                ringId = state.Ring;
                if (ringId is uint admittedRing)
                {
                    ringCreate = RighteousFireRingCreate(player.Id, state, admittedRing, at);

                    ringReceipt = new(state, admittedRing);
                }
            }
        }
        if (state is null) return;
        StartRighteousFireBurn(player, state);
        var current = state;
        string sourceLabel = current.SourceLifetime!.Key.ToString() ?? "exact";
        bool Current()
        {
            return ReferenceEquals(player.Instance, area) && RighteousFireCurrentLocked(player, current);
        }
        bool RingCurrent()
        {
            return ReferenceEquals(player.Instance, area) && RighteousFireRingCurrentLocked(player, ringReceipt!);
        }
        if (Current())
        {
            player.Area.Buffs.Set(player.Id, new(RighteousFireBuffRow, player.Id, current.Skill, Visual: buffVisual));
            // The caster burns in its own fire: righteous_fire_aura too, whose BuffVisuals (19) give a player model the
            // flaming hands (EPKFiles2 ri_fire/player.epk) and the ignite flames; the first buff only draws the ring at its feet.
            player.Area.Buffs.Set(player.Id, new(RighteousFireAuraBuffRow, player.Id, current.Skill, Visual: auraVisual));
            Note($"righteous_fire (BuffDefinitions row {RighteousFireBuffRow}) from skill 0x{current.Skill:x4}, " +
                $"art variation {variation} (BuffVisuals {buffVisual?.ToString() ?? "the row's"} / {auraVisual?.ToString() ?? "the row's"}, ring {ringAo ?? "none"}), " +
                $"exact source {sourceLabel}, radius {initialRadius}, {current.DamagePctPerMinute / 60.0:0.#}% of life per second to enemies, " +
                $"{current.BurnPctPerMinute / 60.0:0.#}% to the player");
        }
        // The big burning circle is not a buff visual: it is MiscAnimated RighteousFireRing, an effect object of its own
        // (EffectObject), attached to the player: the client carries it along itself, so it stays on the player whatever
        // the latency (server glides arrived a round trip behind the client's own walk).
        if (ringCreate is null) return;
        // Scale: the art is drawn for a radius of MiscAnimated __col4 world units (RighteousFireRing 400, about 37 cells of
        // 250/23 units, where the skill's base radius is 18), so it is scaled to the radius the fire burns in.
        await SendWorldCreate(player, ringId!.Value, () => RingCurrent() ? ringCreate : null,
            $"0x02b9 create effect 0x{ringId:x} {ringArt} at ({at.X}, {at.Y}), " +
            $"scale {RighteousFireRingScale(initialRadius, ringArt!):0.###} for radius {initialRadius}",
            still: RingCurrent, awaitBirth: false);
    }

    public static async Task<bool> EndRighteousFire(Player player, string why, RighteousFireState? expected = null, PlayerSkillSource? expectedSource = null,
        InstanceState? expectedInstance = null)
    {
        RighteousFireDetachPlan? plan;
        {
            if (ProjectileOutputGuard.Value is { } guard && !guard()) return false;
            if (expectedSource is { } source)
            {
                using var scope = new PlayerStatScope(player, fresh: true);
                if (!CurrentGameWork || expected is null || expectedInstance is null
                    || !ReferenceEquals(player.Instance, expectedInstance)
                    || !RighteousFireOwnerCurrentLocked(player, expected)
                    || CurrentPlayerSkillSourceLocked(player, source.Id) is not { } current
                    || !SamePlayerSkillSource(current, source))
                    return false;
            }
            plan = PlanRighteousFireDetachLocked(player, expected);
        }
        if (plan is null) return false;
        await PublishRighteousFireDetach(player, plan, why);
        return true;
    }

    private static async Task PublishRighteousFireDetach(Player player, RighteousFireDetachPlan plan, string why)
    {
        for (int i = 0; i < plan.Packets.Count; i++)
        {
            var record = plan.Packets[i];
            string? log = i == 0 ? $"0x01c2 player buff {plan.State.Buff}: righteous_fire ends ({why})" : null;
            byte[]? Project()
            {
                return player.Closed || player.LeavingArea || player.RageRetired ? null : record.Packet;
            }
            if (record.RemovesObject)
                await SendWorldRemoval(player, record.Id, log, atGate: Project);
            else await SendWorldRecord(player, record.Id, record.Packet, log, atGate: Project);
        }
    }

    // The content's work for this area (GameEvents.AreaTick: boss fights, Delve, Delirium), every frame while the player's
    // updates run; no timer when no content has work here. With Game_PRESENT_VERIFY=1 the presentations' check (a debug
    // tool) runs every PlayerTickMs.
    static void StartContentTick(Player player, PlayerUpdatesState state)
    {
        var work = GameEvents.AreaTickWork(player);
        if (work.Count > 0)
            EveryFrameAsPlayer(player, () => PlayerUpdatesCurrent(player, state),
                player => GameEvents.RunAreaTick(work, player, FrameSeconds));
        EveryAsPlayer(player, PlayerTickMs, () => PlayerUpdatesCurrent(player, state), async player =>
        {
            // Elemental Overload's diamond: the client does not expire the invisible indicator buff itself, so remove it
            // when the eight-second window ends (refreshed on each critical strike of the bound skill).
            if (player.ElementalOverloadBuff != 0 && AreaTime(player) >= player.ElementalOverloadBuffUntil)
            {
                ushort overloadBuff = player.ElementalOverloadBuff;
                player.ElementalOverloadBuff = 0;
                await SendWorldRecord(player, player.Id,
                    InstanceEntities.RemoveBuff(player.Id, overloadBuff), "0x01c2 elemental overload window ended");
                if (!PlayerUpdatesCurrent(player, state)) return;
            }
            if (!player.RuleAilments.Idle)
                foreach (var (packet, log) in AlternateAilmentBuffs(player.Id, "player", player.RuleAilments, AreaTime(player)))
                    await Send(player, packet, log);
            if (!PlayerUpdatesCurrent(player, state)) return;
            await SendTemporaryAuraExpiry(player);
            if (!PlayerUpdatesCurrent(player, state)) return;
            if (player.BladefallTrarthus is { PaysLife: true, OutOfLife: true })
                await EndBladefallTrarthus(player, "out of life");
        });
        if (VerifyPresentations)
            EveryAsPlayer(player, PlayerTickMs, () => PlayerUpdatesCurrent(player, state), player =>
                SendPlayerTickBatch(player, async () =>
                {
                    await VerifyStatPresentations(player);
                    return PlayerUpdatesCurrent(player, state);
                }));
    }

    // The recovery's rates are read from the stats again (a stats update marks them).
    sealed class RecoveryRateState { public volatile bool Stale = true; }
    static readonly RuleTable<PlayerUpdatesState, RecoveryRateState> RecoveryRates = new();

    static void RecoveryRatesChanged(Player player)
    {
        if (player.PlayerUpdatesState is PlayerUpdatesState updates) RecoveryRates.GetOrCreateValue(updates).Stale = true;
    }

    // Time inside [4,5), [8,9), ... since the regeneration modifier became active.
    internal static double PeriodicRegenSeconds(double elapsed)
    {
        if (elapsed <= 4) return 0;
        double sinceFirst = elapsed - 4;
        return Math.Floor(sinceFirst / 4) + Math.Min(1, sinceFirst % 4);
    }

    // One writer for continuous recovery: regeneration, flask recovery and the player's RF burn.
    // The game clock excludes pause; scheduling/network delays change the step size, never the per-second rate.
    static void StartResourceRecovery(Player player, PlayerUpdatesState updates)
    {
        double lifeCarry = 0, manaCarry = 0, burnCarry = 0, trapCarry = 0, flaskCarry = 0, last = updates.Schedule.Now(player);
        double lifeRate = 0, manaRate = 0, burnPerSecond = 0, preventedPerSecond = 0;
        bool zealotsOath = false, convertLifeRegen = false, vaalPact = false;
        double? periodicRegenStart = null;
        double periodicRegenPerSecond = 0;
        Dictionary<uint, int> recoveryStats = [];
        int RecoveryStat(uint key) => recoveryStats.GetValueOrDefault(key);
        var cachedRf = player.RighteousFire;
        var rates = RecoveryRates.GetOrCreateValue(updates);
        EveryFrameAsPlayer(player, () => PlayerUpdatesCurrent(player, updates), async player =>
        {
            AdvanceUtilityFlasks(player, updates);
            await PublishFlaskChanges(player);
            if (!PlayerUpdatesCurrent(player, updates)) return;
            await EndGracePeriod(player);
            if (!PlayerUpdatesCurrent(player, updates)) return;
            RighteousFireState? rf = null;
            uint lifeBefore, lifeAfter, manaBefore, manaAfter, manaCap;
            bool dead, instantLifeChanged, instantManaChanged, trapDeath = false;
            List<Flasks.Effect> ended;
            double now;
            int? ailmentDeathEpoch = null;
            bool pbDeath = false, pbRateChanged = false, pbRateBlocked = false;
            List<uint>? deactivatedTinctures = null;
            using (ProjectileMutation(player))
            {
                // Commit earlier projectile hits before advancing leech/recovery to this resource boundary.
                now = updates.Schedule.ResourceNow(player);
                if (!PlayerUpdatesCurrent(player, updates)) return;
                rf = player.RighteousFire;
                if (rf is not null && CurrentRighteousFireSkillLocked(player, rf) is null)
                {
                    if (PlanRighteousFireDetachLocked(player, rf) is { } stale)
                        QueueRighteousFireCleanupLocked(player, stale, "Righteous Fire source became invalid at resource commit");
                    rf = null;
                }
                if (player.LeavingArea)
                {
                    // A failed transfer can resume this connection. Do not catch up old cached rates through loading.
                    // These sessions accrue independently of 'last'; settle and discard their loading-time recovery.
                    DiscardLoadingRecovery(player, now);
                    periodicRegenStart = null;
                    last = now;
                    rates.Stale = true;
                    return;
                }
                double seconds = now - last;
                if (seconds <= 0) return;
                // The rates are read again when the stats changed (PlayerStatsUpdated: a moving or life condition too) or
                // Righteous Fire started or ended, not every step.
                if (rates.Stale || rf != cachedRf)
                {
                    rates.Stale = false;
                    recoveryStats = CharacterPlayerStatsCore(player, out lifeRate);
                    manaRate = PanelStats.ManaPerMinute(
                        PanelStats.WhileMoving(RecoveryStat, player.Walk.Moving(AreaTime(player))), player.Pools.Mana) / 60;
                    player.PetrifiedBlood.RefreshThreshold(LowLifeThresholdPct(recoveryStats));
                    preventedPerSecond = PreventedDamageRecovery.PerSecond(
                        PanelStats.WhileMoving(RecoveryStat, player.Walk.Moving(AreaTime(player))));
                    int periodic = RecoveryStat(Stat.VirtualGainLifeRegenerationPerMinutePctFor1SecondEvery4Seconds);
                    periodicRegenStart = periodic > 0 && player.PlayerLife > 0 ? periodicRegenStart ?? now : null;
                    periodicRegenPerSecond = player.PlayerMaxLife * (double)periodic / 6000 * PanelStats.LifeRegenerationMultiplier(RecoveryStat);
                    zealotsOath = PanelStats.ZealotsOath(RecoveryStat);
                    convertLifeRegen = zealotsOath && EnergyShield.ConversionAllowed(RecoveryStat);
                    vaalPact = RecoveryStat(Stat.KeystoneVaalPact) != 0;
                    burnPerSecond = rf is null ? 0 : DamageModel.Mitigate((player.PlayerMaxLife * (double)rf.BurnPctPerMinute + player.Pools.EnergyShield * (double)rf.EnergyShieldBurnPctPerMinute) / 6000.0,
                        Resistance(player, DamageType.Fire), MaxResistance(player, DamageType.Fire));
                    cachedRf = rf;
                }
                double recoveryFrom = last;
                double life, mana;
                (life, mana, ended) = AdvanceFlaskRecovery(player, last, now);
                last = now;
                lifeBefore = player.PlayerLife;
                manaBefore = player.PlayerMana;
                manaCap = (uint)UnreservedMana(player);
                var leech = LeechOf(player);
                instantLifeChanged = leech.InstantLifeChanged;
                instantManaChanged = leech.InstantManaChanged;
                leech.InstantLifeChanged = leech.InstantManaChanged = false;
                dead = lifeBefore == 0;
                if (dead)
                {
                    AdvanceTincturesLocked(player, now, seconds, recoveryStats, out _, out _, ref deactivatedTinctures);
                    ClearPlayerDecay(player);
                    ClearCorruptedBlood(player);
                    ClearEssenceAuraExposure(player);
                    lifeCarry = manaCarry = burnCarry = trapCarry = flaskCarry = 0;
                    player.NonChaosDotBypassCarry = player.NonChaosDotTakenCarry =
                        player.ChaosDotBypassCarry = 0;
                    FortificationOf(player).Clear();
                    leech.Clear(now);
                    player.LifeRecoup.Clear();
                    PreventedDamageOf(player).Clear();
                    player.EnergyShield.Empty();
                    ClearAntiquarianOnDeath(player);
                    periodicRegenStart = null;
                    lifeAfter = lifeBefore;
                    manaAfter = manaBefore;
                }
                else
                {
                    uint lifeCap = LifeCap(player);
                    leech.ClearFull(now, lifeBefore >= lifeCap, manaBefore >= manaCap,
                        player.EnergyShield.Current >= player.EnergyShield.Maximum);
                    var leeched = leech.TakeRecovery(now);
                    int IncomingStat(uint name) => RecoveryStat(name);
                    double mapRecovery = PanelStats.MapRecoveryMultiplier(RecoveryStat);
                    bool noRegeneration = RecoveryStat(Stat.MapPlayersNoRegenerationIncludingEs) != 0;
                    life *= mapRecovery;
                    double takenFactor = IncomingStat(Stat.BaseCannotBeDamaged) > 0 ? 0
                        : IncomingResourceDefences.FinalTakenFactor(player.Pools, IncomingStat);
                    double trapTakenFactor = takenFactor
                        * Math.Max(0, 1 + IncomingStat(IncomingResourceDefences.UnhingedSaneDamageFinal) / 100.0);
                    burnCarry += (InGracePeriod(player) ? 0 : burnPerSecond * player.ElementalAilments.DamageTakenFactor(now))
                        * seconds * takenFactor;
                    int burnWhole = (int)Math.Truncate(burnCarry); burnCarry -= burnWhole;
                    int lifeBurn = IncomingResourceDefences.DamageOverTime(player.EnergyShield, burnWhole,
                        IncomingStat(IncomingResourceDefences.BypassStat), now, ref player.NonChaosDotBypassCarry);
                    // Physical trap degeneration shares received Focus and non-chaos DoT bypass with the other sources.
                    trapCarry += player.TrapDegenPerSecond * seconds * trapTakenFactor;
                    int trapWhole = (int)Math.Truncate(trapCarry); trapCarry -= trapWhole;
                    int lifeTrap = IncomingResourceDefences.DamageOverTime(player.EnergyShield, trapWhole,
                        IncomingStat(IncomingResourceDefences.BypassStat), now, ref player.NonChaosDotBypassCarry);
                    player.EnergyShield.Advance(recoveryFrom, now, RecoveryStat, true);
                    double periodicLife = periodicRegenStart is double start ? periodicRegenPerSecond
                        * (PeriodicRegenSeconds(now - start) - PeriodicRegenSeconds(recoveryFrom - start)) : 0;
                    player.EnergyShield.Gain(leeched.EnergyShield + (convertLifeRegen ? lifeRate * seconds + periodicLife : 0));
                    double fortificationLife = FortificationOf(player).TakeRecovery(AreaTime(player));
                    if (convertLifeRegen) player.EnergyShield.Gain(fortificationLife);
                    double antiquarianLife = player.Antiquarian.TakeRecovery(now) * (noRegeneration ? 0 : mapRecovery);
                    if (convertLifeRegen) player.EnergyShield.Gain(antiquarianLife);
                    double enduringCryLife = player.EnduringCryRecovery.Take(now) * (noRegeneration ? 0 : mapRecovery);
                    double unhingedHitLife = player.Unhinged.HitRecovery.Take(now);
                    if (noRegeneration) unhingedHitLife = 0;
                    if (convertLifeRegen) player.EnergyShield.Gain(enduringCryLife);
                    double preventedLife = PreventedDamageOf(player).Take(recoveryFrom, now, preventedPerSecond);
                    if (convertLifeRegen) player.EnergyShield.Gain(preventedLife);
                    if (convertLifeRegen) player.EnergyShield.Gain(unhingedHitLife);
                    double recoupLife = player.LifeRecoup.Take(now) * mapRecovery;
                    // Flask recovery alone may pass Low Life under Petrified Blood. Every other source, leech included,
                    // stops at the boundary; leech instances stay alive, but their recovery above Low Life is ignored.
                    double nonFlaskLife = (zealotsOath ? 0 : lifeRate * seconds + periodicLife) + (zealotsOath ? 0 : fortificationLife)
                        + leeched.Life + (zealotsOath ? 0 : antiquarianLife) + (zealotsOath ? 0 : enduringCryLife + preventedLife) + recoupLife;
                    if (!zealotsOath) nonFlaskLife += unhingedHitLife;
                    // Vaal Pact: "Cannot Recover Life other than from Leech" — only leech recovers life here; the
                    // regeneration, flask, recoup and other non-leech shares are blocked. Their Energy Shield
                    // conversions under Zealot's Oath above still apply (the stats are not removed).
                    if (vaalPact) { nonFlaskLife = leeched.Life; life = 0; }
                    AdvanceTincturesLocked(player, now, seconds, recoveryStats, out double tinctureManaDrain,
                        out double tinctureLifeDrain, ref deactivatedTinctures);
                    uint afterNonFlask = PetrifiedBlood.RecoverLife(player, lifeBefore, nonFlaskLife, ref lifeCarry, minimum: 1);
                    uint afterBurn = (uint)Math.Max(1, (long)afterNonFlask - lifeBurn);
                    if (tinctureLifeDrain > 0)
                        afterBurn = RecoveryPool.Advance(afterBurn, lifeCap, -tinctureLifeDrain, ref player.TinctureEffects.LifeBurnCarry, minimum: 1);
                    player.PlayerLife = lifeAfter = RecoveryPool.Advance(afterBurn, lifeCap, life, ref flaskCarry);
                    if (player.BladefallTrarthus is { PaysLife: true, Ended: false, OutOfLife: false } blades)
                    {
                        uint payment = Lifetap.PayOngoingLife(lifeAfter, blades.CostPerMinute * seconds / 60,
                            ref blades.LifeCostCarry, out blades.OutOfLife);
                        player.PlayerLife = lifeAfter -= payment;
                    }
                    // Trap degeneration can kill; keep the same current-owner recovery and death publication gates.
                    if (lifeTrap > 0 && !HardcoreProgressBlocked(player))
                    {
                        player.PlayerLife = lifeAfter = VaalImmortalCallLife(player, (uint)Math.Max(0, (long)lifeAfter - lifeTrap), now);
                        if (trapDeath = lifeAfter == 0) { player.InvalidateIncomingHits(); ClearAntiquarianOnDeath(player); }
                        RecordHardcoreDeath(player);
                    }
                    // Petrified Blood's own life loss is not damage and cannot be mitigated; it may kill, through the
                    // same death gates as trap degeneration.
                    double pbLoss = player.PetrifiedBlood.Take(now);
                    if (InGracePeriod(player)) pbLoss = 0;
                    if (pbLoss > 0 && lifeAfter > 0 && !HardcoreProgressBlocked(player))
                    {
                        player.PlayerLife = lifeAfter = VaalImmortalCallLife(player,
                            (uint)Math.Max(0, (long)lifeAfter - (long)Math.Round(pbLoss, MidpointRounding.AwayFromZero)), now);
                        if (pbDeath = lifeAfter == 0) { player.InvalidateIncomingHits(); ClearAntiquarianOnDeath(player); }
                        RecordHardcoreDeath(player);
                    }
                    bool survived = CompleteResourceRecoveryLocked(player, recoveryFrom, now, recoveryStats,
                        mana + manaRate * seconds + leeched.Mana - BladefallManaSpent(player, seconds) - tinctureManaDrain, manaBefore, manaCap, ref manaCarry);
                    lifeAfter = player.PlayerLife;
                    if (survived) CheckTinctureResourceDeactivationLocked(player, now, recoveryStats, ref deactivatedTinctures);
                    // The once-per-transition Loyalty triggers: Energy Shield Recharge started, Low Life reached.
                    LoyaltyResourceStateLocked(player, recoveryStats, now);
                    if (!survived)
                    {
                        DeactivateAllTincturesOnDeathLocked(player, now, recoveryStats, ref deactivatedTinctures);
                        ailmentDeathEpoch = player.IncomingHitEpoch; dead = true;
                        lifeCarry = manaCarry = burnCarry = trapCarry = flaskCarry = 0; manaAfter = manaBefore;
                    }
                    else
                    {
                        // The client's own Life step must stop at the same Low Life boundary. Publish the real recovery
                        // rate only while Life is more than a short prediction margin below it; inside the margin, the
                        // server's own updates finish the recovery, so the client cannot predict across the boundary.
                        uint lowLife = (uint)((long)player.PlayerMaxLife * LowLifeThresholdPct(recoveryStats) / 100);
                        uint margin = PetrifiedBlood.RecoveryMargin(lifeRate);
                        bool recoveryBlocked = player.PetrifiedBlood.Active && lifeAfter > 0
                            && (long)lifeAfter + margin >= lowLife;
                        if (recoveryBlocked != player.PetrifiedBlood.SentRecoveryBlocked)
                        {
                            player.PetrifiedBlood.SentRecoveryBlocked = recoveryBlocked;
                            (pbRateChanged, pbRateBlocked) = (true, recoveryBlocked);
                        }
                        manaAfter = player.PlayerMana;
                        leech.ClearFull(now, lifeAfter >= lifeCap, manaAfter >= manaCap,
                            player.EnergyShield.Current >= player.EnergyShield.Maximum);
                    }
                }
            }
            if (pbRateChanged && !dead)
            {
                await SendPetrifiedBloodRecoveryRate(player, pbRateBlocked);
                if (!PlayerUpdatesCurrent(player, updates)) return;
            }
            if (ailmentDeathEpoch is int lethalEpoch)
                await PlayerDiedAtEpoch(player,
                    trapDeath ? "0x0055 lethal trap degeneration"
                        : pbDeath ? "0x0055 Petrified Blood life loss" : "0x0055 lethal resource loss", lethalEpoch,
                    deathKind: InstanceProtocol.DeathKindDegen);
            if (!PlayerUpdatesCurrent(player, updates)) return;
            await SendPlayerEnergyShield(player);
            if (!PlayerUpdatesCurrent(player, updates)) return;
            if (deactivatedTinctures is { Count: > 0 })
                await SendTinctureBarInactive(player, deactivatedTinctures);
            if (player.TinctureEffects.DrainPendingAdjacentCharges() is { Count: > 0 } adjacentFlaskCharges)
                await SendTinctureAdjacentFlaskCharges(player, adjacentFlaskCharges);
            await PublishFlaskChanges(player);
            if (!PlayerUpdatesCurrent(player, updates)) return;
            if (dead)
            {
                if (!PlayerUpdatesCurrent(player, updates)) return;
                ended.AddRange(player.FlaskEffects.End(_ => true));
                await EndFlaskEffects(player, ended, "death");
                if (!PlayerUpdatesCurrent(player, updates)) return;
                return;
            }
            if (rf is not null && lifeAfter == 1 && player.EnergyShield.Current == 0)
                await EndRighteousFire(player, "one life and no energy shield", rf);
            if (!PlayerUpdatesCurrent(player, updates)) return;
            if ((lifeAfter != lifeBefore || instantLifeChanged) && ailmentDeathEpoch is null) Note(null);
            if (!PlayerUpdatesCurrent(player, updates)) return;
            if (manaAfter != manaBefore || instantManaChanged) await SendPlayerMana(player, null);
            if (!PlayerUpdatesCurrent(player, updates)) return;
            bool lifeFull = lifeAfter == LifeCap(player), manaFull = manaAfter == manaCap;
            if (!PlayerUpdatesCurrent(player, updates)) return;
            ended.AddRange(player.FlaskEffects.End(e => (e.LifePerSecond == 0 || lifeFull) && (e.ManaPerSecond == 0 || manaFull)));
            ended = player.FlaskEffects.KeepRunning(ended, lifeFull, manaFull, now);
            if (ended.Count > 0)
                await EndFlaskEffects(player, ended, $"life {lifeAfter}/{LifeCap(player)}, mana {manaAfter}/{manaCap}");
            if (!PlayerUpdatesCurrent(player, updates)) return;
        });
    }

    // Called under the combat lock. Advance deadlines and consume accrued recovery without paying it into pools.
    public static void DiscardLoadingRecovery(Player player, double now)
    {
        LeechOf(player).TakeRecovery(now);
        FortificationOf(player).TakeRecovery(now);
        player.Antiquarian.TakeRecovery(now);
        player.EnduringCryRecovery.Take(now);
        player.Unhinged.HitRecovery.Take(now);
        player.LifeRecoup.Take(now);
        DiscardResourceLoadingRecovery(player, now);
    }

    // The ended flask effects' buffs come off the player (S→C 0x1c2).
    public static async Task EndFlaskEffects(Player player, List<Flasks.Effect> ended, string why)
    {
        foreach (var e in ended)
            if (FlaskBuffs && e.Buff != 0) { player.Area.Buffs.Clear(player.Id, e.BuffRow, player.Id, tag: e.Buff); Note($"player buff {e.Buff}: flask {e.Slot} effect ends ({why})"); }
            else Console.WriteLine($"[gateway] {player.Connection.Remote} flask {e.Slot} effect ends ({why})");
        // A life, mana or hybrid flask's "during effect" mods end with its recovery.
        bool changed;
        changed = player.RecoveryFlaskStats.Prune(player.FlaskEffects);
        if (changed) RecomputePoolsAndSpeed(player);
        if (changed)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} recovery flask during effect ends ({why})");
            await SendCharacterStats(player, PlayerStatChange.Flasks);
        }
    }

    // Each entry grants more or less damage to this fire area DoT once its support is admitted.
    static readonly uint[] RighteousFireSupportMore =
    [
        Stat.SupportBurningDamagePlusPctFinal, Stat.SupportGemElementalDamagePlusPctFinal,
        Stat.SupportAreaConcentrateAreaDamagePlusPctFinal, Stat.SupportEfficacyDamageOverTimePlusPctFinal,
        Stat.SupportRapidDecayDamageOverTimePlusPctFinal, Stat.SupportReducedDurationDamagePlusPctFinal,
        Stat.CastOnDamageTakenDamagePlusPctFinal,
    ];

    static double RighteousFireEnemyDps(Player player, RighteousFireState rf,
        IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int> own)
    {
        int Own(uint key) => own.GetValueOrDefault(key);
        double Combined(uint key) => (double)playerStats.GetValueOrDefault(key) + own.GetValueOrDefault(key);
        if (DamageStats.Prohibited(DamageType.Fire, Own)) return 0;
        double basis = (player.PlayerMaxLife * (double)rf.DamagePctPerMinute
            + player.Pools.EnergyShield * (double)rf.EnergyShieldDamagePctPerMinute) / 6000;
        double increased = Combined(Stat.DamagePlusPct) + Combined(Stat.FireDamagePlusPct) + Combined(Stat.ElementalDamagePlusPct)
            + Combined(Stat.DamageOverTimePlusPct) + Combined(Stat.FireDamageOverTimePlusPct) + Combined(Stat.BurnDamagePlusPct)
            + Combined(Stat.AreaDamagePlusPct) + Combined(Stat.RighteousFireDamagePlusPct);
        double dps = basis * Math.Max(0, 100 + increased) / 100;
        foreach (uint stat in RighteousFireSupportMore) dps *= Math.Max(0, 1 + Own(stat) / 100d);
        double dotMultiplier = Combined(Stat.DotMultiplierPlus) + Combined(Stat.FireDotMultiplierPlus);
        dps = dps * Math.Max(0, 100 + dotMultiplier) / 100;
        int deliriumMore = playerStats.GetValueOrDefault(Stat.DeliriumAuraDamageOverTimePlusPctFinal);
        return dps * Math.Max(0, 1 + deliriumMore / 100d) * ChargeDamageMore(playerStats);
    }

    // Righteous Fire's burn: a damage aura (DamageAuras.cs) around the player, its fire damage a share of the player's
    // maximum life and energy shield per minute, with its aura buff on the hostile monsters inside.
    static void StartRighteousFireBurn(Player player, RighteousFireState rf) => StartDamageAura(player, new DamageAura
    {
        Name = "Righteous Fire",
        Radius = () => rf.Radius,
        BuffRow = RighteousFireAuraBuffRow,
        Skill = rf.Skill,
        Visual = rf.AuraVisual,
        Marks = rf.Auras,
        Running = () => RighteousFireCurrentLocked(player, rf),
        Valid = () =>
        {
            if (CurrentRighteousFireSkillLocked(player, rf) is not null) return true;
            if (PlanRighteousFireDetachLocked(player, rf) is { } stale)
                QueueRighteousFireCleanupLocked(player, stale, "stale Righteous Fire consumer retired");
            return false;
        },
        Active = () => player.PlayerLife > 0 && !InGracePeriod(player),
        Damage = seconds =>
        {
            using var scope = new PlayerStatScope(player, fresh: true);
            var stats = new Dictionary<uint, int>(CharacterPlayerStats(player));
            double perSecond = CurrentRighteousFireSkillLocked(player, rf) is { } supported
                ? RighteousFireEnemyDps(player, rf, stats, supported.Stats) * RighteousFireTimedSupportFactor(rf, AreaTime(player), seconds)
                : 0;
            return (new SkillDamageContext(SkillHitKind.Spell, stats).OverTime(new DamageOverTime(DamageType.Fire, perSecond, seconds)), stats);
        },
    });

    // The quest flags the permanent Kitava resistance penalty mirrors (ResistancePenalty): the Act 5 pair and the
    // Act 10 pair. A change to any of them rebuilds the pools (SetQuestFlags), because the overcapped-resistance
    // modifiers (ResistanceOvercap) read the penalty.
    public static readonly string[] ResistancePenaltyAct5 = ["a5q6-CompletedBoss", "SinOnKitavaDefeat"],
        ResistancePenaltyAct10 = [Quests.EndGameAvailable, "a10q3-UsedRewardBook"];

    // The permanent resistance penalty from Kitava: -30% after the Act 5 encounter and another -30% after the final
    // Act 10 Kitava. It is a quest penalty, not a buff/aura: the client keeps it through its quest flags, and the server
    // mirrors it from the same flags. (In the 3.29.3b tables QuestStaticRewards 38 is a5q6-CompletedBoss and 39 is
    // EndGameAvailable, set when Kitava dies in The Feeding Trough; a10q3-UsedRewardBook, Lani's book after him, implies
    // it. EndGameAvailable counts only once earned: every character is given it for the Atlas (in a hideout and the maps
    // only until then, Quests.EndgameOnly), Player.UnearnedQuestFlags.
    // a9q5-KilledBoss is the Act 9 boss and does not grant it.)
    public static int ResistancePenalty(Player player)
    {
        bool act5, act10;
        act5 = ResistancePenaltyAct5.Any(player.Character.QuestFlags.Contains);
        act10 = ResistancePenaltyAct10.Any(f => player.Character.QuestFlags.Contains(f) && !player.Character.UnearnedQuestFlags.Contains(f));
        return (act5 ? -30 : 0) + (act10 ? -30 : 0);
    }

    // Whether a quest-flag change moves the Kitava resistance penalty.
    public static bool ChangesResistancePenalty(IEnumerable<string> changed) =>
        changed.Any(ResistancePenaltyAct5.Concat(ResistancePenaltyAct10).Contains);

    // The player's final resistance for one damage type: the passive/gear modifier plus the Kitava quest penalty.
    public static int Resistance(Player player, DamageType type)
    {
        int modifier = type switch
        {
            DamageType.Fire => player.Pools.FireResistance,
            DamageType.Cold => player.Pools.ColdResistance,
            DamageType.Lightning => player.Pools.LightningResistance,
            DamageType.Chaos => player.Pools.ChaosResistance,
            _ => 0,
        };
        return type == DamageType.Physical ? modifier : modifier + ResistancePenalty(player) + PlayerScorchedResistance(player, type);
    }

    // The per-type maximum from Character.ot's 75 plus the passive/gear maximum-resistance modifiers, hard-capped at 90.
    public static int MaxResistance(Player player, DamageType type) => type switch
    {
        DamageType.Fire => player.Pools.FireMaxResistance,
        DamageType.Cold => player.Pools.ColdMaxResistance,
        DamageType.Lightning => player.Pools.LightningMaxResistance,
        DamageType.Chaos => player.Pools.ChaosMaxResistance,
        _ => DamageModel.DefaultMaxResistance,
    };

    // Damage immunities as the damage model sees them, from the same sources the panel's "Immune to X" rows use.
    public static HashSet<DamageType> PlayerImmunities(Player player) => PlayerImmunities(player, null);

    public static HashSet<DamageType> PlayerImmunities(Player player, IReadOnlyDictionary<uint, int>? prepared)
    {
        if (InGracePeriod(player)) return [.. Enum.GetValues<DamageType>()];
        prepared ??= CharacterPlayerStats(player);
        var result = DamageImmunities.From(name => name is var key ? prepared.GetValueOrDefault(key) : 0);
        if (AreaTime(player) < player.PhysicalImmunityUntil) result.Add(DamageType.Physical);
        return result;
    }

    // Spell Suppression: chance in 0..100 with the conditional passives that hold now (full life, full energy shield,
    // moving, an enemy hit in the last 4 s: PoB's "recently"), the reduction on a successful suppress (40% by default)
    // and its lucky or unlucky roll (PoB's suppressRolls).
    public static (int Chance, int Effect, bool Lucky, bool Unlucky) Suppression(Player player)
        => (SpellDefences(player).Suppression, Math.Max(0, player.Pools.SpellSuppressionEffect),
            player.Pools.SpellSuppressionLucky, player.Pools.SpellSuppressionUnlucky);

    public static (int Suppression, int Dodge) SpellDefences(Player player)
    {
        double now = AreaTime(player);
        bool fullLife, fullEnergyShield, hitRecently;
        fullLife = CountsAsFullLife(player.PlayerLife, player.PlayerMaxLife, SelfCurseConditionInputs(player));
        fullEnergyShield = player.PlayerLife > 0 && player.EnergyShield.Full;
        hitRecently = now - player.LastEnemyHitAt <= RecentlySeconds;
        bool moving = player.Walk.Moving(now);
        return (CharacterStats.SuppressionChance(player.Pools, fullLife, moving, hitRecently, fullEnergyShield),
            CharacterStats.SpellDodgeChance(player.Pools, fullLife, moving, hitRecently, fullEnergyShield));
    }

    static bool DodgesSpellHit(Player player) =>
        (player.Pools.ConvertSuppressionToSpellDodge || player.Pools.BaseSpellDodgeChance > 0)
        && GameServer.Core.World.RandomRolls.Chance(SpellDefences(player).Dodge, 100, player.IncomingHitRandom);

    // Devout of Garukhan's while-moving evasion and regeneration as finals, again when a walk starts or ends, for a
    // character with a while-moving stat. The client steps its pools from life/mana_recovery_per_minute and does not
    // derive them from the raw stats (re/recovery-timing.md), so the panel and that prediction follow the walk.
    static readonly uint[] WhileMovingInputs = [Stat.EvasionRatingPlusPctWhileMoving,
        Stat.LifeRegenerationRatePerMinutePctWhileMoving, Stat.ManaRegenerationRatePlusPctWhileMoving];
    static readonly uint[] WhileMovingFinals = [Stat.EvasionRating, Stat.LifeRecoveryPerMinute,
        Stat.TotalLifeRecoveryPerMinuteFromRegeneration, Stat.ManaRecoveryPerMinute, Stat.TotalManaRecoveryPerMinuteFromRegeneration];

    public static Task SendWhileMovingStats(Player player) =>
        !player.Pools.WhileMovingStats ? Task.CompletedTask : Send(player, [], null, atGate: () =>
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return null;
            var stats = CharacterPlayerStats(player);
            // A row the stationary panel omits (no life regeneration) goes as 0, so the client stops predicting it.
            return PlayerStatsUpdateAtGate(player, [.. WhileMovingFinals.Select(n => (n)).OfType<uint>()
                .Select(k => (k, stats.GetValueOrDefault(k)))]);
        });

    // The evasion rating monster attacks roll against: the while-moving rating during a walk (Devout of Garukhan).
    public static int PlayerEvasionBase(Player player, double now)
    {
        EnsureConditionalItemPools(player);
        return player.Walk.Moving(now) && player.Pools.EvasionWhileMoving is int moving ? moving : player.Pools.Evasion;
    }

    // The death screen's buttons (C→S 0x54). Both hide the screen (0x55 with bit1) and give the player full life; "at
    // checkpoint" revives next to the area's activated waypoint, else at its arrival point (teleport 0x1b7, AreaLayout.Checkpoint),
    // "in town" in the character's last town (an area change;
    // in that town it is the same as the checkpoint).
    public static async Task Resurrect(Player player, bool inTown)
    {
        // The native button is hidden for HC, but a forged 0x54 must not revive the old character in its source league.
        if (player.Character.Hardcore || player.Character.HardcoreDeathRecorded) return;
        if (player.PlayerLife > 0) return;
        player.InvalidateIncomingHits();
        LeechOf(player).Clear(AreaTime(player));
        player.PlayerLife = LifeCap(player);
        player.EnergyShield.SetMaximum(player.Pools.EnergyShield, refill: true);
        player.HitRecovery.Clear();
        player.Ward.SetMaximum(player.Pools.Ward); player.Ward.Restore();
        player.AuraDeathHandled = false;
        player.BossRecoveryHandled = false;
        player.PlayerMana = (uint)UnreservedMana(player);
        foreach (var m in player.Area.Monsters.Values) m.Aggro = false;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0054 resurrect {(inTown ? "in town" : "at checkpoint")}");
        await Send(player, InstanceProtocol.DeathScreen(0x02), "0x0055 hide the death screen");
        // The flasks are full again after a death.
        await GainFlaskCharges(player, int.MaxValue, "resurrected");
        if (inTown && player.Character.LastTown != 0 && player.Layout.Hash != player.Character.LastTown)
        {
            // The new area's player object is created with full life.
            await ChangeArea(player, new PendingTransfer(player.Character.LastTown, 0, false, AtSpawn: true), "resurrect in town");
            await GameEvents.RunResurrected(player);
            return;
        }
        if (!inTown && MapCheckpointExit(player) is { } exit)
        {
            // The area the map was entered from is remade on the next entry (a town or a hideout) and an empty instance is
            // gone six minutes after its last player leaves, so the instance the device recorded may be gone: Auth refuses
            // that transfer ("instance N unavailable in this league") and leaves the dead player in the map (live
            // 2026-11-18, the Simulacrum). Then enter a new instance of that area by its device, as the portal back does
            // (PortalsFeature.ReturnTransfer): it is seed 1, so the cell is the same, and the run's portals follow there.
            if (exit.Instance != 0 && AreaInstances.RecreatedOnEntry(exit.Area)
                && await AreaInstances.Gone(player, exit.Area, exit.Instance))
                exit = exit with { Instance = 0 };
            await ChangeArea(player, exit, "resurrect at checkpoint: out of the map, where it was entered from");
            await GameEvents.RunResurrected(player);
            return;
        }
        // Revive first: a teleport sent while the player is dead left them where they died (live 2026-10-17).
        Note($"0x01b1 player revived with {player.PlayerLife} life");
        await SendPlayerMana(player, $"0x01b1 player mana {player.PlayerMana}");
        // Death may have published false full-pool conditions while the player ticker kept its pre-death chance.
        // Publish the restored life/ES conditions explicitly even when that cached chance is unchanged.
        await SendCharacterStats(player, PlayerStatChange.Panel);
        // The checkpoint: the area's waypoint once activated, else where the player entered the area, as in the real game
        // before any checkpoint is activated. Checkpoint objects and boss arenas (revive at the arena's door) are not done.
        bool activated;
        lock (player.Character.Waypoints)
            activated = WaypointCatalog.Areas.Find(player.Layout.Hash) is { } entry && player.Character.Waypoints.Contains(entry.Id);
        var (x, y) = player.Layout.Checkpoint(activated);
        player.Walk.Teleport(x, y, AreaTime(player));
        await SendWorldRecord(player, player.Id, InstanceProtocol.Teleport(player.Id, x, y),
            $"0x01b7 player to the checkpoint ({x}, {y}): {(activated && player.Layout.Waypoint is not null ? "the activated waypoint" : "the arrival point")}");
        SetStats(player, player.Id, [(Stat.HideMiniLifeBar, 0)]); Note("0x01b3 player hide_mini_life_bar = 0");
        await StartGracePeriod(player);
        await GameEvents.RunResurrected(player);
    }

    // "Resurrect at Checkpoint" in a map leaves it for where it was entered from, the map device's area (the hideout),
    // where its portals stand; "Resurrect in Town" still goes to the last town (the official servers, 3.30.0b; issue #1002). Null outside
    // maps: there the checkpoint is in the area.
    public static PendingTransfer? MapCheckpointExit(Player player) =>
        MapDevice.ForInstance(player) is { } map && (map.ReturnTo ?? map.Entrance) is { } origin
            ? new PendingTransfer(origin.Area, origin.Instance, false, ArriveAt: (origin.X, origin.Y))
            : MapDevices.IsMapArea(player.Layout.Hash) && HomeHideout(player) is { } hideout
                ? new PendingTransfer(hideout.AreaHash16, 0, false, AtWaypoint: true) : null;

    // The player in its class: the Characters.dat row's object type (Metadata/Characters/StrInt/StrInt for the Templar), model
    // (AOFile), class byte (IntegerId), attributes and unarmed attack; the name is the character's.
    public static PlayerObject PlayerForCreation(Player player)
    {
        var (layout, name, classRow, pools) = (player.Layout, player.Character.Name, player.Character.ClassRow, player.Pools);
        var cls = SkillsFor(player).ClassFor(classRow);
        // Keep the ticket's remaining Rage timers frozen while the destination loads (before 0x7d).
        RestoreRage(player);
        RestoreRampage(player);
        RestoreResourceEffects(player);
        RestoreVaalSouls(player);
        RestoreAntiquarian(player);
        player.Ward.SetMaximum(player.Pools.Ward);
        pools = player.Pools;
        var stats = CharacterPlayerStatsSnapshot(player, out _, out var panelSources, capturePanelSources: true);
        var characterInputs = new Dictionary<uint, int>(stats);
        // The equipped weapon's attack with its own local mods (WeaponFor), as SendSkills sends it after an equipment change:
        // before, every area started with the class's unarmed attack, so a weapon's added damage and attack speed showed only
        // after taking it off and putting it back. After the stats, which mark the inactive items WeaponFor skips.
        var weapon = WeaponFor(player);
        foreach (var (k, v) in CosmeticStats(player).Where(s => !ActorScale.IsInput(s.Stat))) stats.TryAdd(k, v);
        var statList = stats.Select(kv => (kv.Key, kv.Value)).ToList();
        var weaponStats = WeaponStats(player, weapon, stats);
        // The new player object starts with exactly these (PlayerStatsWithCleared clears from here).
        player.Area.Stats.Created(player.Id, [.. statList, .. weaponStats]);
        int reservedMana = ReservedMana(player);
        int reservedLife = ReservedLife(player);
        bool washedUp = player.ArriveAt is null && player.CameFrom is null && !player.ArriveAtWaypoint
            && TutorialObjects.ShouldStartWashedUp(layout.Id, ItemsOf(player).StartWeapon is not null,
                () => CharacterSaves.Load(CharacterName(player))?.Tutorial);
        var playerObject = new PlayerObject(string.IsNullOrEmpty(name) ? "Exile" : name, ClassIndex: PackedClass(player), GridX: layout.SpawnX, GridY: layout.SpawnY, Life: player.PlayerLife, EnergyShield: player.EnergyShield.Current,
            Mana: (uint)Math.Min(player.PlayerMana, (uint)Math.Max(0, (int)pools.Mana - reservedMana)), ManaReserved: (uint)reservedMana,
            LifeReserved: (uint)reservedLife,
            Experience: player.Character.Experience,
            AnimatedObject: WornMorph(player)?.Model ?? Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? cls.AnimatedObject,
            Skills: [.. PlayerDefaultSkills(player, weaponStats, stats), .. GemSkills(player, [.. weaponStats, .. statList], characterInputs, panelSources!)], Stats: [.. weaponStats, .. statList],
            Equipped: Looks(player, PlayerInventories.For(string.IsNullOrEmpty(name) ? "Exile" : name, ItemsFor(player))),
            QuestFlags: PlayerQuestFlags(player), Hideout: HomeHideout(player)?.Hash16 ?? 0, LeagueQuestFlags: [.. Quests.League.Bits(player.Character.LeagueQuestFlags)],
            Portrait: WornPortrait(player), PortraitFrame: player.Character.PortraitFrame)
        {
            // A worn morph's animation set (WornMorph: the Goblin Shaman Skin in a town or hideout).
            ActorFile = WornMorph(player)?.Actor,
            // The saved Pantheon gods (campaign/pantheon.md §5): the Player component's two bytes.
            PantheonMajor = PantheonCatalog.Default.Wire(player.Character.PantheonMajor),
            PantheonMinor = PantheonCatalog.Default.Wire(player.Character.PantheonMinor),
            Descendancy = player.Character.DescendancyRow,
            WashedUp = washedUp,
            Rotation = washedUp ? layout.SpawnRotation : 0,
        };
        RefreshVaalSouls(player);
        return playerObject with { Souls = player.VaalSouls.Read() };
    }

    public static async Task SendPlayer(Player player)
    {
        RestoreFortification(player);
        PrepareHideoutWaypoint(player);
        PrepareKaruiBridgeGates(player);
        DepravedTrinity.ConfigureRoutes(player);
        var (layout, name, classRow) = (player.Layout, player.Character.Name, player.Character.ClassRow);
        var cls = SkillsFor(player).ClassFor(classRow);
        uint type = InstanceEntities.ObjectTypeHash(cls.ObjectType);
        player.PeerType = type;
        const string creationLabel = "0x02bb create current player";
        var creates = new Dictionary<byte[], uint>();
        var world = new Dictionary<byte[], uint>();
        byte[] Create(uint id, uint type, byte[] blob)
        {
            var bytes = InstanceEntities.CreateObject(id, type, blob);
            creates.Add(bytes, id);
            return bytes;
        }
        byte[] Wake(uint id)
        {
            var bytes = InstanceProtocol.EnterWakeZone(id);
            world.Add(bytes, id);
            return bytes;
        }
        var messages = new List<(string Name, byte[] Bytes)>
        {
            ("0x01f6 campaign hideout unlocks", InstanceMicrotransactions.HideoutUnlocks(
                HideoutCatalog.OwnedRows.Where(row => row < 128).Select(row => (int)row))),
            ("0x0022 local player 0x1000", InstanceEntities.SetLocalPlayer(player.Id)),
            (creationLabel, []),
        };
        // The player's inventories (id = row + 1), needed by UI built on entering (flask bar → row 11), with the character's items.
        var items = PlayerInventories.For(string.IsNullOrEmpty(name) ? "Exile" : name, ItemsFor(player));
        if (ItemsFor(player).Error is not null) Console.WriteLine($"[gateway] items: {ItemsFor(player).Error}");
        foreach (var row in InstanceInventories.PlayerDefaults)
        {
            var slot = GameEvents.ResolveInventorySlot(player, row);
            var inside = items.Items(slot.Row);
            messages.Add(($"0x00c0 inventory {PlayerInventories.InventoryId(slot.Row)} row {slot.Row}" + (inside.Count > 0 ? $" items {inside.Count}" : ""),
                InstanceInventories.AddInventory(PlayerInventories.InventoryId(slot.Row), slot, items: inside)));
        }
        messages.AddRange(AtlasPresetMessages(player, "player"));
        if (ExtraPlayerMessages is { } extra)
            foreach (var (log, message) in extra(player))
                messages.Add((log, message));
        messages.AddRange(GameEvents.RunBeforeEnterMessages(player));
        var (passives, passivesLabel) = PassivesMessage(player);
        messages.Add((passivesLabel, passives));
        // Area transitions. Like the player they are queued by the world and created when 0x21 activates it.
        foreach (var (id, transition) in layout.AreaTransitions.Where(t => TransitionShown(player, t.Id, t.Transition)))
        {
            lock (player.Connection.ShownTransitions) player.Connection.ShownTransitions.Add(id);
            messages.Add(($"0x02bb create area transition 0x{id:x} {transition}",
                Create(id, InstanceEntities.ObjectTypeHash(transition.Type), transition.Blob())));
        }
        foreach (var (id, effect) in layout.Effects)
            messages.Add(($"0x02bb create transition effect 0x{id:x} {effect}",
                Create(id, InstanceEntities.ObjectTypeHash(effect.Type), effect.Blob())));
        if (layout.Waypoint is { } waypoint)
            messages.Add(($"0x02bb create waypoint 0x{WaypointCatalog.ObjectId:x} {waypoint}",
                Create(WaypointCatalog.ObjectId, InstanceEntities.ObjectTypeHash(WaypointObject.MetadataPath), waypoint.Blob())));
        if (Environment.GetEnvironmentVariable("Game_GATEWAY_ENTER") == "1")
        {
            var tabs = PersonalStashOf(player).Records((int)StashSelectAffinity);
            bool secondSet = PlayerInventories.For(string.IsNullOrEmpty(name) ? "Exile" : name, ItemsFor(player)).WeaponSet == 1;
            messages.Add(($"0x0021 enter area, {tabs.Count} stash tab(s), last {PersonalStashOf(player).LastTab}, weapon set {(secondSet ? 2 : 1)}, frame {FrameMs} ms",
                InstanceProtocol.EnterArea(tabs, secondSet, FrameMs)));
            messages.AddRange(GameEvents.RunEnterMessages(player));
            // The contents of every stash tab with currency in it (stackable items), as 0xc7 would send them (0xc0, no 0xc8:
            // the panel is not open). The client checks a shop price against the currency it knows of, the inventory's and
            // the loaded stash tabs' (3.29 vendors take it from the stash): without this a price was red, and no 0x88 came,
            // until the stash had been opened in that area (live 2026-10-23, Nessa in the hideout). Shop prices are in
            // currency (class StackableCurrency), which only a currency tab or a grid tab can hold: every other loaded tab
            // made each stash open slower by its item count (a full divination tab alone is 470 items, fragments 255), and
            // those tabs still load when the panel shows them (0xc7).
            var personal = PersonalStashOf(player);
            for (int t = 0; t < personal.Count; t++)
            {
                var (slot, inside) = personal.Inventory(t);
                if (!PreloadsForPrices(personal.TabType(t), inside, ItemsFor(player)) || !player.Connection.StashTabsSent.Add(t)) continue;
                messages.Add(($"0x00c0 stash tab {t} inventory {personal.InventoryOf(t)}: {inside.Count} item(s) (currency for shop prices)",
                    InstanceInventories.AddInventory(personal.InventoryOf(t), slot, 0, inside)));
            }
            // Put the player in the wake zone so it is drawn and its actions/movement run.
            messages.Add(("0x01a9 player enters wake zone", Wake(player.Id)));
            foreach (var (id, _) in layout.AreaTransitions.Where(t => TransitionShown(player, t.Id, t.Transition)))
                messages.Add(($"0x01a9 area transition 0x{id:x} enters wake zone", Wake(id)));
            foreach (var (id, _) in layout.Effects)
                messages.Add(($"0x01a9 transition effect 0x{id:x} enters wake zone", Wake(id)));
            if (layout.Waypoint is not null) messages.Add(($"0x01a9 waypoint 0x{WaypointCatalog.ObjectId:x} enters wake zone", Wake(WaypointCatalog.ObjectId)));
            // The attributes once more as a stats update. UI built on entering caches them (0x141e869c0 reads the Stats
            // component's getter); live 2026-10-18 the cache held 0 while the getter returned 14/14/32.
            messages.Add(("0x01b3 class attributes", []));
        }
        // Every connecting player receives its own bootstrap player, transitions, effects and waypoint before 0x0021;
        // mark them as this player's view so they are never recorded in Area.Objects and replayed to later joiners.
        var previousView = Program.ViewOf.Value;
        Program.ViewOf.Value = player;
        try
        {
            foreach (var (label, bytes) in messages)
                if (label == creationLabel)
                    await SendWorldCreate(player, player.Id, () =>
                    {
                        if (player.Closed || player.LeavingArea || player.RageRetired) return null;
                        ReconcileOwnAuras(player, validateSkills: true);
                        var playerObject = PlayerForCreation(player);
                        player.PeerTemplate = playerObject;
                        player.ClientSkillIds = [.. playerObject.Skills!.Select(s => s.Id)];
                        player.SentVaalSouls.Clear();
                        foreach (var budget in playerObject.Souls ?? [])
                            player.SentVaalSouls.Add(budget.ActiveSkillsRow, budget);
                        // A new player object has no buffs: every system's are written again.
                        player.Area.Buffs.Removed(player.Id);
                        player.LeechDisplay.Clear();
                        player.PetrifiedBlood.PresentedLossBuff = 0;
                        player.PetrifiedBlood.SentLossPerMinute = 0;
                        player.PetrifiedBlood.SentLossExpiry = 0;
                        player.AuraStatsDirty = false;
                        player.Connection.PlayerObjectCreated = true;
                        return InstanceEntities.CreateObject(player.Id, player.PeerType, playerObject.Blob());
                    }, label);
                else if (label == "0x01b3 class attributes")
                    await SendPlayerStatSnapshot(player,
                        () => PublishPlayerStats(player), label);
                else if (creates.TryGetValue(bytes, out uint created))
                    await SendWorldCreate(player, created, bytes, label);
                else if (world.TryGetValue(bytes, out uint target))
                    await SendWorldRecord(player, target, bytes, label);
                else await Send(player, bytes, label);
        }
        finally { Program.ViewOf.Value = previousView; }
        // An area change creates a new player object, so the running auras' buffs must be put on it again.
        await SendOwnAuraChanges(player, replay: true);
        await SendPhysicalAegis(player);
        await SendUnhinged(player);
        await SendAvatarOfFire(player);
        await SendHerEmbrace(player);
        await SendElusive(player);
        if (player.PeerTemplate is { WashedUp: true })
            CharacterSaves.ChangeTutorial(CharacterName(player), tutorial => tutorial with { WakeUpShown = true });
    }
}
