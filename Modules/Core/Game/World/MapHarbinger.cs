using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using static Game;
using static HarbingerCatalog;
using static Program;

/// <summary>
/// Harbingers (CoreLeagues row 8, Harbinger): invulnerable unique avatars that activate when a player approaches,
/// summon repeat waves (<c>skill_repeat_count</c>) of area monsters tinted with the Harbinger minion buff, cast their
/// real <c>GrantedEffects</c> skills and auras alongside King Harbinger helpers and Empowered Obelisks, and lose a
/// fixed permyriad of their life for each summoned minion slain until defeated, dropping currency shards (unaffected by
/// IIQ/IIR). Supports King Harbinger bosses (Regency / First Wave), Empowered Obelisks (Obelisks scarab), shard
/// duplication (Warhoards), and all composed Atlas/Idol/map Harbinger stats read from <see cref="MapModsOf"/>.
/// </summary>
public static partial class MapHarbinger
{
    public const string Content = "Harbinger";
    const string SaveKey = "Harbinger/encounters";
    const int LeagueRow = 8;
    const uint Salt = 0x48415242; // "HARB": independent stream of the instance seed.

    /// <summary>Harbinger max life (10,000 gives exact permyriad life-loss steps; client renders life / maxLife).</summary>
    public const uint HarbingerMaxLife = 10_000;
    const uint HelperMaxLife = 1_000;

    // Activation range (cells), repeat sub-wave delay, obelisk cadence & caps, transient effect durations.
    const double ActivationRange = 60, ObeliskFirstDelay = 4.0, ObeliskCooldown = 20.0, WaveRepeatInterval = 0.6;
    const double SummonEffectSeconds = 0.8, DefeatEffectSeconds = 1.2;
    const int MaxAliveObelisks = 2, MaxTotalObelisks = 4, KingHelperCount = 3;

    // Shared immutable invulnerability stat map (Combat.cs & MonsterMods.cs honor base_cannot_be_damaged = 1).
    static readonly IReadOnlyDictionary<string, int> InvulnerableStats =
        new Dictionary<string, int>(StringComparer.Ordinal) { ["base_cannot_be_damaged"] = 1 };

    // Legacy / unimplemented Harbinger stats: warn in MapOpening when present, like MapTorment.Unsupported.
    static readonly string[] Unsupported =
    [
        "map_boss_accompanied_by_harbinger",
        "map_number_of_harbinger_portals",
        "memory_line_player_is_harbinger",
        "map_harbinger_portal_drops_additional_fragments",
    ];

    public enum HarbingerState { Waiting, Active, Defeated }

    public sealed class Encounter
    {
        public int Index { get; set; }
        public HarbingerKind Kind { get; set; }
        public string Variety { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public HarbingerState State { get; set; }
        public uint Life { get; set; } = HarbingerMaxLife;
        public uint Monster { get; set; }
        public List<uint> Helpers { get; set; } = [];
        public List<uint> Minions { get; set; } = [];
        public List<uint> Obelisks { get; set; } = [];
        public int ObelisksSummoned { get; set; }

        [System.Text.Json.Serialization.JsonIgnore] public double WaveCooldown;
        [System.Text.Json.Serialization.JsonIgnore] public int PendingWaveRepeats;
        [System.Text.Json.Serialization.JsonIgnore] public int PendingWaveRemaining;
        [System.Text.Json.Serialization.JsonIgnore] public double WaveRepeatDelay;
        [System.Text.Json.Serialization.JsonIgnore] public double ObeliskCooldown;
    }

    sealed record MinionLink(int HarbingerIndex, int Permyriad);
    sealed record PendingKill(ClientState Killer, bool IsObelisk, uint MonsterId, int HarbingerIndex, int Permyriad, int Level, (int X, int Y) At);
    sealed class PendingEffect(uint objectId, double remaining)
    {
        public uint ObjectId { get; } = objectId;
        public double Remaining { get; set; } = remaining;
    }

    sealed class Area
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public List<Encounter>? Encounters;
        public readonly Lock TrackingLock = new();
        public readonly Dictionary<uint, MinionLink> MinionToHarbinger = [];
        public readonly Dictionary<uint, int> ObeliskToHarbinger = [];
        public readonly ConcurrentQueue<PendingKill> Kills = new();
        public readonly List<PendingEffect> Effects = [];
        public readonly Dictionary<uint, Dictionary<int, double>> ActorCooldowns = [];
        public readonly Dictionary<(uint SourceId, string AuraKey), ActiveTimedBuff> ActiveTimedAuras = [];
        public readonly Dictionary<(uint MonsterId, string AuraKey), AppliedAllyAura> AppliedAllyAuras = [];
        public readonly List<GroundHazard> Hazards = [];
        public readonly Dictionary<ClientState, double[]> GroundCarry = [];
        public volatile bool HasTracked;
        public volatile bool HasLive;
        public volatile bool HasEffects;
        public volatile bool HasHazards;
        public bool NeedsTick => HasLive || HasEffects || HasHazards || !Kills.IsEmpty;
    }

    static readonly RuleTable<InstanceState, Area> Areas = new();
    static readonly RuleTable<ClientState, HashSet<uint>> ShownMinionBuffs = new();
    static readonly ConcurrentDictionary<(string Area, int Level), AreaInfo?> AreaInfos = new();

    static IEnumerable<ClientState> Members(ClientState conn) => conn.Instance.PlayersExcept(conn.PeerId)
        .Select(p => p.Owner).OfType<ClientState>().Prepend(conn)
        .Where(c => !c.Closed && !c.LeavingArea && c.Instance == conn.Instance).Distinct();

