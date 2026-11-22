using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// The Torment stats of the Atlas tree, the Idols and the map's own mods, besides the spirit count the league roll already
/// reads (map_additional_tormented_spirit_chance_%, map_tormented_spirit_chance_%, map_spawn_extra_torment_spirits) and
/// the duration Drive reads. Their wording is the client's atlas, atlas relic and map stat descriptions.
/// </summary>
public static partial class MapTorment
{
    const string SeanceKey = "Torment/seance", RandomUniqueKey = "Torment/random-unique";
    // Our own values: how many spirits surround a map boss ("several"), how far a possessed rare's minions are from it
    // (the server keeps no pack membership on a monster), and how far a dug-up monster rises from the kill.
    const int SurroundCount = 3, MinionRange = 25, DigRange = 6;
    // Speaker of the Dead: "can Possess Players for 20 seconds"; poewiki: each spirit touches up to 40 monsters.
    const double PlayerPossessionSeconds = 20;
    const int PlayerTouchLimit = 40;

    static bool SpeakerOfTheDead(Player player) => AreaStat(player, Stat.AtlasKeystoneSpeakerOfTheDead) > 0
        || AreaStat(player, Stat.MapTormentedSpiritsPossessPlayersInsteadOfMonsters) > 0;

    // The unusual spirits a map asks for by name (the Atlas's sextant-era map_contains_additional_tormented_* stats).
    static readonly (uint Stat, string Variety)[] NamedSpirits =
    [
        (Stat.MapContainsAdditionalTormentedBetrayers, "Metadata/Monsters/Spirit/TormentedBetrayer"),
        (Stat.MapContainsAdditionalTormentedGraverobbers, "Metadata/Monsters/Spirit/TormentedFelon"),
        (Stat.MapContainsAdditionalTormentedHeretics, "Metadata/Monsters/Spirit/TormentedTrespasser"),
    ];

    /// <summary>At the area's roll: the spirits a map names, and Seance's rares (<c>map_rare_monsters_possessed_with_touched_minions_*</c>:
    /// "{0}% chance for up to {1} Rare Monsters to be Possessed and their Minions are Touched", the Atlas notable and the Idols).</summary>
    static void RollExtras(Player player, Area area, Random random)
    {
        var cells = MapStrongboxes.Positions(player.Layout, player.Seed ^ Salt ^ 0x4e414d45, NamedSpirits.Sum(n => Math.Max(0, AreaStat(player, n.Stat))));
        int cell = 0;
        foreach (var (stat, variety) in NamedSpirits)
            for (int i = AreaStat(player, stat); i > 0 && cell < cells.Count; i--, cell++)
                if (TormentCatalog.ByVariety(variety) is { } spirit)
                    area.Spirits!.Add(new() { Index = area.Spirits.Count, Row = spirit.Row, X = cells[cell].X, Y = cells[cell].Y });
        int chance = AreaStat(player, Stat.MapRareMonstersPossessedWithTouchedMinionsChancePct);
        int seance = chance > 0 && random.Next(100) < chance ? AreaStat(player, Stat.MapRareMonstersPossessedWithTouchedMinionsNum) : 0;
        if (seance > 0) player.Instance.ContentState[SeanceKey] = seance.ToString();
    }

