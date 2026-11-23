using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using static Game;
using static Program;

/// <summary>
/// Tormented Spirits (CoreLeagues row 5, Torment): unique monsters that wait until a player comes near, then flee from the
/// players and pass through the area's monsters. A normal or magic monster a spirit passes gets the spirit's Touch; a rare
/// or unique one is possessed (its Grip) and the spirit goes into it. A spirit that is not killed leaves after a while
/// with no items. One connection drives them, the instance's monster runner (SharedMonsters.cs); every client is shown the
/// touched monsters' mods and their buff.
/// </summary>
public static partial class MapTorment
{
    public const string Content = "Torment";
    const string SaveKey = "Torment/spirits", MarksKey = "Torment/marks";
    const int LeagueRow = 5;
    const uint Salt = 0x544f524d; // "TORM": this roll's own stream of the instance seed.
    /// <summary>The spirits' more life (TormentedSpiritLifeScalingPerLevel), handed over to MonsterModEffects in
    /// GameModule.Load. The client has no stat for it: its .ot names the table by index.</summary>
    public static readonly uint LifeStat = Stat.Of("torment_spirit_life_+%_final");
    const string TaperKey = "Torment/taper";
    /// <summary>A waiting or new spirit's less damage taken, handed over to MonsterModEffects.DamageTaken in GameModule.Load.</summary>
    public static readonly uint TaperStat = Stat.Of("torment_spirit_damage_taken_+%_final");

    // poewiki "Tormented spirit": 90% less damage taken, tapering to none over 15 seconds once active; 5% more damage taken
    // per monster touched; at most 50 touches. The pause comes from its TormentSpiritPause skill (GrantedEffectsPerLevel
    // cooldown 5000 ms).
    const double TaperSeconds = 15, PauseEvery = 5;
    const int TouchLimit = 50, TaperPct = 90, PerTouchPct = 5;
    // Our own values: neither the client's tables nor poewiki give them (the spirits' AI is a server file, .ais).
    const double ActiveSeconds = 20, PauseSeconds = 1, SeekRange = 45, ContactRange = 6, ReorderSeconds = 0.75;

    public enum SpiritState { Waiting, Active, Possessed, Slain, Gone }

    public sealed class Spirit
    {
        public int Index { get; set; }
        public int Row { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public uint Monster { get; set; }
        public SpiritState State { get; set; }
        /// <summary>Seconds it has been active (summed ticks: the clocks are per connection).</summary>
        public double Active { get; set; }
        public int Touches { get; set; }
        public double PausedAt { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public double NextOrderAt;
        /// <summary>Where it was at its last order: no step since means its walk stops short of the target.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public (int X, int Y)? SteeredFrom;
        /// <summary>Where it was at the last tick: it touches what its path since passed (it walks ~35 cells a second).</summary>
        [System.Text.Json.Serialization.JsonIgnore] public (int X, int Y)? PreviousAt;
        [System.Text.Json.Serialization.JsonIgnore] public int Taper = int.MinValue;
    }

    /// <summary>A touched or possessed monster: what every client is shown of it, and what its death drops. Kept with
    /// the instance (a module reload keeps it).</summary>
    sealed class Mark
    {
        public bool Grip { get; set; }
        public bool Paid { get; set; }
        public int Version { get; set; }
        public byte[] Magic { get; set; } = [];
        public byte[] Stats { get; set; } = [];
        public List<int> Spirits { get; set; } = [];
        public Dictionary<uint, int> DropStats { get; set; } = [];
    }

    sealed class Area
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public List<Spirit>? Spirits;
        /// <summary>Touched and possessed monsters; with <see cref="SpiritMonsters"/>, under its own lock (kills read it).</summary>
        public readonly Dictionary<uint, Mark> Marks = [];
        /// <summary>The living spirits' monsters, so a kill finds its spirit without the gate.</summary>
        public readonly HashSet<uint> SpiritMonsters = [];
        public readonly HashSet<uint> Checked = [];
        public readonly ConcurrentQueue<Payout> Payouts = new();
        public int Version;
        public bool MarksChanged;
    }

    sealed class Shown
    {
        public readonly Dictionary<uint, int> Versions = [];
    }

    static readonly Store<InstanceState, Area> Areas = new();
    static readonly Store<Player, Shown> Presented = new();
    static readonly ConcurrentDictionary<(string Area, int Level), AreaInfo?> AreaInfos = new();

    // The drop stats of the Touch and Grip mods that MonsterKilled pays out (MonsterVarieties-independent).
    static readonly uint[] DropStatIds =
    [
        Stat.TormentSpiritHauntedAdditionalItemDropChancePct, Stat.MonsterDropAdditionalUniqueItems,
        Stat.PossessedMonsterDropItemTemplateForUniqueItem, Stat.MonsterDropAdditionalMapItems,
        Stat.PossessedMonsterDropTemplateAnyMap, Stat.PossessedMonsterDropTemplateAnyScarab,
        Stat.MonsterDropAdditionalWisdomScrolls, Stat.MonsterDropAdditionalPortalScrolls, Stat.MonsterDropAdditionalRingsAmulets,
        Stat.MonsterDropAdditionalCurrencyItems, Stat.MonsterDropAdditionalVaalItems,
    ];