    static byte AreaLevel(ClientState conn) =>
        (byte)Math.Clamp(MapRunOf(conn) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);

    static int Stat(ClientState conn, string id) => MapModsOf(conn)?.Get(id) ?? 0;

    static double Distance((int X, int Y) a, (int X, int Y) b) =>
        Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>GameEvents.MapOpening: claim Harbinger scarab content and warn on unsupported legacy stats.</summary>
    public static void Opening(MapOpening request)
    {
        if (request.Opening is not { } opening || HarbingerCatalog.All.Count == 0) return;
        request.HandledContent.Add(Content);
        var present = Unsupported.Where(s => opening.Mods.Get(s) != 0).ToArray();
        if (present.Length > 0)
            request.ContentWarnings.Add(("Harbinger.Parity",
                $"area={opening.Area.Id} content=Harbinger: harbingers enabled; not acted on: {string.Join(", ", present)}"));
    }

    /// <summary>How many Harbingers the area gets from CoreLeagues row 8 (scarabs + Atlas/Idol chance + exclusions)
    /// plus any guaranteed Uber/King Harbingers (<c>map_contains_X_additional_uber_harbingers</c>).</summary>
    public static (int Normal, int GuaranteedKing) Count(ClientState conn, byte level)
    {
        int guaranteedKing = Math.Max(0, Stat(conn, "map_contains_X_additional_uber_harbingers"));
        if (Stat(conn, "map_disable_harbingers") > 0) return (0, 0);

        var info = AreaInfos.GetOrAdd((conn.Layout.Id, level), k =>
        {
            try { return AreaInfo.Load(GamePack.Default.TryRead, k.Area, k.Level); }
            catch (Exception e) when (e is ArgumentException or FileNotFoundException) { return null; }
        });
        int normal = 0;
        if (info is not null)
        {
            var stats = conn.Instance.KeptAreaStats?.ToDictionary(s => (int)s.Key, s => s.Value) ?? [];
            normal = ExtraTerrainFeatures.LeagueCount(info with { Stats = stats }, new LevelRandom(conn.Seed ^ Salt, false), LeagueRow);
        }
        else
        {
            normal = Math.Max(0, Stat(conn, "map_num_extra_harbingers"));
            int chance = Math.Clamp(Stat(conn, "map_additional_harbinger_chance_%")
                + Stat(conn, "map_league_content_additional_chance_%"), 0, 100);
            if (chance > 0 && new Random(unchecked((int)(conn.Seed ^ Salt))).Next(100) < chance) normal++;
        }
        if (normal == 0 && Stat(conn, "map_spawn_harbingers") > 0) normal = 1;
        return (normal, guaranteedKing);
    }

    static Area? Of(ClientState conn)
    {
        if (Areas.TryGetValue(conn.Instance, out var area)) return area;
        if (!conn.Instance.ContentState.TryGetValue(SaveKey, out string? saved)) return null;
        area = Areas.GetValue(conn.Instance, _ => new());
        Load(area, saved, AreaLevel(conn));
        return area;
    }

    static void Load(Area area, string saved, int level)
    {
        lock (area.TrackingLock)
        {
            if (area.Encounters is not null) return;
            area.Encounters = JsonSerializer.Deserialize<List<Encounter>>(saved) ?? [];
            RebuildTrackingLocked(area, level);
        }
    }

    static void RebuildTrackingLocked(Area area, int level)
    {
        area.MinionToHarbinger.Clear();
        area.ObeliskToHarbinger.Clear();
        bool live = false;
        if (area.Encounters is not null)
        {
            foreach (var h in area.Encounters)
            {
                if (h.State is HarbingerState.Waiting or HarbingerState.Active) live = true;
                int permyriad = HarbingerCatalog.Profile(h.Kind, level).Permyriad;
                if (h.State != HarbingerState.Defeated)
                    foreach (uint id in h.Minions) area.MinionToHarbinger[id] = new(h.Index, permyriad);
                foreach (uint id in h.Obelisks) area.ObeliskToHarbinger[id] = h.Index;
            }
        }
        area.HasTracked = area.MinionToHarbinger.Count > 0 || area.ObeliskToHarbinger.Count > 0;
        area.HasLive = live;
    }

    static void Keep(ClientState conn, Area area) =>
        conn.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(area.Encounters);

