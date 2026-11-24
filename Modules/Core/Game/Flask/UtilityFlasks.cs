using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // C→S 0x70 on a utility flask (13-items.md §8a): it spends its charges (S→C 0xbf), puts its buff on the player with
    // its duration (S→C 0x1c1) and its stats count in the pools until the duration ends (UtilityFlasks), with the stats
    // and movement speed sent again at the start and the end. Refused while dead or with too few charges.
    // Reusing this flask replaces its timer; another flask of the same kind keeps its own, strongest effect applies.
    public static async Task<bool> UseUtilityFlaskCore(Player player, uint slot,
        InventoryItem flask, ItemCatalog.BaseType type, UtilityFlasks.Utility utility, FlaskAutomationStep? signal = null)
    {
        var owner = CombatLifetime;
        bool CurrentUse() => CurrentGameWork && ReferenceEquals(CombatLifetime, owner) && !player.Closed
            && !player.LeavingArea && !player.RageRetired && !player.Paused && player.PlayerLife > 0
            && ItemsOf(player).Items(PlayerInventories.Flask).FirstOrDefault(i => i.Id == flask.Id && i.X == slot) is { } current
            && (signal is null || Automation(player).CanAccept(signal.Value, current.Id))
            && EndSignalCurrent(player, current, signal);
        if (!CurrentUse()) return false;
        // Mageblood: its magic utility flasks are always on and cannot be used (the client enforces the mod; this is the
        // server backstop).
        if (MagebloodBlocksFlaskUse(player, flask.Item))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0070 use flask {slot} ({type.Name}): refused, Mageblood's magic utility flasks cannot be used");
            return false;
        }
        // The character's flask stats (passives and gear: increased flask effect, duration and charges used) and the
        // flask's own mods.
        Dictionary<uint, int> character;
        int playerLevel;
        // Keep level and coefficient sources in one use snapshot, including across the asynchronous charge spend.
        playerLevel = player.Pools.Level;
        character = new Dictionary<uint, int>(CharacterPlayerStats(player));
        int Get(uint key) => character.GetValueOrDefault(key);
        var local = ItemModStats.Local(flask.Item, ItemsFor(player).Mods);
        var charges = FlaskCharges.Of(utility.Charges, local, Get);
        if (UtilityFlasks.Refusal(player.PlayerLife == 0, player.UtilityFlaskEffects.Of(flask.Id), flask.Item, charges,
                AreaTime(player)) is { } refused)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0070 use flask {slot} ({type.Name}): refused, {refused}");
            return false;
        }
        int adjacentEffect = AdjacentTinctureFlaskEffect(player, (byte)slot, Get, AreaTime(player));
        var flaskCharacter = SelfFlaskEffectValues(Get, adjacentEffect);
        double finalMultiplier = MapFlaskEffectMultiplier(player);
        var (stats, seconds) = ItemUtilityFlaskFor(utility, flask.Item, local, flaskCharacter, playerLevel, finalMultiplier);
        int heal = local.GetValueOrDefault(Stat.LocalUniqueFlaskInstantlyRecoversPctMaximumLife);
        int selfChaos = local.GetValueOrDefault(Stat.LocalUniqueFlaskChaosDamagePctOfMaximumLifeToDealPerMinuteWhileHealing);
        double effectPower = heal > 0 || selfChaos > 0 ? FlaskEffectScaling.Multiplier(flaskCharacter, playerLevel,
            local.GetValueOrDefault(Stat.LocalFlaskEffectPlusPct), finalMultiplier: finalMultiplier) : 1;
        int selfChaosRate = selfChaos > 0 ? UtilityFlasks.Scale(selfChaos, effectPower) : 0;
        bool gainedSouls = false;
        if (FlaskGrounds.OnUse(local).Any(g => g.Kind != FlaskGrounds.Kind.Taunt)) _ = FlaskGroundArt;
        // Payment and installation are one admitted transaction. A reload after the inventory send cannot
        // strand paid charges without their accepted snapshot, deadline, Adjacent edge and presentation.
        if (await SpendFlask(player, flask, charges, slot, CurrentUse, _ =>
        {
            var state = Automation(player);
            if (signal is { } step) state.Accepted(step, flask.Id);
            double now = AreaTime(player);
            GrantAlchemistsGeniusLocked(player, now, Get(Stat.GainAlchemistsGeniusOnFlaskUsePct));
            if (NamedFlaskRecent(type.Id) is { } recent) player.RecentEvents.Record(recent, now);
            if (Get(Stat.FlasksApplyToYourZombiesAndSpectres) != 0)
                foreach (var minion in FlaskMinions(player, raisedOnly: true))
                    ApplyMinionUtilityFlaskLocked(player, minion, flask, type, utility, local, Get, now);
            FlaskToLinkedTargetsLocked(player, flask.Item, local, utility, Get);
            if (Get(Stat.CannotBeAffectedByFlasks) != 0)
            {
                state.Enqueue(new(FlaskEnchantments.Trigger.Adjacent, flask.Id, (byte)slot), signal?.Chain);
                state.Packets.Add(new(InstanceItems.ItemUsed(flask.Item.TypeHash,
                    PlayerInventories.InventoryId(PlayerInventories.Flask), flask.Id), $"0x0263 flask {slot} used"));
                return;
            }
            // The "during effect" suffix buffs this use sends — one buff instance per suffix, on the
            // flask's slot with the flask's duration (UtilityFlasks.ShowBuffs), so the effect carries them and
            // EndBuffs removes exactly those.
            var suffixBuffs = UtilityFlasks.SuffixBuffRowsFor(local, ItemsFor(player).SuffixBuffRows)
                .Select(row => (Buff: player.NextBuff++, Row: row)).ToArray();
            var effect = new UtilityFlasks.Active(flask.Id, (byte)slot, type.Name, utility, player.NextBuff++,
                utility.Extra is null ? null : player.NextBuff++, stats, now, now + seconds)
                { SelfChaosDamageLifePctPerMinute = selfChaosRate, LocalStats = local, SuffixBuffs = suffixBuffs };
            AccrueUtilityFlaskDamage(player, now);
            if (player.UtilityFlaskEffects.Of(flask.Id) is { } previous)
                QueueUtilityFlaskEnd(player, state, previous, "refreshed");
            UtilityFlasks.Start(player, effect, () => RecomputePoolsAndSpeed(player));
            gainedSouls = ApplyUtilityResourceEffectsLocked(player, local, charges.ChargesPerUse, now, effectPower);
            ApplyConditionalFlaskAilmentProtectionLocked(player, local, now);
            if (UtilityFlasks.RestoreWardAfterPaidUse(player.Ward, name => local.GetValueOrDefault(name), now))
                Console.WriteLine($"[gateway] Ward restored on paid {type.Name}: {player.Ward.Current}/{player.Ward.Maximum}");
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0070 use flask {slot} ({type.Name}): {utility.BuffId} for {seconds:0.##} s: " +
                string.Join(", ", stats.Select(s => $"{s.Stat} {s.Value}")) + $"; walk estimate {player.Walk.Speed:0.##} cells/s");
            state.Packets.Add(new([], $"0x01c1 player buff: {type.Name} (flask {slot}), {seconds:0.##} s",
                UtilityStart: effect));
            // "Gain X Endurance/Frenzy/Power Charge(s) on use" (Replica Rumi's Concoction).
            foreach (var (stat, charge) in ChargesOnFlaskUse)
                if (local.GetValueOrDefault(stat) is int count and > 0)
                    ChargesOf(player).Enqueue(new ChargeEvent.Grant(charge, count, $"{type.Name} on use"));
            MakeFlaskGroundsLocked(player, local, seconds, now, character);
            state.StatRevision++;
            state.Enqueue(new(FlaskEnchantments.Trigger.Adjacent, flask.Id, (byte)slot), signal?.Chain);
        }) is null) return false;
        await PublishFlaskChanges(player);
        if (CurrentGameWork && !player.Closed && !player.LeavingArea)
        {
            if (gainedSouls) await SendVaalSouls(player);
            await EnemyDebuffsTick(player, AreaTime(player));
        }
        return true;
    }

    static string? NamedFlaskRecent(string baseTypeId) => baseTypeId switch
    {
        "Metadata/Items/Flasks/FlaskUtility2" => StatConditions.UsedRubyFlask,
        "Metadata/Items/Flasks/FlaskUtility3" => StatConditions.UsedSapphireFlask,
        "Metadata/Items/Flasks/FlaskUtility4" => StatConditions.UsedTopazFlask,
        "Metadata/Items/Flasks/FlaskUtility7" => StatConditions.UsedAmethystFlask,
        _ => null,
    };

    static readonly (uint Stat, ChargeType Charge)[] ChargesOnFlaskUse =
    [
        (Stat.LocalFlaskGainEnduranceChargesOnUse, ChargeType.Endurance),
        (Stat.LocalFlaskGainFrenzyChargesOnUse, ChargeType.Frenzy),
        (Stat.LocalFlaskGainPowerChargesOnUse, ChargeType.Power),
    ];

    // Innate monster IIQ/IIR baselines already represented by ItemDrops.Roll's rarity count ratios
    // (MonsterMagic3 +600%, MonsterRare3 +1400%, MonsterUnique3 +2850%) and RarityChances
    // (MonsterMagic4 +200%, MonsterRare4 +1000%, MonsterUnique4 +1000%).
    static int BaselineMonsterIiq(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.Magic => 600,
        MonsterRarity.Rare => 1400,
        MonsterRarity.Unique => 2850,
        _ => 0,
    };

    static int BaselineMonsterIir(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.Magic => 200,
        MonsterRarity.Rare => 1000,
        MonsterRarity.Unique => 1000,
        _ => 0,
    };

    // The player's increased rarity of items found (Gold Flask, gear, passives, and Item Rarity Support) multiplied
    // by area and monster/boss rarity (ItemDrops.WithIncreasedRarity). Player IIR uses PoE diminishing returns;
    // innate boss rarity (MonsterDroppedItemRarityPlusPct above the baseline rarity tier) uses soft diminishing
    // returns so Act Bosses (+10,000-15,000% IIR) drop abundant rares and elevated uniques (~3-4.5x) without
    // turning half their drops into uniques.
    public static int IncreasedItemRarity(Player player, SpawnedMonster? monster = null)
    {
        var stats = PlayerStatValues(player);
        int Get(uint key) => stats.GetValueOrDefault(key);
        int rawPlayerIir = Get(Stat.BaseItemFoundRarityPlusPct) + Get(Stat.ItemFoundRarityPlusPct);
        double effectivePlayerIir = ItemDrops.EffectivePlayerIir(rawPlayerIir)
            + Math.Max(0, monster?.KilledWithItemRarityPct ?? 0);
        double monsterIirFactor = 1.0;
        if (monster is not null)
        {
            int rawMonsterIir = monster.Effects[Stat.MonsterDroppedItemRarityPlusPct];
            int baseline = BaselineMonsterIir(monster.Rarity);
            if (rawMonsterIir > baseline)
            {
                double extraPct = ((100.0 + rawMonsterIir) / (100.0 + baseline) - 1.0) * 100.0;
                double effectiveBossPct = extraPct / (1.0 + extraPct / 450.0);
                monsterIirFactor = 1.0 + effectiveBossPct / 100.0;
            }
            else if (rawMonsterIir < 0)
                monsterIirFactor = Math.Max(0, 100.0 + rawMonsterIir) / 100.0;
        }
        int areaAndExtraIir = (MapModsOf(player)?.Rarity ?? 0)
            + (monster?.Effects[Stat.BaseItemFoundRarityPlusPct] ?? 0)
            + (monster?.Effects[Stat.FromLeagueItemRarityPlusPctPermyriad] ?? 0) / 100;
        double totalFactor = Math.Max(0, 100.0 + effectivePlayerIir) / 100.0
            * Math.Max(0, 100.0 + areaAndExtraIir) / 100.0
            * monsterIirFactor;
        return (int)Math.Round((totalFactor - 1.0) * 100.0);
    }

    // The player's increased quantity of items found (gear, passives) as a multiplier of the kill's item rolls
    // (ItemDrops.Roll). Player IIQ uses PoE diminishing returns; innate monster/boss quantity above the
    // baseline rarity tier (MonsterDroppedItemQuantityPlusPct) scales relative to the baseline without double-counting.
    public static double ItemQuantityFactor(Player player, SpawnedMonster? monster = null)
    {
        var stats = PlayerStatValues(player);
        int Get(uint key) => stats.GetValueOrDefault(key);
        int rawPlayerIiq = Get(Stat.BaseItemFoundQuantityPlusPct) + Get(Stat.ItemFoundQuantityPlusPct);
        double playerValue = Math.Max(0, 1.0 + ItemDrops.EffectivePlayerIiq(rawPlayerIiq));
        double monsterIiqFactor = 1.0;
        if (monster is not null)
        {
            int rawMonsterIiq = monster.Effects[Stat.MonsterDroppedItemQuantityPlusPct];
            int baseline = BaselineMonsterIiq(monster.Rarity);
            if (rawMonsterIiq > baseline)
                monsterIiqFactor = (100.0 + rawMonsterIiq) / (100.0 + baseline);
            else if (rawMonsterIiq < 0)
                monsterIiqFactor = Math.Max(0, 100.0 + rawMonsterIiq) / 100.0;
        }
        return playerValue * (1 + Math.Max(0, monster?.KilledWithItemQuantityPct ?? 0) / 100.0)
            * Math.Max(0, 1 + (monster?.Effects[Stat.BaseItemFoundQuantityPlusPct] ?? 0) / 100.0)
            * Math.Max(0, 1 + (monster?.Effects[MapSulphite.EnragedQuantity] ?? 0) / 10000.0)
            * monsterIiqFactor
            * (MapModsOf(player)?.QuantityFactor ?? 1);
    }

    // The pools and the walk speed again (in the area's frame): after a utility flask effect started or ended, or the
    // equipment changed.
    public static void RecomputePoolsAndSpeed(Player player)
    {
        RefreshRighteousFireProfileLocked(player);
        using (new PlayerStatScope(player, fresh: true))
        {
            player.Pools = PoolsFor(player, player.Pools.Level);
            player.ApplyMovementSpeedAt(AreaTime(player));
            player.PlayerLife = LifeReservation.Clamp(player.PlayerLife, LifeReservationOf(player));
            var stats = CharacterPlayerStats(player);
            int Get(uint key) => stats.GetValueOrDefault(key);
            player.Phasing = UtilityFlasks.Phasing(UtilityStats(player, Get));
            player.ElementalAilments.Reconcile(Get);
            player.ApplyMovementSpeedAt(AreaTime(player));
        }
    }

    // The already-owned resource writer advances utility deadlines. During Stop/Start there is no writer;
    // the unchanged accepted effect is claimed by the replacement, even if its deadline passed in that gap.
    public static void AdvanceUtilityFlasks(Player player, PlayerUpdatesState updates)
    {
        if (!PlayerUpdatesCurrent(player, updates) || player.LeavingArea || !player.UtilityFlaskEffects.Any) return;
        bool dead = player.PlayerLife == 0;
        double now = updates.Schedule.Now(player);
        if (dead) ClearUtilityFlaskDamage(player, now);
        else AccrueUtilityFlaskDamage(player, now);
        var bar = ItemsOf(player).Items(PlayerInventories.Flask);
        bool Gone(UtilityFlasks.Active effect) => UtilityFlasks.LeftItsCell(effect, bar);
        if (!dead) AdvanceUtilityFlaskChargesLocked(player, now, Gone);
        var ended = UtilityFlasks.EndDue(player, now, dead, Gone,
            () => RecomputePoolsAndSpeed(player), out _);
        if (ended.Count == 0) return;
        var state = Automation(player);
        foreach (var effect in ended)
        {
            bool triggered = !dead && !Gone(effect);
            EndUtilityFlaskLocked(player, state, effect, now,
                dead ? "death" : triggered ? "its time is up" : "the flask left its cell", triggered);
        }
        state.StatRevision++;
    }

    sealed class UtilityFlaskChargeClock
    {
        public double Through = double.NegativeInfinity;
    }
    static readonly RuleTable<Player, UtilityFlaskChargeClock> UtilityFlaskChargeClocks = new();

    static void AdvanceUtilityFlaskChargesLocked(Player player, double now, Func<UtilityFlasks.Active, bool> gone)
    {
        const uint stat = Stat.LocalFlaskGainEnduranceChargePerSecondDuringFlaskEffect;
        if (player.UtilityFlaskEffects.FirstWithLocalStat(stat, effect => !gone(effect)) is not { } active) return;
        var clock = UtilityFlaskChargeClocks.GetOrCreateValue(player);
        // Concurrent copies share the charge rate. The first active source's already-issued seconds cannot be
        // spent again by another copy when the older activation expires.
        double last = Math.Max(active.PeriodicThrough, clock.Through);
        int elapsed = (int)Math.Clamp(Math.Floor(Math.Min(now, Math.BitDecrement(active.Ends)) - last), 0, int.MaxValue);
        if (elapsed == 0) return;
        active.PeriodicThrough = clock.Through = last + elapsed;
        int count = ActorScale.Saturate((long)elapsed * active.LocalStats!.GetValueOrDefault(stat));
        ChargesOf(player).Enqueue(new ChargeEvent.Grant(ChargeType.Endurance, count, "utility flask periodic effect"));
    }

    static void EndOnWardBreakLocked(Player player, double now)
    {
        if (!player.UtilityFlaskEffects.Any) return;
        AccrueUtilityFlaskDamage(player, now);
        bool Breaks(UtilityFlasks.Active effect) =>
            effect.LocalStats?.GetValueOrDefault(Stat.LocalFlaskRemoveEffectWhenWardBreaks) > 0;
        var ended = UtilityFlasks.EndDue(player, now, dead: false, Breaks,
            () => RecomputePoolsAndSpeed(player), out _);
        if (ended.Count == 0) return;
        var state = Automation(player);
        foreach (var effect in ended) EndUtilityFlaskLocked(player, state, effect, now,
            effect.Ends > now ? "Ward broke" : "its time is up", triggered: true);
        state.StatRevision++;
    }

    static void EndUtilityFlaskLocked(Player player, FlaskAutomationState state, UtilityFlasks.Active effect,
        double now, string why, bool triggered)
    {
        QueueUtilityFlaskEnd(player, state, effect, why);
        if (!triggered) return;
        state.Enqueue(new(FlaskEnchantments.Trigger.End, effect.ItemId, effect.Slot, effect.Activation));
        int seconds = effect.LocalStats?.GetValueOrDefault(Stat.LocalFlaskDebilitateNearbyEnemiesForXSecondsWhenFlaskEffectEnds) ?? 0;
        if (seconds <= 0) return;
        const int nearby = 30; // The native description of Vorana's Preparation specifies three metres.
        var at = player.Walk.Position(now);
        foreach (var (id, monster) in player.Area.MonstersNear(at, nearby))
            if (monster.Hostile && !monster.Dead && monster.Life > 0 && Dist(at, monster.Walk.Position(now)) <= nearby)
                ApplyDebilitateLocked(player, id, monster, now, seconds);
    }

    static void QueueUtilityFlaskEnd(Player player, FlaskAutomationState state, UtilityFlasks.Active effect, string why)
    {
        UtilityFlasks.EndBuffs(effect, player);
        Note($"player buff: {effect.Name} (flask {effect.Slot}) effect ends ({why})");
    }

    public static async Task PublishFlaskChanges(Player player)
    {
        var state = Automation(player);
        if (!CurrentGameWork || player.Closed || player.LeavingArea
            || state.Packets.Count == 0 && state.StatRevision == state.PresentedStatRevision) return;
        await state.PublishGate.WaitAsync();
        try
        {
            while (true)
            {
                FlaskOutput output;
                if (!CurrentGameWork || player.Closed || player.LeavingArea || player.RageRetired || state.Packets.Count == 0) break;
                output = state.Packets[0];
                bool sent = false;
                byte[][]? Project()
                {
                    if (!CurrentGameWork || player.Closed || player.LeavingArea || player.RageRetired || state.Packets.Count == 0
                        || !ReferenceEquals(state.Packets[0], output)) return null;
                    // Pool/reservation output samples current values, never a pre-reload instant snapshot.
                    byte[][] packet = output.Pool switch
                    {
                        FlaskOutputPool.Life => PoolsResent(player),
                        FlaskOutputPool.Mana => WithOwnAuraRecords(player, () => PoolsResent(player)),
                        _ => [output.Packet],
                    };
                    if (output.UtilityStart is { } effect)
                    {
                        var prefix = PlayerStatsOffFrames(player);
                        double now = AreaTime(player);
                        bool live = player.PlayerLife > 0 && effect.Ends > now
                            && player.UtilityFlaskEffects.IsLatest(effect.ItemId, effect.Activation)
                            && ReferenceEquals(player.UtilityFlaskEffects.Of(effect.ItemId), effect);
                        if (live) UtilityFlasks.ShowBuffs(effect with { Started = now }, player, FlaskEffectVisual(player, effect.Slot));
                        packet = [prefix];
                    }
                    state.Packets.RemoveAt(0); sent = true; return packet;
                }
                uint? worldId = output.WorldId ?? (output.Pool != FlaskOutputPool.None || output.UtilityStart is not null ? player.Id : null);
                if (output.CreatesObject)
                    await SendWorldCreate(player, worldId!.Value, () => Project()?.Single(), output.Log);
                else if (output.RemovesObject)
                    await SendWorldRemoval(player, worldId!.Value, output.Log, atGate: () => Project()?.Single());
                else if (worldId is uint id)
                    await SendWorldRecords(player, id, Project, output.Log);
                else await Send(player, [], output.Log, atGate: () =>
                    Project() is { } records ? [.. records.SelectMany(p => p)] : null);
                if (sent && output.Pool == FlaskOutputPool.Mana && CurrentGameWork)
                    await SendPlayerEnergyShield(player);
            }
        }
        finally { state.PublishGate.Release(); }
        long revision;
        if (!CurrentGameWork || player.Closed || player.LeavingArea || state.StatRevision == state.PresentedStatRevision) return;
        revision = state.StatRevision;
        await SendCharacterStats(player, PlayerStatChange.Flasks);
        if (CurrentGameWork && !player.Closed && !player.LeavingArea) state.PresentedStatRevision = Math.Max(state.PresentedStatRevision, revision);
    }
}
