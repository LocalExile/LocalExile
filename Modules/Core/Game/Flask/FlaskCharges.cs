using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

public static partial class Game
{
    static int MapFlaskChargeGain(Player player) => MapModsOf(player)?.Get(Stat.MapPlayerChargesGainedPlusPct) ?? 0;

    // Apply the flask's area-entry rule before publishing the inventory. An entry reset takes priority over a refill.
    static void SettleAreaEntryFlasks(Player player, PlayerInventories inventory)
    {
        bool refill = LeagueFor(player).Rules.TownRefillsFlasks && (InTown(player) || HideoutCatalog.ByArea(player.Layout.Hash) is not null);
        var catalog = ItemsFor(player);
        int emptied = 0, refilled = 0;
        foreach (var flask in inventory.Items(PlayerInventories.Flask))
        {
            if (catalog.ByHash(flask.Item.TypeHash) is not { } type || catalog.FlaskChargesOf(type) is not { } baseUse) continue;
            var local = ItemModStats.Local(flask.Item, catalog.Mods);
            bool empty = local.GetValueOrDefault(Stat.LocalFlaskLoseAllChargesOnEnteringNewArea) != 0;
            int current = flask.Item.Charges;
            int requested = empty ? 0 : refill ? Math.Max(current, FlaskCharges.Of(baseUse, local).MaxCharges) : current;
            if (requested == current || inventory.ChangeUnsaved(PlayerInventories.Flask, flask.Id,
                item => item with { Charges = requested }) is null) continue;
            if (empty) emptied++; else refilled++;
        }
        if (emptied == 0 && refilled == 0) return;
        inventory.SaveUnsaved();
        Console.WriteLine($"[gateway] {player.Connection.Remote} area entry: {emptied} flasks emptied, {refilled} flasks refilled");
    }