    /// <summary>Monsters the runner sees for the first time: the Possession scarab's, Seance's
    /// and the memories' possessed rares, the map boss "is Possessed" (Enthralled) or "surrounded by Tormented Spirits"
    /// (Unrelenting Torment), a map's random possessed unique. Returns the cells to put surrounding spirits on.</summary>
    static List<(int X, int Y)> FirstSight(Player player, Area area, double now, ref bool changed)
    {
        var surround = new List<(int X, int Y)>();
        int scarab = AreaStat(player, Stat.Map25PctChanceForRareMonstersToBePossessedUpToXTimes);
        int rareChance = AreaStat(player, Stat.MapRareMonstersPossessedChancePct);
        int seance = player.Instance.ContentState.TryGetValue(SeanceKey, out string? left) && int.TryParse(left, out int n) ? n : 0;
        bool boss = AreaStat(player, Stat.MapBossIsPossessed) > 0;
        int surroundChance = AreaStat(player, Stat.MapBossChanceToBeSurroundedBySpiritsPct);
        bool randomUnique = AreaStat(player, Stat.MapRandomUniqueMonsterIsPossessed) > 0 && !player.Instance.HasState(RandomUniqueKey);
        bool rolls = scarab > 0 || rareChance > 0 || seance > 0 || boss || surroundChance > 0 || randomUnique;
        // Nothing to roll and nothing marked (a kept instance's monsters come back under new ids): no scan.
        bool marked;
        lock (area.Marks) marked = area.Marks.Count > 0;
        if (!rolls && !marked) return surround;
        var pool = TormentCatalog.Pool(AreaLevel(player), AreaStat(player, Stat.MapTormentedSpiritsRareVarieties) > 0);
        rolls &= pool.Count > 0;
        // The map's boss: one of the area's WorldAreas bosses.
        var bosses = Packs.For(player.Layout.Id)?.Bosses ?? [];
        foreach (var (id, m) in player.Area.Monsters)
        {
            if (!Touchable(m) || !area.Checked.Add(id)) continue;
            // A kept monster that is already touched or possessed: its mark again, and nothing more.
            if (Remark(player, area, id, m)) { changed = true; continue; }
            if (!rolls || m.Rarity is not (MonsterRarity.Rare or MonsterRarity.Unique)) continue;
            var random = new Random(unchecked((int)(player.Seed ^ Salt ^ id)));
            int spirits = 0;
            bool minions = false;
            if (m.Rarity == MonsterRarity.Rare)
            {
                // The Possession scarab: "a quarter chance to be Possessed by up to {0} Tormented Spirits", read as a
                // quarter chance for each of the {0} (3 per scarab: more scarabs, more chance and more spirits).
                for (int i = 0; i < scarab; i++) if (random.Next(4) == 0) spirits++;
                if (rareChance > 0 && random.Next(100) < rareChance) spirits++;
                if (seance > 0) { spirits++; minions = true; seance--; }
            }
            else
            {
                bool mapBoss = m.Variety is { } v && bosses.Contains(v.Row);
                if (mapBoss && boss) spirits++;
                if (randomUnique && player.Instance.SetState(RandomUniqueKey)) { spirits++; randomUnique = false; }
                if (mapBoss && surroundChance > 0 && random.Next(100) < surroundChance)
                {
                    var at = m.Walk.Position(now);
                    for (int i = 0; i < SurroundCount; i++)
                    {
                        double angle = Math.Tau * i / SurroundCount;
                        surround.Add(player.Layout.Near(((int)Math.Round(at.X + 12 * Math.Cos(angle)), (int)Math.Round(at.Y + 12 * Math.Sin(angle)))));
                    }
                }
            }
            if (spirits == 0) continue;
            TormentCatalog.Spirit? last = null;
            for (int i = 0; i < spirits; i++)
                if (TormentCatalog.Pick(pool, random, AreaStat(player, Stat.MapRareSpiritsMoreCommon) > 0) is { } spirit)
                    Apply(player, area, id, m, last = spirit, grip: true, now);
            if (minions && last is not null)
            {
                var here = m.Walk.Position(now);
                foreach (var (other, o) in player.Area.Monsters)
                    if (o.Rarity is MonsterRarity.Normal or MonsterRarity.Magic && Touchable(o) && !TormentCatalog.Has(o.Mods, grip: false)
                        && Distance(o.Walk.Position(now), here) <= MinionRange)
                        Apply(player, area, other, o, last, grip: false, now);
            }
            changed = true;
        }
        if (player.Instance.ContentState.ContainsKey(SeanceKey)) player.Instance.ContentState[SeanceKey] = seance.ToString();
        return surround;
    }

