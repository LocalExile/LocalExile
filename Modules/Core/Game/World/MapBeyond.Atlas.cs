using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Game;
using static Program;

/// <summary>
/// Beyond drops (Tainted currency, faction notables, corrupted drops), experience scaling, Scarab of Haemophilia rare
/// mod stealing (20 s), and Demonic Power's map-instance boss buffs (K'Tash, Ghorr, Beidat).
/// </summary>
public static partial class MapBeyond
{
    const double HaemophiliaSeconds = 20;
    const string BossBuffsKey = "Beyond/boss-buffs";
    // In 3.29 BuffTemplates row 634 (BeyondBossBuff2), damage_taken_+%_final_from_beyond_boss_buff carries +10 even
    // though BuffDefinitions row 2423 (Ghorr's Magnificence) states "taking less Damage" (GGG fixed this sign typo in
    // 3.30.0). The buff's stats carry it as -10, a final damage-taken stat handed over to IncomingResourceDefences.
    public const uint BossLessDamageStat = Stat.DamageTakenPlusPctFinalFromBeyondBossBuff;

    // ---- Beyond monster kills: Haemophilia & Demonic Power ----

    static void OnBeyondMonsterKilled(KillContext kill)
    {
        var player = kill.Player;
        if (player.PlayerLife == 0) return;

        // Beyond Scarab of Haemophilia: X% chance for rare Beyond monsters to grant their mods to the killer for 20 s.
        if (kill.Monster.Rarity == MonsterRarity.Rare && KilledByPlayerOrOwned(kill)
            && AreaStat(player, Stat.MapRareBeyondMonsterGivesModsToKillerChancePct) is int stealChance and > 0
            && kill.Monster.Mods.Affixes.Any()
            && Random.Shared.Next(100) < stealChance)
        {
            player.StolenMods.Steal(kill.Monster.Mods, ItemsFor(player).Mods, AreaTime(player), HaemophiliaSeconds);
        }

        // Demonic Power (Atlas notable / Idol): in 3.24–3.29, slaying a Beyond boss grants its Magnificence buff for
        // the rest of the map instance (BuffTemplates __col7 = 0 / __col8 = 1, BuffDefinitions CancelOnDeath = 0; the
        // 60 s timer was only introduced in 3.30.0).
        if (BeyondCatalog.ByBossVariety(kill.VarietyId) is { BossBuffRow: > 0 } bossFaction
            && AreaStat(player, Stat.MapBeyondBossGrantsSpecialBuffOnDeath) > 0)
        {
            GrantBossBuff(player, bossFaction);
        }
    }

    // ---- Demonic Power map-instance boss buffs ----

    sealed class InstanceBossBuffStore
    {
        public readonly Dictionary<string, List<int>> ByCharacter = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class BossBuffState
    {
        public readonly List<(int FactionRow, ushort BuffInstance)> Active = [];
        public Dictionary<uint, int>? Stats;
        public volatile bool Pending;
    }

    static readonly Store<InstanceState, InstanceBossBuffStore> InstanceBossBuffs = new();
    static readonly Store<Player, BossBuffState> BossBuffs = new();

    static InstanceBossBuffStore LoadInstanceBossBuffs(InstanceState instance)
    {
        return InstanceBossBuffs.GetValue(instance, static inst =>
        {
            var store = new InstanceBossBuffStore();
            if (inst.ContentState.TryGetValue(BossBuffsKey, out string? saved)
                && JsonSerializer.Deserialize<Dictionary<string, List<int>>>(saved) is { } parsed)
            {
                foreach (var (name, rows) in parsed)
                    if (rows is { Count: > 0 })
                        store.ByCharacter[name] = [.. rows.Distinct()];
            }
            return store;
        });
    }