    // A change of the flask bar and its S→C 0xbf as one step under the connection's flask-bar gate: the crit, hit, kill and
    // every-3-seconds gains and the flask uses run on their own tasks, and the client must see their revisions in order,
    // each with the flask's latest charges. `change` makes the change and returns what to send (removed ids and the changed
    // items), or null for nothing.
    public static async Task ChangeFlaskBar(Player player,
        Func<PlayerInventories, (uint[] Removed, List<InventoryItem> Changed)?> change, Func<List<InventoryItem>, string> log,
        bool retainOutput = false)
    {
        await player.FlaskBarGate.WaitAsync();
        try
        {
            byte[] packet;
            string message;
            if (!CurrentGameWork || HardcoreProgressBlocked(player)) return;
            var items = ItemsOf(player);
            var before = items.Items(PlayerInventories.Flask).ToDictionary(i => i.Id);
            int outputAt = Automation(player).Packets.Count;
            if (change(items) is not { } done || done.Changed.Count == 0) return;
            foreach (var changed in done.Changed)
                if (before.TryGetValue(changed.Id, out var prevItem) && changed.Item.Charges > prevItem.Item.Charges
                    && ItemsFor(player).ByHash(changed.Item.TypeHash) is { } flaskType
                    && ItemsFor(player).FlaskChargesOf(flaskType) is { } baseCharges
                    && changed.Item.Charges >= FlaskCharges.Of(baseCharges, ItemModStats.Local(changed.Item, ItemsFor(player).Mods)).MaxCharges)
                    QueueFlaskTrigger(player, FlaskEnchantments.Trigger.Full, changed.Id, changed.X);
            bool chargesOnly = done.Removed.Length == done.Changed.Count
                && done.Changed.All(c => Array.IndexOf(done.Removed, c.Id) >= 0
                    && before.TryGetValue(c.Id, out var prev)
                    && c.X == prev.X && c.Y == prev.Y
                    && c.Item.Components.Contains("Charges")
                    && c.Item.Charges != prev.Item.Charges
                    && (c.Item with { Charges = prev.Item.Charges }) == prev.Item);
            if (chargesOnly)
            {
                var chargeBytes = new List<byte>(done.Changed.Count * 10);
                foreach (var changed in done.Changed)
                    chargeBytes.AddRange(InstanceItems.FlaskCharges(changed.Id, (uint)Math.Max(0, changed.Item.Charges)));
                packet = [.. chargeBytes];
            }
            else
            {
                packet = InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Flask),
                    items.NextRevision(PlayerInventories.Flask), done.Removed, done.Changed);
            }
            message = log(done.Changed);
            if (retainOutput) Automation(player).Packets.Insert(outputAt, new(packet, message));
            else if (player.ProjectileRuntime is { Active: true } session)
            {
                session.Packet(packet, message);
                return;
            }
            if (retainOutput) await PublishFlaskChanges(player);
            else await Send(player, packet, message);
        }
        finally
        {
            player.FlaskBarGate.Release();
        }
    }

    // A use's charges taken from the flask (still in its cell, with enough charges: checked again under the gate, as a
    // gain or another use may have run meanwhile) and sent (S→C 0xbf): the spent flask, or null when there were too few.
    public static async Task<InventoryItem?> SpendFlask(Player player,
        InventoryItem flask, Flasks.Flask charges, uint slot, Func<bool>? still = null, Action<InventoryItem>? accepted = null)
    {
        InventoryItem? spent = null;
        await ChangeFlaskBar(player, items =>
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
                double now = AreaTime(player);
                if (player.EntryProtection.End(now, player.Walk.Position(now), action: true) is ushort)
                    player.Area.Buffs.Clear(player.Id, GracePeriod.BuffRow, player.Id);
                accepted(done);
                ApplyEquipmentFlaskCleansesLocked(player, now);
            }
            return ([done.Id], [done]);
        }, changed => $"0x00c6 flask {slot}: {changed[0].Item.Charges}/{charges.MaxCharges} charges", retainOutput: accepted is not null);
        if (spent is not null && accepted is null) await EndGracePeriod(player, action: true);
        return spent;
    }

    // Flask charge gains (13-items.md §8b, FlaskCharges): each flask in the bar that `chargesFor` gives something gets it
    // times its gain multiplier (the character's increased flask charges gained, the flask's increased charge recovery),
    // whole charges now and the rest kept, up to its maximum with its mods; the changed flasks in one S→C 0xbf, saved with
    // the next save. The character's stats are only worked out when some flask that is not full gains.
    public static async Task GainFlaskCharges(Player player,
        Func<InventoryItem, IReadOnlyDictionary<uint, int>, double> chargesFor, string why)
    {
        var catalog = ItemsFor(player);
        List<(InventoryItem Flask, IReadOnlyDictionary<uint, int> Local, Flasks.Flask Charges, double Gain)>? gaining = null;
        foreach (var flask in ItemsOf(player).Items(PlayerInventories.Flask))
        {
            if (catalog.ByHash(flask.Item.TypeHash) is not { } type || catalog.FlaskChargesOf(type) is not { } baseCharges) continue;
            var local = FlaskLocalStats(player, flask.Item);
            var charges = FlaskCharges.Of(baseCharges, local);
            if (flask.Item.Charges >= charges.MaxCharges) continue;
            double gain = chargesFor(flask, local);
            if (gain > 0) (gaining ??= []).Add((flask, local, charges, gain));
        }
        if (gaining is null) return;
        int areaIncrease = MapFlaskChargeGain(player);
        var character = PlayerStatValues(player);
        int Get(uint key) => character.GetValueOrDefault(key);
        await ChangeFlaskBar(player, items =>
        {
            List<InventoryItem> changed = [];
            foreach (var (flask, local, charges, gain) in gaining)
            {
                if (!CanGainFlaskCharges(player, flask, local, character)) continue;
                int whole = player.FlaskChargeCarry.Take(flask.Id, gain * FlaskCharges.GainMultiplier(local, Get, areaIncrease));
                if (whole > 0 && items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, i => Flasks.Gain(i, charges, whole)) is { } done)
                {
                    changed.Add(done);
                }
            }
            return ([.. changed.Select(c => c.Id)], changed);
        }, changed => $"0x00c6 flasks: {string.Join(", ", changed.Select(c => $"{c.X}: {c.Item.Charges}"))} charges ({why})");
    }

    public static bool CanGainFlaskCharges(Player player, InventoryItem flask) =>
        CanGainFlaskCharges(player, flask, null, null);

    internal static bool CanGainFlaskCharges(Player player, InventoryItem flask,
        IReadOnlyDictionary<uint, int>? local, IReadOnlyDictionary<uint, int>? playerStats)
    {
        var activeEffect = player.UtilityFlaskEffects.Of(flask.Id);
        local ??= FlaskLocalStats(player, flask.Item);
        bool running = activeEffect is not null && activeEffect.Ends > AreaTime(player);
        if (FlaskEnchantments.BlocksGain(local, running)) return false;
        var rules = LeagueFor(player).Rules;
        var catalog = ItemsFor(player);
        if (rules.UtilityFlaskChargesWhileActive || catalog.ByHash(flask.Item.TypeHash) is not { } type
            || catalog.UtilityFlaskOf(type) is null) return true;
        bool exception = rules.UtilityFlaskExceptionStat is { } stat && Stats.Key(stat) is uint key
            && (playerStats ?? PlayerStatValues(player)).GetValueOrDefault(key) > 0;
        return LeagueGameplay.CanGainUtilityCharges(!rules.UtilityFlaskChargesWhileActive, running, exception);
    }

    // A kill: 1 / 3.5 / 6 / 11 charges to every flask by the monster's rarity (FlaskCharges.KillCharges), or with
    // Game_FLASK_CHARGES=full every flask full.
    public static Task GainKillCharges(Player player, MonsterRarity rarity, string why) =>
        GainKillCharges(player, rarity, why, 0);

    static Task GainKillCharges(Player player, MonsterRarity rarity, string why, int grantedPct)
    {
        if (FlaskCharges.FullShortcut) return GainFlaskCharges(player, int.MaxValue, why);
        double? killGain = null;
        return GainFlaskCharges(player, (_, _) =>
        {
            if (killGain is double computed) return computed;
            var stats = PlayerStatValues(player);
            long finalPct = Math.Max(0, 100L + stats.GetValueOrDefault(Stat.FlaskChargesGainedFromKillsPlusPctFinalFromUnique));
            double gain = FlaskCharges.KillCharges(rarity, ItemsFor(player).Mods, grantedPct) * finalPct / 100d;
            killGain = gain;
            return gain;
        }, why);
    }

    // A critical strike the player dealt (a hit that landed): each flask with "#% chance to gain a Flask Charge when you deal
    // a Critical Strike" (local_recharge_on_crit_%) rolls for one charge, and the character's chance and sure charges
    // (FlaskCharges.CritSources) go to the first flask that is not full. One cooldown for all of them
    // (FlaskCharges.CritCooldown), looked at before anything else.
    public static async Task GainFlaskChargesOnCrit(Player player)
    {
        double now = AreaTime(player);
        if (!player.FlaskChargeCarry.CritReady(now)) return;
        var bar = CritFlaskBar(player);
        if (bar.All(f => f.Full)) return;
        var character = CharacterPlayerStats(player);
        int Get(uint key) => character.GetValueOrDefault(key);
        bool precision;
        precision = AffectingAuras(player).Any(a => a.EffectId == "Precision");
        var gains = FlaskCharges.OnCrit(bar, FlaskCharges.CritSources(Get, precision), player.FlaskChargeCarry, now, AreaRng(player).Next);
        if (gains.Count > 0)
            await GainFlaskCharges(player, (f, _) => gains.GetValueOrDefault(f.Id), "critical strike");
    }

    // An accepted enemy hit, including a blocked hit: "Gain # Charges when you are Hit by an Enemy" (local_flask_gain_X_charges_when_hit).
    public static Task GainFlaskChargesWhenHit(Player player) =>
        GainFlaskCharges(player, (_, local) => local.GetValueOrDefault(Stat.LocalFlaskGainXChargesWhenHit), "hit");

    // "Flasks gain # charges every 3 seconds" (FlaskCharges.PerThreeSeconds: every flask, utility, life, life on low life,
    // mana), every 3 s of the area's frames (a pause holds them) while the player's updates run.
    static void StartFlaskChargeRegen(Player player, PlayerUpdatesState updates)
    {
        bool Current() => PlayerUpdatesCurrent(player, updates);
        EveryAsPlayer(player, 3000, Current, GainPeriodicFlaskCharges);
        EveryAsPlayer(player, 1000, Current, GainUniqueHitFlaskCharges, () => player.PlayerLife > 0
            && player.RecentEvents.Happened(StatConditions.HitUniqueEnemy, AreaTime(player)));
    }

    static Task GainUniqueHitFlaskCharges(Player player)
    {
        int charges = CharacterPlayerStats(player).GetValueOrDefault(Stat.GainFlaskChargesEverySecondIfHitUniqueEnemyRecently);
        return charges > 0 ? GainFlaskCharges(player, charges, "recent unique enemy hit") : Task.CompletedTask;
    }

    internal static Task GainPeriodicFlaskCharges(Player player)
    {
        if (player.PlayerLife == 0) return Task.CompletedTask;
        var character = CharacterPlayerStats(player);
        int alchemist = player.Summons.Fighters.Values.Any(f => f.Definition.Kind == "Spectre"
            && f.Monster.Variety?.Id == "Metadata/Monsters/LeagueAzmeri/SpecialCorpses/FlaskloverHigh"
            && !f.Monster.Dead && f.Monster.Life > 0 && GolemCurrent(player, f)) ? 1 : 0;
        int ryslatha = !player.RecentEvents.Happened(StatConditions.UsedLifeFlask, AreaTime(player))
            ? character.GetValueOrDefault(Stat.LifeFlasksGainXChargesEvery3SecondsIfYouHaveNotUsedALifeFlaskRecently) : 0;
        int Get(uint key) => character.GetValueOrDefault(key) + (key == Stat.LifeFlaskChargesRecoveredPer3Seconds ? alchemist + ryslatha : 0);
        bool lowLife = FlaskRecovery.LowLife(player.PlayerLife, player.PlayerMaxLife);
        return GainFlaskCharges(player, (f, _) => ItemsFor(player).ByHash(f.Item.TypeHash) is not { } t ? 0
            : FlaskCharges.PerThreeSeconds(Get, life: ItemsFor(player).FlaskOf(t)?.LifePerUse > 0, mana: ItemsFor(player).FlaskOf(t)?.ManaPerUse > 0,
                utility: ItemsFor(player).UtilityFlaskOf(t) is not null, lowLife),
            "every 3 seconds");
    }

    // Cinderswallow Urn: the ignited corpses consumed since the last time (NoteCorpseConsumedLocked marks it), as a stat
    // presentation.
    static async Task SendConsumedCorpseCharges(Player player)
    {
        if (Interlocked.Exchange(ref player.IgnitedCorpsesConsumed, 0) is not (int corpses and > 0)) return;
        Console.WriteLine($"[gateway] {player.Connection.Remote} {corpses} ignited corpse(s) consumed: flask charges");
        await GainFlaskCharges(player,
            (_, local) => corpses * local.GetValueOrDefault(Stat.LocalFlaskGainXChargesOnConsumingIgnitedCorpse),
            "consumed ignited corpse");
    }

    // A flask put on the flask bar in a hostile area has no charges (FlaskCharges.WhenEquipped).
    public static InventoryItem EmptyIfEquippedInHostileArea(Player player, PlayerInventories items, InventoryItem placed)
    {
        var equipped = FlaskCharges.WhenEquipped(placed.Item, safeArea: InTown(player) || HideoutCatalog.ByArea(player.Layout.Hash) is not null);
        return equipped == placed.Item ? placed : items.Change(PlayerInventories.Flask, placed.Id, _ => equipped) ?? placed;
    }
}