    /// <summary>Spirits that come with a map boss (Unrelenting Torment), waiting where they are put.</summary>
    static async Task AddSpirits(Player player, Area area, IReadOnlyList<(int X, int Y)> cells)
    {
        var pool = TormentCatalog.Pool(AreaLevel(player), AreaStat(player, Stat.MapTormentedSpiritsRareVarieties) > 0);
        bool flatten = AreaStat(player, Stat.MapRareSpiritsMoreCommon) > 0;
        foreach (var (x, y) in cells)
        {
            if (TormentCatalog.Pick(pool, Random.Shared, flatten) is not { } pick) continue;
            var s = new Spirit { Index = area.Spirits!.Count, Row = pick.Row, X = x, Y = y };
            area.Spirits.Add(s);
            await Spawn(player, area, s);
        }
        if (cells.Count > 0) Console.WriteLine($"[torment] {cells.Count} spirits surround the map boss");
    }

    // ---- Speaker of the Dead: the spirits possess players, not monsters ----

    /// <summary>A player's possession: which spirits, the seconds left of each, and how many monsters each still touches.</summary>
    sealed class Possession
    {
        public readonly List<(int Row, double Left, int Touches, ushort Buff)> Spirits = [];
        public Dictionary<uint, int>? Stats;
    }
    static readonly Store<Player, Possession> Possessed = new();

    /// <summary>A spirit that reaches a player goes into them, unless they already have its Grip.</summary>
    static Player? PlayerContact(Player player, TormentCatalog.Spirit spirit, (int X, int Y) at)
    {
        foreach (var c in Members(player))
        {
            if (!c.Connection.PlayerSent || c.PlayerLife == 0 || Distance(c.Walk.Position(AreaTime(c)), at) > ContactRange) continue;
            var possession = Possessed.GetValue(c, _ => new());
            lock (possession)
            {
                if (possession.Spirits.Any(p => p.Row == spirit.Row)) continue;
                possession.Spirits.Add((spirit.Row, PlayerPossessionSeconds, PlayerTouchLimit, 0));
                possession.Stats = null;
            }
            Console.WriteLine($"[torment] {CharacterName(c)} possessed by {spirit.Variety} for {PlayerPossessionSeconds} s");
            return c;
        }
        return null;
    }

    /// <summary>Possesses a player from outside a torment area (content that grants a Possession, such as a league
    /// encounter's reward): <paramref name="spiritRow"/>'s spirit, or a random one of the area level's weighted spirits
    /// (not the unusual ones) when null, for <paramref name="seconds"/> (<see cref="double.PositiveInfinity"/>: until death
    /// or leaving the area). The Possessed buff (2113) follows on the player's next tick, the Grip's stats come with it,
    /// and both end at life 0 or on leaving. Monsters are touched only in a torment area (the possession needs its spirit
    /// bookkeeping). Returns the spirit's row, or null when none fits or the player already has that spirit.</summary>
    public static int? PossessPlayer(Player player, int? spiritRow, double seconds)
    {
        if (seconds <= 0) return null;
        TormentCatalog.Spirit? spirit = spiritRow is int row ? TormentCatalog.ByRow(row)
            : TormentCatalog.Pick(TormentCatalog.Pool(AreaLevel(player), unusual: false), Random.Shared)
              ?? TormentCatalog.Pick([.. TormentCatalog.All.Where(s => s.Weight > 0)], Random.Shared);
        if (spirit is null) return null;
        var possession = Possessed.GetValue(player, _ => new());
        lock (possession)
        {
            if (possession.Spirits.Any(p => p.Row == spirit.Row)) return null;
            possession.Spirits.Add((spirit.Row, seconds, PlayerTouchLimit, 0));
            possession.Stats = null;
        }
        Console.WriteLine($"[torment] {CharacterName(player)} possessed by {spirit.Variety} for {(double.IsPositiveInfinity(seconds) ? "the area" : $"{seconds} s")}");
        return spirit.Row;
    }