    static void GrantBossBuff(Player player, BeyondCatalog.Faction faction)
    {
        string character = CharacterName(player);
        if (player.Instance is { } instance)
        {
            var store = LoadInstanceBossBuffs(instance);
            lock (store)
            {
                if (!store.ByCharacter.TryGetValue(character, out var rows))
                    store.ByCharacter[character] = rows = [];
                if (!rows.Contains(faction.Row))
                {
                    rows.Add(faction.Row);
                    instance.ContentState[BossBuffsKey] = JsonSerializer.Serialize(store.ByCharacter);
                }
            }
        }

        var state = BossBuffs.GetValue(player, _ => new());
        bool added = false;
        lock (state)
        {
            if (!state.Active.Any(b => b.FactionRow == faction.Row))
            {
                state.Active.Add((faction.Row, 0));
                state.Pending = true;
                RebuildBuffCacheLocked(state);
                added = true;
            }
        }
        if (added)
            Console.WriteLine($"[beyond] {character} gains {faction.Id} boss buff ({faction.BossBuffRow}) for this map instance");
    }

    static async Task RestoreBossBuffs(Player player)
    {
        if (player.Instance is not { } instance) return;
        if (!InstanceBossBuffs.TryGetValue(instance, out var store) && !instance.ContentState.ContainsKey(BossBuffsKey))
            return;
        store ??= LoadInstanceBossBuffs(instance);
        int[]? savedRows = null;
        string character = CharacterName(player);
        lock (store)
            if (store.ByCharacter.TryGetValue(character, out var rows) && rows.Count > 0)
                savedRows = [.. rows];
        if (savedRows is null) return;

        var state = BossBuffs.GetValue(player, _ => new());
        lock (state)
        {
            bool changed = false;
            foreach (int row in savedRows)
            {
                if (state.Active.Any(b => b.FactionRow == row)) continue;
                state.Active.Add((row, 0));
                changed = true;
            }
            if (changed)
            {
                state.Pending = true;
                RebuildBuffCacheLocked(state);
            }
        }
        await SyncBossBuffs(player, state);
    }

    static void RebuildBuffCacheLocked(BossBuffState state)
    {
        var stats = new Dictionary<uint, int>();
        foreach (var (row, _) in state.Active)
        {
            if (BeyondCatalog.ByRow(row) is not { } faction) continue;
            foreach (var (key, value) in faction.BossBuffStats)
            {
                // The template's +10 means 10% less damage taken (see BossLessDamageStat).
                int effective = key == BossLessDamageStat ? -Math.Abs(value) : value;
                stats[key] = stats.GetValueOrDefault(key) + effective;
            }
        }
        state.Stats = stats;
    }

    static Task TickBossBuffs(Player player) =>
        BossBuffs.TryGetValue(player, out var state) && state.Pending ? SyncBossBuffs(player, state) : Task.CompletedTask;

    static async Task SyncBossBuffs(Player player, BossBuffState state)
    {
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent || !player.Connection.Connected) return;
        List<(ushort Instance, ushort Row)>? add = null;
        lock (state)
        {
            state.Pending = false;
            for (int i = 0; i < state.Active.Count; i++)
            {
                var (row, inst) = state.Active[i];
                if (inst != 0) continue;
                if (BeyondCatalog.ByRow(row) is { BossBuffRow: > 0 } faction)
                {
                    inst = player.NextBuff++;
                    (add ??= []).Add((inst, faction.BossBuffRow));
                    state.Active[i] = (row, inst);
                }
            }
        }
        if (add is null) return;
        foreach (var (inst, row) in add)
            // Omit u68 so the client defaults duration to +infinity (map-instance buff with no countdown timer).
            await SendWorldRecord(player, player.Id,
                InstanceEntities.AddBuff(player.Id, inst, row, player.Id),
                $"beyond: boss buff {row}");
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        await SendPlayerStatSnapshot(player,
            () => PlayerStatsOffFrames(player), null);
    }

    /// <summary>GameEvents.PlayerStatSources: active Demonic Power boss buffs. Zero allocations when inactive.</summary>
    public static Dictionary<uint, int> PlayerStats(Player player, Dictionary<uint, int> stats)
    {
        if (!BossBuffs.TryGetValue(player, out var state)) return stats;
        Dictionary<uint, int>? own;
        lock (state)
        {
            if (state.Active.Count == 0) return stats;
            if (state.Stats is null) RebuildBuffCacheLocked(state);
            own = state.Stats;
        }
        if (own is null || own.Count == 0) return stats;
        foreach (var (key, value) in own) stats[key] = stats.GetValueOrDefault(key) + value;
        return stats;
    }