    // Map, Atlas and Idol stats this provider does not act on yet; an opening that has them says so. Wild Rogue Exiles
    // (Exiled Will, an Idol) come with the Anarchy league's content, the scarab weights with the drops' scarab types,
    // Refiner's Bargain's dust with Kingsmarch.
    static readonly uint[] Unsupported =
    [
        Stat.MapRogueExilesPossessedPctChance, Stat.MapTormentScarabMoreLikelyPct,
        Stat.MapPossessedMonsterEquipmentDropsConvertedToDustPctChance,
    ];

    // A possessed monster's own chance to drop an item of a kind (the Atlas's sextant-era map stats).
    static readonly (uint Stat, string Kind)[] PossessedDropChances =
    [
        (Stat.MapPossessedMonstersDropUniqueChancePct, "unique"), (Stat.MapPossessedMonstersDropMapChancePct, "map"),
        (Stat.MapPossessedMonstersDropRustedScarabChancePct, "scarab"), (Stat.MapPossessedMonstersDropPolishedScarabChancePct, "scarab"),
        (Stat.MapPossessedMonstersDropGildedScarabChancePct, "scarab"), (Stat.MapPossessedMonstersDropWingedScarabChancePct, "scarab"),
    ];

    static IEnumerable<Player> Members(Player player) => player.Instance.PlayersExcept(player.Id)
        .Prepend(player)
        .Where(c => !c.Closed && !c.LeavingArea && c.Instance == player.Instance).Distinct();

    static byte AreaLevel(Player player) =>
        (byte)Math.Clamp(MapRunOf(player) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);

    static int AreaStat(Player player, uint id) => MapModsOf(player)?.Get(id) ?? 0;

    public static void Opening(MapOpening request)
    {
        if (request.Opening is not { } opening || TormentCatalog.All.Count == 0) return;
        request.HandledContent.Add(Content);
        var present = Unsupported.Where(s => opening.Mods.Get(s) != 0).Select(Program.Stats.Name).ToArray();
        if (present.Length > 0)
            request.ContentWarnings.Add(("Torment.Parity",
                $"area={opening.Area.Id} content=Torment: spirits enabled; not acted on: {string.Join(", ", present)}"));
    }

    /// <summary>How many spirits the area gets: the client's own league roll over the area's stats (the map's, the
    /// scarabs' <c>map_spawn_extra_torment_spirits</c> and the Atlas tree's chance), 0 where its row allows none.</summary>
    static int Count(Player player, byte level)
    {
        var info = AreaInfos.GetOrAdd((player.Layout.Id, level), k =>
        {
            try { return AreaInfo.Load(GamePack.Default.TryRead, k.Area, k.Level); }
            catch (Exception e) when (e is ArgumentException or FileNotFoundException) { return null; }
        });
        if (info is null) return 0;
        var stats = player.Instance.KeptAreaStats?.ToDictionary(s => (int)s.Key, s => s.Value) ?? [];
        return ExtraTerrainFeatures.LeagueCount(info with { Stats = stats }, new LevelRandom(player.Seed ^ Salt, false), LeagueRow);
    }

    static Area? Of(Player player)
    {
        if (Areas.TryGetValue(player.Instance, out var area)) return area;
        // A module reload keeps the instance's own record (ContentState), not this table.
        if (!player.Instance.ContentState.TryGetValue(SaveKey, out string? saved)) return null;
        area = Areas.GetValue(player.Instance, _ => new());
        Load(area, saved, player.Instance);
        return area;
    }

    static void Load(Area area, string saved, InstanceState instance)
    {
        lock (area.Marks)
        {
            if (area.Spirits is not null) return;
            area.Spirits = JsonSerializer.Deserialize<List<Spirit>>(saved) ?? [];
            foreach (var s in area.Spirits.Where(s => s.Monster != 0 && s.State is SpiritState.Waiting or SpiritState.Active))
                area.SpiritMonsters.Add(s.Monster);
            if (instance.ContentState.TryGetValue(MarksKey, out string? marks))
                foreach (var (id, mark) in JsonSerializer.Deserialize<Dictionary<uint, Mark>>(marks) ?? [])
                {
                    area.Marks[id] = mark;
                    area.Version = Math.Max(area.Version, mark.Version);
                }
        }
    }

    // Under the Marks lock: the marks are written with the instance on the next tick.
    static void KeepMarks(Player player, Area area) => area.MarksChanged = true;

    static void FlushMarks(Player player, Area area)
    {
        lock (area.Marks)
        {
            if (!area.MarksChanged) return;
            area.MarksChanged = false;
            player.Instance.ContentState[MarksKey] = JsonSerializer.Serialize(area.Marks);
        }
    }

    static void Keep(Player player, Area area) => player.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(area.Spirits);

