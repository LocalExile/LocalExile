using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Game;
using static Program;

/// <summary>
/// Beyond drops (Tainted currency, faction notables, corrupted drops), experience scaling, Scarab of Haemophilia rare
/// mod stealing (20 s), and Demonic Power's 60-second boss buffs (K'Tash, Ghorr, Beidat).
/// </summary>
public static partial class MapBeyond
{
    const double HaemophiliaSeconds = 20;
    const double BossBuffSeconds = 60;
    const string BossLessDamageStat = "damage_taken_+%_final_from_beyond_boss_buff";

    // ---- Beyond monster kills: Haemophilia & Demonic Power ----

    static void OnBeyondMonsterKilled(KillContext kill)
    {
        var conn = kill.Conn;
        if (conn.PlayerLife == 0) return;

        // Beyond Scarab of Haemophilia: X% chance for rare Beyond monsters to grant their mods to the killer for 20 s.
        if (kill.Monster.Rarity == MonsterRarity.Rare
            && Stat(conn, "map_rare_beyond_monster_gives_mods_to_killer_chance_%") is int stealChance and > 0
            && kill.Monster.Mods.Affixes.Any()
            && Random.Shared.Next(100) < stealChance)
        {
            conn.StolenMods.Steal(kill.Monster.Mods, ItemsFor(conn).Mods, conn.Clock.Elapsed.TotalSeconds, HaemophiliaSeconds);
        }

        // Demonic Power (Atlas notable / Idol): slaying a Beyond boss grants its Magnificence buff for 60 s.
        if (BeyondCatalog.ByBossVariety(kill.VarietyId) is { BossBuffRow: > 0 } bossFaction
            && Stat(conn, "map_beyond_boss_grants_special_buff_on_death") > 0)
        {
            GrantBossBuff(conn, bossFaction);
        }
    }

    // ---- Demonic Power 60-second boss buffs ----

    sealed class BossBuffState
    {
        public readonly List<(int FactionRow, double Left, ushort BuffInstance, bool Refresh)> Active = [];
        public Dictionary<uint, int>? Stats;
        public volatile int LessDamageTakenPct;
    }

    static readonly RuleTable<ClientState, BossBuffState> BossBuffs = new();

    static void GrantBossBuff(ClientState conn, BeyondCatalog.Faction faction)
    {
        var state = BossBuffs.GetValue(conn, _ => new());
        lock (state)
        {
            int existing = state.Active.FindIndex(b => b.FactionRow == faction.Row);
            if (existing >= 0)
            {
                var (_, _, inst, _) = state.Active[existing];
                state.Active[existing] = (faction.Row, BossBuffSeconds, inst, true);
            }
            else
                state.Active.Add((faction.Row, BossBuffSeconds, 0, true));
            RebuildBuffCacheLocked(state);
        }
        Console.WriteLine($"[beyond] {CharacterName(conn)} gains {faction.Id} boss buff ({faction.BossBuffRow}) for {BossBuffSeconds} s");
    }

    static void RebuildBuffCacheLocked(BossBuffState state)
    {
        var stats = new Dictionary<uint, int>();
        int lessTaken = 0;
        foreach (var (row, _, _, _) in state.Active)
        {
            if (BeyondCatalog.ByRow(row) is not { } faction) continue;
            foreach (var (name, value) in faction.BossBuffStats)
            {
                if (name == BossLessDamageStat) { lessTaken += value; continue; }
                if (Program.Stats.Key(name) is uint key)
                    stats[key] = stats.GetValueOrDefault(key) + value;
            }
        }
        state.Stats = stats;
        state.LessDamageTakenPct = lessTaken;
    }