    // ---- Drops & Experience ----

    /// <summary>Whether drops from <paramref name="varietyId"/> in <paramref name="player"/>'s area drop corrupted
    /// (Beyond Scarab of Corruption: <c>map_beyond_demon_corrupted_drops</c>). Fast prefix check for non-Beyond kills.</summary>
    public static bool CorruptsDrops(Player player, string? varietyId) =>
        BeyondCatalog.IsBeyondVariety(varietyId) && AreaStat(player, Stat.MapBeyondDemonCorruptedDrops) > 0;

    /// <summary>GameEvents.Drops: Tainted currency, faction Atlas notables, and Beyond quantity / extra currency
    /// modifiers. Early-outs in one string prefix check for non-Beyond kills.</summary>
    public static void Drops(DropRoll drop)
    {
        if (!BeyondCatalog.IsBeyondVariety(drop.Monster.VarietyId)) return;
        var player = drop.Player;
        var rng = Random.Shared;
        int level = Math.Clamp(drop.Monster.Level, 1, 100);
        bool isBoss = BeyondCatalog.ByBossVariety(drop.Monster.VarietyId) is not null;

        // Legacy / Idol Beyond quantity modifiers.
        if (AreaStat(player, Stat.MapBeyondMonsterItemQuantityPlusPct) is int monsterQty and not 0)
            drop.Quantity *= Math.Max(0, 100 + monsterQty) / 100.0;
        if (isBoss && AreaStat(player, Stat.MapBeyondBossItemQuantityPlusPctFinal) is int bossQty and not 0)
            drop.Quantity *= Math.Max(0, 100 + bossQty) / 100.0;

        // Tainted currency drops.
        int taintedInc = AreaStat(player, Stat.MapBeyondMonsterTaintedCurrencyQuantityPlusPct)
            + AreaStat(player, Stat.TaintedCurrencyChancePlusPct)
            + (isBoss ? AreaStat(player, Stat.MapBeyondBossTaintedCurrencyQuantityPlusPct) : 0);
        double taintedFactor = Math.Max(0, 100 + taintedInc) / 100.0;
        var items = ItemsFor(player);
        if (isBoss)
        {
            // shortcut: server estimate — poewiki confirms Beyond bosses guarantee multiple Tainted currency drops,
            // but GGG does not publish exact drop-count bounds.
            double expected = rng.Next(2, 5) * taintedFactor;
            int rolls = (int)expected + (rng.NextDouble() < expected - (int)expected ? 1 : 0);
            if (taintedFactor > 0) rolls = Math.Max(1, rolls);
            for (int i = 0; i < rolls; i++)
                if (BeyondCatalog.PickTainted(items, level, rng) is { } tainted)
                    drop.Stacks.Add((tainted, 1));
        }
        else
        {
            // shortcut: server estimates for non-boss Tainted currency base drop chances (exact retail rates are server-side).
            double baseChance = drop.Monster.Rarity switch
            {
                MonsterRarity.Rare => 12.0,
                MonsterRarity.Magic => 4.0,
                _ => 1.5,
            };
            double chance = baseChance * taintedFactor;
            int rolls = (int)(chance / 100.0) + (rng.NextDouble() * 100.0 < chance % 100.0 ? 1 : 0);
            for (int i = 0; i < rolls; i++)
                if (BeyondCatalog.PickTainted(items, level, rng) is { } tainted)
                    drop.Stacks.Add((tainted, 1));
        }

        // Faction Atlas notable / Idol drop bonuses (Swarming Hive, Voracious Throng, Pale Clarion).
        // shortcut: server estimates for each faction's base divination-card / unique / basic-currency roll chances
        // (retail scales the underlying monster drop tables by the *_+%_final stat without published base rates).
        int faction = BeyondCatalog.FactionOfVariety(drop.Monster.VarietyId);
        if (faction == 0 && AreaStat(player, Stat.MapBeyondDivinationCardQuantityPlusPctFinalFromDemonFaction) is int cardPct and > 0)
        {
            double baseCard = isBoss ? 35.0 : drop.Monster.Rarity switch { MonsterRarity.Rare => 12.0, MonsterRarity.Magic => 6.0, _ => 3.0 };
            if (rng.NextDouble() * 100.0 < baseCard * cardPct / 100.0 && DropsFor(player).Card(rng, level) is { } card)
                drop.Made.Add(card.Item);
        }
        else if (faction == 1 && AreaStat(player, Stat.MapBeyondUniqueItemQuantityPlusPctFinalFromFleshFaction) is int uniquePct and > 0)
        {
            double baseUnique = isBoss ? 20.0 : drop.Monster.Rarity switch { MonsterRarity.Rare => 5.0, MonsterRarity.Magic => 1.5, _ => 0.5 };
            if (rng.NextDouble() * 100.0 < baseUnique * uniquePct / 100.0 && RollUnique(player, level, rng) is { } unique)
                drop.Made.Add(unique);
        }
        else if (faction == 2 && AreaStat(player, Stat.MapBeyondBasicCurrencyQuantityPlusPctFinalFromPaleFaction) is int curPct and > 0)
        {
            double baseCur = isBoss ? 100.0 : drop.Monster.Rarity switch { MonsterRarity.Rare => 40.0, MonsterRarity.Magic => 20.0, _ => 10.0 };
            double chance = baseCur * curPct / 100.0;
            int rolls = (int)(chance / 100.0) + (rng.NextDouble() * 100.0 < chance % 100.0 ? 1 : 0);
            for (int i = 0; i < rolls; i++)
                if (PickBasicCurrency(items, level, rng) is { } cur)
                    drop.Stacks.Add((cur, 1));
        }

        // Legacy / Idol: rare Beyond monsters drop X additional basic currency items.
        if (drop.Monster.Rarity == MonsterRarity.Rare
            && AreaStat(player, Stat.MapRareBeyondMonstersDropXAdditionalCurrencyItems) is int extraCur and > 0)
        {
            for (int i = 0; i < extraCur; i++)
                if (PickBasicCurrency(items, level, rng) is { } cur)
                    drop.Stacks.Add((cur, 1));
        }
    }

