using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // C→S 0x70 on a utility flask (13-items.md §8a): it spends its charges (S→C 0xbf), puts its buff on the player with
    // its duration (S→C 0x1c1) and its stats count in the pools until the duration ends (UtilityFlasks), with the stats
    // and movement speed sent again at the start and the end. Refused while dead or with too few charges.
    // Reusing this flask replaces its timer; another flask of the same kind keeps its own, strongest effect applies.
    public static async Task<bool> UseUtilityFlaskCore(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint slot,
        InventoryItem flask, ItemCatalog.BaseType type, UtilityFlasks.Utility utility, FlaskAutomationStep? signal = null)
    {
        var owner = CombatLifetime;
        bool CurrentUse() => CurrentGameWork && ReferenceEquals(CombatLifetime, owner) && !conn.Closed
            && !conn.LeavingArea && !conn.RageRetired && !conn.Paused && conn.PlayerLife > 0
            && ItemsOf(conn).Items(PlayerInventories.Flask).FirstOrDefault(i => i.Id == flask.Id && i.X == slot) is { } current
            && (signal is null || Automation(conn).CanAccept(signal.Value, current.Id))
            && EndSignalCurrent(conn, current, signal);
        if (!CurrentUse()) return false;
        // Mageblood: its magic utility flasks are always on and cannot be used (the client enforces the mod; this is the
        // server backstop).
        if (MagebloodBlocksFlaskUse(conn, flask.Item))
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot} ({type.Name}): refused, Mageblood's magic utility flasks cannot be used");
            return false;
        }
        // The character's flask stats (passives and gear: increased flask effect, duration and charges used) and the
        // flask's own mods.
        Dictionary<uint, int> character;
        int playerLevel;
        // Keep level and coefficient sources in one use snapshot, including across the asynchronous charge spend.
        lock (conn.Monsters)
        {
            playerLevel = conn.Pools.Level;
            character = CharacterPlayerStats(conn);
        }
        int Get(string name) => Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
        var local = ItemModStats.Local(flask.Item, ItemsFor(conn).Mods);
        var charges = FlaskCharges.Of(utility.Charges, local, Get);
        if (UtilityFlasks.Refusal(conn.PlayerLife == 0, conn.UtilityFlaskEffects.Of(flask.Id), flask.Item, charges,
                AreaTime(conn)) is { } refused)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot} ({type.Name}): refused, {refused}");
            return false;
        }
        var (stats, seconds) = UtilityFlasks.For(utility, flask.Item, local, Get, playerLevel);
        if (FlaskGrounds.OnUse(local).Any(g => g.Kind != FlaskGrounds.Kind.Taunt)) _ = FlaskGroundArt.Value;
        // Payment and installation are one admitted transaction. A reload after the inventory send cannot
        // strand paid charges without their accepted snapshot, deadline, Adjacent edge and presentation.
        if (await SpendFlask(remote, stream, ciphers, conn, flask, charges, slot, CurrentUse, _ =>
        {
            var state = Automation(conn);
            if (signal is { } step) state.Accepted(step, flask.Id);
            double now = AreaTime(conn);
            if (NamedFlaskRecent(type.Id) is { } recent) conn.RecentEvents.Record(recent, now);
            var effect = new UtilityFlasks.Active(flask.Id, (byte)slot, type.Name, utility, conn.NextBuff++,
                utility.Extra is null ? null : conn.NextBuff++, stats, now, now + seconds);
            if (conn.UtilityFlaskEffects.Of(flask.Id) is { } previous)
                QueueUtilityFlaskEnd(state, previous, "refreshed");
            UtilityFlasks.Start(conn, effect, () => RecomputePoolsAndSpeed(conn));
            ApplyConditionalFlaskAilmentProtectionLocked(conn, local, now);
            if (UtilityFlasks.RestoreWardAfterPaidUse(conn.Ward, name => local.GetValueOrDefault(name), now))
                Console.WriteLine($"[gateway] Ward restored on paid {type.Name}: {conn.Ward.Current}/{conn.Ward.Maximum}");
            Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot} ({type.Name}): {utility.BuffId} for {seconds:0.##} s: " +
                string.Join(", ", stats.Select(s => $"{s.Stat} {s.Value}")) + $"; walk estimate {conn.Walk.Speed:0.##} cells/s");
            state.Packets.Add(new([], $"0x01c1 player buff: {type.Name} (flask {slot}), {seconds:0.##} s",
                UtilityStart: effect));
            FlaskToLinkedTargetsLocked(conn, flask.Item, local, utility, Get);
            MakeFlaskGroundsLocked(conn, local, seconds, now, character);
            state.StatRevision++;
            state.Enqueue(new(FlaskEnchantments.Trigger.Adjacent, flask.Id, (byte)slot), signal?.Chain);
        }) is null) return false;
        await PublishFlaskChanges(remote, stream, ciphers, conn);
        if (CurrentGameWork && !conn.Closed && !conn.LeavingArea) await EnemyDebuffsTick(remote, stream, ciphers, conn, AreaTime(conn));
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

    // The player's increased rarity of items found (Gold Flask, gear, passives) that the drops of a kill use
    // (ItemDrops.WithIncreasedRarity).
    public static int IncreasedItemRarity(ClientState conn)
    {
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        int player = Get("base_item_found_rarity_+%") + Get("item_found_rarity_+%");
        return player + (MapDevice.Current(CharacterName(conn), conn.Layout.Hash)?.Mods.Rarity ?? 0);
    }

    // The player's increased quantity of items found (gear, passives) as a multiplier of the kill's item rolls
    // (ItemDrops.Roll). A kill made with a supported skill adds the skill's own
    // base_killed_monster_dropped_item_quantity_+% (Item Quantity Support).
    public static double ItemQuantityFactor(ClientState conn, SpawnedMonster? monster = null)
    {
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        double player = Math.Max(0, 100 + Get("base_item_found_quantity_+%") + Get("item_found_quantity_+%")) / 100.0;
        return player * (1 + Math.Max(0, monster?.KilledWithItemQuantityPct ?? 0) / 100.0)
            * (1 + Math.Max(0, monster?.EncounterStats?.GetValueOrDefault(MapSulphite.EnragedQuantity) ?? 0) / 10000.0)
            * (MapDevice.Current(CharacterName(conn), conn.Layout.Hash)?.Mods.QuantityFactor ?? 1);
    }

    // The pools and the walk speed again (under the Monsters lock): after a utility flask effect started or ended, or the
    // equipment changed.
    public static void RecomputePoolsAndSpeed(ClientState conn)
    {
        RefreshRighteousFireProfileLocked(conn);
        using (new PlayerStatScope(conn, fresh: true))
        {
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
            conn.PlayerLife = LifeReservation.Clamp(conn.PlayerLife, LifeReservationOf(conn));
            var stats = CharacterPlayerStats(conn);
            int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
            conn.Phasing = UtilityFlasks.Phasing(UtilityStats(conn, Get));
            conn.ElementalAilments.Reconcile(Get);
            conn.ApplyMovementSpeed();
        }
    }

    // The already-owned resource writer advances utility deadlines. During Stop/Start there is no writer;
    // the unchanged accepted effect is claimed by the replacement, even if its deadline passed in that gap.
    public static void AdvanceUtilityFlasks(ClientState conn, PlayerUpdatesState updates)
    {
        lock (conn.Monsters)
        {
            if (!PlayerUpdatesCurrent(conn, updates) || conn.LeavingArea || !conn.UtilityFlaskEffects.Any) return;
            bool dead = conn.PlayerLife == 0;
            var bar = ItemsOf(conn).Items(PlayerInventories.Flask);
            bool Gone(UtilityFlasks.Active effect) => UtilityFlasks.LeftItsCell(effect, bar);
            var ended = UtilityFlasks.EndDue(conn, updates.Schedule.Now(conn), dead, Gone,
                () => RecomputePoolsAndSpeed(conn), out _);
            if (ended.Count == 0) return;
            var state = Automation(conn);
            foreach (var effect in ended)
            {
                QueueUtilityFlaskEnd(state, effect, dead ? "death" : Gone(effect) ? "the flask left its cell" : "its time is up");
                // Record this admitted edge before any await or second lifetime read can lose it.
                if (!dead && !Gone(effect)) state.Enqueue(new(FlaskEnchantments.Trigger.End,
                    effect.ItemId, effect.Slot, effect.Activation));
            }
            state.StatRevision++;
        }
    }

    static void QueueUtilityFlaskEnd(FlaskAutomationState state, UtilityFlasks.Active effect, string why) =>
        state.Packets.AddRange(UtilityFlasks.EndPackets(effect, PlayerId)
            .Select(p => new FlaskOutput(p, $"0x01c2 player buff: {effect.Name} (flask {effect.Slot}) effect ends ({why})", WorldId: PlayerId)));

    public static async Task PublishFlaskChanges(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var state = Automation(conn);
        lock (conn.Monsters)
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea
                || state.Packets.Count == 0 && state.StatRevision == state.PresentedStatRevision) return;
        while (true)
        {
            FlaskOutput output;
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired || state.Packets.Count == 0) break;
                output = state.Packets[0];
            }
            bool sent = false;
            byte[][]? Project()
            {
                lock (conn.Monsters)
                {
                    if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired || state.Packets.Count == 0
                        || !ReferenceEquals(state.Packets[0], output)) return null;
                    // Pool/reservation output samples current values, never a pre-reload instant snapshot.
                    byte[][] packet = output.Pool switch
                    {
                        FlaskOutputPool.Life => [PlayerLifeUpdate(conn)],
                        FlaskOutputPool.Mana => WithOwnAuraRecords(conn, () => [InstanceProtocol.LifeUpdate(PlayerId,
                            conn.PlayerMana, (uint)ReservedMana(conn), pool: ManaPool)]),
                        _ => [output.Packet],
                    };
                    if (output.UtilityStart is { } effect)
                    {
                        var prefix = PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn));
                        double now = AreaTime(conn);
                        bool live = conn.PlayerLife > 0 && effect.Ends > now
                            && conn.UtilityFlaskEffects.IsLatest(effect.ItemId, effect.Activation)
                            && ReferenceEquals(conn.UtilityFlaskEffects.Of(effect.ItemId), effect);
                        packet = live ? [prefix, .. UtilityFlasks.StartPackets(effect with { Started = now }, PlayerId)] : [prefix];
                    }
                    state.Packets.RemoveAt(0); sent = true; return packet;
                }
            }
            uint? worldId = output.WorldId ?? (output.Pool != FlaskOutputPool.None || output.UtilityStart is not null ? PlayerId : null);
            if (output.CreatesObject)
                await SendWorldCreate(conn, worldId!.Value, () => Project()?.Single(), output.Log, transport: (remote, stream, ciphers));
            else if (output.RemovesObject)
                await SendWorldRemoval(remote, stream, ciphers, conn, worldId!.Value, output.Log, atGate: () => Project()?.Single());
            else if (worldId is uint id)
                await SendWorldRecords(remote, stream, ciphers, conn, id, Project, output.Log);
            else await Send(remote, stream, ciphers, [], output.Log, atGate: () =>
                Project() is { } records ? [.. records.SelectMany(p => p)] : null);
            if (sent && output.Pool == FlaskOutputPool.Mana && CurrentGameWork)
                await SendPlayerEnergyShield(remote, stream, ciphers, conn);
        }
        long revision;
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || state.StatRevision == state.PresentedStatRevision) return;
            revision = state.StatRevision;
        }
        await SendCharacterStats(remote, stream, ciphers, conn, "flask effects changed");
        lock (conn.Monsters)
            if (CurrentGameWork && !conn.Closed && !conn.LeavingArea) state.PresentedStatRevision = Math.Max(state.PresentedStatRevision, revision);
    }
}