    /// <summary>Spawns Tormented Spirits at explicit coordinates (e.g., released from a cage or encounter),
    /// initializing the instance's Torment state even when <c>Packs.For(Layout.Id)</c> has no scattered packs.</summary>
    public static async Task<int> SpawnSpiritsAt(Player player, IReadOnlyList<(int X, int Y)> cells, byte level)
    {
        if (player.Instance is null || TormentCatalog.All.Count == 0 || cells.Count == 0) return 0;
        var area = Areas.GetValue(player.Instance, _ => new());
        var spawned = new List<Spirit>(cells.Count);
        await area.Gate.WaitAsync();
        try
        {
            if (area.Spirits is null)
            {
                if (player.Instance.ContentState.TryGetValue(SaveKey, out string? saved)) Load(area, saved, player.Instance);
                area.Spirits ??= [];
            }
            var pool = TormentCatalog.Pool(level, AreaStat(player, Stat.MapTormentedSpiritsRareVarieties) > 0);
            if (pool.Count == 0) pool = [.. TormentCatalog.All.Where(s => s.Weight > 0)];
            if (pool.Count == 0) return 0;
            bool flatten = AreaStat(player, Stat.MapRareSpiritsMoreCommon) > 0;
            foreach (var (x, y) in cells)
            {
                if (TormentCatalog.Pick(pool, Random.Shared, flatten) is not { } pick) continue;
                var s = new Spirit { Index = area.Spirits.Count, Row = pick.Row, X = x, Y = y };
                area.Spirits.Add(s);
                spawned.Add(s);
            }
            foreach (var s in spawned) await Spawn(player, area, s, level);
            if (spawned.Count > 0) Keep(player, area);
        }
        finally { area.Gate.Release(); }
        return spawned.Count;
    }

    public static async Task Enter(Player player)
    {
        if (TormentCatalog.All.Count == 0 || player.Layout.Walkable is null) return;
        bool hasPacks = Packs.For(player.Layout.Id)?.Scattered.Count > 0;
        if (!hasPacks && AreaStat(player, Stat.MapRareMonstersPossessedChancePct) <= 0 && !player.Instance.ContentState.ContainsKey(SaveKey))
            return;
        var area = Areas.GetValue(player.Instance, _ => new());
        await area.Gate.WaitAsync();
        try
        {
            if (area.Spirits is null)
            {
                if (player.Instance.ContentState.TryGetValue(SaveKey, out string? saved)) Load(area, saved, player.Instance);
                else
                {
                    area.Spirits = [];
                    byte level = AreaLevel(player);
                    int count = Count(player, level);
                    var pool = TormentCatalog.Pool(level, AreaStat(player, Stat.MapTormentedSpiritsRareVarieties) > 0);
                    var random = new Random(unchecked((int)(player.Seed ^ Salt)));
                    bool lessCommon = AreaStat(player, Stat.MapRareSpiritsMoreCommon) > 0;
                    if (pool.Count > 0)
                        foreach (var (x, y) in MapStrongboxes.Positions(player.Layout, player.Seed ^ Salt, count))
                            if (TormentCatalog.Pick(pool, random, lessCommon) is { } pick)
                                area.Spirits.Add(new() { Index = area.Spirits.Count, Row = pick.Row, X = x, Y = y });
                    RollExtras(player, area, random);
                    count = area.Spirits.Count;
                    Keep(player, area);
                    if (count > 0) Console.WriteLine($"[map-content] torment area={player.Layout.Id} level={level}: placed {area.Spirits.Count}/{count}");
                }
            }
            await Restore(player, area);
        }
        finally { area.Gate.Release(); }
    }

    // Spawns the spirits that are waiting or active and have no monster in the instance: new ones, and the ones of a kept
    // instance, whose monsters came back as ordinary monsters under new ids (SavedMonster keeps no script): those go.
    static async Task Restore(Player player, Area area)
    {
        var tracked = new HashSet<uint>();
        var missing = new List<Spirit>();
        foreach (var s in area.Spirits!.Where(s => s.State is SpiritState.Waiting or SpiritState.Active))
            if (s.Monster != 0 && player.Area.Monsters.ContainsKey(s.Monster)) tracked.Add(s.Monster);
            else missing.Add(s);
        if (missing.Count == 0) return;
        uint[] strays = [.. player.Area.Monsters.Where(p => !tracked.Contains(p.Key) && TormentCatalog.ByVariety(p.Value.Variety?.Id) is not null)
            .Select(p => p.Key)];
        foreach (uint stray in strays) await Remove(player, stray, "kept spirit replaced");
        foreach (var s in missing) await Spawn(player, area, s);
        Keep(player, area);
    }

    static async Task Spawn(Player player, Area area, Spirit s, byte? levelOverride = null)
    {
        if (TormentCatalog.ByRow(s.Row) is not { } spirit || Monsters.Find(spirit.Variety) is not { Supported: true } variety) return;
        byte level = levelOverride ?? AreaLevel(player);
        var basis = MonsterModsFor(player, variety, level, MonsterRarity.Unique, null, true);
        var own = TormentCatalog.Roll(spirit.Own, Random.Shared);
        var mods = MonsterModRolls.Create([.. basis.Mods, .. own.Mods], basis.NameWords, MonsterRarity.Unique, level);
        mods = mods with { Effects = mods.Effects.Plus(LifeStat, TormentCatalog.LifePct(level))
            .Plus(Stat.BaseMovementVelocityPlusPct, AreaStat(player, Stat.MapTormentedSpiritsMovementSpeedPlusPct)) };
        uint id = await SpawnMonster(player, variety, level, MonsterRarity.Unique, s.X, s.Y,
            mods: mods, alone: true, scripted: true, movement: new MonsterMovement { ThroughBodies = true });
        if (id == 0) return;
        s.Monster = id;
        s.Taper = int.MinValue;
        lock (area.Marks) area.SpiritMonsters.Add(id);
        if (player.Area.Monsters.TryGetValue(id, out var m)) SetTaper(m, s);
    }

