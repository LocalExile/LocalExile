using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    // Tincture weapon-scoped stats mapped to existing attack/weapon-only stat consumers so active tinctures apply to
    // eligible weapon attacks without leaking into spells or unarmed attacks. Ailment chances/effects/durations, DoT
    // multipliers, stun, crit multiplier, culling, and low-life enemy damage are read directly on weapon-only paths.
    static readonly (uint Source, uint Target)[] TinctureWeaponStatMap =
    [
        (Stat.Of("critical_strike_chance_+%_with_tinctured_weapons"), Stat.Of("attack_critical_strike_chance_+%")),
        (Stat.Of("elemental_damage_+%_with_tinctured_weapons"), Stat.Of("elemental_damage_with_attack_skills_+%")),
        (Stat.Of("attack_speed_+%_with_tinctured_weapons"), Stat.Of("attack_speed_+%")),
        (Stat.Of("penetrate_elemental_resistances_%_with_tinctured_weapons"), Stat.Of("reduce_enemy_elemental_resistance_with_weapons_%")),
        (Stat.Of("chance_to_bleed_on_hit_%_with_tinctured_weapons"), Stat.Of("bleed_on_hit_with_attacks_%")),
        (Stat.Of("bleeding_damage_+%_with_tinctured_weapons"), Stat.Of("bleeding_damage_+%")),
        (Stat.Of("chance_to_poison_on_hit_%_with_tinctured_weapons"), Stat.Of("chance_to_poison_on_hit_with_attacks_%")),
        (Stat.Of("chance_to_blind_on_hit_%_with_tinctured_weapons"), Stat.Of("attacks_chance_to_blind_on_hit_%")),
        (Stat.Of("blind_effect_+%_with_tinctured_weapons"), Stat.Of("blind_effect_+%")),
        (Stat.Of("gain_x_rage_on_hit_with_tinctured_weapons"), Stat.Of("gain_x_rage_on_attack_hit")),
        (Stat.Of("life_gained_on_hitting_enemies_with_tinctured_weapons"), Stat.Of("base_life_gain_per_target")),
        (Stat.Of("mana_gained_on_hitting_enemies_with_tinctured_weapons"), Stat.Of("mana_gain_per_target")),
    ];

    static byte[] TinctureBuffPacket(uint playerId, ushort buffInstance, ushort buffRow, byte slot, double? seconds) =>
        InstanceEntities.AddBuff(playerId, buffInstance, buffRow, playerId, sourceSkill: slot,
            u68: seconds is double s ? BitConverter.SingleToUInt32Bits((float)s) : null);

    static int TinctureStat(IReadOnlyDictionary<uint, int> stats, string name) =>
        Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    public sealed record TinctureKillBonus(Player Owner, int Life, int Mana, int FlaskChargesPct);
    static readonly RuleTable<SpawnedMonster, TinctureKillBonus> TinctureWeaponKills = new();

    public static TinctureKillBonus? TinctureKillBonusFor(Player player, IReadOnlyDictionary<uint, int> playerStats, AttackHand hand)
    {
        if (!player.TinctureEffects.AnyEffective) return null;
        int Get(string stat) => TinctureStat(playerStats, stat);
        if (!(hand != AttackHand.Off && Get("tincture_applied_to_main_hand") != 0
            || hand != AttackHand.Main && Get("tincture_applied_to_off_hand") != 0)) return null;
        int life = Get("life_gained_on_killing_enemies_with_tinctured_weapons");
        int mana = Get("base_mana_gained_on_tinctured_weapon_kill");
        int charges = Get("flask_charges_granted_from_tinctured_weapon_kills_+%");
        return life == 0 && mana == 0 && charges == 0 ? null : new(player, life, mana, charges);
    }

    // The killing blow and its rewards can run on different turns. Only the killer can claim its bonus.
    static void RecordTinctureKill(SpawnedMonster monster, TinctureKillBonus? bonus)
    {
        if (bonus is not null) TinctureWeaponKills.AddOrUpdate(monster, bonus);
    }

    static TinctureKillBonus? TakeTinctureKill(Player player, SpawnedMonster monster)
    {
        if (!TinctureWeaponKills.TryGetValue(monster, out var bonus) || bonus.Owner != player) return null;
        TinctureWeaponKills.Remove(monster);
        return bonus;
    }

    static double TinctureLingerSeconds(IReadOnlyDictionary<uint, int> character, int stacks)
    {
        int msPerStack = TinctureStat(character, "tincture_effects_linger_for_ms_per_toxicity_up_to_6_seconds");
        return msPerStack > 0 && stacks > 0 ? Math.Min(6.0, stacks * msPerStack / 1000.0) : 0;
    }

    // C→S 0x70 on a tincture belt slot: toggles the tincture on or off, enforcing level requirements, shared cooldown,
    // resource gate, active tincture limit, wire state (Tincture component byte via 0xbf), buffs (0x1c1/0x1c2), and stats.
    public static async Task UseTincture(Player player,
        uint slot, InventoryItem flask, ItemCatalog.BaseType type, Tinctures.Definition definition)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.RageRetired || player.Paused || player.PlayerLife == 0) return;
        string remote = player.Connection.Remote;
        await player.FlaskBarGate.WaitAsync();
        bool changed = false;
        try
        {
            if (!CurrentGameWork || HardcoreProgressBlocked(player) || player.Closed || player.LeavingArea || player.RageRetired
                || player.Paused || player.PlayerLife == 0) return;
            var items = ItemsOf(player);
            var bar = items.Items(PlayerInventories.Flask);
            if (bar.FirstOrDefault(i => i.Id == flask.Id && i.X == slot) is not { } current) return;
            double now = AreaTime(player);
            var catalog = ItemsFor(player);
            var state = Automation(player);
            var effects = player.TinctureEffects;

            // Pressing an already-active tincture manually deactivates it and triggers the shared cooldown.
            if (effects.IsActive(current.Id))
            {
                var character = CharacterPlayerStats(player);
                DeactivateTinctureLocked(player, current.Id, now, character, "manually deactivated", inBar: current,
                    allowLinger: true);
                RecomputePoolsAndSpeed(player);
                state.StatRevision++;
                changed = true;
                Console.WriteLine($"[gateway] {remote} <= 0x0070 tincture {slot} ({type.Name}): deactivated, cooldown {effects.CooldownRemaining(now):0.##} s");
                return;
            }

            // Activation checks: level requirement, cooldown, resource floor, and active tincture cap.
            int reqLevel = ItemModStats.LevelRequirement(current.Item, type.DropLevel, catalog.Mods);
            if (player.Pools.Level < reqLevel)
            {
                Console.WriteLine($"[gateway] {remote} <= 0x0070 tincture {slot} ({type.Name}): refused, requires level {reqLevel} (character is {player.Pools.Level})");
                return;
            }
            if (effects.OnCooldown(now))
            {
                Console.WriteLine($"[gateway] {remote} <= 0x0070 tincture {slot} ({type.Name}): refused, on cooldown ({effects.CooldownRemaining(now):0.##} s left)");
                return;
            }

            var playerStats = CharacterPlayerStats(player);
            int Get(string name) => TinctureStat(playerStats, name);
            bool lifeBurn = Get("keystone_tinctures_drain_life") != 0;
            int recoverManaPct = Get("recover_%_maximum_mana_on_activating_tincture");
            if (!lifeBurn && player.PlayerMana == 0 && recoverManaPct <= 0)
            {
                Console.WriteLine($"[gateway] {remote} <= 0x0070 tincture {slot} ({type.Name}): refused, 0 mana");
                return;
            }
            if (lifeBurn && player.PlayerLife <= 1)
            {
                Console.WriteLine($"[gateway] {remote} <= 0x0070 tincture {slot} ({type.Name}): refused, 1 life");
                return;
            }
            int maxActive = 1 + Math.Max(0, Get("can_apply_additional_tincture"));
            if (effects.ActiveCount >= maxActive)
            {
                Console.WriteLine($"[gateway] {remote} <= 0x0070 tincture {slot} ({type.Name}): refused, already at maximum active tinctures ({maxActive})");
                return;
            }

            if (player.EntryProtection.End(now, player.Walk.Position(now), action: true) is ushort graceBuff)
                state.Packets.Add(new(InstanceEntities.RemoveBuff(player.Id, graceBuff),
                    "Grace Period ends: tincture activated", WorldId: player.Id));

            var local = ItemModStats.Local(current.Item, catalog.Mods);
            int emptySlots = Math.Max(0, 5 - bar.Count);
            bool highToxicity = effects.BurnStacks >= Tinctures.HighToxicityThreshold;
            bool usedLifeFlaskRecently = player.RecentEvents.Happened(StatConditions.UsedLifeFlask, now);
            int GetId(uint key) => playerStats.GetValueOrDefault(key);
            double multiplier = Tinctures.EffectMultiplier(current.Item, local, GetId, emptySlots,
                highToxicity, usedLifeFlaskRecently, usingFlask: AnyFlaskEffect(player));
            var scaled = Tinctures.ScaledMods(current.Item, catalog.Mods, multiplier);
            double interval = Tinctures.DebuffInterval(definition, local, GetId);
            double cooldown = Tinctures.CooldownSeconds(definition, local, GetId);
            var buffs = catalog.TinctureBuffs;
            ushort buffRow = Tinctures.ResolveBuffRow(current.Item, catalog.Mods, definition, buffs);
            ushort buffInstance = buffRow != 0 ? player.NextBuff++ : (ushort)0;

            if (recoverManaPct > 0)
            {
                uint manaBefore = player.PlayerMana;
                double manaCarry = 0;
                player.PlayerMana = RecoveryPool.Advance(manaBefore, (uint)UnreservedMana(player),
                    Math.Floor(player.Pools.Mana * recoverManaPct / 100.0), ref manaCarry);
                if (player.PlayerMana != manaBefore)
                    state.Packets.Add(new([], $"0x01b1 player mana {player.PlayerMana} (tincture activation recovery)",
                        Pool: FlaskOutputPool.Mana));
            }

            effects.LifeBurn = lifeBurn;
            var active = new Tinctures.Active(current.Id, (byte)slot, type.Name, current.Item, definition,
                buffInstance, buffRow, now, interval, cooldown, local, multiplier, scaled);
            if (effects.HasLingering) EndLingeringTincturesLocked(player, l => l.ItemId == current.Id, "reactivated");
            effects.Activate(active);

            // Emit wire-active Tincture component byte (1) in the flask bar without persisting TinctureActive in saves.
            var wireActive = current with { Item = current.Item with { TinctureActive = true } };
            state.Packets.Add(new(InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Flask),
                items.NextRevision(PlayerInventories.Flask), [current.Id], [wireActive]),
                $"0x00bf tincture {slot} ({type.Name}): active"));
            state.Packets.Add(new(InstanceItems.ItemUsed(current.Item.TypeHash,
                PlayerInventories.InventoryId(PlayerInventories.Flask), current.Id),
                $"0x0263 tincture {slot} ({type.Name}) activated"));
            if (effects.ParentBuff == 0 && buffs.Parent != 0)
            {
                effects.ParentBuff = player.NextBuff++;
                state.Packets.Add(new(InstanceEntities.AddBuff(player.Id, effects.ParentBuff, buffs.Parent, player.Id),
                    $"0x01c1 player buff: tincture parent ({buffs.Parent})", WorldId: player.Id));
            }
            if (buffInstance != 0)
                state.Packets.Add(new(TinctureBuffPacket(player.Id, buffInstance, buffRow, (byte)slot, seconds: null),
                    $"0x01c1 player buff: {type.Name} (tincture {slot})", WorldId: player.Id));

            RecomputePoolsAndSpeed(player);
            state.StatRevision++;
            changed = true;
            Console.WriteLine($"[gateway] {remote} <= 0x0070 tincture {slot} ({type.Name}): active, burn every {interval:0.##} s, cooldown {cooldown:0.##} s");
        }
        finally
        {
            player.FlaskBarGate.Release();
            if (changed) await PublishFlaskChanges(player);
        }
    }

    // Called after PickUpItem / PlaceItem / HeldOntoItem / currency modification on the flask bar: deactivates any
    // active tincture that was removed, swapped, moved to another slot, or modified.
    public static async Task ReconcileTincturesAfterFlaskBarChange(Player player)
    {
        if (!player.TinctureEffects.AnyEffective) return;
        await player.FlaskBarGate.WaitAsync();
        bool changed = false;
        try
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
            var bar = ItemsOf(player).Items(PlayerInventories.Flask);
            double now = AreaTime(player);
            var snapshot = player.TinctureEffects.ActiveSnapshot();
            IReadOnlyDictionary<uint, int>? character = null;
            foreach (var active in snapshot)
            {
                var present = bar.FirstOrDefault(i => i.Id == active.ItemId);
                if (present is not null && present.X == active.Slot && ReferenceEquals(present.Item, active.Item)) continue;
                character ??= CharacterPlayerStats(player);
                DeactivateTinctureLocked(player, active.ItemId, now, character,
                    present is null ? "unequipped" : "moved or modified", inBar: present, allowLinger: false);
                changed = true;
            }
            if (player.TinctureEffects.HasLingering)
            {
                changed |= EndLingeringTincturesLocked(player, l => !bar.Any(i =>
                    i.Id == l.ItemId && i.X == l.Slot && ReferenceEquals(i.Item, l.Item)), "left flask bar");
                if (!player.TinctureEffects.AnyEffective) ClearTinctureBurnAndParentBuffsLocked(player, "left flask bar");
            }
            if (changed)
            {
                RecomputePoolsAndSpeed(player);
                Automation(player).StatRevision++;
            }
        }
        finally
        {
            player.FlaskBarGate.Release();
        }
        if (changed) await PublishFlaskChanges(player);
    }

    // When inBar is non-null, caller must hold FlaskBarGate so 0xbf revisions stay ordered;
    // when called from RunResourceRecovery (which does not hold FlaskBarGate), pass inBar: null and queue the item id for SendTinctureBarInactive.
    static void DeactivateTinctureLocked(Player player, uint itemId, double now, IReadOnlyDictionary<uint, int> character,
        string why, InventoryItem? inBar, bool allowLinger)
    {
        var effects = player.TinctureEffects;
        var state = Automation(player);
        int stacksBefore = effects.BurnStacks;
        double lingerSeconds = allowLinger ? TinctureLingerSeconds(character, stacksBefore) : 0;
        ushort lingerBuff = lingerSeconds > 0 ? player.NextBuff++ : (ushort)0;
        var removed = effects.Deactivate(itemId, now, out bool allInactive, lingerSeconds, lingerBuff);
        if (removed is null) return;

        if (removed.Buff != 0)
            state.Packets.Add(new(InstanceEntities.RemoveBuff(player.Id, removed.Buff),
                $"0x01c2 player buff: {removed.Name} (tincture {removed.Slot}) ends ({why})", WorldId: player.Id));
        if (lingerSeconds > 0 && lingerBuff != 0 && removed.BuffRow != 0)
            state.Packets.Add(new(TinctureBuffPacket(player.Id, lingerBuff, removed.BuffRow, removed.Slot, lingerSeconds),
                $"0x01c1 player buff: {removed.Name} lingers for {lingerSeconds:0.##} s", WorldId: player.Id));
        if (inBar is not null)
        {
            var items = ItemsOf(player);
            var wireInactive = inBar with { Item = inBar.Item with { TinctureActive = false } };
            state.Packets.Add(new(InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Flask),
                items.NextRevision(PlayerInventories.Flask), [inBar.Id], [wireInactive]),
                $"0x00bf tincture {inBar.X} ({removed.Name}): inactive ({why})"));
        }
        if (allInactive) ClearTinctureBurnAndParentBuffsLocked(player, why);
    }

    static void DeactivateAllTincturesLocked(Player player, double now, IReadOnlyDictionary<uint, int> character,
        string why, bool allowLinger, List<uint>? deactivatedIds)
    {
        var effects = player.TinctureEffects;
        if (!effects.AnyActive) return;
        var state = Automation(player);
        int stacksBefore = effects.BurnStacks;
        double lingerSeconds = allowLinger ? TinctureLingerSeconds(character, stacksBefore) : 0;
        var (removed, lingering) = effects.DeactivateAll(now, lingerSeconds,
            a => lingerSeconds > 0 && a.BuffRow != 0 ? player.NextBuff++ : (ushort)0);
        if (removed.Count == 0) return;

        foreach (var a in removed)
        {
            if (a.Buff != 0)
                state.Packets.Add(new(InstanceEntities.RemoveBuff(player.Id, a.Buff),
                    $"0x01c2 player buff: {a.Name} (tincture {a.Slot}) ends ({why})", WorldId: player.Id));
            deactivatedIds?.Add(a.ItemId);
        }
        foreach (var l in lingering)
            if (l.Buff != 0 && l.BuffRow != 0)
                state.Packets.Add(new(TinctureBuffPacket(player.Id, l.Buff, l.BuffRow, l.Slot, lingerSeconds),
                    $"0x01c1 player buff: {l.Name} lingers for {lingerSeconds:0.##} s", WorldId: player.Id));
        ClearTinctureBurnAndParentBuffsLocked(player, why);
    }

    public static void DeactivateAllTincturesOnDeathLocked(Player player, double now,
        IReadOnlyDictionary<uint, int> recoveryStats, ref List<uint>? deactivatedIds)
    {
        var effects = player.TinctureEffects;
        if (!effects.AnyEffective) return;
        var state = Automation(player);
        if (effects.AnyActive)
        {
            deactivatedIds ??= [];
            DeactivateAllTincturesLocked(player, now, recoveryStats, "death", allowLinger: false, deactivatedIds);
        }
        EndLingeringTincturesLocked(player, static _ => true, "death");
        ClearTinctureBurnAndParentBuffsLocked(player, "death");
        RecomputePoolsAndSpeed(player);
        state.StatRevision++;
    }

    static bool EndLingeringTincturesLocked(Player player, Func<Tinctures.Lingering, bool> endedWhen, string reason)
    {
        var ended = player.TinctureEffects.EndLingering(endedWhen);
        foreach (var l in ended)
            if (l.Buff != 0)
                Automation(player).Packets.Add(new(InstanceEntities.RemoveBuff(player.Id, l.Buff),
                    $"0x01c2 player buff: lingering {l.Name} ends ({reason})", WorldId: player.Id));
        return ended.Count > 0;
    }

    static void ClearTinctureBurnAndParentBuffsLocked(Player player, string why)
    {
        var effects = player.TinctureEffects;
        var state = Automation(player);
        if (effects.BurnBuff != 0)
        {
            state.Packets.Add(new(InstanceEntities.RemoveBuff(player.Id, effects.BurnBuff),
                $"0x01c2 player buff: tincture burn ends ({why})", WorldId: player.Id));
            effects.BurnBuff = 0;
            effects.PresentedBurnStacks = 0;
        }
        if (!effects.AnyEffective && effects.ParentBuff != 0)
        {
            state.Packets.Add(new(InstanceEntities.RemoveBuff(player.Id, effects.ParentBuff),
                $"0x01c2 player buff: tincture parent ends ({why})", WorldId: player.Id));
            effects.ParentBuff = 0;
        }
    }

    static void SyncTinctureBurnBuffLocked(Player player, bool lifeBurn)
    {
        var effects = player.TinctureEffects;
        var state = Automation(player);
        var buffs = ItemsFor(player).TinctureBuffs;
        ushort burnRow = lifeBurn ? buffs.LifeBurn : buffs.ManaBurn;
        int stacks = effects.BurnStacks;
        if (stacks <= 0 || burnRow == 0)
        {
            if (effects.BurnBuff != 0)
            {
                state.Packets.Add(new(InstanceEntities.RemoveBuff(player.Id, effects.BurnBuff),
                    "0x01c2 player buff: tincture burn cleared", WorldId: player.Id));
                effects.BurnBuff = 0;
                effects.PresentedBurnStacks = 0;
            }
            return;
        }
        if (effects.BurnBuff != 0 && effects.PresentedBurnStacks == stacks && effects.PresentedLifeBurn == lifeBurn)
            return;
        if (effects.BurnBuff != 0)
            state.Packets.Add(new(InstanceEntities.RemoveBuff(player.Id, effects.BurnBuff), null, WorldId: player.Id));
        effects.BurnBuff = player.NextBuff++;
        effects.PresentedBurnStacks = stacks;
        effects.PresentedLifeBurn = lifeBurn;
        state.Packets.Add(new(InstanceEntities.AddBuff(player.Id, effects.BurnBuff, burnRow, player.Id,
            stacks: (ushort)Math.Min(stacks, ushort.MaxValue)),
            $"0x01c1 player buff: {(lifeBurn ? "Weeping Wounds" : "Mana Burn")} ({stacks})", WorldId: player.Id));
    }

    // Advances active/lingering tinctures inside RunResourceRecovery. Returns per-step Mana/Life
    // burn drain and any deactivated item ids whose 0xbf inactive update must be sent after.
    public static void AdvanceTincturesLocked(Player player, double now, double seconds,
        IReadOnlyDictionary<uint, int> recoveryStats, out double manaDrain, out double lifeDrain,
        ref List<uint>? deactivatedIds)
    {
        manaDrain = 0;
        lifeDrain = 0;
        var effects = player.TinctureEffects;
        if (effects.DrainPendingBarInactive() is { Count: > 0 } pending)
        {
            deactivatedIds ??= [];
            deactivatedIds.AddRange(pending);
        }
        if (!effects.AnyEffective) return;
        var state = Automation(player);

        if (player.PlayerLife == 0)
        {
            DeactivateAllTincturesOnDeathLocked(player, now, recoveryStats, ref deactivatedIds);
            return;
        }

        if (player.LeavingArea) return;

        bool statsChanged = effects.HasLingering && EndLingeringTincturesLocked(player, l => l.Ends <= now, "expired");
        if (!effects.AnyEffective && effects.ParentBuff != 0)
            ClearTinctureBurnAndParentBuffsLocked(player, "linger expired");

        // 2. Verify active tinctures are still in their flask-bar cells.
        if (effects.AnyActive)
        {
            var bar = ItemsOf(player).Items(PlayerInventories.Flask);
            foreach (var active in effects.ActiveSnapshot())
            {
                if (bar.Any(i => i.Id == active.ItemId && i.X == active.Slot && ReferenceEquals(i.Item, active.Item))) continue;
                DeactivateTinctureLocked(player, active.ItemId, now, recoveryStats, "left flask bar", inBar: null, allowLinger: false);
                statsChanged = true;
            }
        }

        // 3. Accumulate Mana Burn / Weeping Wounds stacks on each active tincture's interval.
        if (effects.AnyActive)
        {
            bool lifeBurn = TinctureStat(recoveryStats, "keystone_tinctures_drain_life") != 0;
            effects.LifeBurn = lifeBurn;
            int avoidChance = Math.Clamp(TinctureStat(recoveryStats, "chance_to_not_gain_tincture_toxicity_%"), 0, 100);
            int deactivateAt = TinctureStat(recoveryStats, "tinctures_deactivate_on_reaching_X_toxicity");
            int stacksBefore = effects.BurnStacks;
            bool thresholdDeactivated = false;

            foreach (var active in effects.ActiveSnapshot())
            {
                if (double.IsInfinity(active.NextBurnAt)) continue;
                int steps = 0;
                while (now >= active.NextBurnAt && steps++ < 100)
                {
                    active.NextBurnAt += active.DebuffInterval;
                    if (avoidChance > 0 && AreaRng(player).Next(100) < avoidChance) continue;
                    int next = effects.AddBurnStacks(1);
                    if (deactivateAt > 0 && next >= deactivateAt)
                    {
                        deactivatedIds ??= [];
                        DeactivateAllTincturesLocked(player, now, recoveryStats,
                            $"reached {deactivateAt} Mana Burn", allowLinger: true, deactivatedIds);
                        thresholdDeactivated = true;
                        statsChanged = true;
                        break;
                    }
                }
                if (thresholdDeactivated) break;
            }

            if (effects.AnyActive)
            {
                // Re-scale active tinctures live when conditional effect modifiers change (Legacy of the Wilds high
                // toxicity, Runegraft of the Imbued not using flask, Colloidal Mixture life flask recently, empty slots).
                var catalog = ItemsFor(player);
                int emptySlots = Math.Max(0, 5 - ItemsOf(player).Items(PlayerInventories.Flask).Count);
                bool highToxicity = effects.BurnStacks >= Tinctures.HighToxicityThreshold;
                bool usedLifeFlaskRecently = player.RecentEvents.Happened(StatConditions.UsedLifeFlask, now);
                bool usingFlask = AnyFlaskEffect(player);
                int RecStat(uint key) => recoveryStats.GetValueOrDefault(key);
                foreach (var active in effects.ActiveSnapshot())
                {
                    double mult = Tinctures.EffectMultiplier(active.Item, active.Local, RecStat, emptySlots,
                        highToxicity, usedLifeFlaskRecently, usingFlask);
                    if (Math.Abs(mult - active.EffectMultiplier) > 1e-6)
                    {
                        effects.UpdateScaling(active.ItemId, mult, Tinctures.ScaledMods(active.Item, catalog.Mods, mult));
                        statsChanged = true;
                    }
                }
                if (effects.BurnStacks != stacksBefore || effects.PresentedLifeBurn != lifeBurn)
                {
                    SyncTinctureBurnBuffLocked(player, lifeBurn);
                    statsChanged = true;
                }
                int ignored = Math.Max(0, TinctureStat(recoveryStats, "first_X_stacks_of_tincture_toxicity_have_no_effect"));
                double drain = Tinctures.BurnPerSecond(effects.BurnStacks,
                    lifeBurn ? player.PlayerMaxLife : player.Pools.Mana, ignored) * seconds;
                if (lifeBurn) lifeDrain = drain;
                else manaDrain = drain;
            }
        }

        if (statsChanged)
        {
            RecomputePoolsAndSpeed(player);
            state.StatRevision++;
        }
    }

    // Post-recovery check: automatically deactivates active tinctures when Mana reaches 0
    // (or when Life reaches 1 under Bloodsoaked Blade).
    public static void CheckTinctureResourceDeactivationLocked(Player player, double now,
        IReadOnlyDictionary<uint, int> recoveryStats, ref List<uint>? deactivatedIds)
    {
        var effects = player.TinctureEffects;
        if (!effects.AnyActive) return;
        bool depleted = effects.LifeBurn ? player.PlayerLife <= 1 : player.PlayerMana == 0;
        if (!depleted) return;
        deactivatedIds ??= [];
        DeactivateAllTincturesLocked(player, now, recoveryStats,
            effects.LifeBurn ? "life reached 1" : "mana reached 0", allowLinger: true, deactivatedIds);
        RecomputePoolsAndSpeed(player);
        Automation(player).StatRevision++;
    }

    // Sends 0xbf inactive updates for tinctures deactivated during RunResourceRecovery.
    public static async Task SendTinctureBarInactive(Player player, List<uint>? deactivatedIds)
    {
        if (deactivatedIds is not { Count: > 0 }) return;
        await ChangeFlaskBar(player, items =>
        {
            var bar = items.Items(PlayerInventories.Flask);
            List<InventoryItem> changed = [];
            foreach (uint id in deactivatedIds)
                if (!player.TinctureEffects.IsActive(id) && bar.FirstOrDefault(i => i.Id == id) is { } current)
                    changed.Add(current with { Item = current.Item with { TinctureActive = false } });
            return changed.Count == 0 ? null : ([.. changed.Select(c => c.Id)], changed);
        }, changed => $"0x00bf tincture(s) inactive: {string.Join(", ", changed.Select(c => c.X))}", retainOutput: true);
    }

    // Applies active and lingering tincture stats to CharacterStatSources. Zero-allocation fast path when inactive.
    public static Dictionary<uint, int> WithTinctures(Player player, Dictionary<uint, int> sources)
    {
        if (!player.TinctureEffects.AnyEffective) return sources;
        var (scaledStats, stacks, active, lingering) = player.TinctureEffects.EffectiveSnapshot();
        if (!active && !lingering) return sources;

        void Set(string name, int value)
        {
            if (Stats.Key(name) is uint key) sources[key] = value;
        }
        void Add(string name, int value)
        {
            if (value != 0 && Stats.Key(name) is uint key)
                sources[key] = ActorScale.Saturate((long)sources.GetValueOrDefault(key) + value);
        }

        void AddKey(uint key, int value)
        {
            if (value != 0)
                sources[key] = ActorScale.Saturate((long)sources.GetValueOrDefault(key) + value);
        }

        int Get(string name) => Stats.Key(name) is uint key ? sources.GetValueOrDefault(key) : 0;

        var weapons = WeaponProfileFor(player, activeSet: true);
        bool allowRanged = Get("tinctures_can_apply_to_ranged_weapons") != 0;
        bool mainEligible = !weapons.Unarmed && (weapons.Melee || allowRanged && (weapons.Bow || weapons.Wand));
        bool offEligible = ActiveOffHand(player) is { Type: { } offType }
            && (WeaponIdentity.OneHandMelee(offType.Class) || allowRanged && offType.Class == "Wand");
        bool weaponApplied = mainEligible || offEligible;

        if (active) Set("using_tincture", 1);
        if (lingering) Set("has_lingering_tincture", 1);
        if (stacks > 0)
        {
            Set("current_tincture_toxicity", stacks);
            // BuffTemplates TinctureManaBurn / TinctureLifeBurn carry 60 (%/min per stack = 1%/s) so the client's
            // own mana/life orb tick predicts Mana Burn / Weeping Wounds smoothly between 0x1b1 updates.
            Set(player.TinctureEffects.LifeBurn
                ? "non_lethal_life_loss_%_per_minute_per_tincture_toxicity"
                : "mana_degeneration_per_minute_%_per_tincture_toxicity", 60);
        }
        if (stacks >= Tinctures.HighToxicityThreshold) Set("is_high_toxicity", 1);
        if (weaponApplied) Set("tincture_applied", 1);
        if (mainEligible) Set("tincture_applied_to_main_hand", 1);
        if (offEligible) Set("tincture_applied_to_off_hand", 1);

        // Passive/mastery bonuses gated on an active tincture or current Mana Burn stacks.
        if (active)
        {
            Add("evasion_rating_+%", Get("evasion_rating_+%_while_tincture_active"));
            Add("recover_%_maximum_mana_on_kill", Get("recover_%_maximum_mana_on_kill_while_tincture_active"));
            if (stacks > 0)
            {
                int chargesPerBurn = Get("flask_charges_gained_+%_per_tincture_toxicity");
                if (chargesPerBurn != 0) Add("charges_gained_+%", ActorScale.Saturate((long)chargesPerBurn * stacks));
            }
        }

        // Apply each active/lingering tincture's scaled modifiers.
        foreach (var (stat, value) in scaledStats)
        {
            AddKey(stat, value);
            if (stat == Stat.ItemRarityPlusPctPerToxicityUpTo100PctFromTincture && stacks > 0)
                AddKey(Stat.BaseItemFoundRarityPlusPct, Math.Min(100, (int)Math.Clamp((long)value * stacks, 0, 100)));
            else if (stat == Stat.FireDamageTakenPlusPerToxicityFromTincture && stacks > 0)
                AddKey(Stat.FireDamageTakenPlus, ActorScale.Saturate((long)value * stacks));
            else if (weaponApplied)
            {
                if (stat == Stat.PenetrateElementalResistancesPctPerToxicityWithTincturedWeapons && stacks > 0)
                    AddKey(Stat.ReduceEnemyElementalResistanceWithWeaponsPct, ActorScale.Saturate((long)value * stacks));
                else
                    foreach (var (source, target) in TinctureWeaponStatMap)
                        if (stat == source) { AddKey(target, value); break; }
            }
        }
        return sources;
    }

    // Sends 0xbf charge updates for flasks adjacent to active tinctures (Nature's Concoction).
    public static async Task SendTinctureAdjacentFlaskCharges(Player player, List<(byte Slot, int Charges)>? grants)
    {
        if (grants is not { Count: > 0 }) return;
        var catalog = ItemsFor(player);
        await ChangeFlaskBar(player, items =>
        {
            var totals = new Dictionary<byte, int>();
            foreach (var (slot, charges) in grants)
                totals[slot] = totals.GetValueOrDefault(slot) + charges;
            List<InventoryItem> changed = [];
            foreach (var flask in items.Items(PlayerInventories.Flask).ToArray())
            {
                if (flask.X > byte.MaxValue || !totals.TryGetValue((byte)flask.X, out int amount) || amount <= 0) continue;
                if (catalog.ByHash(flask.Item.TypeHash) is not { } type || catalog.FlaskChargesOf(type) is not { } use) continue;
                if (!CanGainFlaskCharges(player, flask)) continue;
                if (items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, i => Flasks.Gain(i, use, amount)) is { } done)
                    changed.Add(done);
            }
            return changed.Count == 0 ? null : ([.. changed.Select(c => c.Id)], changed);
        }, changed => $"0x00bf adjacent tincture flask charges: {string.Join(", ", changed.Select(c => $"{c.X}: {c.Item.Charges}"))}",
            retainOutput: true);
    }

    // Extra flask effect granted by Nature's Concoction / Tincture Mastery 4 to flasks adjacent to an active tincture
    // when the player has hit an enemy with a weapon recently. Zero-allocation fast path when no tincture is active.
    public static int AdjacentTinctureFlaskEffect(Player player, byte slot, Func<uint, int> character, double now)
    {
        if (!player.TinctureEffects.HasActiveAdjacentTo(slot)) return 0;
        int bonus = character(Stat.FlasksAdjacentToActiveTincturesHaveEffectPlusPctIfHitWithWeaponRecently);
        if (bonus == 0) return 0;
        return player.RecentEvents.Happened(StatConditions.HitEnemyWithWeapon, now) ? bonus : 0;
    }

    // Called on a landed player weapon attack hit (melee, area attack, or projectile attack):
    // handles The Battle Within (on-hit Mana Burn), Grasping Nightshade (Wither stacks), Wildfire Phloem (Ignite
    // refresh & Covered in Ash on full life), and Nature's Concoction (adjacent flask charges on weapon hit, 0.5 s ICD).
    public static void TinctureOnWeaponHitLocked(Player player, IReadOnlyDictionary<uint, int> playerStats,
        SpawnedMonster target, uint targetLifeBefore, double now, Random? random = null)
    {
        var effects = player.TinctureEffects;
        if (!effects.AnyEffective) return;
        if (TinctureStat(playerStats, "tincture_applied") == 0) return;
        int Get(string name) => TinctureStat(playerStats, name);
        random ??= AreaRng(player);

        // Grasping Nightshade & Wildfire Phloem apply while active or lingering on living hostile targets.
        if (target.Hostile && !target.Dead && target.Life > 0)
        {
            int witherStacks = Get("apply_X_wither_stacks_for_2_seconds_with_tinctured_weapons");
            if (witherStacks > 0)
                ApplyWitherStacksLocked(target, witherStacks, WitherDurationSeconds(2.0, k => playerStats.GetValueOrDefault(k)), k => playerStats.GetValueOrDefault(k), now);
            int refreshChance = Get("refresh_ignite_duration_on_tinctured_weapon_hit_%_chance");
            if (refreshChance > 0 && target.Ignites.IgnitedAt(now) && RandomRolls.Chance(refreshChance, 100, random))
                target.Ignites.Refresh(now);
            int ashSeconds = Get("tinctured_weapon_cover_in_ash_on_hit_for_X_seconds_vs_full_life_enemies");
            if (ashSeconds > 0 && targetLifeBefore == target.MaxLife)
                target.CoveredInAshUntil = Math.Max(target.CoveredInAshUntil, now + ashSeconds);
        }

        if (!effects.AnyActive) return;

        // Nature's Concoction: flasks adjacent to active tinctures gain charges on weapon hit (0.5 s cooldown).
        int adjCharges = Get("flasks_adjacent_to_active_tinctures_gain_X_charges_on_weapon_hit");
        if (adjCharges > 0 && now - effects.LastAdjacentChargeGrantAt >= 0.5)
        {
            effects.LastAdjacentChargeGrantAt = now;
            foreach (var active in effects.ActiveSnapshot())
            {
                if (active.Slot > 0) effects.QueuePendingAdjacentCharges((byte)(active.Slot - 1), adjCharges);
                if (active.Slot < 4) effects.QueuePendingAdjacentCharges((byte)(active.Slot + 1), adjCharges);
            }
        }

        // The Battle Within: gain Mana Burn stacks on weapon hit.
        int gain = Get("toxicity_stacks_gained_on_hit_with_tinctured_weapons");
        if (gain <= 0) return;
        int next = effects.AddBurnStacks(gain);
        int deactivateAt = Get("tinctures_deactivate_on_reaching_X_toxicity");
        if (deactivateAt > 0 && next >= deactivateAt)
        {
            List<uint> ids = [];
            DeactivateAllTincturesLocked(player, now, playerStats, $"reached {deactivateAt} Mana Burn on hit",
                allowLinger: true, deactivatedIds: ids);
            foreach (uint id in ids) effects.QueuePendingBarInactive(id);
        }
        else
            SyncTinctureBurnBuffLocked(player, effects.LifeBurn);
        RecomputePoolsAndSpeed(player);
        Automation(player).StatRevision++;
    }

    // Called on player kill: handles "X% chance to remove 1 stack of Mana Burn on Kill".
    public static void TinctureOnKillLocked(Player player, IReadOnlyDictionary<uint, int> playerStats)
    {
        if (!player.TinctureEffects.AnyActive || player.TinctureEffects.BurnStacks <= 0) return;
        int chance = Math.Clamp(TinctureStat(playerStats, "chance_to_remove_1_tincture_toxicity_on_kill_%"), 0, 100);
        if (chance <= 0 || AreaRng(player).Next(100) >= chance) return;
        player.TinctureEffects.RemoveBurnStacks(1);
        SyncTinctureBurnBuffLocked(player, player.TinctureEffects.LifeBurn);
        RecomputePoolsAndSpeed(player);
        Automation(player).StatRevision++;
    }
}