    /// <summary>GameEvents.AreaGenerated: roll Harbingers once per instance and spawn waiting/active encounters.</summary>
    public static async Task Enter(ClientState conn)
    {
        if (HarbingerCatalog.All.Count == 0 || conn.Layout.Walkable is null
            || Packs.For(conn.Layout.Id)?.Scattered.Count is not > 0) return;

        var area = Areas.GetValue(conn.Instance, _ => new());
        await area.Gate.WaitAsync();
        try
        {
            byte level = AreaLevel(conn);
            if (area.Encounters is null)
            {
                if (conn.Instance.ContentState.TryGetValue(SaveKey, out string? saved))
                    Load(area, saved, level);
                else
                {
                    area.Encounters = [];
                    var (normalCount, guaranteedKing) = Count(conn, level);
                    int total = normalCount + guaranteedKing;
                    if (total > 0)
                    {
                        var pool = HarbingerCatalog.Pool(level);
                        var random = new Random(unchecked((int)(conn.Seed ^ Salt)));
                        int bossChance = Math.Clamp(Stat(conn, "map_harbingers_%_chance_to_be_replaced_as_atlas_boss"), 0, 100);
                        var cells = MapStrongboxes.Positions(conn.Layout, conn.Seed ^ Salt, total);
                        for (int i = 0; i < cells.Count; i++)
                        {
                            bool isKing = i < guaranteedKing || (bossChance > 0 && random.Next(100) < bossChance);
                            HarbingerKind kind;
                            string variety;
                            if (isKing && Monsters.Find(KingVariety) is { Supported: true })
                            {
                                kind = HarbingerKind.King;
                                variety = KingVariety;
                            }
                            else if (HarbingerCatalog.Pick(pool, random) is { } pick)
                            {
                                kind = pick.Strong ? HarbingerKind.Strong : HarbingerKind.Normal;
                                variety = pick.Variety;
                            }
                            else continue;

                            area.Encounters.Add(new()
                            {
                                Index = area.Encounters.Count,
                                Kind = kind,
                                Variety = variety,
                                X = cells[i].X,
                                Y = cells[i].Y,
                                State = HarbingerState.Waiting,
                                Life = HarbingerMaxLife,
                            });
                        }
                    }
                    lock (area.TrackingLock) RebuildTrackingLocked(area, level);
                    Keep(conn, area);
                    if (area.Encounters.Count > 0)
                    {
                        int kings = area.Encounters.Count(h => h.Kind == HarbingerKind.King);
                        Console.WriteLine($"[map-content] harbinger area={conn.Layout.Id} level={level}: placed {area.Encounters.Count} ({kings} boss)");
                    }
                }
            }
            if (area.HasLive) await Restore(conn, area);
        }
        finally { area.Gate.Release(); }
    }

    static async Task Restore(ClientState conn, Area area)
    {
        var tracked = new HashSet<uint>();
        var missing = new List<Encounter>();
        var missingHelpers = new List<Encounter>();
        lock (conn.Monsters)
        {
            foreach (var h in area.Encounters!)
            {
                if (h.State is HarbingerState.Waiting or HarbingerState.Active)
                {
                    if (h.Monster != 0 && conn.Monsters.ContainsKey(h.Monster)) tracked.Add(h.Monster);
                    else missing.Add(h);
                    if (h.Kind == HarbingerKind.King)
                    {
                        h.Helpers.RemoveAll(id => !conn.Monsters.ContainsKey(id));
                        foreach (uint id in h.Helpers) tracked.Add(id);
                        if (h.State == HarbingerState.Active && h.Helpers.Count == 0) missingHelpers.Add(h);
                    }
                }
                foreach (uint id in h.Obelisks)
                    if (conn.Monsters.ContainsKey(id)) tracked.Add(id);
            }
        }
        if (missing.Count == 0 && missingHelpers.Count == 0) return;

        // Clean up any unmanaged living restored Harbinger/helper/obelisk copies from a kept instance.
        uint[] strays;
        lock (conn.Monsters)
            strays = [.. conn.Monsters
                .Where(p => !p.Value.Dead && !tracked.Contains(p.Key) && HarbingerCatalog.IsManagedVariety(p.Value.Variety?.Id))
                .Select(p => p.Key)];
        foreach (uint stray in strays) await Remove(conn, stray, "kept harbinger replaced");

        foreach (var h in missing) await SpawnHarbinger(conn, h);
        foreach (var h in missingHelpers) await SpawnKingHelpers(conn, h);
        Keep(conn, area);
    }

    static async Task SpawnHarbinger(ClientState conn, Encounter h)
    {
        if (Monsters.Find(h.Variety) is not { Supported: true } variety) return;
        byte level = AreaLevel(conn);
        var w = conn.Wire;
        uint id = await SpawnMonster(w.Remote, w.Stream, w.Ciphers, conn, variety, level, MonsterRarity.Unique,
            h.X, h.Y, name: null, damagedLife: h.Life < HarbingerMaxLife ? h.Life : null, alone: true, unrisen: false,
            owned: (_, m) => { m.EncounterStats = InvulnerableStats; m.NoRewards = true; },
            ownedMaximumLife: HarbingerMaxLife, scripted: true,
            ownedSkills: BuildOwnedSkills(conn, variety, level),
            movement: new MonsterMovement { ThroughBodies = true });
        if (id == 0) return;
        h.Monster = id;
    }

    static async Task SpawnKingHelpers(ClientState conn, Encounter h)
    {
        byte level = AreaLevel(conn);
        var w = conn.Wire;
        var rng = Random.Shared;
        string[] order = [.. KingHelperVarieties];
        rng.Shuffle(order);
        for (int i = 0; i < Math.Min(KingHelperCount, order.Length); i++)
        {
            string vid = order[i];
            if (Monsters.Find(vid) is not { Supported: true } variety) continue;
            double angle = Math.Tau * i / KingHelperCount;
            var cell = conn.Layout.Near(((int)Math.Round(h.X + 10 * Math.Cos(angle)), (int)Math.Round(h.Y + 10 * Math.Sin(angle))));
            uint id = await SpawnMonster(w.Remote, w.Stream, w.Ciphers, conn, variety, level, MonsterRarity.Unique,
                cell.X, cell.Y, name: null, alone: true, unrisen: false,
                owned: (_, m) => { m.EncounterStats = InvulnerableStats; m.NoRewards = true; },
                ownedMaximumLife: HelperMaxLife, scripted: true,
                ownedSkills: BuildOwnedSkills(conn, variety, level),
                movement: new MonsterMovement { ThroughBodies = true });
            if (id != 0) h.Helpers.Add(id);
        }
    }