    // The spirit's damage taken: 90% less while waiting, tapering to none over 15 seconds once active (a final factor),
    // and 5% increased per touch.
    static void SetTaper(SpawnedMonster m, Spirit s)
    {
        double left = s.State == SpiritState.Waiting ? 1 : Math.Max(0, 1 - s.Active / TaperSeconds);
        int less = -(int)Math.Round(TaperPct * left);
        if (less * 1000 + s.Touches == s.Taper) return;
        s.Taper = less * 1000 + s.Touches;
        m.SetTemporaryStats(TaperKey, new Dictionary<uint, int> { [TaperStat] = less, [Stat.BaseDamageTakenPlusPct] = PerTouchPct * s.Touches });
    }

    public static async Task Tick(Player player, double seconds)
    {
        if (!player.Closed && !player.LeavingArea) await TickPossession(player, seconds);
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent || player.Instance is null || Of(player) is not { } area) return;
        await area.Gate.WaitAsync();
        try
        {
            if (area.Spirits is null || player.Closed || player.LeavingArea) return;
            await PayOut(player, area);
            await Present(player, area);
            if (IsMonsterRunner(player)) await Drive(player, area, Math.Clamp(seconds, 0, 1));
            FlushMarks(player, area);
        }
        finally { area.Gate.Release(); }
    }

    // What this client has not been shown of the touched and possessed monsters it has: their mods, stats and buff.
    static async Task Present(Player player, Area area)
    {
        if (area.Marks.Count == 0) return;
        var shown = Presented.GetValue(player, _ => new());
        var due = new List<(uint Id, Mark Mark, bool Buff)>();
        lock (area.Marks)
            foreach (var (id, mark) in area.Marks)
                if (MonsterShown(player, id) && shown.Versions.GetValueOrDefault(id, -1) != mark.Version)
                    due.Add((id, mark, !shown.Versions.ContainsKey(id) || mark.Grip));
        foreach (var (id, mark, buff) in due)
        {
            await SendWorldRecord(player, id, mark.Magic, null);
            await SendWorldRecord(player, id, mark.Stats, null);
            if (buff)
            {
                ushort buffId = NextBuffId(player);
                await SendWorldRecord(player, id, InstanceEntities.AddBuff(id, buffId,
                    mark.Grip ? TormentCatalog.PossessedBuff : TormentCatalog.TouchedBuff),
                    $"torment: monster 0x{id:x} {(mark.Grip ? "possessed" : "touched")}");
            }
            shown.Versions[id] = mark.Version;
        }
    }

    static async Task Drive(Player player, Area area, double seconds)
    {
        double now = AreaTime(player);
        var players = Members(player).Where(c => c.Connection.PlayerSent && c.PlayerLife > 0)
            .Select(c => c.Walk.Position(AreaTime(c))).ToArray();
        bool changed = false;
        List<(int X, int Y)> surround = FirstSight(player, area, now, ref changed);
        bool speaker = SpeakerOfTheDead(player);
        if (speaker) PlayerTouches(player, area, now, ref changed);
        double duration = ActiveSeconds * Math.Max(0, 1 + AreaStat(player, Stat.MapTormentedSpiritsDurationPlusPct) / 100.0);
        foreach (var s in area.Spirits!)
        {
            if (s.State is not (SpiritState.Waiting or SpiritState.Active) || TormentCatalog.ByRow(s.Row) is not { } spirit) continue;
            SpawnedMonster? m;
            (int X, int Y) at;
            uint? possess = null;
            Player? targetPlayer = null;
            if (!player.Area.Monsters.TryGetValue(s.Monster, out m) || m.Dead)
            {
                if (m is null) { s.State = SpiritState.Gone; changed = true; }
                continue;
            }
            at = m.Walk.Position(now);
            var from = s.PreviousAt ?? at;
            s.PreviousAt = at;
            (s.X, s.Y) = at;
            if (s.State == SpiritState.Waiting)
            {
                int range = m.Variety?.AggroRange ?? 60;
                if (!players.Any(p => Distance(p, at) <= range)) continue;
                s.State = SpiritState.Active;
                s.PausedAt = 0;
                changed = true;
                Console.WriteLine($"[torment] spirit {s.Index} {spirit.Variety} 0x{s.Monster:x} active at ({at.X}, {at.Y})");
            }
            s.Active += seconds;
            if (s.Active < duration)
            {
                // What it passes through: a rare or unique it goes into, else the normal and magic monsters it touches.
                // Speaker of the Dead: it goes into a player it reaches, and into no monster.
                if (speaker) targetPlayer = PlayerContact(player, spirit, at);
                foreach (var (id, other) in player.Area.Monsters)
                {
                    if (targetPlayer is not null) break;
                    if (!Touchable(other) || Passed(from, at, other.Walk.Position(now)) > Contact(other)) continue;
                    if (other.Rarity is MonsterRarity.Rare or MonsterRarity.Unique)
                    {
                        if (speaker) continue;
                        Apply(player, area, id, other, spirit, grip: true, now);
                        possess = id;
                        break;
                    }
                    if (s.Touches >= TouchLimit || TormentCatalog.Has(other.Mods, grip: false)) continue;
                    Apply(player, area, id, other, spirit, grip: false, now);
                    s.Touches++;
                    changed = true;
                }
                SetTaper(m, s);
            }
            if (targetPlayer is not null)
            {
                s.State = SpiritState.Possessed;
                await Remove(player, s.Monster, $"spirit possesses {CharacterName(targetPlayer)}");
                changed = true;
                continue;
            }
            if (possess is uint into)
            {
                s.State = SpiritState.Possessed;
                Console.WriteLine($"[torment] spirit {s.Index} possesses 0x{into:x} after {s.Touches} touches");
                await Remove(player, s.Monster, "spirit possesses a monster");
                changed = true;
                continue;
            }
            if (s.Active >= duration)
            {
                s.State = SpiritState.Gone;
                Console.WriteLine($"[torment] spirit {s.Index} leaves after {s.Active:0.#} s, {s.Touches} touches");
                await Remove(player, s.Monster, "spirit's time is up");
                changed = true;
                continue;
            }
            if (now >= s.NextOrderAt) await Steer(player, s, m!, at, players, now, speaker);
        }
        if (surround.Count > 0) { await AddSpirits(player, area, surround); changed = true; }
        if (changed) Keep(player, area);
    }

