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

    static bool SpeakerOfTheDead(ClientState conn) => Stat(conn, "atlas_keystone_speaker_of_the_dead") > 0
        || Stat(conn, "map_tormented_spirits_possess_players_instead_of_monsters") > 0;

    // The unusual spirits a map asks for by name (the Atlas's sextant-era map_contains_additional_tormented_* stats).
    static readonly (string Stat, string Variety)[] NamedSpirits =
    [
        ("map_contains_additional_tormented_betrayers", "Metadata/Monsters/Spirit/TormentedBetrayer"),
        ("map_contains_additional_tormented_graverobbers", "Metadata/Monsters/Spirit/TormentedFelon"),
        ("map_contains_additional_tormented_heretics", "Metadata/Monsters/Spirit/TormentedTrespasser"),
    ];

    /// <summary>At the area's roll: the spirits a map names, and Seance's rares (<c>map_rare_monsters_possessed_with_touched_minions_*</c>:
    /// "{0}% chance for up to {1} Rare Monsters to be Possessed and their Minions are Touched", the Atlas notable and the Idols).</summary>
    static void RollExtras(ClientState conn, Area area, Random random)
    {
        var cells = MapStrongboxes.Positions(conn.Layout, conn.Seed ^ Salt ^ 0x4e414d45, NamedSpirits.Sum(n => Math.Max(0, Stat(conn, n.Stat))));
        int cell = 0;
        foreach (var (stat, variety) in NamedSpirits)
            for (int i = Stat(conn, stat); i > 0 && cell < cells.Count; i--, cell++)
                if (TormentCatalog.ByVariety(variety) is { } spirit)
                    area.Spirits!.Add(new() { Index = area.Spirits.Count, Row = spirit.Row, X = cells[cell].X, Y = cells[cell].Y });
        int chance = Stat(conn, "map_rare_monsters_possessed_with_touched_minions_chance_%");
        int seance = chance > 0 && random.Next(100) < chance ? Stat(conn, "map_rare_monsters_possessed_with_touched_minions_num") : 0;
        if (seance > 0) conn.Instance.ContentState[SeanceKey] = seance.ToString();
    }

    /// <summary>Monsters the runner sees for the first time, under the Monsters lock: the Possession scarab's, Seance's
    /// and the memories' possessed rares, the map boss "is Possessed" (Enthralled) or "surrounded by Tormented Spirits"
    /// (Unrelenting Torment), a map's random possessed unique. Returns the cells to put surrounding spirits on.</summary>
    static List<(int X, int Y)> FirstSight(ClientState conn, Area area, double now, ref bool changed)
    {
        var surround = new List<(int X, int Y)>();
        int scarab = Stat(conn, "map_25%_chance_for_rare_monsters_to_be_possessed_up_to_x_times");
        int rareChance = Stat(conn, "map_rare_monsters_possessed_chance_%");
        int seance = conn.Instance.ContentState.TryGetValue(SeanceKey, out string? left) && int.TryParse(left, out int n) ? n : 0;
        bool boss = Stat(conn, "map_boss_is_possessed") > 0;
        int surroundChance = Stat(conn, "map_boss_chance_to_be_surrounded_by_spirits_%");
        bool randomUnique = Stat(conn, "map_random_unique_monster_is_possessed") > 0 && !conn.Instance.HasState(RandomUniqueKey);
        bool rolls = scarab > 0 || rareChance > 0 || seance > 0 || boss || surroundChance > 0 || randomUnique;
        // Nothing to roll and nothing marked (a kept instance's monsters come back under new ids): no scan.
        bool marked;
        lock (area.Marks) marked = area.Marks.Count > 0;
        if (!rolls && !marked) return surround;
        var pool = TormentCatalog.Pool(AreaLevel(conn), Stat(conn, "map_tormented_spirits_rare_varieties") > 0);
        rolls &= pool.Count > 0;
        // The map's boss: one of the area's WorldAreas bosses.
        var bosses = Packs.For(conn.Layout.Id)?.Bosses ?? [];
        foreach (var (id, m) in conn.Monsters)
        {
            if (!Touchable(m) || !area.Checked.Add(id)) continue;
            // A kept monster that is already touched or possessed: its mark again, and nothing more.
            if (Remark(conn, area, id, m)) { changed = true; continue; }
            if (!rolls || m.Rarity is not (MonsterRarity.Rare or MonsterRarity.Unique)) continue;
            var random = new Random(unchecked((int)(conn.Seed ^ Salt ^ id)));
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
                if (randomUnique && conn.Instance.SetState(RandomUniqueKey)) { spirits++; randomUnique = false; }
                if (mapBoss && surroundChance > 0 && random.Next(100) < surroundChance)
                {
                    var at = m.Walk.Position(now);
                    for (int i = 0; i < SurroundCount; i++)
                    {
                        double angle = Math.Tau * i / SurroundCount;
                        surround.Add(conn.Layout.Near(((int)Math.Round(at.X + 12 * Math.Cos(angle)), (int)Math.Round(at.Y + 12 * Math.Sin(angle)))));
                    }
                }
            }
            if (spirits == 0) continue;
            TormentCatalog.Spirit? last = null;
            for (int i = 0; i < spirits; i++)
                if (TormentCatalog.Pick(pool, random, Stat(conn, "map_rare_spirits_more_common") > 0) is { } spirit)
                    Apply(conn, area, id, m, last = spirit, grip: true, now);
            if (minions && last is not null)
            {
                var here = m.Walk.Position(now);
                foreach (var (other, o) in conn.Monsters)
                    if (o.Rarity is MonsterRarity.Normal or MonsterRarity.Magic && Touchable(o) && !TormentCatalog.Has(o.Mods, grip: false)
                        && Distance(o.Walk.Position(now), here) <= MinionRange)
                        Apply(conn, area, other, o, last, grip: false, now);
            }
            changed = true;
        }
        if (conn.Instance.ContentState.ContainsKey(SeanceKey)) conn.Instance.ContentState[SeanceKey] = seance.ToString();
        return surround;
    }

    /// <summary>Spirits that come with a map boss (Unrelenting Torment), waiting where they are put.</summary>
    static async Task AddSpirits(ClientState conn, Area area, IReadOnlyList<(int X, int Y)> cells)
    {
        var pool = TormentCatalog.Pool(AreaLevel(conn), Stat(conn, "map_tormented_spirits_rare_varieties") > 0);
        bool flatten = Stat(conn, "map_rare_spirits_more_common") > 0;
        foreach (var (x, y) in cells)
        {
            if (TormentCatalog.Pick(pool, Random.Shared, flatten) is not { } pick) continue;
            var s = new Spirit { Index = area.Spirits!.Count, Row = pick.Row, X = x, Y = y };
            area.Spirits.Add(s);
            await Spawn(conn, area, s);
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
    static readonly RuleTable<ClientState, Possession> Possessed = new();

    /// <summary>Under the Monsters lock: a spirit that reaches a player goes into them, unless they already have its Grip.</summary>
    static ClientState? PlayerContact(ClientState conn, TormentCatalog.Spirit spirit, (int X, int Y) at)
    {
        foreach (var c in Members(conn))
        {
            if (!c.PlayerSent || c.PlayerLife == 0 || Distance(c.Walk.Position(c.Clock.Elapsed.TotalSeconds), at) > ContactRange) continue;
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
    public static int? PossessPlayer(ClientState conn, int? spiritRow, double seconds)
    {
        if (seconds <= 0) return null;
        TormentCatalog.Spirit? spirit = spiritRow is int row ? TormentCatalog.ByRow(row)
            : TormentCatalog.Pick(TormentCatalog.Pool(AreaLevel(conn), unusual: false), Random.Shared)
              ?? TormentCatalog.Pick([.. TormentCatalog.All.Where(s => s.Weight > 0)], Random.Shared);
        if (spirit is null) return null;
        var possession = Possessed.GetValue(conn, _ => new());
        lock (possession)
        {
            if (possession.Spirits.Any(p => p.Row == spirit.Row)) return null;
            possession.Spirits.Add((spirit.Row, seconds, PlayerTouchLimit, 0));
            possession.Stats = null;
        }
        Console.WriteLine($"[torment] {CharacterName(conn)} possessed by {spirit.Variety} for {(double.IsPositiveInfinity(seconds) ? "the area" : $"{seconds} s")}");
        return spirit.Row;
    }

    /// <summary>The runner, under the Monsters lock: a possessed player touches the normal and magic monsters they reach.</summary>
    static void PlayerTouches(ClientState conn, Area area, double now, ref bool changed)
    {
        foreach (var c in Members(conn))
        {
            if (!Possessed.TryGetValue(c, out var possession)) continue;
            var at = c.Walk.Position(c.Clock.Elapsed.TotalSeconds);
            lock (possession)
                for (int i = 0; i < possession.Spirits.Count; i++)
                {
                    var (row, left, touches, buff) = possession.Spirits[i];
                    if (touches <= 0 || TormentCatalog.ByRow(row) is not { } spirit) continue;
                    foreach (var (id, m) in conn.Monsters)
                    {
                        if (touches <= 0) break;
                        if (m.Rarity is not (MonsterRarity.Normal or MonsterRarity.Magic) || !Touchable(m)
                            || TormentCatalog.Has(m.Mods, grip: false) || Distance(m.Walk.Position(now), at) > ContactRange) continue;
                        Apply(conn, area, id, m, spirit, grip: false, now);
                        touches--;
                        changed = true;
                    }
                    possession.Spirits[i] = (row, left, touches, buff);
                }
        }
    }

    /// <summary>The player's own tick: the buff of each new possession, the ones that end, and the stats again.</summary>
    static async Task TickPossession(ClientState conn, double seconds)
    {
        if (!Possessed.TryGetValue(conn, out var possession)) return;
        var add = new List<int>();
        var remove = new List<ushort>();
        lock (possession)
        {
            for (int i = possession.Spirits.Count - 1; i >= 0; i--)
            {
                var (row, left, touches, buff) = possession.Spirits[i];
                left -= seconds;
                if (left <= 0 || conn.PlayerLife == 0)
                {
                    if (buff != 0) remove.Add(buff);
                    possession.Spirits.RemoveAt(i);
                    possession.Stats = null;
                    continue;
                }
                if (buff == 0) { buff = conn.NextBuff++; add.Add(buff); }
                possession.Spirits[i] = (row, left, touches, buff);
            }
            // An empty possession stays in the table until the area is left: a spirit may be going into it right now.
        }
        if (add.Count == 0 && remove.Count == 0) return;
        var w = conn.Wire;
        foreach (ushort buff in add)
            await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, conn, PlayerId,
                InstanceEntities.AddBuff(PlayerId, buff, TormentCatalog.PossessedPlayerBuff), "torment: player possessed");
        foreach (ushort buff in remove)
            await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, conn, PlayerId, InstanceEntities.RemoveBuff(PlayerId, buff), "torment: possession ends");
        lock (conn.Monsters) { conn.Pools = PoolsFor(conn, conn.Pools.Level); conn.ApplyMovementSpeed(); }
        await SendPlayerStatSnapshot(w.Remote, w.Stream, w.Ciphers, conn, () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)), null);
    }

    /// <summary>Right-click cancellation (C→S 0x004b): ends the possessing spirit whose buff instance is
    /// <paramref name="buffInstance"/>, removing its icon and Grip stats.</summary>
    public static async Task<bool> CancelBuff(ClientState conn, ushort buffInstance)
    {
        if (buffInstance == 0 || !Possessed.TryGetValue(conn, out var possession)) return false;
        int spiritRow;
        lock (possession)
        {
            int index = possession.Spirits.FindIndex(p => p.Buff == buffInstance);
            if (index < 0) return false;
            spiritRow = possession.Spirits[index].Row;
            possession.Spirits.RemoveAt(index);
            possession.Stats = null;
        }
        var w = conn.Wire;
        await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, conn, PlayerId,
            InstanceEntities.RemoveBuff(PlayerId, buffInstance), "torment: possession cancelled");
        using var fresh = new PlayerStatScope(conn, fresh: true);
        lock (conn.Monsters) { conn.Pools = PoolsFor(conn, conn.Pools.Level); conn.ApplyMovementSpeed(); }
        await SendPlayerStatSnapshot(w.Remote, w.Stream, w.Ciphers, conn, () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)), null);
        Console.WriteLine($"[torment] {CharacterName(conn)} cancelled possession ({TormentCatalog.ByRow(spiritRow)?.Variety ?? $"#{spiritRow}"})");
        return true;
    }

    /// <summary>The player stat source (GameEvents.PlayerStatSources): each possessing spirit's Possessed buff (35% more
    /// damage, attack, cast and movement speed: TormentPossessed) and its Grip's stats that a player has; the Grip's
    /// monster-only stats (drops, the monster flags) are left out. Nothing when no spirit possesses the player.</summary>
    public static Dictionary<uint, int> PlayerStats(ClientState conn, Dictionary<uint, int> stats)
    {
        if (!Possessed.TryGetValue(conn, out var possession) || possession.Spirits.Count == 0) return stats;
        Dictionary<uint, int> own;
        lock (possession)
        {
            own = possession.Stats ??= GripStats(possession.Spirits.Select(p => p.Row));
        }
        foreach (var (key, value) in own) stats[key] = stats.GetValueOrDefault(key) + value;
        return stats;
    }

    static Dictionary<uint, int> GripStats(IEnumerable<int> rows)
    {
        var result = new Dictionary<uint, int>();
        foreach (int row in rows)
        {
            if (TormentCatalog.ByRow(row) is not { } spirit) continue;
            var (_, stats) = TormentCatalog.Roll(spirit.Grip, Random.Shared);
            foreach (var (name, value) in stats)
                if (!name.StartsWith("monster_", StringComparison.Ordinal) && !name.Contains("possess", StringComparison.Ordinal)
                    && !name.StartsWith("torment_", StringComparison.Ordinal) && !name.Contains("drop", StringComparison.Ordinal)
                    && !name.StartsWith("from_league_", StringComparison.Ordinal) && name != "cant_touch_this"
                    && Program.Stats.Key(name) is uint key)
                    result[key] = result.GetValueOrDefault(key) + value;
        }
        return result;
    }

    // ---- the Torment Scarab's release and dig ----

    /// <summary>"set free when Possessed Monsters are slain and dig up a Possessed Monster": a rare of the area's packs
    /// rises next to the kill, possessed by the freed spirit.</summary>
    static async Task Dig(ClientState conn, Area area, int row, (int X, int Y) at)
    {
        if (TormentCatalog.ByRow(row) is not { } spirit) return;
        var pool = Packs.For(conn.Layout.Id)?.Scattered.SelectMany(p => p.Pack.Entries)
            .Where(e => Monsters.Find(e.Variety.ToString()) is { Supported: true }).ToArray() ?? [];
        if (pool.Length == 0) return;
        int roll = Random.Shared.Next(pool.Sum(e => Math.Max(1, e.Weight)));
        var entry = pool[^1];
        foreach (var e in pool) if ((roll -= Math.Max(1, e.Weight)) < 0) { entry = e; break; }
        var variety = Monsters.Find(entry.Variety.ToString())!;
        var cell = conn.Layout.Near((at.X + Random.Shared.Next(-DigRange, DigRange + 1), at.Y + Random.Shared.Next(-DigRange, DigRange + 1)));
        var w = conn.Wire;
        uint id = await SpawnMonster(w.Remote, w.Stream, w.Ciphers, conn, variety, AreaLevel(conn), MonsterRarity.Rare, cell.X, cell.Y,
            alone: true, unrisen: false);
        if (id == 0) return;
        lock (conn.Monsters)
            if (conn.Monsters.TryGetValue(id, out var m))
            {
                area.Checked.Add(id);
                Apply(conn, area, id, m, spirit, grip: true, conn.Clock.Elapsed.TotalSeconds);
            }
        Console.WriteLine($"[torment] a possessed {variety.Id} is dug up at ({cell.X}, {cell.Y})");
    }
}