    /// <summary>GameEvents.Tick: cheap early-out when Harbinger has no live encounters, kills, hazards or active effects.</summary>
    public static async Task Tick(ClientState conn, double seconds)
    {
        if (conn.Closed || conn.LeavingArea || !conn.PlayerSent || conn.Instance is null) return;
        var area = Of(conn);
        if (area is null || !area.NeedsTick) return;

        await area.Gate.WaitAsync();
        try
        {
            if (area.Encounters is null || conn.Closed || conn.LeavingArea) return;
            await DrainKills(conn, area);
            await PresentMinionBuffs(conn, area);
            if (IsMonsterRunner(conn))
            {
                double dt = Math.Clamp(seconds, 0, 30);
                await ReapEffects(conn, area, dt);
                if (area.HasHazards) await TickHazards(conn, area, dt);
                if (area.HasLive) await Drive(conn, area, dt);
            }
        }
        finally { area.Gate.Release(); }
    }

    /// <summary>Ensure each viewer receives the blue Harbinger minion buff once a summoned minion enters their view.</summary>
    static async Task PresentMinionBuffs(ClientState conn, Area area)
    {
        if (!area.HasTracked) return;
        var shown = ShownMinionBuffs.GetValue(conn, _ => []);
        List<uint>? due = null;
        lock (conn.Monsters)
        {
            lock (area.TrackingLock)
            {
                foreach (uint id in area.MinionToHarbinger.Keys)
                    if (conn.MonsterViews.ContainsKey(id) && shown.Add(id))
                        (due ??= []).Add(id);
            }
        }
        if (due is null) return;
        var w = conn.Wire;
        foreach (uint id in due)
            await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, conn, id,
                InstanceEntities.AddBuff(id, conn.NextBuff++, MinionBuffRow), null);
    }

    static async Task Drive(ClientState conn, Area area, double seconds)
    {
        var liveMembers = Members(conn).Where(c => c.PlayerSent && c.PlayerLife > 0).ToArray();
        if (liveMembers.Length == 0) return;
        var players = new (int X, int Y)[liveMembers.Length];
        for (int i = 0; i < liveMembers.Length; i++)
            players[i] = liveMembers[i].Walk.Position(liveMembers[i].Clock.Elapsed.TotalSeconds);

        byte level = AreaLevel(conn);
        double cdScale = Math.Max(0.1, 1.0 + Stat(conn, "map_harbinger_cooldown_speed_+%") / 100.0);
        bool empowerObelisks = Stat(conn, "map_harbinger_scarab_empower") > 0;
        bool changed = false;

        foreach (var h in area.Encounters!)
        {
            if (h.State is not (HarbingerState.Waiting or HarbingerState.Active)) continue;

            // Prune missing/dead minion and obelisk references.
            List<uint>? prunedMinions = null, prunedObelisks = null;
            bool bodyAlive;
            lock (conn.Monsters)
            {
                bodyAlive = h.Monster != 0 && conn.Monsters.TryGetValue(h.Monster, out var body) && !body.Dead;
                if (bodyAlive)
                {
                    for (int i = h.Minions.Count - 1; i >= 0; i--)
                    {
                        uint mid = h.Minions[i];
                        if (!conn.Monsters.TryGetValue(mid, out var mm) || mm.Dead || mm.Life == 0)
                        {
                            h.Minions.RemoveAt(i);
                            (prunedMinions ??= []).Add(mid);
                            changed = true;
                        }
                    }
                    for (int i = h.Obelisks.Count - 1; i >= 0; i--)
                    {
                        uint oid = h.Obelisks[i];
                        if (!conn.Monsters.TryGetValue(oid, out var om) || om.Dead || om.Life == 0)
                        {
                            h.Obelisks.RemoveAt(i);
                            (prunedObelisks ??= []).Add(oid);
                            changed = true;
                        }
                    }
                }
            }
            if (!bodyAlive) continue;
            if (prunedMinions is not null || prunedObelisks is not null)
            {
                lock (area.TrackingLock)
                {
                    if (prunedMinions is not null)
                        foreach (uint mid in prunedMinions) area.MinionToHarbinger.Remove(mid);
                    if (prunedObelisks is not null)
                        foreach (uint oid in prunedObelisks) area.ObeliskToHarbinger.Remove(oid);
                    area.HasTracked = area.MinionToHarbinger.Count > 0 || area.ObeliskToHarbinger.Count > 0;
                }
                if (prunedObelisks is not null)
                    foreach (uint oid in prunedObelisks)
                        await ClearAurasFromSource(conn, area, oid);
            }

            if (h.State == HarbingerState.Waiting)
            {
                if (!players.Any(p => Distance(p, (h.X, h.Y)) <= ActivationRange)) continue;
                h.State = HarbingerState.Active;
                h.WaveCooldown = 0;
                h.PendingWaveRepeats = 0;
                h.PendingWaveRemaining = 0;
                h.WaveRepeatDelay = 0;
                h.ObeliskCooldown = ObeliskFirstDelay / cdScale;
                if (h.Kind == HarbingerKind.King && h.Helpers.Count == 0)
                    await SpawnKingHelpers(conn, h);
                SeedEncounterCooldowns(conn, area, h, cdScale);
                changed = true;
                Console.WriteLine($"[harbinger] encounter {h.Index} ({h.Kind}, {h.Variety}) 0x{h.Monster:x} activated at ({h.X}, {h.Y})");
            }
            else
            {
                h.WaveCooldown = Math.Max(0, h.WaveCooldown - seconds);
                h.WaveRepeatDelay = Math.Max(0, h.WaveRepeatDelay - seconds);
                h.ObeliskCooldown = Math.Max(0, h.ObeliskCooldown - seconds);
            }

            var profile = HarbingerCatalog.Profile(h.Kind, level);

            // Summon wave & skill_repeat_count sub-waves when off cooldown and below the active cap.
            if (h.PendingWaveRepeats > 0)
            {
                if (h.WaveRepeatDelay <= 0)
                {
                    int pulsesLeft = h.PendingWaveRepeats;
                    int pulseCount = h.PendingWaveRemaining > 0
                        ? Math.Max(1, (h.PendingWaveRemaining + pulsesLeft - 1) / pulsesLeft)
                        : Math.Max(1, (profile.BaseCount + profile.RepeatCount) / (1 + profile.RepeatCount));
                    h.PendingWaveRepeats--;
                    h.PendingWaveRemaining = Math.Max(0, h.PendingWaveRemaining - pulseCount);
                    if (h.Minions.Count < profile.AliveCap)
                    {
                        await SummonWave(conn, area, h, profile, level, players, pulseCount);
                        changed = true;
                    }
                    if (h.PendingWaveRepeats > 0) h.WaveRepeatDelay = WaveRepeatInterval / cdScale;
                    else h.WaveCooldown = profile.CooldownSeconds / cdScale;
                }
            }
            else if (h.WaveCooldown <= 0 && h.Minions.Count < profile.AliveCap)
            {
                int totalWant = RollWaveCount(conn, profile);
                int pulses = 1 + Math.Max(0, profile.RepeatCount);
                int firstPulse = (totalWant + pulses - 1) / pulses;
                await SummonWave(conn, area, h, profile, level, players, firstPulse);
                if (profile.RepeatCount > 0)
                {
                    h.PendingWaveRepeats = profile.RepeatCount;
                    h.PendingWaveRemaining = Math.Max(0, totalWant - firstPulse);
                    h.WaveRepeatDelay = WaveRepeatInterval / cdScale;
                }
                else
                {
                    h.WaveCooldown = profile.CooldownSeconds / cdScale;
                }
                changed = true;
            }

            // Harbinger Scarab of Obelisks: periodically summon Empowered Obelisks.
            if (empowerObelisks && h.ObeliskCooldown <= 0
                && h.Obelisks.Count < MaxAliveObelisks && h.ObelisksSummoned < MaxTotalObelisks)
            {
                h.ObeliskCooldown = ObeliskCooldown / cdScale;
                await SummonEmpoweredObelisk(conn, area, h, level, cdScale);
                changed = true;
            }

            // Drive real GrantedEffects skills for the Harbinger, King helpers, and Empowered Obelisks.
            await DriveEncounterSkills(conn, area, h, level, seconds, cdScale, players);
        }

        // Synchronize ally auras (obelisk damage reduction/haste, helper action speed/elemental auras) and player curses.
        await SyncAllyAuras(conn, area, seconds);
        await SyncPlayerCurses(conn, area, liveMembers, players);

        if (changed) Keep(conn, area);
    }

    static int RollWaveCount(ClientState conn, WaveProfile profile)
    {
        var rng = Random.Shared;
        int rawCount = profile.BaseCount + (profile.ExtraCountChancePct > 0 && rng.Next(100) < profile.ExtraCountChancePct ? 1 : 0);
        double countScale = Math.Max(0.25, 1.0 + Stat(conn, "map_harbinger_summoned_minion_count_+%") / 100.0);
        return Math.Max(1, (int)Math.Round(rawCount * countScale));
    }

    static async Task SummonWave(ClientState conn, Area area, Encounter h, WaveProfile profile, byte level,
        (int X, int Y)[] players, int wantCount)
    {
        var pool = WaveVarietyPool(conn);
        if (pool.Count == 0) return;

        int budget = Math.Max(0, profile.AliveCap - h.Minions.Count);
        int count = Math.Min(wantCount, budget);
        if (count <= 0) return;

        var rng = Random.Shared;
        var origin = ActorPosition(conn, h.Monster, (h.X, h.Y));
        // Place the summon circle between the Harbinger and the nearest player (around 10 cells out).
        var nearest = players.MinBy(p => Distance(p, origin));
        double dist = Distance(origin, nearest);
        double angle = dist > 1 ? Math.Atan2(nearest.Y - origin.Y, nearest.X - origin.X) : rng.NextDouble() * Math.Tau;
        double offset = Math.Min(12.0, Math.Max(6.0, dist * 0.4));
        var center = conn.Layout.Near(((int)Math.Round(origin.X + offset * Math.Cos(angle)), (int)Math.Round(origin.Y + offset * Math.Sin(angle))));

        // Emit the Harbinger's real summon skill action (0x1ab) before spawning the summon circle and monsters.
        if (HarbingerCatalog.KitFor(h.Variety).Skills.FirstOrDefault(s => s.Role == SkillRole.Summon) is { } summonSkill)
            await SendActorSkillAction(conn, h.Monster, summonSkill.SkillId, center);

        await SpawnEffect(conn, area, center, SummonCircleAo, SummonEffectSeconds);

        int maxRare = Math.Max(0, profile.MaxRare + Stat(conn, "map_harbinger_minion_max_rare_count"));
        int rareChance = Math.Clamp(profile.RareChancePct + Stat(conn, "map_harbinger_minion_rare_chance_%"), 0, 100);
        int magicChance = Math.Clamp(profile.MagicChancePct, 0, 100);
        int rares = 0, magics = 0;

        var w = conn.Wire;
        var spawnedIds = new List<uint>(count);
        for (int i = 0; i < count; i++)
        {
            var variety = pool[rng.Next(pool.Count)];
            MonsterRarity rarity = MonsterRarity.Normal;
            if (rares < maxRare && rng.Next(100) < rareChance) { rarity = MonsterRarity.Rare; rares++; }
            else if (magics < profile.MaxMagic && rng.Next(100) < magicChance) { rarity = MonsterRarity.Magic; magics++; }

            double spreadAngle = Math.Tau * i / count + rng.NextDouble() * 0.5;
            double spreadDist = 2 + rng.NextDouble() * 5;
            var cell = conn.Layout.Near(((int)Math.Round(center.X + spreadDist * Math.Cos(spreadAngle)),
                (int)Math.Round(center.Y + spreadDist * Math.Sin(spreadAngle))));

            uint id = await SpawnMonster(w.Remote, w.Stream, w.Ciphers, conn, variety, level, rarity,
                cell.X, cell.Y, alone: true, unrisen: false, owned: (_, m) => m.Aggro = true);
            if (id == 0) continue;

            h.Minions.Add(id);
            spawnedIds.Add(id);
        }

        if (spawnedIds.Count == 0) return;

        lock (area.TrackingLock)
        {
            foreach (uint id in spawnedIds) area.MinionToHarbinger[id] = new(h.Index, profile.Permyriad);
            area.HasTracked = true;
        }

        // Send the blue Harbinger minion buff to all viewers who currently have each minion in view.
        foreach (var viewer in Members(conn))
        {
            var shown = ShownMinionBuffs.GetValue(viewer, _ => []);
            var vw = viewer.Wire;
            foreach (uint id in spawnedIds)
            {
                bool visible;
                lock (viewer.Monsters) visible = viewer.MonsterViews.ContainsKey(id) && shown.Add(id);
                if (visible)
                    await SendWorldRecord(vw.Remote, vw.Stream, vw.Ciphers, viewer, id,
                        InstanceEntities.AddBuff(id, viewer.NextBuff++, MinionBuffRow), null);
            }
        }
    }

    static IReadOnlyList<MonsterCatalog.Variety> WaveVarietyPool(ClientState conn)
    {
        var standing = new List<MonsterCatalog.Variety>();
        var any = new List<MonsterCatalog.Variety>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (Packs.For(conn.Layout.Id)?.Scattered is { Count: > 0 } scattered)
        {
            foreach (var p in scattered)
                foreach (var e in p.Pack.Entries)
                    if (Monsters.Find(e.Variety.ToString()) is { Supported: true } v && seen.Add(v.Id))
                    {
                        any.Add(v);
                        if (Emerges.Kind(v, MonsterRarity.Normal) == EmergeKind.None) standing.Add(v);
                    }
        }
        if (standing.Count > 0) return standing;
        if (any.Count > 0) return any;
        var fallback = new List<MonsterCatalog.Variety>();
        foreach (string id in FallbackMinionVarieties)
            if (Monsters.Find(id) is { Supported: true } v) fallback.Add(v);
        return fallback;
    }

    static async Task SummonEmpoweredObelisk(ClientState conn, Area area, Encounter h, byte level, double cdScale)
    {
        var rng = Random.Shared;
        string vid = EmpoweredObeliskVarieties[rng.Next(EmpoweredObeliskVarieties.Length)];
        if (Monsters.Find(vid) is not { Supported: true } variety) return;
        double angle = rng.NextDouble() * Math.Tau;
        double dist = 8 + rng.NextDouble() * 6;
        var cell = conn.Layout.Near(((int)Math.Round(h.X + dist * Math.Cos(angle)), (int)Math.Round(h.Y + dist * Math.Sin(angle))));
        var w = conn.Wire;
        // Empowered Obelisks are immobile totems (LifeMultiplier = 1000, ModelSizeMultiplier = 200) that drop only
        // their HarbingerCurrencyShardsObelisk payout on death.
        uint id = await SpawnMonster(w.Remote, w.Stream, w.Ciphers, conn, variety, level, MonsterRarity.Normal,
            cell.X, cell.Y, name: null, alone: true, unrisen: false,
            owned: (_, m) => m.NoRewards = true, scripted: true,
            ownedSkills: BuildOwnedSkills(conn, variety, level));
        if (id == 0) return;

        h.Obelisks.Add(id);
        h.ObelisksSummoned++;
        SeedActorCooldowns(area, id, vid, 1.0 / cdScale);
        lock (area.TrackingLock)
        {
            area.ObeliskToHarbinger[id] = h.Index;
            area.HasTracked = true;
        }
        Console.WriteLine($"[harbinger] encounter {h.Index} summoned empowered obelisk {vid} 0x{id:x} at ({cell.X}, {cell.Y})");
    }

    /// <summary>GameEvents.Kill: O(1) fast early-out when Harbinger has no tracked minions or obelisks.</summary>
    public static void Kill(KillContext kill)
    {
        uint id = kill.Monster.ObjectId;
        if (id == 0 || kill.Conn.Instance is null
            || !Areas.TryGetValue(kill.Conn.Instance, out var area) || !area.HasTracked) return;

        PendingKill? pending = null;
        lock (area.TrackingLock)
        {
            if (area.MinionToHarbinger.Remove(id, out var link))
                pending = new(kill.Conn, IsObelisk: false, id, link.HarbingerIndex, link.Permyriad, kill.Monster.Level, kill.At);
            else if (area.ObeliskToHarbinger.Remove(id, out int ownerIndex))
                pending = new(kill.Conn, IsObelisk: true, id, ownerIndex, 0, kill.Monster.Level, kill.At);
            if (pending is not null)
                area.HasTracked = area.MinionToHarbinger.Count > 0 || area.ObeliskToHarbinger.Count > 0;
        }
        if (pending is null) return;

        area.Kills.Enqueue(pending);
        kill.Later(() => ProcessKillsAsync(kill.Conn, area));
    }

    static async Task ProcessKillsAsync(ClientState conn, Area area)
    {
        if (conn.Closed || conn.LeavingArea) return;
        await area.Gate.WaitAsync();
        try
        {
            if (area.Encounters is null || conn.Closed || conn.LeavingArea) return;
            await DrainKills(conn, area);
        }
        finally { area.Gate.Release(); }
    }

    static async Task DrainKills(ClientState conn, Area area)
    {
        bool changed = false;
        while (area.Kills.TryDequeue(out var kill))
        {
            var killer = kill.Killer is { Closed: false, LeavingArea: false } k && k.Instance == conn.Instance ? k : conn;
            if (kill.IsObelisk)
            {
                if (kill.HarbingerIndex >= 0 && kill.HarbingerIndex < area.Encounters!.Count)
                    area.Encounters[kill.HarbingerIndex].Obelisks.Remove(kill.MonsterId);
                await ClearAurasFromSource(killer, area, kill.MonsterId);
                area.ActorCooldowns.Remove(kill.MonsterId);
                int tenths = Random.Shared.Next(ObeliskMinTenths, ObeliskMaxTenths + 1);
                var drops = HarbingerCatalog.RollShards(ItemsFor(killer), Math.Clamp(kill.Level, 1, 100), tenths, 0, MapModsOf(killer), Random.Shared);
                await DropAll(killer, drops, kill.At, "empowered obelisk");
                changed = true;
                continue;
            }

            if (kill.HarbingerIndex < 0 || kill.HarbingerIndex >= area.Encounters!.Count) continue;
            var h = area.Encounters[kill.HarbingerIndex];
            h.Minions.Remove(kill.MonsterId);
            ClearMinionAuraRecords(area, kill.MonsterId);
            if (h.State != HarbingerState.Active) continue;

            uint loss = Math.Max(1u, (uint)Math.Ceiling((double)HarbingerMaxLife * kill.Permyriad / 10_000.0));
            h.Life = h.Life > loss ? h.Life - loss : 0;
            changed = true;

            lock (conn.Monsters)
            {
                if (conn.Monsters.TryGetValue(h.Monster, out var body))
                {
                    body.Life = h.Life;
                    if (h.Life == 0) { body.Dead = true; ++body.ActionGeneration; }
                }
            }

            foreach (var viewer in Members(conn))
            {
                bool visible;
                lock (viewer.Monsters) visible = viewer.MonsterViews.ContainsKey(h.Monster);
                if (visible)
                {
                    var vw = viewer.Wire;
                    await SendWorldRecord(vw.Remote, vw.Stream, vw.Ciphers, viewer, h.Monster,
                        InstanceProtocol.LifeUpdate(h.Monster, h.Life),
                        h.Life == 0 ? $"harbinger {h.Index} defeated" : null);
                }
            }

            if (h.Life == 0)
                await DefeatHarbinger(killer, area, h);
        }

        if (changed) Keep(conn, area);
    }

    static async Task DefeatHarbinger(ClientState conn, Area area, Encounter h)
    {
        h.State = HarbingerState.Defeated;
        h.PendingWaveRepeats = 0;
        h.PendingWaveRemaining = 0;
        (int X, int Y) at = (h.X, h.Y);
        lock (conn.Monsters)
        {
            if (conn.Monsters.TryGetValue(h.Monster, out var body))
            {
                at = body.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                body.Life = 0;
                body.Dead = true;
                ++body.ActionGeneration;
            }
            conn.Instance.World.MonsterOrigins.TryRemove(h.Monster, out _);
        }

        // Remove invulnerable King helpers and surviving obelisks (remaining summoned wave minions stay killable).
        uint[] helpers = [.. h.Helpers];
        uint[] obelisks = [.. h.Obelisks];
        h.Helpers.Clear();
        h.Obelisks.Clear();
        lock (area.TrackingLock)
        {
            foreach (uint oid in obelisks) area.ObeliskToHarbinger.Remove(oid);
            foreach (uint mid in h.Minions) area.MinionToHarbinger.Remove(mid);
            area.HasTracked = area.MinionToHarbinger.Count > 0 || area.ObeliskToHarbinger.Count > 0;
            area.HasLive = area.Encounters!.Any(e => e.State is HarbingerState.Waiting or HarbingerState.Active);
        }
        await ClearAurasFromSource(conn, area, h.Monster);
        area.ActorCooldowns.Remove(h.Monster);
        foreach (uint helper in helpers)
        {
            await ClearAurasFromSource(conn, area, helper);
            area.ActorCooldowns.Remove(helper);
            await Remove(conn, helper, "harbinger boss defeated");
        }
        foreach (uint obelisk in obelisks)
        {
            await ClearAurasFromSource(conn, area, obelisk);
            area.ActorCooldowns.Remove(obelisk);
            await Remove(conn, obelisk, "parent harbinger defeated");
        }
        if (!area.HasLive)
            await ClearAllPlayerCurses(conn);

        await SpawnEffect(conn, area, at, DefeatBurstAo, DefeatEffectSeconds);
        // LeagueAvatar has no corpse/fall animation on LifeUpdate(0), so remove the defeated avatar cleanly.
        if (h.Monster != 0) await Remove(conn, h.Monster, "harbinger defeated");
        var w = conn.Wire;
        await SendRemainingMonsters(w.Remote, w.Stream, w.Ciphers, conn);

        int baseTenths = h.Kind switch
        {
            HarbingerKind.King => KingBaseTenths,
            HarbingerKind.Strong => StrongBaseTenths,
            _ => NormalBaseTenths,
        };
        int stackSizePct = h.Kind == HarbingerKind.King ? KingStackSizePct : 0;
        var drops = HarbingerCatalog.RollShards(ItemsFor(conn), AreaLevel(conn), baseTenths, stackSizePct, MapModsOf(conn), Random.Shared);
        await DropAll(conn, drops, at, $"{h.Kind} harbinger");
        Console.WriteLine($"[harbinger] encounter {h.Index} ({h.Kind}) defeated at ({at.X}, {at.Y}), dropped {drops.Count} stack(s)");
    }

    static async Task SpawnEffect(ClientState conn, Area area, (int X, int Y) at, string ao, double durationSeconds)
    {
        uint id = conn.Instance.World.NewMonsterId();
        var blob = new EffectObject(at.X, at.Y, ao).Blob();
        uint hash = InstanceEntities.ObjectTypeHash(EffectObject.TypePath);
        foreach (var viewer in Members(conn))
        {
            var vw = viewer.Wire;
            await SendWorldCreate(vw.Remote, vw.Stream, vw.Ciphers, viewer, id,
                InstanceEntities.CreateObject(id, hash, blob), null);
        }
        area.Effects.Add(new(id, durationSeconds));
        area.HasEffects = true;
    }

    static async Task ReapEffects(ClientState conn, Area area, double seconds)
    {
        if (area.Effects.Count == 0) { area.HasEffects = false; return; }
        for (int i = area.Effects.Count - 1; i >= 0; i--)
        {
            var eff = area.Effects[i];
            eff.Remaining -= seconds;
            if (eff.Remaining > 0) continue;
            area.Effects.RemoveAt(i);
            foreach (var viewer in Members(conn))
            {
                var vw = viewer.Wire;
                await SendWorldRemoval(vw.Remote, vw.Stream, vw.Ciphers, viewer, eff.ObjectId, null);
            }
        }
        area.HasEffects = area.Effects.Count > 0;
    }

    static async Task Remove(ClientState conn, uint id, string why)
    {
        lock (conn.Monsters)
        {
            if (conn.Monsters.Remove(id, out var m)) { m.Dead = true; m.Life = 0; ++m.ActionGeneration; }
            conn.Instance.World.MonsterOrigins.TryRemove(id, out _);
        }
        foreach (var viewer in Members(conn))
        {
            bool shown;
            lock (viewer.Monsters) { shown = viewer.MonsterViews.Remove(id); viewer.AsleepMonsters.Remove(id); }
            if (shown)
            {
                var vw = viewer.Wire;
                await SendWorldRemoval(vw.Remote, vw.Stream, vw.Ciphers, viewer, id, $"[harbinger] 0x{id:x} removed: {why}");
            }
        }
    }

    // Individual stacks (NOT coalesced through ItemDrops.Stacked) so Warhoards visibly emits duplicate stacks.
    static async Task DropAll(ClientState conn, List<ItemDrops.Drop> items, (int X, int Y) at, string label)
    {
        if (items.Count == 0 || Environment.GetEnvironmentVariable("Game_DROPS") == "0" || !GameEvents.AllowsLoot(conn)) return;
        var (remote, stream, ciphers) = conn.Wire;
        foreach (var drop in items)
        {
            var cell = conn.Layout.Near((at.X + Random.Shared.Next(-5, 6), at.Y + Random.Shared.Next(-5, 6)));
            var ground = new WorldItemObject(cell.X, cell.Y, drop.Item);
            uint id = conn.Instance.Ground.Add(ground, conn);
            await SendGroundItem(remote, stream, ciphers, conn, id, ground,
                $"harbinger: {label} drops {drop.Item.StackSize}x {drop.Type.Name}", justDropped: true);
        }
    }

    /// <summary>GameEvents.StateBuilding: publish Harbinger encounter state for inspection/debugging.</summary>
    public static void Describe(ClientState conn, JsonObject state)
    {
        if (conn.Instance is null || !Areas.TryGetValue(conn.Instance, out var area) || area.Encounters is null) return;
        state["harbinger"] = new JsonObject
        {
            ["encounters"] = JsonSerializer.SerializeToNode(area.Encounters),
            ["trackedMinions"] = area.MinionToHarbinger.Count,
            ["trackedObelisks"] = area.ObeliskToHarbinger.Count,
            ["activeHazards"] = area.Hazards.Count,
            ["appliedAllyAuras"] = area.AppliedAllyAuras.Count,
        };
    }

    public static void Leave(ClientState conn)
    {
        ShownMinionBuffs.Remove(conn);
        ClearPlayerCursesSync(conn);
    }
}