    // It touches what it reaches: the two bodies' edges within the contact range.
    static double Contact(SpawnedMonster other) => ContactRange + Math.Max(0, (other.Variety?.ObjectSize ?? 3) - 1);

    static bool Touchable(SpawnedMonster m) => m is { Hostile: true, Dead: false, Life: > 0, Asleep: false, WorldHidden: false }
        && m.OwnerId is null && m.Variety is { } v && TormentCatalog.ByVariety(v.Id) is null;

    // The distance from a point to the path from a to b.
    static double Passed((int X, int Y) a, (int X, int Y) b, (int X, int Y) p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
        double t = length == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length, 0, 1);
        double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    // Where it goes next: a pause every few seconds; else to a rare or unique to possess, else to a monster it has not
    // touched, else away from the nearest player.
    static async Task Steer(Player player, Spirit s, SpawnedMonster m, (int X, int Y) at, (int X, int Y)[] players, double now, bool speaker)
    {
        (int X, int Y)? to = null;
        if (s.Active - s.PausedAt >= PauseEvery)
        {
            s.PausedAt = s.Active;
            s.NextOrderAt = now + PauseSeconds;
            to = at;
        }
        else
        {
            s.NextOrderAt = now + ReorderSeconds;
            // No step since the last order: the walk stops short of that target, so it goes away from the players instead.
            bool stuck = s.SteeredFrom == at;
            s.SteeredFrom = at;
            var near = player.Area.Monsters.Values.Where(o => Touchable(o) && Distance(o.Walk.Position(now), at) <= SeekRange)
                .Select(o => (Monster: o, At: o.Walk.Position(now))).ToArray();
            var target = stuck ? null : near.Where(o => !speaker && o.Monster.Rarity is MonsterRarity.Rare or MonsterRarity.Unique).OrderBy(o => Distance(o.At, at))
                .Select(o => ((int, int)?)o.At).FirstOrDefault()
                ?? (s.Touches < TouchLimit ? near.Where(o => !TormentCatalog.Has(o.Monster.Mods, grip: false)).OrderBy(o => Distance(o.At, at))
                    .Select(o => ((int, int)?)o.At).FirstOrDefault() : null);
            // A monster moves: while it chases one it is ordered again every tick.
            if (target is not null) s.NextOrderAt = now;
            if (target is null && players.Length > 0)
            {
                var nearestPlayer = players.MinBy(p => Distance(p, at));
                target = MonsterFlee.Point(at, nearestPlayer, m.Walk.Floor, Random.Shared);
            }
            // Nothing to chase and no way away: it drifts somewhere near.
            target ??= MonsterFlee.Point(at, at, m.Walk.Floor, Random.Shared);
            to = target;
        }
        if (to is not { } dest || dest == m.Walk.Destination && dest != at) return;
        int generation = ++m.ActionGeneration;
        await SendMonsterAction(player, s.Monster, m, generation, () =>
        {
            double t = AreaTime(player);
            var here = m.Walk.Position(t);
            if (dest == at) m.Walk.Teleport(here.X, here.Y, t);
            else m.Walk.Order(dest.X, dest.Y, t);
            var goal = dest == at ? here : dest;
            return InstanceProtocol.ActionStarted(s.Monster, here.X, here.Y, goal.X, goal.Y, 0x2909, player.NextActionSequence(), player.MonsterOrderFlags);
        });
    }