    static Item? RollUnique(Player player, int level, Random rng)
    {
        var catalog = ItemsFor(player);
        var uniques = UniquesFor(player);
        var pool = uniques.All.Where(u => !u.EncounterOnly && catalog.Find(u.BaseTypeId) is { } t
            && t.DropLevel <= level && ItemDrops.Droppable(t)).ToArray();
        if (DropsFor(player).PickUnique(pool, rng) is not { } def || uniques.Create(def, rng) is not { } item) return null;
        return item with { Identified = false };
    }

    static string? PickBasicCurrency(ItemCatalog items, int level, Random rng)
    {
        double total = 0;
        for (int i = 0; i < ItemDrops.Currency.Length; i++)
            if (items.Find(ItemDrops.Currency[i].Id) is { Supported: true } t && t.DropLevel <= level)
                total += ItemDrops.Currency[i].Weight;
        if (total <= 0) return null;
        double roll = rng.NextDouble() * total;
        for (int i = 0; i < ItemDrops.Currency.Length; i++)
            if (items.Find(ItemDrops.Currency[i].Id) is { Supported: true } t && t.DropLevel <= level
                && (roll -= ItemDrops.Currency[i].Weight) < 0)
                return ItemDrops.Currency[i].Id;
        return null;
    }

    /// <summary>GameEvents.Experience: <c>map_beyond_monster_experience_+%</c> on Beyond kills.</summary>
    public static void Experience(ExperienceGain gain)
    {
        if (!BeyondCatalog.IsBeyondVariety(gain.Monster.VarietyId)) return;
        int inc = AreaStat(gain.Player, Stat.MapBeyondMonsterExperiencePlusPct);
        if (inc != 0)
            gain.Experience = (uint)Math.Clamp(gain.Experience * Math.Max(0, 100 + inc) / 100.0, 0, uint.MaxValue);
    }
}