    /// <summary>The runner: a possessed player touches the normal and magic monsters they reach.</summary>
    static void PlayerTouches(Player player, Area area, double now, ref bool changed)
    {
        foreach (var c in Members(player))
        {
            if (!Possessed.TryGetValue(c, out var possession)) continue;
            var at = c.Walk.Position(AreaTime(c));
            lock (possession)
                for (int i = 0; i < possession.Spirits.Count; i++)
                {
                    var (row, left, touches, buff) = possession.Spirits[i];
                    if (touches <= 0 || TormentCatalog.ByRow(row) is not { } spirit) continue;
                    foreach (var (id, m) in player.Area.Monsters)
                    {
                        if (touches <= 0) break;
                        if (m.Rarity is not (MonsterRarity.Normal or MonsterRarity.Magic) || !Touchable(m)
                            || TormentCatalog.Has(m.Mods, grip: false) || Distance(m.Walk.Position(now), at) > ContactRange) continue;
                        Apply(player, area, id, m, spirit, grip: false, now);
                        touches--;
                        changed = true;
                    }
                    possession.Spirits[i] = (row, left, touches, buff);
                }
        }
    }

    /// <summary>The player's own tick: the buff of each new possession, the ones that end, and the stats again.</summary>
    static async Task TickPossession(Player player, double seconds)
    {
        if (!Possessed.TryGetValue(player, out var possession)) return;
        var add = new List<ushort>();
        var remove = new List<ushort>();
        int needBuffs = 0;
        lock (possession)
        {
            for (int i = possession.Spirits.Count - 1; i >= 0; i--)
            {
                var (row, left, touches, buff) = possession.Spirits[i];
                left -= seconds;
                if (left <= 0 || player.PlayerLife == 0)
                {
                    if (buff != 0) remove.Add(buff);
                    possession.Spirits.RemoveAt(i);
                    possession.Stats = null;
                    continue;
                }
                if (buff == 0) needBuffs++;
                possession.Spirits[i] = (row, left, touches, buff);
            }
            // An empty possession stays in the table until the area is left: a spirit may be going into it right now.
        }
        if (needBuffs > 0)
        {
            var allocated = new ushort[needBuffs];
            for (int i = 0; i < needBuffs; i++) allocated[i] = NextBuffId(player);
            int next = 0;
            lock (possession)
            {
                for (int i = 0; i < possession.Spirits.Count && next < allocated.Length; i++)
                {
                    var (row, left, touches, buff) = possession.Spirits[i];
                    if (buff != 0) continue;
                    buff = allocated[next++];
                    possession.Spirits[i] = (row, left, touches, buff);
                    add.Add(buff);
                }
            }
        }
        if (add.Count == 0 && remove.Count == 0) return;
        foreach (ushort buff in add)
            await SendWorldRecord(player, player.Id,
                InstanceEntities.AddBuff(player.Id, buff, TormentCatalog.PossessedPlayerBuff), "torment: player possessed");
        foreach (ushort buff in remove)
            await SendWorldRecord(player, player.Id, InstanceEntities.RemoveBuff(player.Id, buff), "torment: possession ends");
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        await SendPlayerStatSnapshot(player, () => PlayerStatsOffFrames(player), null);
    }