    /// <summary>Gives a monster a spirit's Touch or Grip: its mods (kept with the instance and
    /// shown on its name plate), what they change (life, hit, attack time, walk, drops) and the record later clients get.</summary>
    static void Apply(Player player, Area area, uint id, SpawnedMonster m, TormentCatalog.Spirit spirit, bool grip, double now)
    {
        var (mods, stats) = TormentCatalog.Roll(grip ? spirit.Grip : spirit.Touch, Random.Shared);
        // A Blessed map's "Possessed Monsters take increased Damage".
        if (grip && AreaStat(player, Stat.MapMonsterDamageTakenPlusPctWhilePossessed) is int taken and not 0)
            stats[Stat.BaseDamageTakenPlusPct] = stats.GetValueOrDefault(Stat.BaseDamageTakenPlusPct) + taken;
        var before = m.Effects;
        uint oldLife = m.MaxLife;
        m.Mods = m.Mods with { Mods = [.. m.Mods.Mods, .. mods], Effects = m.Mods.Effects.Combine(stats) };
        var after = m.Effects;
        int scale = after.MaxLife(100000);
        if (scale != before.MaxLife(100000))
        {
            m.MaxLife = (uint)Math.Clamp((long)oldLife * scale / Math.Max(1, before.MaxLife(100000)), 1, uint.MaxValue);
            m.Life = (uint)Math.Clamp(Math.Ceiling(m.Life * (double)m.MaxLife / Math.Max(1, oldLife)), 1, m.MaxLife);
        }
        if (m.Attack is { } attack)
            m.Attack = attack with
            {
                Damage = MonsterModEffects.AddedTo(attack.Damage, stats),
                Seconds = before.AttackSeconds(attack.Seconds, stats.GetValueOrDefault(Stat.AttackSpeedPlusPct)),
            };
        if (after.MovementPct != before.MovementPct)
            m.Walk.SetSpeed(m.Walk.Speed * (100.0 + after.MovementPct) / Math.Max(1, 100.0 + before.MovementPct), now);
        Show(player, area, id, m, mark =>
        {
            mark.Grip |= grip;
            if (grip) mark.Spirits.Add(spirit.Row);
            foreach (uint stat in DropStatIds)
                if (stats.GetValueOrDefault(stat) is int value and not 0) mark.DropStats[stat] = mark.DropStats.GetValueOrDefault(stat) + value;
        });
        Console.WriteLine($"[torment] {(grip ? "possessed" : "touched")} 0x{id:x} {m.Variety?.Id} ({m.Rarity}) by {spirit.Variety}");
    }

