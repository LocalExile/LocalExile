using Mark = TormentRewards.Mark;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public const string LifeStat = "torment_spirit_life_+%_final";
    const string TaperKey = "Torment/taper";
    /// <summary>A waiting or new spirit's less damage taken, handed over to MonsterModEffects.DamageTaken in GameModule.Load.</summary>
    public const string TaperStat = "torment_spirit_damage_taken_+%_final";

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

    sealed class Area(TormentRewards rewards)
    {
        public TormentRewards Rewards { get; } = rewards;
        public SemaphoreSlim Gate => Rewards.Gate;
        public List<Spirit>? Spirits;
        /// <summary>Touched and possessed monsters; with <see cref="SpiritMonsters"/>, under its own lock (kills read it).</summary>
        public Dictionary<uint, Mark> Marks => Rewards.Marks;
        /// <summary>The living spirits' monsters, so a kill finds its spirit without the gate.</summary>
        public readonly HashSet<uint> SpiritMonsters = [];
        public readonly HashSet<uint> Checked = [];
    }

    sealed class Shown
    {
        public readonly Dictionary<uint, int> Versions = [];
    }

    static readonly RuleTable<InstanceState, Area> Areas = new();
    static readonly RuleTable<ClientState, Shown> Presented = new();
    static readonly ConcurrentDictionary<(string Area, int Level), AreaInfo?> AreaInfos = new();

    // The drop stats of the Touch and Grip mods that MonsterKilled pays out (MonsterVarieties-independent).
    static readonly string[] DropStatIds =
    [
        "torment_spirit_haunted_additional_item_drop_chance_%", "monster_drop_additional_unique_items",
        "possessed_monster_drop_item_template_for_unique_item", "monster_drop_additional_map_items",
        "possessed_monster_drop_template_any_map", "possessed_monster_drop_template_any_scarab",
        "monster_drop_additional_wisdom_scrolls", "monster_drop_additional_portal_scrolls", "monster_drop_additional_rings_amulets",
        "monster_drop_additional_currency_items", "monster_drop_additional_vaal_items",
    ];

    // Map, Atlas and Idol stats this provider does not act on yet; an opening that has them says so. Wild Rogue Exiles
    // (Exiled Will, an Idol) come with the Anarchy league's content, the scarab weights with the drops' scarab types,
    // Refiner's Bargain's dust with Kingsmarch.
    static readonly string[] Unsupported =
    [
        "map_rogue_exiles_possessed_%_chance", "map_torment_scarab_more_likely_%",
        "map_possessed_monster_equipment_drops_converted_to_dust_%_chance",
    ];

    // A possessed monster's own chance to drop an item of a kind (the Atlas's sextant-era map stats).
    static readonly (string Stat, string Kind)[] PossessedDropChances =
    [
        ("map_possessed_monsters_drop_unique_chance_%", "unique"), ("map_possessed_monsters_drop_map_chance_%", "map"),
        ("map_possessed_monsters_drop_rusted_scarab_chance_%", "scarab"), ("map_possessed_monsters_drop_polished_scarab_chance_%", "scarab"),
        ("map_possessed_monsters_drop_gilded_scarab_chance_%", "scarab"), ("map_possessed_monsters_drop_winged_scarab_chance_%", "scarab"),
    ];

    static bool LeftInstance(InstanceState instance, ClientState player) =>
        player.Closed || player.TransferAccepted || player.WorldOutput.Closed || player.Instance != instance;

    static IEnumerable<ClientState> Members(ClientState conn) => Members(conn, conn.Instance).Where(c => !c.LeavingArea);

    static IEnumerable<ClientState> Members(ClientState conn, InstanceState instance) => instance.PlayersExcept(conn.PeerId)
        .Select(p => p.Owner).OfType<ClientState>().Prepend(conn)
        .Where(c => !LeftInstance(instance, c)).Distinct();

    static byte AreaLevel(ClientState conn) =>
        (byte)Math.Clamp(MapRunOf(conn) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);

    static int Stat(ClientState conn, string id) => MapModsOf(conn)?.Get(id) ?? 0;

    public static void Opening(MapOpening request)
    {
        if (request.Opening is not { } opening || TormentCatalog.All.Count == 0) return;
        request.HandledContent.Add(Content);
        var present = Unsupported.Where(s => opening.Mods.Get(s) != 0).ToArray();
        if (present.Length > 0)
            request.ContentWarnings.Add(("Torment.Parity",
                $"area={opening.Area.Id} content=Torment: spirits enabled; not acted on: {string.Join(", ", present)}"));
    }

    /// <summary>How many spirits the area gets: the client's own league roll over the area's stats (the map's, the
    /// scarabs' <c>map_spawn_extra_torment_spirits</c> and the Atlas tree's chance), 0 where its row allows none.</summary>
    static int Count(ClientState conn, byte level)
    {
        var info = AreaInfos.GetOrAdd((conn.Layout.Id, level), k =>
        {
            try { return AreaInfo.Load(GamePack.Default.TryRead, k.Area, k.Level); }
            catch (Exception e) when (e is ArgumentException or FileNotFoundException) { return null; }
        });
        if (info is null) return 0;
        var stats = conn.Instance.KeptAreaStats?.ToDictionary(s => (int)s.Key, s => s.Value) ?? [];
        return ExtraTerrainFeatures.LeagueCount(info with { Stats = stats }, new LevelRandom(conn.Seed ^ Salt, false), LeagueRow);
    }

    static Area? Of(ClientState conn)
    {
        if (Areas.TryGetValue(conn.Instance, out var area)) return area;
        // A module reload keeps the instance's own record (ContentState), not this table.
        if (!conn.Instance.ContentState.TryGetValue(SaveKey, out string? saved)) return null;
        area = Areas.GetValue(conn.Instance, instance => new(instance.World.TormentRewards));
        Load(area, saved, conn.Instance);
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
            if (area.Marks.Count == 0 && instance.ContentState.TryGetValue(MarksKey, out string? marks))
                foreach (var (id, mark) in JsonSerializer.Deserialize<Dictionary<uint, Mark>>(marks) ?? [])
                {
                    area.Marks[id] = mark;
                    area.Rewards.Version = Math.Max(area.Rewards.Version, mark.Version);
                }
        }
    }

    // Under the Marks lock: the marks are written with the instance on the next tick.
    static void KeepMarks(ClientState conn, Area area) => area.Rewards.MarksChanged = true;

    static void FlushMarks(ClientState conn, Area area)
    {
        lock (area.Marks)
        {
            if (!area.Rewards.MarksChanged) return;
            area.Rewards.MarksChanged = false;
            conn.Instance.ContentState[MarksKey] = JsonSerializer.Serialize(area.Marks);
        }
    }

    static void Keep(ClientState conn, Area area) => conn.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(area.Spirits);

    public static async Task Enter(ClientState conn)
    {
        if (TormentCatalog.All.Count == 0 || conn.Layout.Walkable is null
            || Packs.For(conn.Layout.Id)?.Scattered.Count is not > 0) return;
        var area = Areas.GetValue(conn.Instance, instance => new(instance.World.TormentRewards));
        await area.Gate.WaitAsync();
        try
        {
            if (area.Spirits is null)
            {
                if (conn.Instance.ContentState.TryGetValue(SaveKey, out string? saved)) Load(area, saved, conn.Instance);
                else
                {
                    area.Spirits = [];
                    byte level = AreaLevel(conn);
                    int count = Count(conn, level);
                    var pool = TormentCatalog.Pool(level, Stat(conn, "map_tormented_spirits_rare_varieties") > 0);
                    var random = new Random(unchecked((int)(conn.Seed ^ Salt)));
                    bool lessCommon = Stat(conn, "map_rare_spirits_more_common") > 0;
                    if (pool.Count > 0)
                        foreach (var (x, y) in MapStrongboxes.Positions(conn.Layout, conn.Seed ^ Salt, count))
                            if (TormentCatalog.Pick(pool, random, lessCommon) is { } pick)
                                area.Spirits.Add(new() { Index = area.Spirits.Count, Row = pick.Row, X = x, Y = y });
                    RollExtras(conn, area, random);
                    count = area.Spirits.Count;
                    Keep(conn, area);
                    if (count > 0) Console.WriteLine($"[map-content] torment area={conn.Layout.Id} level={level}: placed {area.Spirits.Count}/{count}");
                }
            }
            await Restore(conn, area);
        }
        finally { area.Gate.Release(); }
    }

    // Spawns the spirits that are waiting or active and have no monster in the instance: new ones, and the ones of a kept
    // instance, whose monsters came back as ordinary monsters under new ids (SavedMonster keeps no script): those go.
    static async Task Restore(ClientState conn, Area area)
    {
        var tracked = new HashSet<uint>();
        var missing = new List<Spirit>();
        lock (conn.Monsters)
            foreach (var s in area.Spirits!.Where(s => s.State is SpiritState.Waiting or SpiritState.Active))
                if (s.Monster != 0 && conn.Monsters.ContainsKey(s.Monster)) tracked.Add(s.Monster);
                else missing.Add(s);
        if (missing.Count == 0) return;
        uint[] strays;
        lock (conn.Monsters)
            strays = [.. conn.Monsters.Where(p => !tracked.Contains(p.Key) && TormentCatalog.ByVariety(p.Value.Variety?.Id) is not null)
                .Select(p => p.Key)];
        foreach (uint stray in strays) await Remove(conn, stray, "kept spirit replaced");
        foreach (var s in missing) await Spawn(conn, area, s);
        Keep(conn, area);
    }

    static async Task Spawn(ClientState conn, Area area, Spirit s)
    {
        if (TormentCatalog.ByRow(s.Row) is not { } spirit || Monsters.Find(spirit.Variety) is not { Supported: true } variety) return;
        byte level = AreaLevel(conn);
        var basis = MonsterModsFor(conn, variety, level, MonsterRarity.Unique, null, true);
        var own = TormentCatalog.Roll(spirit.Own, Random.Shared);
        var mods = MonsterModRolls.Value.Create([.. basis.Mods, .. own.Mods], basis.NameWords, MonsterRarity.Unique, level);
        mods = mods with { Effects = mods.Effects.Plus(LifeStat, TormentCatalog.LifePct(level))
            .Plus("base_movement_velocity_+%", Stat(conn, "map_tormented_spirits_movement_speed_+%")) };
        var w = conn.Wire;
        uint id = await SpawnMonster(w.Remote, w.Stream, w.Ciphers, conn, variety, level, MonsterRarity.Unique, s.X, s.Y,
            mods: mods, alone: true, scripted: true, movement: new MonsterMovement { ThroughBodies = true });
        if (id == 0) return;
        s.Monster = id;
        s.Taper = int.MinValue;
        lock (area.Marks) area.SpiritMonsters.Add(id);
        lock (conn.Monsters)
            if (conn.Monsters.TryGetValue(id, out var m)) SetTaper(m, s);
    }

    // The spirit's damage taken: 90% less while waiting, tapering to none over 15 seconds once active (a final factor),
    // and 5% increased per touch.
    static void SetTaper(SpawnedMonster m, Spirit s)
    {
        double left = s.State == SpiritState.Waiting ? 1 : Math.Max(0, 1 - s.Active / TaperSeconds);
        int less = -(int)Math.Round(TaperPct * left);
        if (less * 1000 + s.Touches == s.Taper) return;
        s.Taper = less * 1000 + s.Touches;
        m.TemporaryStats[TaperKey] = new Dictionary<string, int> { [TaperStat] = less, ["base_damage_taken_+%"] = PerTouchPct * s.Touches };
    }

    public static async Task Tick(ClientState conn, double seconds)
    {
        if (!conn.Closed && !conn.LeavingArea) await TickPossession(conn, seconds);
        if (conn.Closed || conn.LeavingArea || !conn.PlayerSent || conn.Instance is null) return;
        await FinishAbandonedMartyrs(conn);
        if (Of(conn) is not { } area) return;
        await area.Gate.WaitAsync();
        try
        {
            if (conn.Closed || conn.LeavingArea) return;
            if (area.Spirits is null) return;
            await PayOut(conn, area);
            await Present(conn, area);
            if (IsMonsterRunner(conn)) await Drive(conn, area, Math.Clamp(seconds, 0, 1));
            FlushMarks(conn, area);
        }
        finally { area.Gate.Release(); }
    }

    // What this client has not been shown of the touched and possessed monsters it has: their mods, stats and buff.
    static async Task Present(ClientState conn, Area area)
    {
        if (area.Marks.Count == 0) return;
        var shown = Presented.GetValue(conn, _ => new());
        var due = new List<(uint Id, Mark Mark)>();
        lock (conn.Monsters)
            lock (area.Marks)
                foreach (var (id, mark) in area.Marks)
                    if (conn.MonsterViews.ContainsKey(id) && !conn.AsleepMonsters.Contains(id)
                        && shown.Versions.GetValueOrDefault(id, -1) != mark.Version)
                        due.Add((id, mark));
        var w = conn.Wire;
        foreach (var (id, mark) in due)
            await SendWorldRecords(w.Remote, w.Stream, w.Ciphers, conn, id, () =>
            {
                lock (conn.Monsters)
                {
                    if (!conn.MonsterViews.ContainsKey(id) || conn.AsleepMonsters.Contains(id)
                        || !conn.Monsters.TryGetValue(id, out var monster)) return null;
                    var records = new List<byte[]> { mark.Magic, InstanceEntities.StatsUpdate(id, ClientStats(monster)) };
                    if (mark.Skills is { } skills) records.Add(skills);
                    if (!shown.Versions.ContainsKey(id) || mark.Grip)
                        records.Add(InstanceEntities.AddBuff(id, conn.NextBuff++,
                            mark.Grip ? TormentCatalog.PossessedBuff : TormentCatalog.TouchedBuff));
                    shown.Versions[id] = mark.Version;
                    return records.ToArray();
                }
            });
    }

    static async Task Drive(ClientState conn, Area area, double seconds)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        var players = Members(conn).Where(c => c.PlayerSent && c.PlayerLife > 0)
            .Select(c => c.Walk.Position(c.Clock.Elapsed.TotalSeconds)).ToArray();
        bool changed = false;
        List<(int X, int Y)> surround;
        lock (conn.Monsters) surround = FirstSight(conn, area, now, ref changed);
        bool speaker = SpeakerOfTheDead(conn);
        if (speaker) lock (conn.Monsters) PlayerTouches(conn, area, now, ref changed);
        double duration = ActiveSeconds * Math.Max(0, 1 + Stat(conn, "map_tormented_spirits_duration_+%") / 100.0);
        foreach (var s in area.Spirits!)
        {
            if (s.State is not (SpiritState.Waiting or SpiritState.Active) || TormentCatalog.ByRow(s.Row) is not { } spirit) continue;
            SpawnedMonster? m;
            (int X, int Y) at;
            uint? possess = null;
            ClientState? player = null;
            lock (conn.Monsters)
            {
                if (!conn.Monsters.TryGetValue(s.Monster, out m) || m.Dead)
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
                    if (speaker) player = PlayerContact(conn, spirit, at);
                    foreach (var (id, other) in conn.Monsters)
                    {
                        if (player is not null) break;
                        if (!Touchable(other) || Passed(from, at, other.Walk.Position(now)) > Contact(other)) continue;
                        if (other.Rarity is MonsterRarity.Rare or MonsterRarity.Unique)
                        {
                            if (speaker) continue;
                            Apply(conn, area, id, other, spirit, grip: true, now);
                            possess = id;
                            break;
                        }
                        if (s.Touches >= TouchLimit || TormentCatalog.Has(other.Mods, grip: false)) continue;
                        Apply(conn, area, id, other, spirit, grip: false, now);
                        s.Touches++;
                        changed = true;
                    }
                    SetTaper(m, s);
                }
            }
            if (player is not null)
            {
                s.State = SpiritState.Possessed;
                await Remove(conn, s.Monster, $"spirit possesses {CharacterName(player)}");
                changed = true;
                continue;
            }
            if (possess is uint into)
            {
                s.State = SpiritState.Possessed;
                Console.WriteLine($"[torment] spirit {s.Index} possesses 0x{into:x} after {s.Touches} touches");
                await Remove(conn, s.Monster, "spirit possesses a monster");
                changed = true;
                continue;
            }
            if (s.Active >= duration)
            {
                s.State = SpiritState.Gone;
                Console.WriteLine($"[torment] spirit {s.Index} leaves after {s.Active:0.#} s, {s.Touches} touches");
                await Remove(conn, s.Monster, "spirit's time is up");
                changed = true;
                continue;
            }
            if (now >= s.NextOrderAt) await Steer(conn, s, m!, at, players, now, speaker);
        }
        if (surround.Count > 0) { await AddSpirits(conn, area, surround); changed = true; }
        if (changed) Keep(conn, area);
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
    static async Task Steer(ClientState conn, Spirit s, SpawnedMonster m, (int X, int Y) at, (int X, int Y)[] players, double now, bool speaker)
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
            lock (conn.Monsters)
            {
                var near = conn.Monsters.Values.Where(o => Touchable(o) && Distance(o.Walk.Position(now), at) <= SeekRange)
                    .Select(o => (Monster: o, At: o.Walk.Position(now))).ToArray();
                var target = stuck ? null : near.Where(o => !speaker && o.Monster.Rarity is MonsterRarity.Rare or MonsterRarity.Unique).OrderBy(o => Distance(o.At, at))
                    .Select(o => ((int, int)?)o.At).FirstOrDefault()
                    ?? (s.Touches < TouchLimit ? near.Where(o => !TormentCatalog.Has(o.Monster.Mods, grip: false)).OrderBy(o => Distance(o.At, at))
                        .Select(o => ((int, int)?)o.At).FirstOrDefault() : null);
                // A monster moves: while it chases one it is ordered again every tick.
                if (target is not null) s.NextOrderAt = now;
                if (target is null && players.Length > 0)
                {
                    var player = players.MinBy(p => Distance(p, at));
                    target = MonsterFlee.Point(at, player, m.Walk.Floor, Random.Shared);
                }
                // Nothing to chase and no way away: it drifts somewhere near.
                target ??= MonsterFlee.Point(at, at, m.Walk.Floor, Random.Shared);
                to = target;
            }
        }
        if (to is not { } dest || dest == m.Walk.Destination && dest != at) return;
        int generation;
        lock (conn.Monsters) generation = ++m.ActionGeneration;
        var w = conn.Wire;
        await SendMonsterAction(w.Remote, w.Stream, w.Ciphers, conn, s.Monster, m, generation, () =>
        {
            double t = conn.Clock.Elapsed.TotalSeconds;
            var here = m.Walk.Position(t);
            if (dest == at) m.Walk.Teleport(here.X, here.Y, t);
            else m.Walk.Order(dest.X, dest.Y, t);
            var goal = dest == at ? here : dest;
            return InstanceProtocol.ActionStarted(s.Monster, here.X, here.Y, goal.X, goal.Y, 0x2909, conn.NextActionSequence(), conn.MonsterOrderFlags);
        });
    }

    /// <summary>Gives a monster a spirit's Touch or Grip, under the Monsters lock: its mods (kept with the instance and
    /// shown on its name plate), what they change (life, hit, attack time, walk, drops) and the record later clients get.</summary>
    static void Apply(ClientState conn, Area area, uint id, SpawnedMonster m, TormentCatalog.Spirit spirit, bool grip, double now)
    {
        var (mods, stats) = TormentCatalog.Roll(grip ? spirit.Grip : spirit.Touch, Random.Shared);
        // A Blessed map's "Possessed Monsters take increased Damage".
        if (grip && Stat(conn, "map_monster_damage_taken_+%_while_possessed") is int taken and not 0)
            stats["base_damage_taken_+%"] = stats.GetValueOrDefault("base_damage_taken_+%") + taken;
        var before = m.Effects;
        m.Mods = m.Mods with { Mods = [.. m.Mods.Mods, .. mods], Effects = m.Mods.Effects.Combine(stats) };
        var after = m.Effects;
        m.RefreshLife(before, after);
        BossKits.RefreshAttacks(conn, m);
        if (after.MovementPct != before.MovementPct)
            m.Walk.SetSpeed(m.Walk.Speed * (100.0 + after.MovementPct) / Math.Max(1, 100.0 + before.MovementPct), now);
        Show(conn, area, id, m, mark =>
        {
            mark.Grip |= grip;
            if (grip) mark.Spirits.Add(spirit.Row);
            foreach (string stat in DropStatIds)
                if (stats.GetValueOrDefault(stat) is int value and not 0) mark.DropStats[stat] = mark.DropStats.GetValueOrDefault(stat) + value;
        }, refreshSkills: true);
        Console.WriteLine($"[torment] {(grip ? "possessed" : "touched")} 0x{id:x} {m.Variety?.Id} ({m.Rarity}) by {spirit.Variety}");
    }

    // Replace the ordinary actions' final values while keeping each granted record's native stats and extra sets.
    static Dictionary<ushort, GrantedSkill> AttackSkills(SpawnedMonster m, IReadOnlyList<GrantedSkill> skills)
    {
        var changed = new Dictionary<ushort, GrantedSkill>();
        foreach (var skill in skills)
        {
            if (BossKits.AttackById(m, skill.Id) is not { } attack || skill.Stats is null) continue;
            var values = attack.SkillStats().ToDictionary(s => s.Item1, s => s.Item2);
            changed.Add(skill.Id, skill with { Stats = [.. skill.Stats.Select(s => (s.Key, values.GetValueOrDefault(s.Key, s.Value)))] });
        }
        return changed;
    }

    // A mark outlives temporary auras. Read the host's current values for the player receiving the mark.
    static List<(uint Key, int Value)> ClientStats(SpawnedMonster m)
    {
        var effects = m.Effects;
        var client = new List<(uint Key, int Value)> { (StatKeys.MaximumLife, (int)Math.Min(int.MaxValue, m.MaxLife)),
            (StatKeys.MovementVelocityPct, effects.MovementPct) };
        if (Program.Stats.Key("actor_scale_+%") is uint actorScale) client.Add((actorScale, effects.ActorScalePct));
        var basic = m.Kit.Count > 0 ? m.Kit[0].Attack : m.Attack;
        if (basic is not null) client.AddRange(basic.Stats());
        return client;
    }

    /// <summary>Under the Monsters lock: a touched or possessed monster's record for the clients (its mods, the stats they
    /// change, the record later clients are created from) and its mark, changed by <paramref name="change"/>.</summary>
    static void Show(ClientState conn, Area area, uint id, SpawnedMonster m, Action<Mark> change, bool refreshSkills = false)
    {
        byte[]? skillUpdate = null;
        // The record a client that comes later is created from (SyncInstanceMonsters).
        string? name = null;
        if (conn.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
        {
            var skills = origin.Object.Skills;
            if (refreshSkills)
            {
                var changed = skills is not null && !m.Scripted && m.Attack is { Scripted: false } ? AttackSkills(m, skills) : [];
                if (changed.Count > 0) skills = [.. skills!.Select(s => changed.GetValueOrDefault(s.Id, s))];
                skills = TormentCatalog.WithMartyrSkill(conn, m.Mods, skills, out var martyr);
                if (martyr is not null) changed[martyr.Id] = martyr;
                if (changed.Count > 0)
                {
                    byte actor = checked((byte)origin.Object.Components.ToList().IndexOf("Actor"));
                    skillUpdate = InstanceEntities.ActorSkills(id, actor, [.. changed.Values]);
                }
            }
            conn.Instance.World.MonsterOrigins[id] = origin with
            {
                Object = origin.Object.WithStats(ClientStats(m)) with
                {
                    Mods = m.Mods.Mods,
                    Skills = skills,
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
            mark.Version = ++area.Rewards.Version;
            mark.Magic = InstanceEntities.ComponentUpdate(id, 2, magic.ToArray());
            mark.Skills = skillUpdate;
            KeepMarks(conn, area);
        }
    }

    /// <summary>Under the Monsters lock: a monster that came back with a Touch or Grip in its kept mods (a kept instance
    /// gives it a new id) gets its mark again, from those mods; it is not touched or possessed again.</summary>
    static bool Remark(ClientState conn, Area area, uint id, SpawnedMonster m)
    {
        bool grip = TormentCatalog.Has(m.Mods, grip: true), touch = TormentCatalog.Has(m.Mods, grip: false);
        if (!grip && !touch) return false;
        lock (area.Marks) if (area.Marks.ContainsKey(id)) return true;
        Show(conn, area, id, m, mark =>
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

    // A committed instance event belongs to each remaining viewer, even when its original projectile or runner retires.
    static async Task PublishToMember(InstanceState instance, ClientState player, Func<Task> publish)
    {
        bool Current() => CurrentGameRules && !player.LeavingArea && !LeftInstance(instance, player);
        var previous = ProjectileOutputGuard.Value;
        ProjectileOutputGuard.Value = Current;
        try
        {
            if (Current()) await publish();
        }
        catch (ObjectDisposedException) when (LeftInstance(instance, player))
        {
            // A completed transfer closes this recipient's old WorldOutput while its TCP connection is still open.
        }
        catch (Exception ex) when (ex is IOException { InnerException: System.Net.Sockets.SocketException }
            || ex is ObjectDisposedException && !player.Wire.Stream.CanWrite)
        {
            Console.WriteLine($"[gateway] {player.Wire.Remote} Torment output failed: {ex.Message}");
            FailWorldOutput(player);
        }
        finally { ProjectileOutputGuard.Value = previous; }
    }

    static HashSet<ClientState> RetireMonsterLocked(InstanceState instance, uint id, IEnumerable<ClientState> viewers)
    {
        if (Areas.TryGetValue(instance, out var area)) lock (area.Marks) area.SpiritMonsters.Remove(id);
        if (instance.World.Monsters.Remove(id, out var m)) { m.Dead = true; m.Life = 0; ++m.ActionGeneration; }
        instance.World.MonsterOrigins.TryRemove(id, out _);
        HashSet<ClientState> shown = [];
        foreach (var p in viewers)
        {
            if (LeftInstance(instance, p)) continue;
            if (p.MonsterViews.Remove(id)) shown.Add(p);
            p.AsleepMonsters.Remove(id);
        }
        return shown;
    }

    static async Task Remove(ClientState conn, uint id, string why)
    {
        var instance = conn.Instance;
        HashSet<ClientState> shown;
        lock (instance.World.Monsters) shown = RetireMonsterLocked(instance, id, Members(conn));
        await Task.WhenAll(shown.Select(p => PublishToMember(instance, p, () =>
            SendWorldRemoval(p.Wire.Remote, p.Wire.Stream, p.Wire.Ciphers, p, id, $"[torment] 0x{id:x} removed: {why}"))));
    }

    /// <summary>Per kill: a touched or possessed monster's more items from the map
    /// (<c>map_possessed_and_touched_monsters_item_quantity_+%</c>).</summary>
    public static void Drops(DropRoll roll)
    {
        if (roll.Monster.ObjectId == 0 || !Areas.TryGetValue(roll.Conn.Instance, out var area) || area.Spirits is null) return;
        lock (area.Marks)
            if (area.Marks.ContainsKey(roll.Monster.ObjectId)
                && Stat(roll.Conn, "map_possessed_and_touched_monsters_item_quantity_+%") is int quantity and not 0)
                roll.Quantity *= Math.Max(0, 1 + quantity / 100.0);
    }

    /// <summary>GameEvents.Kill: claims a slain spirit's or a touched or possessed monster's payout once.</summary>
    public static void Kill(KillContext kill)
    {
        uint id = kill.Monster.ObjectId;
        if (id == 0 || kill.Conn.Instance is null || !Areas.TryGetValue(kill.Conn.Instance, out var area)) return;
        Mark? mark;
        bool spirit;
        lock (area.Marks)
        {
            spirit = area.SpiritMonsters.Remove(id);
            if (area.Marks.TryGetValue(id, out mark) && !mark.Paid) mark.Paid = true;
            else mark = null;
            if (!spirit && mark is null) return;
            int row = spirit ? TormentCatalog.ByVariety(kill.VarietyId)?.Row ?? -1 : -1;
            area.Rewards.Payouts.Enqueue(new(kill.Conn, id, row, mark, kill.Monster.Rarity, kill.Monster.Level, kill.At,
                kill.Monster.Variety?.Name ?? kill.VarietyId));
            KeepMarks(kill.Conn, area);
        }
    }

    // Under the gate, on a tick: the kills claimed since the last one.
    static async Task PayOut(ClientState conn, Area area)
    {
        while (area.Rewards.Payouts.TryDequeue(out var payout))
        {
            var killer = payout.Killer is { Closed: false, LeavingArea: false } k && k.Instance == conn.Instance ? k : conn;
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
                    Keep(conn, area);
                    Console.WriteLine($"[torment] spirit {slain.Index} slain after {slain.Touches} touches");
                }
                // poewiki: a slain spirit always drops a rare; the Peculiarity's spirits also their own kind of item.
                items.AddRange(Rares(killer, level, 1 + Math.Max(0, Stat(killer, "map_tormented_spirits_drop_x_additional_rare_items")), random));
                if (TormentCatalog.ByRow(payout.SpiritRow) is { Unusual: true } unusual
                    && (unusual.Variety.EndsWith("Betrayer", StringComparison.Ordinal) ? drops.Scarab(random, level)
                        : unusual.Variety.EndsWith("Felon", StringComparison.Ordinal) ? Unique(killer, level, random)
                        : drops.Map(level, MonsterRarity.Unique, Math.Max(1, tier), random)) is { } own) items.Add(own);
            }
            if (payout.Mark is { } mark)
            {
                int Chance(string stat) => mark.DropStats.GetValueOrDefault(stat);
                int Count(string stat) => Chance(stat) / 100 + (random.Next(100) < Chance(stat) % 100 ? 1 : 0);
                // Each Grip's 100% additional item, rare; poewiki: a possessed rare drops at least two rares, a unique three.
                int rares = Count("torment_spirit_haunted_additional_item_drop_chance_%");
                if (mark.Grip) rares = Math.Max(rares, payout.Rarity == MonsterRarity.Unique ? 3 : 2);
                items.AddRange(Rares(killer, level, rares, random));
                for (int i = Chance("monster_drop_additional_unique_items") + Count("possessed_monster_drop_item_template_for_unique_item"); i > 0; i--)
                    if (Unique(killer, level, random) is { } unique) items.Add(unique);
                for (int i = Chance("monster_drop_additional_map_items") + Count("possessed_monster_drop_template_any_map"); i > 0; i--)
                    if (drops.Map(level, MonsterRarity.Unique, Math.Max(1, tier), random) is { } map) items.Add(map);
                for (int i = Count("possessed_monster_drop_template_any_scarab"); i > 0; i--)
                    if (drops.Scarab(random, level) is { } scarab) items.Add(scarab);
                foreach (var (stat, name) in new[] { ("monster_drop_additional_wisdom_scrolls", "Scroll of Wisdom"), ("monster_drop_additional_portal_scrolls", "Portal Scroll") })
                    for (int i = Chance(stat); i > 0; i--)
                        if (ModDrop(killer, name, level) is { } scroll) items.Add(scroll);
                if (Chance("monster_drop_additional_rings_amulets") is int jewellery and > 0)
                    items.AddRange(drops.Container(level, jewellery, 0, random, t => t.Class is "Ring" or "Amulet"));
                for (int i = Chance("monster_drop_additional_currency_items"); i > 0; i--)
                    if (Currency(killer, level, random) is { } currency) items.Add(currency);
                for (int i = Chance("monster_drop_additional_vaal_items"); i > 0; i--)
                    if (VaalItem(killer, level, random) is { } vaal) items.Add(vaal);
                if (mark.Grip)
                {
                    foreach (var (stat, kind) in PossessedDropChances)
                        if (Stat(killer, stat) is int chance and > 0 && random.Next(100) < chance)
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
    static async Task Release(ClientState conn, Area area, Mark mark, (int X, int Y) at)
    {
        int chance = Stat(conn, "map_tormented_spirit_release_chance"), dig = Stat(conn, "map_tormented_spirit_release_and_dig_%_chance");
        if (chance <= 0 && dig <= 0) return;
        foreach (int row in mark.Spirits)
        {
            bool digs = Random.Shared.Next(100) < dig;
            if (!digs && Random.Shared.Next(100) >= chance) continue;
            var s = new Spirit { Index = area.Spirits!.Count, Row = row, X = at.X, Y = at.Y, State = SpiritState.Active };
            area.Spirits.Add(s);
            await Spawn(conn, area, s);
            Console.WriteLine($"[torment] spirit {s.Index} set free at ({at.X}, {at.Y})");
            if (digs) await Dig(conn, area, row, at);
        }
        Keep(conn, area);
    }

    static ItemDrops.Drop? Currency(ClientState conn, int level, Random random)
    {
        double roll = random.NextDouble() * ItemDrops.Currency.Sum(c => c.Weight);
        foreach (var (id, weight) in ItemDrops.Currency)
            if ((roll -= weight) < 0) return ModDrop(conn, id, level);
        return null;
    }

    // A Vaal Cultist's "Vaal items": a Vaal skill gem of the area's level.
    static ItemDrops.Drop? VaalItem(ClientState conn, int level, Random random)
    {
        var gems = ItemsFor(conn).All.Where(t => t.Supported && t.Id.Contains("/SkillGemVaal", StringComparison.Ordinal) && t.DropLevel <= level).ToArray();
        return gems.Length == 0 ? null : ModDrop(conn, gems[random.Next(gems.Length)].Id, level);
    }

    // Rare equipment: the container roll's equipment with its rarity raised to rare (a unique stays unique).
    static IEnumerable<ItemDrops.Drop> Rares(ClientState conn, int level, int count, Random random)
    {
        if (count <= 0) return [];
        var rolls = RollsFor(conn);
        return DropsFor(conn).Container(level, count, 0, random, t => ItemDrops.EquipmentClasses.Contains(t.Class))
            .Select(d => d.Item.Rarity is ItemRarity.Rare or ItemRarity.Unique ? d
                : d with { Item = rolls.Roll(d.Item with { Rarity = ItemRarity.Normal, Mods = [.. d.Item.Mods.Where(m => m.Section != ModSection.Explicit)] },
                    ItemRarity.Rare, random) with { Identified = false } });
    }

    static ItemDrops.Drop? Unique(ClientState conn, int level, Random random)
    {
        var catalog = ItemsFor(conn);
        var uniques = UniquesFor(conn);
        var pool = uniques.All.Where(u => !u.EncounterOnly && catalog.Find(u.BaseTypeId) is { } t && t.DropLevel <= level && ItemDrops.Droppable(t)).ToArray();
        if (DropsFor(conn).PickUnique(pool, random) is not { } definition || uniques.Create(definition, random) is not { } item
            || catalog.ByHash(item.TypeHash) is not { } type) return null;
        return new(item with { Identified = false }, type);
    }

    static async Task DropAll(ClientState conn, List<ItemDrops.Drop> items, (int X, int Y) at, string label)
    {
        if (items.Count == 0 || Environment.GetEnvironmentVariable("Game_DROPS") == "0" || !GameEvents.AllowsLoot(conn)) return;
        var (remote, stream, ciphers) = conn.Wire;
        foreach (var drop in ItemDrops.Stacked(items))
        {
            var cell = conn.Layout.Near((at.X + Random.Shared.Next(-4, 5), at.Y + Random.Shared.Next(-4, 5)));
            var ground = new WorldItemObject(cell.X, cell.Y, drop.Item);
            uint id = conn.Instance.Ground.Add(ground, conn);
            await SendGroundItem(remote, stream, ciphers, conn, id, ground, $"torment: {label} drops {drop.Type.Name}", justDropped: true);
        }
    }

    public static void Describe(ClientState conn, JsonObject state)
    {
        if (conn.Instance is null || !Areas.TryGetValue(conn.Instance, out var area) || area.Spirits is null) return;
        state["torment"] = new JsonObject
        {
            ["spirits"] = JsonSerializer.SerializeToNode(area.Spirits),
            ["marked"] = area.Marks.Count,
        };
    }

    // A possession ends with the area (its buff goes with the area too).
    public static void Leave(ClientState conn) { Presented.Remove(conn); Possessed.Remove(conn); }
}
