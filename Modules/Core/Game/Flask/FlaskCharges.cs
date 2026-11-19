using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

public static partial class Game
{
    // A change of the flask bar and its S→C 0xbf as one step under the connection's flask-bar gate: the crit, hit, kill and
    // every-3-seconds gains and the flask uses run on their own tasks, and the client must see their revisions in order,
    // each with the flask's latest charges. `change` makes the change and returns what to send (removed ids and the changed
    // items), or null for nothing.
    public static async Task ChangeFlaskBar(Player conn,
        Func<PlayerInventories, (uint[] Removed, List<InventoryItem> Changed)?> change, Func<List<InventoryItem>, string> log,
        bool retainOutput = false)
    {
        await conn.FlaskBarGate.WaitAsync();
        try
        {
            byte[] packet;
            string message;
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || HardcoreProgressBlocked(conn)) return;
                var items = ItemsOf(conn);
                var before = items.Items(PlayerInventories.Flask).ToDictionary(i => i.Id, i => i.Item.Charges);
                int outputAt = Automation(conn).Packets.Count;
                if (change(items) is not { } done || done.Changed.Count == 0) return;
                foreach (var changed in done.Changed)
                    if (before.TryGetValue(changed.Id, out int held) && changed.Item.Charges > held
                        && ItemsFor(conn).ByHash(changed.Item.TypeHash) is { } flaskType
                        && ItemsFor(conn).FlaskChargesOf(flaskType) is { } baseCharges
                        && changed.Item.Charges >= FlaskCharges.Of(baseCharges, ItemModStats.Local(changed.Item, ItemsFor(conn).Mods)).MaxCharges)
                        QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Full, changed.Id, changed.X);
                packet = InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Flask),
                    items.NextRevision(PlayerInventories.Flask), done.Removed, done.Changed);
                message = log(done.Changed);
                if (retainOutput) Automation(conn).Packets.Insert(outputAt, new(packet, message));
                else if (conn.ProjectileRuntime is { Active: true } session)
                {
                    session.Packet(packet, message);
                    return;
                }
            }
            if (retainOutput) await PublishFlaskChanges(conn);
            else await Send(conn, packet, message);
        }
        finally
        {
            conn.FlaskBarGate.Release();
        }
    }

    // A use's charges taken from the flask (still in its cell, with enough charges: checked again under the gate, as a
    // gain or another use may have run meanwhile) and sent (S→C 0xbf): the spent flask, or null when there were too few.
    public static async Task<InventoryItem?> SpendFlask(Player conn,
        InventoryItem flask, Flasks.Flask charges, uint slot, Func<bool>? still = null, Action<InventoryItem>? accepted = null)
    {
        InventoryItem? spent = null;
        await ChangeFlaskBar(conn, items =>
        {
            if (still is not null && !still()) return null;
            bool enough = false;
            var done = items.Change(PlayerInventories.Flask, flask.Id, i =>
            {
                var after = Flasks.Spend(i, charges);
                enough = after is not null;
                return after ?? i;
            });
            if (!enough || done is null) return null;
            spent = done;
            if (accepted is not null)
            {
                double now = AreaTime(conn);
                if (conn.EntryProtection.End(now, conn.Walk.Position(now), action: true) is ushort buff)
                    Automation(conn).Packets.Add(new(InstanceEntities.RemoveBuff(PlayerId, buff),
                        "Grace Period ends: movement, action or deadline", WorldId: PlayerId));
                accepted(done);
            }
            return ([done.Id], [done]);
        }, changed => $"0x00bf flask {slot}: {changed[0].Item.Charges}/{charges.MaxCharges} charges", retainOutput: accepted is not null);
        if (spent is not null && accepted is null) await EndGracePeriod(conn, action: true);
        return spent;
    }

    // Flask charge gains (13-items.md §8b, FlaskCharges): each flask in the bar that `chargesFor` gives something gets it
    // times its gain multiplier (the character's increased flask charges gained, the flask's increased charge recovery),
    // whole charges now and the rest kept, up to its maximum with its mods; the changed flasks in one S→C 0xbf, saved with
    // the next save. The character's stats are only worked out when some flask that is not full gains.
    public static async Task GainFlaskCharges(Player conn,
        Func<InventoryItem, IReadOnlyDictionary<uint, int>, double> chargesFor, string why)
    {
        var gaining = new List<(InventoryItem Flask, IReadOnlyDictionary<uint, int> Local, Flasks.Flask Charges, double Gain)>();
        foreach (var flask in ItemsOf(conn).Items(PlayerInventories.Flask))
        {
            if (ItemsFor(conn).ByHash(flask.Item.TypeHash) is not { } type || ItemsFor(conn).FlaskChargesOf(type) is not { } baseCharges) continue;
            var local = ItemModStats.Local(flask.Item, ItemsFor(conn).Mods);
            var charges = FlaskCharges.Of(baseCharges, local);
            double gain = chargesFor(flask, local);
            if (gain > 0 && flask.Item.Charges < charges.MaxCharges) gaining.Add((flask, local, charges, gain));
        }
        if (gaining.Count == 0) return;
        var character = CharacterPlayerStats(conn);
        int Get(uint key) => character.GetValueOrDefault(key);
        await ChangeFlaskBar(conn, items =>
        {
            List<InventoryItem> changed = [];
            foreach (var (flask, local, charges, gain) in gaining)
            {
                if (!CanGainFlaskCharges(conn, flask)) continue;
                int whole = conn.FlaskChargeCarry.Take(flask.Id, gain * FlaskCharges.GainMultiplier(local, Get));
                if (whole > 0 && items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, i => Flasks.Gain(i, charges, whole)) is { } done)
                {
                    changed.Add(done);
                }
            }
            return ([.. changed.Select(c => c.Id)], changed);
        }, changed => $"0x00bf flasks: {string.Join(", ", changed.Select(c => $"{c.X}: {c.Item.Charges}"))} charges ({why})");
    }

    public static bool CanGainFlaskCharges(Player conn, InventoryItem flask)
    {
        var activeEffect = conn.UtilityFlaskEffects.Of(flask.Id);
        if (FlaskEnchantments.BlocksGain(ItemModStats.Local(flask.Item, ItemsFor(conn).Mods),
                activeEffect is not null && activeEffect.Ends > AreaTime(conn))) return false;
        var rules = LeagueFor(conn).Rules;
        if (rules.UtilityFlaskChargesWhileActive || ItemsFor(conn).ByHash(flask.Item.TypeHash) is not { } type
            || ItemsFor(conn).UtilityFlaskOf(type) is null) return true;
        var active = conn.UtilityFlaskEffects.Of(flask.Id);
        bool running = active is not null && active.Ends > AreaTime(conn);
        bool exception = rules.UtilityFlaskExceptionStat is { } stat && Stats.Key(stat) is uint key
            && CharacterPlayerStats(conn).GetValueOrDefault(key) > 0;
        return LeagueGameplay.CanGainUtilityCharges(!rules.UtilityFlaskChargesWhileActive, running, exception);
    }

    // A kill: 1 / 3.5 / 6 / 11 charges to every flask by the monster's rarity (FlaskCharges.KillCharges), or with
    // Game_FLASK_CHARGES=full every flask full.
    public static Task GainKillCharges(Player conn, MonsterRarity rarity, string why) =>
        FlaskCharges.FullShortcut ? GainFlaskCharges(conn, int.MaxValue, why)
            : GainFlaskCharges(conn, (_, _) => FlaskCharges.KillCharges(rarity, ItemsFor(conn).Mods), why);

    // A critical strike the player dealt (a hit that landed): each flask with "#% chance to gain a Flask Charge when you deal
    // a Critical Strike" (local_recharge_on_crit_%) rolls for one charge, and the character's chance and sure charges
    // (FlaskCharges.CritSources) go to the first flask that is not full. One cooldown for all of them
    // (FlaskCharges.CritCooldown), looked at before anything else.
    public static async Task GainFlaskChargesOnCrit(Player conn)
    {
        double now = AreaTime(conn);
        if (!conn.FlaskChargeCarry.CritReady(now)) return;
        var bar = CritFlaskBar(conn);
        if (bar.All(f => f.Full)) return;
        var character = CharacterPlayerStats(conn);
        int Get(uint key) => character.GetValueOrDefault(key);
        bool precision;
        lock (conn.Monsters) precision = AffectingAuras(conn).Any(a => a.EffectId == "Precision");
        var gains = FlaskCharges.OnCrit(bar, FlaskCharges.CritSources(Get, precision), conn.FlaskChargeCarry, now, AreaRng(conn).Next);
        if (gains.Count > 0)
            await GainFlaskCharges(conn, (f, _) => gains.GetValueOrDefault(f.Id), "critical strike");
    }

    // An accepted enemy hit, including a blocked hit: "Gain # Charges when you are Hit by an Enemy" (local_flask_gain_X_charges_when_hit).
    public static Task GainFlaskChargesWhenHit(Player conn) =>
        GainFlaskCharges(conn, (_, local) => local.GetValueOrDefault(Stat.LocalFlaskGainXChargesWhenHit), "hit");

    // "Flasks gain # charges every 3 seconds" (FlaskCharges.PerThreeSeconds: every flask, utility, life, life on low life,
    // mana), every 3 s of the area's frames (a pause holds them) while the player's updates run.
    static void StartFlaskChargeRegen(Player conn, PlayerUpdatesState updates) =>
        EveryAsPlayer(conn, 3000, () => PlayerUpdatesCurrent(conn, updates), conn =>
        {
            if (conn.PlayerLife == 0) return Task.CompletedTask;
            var character = CharacterPlayerStats(conn);
            int Get(uint key) => character.GetValueOrDefault(key);
            bool lowLife = FlaskRecovery.LowLife(conn.PlayerLife, conn.PlayerMaxLife);
            return GainFlaskCharges(conn, (f, _) => ItemsFor(conn).ByHash(f.Item.TypeHash) is not { } t ? 0
                : FlaskCharges.PerThreeSeconds(Get, life: ItemsFor(conn).FlaskOf(t)?.LifePerUse > 0, mana: ItemsFor(conn).FlaskOf(t)?.ManaPerUse > 0,
                    utility: ItemsFor(conn).UtilityFlaskOf(t) is not null, lowLife),
                "every 3 seconds");
        });

    // Cinderswallow Urn: the ignited corpses consumed since the last time (NoteCorpseConsumedLocked marks it), as a stat
    // presentation.
    static async Task SendConsumedCorpseCharges(Player conn)
    {
        if (Interlocked.Exchange(ref conn.IgnitedCorpsesConsumed, 0) is not (int corpses and > 0)) return;
        Console.WriteLine($"[gateway] {conn.Remote} {corpses} ignited corpse(s) consumed: flask charges");
        await GainFlaskCharges(conn,
            (_, local) => corpses * local.GetValueOrDefault(Stat.LocalFlaskGainXChargesOnConsumingIgnitedCorpse),
            "consumed ignited corpse");
    }

    // A flask put on the flask bar in a hostile area has no charges (FlaskCharges.WhenEquipped).
    public static InventoryItem EmptyIfEquippedInHostileArea(Player conn, PlayerInventories items, InventoryItem placed)
    {
        var equipped = FlaskCharges.WhenEquipped(placed.Item, safeArea: InTown(conn) || HideoutCatalog.ByArea(conn.Layout.Hash) is not null);
        return equipped == placed.Item ? placed : items.Change(PlayerInventories.Flask, placed.Id, _ => equipped) ?? placed;
    }
}