    /// <summary>A touched or possessed monster's record for the clients (its mods, the stats they
    /// change, the record later clients are created from) and its mark, changed by <paramref name="change"/>.</summary>
    static void Show(Player player, Area area, uint id, SpawnedMonster m, Action<Mark> change)
    {
        var effects = m.Effects;
        var client = new List<(uint Key, int Value)>
        {
            (Stat.MaximumLife, (int)Math.Min(int.MaxValue, m.MaxLife)),
            (Stat.MovementVelocityPlusPct, effects.MovementPct),
            (Stat.ActorScalePlusPct, effects.ActorScalePct),
        };
        if (m.Attack is { } a) client.AddRange(a.Stats());
        // The record a client that comes later is created from (SyncInstanceMonsters).
        string? name = null;
        if (player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
        {
            var keys = client.Select(c => c.Key).ToHashSet();
            player.Instance.World.MonsterOrigins[id] = origin with
            {
                Object = origin.Object with
                {
                    Mods = m.Mods.Mods,
                    Stats = [.. (origin.Object.Stats ?? []).Where(s => !keys.Contains(s.Key)), .. client],
                },
            };
            name = origin.Object.Name;
        }
        var magic = new BlobWriter();
        MagicProperties.Write(magic, (byte)m.Rarity, identified: true, m.Mods.Mods, m.Mods.NameWords, name);
        magic.U8(0);
        lock (area.Marks)
        {
            if (!area.Marks.TryGetValue(id, out var mark)) area.Marks[id] = mark = new();
            change(mark);
            mark.Version = ++area.Version;
            mark.Magic = InstanceEntities.ComponentUpdate(id, 2, magic.ToArray());
            mark.Stats = InstanceEntities.StatsUpdate(id, client);
            KeepMarks(player, area);
        }
    }

    /// <summary>A monster that came back with a Touch or Grip in its kept mods (a kept instance
    /// gives it a new id) gets its mark again, from those mods; it is not touched or possessed again.</summary>
    static bool Remark(Player player, Area area, uint id, SpawnedMonster m)
    {
        bool grip = TormentCatalog.Has(m.Mods, grip: true), touch = TormentCatalog.Has(m.Mods, grip: false);
        if (!grip && !touch) return false;
        lock (area.Marks) if (area.Marks.ContainsKey(id)) return true;
        Show(player, area, id, m, mark =>
        {
            mark.Grip = grip;
            foreach (var mod in m.Mods.Mods)
            {
                if (Program.Items.Mods.ByHash(mod.Hash) is not { } row) continue;
                if (TormentCatalog.ByGripMod(row.Row) is { } spirit) mark.Spirits.Add(spirit.Row);
                for (int i = 0; i < row.Stats.Count && i < mod.Values.Count; i++)
                    if (DropStatIds.Contains(row.Stats[i].Id))
                        mark.DropStats[row.Stats[i].Id] = mark.DropStats.GetValueOrDefault(row.Stats[i].Id) + mod.Values[i];
            }
        });
        return true;
    }

    static async Task Remove(Player player, uint id, string why)
    {
        if (Areas.TryGetValue(player.Instance, out var area)) lock (area.Marks) area.SpiritMonsters.Remove(id);
        bool shown = player.Area.Monsters.Remove(id, out var m);
        if (m is not null) { m.Dead = true; m.Life = 0; ++m.ActionGeneration; }
        player.Instance.World.MonsterOrigins.TryRemove(id, out _);
        if (shown) await SendWorldRemoval(player, id, $"[torment] 0x{id:x} removed: {why}");
    }

    /// <summary>Per kill: a touched or possessed monster's more items from the map
    /// (<c>map_possessed_and_touched_monsters_item_quantity_+%</c>).</summary>
    public static void Drops(DropRoll roll)
    {
        if (roll.Monster.ObjectId == 0 || !Areas.TryGetValue(roll.Player.Instance, out var area) || area.Spirits is null) return;
        lock (area.Marks)
            if (area.Marks.ContainsKey(roll.Monster.ObjectId)
                && AreaStat(roll.Player, Stat.MapPossessedAndTouchedMonstersItemQuantityPlusPct) is int quantity and not 0)
                roll.Quantity *= Math.Max(0, 1 + quantity / 100.0);
    }

    /// <summary>A kill's payout, claimed in the kill (every kill: the player's, a minion's, a totem's, a trap's) and paid
    /// on the next tick, so the kill's own drop roll still finds its mark (<see cref="Drops"/>).</summary>
    sealed record Payout(Player Killer, uint Id, int SpiritRow, Mark? Mark, MonsterRarity Rarity, int Level, (int X, int Y) At, string Name);

    /// <summary>GameEvents.Kill: claims a slain spirit's or a touched or possessed monster's payout once.</summary>
    public static void Kill(KillContext kill)
    {
        uint id = kill.Monster.ObjectId;
        if (id == 0 || kill.Player.Instance is null || !Areas.TryGetValue(kill.Player.Instance, out var area)) return;
        Mark? mark;
        bool spirit;
        lock (area.Marks)
        {
            spirit = area.SpiritMonsters.Remove(id);
            if (area.Marks.TryGetValue(id, out mark) && !mark.Paid) mark.Paid = true;
            else mark = null;
            if (!spirit && mark is null) return;
            int row = spirit ? TormentCatalog.ByVariety(kill.VarietyId)?.Row ?? -1 : -1;
            area.Payouts.Enqueue(new(kill.Player, id, row, mark, kill.Monster.Rarity, kill.Monster.Level, kill.At,
                kill.Monster.Variety?.Name ?? kill.VarietyId));
            KeepMarks(kill.Player, area);
        }
    }

    // Under the gate, on a tick: the kills claimed since the last one.
    static async Task PayOut(Player player, Area area)
    {
        while (area.Payouts.TryDequeue(out var payout))
        {
            var killer = payout.Killer is { Closed: false, LeavingArea: false } k && k.Instance == player.Instance ? k : player;
            var items = new List<ItemDrops.Drop>();
            var random = Random.Shared;
            int level = Math.Clamp(payout.Level, 1, 100);
            var drops = DropsFor(killer);
            int tier = MapRunOf(killer)?.Area.Tier ?? 0;
            if (payout.SpiritRow >= 0)
            {
                if (area.Spirits!.FirstOrDefault(s => s.Monster == payout.Id && s.State is not (SpiritState.Possessed or SpiritState.Slain)) is { } slain)
                {
                    slain.State = SpiritState.Slain;
                    Keep(player, area);
                    Console.WriteLine($"[torment] spirit {slain.Index} slain after {slain.Touches} touches");
                }
                // poewiki: a slain spirit always drops a rare; the Peculiarity's spirits also their own kind of item.
                items.AddRange(Rares(killer, level, 1 + Math.Max(0, AreaStat(killer, Stat.MapTormentedSpiritsDropXAdditionalRareItems)), random));
                if (TormentCatalog.ByRow(payout.SpiritRow) is { Unusual: true } unusual
                    && (unusual.Variety.EndsWith("Betrayer", StringComparison.Ordinal) ? drops.Scarab(random, level)
                        : unusual.Variety.EndsWith("Felon", StringComparison.Ordinal) ? Unique(killer, level, random)
                        : drops.Map(level, MonsterRarity.Unique, Math.Max(1, tier), random)) is { } own) items.Add(own);
            }
            if (payout.Mark is { } mark)
            {
                int Chance(uint stat) => mark.DropStats.GetValueOrDefault(stat);
                int Count(uint stat) => Chance(stat) / 100 + (random.Next(100) < Chance(stat) % 100 ? 1 : 0);
                // Each Grip's 100% additional item, rare; poewiki: a possessed rare drops at least two rares, a unique three.
                int rares = Count(Stat.TormentSpiritHauntedAdditionalItemDropChancePct);
                if (mark.Grip) rares = Math.Max(rares, payout.Rarity == MonsterRarity.Unique ? 3 : 2);
                items.AddRange(Rares(killer, level, rares, random));
                for (int i = Chance(Stat.MonsterDropAdditionalUniqueItems) + Count(Stat.PossessedMonsterDropItemTemplateForUniqueItem); i > 0; i--)
                    if (Unique(killer, level, random) is { } unique) items.Add(unique);
                for (int i = Chance(Stat.MonsterDropAdditionalMapItems) + Count(Stat.PossessedMonsterDropTemplateAnyMap); i > 0; i--)
                    if (drops.Map(level, MonsterRarity.Unique, Math.Max(1, tier), random) is { } map) items.Add(map);
                for (int i = Count(Stat.PossessedMonsterDropTemplateAnyScarab); i > 0; i--)
                    if (drops.Scarab(random, level) is { } scarab) items.Add(scarab);
                foreach (var (stat, name) in new[] { (Stat.MonsterDropAdditionalWisdomScrolls, "Scroll of Wisdom"), (Stat.MonsterDropAdditionalPortalScrolls, "Portal Scroll") })
                    for (int i = Chance(stat); i > 0; i--)
                        if (ModDrop(killer, name, level) is { } scroll) items.Add(scroll);
                if (Chance(Stat.MonsterDropAdditionalRingsAmulets) is int jewellery and > 0)
                    items.AddRange(drops.Container(level, jewellery, 0, random, t => t.Class is "Ring" or "Amulet"));
                for (int i = Chance(Stat.MonsterDropAdditionalCurrencyItems); i > 0; i--)
                    if (Currency(killer, level, random) is { } currency) items.Add(currency);
                for (int i = Chance(Stat.MonsterDropAdditionalVaalItems); i > 0; i--)
                    if (VaalItem(killer, level, random) is { } vaal) items.Add(vaal);
                if (mark.Grip)
                {
                    foreach (var (stat, kind) in PossessedDropChances)
                        if (AreaStat(killer, stat) is int chance and > 0 && random.Next(100) < chance)
                            if ((kind == "unique" ? Unique(killer, level, random) : kind == "map" ? drops.Map(level, MonsterRarity.Unique, Math.Max(1, tier), random)
                                : drops.Scarab(random, level)) is { } extra) items.Add(extra);
                    await Release(killer, area, mark, payout.At);
                }
            }
            await DropAll(killer, items, payout.At, payout.Name);
        }
    }

    // The Torment Scarab of Release (and an Idol's): each spirit in the slain monster has the chance to be set free,
    // active at once. The Torment Scarab's own chance also digs up a possessed monster. Under the gate.
    static async Task Release(Player player, Area area, Mark mark, (int X, int Y) at)
    {
        int chance = AreaStat(player, Stat.MapTormentedSpiritReleaseChance), dig = AreaStat(player, Stat.MapTormentedSpiritReleaseAndDigPctChance);
        if (chance <= 0 && dig <= 0) return;
        foreach (int row in mark.Spirits)
        {
            bool digs = Random.Shared.Next(100) < dig;
            if (!digs && Random.Shared.Next(100) >= chance) continue;
            var s = new Spirit { Index = area.Spirits!.Count, Row = row, X = at.X, Y = at.Y, State = SpiritState.Active };
            area.Spirits.Add(s);
            await Spawn(player, area, s);
            Console.WriteLine($"[torment] spirit {s.Index} set free at ({at.X}, {at.Y})");
            if (digs) await Dig(player, area, row, at);
        }
        Keep(player, area);
    }

    static ItemDrops.Drop? Currency(Player player, int level, Random random)
    {
        double roll = random.NextDouble() * ItemDrops.Currency.Sum(c => c.Weight);
        foreach (var (id, weight) in ItemDrops.Currency)
            if ((roll -= weight) < 0) return ModDrop(player, id, level);
        return null;
    }

    // A Vaal Cultist's "Vaal items": a Vaal skill gem of the area's level.
    static ItemDrops.Drop? VaalItem(Player player, int level, Random random)
    {
        var gems = ItemsFor(player).All.Where(t => t.Supported && t.Id.Contains("/SkillGemVaal", StringComparison.Ordinal) && t.DropLevel <= level).ToArray();
        return gems.Length == 0 ? null : ModDrop(player, gems[random.Next(gems.Length)].Id, level);
    }

    // Rare equipment: the container roll's equipment with its rarity raised to rare (a unique stays unique).
    static IEnumerable<ItemDrops.Drop> Rares(Player player, int level, int count, Random random)
    {
        if (count <= 0) return [];
        var rolls = RollsFor(player);
        return DropsFor(player).Container(level, count, 0, random, t => ItemDrops.EquipmentClasses.Contains(t.Class))
            .Select(d => d.Item.Rarity is ItemRarity.Rare or ItemRarity.Unique ? d
                : d with { Item = rolls.Roll(d.Item with { Rarity = ItemRarity.Normal, Mods = [.. d.Item.Mods.Where(m => m.Section != ModSection.Explicit)] },
                    ItemRarity.Rare, random) with { Identified = false } });
    }

    static ItemDrops.Drop? Unique(Player player, int level, Random random)
    {
        var catalog = ItemsFor(player);
        var uniques = UniquesFor(player);
        var pool = uniques.All.Where(u => !u.EncounterOnly && catalog.Find(u.BaseTypeId) is { } t && t.DropLevel <= level && ItemDrops.Droppable(t)).ToArray();
        if (DropsFor(player).PickUnique(pool, random) is not { } definition || uniques.Create(definition, random) is not { } item
            || catalog.ByHash(item.TypeHash) is not { } type) return null;
        return new(item with { Identified = false }, type);
    }

    static async Task DropAll(Player player, List<ItemDrops.Drop> items, (int X, int Y) at, string label)
    {
        if (items.Count == 0 || Environment.GetEnvironmentVariable("Game_DROPS") == "0" || !GameEvents.AllowsLoot(player)) return;
        foreach (var drop in ItemDrops.Stacked(items))
        {
            var cell = player.Layout.Near((at.X + Random.Shared.Next(-4, 5), at.Y + Random.Shared.Next(-4, 5)));
            var ground = new WorldItemObject(cell.X, cell.Y, drop.Item);
            uint id = player.Instance.Ground.Add(ground, player);
            await SendGroundItem(player, id, ground, $"torment: {label} drops {drop.Type.Name}", justDropped: true);
        }
    }

    public static void Describe(Player player, JsonObject state)
    {
        if (player.Instance is null || !Areas.TryGetValue(player.Instance, out var area) || area.Spirits is null) return;
        state["torment"] = new JsonObject
        {
            ["spirits"] = JsonSerializer.SerializeToNode(area.Spirits),
            ["marked"] = area.Marks.Count,
        };
    }

    // A possession ends with the area (its buff goes with the area too).
    public static void Leave(Player player) { Presented.Remove(player); Possessed.Remove(player); }
}