    static async Task TickBossBuffs(ClientState conn, double seconds)
    {
        if (!BossBuffs.TryGetValue(conn, out var state)) return;
        var add = new List<(ushort Instance, ushort Row, float Duration)>();
        var remove = new List<ushort>();
        bool changed = false;
        lock (state)
        {
            for (int i = state.Active.Count - 1; i >= 0; i--)
            {
                var (row, left, inst, refresh) = state.Active[i];
                left -= seconds;
                if (left <= 0 || conn.PlayerLife == 0)
                {
                    if (inst != 0) remove.Add(inst);
                    state.Active.RemoveAt(i);
                    changed = true;
                    continue;
                }
                if (refresh || inst == 0)
                {
                    if (inst != 0) remove.Add(inst);
                    if (BeyondCatalog.ByRow(row) is { BossBuffRow: > 0 } faction)
                    {
                        inst = conn.NextBuff++;
                        add.Add((inst, faction.BossBuffRow, (float)left));
                    }
                    state.Active[i] = (row, left, inst, false);
                    changed = true;
                }
                else
                    state.Active[i] = (row, left, inst, false);
            }
            if (changed) RebuildBuffCacheLocked(state);
        }
        if (!changed) return;
        var w = conn.Wire;
        foreach (ushort buff in remove)
            await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, conn, PlayerId,
                InstanceEntities.RemoveBuff(PlayerId, buff), "beyond: boss buff ends");
        foreach (var (inst, row, duration) in add)
            await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, conn, PlayerId,
                InstanceEntities.AddBuff(PlayerId, inst, row, PlayerId, u68: BitConverter.SingleToUInt32Bits(duration)),
                $"beyond: boss buff {row}");
        lock (conn.Monsters) { conn.Pools = PoolsFor(conn, conn.Pools.Level); conn.ApplyMovementSpeed(); }
        await SendPlayerStatSnapshot(w.Remote, w.Stream, w.Ciphers, conn,
            () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)), null);
    }

    /// <summary>GameEvents.PlayerStatSources: active Demonic Power boss buffs. Zero allocations when inactive.</summary>
    public static Dictionary<uint, int> PlayerStats(ClientState conn, Dictionary<uint, int> stats)
    {
        if (!BossBuffs.TryGetValue(conn, out var state)) return stats;
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

    /// <summary>GameEvents.IncomingDamage: Ghorr's Magnificence 10% less damage taken. Fast-path when inactive.</summary>
    public static int IncomingDamage(ClientState conn, int damage)
    {
        if (damage <= 0 || !BossBuffs.TryGetValue(conn, out var state)) return damage;
        int less = state.LessDamageTakenPct;
        if (less <= 0) return damage;
        return Math.Max(1, (int)Math.Round(damage * Math.Max(0, 100 - less) / 100.0));
    }

    // ---- Drops & Experience ----

    /// <summary>Whether drops from <paramref name="varietyId"/> in <paramref name="conn"/>'s area drop corrupted
    /// (Beyond Scarab of Corruption: <c>map_beyond_demon_corrupted_drops</c>). Fast prefix check for non-Beyond kills.</summary>
    public static bool CorruptsDrops(ClientState conn, string? varietyId) =>
        BeyondCatalog.IsBeyondVariety(varietyId) && Stat(conn, "map_beyond_demon_corrupted_drops") > 0;

    /// <summary>GameEvents.Drops: Tainted currency, faction Atlas notables, and Beyond quantity / extra currency
    /// modifiers. Early-outs in one string prefix check for non-Beyond kills.</summary>
    public static void Drops(DropRoll drop)
    {
        if (!BeyondCatalog.IsBeyondVariety(drop.Monster.VarietyId)) return;
        var conn = drop.Conn;
        var rng = Random.Shared;
        int level = Math.Clamp(drop.Monster.Level, 1, 100);
        bool isBoss = BeyondCatalog.ByBossVariety(drop.Monster.VarietyId) is not null;

        // Legacy / Idol Beyond quantity modifiers.
        if (Stat(conn, "map_beyond_monster_item_quantity_+%") is int monsterQty and not 0)
            drop.Quantity *= Math.Max(0, 100 + monsterQty) / 100.0;
        if (isBoss && Stat(conn, "map_beyond_boss_item_quantity_+%_final") is int bossQty and not 0)
            drop.Quantity *= Math.Max(0, 100 + bossQty) / 100.0;

        // Tainted currency drops.
        int taintedInc = Stat(conn, "map_beyond_monster_tainted_currency_quantity_+%")
            + Stat(conn, "tainted_currency_chance_+%")
            + (isBoss ? Stat(conn, "map_beyond_boss_tainted_currency_quantity_+%") : 0);
        double taintedFactor = Math.Max(0, 100 + taintedInc) / 100.0;
        var items = ItemsFor(conn);
        if (isBoss)
        {
            double expected = rng.Next(2, 5) * taintedFactor;
            int rolls = (int)expected + (rng.NextDouble() < expected - (int)expected ? 1 : 0);
            if (taintedFactor > 0) rolls = Math.Max(1, rolls);
            for (int i = 0; i < rolls; i++)
                if (BeyondCatalog.PickTainted(items, level, rng) is { } tainted)
                    drop.Stacks.Add((tainted, 1));
        }
        else
        {
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
        int faction = BeyondCatalog.FactionOfVariety(drop.Monster.VarietyId);
        if (faction == 0 && Stat(conn, "map_beyond_divination_card_quantity_+%_final_from_demon_faction") is int cardPct and > 0)
        {
            double baseCard = isBoss ? 35.0 : drop.Monster.Rarity switch { MonsterRarity.Rare => 12.0, MonsterRarity.Magic => 6.0, _ => 3.0 };
            if (rng.NextDouble() * 100.0 < baseCard * cardPct / 100.0 && DropsFor(conn).Card(rng, level) is { } card)
                drop.Made.Add(card.Item);
        }
        else if (faction == 1 && Stat(conn, "map_beyond_unique_item_quantity_+%_final_from_flesh_faction") is int uniquePct and > 0)
        {
            drop.Rarity *= (100.0 + uniquePct) / 100.0;
            double baseUnique = isBoss ? 20.0 : drop.Monster.Rarity switch { MonsterRarity.Rare => 5.0, MonsterRarity.Magic => 1.5, _ => 0.5 };
            if (rng.NextDouble() * 100.0 < baseUnique * uniquePct / 100.0 && RollUnique(conn, level, rng) is { } unique)
                drop.Made.Add(unique);
        }
        else if (faction == 2 && Stat(conn, "map_beyond_basic_currency_quantity_+%_final_from_pale_faction") is int curPct and > 0)
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
            && Stat(conn, "map_rare_beyond_monsters_drop_X_additional_currency_items") is int extraCur and > 0)
        {
            for (int i = 0; i < extraCur; i++)
                if (PickBasicCurrency(items, level, rng) is { } cur)
                    drop.Stacks.Add((cur, 1));
        }
    }

    static Item? RollUnique(ClientState conn, int level, Random rng)
    {
        var catalog = ItemsFor(conn);
        var uniques = UniquesFor(conn);
        var pool = uniques.All.Where(u => !u.EncounterOnly && catalog.Find(u.BaseTypeId) is { } t
            && t.DropLevel <= level && ItemDrops.Droppable(t)).ToArray();
        if (DropsFor(conn).PickUnique(pool, rng) is not { } def || uniques.Create(def, rng) is not { } item) return null;
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
        int inc = Stat(gain.Conn, "map_beyond_monster_experience_+%");
        if (inc != 0)
            gain.Experience = (uint)Math.Clamp(gain.Experience * Math.Max(0, 100 + inc) / 100.0, 0, uint.MaxValue);
    }
}
