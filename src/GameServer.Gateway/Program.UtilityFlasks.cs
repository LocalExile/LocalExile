using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

partial class Program
{
    // C→S 0x70 on a utility flask (13-items.md §8a): it spends its charges (S→C 0xbf), puts its buff on the player with
    // its duration (S→C 0x1c1) and its stats count in the pools until the duration ends (UtilityFlasks), with the stats
    // and movement speed sent again at the start and the end. Refused while dead, with too few charges, or while this
    // flask's own effect still runs. Another flask of the same kind keeps its own timer; only the strongest applies.
    static async Task UseUtilityFlask(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint slot,
        InventoryItem flask, ItemCatalog.BaseType type, UtilityFlasks.Utility utility)
    {
        // Mageblood: its magic utility flasks are always on and cannot be used (the client enforces the mod; this is the
        // server backstop).
        if (MagebloodBlocksFlaskUse(conn, flask.Item))
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot} ({type.Name}): refused, Mageblood's magic utility flasks cannot be used");
            return;
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
                conn.Clock.Elapsed.TotalSeconds) is { } refused)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot} ({type.Name}): refused, {refused}");
            return;
        }
        var items = ItemsOf(conn);
        if (await SpendFlask(remote, stream, ciphers, conn, flask, charges, slot) is null) return;

        var (stats, seconds) = UtilityFlasks.For(utility, flask.Item, local, Get, playerLevel);
        double now = conn.Clock.Elapsed.TotalSeconds;
        ushort buff;
        ushort? extra;
        lock (conn.Monsters)
        {
            buff = conn.NextBuff++;
            extra = utility.Extra is null ? null : conn.NextBuff++;
        }
        var effect = new UtilityFlasks.Active(flask.Id, (byte)slot, type.Name, utility, buff, extra, stats, now, now + seconds);
        var ticker = UtilityFlasks.Start(conn, effect, () => RecomputePoolsAndSpeed(conn));
        lock (conn.Monsters)
            if (!conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0
                && UtilityFlasks.RestoreWardAfterPaidUse(conn.Ward, name => local.GetValueOrDefault(name), conn.Clock.Elapsed.TotalSeconds))
                Console.WriteLine($"[gateway] Ward restored on paid {type.Name}: {conn.Ward.Current}/{conn.Ward.Maximum}");
        // The ticker starts before anything is sent: a failed send must not leave an effect without one to end it.
        if (ticker is int ticket) _ = RunUtilityFlasks(remote, stream, ciphers, conn, ticket);
        Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot} ({type.Name}): {utility.BuffId} for {seconds:0.##} s: " +
            string.Join(", ", stats.Select(s => $"{s.Stat} {s.Value}")) + $"; walk estimate {conn.Walk.Speed:0.##} cells/s");
        await SendUtilityFlaskStart(remote, stream, ciphers, conn, effect);
        await MakeFlaskGrounds(remote, stream, ciphers, conn, type.Name, local, seconds);
        await SendCharacterStats(remote, stream, ciphers, conn, $"{type.Name} on");
    }

    // The source buff and its maintained projection are one accepted write: expiry/death cannot remove the source
    // between their adds and leave an infinite Onslaught behind. Sample recipient stats and base time at that gate.
    static Task<bool> SendUtilityFlaskStart(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, UtilityFlasks.Active effect) => SendGenerated(remote, stream, ciphers, () =>
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0
                || UtilityFlasks.LeftItsCell(effect, ItemsOf(conn).Items(PlayerInventories.Flask))
                || conn.UtilityFlaskEffects.RemainingBase(effect, conn.Clock.Elapsed.TotalSeconds) <= 0) return null;
            var stats = PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn));
            return UtilityFlaskStartPacket(conn, effect, stats, conn.Clock.Elapsed.TotalSeconds);
        }
    }, $"0x01c1 player buffs: {effect.Name} (flask {effect.Slot})");

    static byte[] UtilityFlaskStartPacket(ClientState conn, UtilityFlasks.Active effect, byte[] stats, double now)
    {
        double remaining = conn.UtilityFlaskEffects.RemainingBase(effect, now);
        // The snapshot has already consumed SentStatKeys clears. Keep that publication even if construction
        // crossed the source deadline; only the now-invalid source/maintained buff adds must be suppressed.
        return remaining <= 0 ? stats
            : [.. stats, .. UtilityFlasks.StartPackets(effect, PlayerId, remaining).SelectMany(p => p)];
    }

    // Each resource of a hybrid has its own source and queue. Publish only that source's remaining base budget.
    static Task<bool> SendRecoveryFlaskStart(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, Flasks.Effect effect, bool withDuration) => SendGenerated(remote, stream, ciphers, () =>
    {
        lock (conn.Monsters)
        {
            double Rate() => RecipientEffectRate(conn,
                EquipmentStatSources(conn, ItemsOf(conn).EquipmentSnapshot()).Sources, 4);
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0
                || conn.FlaskEffects.ProjectedRemaining(effect, conn.Clock.Elapsed.TotalSeconds, Rate()) <= 0) return null;
            var stats = PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn));
            double remaining = conn.FlaskEffects.ProjectedRemaining(effect, conn.Clock.Elapsed.TotalSeconds, Rate());
            if (remaining <= 0) return stats;
            uint perMinute = (uint)Math.Clamp(Math.Round((effect.LifePerSecond + effect.ManaPerSecond) * 60), 0, uint.MaxValue);
            return [.. stats, .. Flasks.Buff(PlayerId, effect.Buff, effect.BuffRow, effect.Slot,
                withDuration ? remaining : null, [perMinute])];
        }
    }, $"0x01c1 recovery flask {effect.Slot}, buff {effect.Buff}");

    // The player's increased rarity of items found (Gold Flask, gear, passives) that the drops of a kill use
    // (ItemDrops.WithIncreasedRarity).
    static int IncreasedItemRarity(ClientState conn)
    {
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        return Get("base_item_found_rarity_+%") + Get("item_found_rarity_+%");
    }

    // The pools and the walk speed again (under the Monsters lock): after a utility flask effect started or ended, or the
    // equipment changed.
    static void RecomputePoolsAndSpeed(ClientState conn)
    {
        conn.Pools = PoolsFor(conn, conn.Pools.Level);
        conn.ApplyMovementSpeed();
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        conn.Phasing = UtilityFlasks.Phasing(UtilityStats(conn, Get));
        conn.ElementalAilments.Reconcile(Get, conn.Clock.Elapsed.TotalSeconds);
        conn.ApplyMovementSpeed();
    }

    // Ends the utility flask effects whose time is up (by the connection clock: a pause holds them), whose flask left its
    // cell of the flask bar, or, on death, all of them: their buffs come off and the stats and movement speed go back. Runs
    // while any effect runs; a failed step is logged and the next one tries again.
    static async Task RunUtilityFlasks(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int ticket)
    {
        try
        {
            bool stop = false;
            while (!conn.Closed && !conn.RageRetired && !stop)
            {
                await Task.Delay(RecoveryStepMs);
                if (conn.Closed || conn.RageRetired) break;
                if (conn.LeavingArea) continue;
                try
                {
                    bool dead = conn.PlayerLife == 0;
                    var bar = ItemsOf(conn).Items(PlayerInventories.Flask);
                    bool Gone(UtilityFlasks.Active e) => UtilityFlasks.LeftItsCell(e, bar);
                    var ended = UtilityFlasks.EndDue(conn, conn.Clock.Elapsed.TotalSeconds, dead, Gone, () => RecomputePoolsAndSpeed(conn), out stop);
                    if (ended.Count == 0) continue;
                    foreach (var e in ended)
                        await SendUtilityFlaskEnd(remote, stream, ciphers, e, dead ? "death" : Gone(e) ? "the flask left its cell" : "its time is up");
                    await SendCharacterStats(remote, stream, ciphers, conn, $"{string.Join(", ", ended.Select(e => e.Name))} off, walk estimate {conn.Walk.Speed:0.##} cells/s");
                }
                catch (Exception ex)
                {
                    if (!conn.Closed) Console.WriteLine($"[gateway] {remote} utility flasks: {ServerLog.Describe(ex)}");
                }
            }
        }
        finally
        {
            // However it stopped, the next use starts a ticker again (a closed connection ends its effects with it: an area
            // change starts a new connection without them).
            conn.UtilityFlaskEffects.TickerExited(ticket);
        }
    }

    static async Task SendUtilityFlaskEnd(string remote, NetworkStream stream, ConnectionCiphers ciphers, UtilityFlasks.Active e, string why)
    {
        foreach (var packet in UtilityFlasks.EndPackets(e, PlayerId))
            await Send(remote, stream, ciphers, packet, $"0x01c2 player buff: {e.Name} (flask {e.Slot}) effect ends ({why})");
    }
}