    /// <summary>Right-click cancellation (C→S 0x004b): ends the possessing spirit whose buff instance is
    /// <paramref name="buffInstance"/>, removing its icon and Grip stats.</summary>
    public static async Task<bool> CancelBuff(Player player, ushort buffInstance)
    {
        if (buffInstance == 0 || !Possessed.TryGetValue(player, out var possession)) return false;
        int spiritRow;
        lock (possession)
        {
            int index = possession.Spirits.FindIndex(p => p.Buff == buffInstance);
            if (index < 0) return false;
            spiritRow = possession.Spirits[index].Row;
            possession.Spirits.RemoveAt(index);
            possession.Stats = null;
        }
        await SendWorldRecord(player, player.Id,
            InstanceEntities.RemoveBuff(player.Id, buffInstance), "torment: possession cancelled");
        using var fresh = new PlayerStatScope(player, fresh: true);
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        await SendPlayerStatSnapshot(player, () => PlayerStatsOffFrames(player), null);
        Console.WriteLine($"[torment] {CharacterName(player)} cancelled possession ({TormentCatalog.ByRow(spiritRow)?.Variety ?? $"#{spiritRow}"})");
        return true;
    }

    /// <summary>The player stat source (GameEvents.PlayerStatSources): each possessing spirit's Possessed buff (35% more
    /// damage, attack, cast and movement speed: TormentPossessed) and its Grip's stats that a player has; the Grip's
    /// monster-only stats (drops, the monster flags) are left out. Nothing when no spirit possesses the player.</summary>
    public static Dictionary<uint, int> PlayerStats(Player player, Dictionary<uint, int> stats)
    {
        if (!Possessed.TryGetValue(player, out var possession)) return stats;
        Dictionary<uint, int> own;
        lock (possession)
        {
            if (possession.Spirits.Count == 0) return stats;
            own = possession.Stats ??= GripStats(possession.Spirits.Select(p => p.Row));
        }
        foreach (var (key, value) in own) stats[key] = stats.GetValueOrDefault(key) + value;
        return stats;
    }

    static ushort NextBuffId(Player player)
    {
        ushort id = player.NextBuff++;
        return id == 0 ? player.NextBuff++ : id;
    }

    static Dictionary<uint, int> GripStats(IEnumerable<int> rows)
    {
        var result = new Dictionary<uint, int>();
        foreach (int row in rows)
        {
            if (TormentCatalog.ByRow(row) is not { } spirit) continue;
            var (_, stats) = TormentCatalog.Roll(spirit.Grip, Random.Shared);
            foreach (var (key, value) in stats)
            {
                string name = Program.Stats.Name(key);
                if (!name.StartsWith("monster_", StringComparison.Ordinal) && !name.Contains("possess", StringComparison.Ordinal)
                    && !name.StartsWith("torment_", StringComparison.Ordinal) && !name.Contains("drop", StringComparison.Ordinal)
                    && !name.StartsWith("from_league_", StringComparison.Ordinal) && name != "cant_touch_this")
                    result[key] = result.GetValueOrDefault(key) + value;
            }
        }
        return result;
    }

    // ---- the Torment Scarab's release and dig ----

    /// <summary>"set free when Possessed Monsters are slain and dig up a Possessed Monster": a rare of the area's packs
    /// rises next to the kill, possessed by the freed spirit.</summary>
    static async Task Dig(Player player, Area area, int row, (int X, int Y) at)
    {
        if (TormentCatalog.ByRow(row) is not { } spirit) return;
        var pool = Packs.For(player.Layout.Id)?.Scattered.SelectMany(p => p.Pack.Entries)
            .Where(e => Monsters.Find(e.Variety.ToString()) is { Supported: true }).ToArray() ?? [];
        if (pool.Length == 0) return;
        int roll = Random.Shared.Next(pool.Sum(e => Math.Max(1, e.Weight)));
        var entry = pool[^1];
        foreach (var e in pool) if ((roll -= Math.Max(1, e.Weight)) < 0) { entry = e; break; }
        var variety = Monsters.Find(entry.Variety.ToString())!;
        var cell = player.Layout.Near((at.X + Random.Shared.Next(-DigRange, DigRange + 1), at.Y + Random.Shared.Next(-DigRange, DigRange + 1)));
        uint id = await SpawnMonster(player, variety, AreaLevel(player), MonsterRarity.Rare, cell.X, cell.Y,
            alone: true, unrisen: false);
        if (id == 0) return;
        if (player.Area.Monsters.TryGetValue(id, out var m))
        {
            area.Checked.Add(id);
            Apply(player, area, id, m, spirit, grip: true, AreaTime(player));
        }
        Console.WriteLine($"[torment] a possessed {variety.Id} is dug up at ({cell.X}, {cell.Y})");
    }
}
