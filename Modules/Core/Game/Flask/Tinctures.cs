using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    // Tincture weapon-scoped stats mapped to existing attack/weapon-only stat consumers so active tinctures apply to
    // eligible weapon attacks without leaking into spells or unarmed attacks. Ailment chances/effects/durations, DoT
    // multipliers, stun, crit multiplier, culling, and low-life enemy damage are read directly on weapon-only paths.
    static readonly (string Source, string Target)[] TinctureWeaponStatMap =
    [
        ("critical_strike_chance_+%_with_tinctured_weapons", "attack_critical_strike_chance_+%"),
        ("elemental_damage_+%_with_tinctured_weapons", "elemental_damage_with_attack_skills_+%"),
        ("attack_speed_+%_with_tinctured_weapons", "attack_speed_+%"),
        ("penetrate_elemental_resistances_%_with_tinctured_weapons", "reduce_enemy_elemental_resistance_with_weapons_%"),
        ("chance_to_bleed_on_hit_%_with_tinctured_weapons", "bleed_on_hit_with_attacks_%"),
        ("bleeding_damage_+%_with_tinctured_weapons", "bleeding_damage_+%"),
        ("chance_to_poison_on_hit_%_with_tinctured_weapons", "chance_to_poison_on_hit_with_attacks_%"),
        ("chance_to_blind_on_hit_%_with_tinctured_weapons", "attacks_chance_to_blind_on_hit_%"),
        ("blind_effect_+%_with_tinctured_weapons", "blind_effect_+%"),
        ("gain_x_rage_on_hit_with_tinctured_weapons", "gain_x_rage_on_attack_hit"),
        ("life_gained_on_hitting_enemies_with_tinctured_weapons", "base_life_gain_per_target"),
        ("mana_gained_on_hitting_enemies_with_tinctured_weapons", "mana_gain_per_target"),
        ("life_gained_on_killing_enemies_with_tinctured_weapons", "base_life_gained_on_enemy_death"),
        ("base_mana_gained_on_tinctured_weapon_kill", "base_mana_gained_on_enemy_death"),
        ("flask_charges_granted_from_tinctured_weapon_kills_+%", "charges_gained_+%"),
    ];

    static int TinctureStat(IReadOnlyDictionary<uint, int> stats, string name) =>
        Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    static double TinctureLingerSeconds(IReadOnlyDictionary<uint, int> character, int stacks)
    {
        int msPerStack = TinctureStat(character, "tincture_effects_linger_for_ms_per_toxicity_up_to_6_seconds");
        return msPerStack > 0 && stacks > 0 ? Math.Min(6.0, stacks * msPerStack / 1000.0) : 0;
    }

    // C→S 0x70 on a tincture belt slot: toggles the tincture on or off, enforcing level requirements, shared cooldown,
    // resource gate, active tincture limit, wire state (Tincture component byte via 0xbf), buffs (0x1c1/0x1c2), and stats.
    public static async Task UseTincture(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint slot, InventoryItem flask, ItemCatalog.BaseType type, Tinctures.Definition definition)
    {
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired || conn.Paused || conn.PlayerLife == 0) return;
        await conn.FlaskBarGate.WaitAsync();
        bool changed = false;
        try
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || HardcoreProgressBlocked(conn) || conn.Closed || conn.LeavingArea || conn.RageRetired
                    || conn.Paused || conn.PlayerLife == 0) return;
                var items = ItemsOf(conn);
                var bar = items.Items(PlayerInventories.Flask);
                if (bar.FirstOrDefault(i => i.Id == flask.Id && i.X == slot) is not { } current) return;
                double now = AreaTime(conn);
                var catalog = ItemsFor(conn);
                var state = Automation(conn);
                var effects = conn.TinctureEffects;

                // Pressing an already-active tincture manually deactivates it and triggers the shared cooldown.
                if (effects.IsActive(current.Id))
                {
                    var character = CharacterPlayerStats(conn);
                    DeactivateTinctureLocked(conn, current.Id, now, character, "manually deactivated", inBar: current,
                        allowLinger: true);
                    RecomputePoolsAndSpeed(conn);
                    state.StatRevision++;
                    changed = true;
                    Console.WriteLine($"[gateway] {remote} <= 0x0070 tincture {slot} ({type.Name}): deactivated, cooldown {effects.CooldownRemaining(now):0.##} s");
                    return;
                }

                // Activation checks: level requirement, cooldown, resource floor, and active tincture cap.
                int reqLevel = ItemModStats.LevelRequirement(current.Item, type.DropLevel, catalog.Mods);
                if (conn.Pools.Level < reqLevel)
                {
                    Console.WriteLine($"[gateway] {remote} <= 0x0070 tincture {slot} ({type.Name}): refused, requires level {reqLevel} (character is {conn.Pools.Level})");
                    return;
                }
                if (effects.OnCooldown(now))
                {
                    Console.WriteLine($"[gateway] {remote} <= 0x0070 tincture {slot} ({type.Name}): refused, on cooldown ({effects.CooldownRemaining(now):0.##} s left)");
                    return;
                }

                var playerStats = CharacterPlayerStats(conn);
                int Get(string name) => TinctureStat(playerStats, name);
                bool lifeBurn = Get("keystone_tinctures_drain_life") != 0;
                int recoverManaPct = Get("recover_%_maximum_mana_on_activating_tincture");
                if (!lifeBurn && conn.PlayerMana == 0 && recoverManaPct <= 0)
                {
                    Console.WriteLine($"[gateway] {remote} <= 0x0070 tincture {slot} ({type.Name}): refused, 0 mana");
                    return;
                }
                if (lifeBurn && conn.PlayerLife <= 1)
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

                if (conn.EntryProtection.End(now, conn.Walk.Position(now), action: true) is ushort graceBuff)
                    state.Packets.Add(new(InstanceEntities.RemoveBuff(PlayerId, graceBuff),
                        "Grace Period ends: tincture activated", WorldId: PlayerId));

                var local = ItemModStats.Local(current.Item, catalog.Mods);
                int emptySlots = Math.Max(0, 5 - bar.Count);
                bool highToxicity = effects.BurnStacks >= Tinctures.HighToxicityThreshold;
                bool usedLifeFlaskRecently = conn.RecentEvents.Happened(StatConditions.UsedLifeFlask, now);
                double multiplier = Tinctures.EffectMultiplier(current.Item, local, Get, emptySlots,
                    highToxicity, usedLifeFlaskRecently, usingFlask: AnyFlaskEffect(conn));
                var scaled = Tinctures.ScaledMods(current.Item, catalog.Mods, multiplier);
                double interval = Tinctures.DebuffInterval(definition, local, Get);
                double cooldown = Tinctures.CooldownSeconds(definition, local, Get);
                var buffs = catalog.TinctureBuffs;
                ushort buffRow = Tinctures.ResolveBuffRow(current.Item, catalog.Mods, definition, buffs);
                ushort buffInstance = buffRow != 0 ? conn.NextBuff++ : (ushort)0;

                if (recoverManaPct > 0)
                {
                    uint manaBefore = conn.PlayerMana;
                    double manaCarry = 0;
                    conn.PlayerMana = RecoveryPool.Advance(manaBefore, (uint)UnreservedMana(conn),
                        Math.Floor(conn.Pools.Mana * recoverManaPct / 100.0), ref manaCarry);
                    if (conn.PlayerMana != manaBefore)
                        state.Packets.Add(new([], $"0x01b1 player mana {conn.PlayerMana} (tincture activation recovery)",
                            Pool: FlaskOutputPool.Mana));
                }

                effects.LifeBurn = lifeBurn;
                var active = new Tinctures.Active(current.Id, (byte)slot, type.Name, current.Item, definition,
                    buffInstance, buffRow, now, interval, cooldown, local, multiplier, scaled);
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
                    effects.ParentBuff = conn.NextBuff++;
                    state.Packets.Add(new(InstanceEntities.AddBuff(PlayerId, effects.ParentBuff, buffs.Parent, PlayerId),
                        $"0x01c1 player buff: tincture parent ({buffs.Parent})", WorldId: PlayerId));
                }
                if (buffInstance != 0)
                    state.Packets.Add(new(Flasks.Buff(PlayerId, buffInstance, buffRow, (byte)slot, seconds: null),
                        $"0x01c1 player buff: {type.Name} (tincture {slot})", WorldId: PlayerId));

                RecomputePoolsAndSpeed(conn);
                state.StatRevision++;
                changed = true;
                Console.WriteLine($"[gateway] {remote} <= 0x0070 tincture {slot} ({type.Name}): active, burn every {interval:0.##} s, cooldown {cooldown:0.##} s");
            }
        }
        finally
        {
            conn.FlaskBarGate.Release();
        }
        if (changed) await PublishFlaskChanges(remote, stream, ciphers, conn);
    }

    // Called after PickUpItem / PlaceItem / HeldOntoItem / currency modification on the flask bar: deactivates any
    // active tincture that was removed, swapped, moved to another slot, or modified.
    public static async Task ReconcileTincturesAfterFlaskBarChange(string remote, NetworkStream stream,
        ConnectionCiphers ciphers, ClientState conn)
    {
        if (!conn.TinctureEffects.AnyActive) return;
        await conn.FlaskBarGate.WaitAsync();
        bool changed = false;
        try
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return;
                var bar = ItemsOf(conn).Items(PlayerInventories.Flask);
                double now = AreaTime(conn);
                var snapshot = conn.TinctureEffects.ActiveSnapshot();
                Dictionary<uint, int>? character = null;
                foreach (var active in snapshot)
                {
                    var present = bar.FirstOrDefault(i => i.Id == active.ItemId);
                    if (present is not null && present.X == active.Slot && ReferenceEquals(present.Item, active.Item)) continue;
                    character ??= CharacterPlayerStats(conn);
                    DeactivateTinctureLocked(conn, active.ItemId, now, character,
                        present is null ? "unequipped" : "moved or modified", inBar: present, allowLinger: false);
                    changed = true;
                }
                if (changed)
                {
                    RecomputePoolsAndSpeed(conn);
                    Automation(conn).StatRevision++;
                }
            }
        }
        finally
        {
            conn.FlaskBarGate.Release();
        }
        if (changed) await PublishFlaskChanges(remote, stream, ciphers, conn);
    }

    // Must be called under conn.Monsters. When inBar is non-null, caller must hold FlaskBarGate so 0xbf revisions stay ordered;
    // when called from RunResourceRecovery (which does not hold FlaskBarGate), pass inBar: null and queue the item id for SendTinctureBarInactive.
    static void DeactivateTinctureLocked(ClientState conn, uint itemId, double now, IReadOnlyDictionary<uint, int> character,
        string why, InventoryItem? inBar, bool allowLinger)
    {
        var effects = conn.TinctureEffects;
        var state = Automation(conn);
        int stacksBefore = effects.BurnStacks;
        double lingerSeconds = allowLinger ? TinctureLingerSeconds(character, stacksBefore) : 0;
        ushort lingerBuff = lingerSeconds > 0 ? conn.NextBuff++ : (ushort)0;
        var removed = effects.Deactivate(itemId, now, out bool allInactive, lingerSeconds, lingerBuff);
        if (removed is null) return;

        if (removed.Buff != 0)
            state.Packets.Add(new(InstanceEntities.RemoveBuff(PlayerId, removed.Buff),
                $"0x01c2 player buff: {removed.Name} (tincture {removed.Slot}) ends ({why})", WorldId: PlayerId));
        if (lingerSeconds > 0 && lingerBuff != 0 && removed.BuffRow != 0)
            state.Packets.Add(new(Flasks.Buff(PlayerId, lingerBuff, removed.BuffRow, removed.Slot, lingerSeconds),
                $"0x01c1 player buff: {removed.Name} lingers for {lingerSeconds:0.##} s", WorldId: PlayerId));
        if (inBar is not null)
        {
            var items = ItemsOf(conn);
            var wireInactive = inBar with { Item = inBar.Item with { TinctureActive = false } };
            state.Packets.Add(new(InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Flask),
                items.NextRevision(PlayerInventories.Flask), [inBar.Id], [wireInactive]),
                $"0x00bf tincture {inBar.X} ({removed.Name}): inactive ({why})"));
        }
        if (allInactive) ClearTinctureBurnAndParentBuffsLocked(conn, why);
    }

    static void DeactivateAllTincturesLocked(ClientState conn, double now, IReadOnlyDictionary<uint, int> character,
        string why, bool allowLinger, List<uint>? deactivatedIds)
    {
        var effects = conn.TinctureEffects;
        if (!effects.AnyActive) return;
        var state = Automation(conn);
        int stacksBefore = effects.BurnStacks;
        double lingerSeconds = allowLinger ? TinctureLingerSeconds(character, stacksBefore) : 0;
        var (removed, lingering) = effects.DeactivateAll(now, lingerSeconds,
            a => lingerSeconds > 0 && a.BuffRow != 0 ? conn.NextBuff++ : (ushort)0);
        if (removed.Count == 0) return;

        foreach (var a in removed)
        {
            if (a.Buff != 0)
                state.Packets.Add(new(InstanceEntities.RemoveBuff(PlayerId, a.Buff),
                    $"0x01c2 player buff: {a.Name} (tincture {a.Slot}) ends ({why})", WorldId: PlayerId));
            deactivatedIds?.Add(a.ItemId);
        }
        foreach (var l in lingering)
            if (l.Buff != 0 && l.BuffRow != 0)
                state.Packets.Add(new(Flasks.Buff(PlayerId, l.Buff, l.BuffRow, l.Slot, lingerSeconds),
                    $"0x01c1 player buff: {l.Name} lingers for {lingerSeconds:0.##} s", WorldId: PlayerId));
        ClearTinctureBurnAndParentBuffsLocked(conn, why);
    }

    public static void DeactivateAllTincturesOnDeathLocked(ClientState conn, double now,
        IReadOnlyDictionary<uint, int> recoveryStats, ref List<uint>? deactivatedIds)
    {
        var effects = conn.TinctureEffects;
        if (!effects.AnyEffective) return;
        var state = Automation(conn);
        if (effects.AnyActive)
        {
            deactivatedIds ??= [];
            DeactivateAllTincturesLocked(conn, now, recoveryStats, "death", allowLinger: false, deactivatedIds);
        }
        foreach (var l in effects.ExpireLingering(now, all: true))
            if (l.Buff != 0)
                state.Packets.Add(new(InstanceEntities.RemoveBuff(PlayerId, l.Buff),
                    $"0x01c2 player buff: lingering {l.Name} ends (death)", WorldId: PlayerId));
        ClearTinctureBurnAndParentBuffsLocked(conn, "death");
        RecomputePoolsAndSpeed(conn);
        state.StatRevision++;
    }

    static void ClearTinctureBurnAndParentBuffsLocked(ClientState conn, string why)
    {
        var effects = conn.TinctureEffects;
        var state = Automation(conn);
        if (effects.BurnBuff != 0)
        {
            state.Packets.Add(new(InstanceEntities.RemoveBuff(PlayerId, effects.BurnBuff),
                $"0x01c2 player buff: tincture burn ends ({why})", WorldId: PlayerId));
            effects.BurnBuff = 0;
            effects.PresentedBurnStacks = 0;
        }
        if (!effects.AnyEffective && effects.ParentBuff != 0)
        {
            state.Packets.Add(new(InstanceEntities.RemoveBuff(PlayerId, effects.ParentBuff),
                $"0x01c2 player buff: tincture parent ends ({why})", WorldId: PlayerId));
            effects.ParentBuff = 0;
        }
    }

    static void SyncTinctureBurnBuffLocked(ClientState conn, bool lifeBurn)
    {
        var effects = conn.TinctureEffects;
        var state = Automation(conn);
        var buffs = ItemsFor(conn).TinctureBuffs;
        ushort burnRow = lifeBurn ? buffs.LifeBurn : buffs.ManaBurn;
        int stacks = effects.BurnStacks;
        if (stacks <= 0 || burnRow == 0)
        {
            if (effects.BurnBuff != 0)
            {
                state.Packets.Add(new(InstanceEntities.RemoveBuff(PlayerId, effects.BurnBuff),
                    "0x01c2 player buff: tincture burn cleared", WorldId: PlayerId));
                effects.BurnBuff = 0;
                effects.PresentedBurnStacks = 0;
            }
            return;
        }
        if (effects.BurnBuff != 0 && effects.PresentedBurnStacks == stacks && effects.PresentedLifeBurn == lifeBurn)
            return;
        if (effects.BurnBuff != 0)
            state.Packets.Add(new(InstanceEntities.RemoveBuff(PlayerId, effects.BurnBuff), null, WorldId: PlayerId));
        effects.BurnBuff = conn.NextBuff++;
        effects.PresentedBurnStacks = stacks;
        effects.PresentedLifeBurn = lifeBurn;
        state.Packets.Add(new(InstanceEntities.AddBuff(PlayerId, effects.BurnBuff, burnRow, PlayerId,
            stacks: (ushort)Math.Min(stacks, ushort.MaxValue)),
            $"0x01c1 player buff: {(lifeBurn ? "Weeping Wounds" : "Mana Burn")} ({stacks})", WorldId: PlayerId));
    }

    // Advances active/lingering tinctures inside RunResourceRecovery (under conn.Monsters). Returns per-step Mana/Life
    // burn drain and any deactivated item ids whose 0xbf inactive update must be sent after releasing conn.Monsters.
    public static void AdvanceTincturesLocked(ClientState conn, double now, double seconds,
        IReadOnlyDictionary<uint, int> recoveryStats, out double manaDrain, out double lifeDrain,
        ref List<uint>? deactivatedIds)
    {
        manaDrain = 0;
        lifeDrain = 0;
        var effects = conn.TinctureEffects;
        if (effects.DrainPendingBarInactive() is { Count: > 0 } pending)
        {
            deactivatedIds ??= [];
            deactivatedIds.AddRange(pending);
        }
        if (!effects.AnyEffective) return;
        var state = Automation(conn);

        if (conn.PlayerLife == 0)
        {
            DeactivateAllTincturesOnDeathLocked(conn, now, recoveryStats, ref deactivatedIds);
            return;
        }

        if (conn.LeavingArea) return;

        bool statsChanged = false;
        // 1. Expire lingering tinctures whose duration ended.
        foreach (var l in effects.ExpireLingering(now))
        {
            if (l.Buff != 0)
                state.Packets.Add(new(InstanceEntities.RemoveBuff(PlayerId, l.Buff),
                    $"0x01c2 player buff: lingering {l.Name} ends", WorldId: PlayerId));
            statsChanged = true;
        }
        if (!effects.AnyEffective && effects.ParentBuff != 0)
            ClearTinctureBurnAndParentBuffsLocked(conn, "linger expired");

        // 2. Verify active tinctures are still in their flask-bar cells.
        if (effects.AnyActive)
        {
            var bar = ItemsOf(conn).Items(PlayerInventories.Flask);
            foreach (var active in effects.ActiveSnapshot())
            {
                if (bar.Any(i => i.Id == active.ItemId && i.X == active.Slot && ReferenceEquals(i.Item, active.Item))) continue;
                DeactivateTinctureLocked(conn, active.ItemId, now, recoveryStats, "left flask bar", inBar: null, allowLinger: false);
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
                    if (avoidChance > 0 && Random.Shared.Next(100) < avoidChance) continue;
                    int next = effects.AddBurnStacks(1);
                    if (deactivateAt > 0 && next >= deactivateAt)
                    {
                        deactivatedIds ??= [];
                        DeactivateAllTincturesLocked(conn, now, recoveryStats,
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
                var catalog = ItemsFor(conn);
                int emptySlots = Math.Max(0, 5 - ItemsOf(conn).Items(PlayerInventories.Flask).Count);
                bool highToxicity = effects.BurnStacks >= Tinctures.HighToxicityThreshold;
                bool usedLifeFlaskRecently = conn.RecentEvents.Happened(StatConditions.UsedLifeFlask, now);
                bool usingFlask = AnyFlaskEffect(conn);
                int RecStat(string name) => TinctureStat(recoveryStats, name);
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
                    SyncTinctureBurnBuffLocked(conn, lifeBurn);
                    statsChanged = true;
                }
                int ignored = Math.Max(0, TinctureStat(recoveryStats, "first_X_stacks_of_tincture_toxicity_have_no_effect"));
                double drain = Tinctures.BurnPerSecond(effects.BurnStacks,
                    lifeBurn ? conn.PlayerMaxLife : conn.Pools.Mana, ignored) * seconds;
                if (lifeBurn) lifeDrain = drain;
                else manaDrain = drain;
            }
        }

        if (statsChanged)
        {
            RecomputePoolsAndSpeed(conn);
            state.StatRevision++;
        }
    }

    // Post-recovery check under conn.Monsters: automatically deactivates active tinctures when Mana reaches 0
    // (or when Life reaches 1 under Bloodsoaked Blade).
    public static void CheckTinctureResourceDeactivationLocked(ClientState conn, double now,
        IReadOnlyDictionary<uint, int> recoveryStats, ref List<uint>? deactivatedIds)
    {
        var effects = conn.TinctureEffects;
        if (!effects.AnyActive) return;
        bool depleted = effects.LifeBurn ? conn.PlayerLife <= 1 : conn.PlayerMana == 0;
        if (!depleted) return;
        deactivatedIds ??= [];
        DeactivateAllTincturesLocked(conn, now, recoveryStats,
            effects.LifeBurn ? "life reached 1" : "mana reached 0", allowLinger: true, deactivatedIds);
        RecomputePoolsAndSpeed(conn);
        Automation(conn).StatRevision++;
    }

    // Sends 0xbf inactive updates for tinctures deactivated during RunResourceRecovery, acquiring FlaskBarGate
    // outside conn.Monsters to preserve canonical lock ordering.
    public static async Task SendTinctureBarInactive(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, List<uint>? deactivatedIds)
    {
        if (deactivatedIds is not { Count: > 0 }) return;
        await ChangeFlaskBar(remote, stream, ciphers, conn, items =>
        {
            var bar = items.Items(PlayerInventories.Flask);
            List<InventoryItem> changed = [];
            foreach (uint id in deactivatedIds)
                if (!conn.TinctureEffects.IsActive(id) && bar.FirstOrDefault(i => i.Id == id) is { } current)
                    changed.Add(current with { Item = current.Item with { TinctureActive = false } });
            return changed.Count == 0 ? null : ([.. changed.Select(c => c.Id)], changed);
        }, changed => $"0x00bf tincture(s) inactive: {string.Join(", ", changed.Select(c => c.X))}", retainOutput: true);
    }

    // Applies active and lingering tincture stats to CharacterStatSources. Zero-allocation fast path when inactive.
    public static Dictionary<uint, int> WithTinctures(ClientState conn, Dictionary<uint, int> sources)
    {
        if (!conn.TinctureEffects.AnyEffective) return sources;
        var (scaledStats, stacks, active, lingering) = conn.TinctureEffects.EffectiveSnapshot();
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

        int Get(string name)
        {
            if (Stats.Key(name) is not uint key) return 0;
            lock (conn.Monsters) return conn.PlayerStats.GetValueOrDefault(key, sources.GetValueOrDefault(key));
        }

        var weapons = WeaponProfileFor(conn, activeSet: true);
        bool allowRanged = Get("tinctures_can_apply_to_ranged_weapons") != 0;
        bool mainEligible = !weapons.Unarmed && (weapons.Melee || allowRanged && (weapons.Bow || weapons.Wand));
        bool offEligible = ActiveOffHand(conn) is { Type: { } offType }
            && (WeaponIdentity.OneHandMelee(offType.Class) || allowRanged && offType.Class == "Wand");
        bool weaponApplied = mainEligible || offEligible;

        if (active) Set("using_tincture", 1);
        if (lingering) Set("has_lingering_tincture", 1);
        if (stacks > 0)
        {
            Set("current_tincture_toxicity", stacks);
            // BuffTemplates TinctureManaBurn / TinctureLifeBurn carry 60 (%/min per stack = 1%/s) so the client's
            // own mana/life orb tick predicts Mana Burn / Weeping Wounds smoothly between 0x1b1 updates.
            Set(conn.TinctureEffects.LifeBurn
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
            Add(stat, value);
            if (stat == "item_rarity_+%_per_toxicity_up_to_100%_from_tincture" && stacks > 0)
                Add("base_item_found_rarity_+%", Math.Min(100, (int)Math.Clamp((long)value * stacks, 0, 100)));
            else if (stat == "fire_damage_taken_+_per_toxicity_from_tincture" && stacks > 0)
                Add("fire_damage_taken_+%", ActorScale.Saturate((long)value * stacks));
            else if (weaponApplied)
            {
                if (stat == "penetrate_elemental_resistances_%_per_toxicity_with_tinctured_weapons" && stacks > 0)
                    Add("reduce_enemy_elemental_resistance_with_weapons_%", ActorScale.Saturate((long)value * stacks));
                else
                    foreach (var (source, target) in TinctureWeaponStatMap)
                        if (stat == source) { Add(target, value); break; }
            }
        }
        return sources;
    }

    // Sends 0xbf charge updates for flasks adjacent to active tinctures (Nature's Concoction), acquiring FlaskBarGate
    // outside conn.Monsters to preserve canonical lock ordering.
    public static async Task SendTinctureAdjacentFlaskCharges(string remote, NetworkStream stream,
        ConnectionCiphers ciphers, ClientState conn, List<(byte Slot, int Charges)>? grants)
    {
        if (grants is not { Count: > 0 }) return;
        var catalog = ItemsFor(conn);
        await ChangeFlaskBar(remote, stream, ciphers, conn, items =>
        {
            var totals = new Dictionary<byte, int>();
            foreach (var (slot, charges) in grants)
                totals[slot] = totals.GetValueOrDefault(slot) + charges;
            List<InventoryItem> changed = [];
            foreach (var flask in items.Items(PlayerInventories.Flask).ToArray())
            {
                if (flask.X > byte.MaxValue || !totals.TryGetValue((byte)flask.X, out int amount) || amount <= 0) continue;
                if (catalog.ByHash(flask.Item.TypeHash) is not { } type || catalog.FlaskChargesOf(type) is not { } use) continue;
                if (!CanGainFlaskCharges(conn, flask)) continue;
                if (items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, i => Flasks.Gain(i, use, amount)) is { } done)
                    changed.Add(done);
            }
            return changed.Count == 0 ? null : ([.. changed.Select(c => c.Id)], changed);
        }, changed => $"0x00bf adjacent tincture flask charges: {string.Join(", ", changed.Select(c => $"{c.X}: {c.Item.Charges}"))}",
            retainOutput: true);
    }

    // Extra flask effect granted by Nature's Concoction / Tincture Mastery 4 to flasks adjacent to an active tincture
    // when the player has hit an enemy with a weapon recently. Zero-allocation fast path when no tincture is active.
    public static int AdjacentTinctureFlaskEffect(ClientState conn, byte slot, Func<string, int> character, double now)
    {
        if (!conn.TinctureEffects.HasActiveAdjacentTo(slot)) return 0;
        int bonus = character("flasks_adjacent_to_active_tinctures_have_effect_+%_if_hit_with_weapon_recently");
        if (bonus == 0) return 0;
        return conn.RecentEvents.Happened(StatConditions.HitEnemy, now) ? bonus : 0;
    }

    // Called under conn.Monsters on a landed player weapon attack hit (melee, area attack, or projectile attack):
    // handles The Battle Within (on-hit Mana Burn), Grasping Nightshade (Wither stacks), Wildfire Phloem (Ignite
    // refresh & Covered in Ash on full life), and Nature's Concoction (adjacent flask charges on weapon hit, 0.5 s ICD).
    public static void TinctureOnWeaponHitLocked(ClientState conn, IReadOnlyDictionary<uint, int> player,
        SpawnedMonster target, uint targetLifeBefore, double now, Random? random = null)
    {
        var effects = conn.TinctureEffects;
        if (!effects.AnyEffective) return;
        if (TinctureStat(player, "tincture_applied") == 0) return;
        int Get(string name) => TinctureStat(player, name);
        random ??= Random.Shared;

        // Grasping Nightshade & Wildfire Phloem apply while active or lingering on living hostile targets.
        if (target.Hostile && !target.Dead && target.Life > 0)
        {
            int witherStacks = Get("apply_X_wither_stacks_for_2_seconds_with_tinctured_weapons");
            if (witherStacks > 0)
                ApplyWitherStacksLocked(target, witherStacks, WitherDurationSeconds(2.0, Get), Get, now);
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
            DeactivateAllTincturesLocked(conn, now, player, $"reached {deactivateAt} Mana Burn on hit",
                allowLinger: true, deactivatedIds: ids);
            foreach (uint id in ids) effects.QueuePendingBarInactive(id);
        }
        else
            SyncTinctureBurnBuffLocked(conn, effects.LifeBurn);
        RecomputePoolsAndSpeed(conn);
        Automation(conn).StatRevision++;
    }

    // Called on player kill: handles "X% chance to remove 1 stack of Mana Burn on Kill".
    public static void TinctureOnKillLocked(ClientState conn, IReadOnlyDictionary<uint, int> player)
    {
        if (!conn.TinctureEffects.AnyActive || conn.TinctureEffects.BurnStacks <= 0) return;
        int chance = Math.Clamp(TinctureStat(player, "chance_to_remove_1_tincture_toxicity_on_kill_%"), 0, 100);
        if (chance <= 0 || Random.Shared.Next(100) >= chance) return;
        conn.TinctureEffects.RemoveBurnStacks(1);
        SyncTinctureBurnBuffLocked(conn, conn.TinctureEffects.LifeBurn);
        RecomputePoolsAndSpeed(conn);
        Automation(conn).StatRevision++;
    }
}
