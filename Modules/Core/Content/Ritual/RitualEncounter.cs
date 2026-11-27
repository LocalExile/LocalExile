using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;

namespace GameServer.Modules.Ritual;

/// <summary>
/// An instance's Ritual: campaign &amp; map placement, guards, tied monsters, circular arena containment, revived waves,
/// combat daemons, Tribute calculation (with Atlas &amp; Scarab modifiers), Blood-filled Vessel capture/injection,
/// Favour window progression, and multiplayer/re-entry replay.
/// </summary>
public sealed class RitualEncounter
{
    public const string InteractableType = "Metadata/Terrain/Leagues/Ritual/RitualRuneInteractable";
    public const string ObjectType = "Metadata/Terrain/Leagues/Ritual/RitualRuneObject";
    public const string LightType = "Metadata/Terrain/Leagues/Ritual/RitualRuneLight";
    public const string PoolType = "Metadata/Terrain/Leagues/Ritual/BloodPool";
    public const string BlockerType = "Metadata/Terrain/Leagues/Ritual/RitualBlocker";
    public const string VesselsContentKey = "ritual.vessels";
    const string MapOwnerFlag = "IsMapOwner";

    /// <summary>An altar's state, as 0x165 carries it: 0 inactive, 1 ready, 2 running, 3 done.</summary>
    public const int Inactive = 0, Ready = 1, Running = 2, Done = 3;

    const uint RollSalt = 0x52697475;
    const double ScanSeconds = 0.5;
    const double RevealSeconds = 0.25;
    static readonly string[] OwnerFlags = [MapOwnerFlag], NoFlags = [];

    public sealed class Altar
    {
        public required (int X, int Y) Cell;
        public required float Rotation;
        public required RitualTables.Rune Rune;
        public required List<List<int>> Guards;
        public uint Interactable, Object, Light;
        public int State;
        public bool Shown;
        public bool Bottled;
        public readonly HashSet<uint> Tied = [];
        public readonly HashSet<uint> Dead = [];
        public readonly List<(string Path, MonsterRarity Rarity)> Credit = [];
        public readonly List<uint> Pools = [];
        public readonly List<(uint Pool, double At)> Faded = [];
    }

    public sealed class Revived
    {
        public required string Path;
        public required MonsterRarity Rarity;
        public required int N;
    }

    public sealed class Wave
    {
        public required double At;
        public int Spawned;
        public readonly List<uint> Ids = [];
    }

    public sealed class Run
    {
        public required int Altar;
        public required double StartedAt;
        public required List<Revived> Monsters;
        public required List<(double X, double Y)> Order;
        public required Random Rng;
        public int Next;
        public readonly List<Wave> Waves = [];
        public readonly Dictionary<uint, Revived> Alive = [];
        public readonly List<uint> Daemons = [];
        public int DaemonsDue;
        public double NextHazardAt;
        public uint Wisp;
        public uint Blocker;
        public int Killed;
    }

    public sealed class State
    {
        public readonly List<Altar> Altars = [];
        public string Owner = "";
        public int Seed;
        public int Level;
        /// <summary>The rune's mod set for the area's spawns, resolved once in <see cref="Populate"/>: every altar shares
        /// one rune, so every spawn shares one set.</summary>
        public MonsterModSet? ModSet;
        public int Completed;
        public int Tribute;
        public int RerollsUsed;
        public uint ShopRevision;
        // Cached area/Atlas/Scarab stats for this instance.
        public int TributePct;
        public int TributeMorePct;           // Immutable Dogma: map_ritual_tribute_+%_final_from_atlas_keystone
        public int UniqueTributeMorePct;     // Answered Appeals: map_unique_monster_tribute_multiplier_final_+%
        public int AdditionalRerolls;        // map_ritual_additional_reward_rerolls
        public int FreeRerolls;              // map_ritual_number_of_free_rerolls
        public int RerollCostPctFinal;       // map_ritual_rewards_reroll_cost_+%_final
        public bool CannotReroll;            // map_ritual_cannot_reroll_rewards
        public bool VariableCosts;           // map_ritual_offered_rewards_have_additional_variable_cost
        public bool AllRewardsRevealed;      // map_ritual_all_rewards_revealed
        public int DeferCostPct;             // map_ritual_defer_reward_tribute_cost_+%
        public int DeferSoonerPct;           // map_ritual_deferred_rewards_are_offered_again_+%_sooner
        public int RewardCountPct;           // map_ritual_reward_count_+%
        public int SplinterStackPct;         // map_ritual_offered_ritual_splinter_reward_stack_size_+%
        public int FreeRerollRewardPermyriad;// map_ritual_offered_rewards_from_rerolls_have_permyriad_chance_to_cost_no_tribute
        public int SpawnSpeedPct;            // map_ritual_monster_spawning_speed_+%
        public int MaxAlivePct;              // map_ritual_maximum_monsters_alive_at_once_+%
        public int VesselDropChancePct;      // map_ritual_%_chance_to_drop_vessel_from_final_encounter
        public bool SpawnWisp;               // ritual_altars_spawn_wisp (ScarabRitual2)
        public int ExtraCorpses;             // map_ritual_x_additional_itemised_corpses_encountered (ScarabRitual4)
        public bool OpenedWithBloodVessel;
        public int VesselCount;
        public readonly List<(string Path, MonsterRarity Rarity)> VesselMonsters = [];
        public double WispBuffUntil;
        public bool WispBuffActive;

        public List<RitualFavours.Offer> Offers = [];
        public readonly HashSet<uint> ShopSentTo = [];
        public double Now;
        public double ScanAt;
        public double RevealAt;
        public Run? Run;
        public List<Revived> Revived = [];
        public readonly Dictionary<uint, (int Altar, string Path, MonsterRarity Rarity)> Tie = [];
        public readonly HashSet<uint> Mine = [];
        public bool Done;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public GameArea? Area;
    }

    readonly ConditionalWeakTable<InstanceState, State> _states = new();

    public void Clear() => _states.Clear();

    public bool IsActivePause(Player player) =>
        player.Instance is { } instance && _states.TryGetValue(instance, out var state) && Volatile.Read(ref state.Run) is not null;

    public State? Of(Player player) => player.Instance is { } instance && _states.TryGetValue(instance, out var state) ? state : null;

    public bool IsOwner(Player player, State state) =>
        string.IsNullOrEmpty(state.Owner) || string.Equals(state.Owner, CharacterName(player), StringComparison.OrdinalIgnoreCase);

    /// <summary>Grants <c>IsMapOwner</c> (QuestFlags 2020) to the instance owner so client Buy/Defer/Reroll buttons unlock.</summary>
    public IReadOnlyList<string> QuestFlags(Player player) =>
        Of(player) is { Altars.Count: > 0 } state && IsOwner(player, state) ? OwnerFlags : NoFlags;

    /// <summary>Applies <c>Stat.IsInRitual = 1</c> and active Wildwood Wisp tribute bonus while inside an active Ritual.</summary>
    public void ApplyStats(StatSheet sheet)
    {
        if (Of(sheet.Player) is not { } state) return;
        if (state.Run is { } run)
        {
            var altar = state.Altars[run.Altar];
            var pos = sheet.Player.Walk.Position(AreaTime(sheet.Player));
            if (Distance(pos, altar.Cell) <= RitualTables.RadiusCells + 6)
                sheet.Add(Stat.IsInRitual, 1);
        }
        if (state.WispBuffActive && state.WispBuffUntil > state.Now)
            sheet.Add(Stat.RitualTributePlusPct, 100);
    }

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    public IEnumerable<Player> InstanceClients(Player player)
    {
        var members = InGame.Keys.Where(c => c.HasClient && ReferenceEquals(c.Instance, player.Instance) && Current(c)).ToList();
        if (!members.Contains(player)) members.Insert(0, player);
        return members;
    }

    async Task Broadcast(Player player, byte[] message, string log)
    {
        var members = InstanceClients(player).ToList();
        for (int i = 0; i < members.Count; i++) await Send(members[i], message.ToArray(), i == 0 ? log : null);
    }

    // ---- Map Device Opening: Scarabs & Blood-filled Vessels ------------------------------------------------------------

    /// <summary>Handles <c>GameEvents.MapOpening</c>: claims <c>"Ritual"</c> content (silencing unhandled scarab warnings),
    /// validates and extracts up to 4 Blood-filled Vessels (<c>RitualFragment</c>), applies their Ritual stacking bonuses
    /// (+20% spawn speed, +20% max alive; +5% IIQ per vessel is already applied from <c>MapFragmentMods</c> row 247 by
    /// <see cref="MapRunCatalog.Of"/>), and forwards captured monsters to the map run.</summary>
    public void OnMapOpening(MapOpening request)
    {
        if (request.Opening is not { } opening || request.Campaign) return;
        var vessels = new List<RitualEncounterData>();
        bool hasRitualScarab = false;
        foreach (var slot in request.Slots)
        {
            if (slot.IsMap) continue;
            if (slot.BaseId.StartsWith("Metadata/Items/Scarabs/ScarabRitual", StringComparison.Ordinal))
                hasRitualScarab = true;
            if (slot.BaseId == RitualTables.BloodFilledVesselId)
                vessels.Add(slot.SourceItem?.RitualEncounter ?? new RitualEncounterData(opening.Area.Level, 0, []));
        }
        if (hasRitualScarab || vessels.Count > 0)
            request.HandledContent.Add("Ritual");
        if (vessels.Count == 0) return;

        // Blood-filled Vessels cannot open monsterless / boss-only arenas without natural monsters.
        if (opening.Area.Id.Contains("Boss", StringComparison.OrdinalIgnoreCase) && !opening.Area.Id.StartsWith("MapWorlds", StringComparison.Ordinal))
        {
            request.Opening = null;
            request.Reason = "Blood-filled Vessels cannot be used on this map.";
            return;
        }

        int n = Math.Min(4, vessels.Count);
        var mods = opening.Mods
            .WithFragmentStat(Stat.MapAreaContainsRituals, 1)
            .WithFragmentStat(Stat.MapRitualMonsterSpawningSpeedPlusPct, 20 * n)
            .WithFragmentStat(Stat.MapRitualMaximumMonstersAliveAtOncePlusPct, 20 * n);

        var content = opening.ContentData is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(opening.ContentData, StringComparer.Ordinal);
        content[VesselsContentKey] = JsonSerializer.Serialize(vessels.Take(n).ToList(), CharacterSaves.Opts);

        request.Opening = opening with
        {
            Mods = mods,
            ContentData = content,
        };
    }

    // ---- Roll & Placement (Maps + Retail Campaign Act 2) ---------------------------------------------------------------

    public static int? ForcedChance(string? setting)
    {
        foreach (var entry in (setting ?? "").Split(',', StringSplitOptions.TrimEntries))
            if (entry.Split('=') is [var id, var value] && id.Equals("Ritual", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int c))
                return c;
        return null;
    }

    static (int Count, int ForcedAltars, string Roll) Roll(AreaPopulated area, IReadOnlyDictionary<uint, int> stats, bool isMap)
    {
        var player = area.Player;
        var rng = new Random(unchecked(area.Seed ^ (int)RollSalt));
        // 1. Explicit test override: Game_LEAGUE_CHANCES=Ritual=N (works in both maps and campaign areas!).
        if (ForcedChance(Environment.GetEnvironmentVariable("Game_LEAGUE_CHANCES")) is int forced)
        {
            int c = forced >= 100 ? 1 : forced <= 0 ? 0 : rng.Next(100) < forced ? 1 : 0;
            return (c, 0, $"Game_LEAGUE_CHANCES {forced}%");
        }
        // 2. Retail 3.29 campaign integration: WorldAreas.ModsKeys -> MapActsRitualSpawn (Act 2 Northern Forest & Dread Thicket).
        int campaignForced = RitualTables.CampaignForcedAltars(player.Layout.Id);
        if (!isMap)
        {
            if (campaignForced > 0) return (1, campaignForced, "campaign MapActsRitualSpawn");
            return (0, 0, "campaign area without Ritual");
        }
        // 3. Map roll via native ExtraTerrainFeatures.LeagueCount (handles WorldAreaLeagueChances, Atlas, Scarabs, Secular Focus).
        AreaInfo info;
        try { info = AreaInfo.Load(GamePack.Default.TryRead, player.Layout.Id, area.Level); }
        catch (Exception e) when (e is ArgumentException or IOException or KeyNotFoundException)
        {
            return (0, 0, $"area row not read ({e.GetType().Name})");
        }
        var byId = stats.ToDictionary(s => (int)s.Key, s => s.Value);
        return (ExtraTerrainFeatures.LeagueCount(info with { Stats = byId }, new LevelRandom(unchecked((uint)area.Seed ^ RollSalt), false),
            LeagueChances.Ritual), 0, "the area's roll");
    }

    public string? Populate(AreaPopulated area)
    {
        var player = area.Player;
        if (player.Instance is not { } instance) return "no instance";
        if (_states.TryGetValue(instance, out _)) return "the instance has its Ritual already";
        var layout = player.Layout;
        if (InTown(player) || HideoutCatalog.ByArea(layout.Hash) is not null) return "town or hideout";
        if (layout.Walkable is null || !RitualTables.Loaded) return "no walk map or no Ritual tables";

        bool isMap = MapDevices.IsMapArea(layout.Hash);
        var stats = instance.KeptAreaStats ?? AreaLeagueStats(player) ?? new Dictionary<uint, int>();
        int S(uint name) => stats.GetValueOrDefault(name);

        // Extract any Blood-filled Vessels attached to this map run.
        List<RitualEncounterData> vessels = [];
        if (instance.Map?.ContentData is { } cd && cd.TryGetValue(VesselsContentKey, out string? vJson) && !string.IsNullOrWhiteSpace(vJson))
        {
            try { vessels = JsonSerializer.Deserialize<List<RitualEncounterData>>(vJson, CharacterSaves.Opts) ?? []; }
            catch (JsonException) { }
        }

        var (count, forcedAltars, roll) = Roll(area, stats, isMap);
        if (vessels.Count > 0 && count < 1) (count, roll) = (1, "Blood-filled Vessel");
        if (count < 1) return $"the roll gave none ({roll})";

        var rng = new Random(area.Seed ^ (int)RollSalt);
        int level = Math.Clamp((int)area.Level, 1, 100);
        var (lo, hi) = RitualTables.AltarRange(level);
        if (forcedAltars > 0) (lo, hi) = (forcedAltars, forcedAltars);
        else if (S(Stat.MapRitualEncounterCountIsFour) > 0) (lo, hi) = (4, 4);
        else if (S(Stat.MapForcedRitualSpawns) > 0) (lo, hi) = (S(Stat.MapForcedRitualSpawns), S(Stat.MapForcedRitualSpawns));

        var rune = RitualTables.PickRune(level, rng, S(Stat.MapRitualUberRuneTypeWeightingPlusPct), isMap);
        if (rune is null) return "no rune for the level";

        var taken = area.Taken.ToList();
        int wanted = rng.Next(lo, hi + 1);
        var cells = new List<(int X, int Y)>();
        bool atArrival = Environment.GetEnvironmentVariable("Game_ENCOUNTERS_AT_ARRIVAL") == "1";
        for (int attempt = 0; attempt < wanted * 24 && cells.Count < wanted; attempt++)
        {
            (int X, int Y)? at;
            // With Game_ENCOUNTERS_AT_ARRIVAL=1, EncounterSpot pins every call near spawn; use it for the first altar
            // and sample open walkable cells across the map for the remaining altars so all 3-4 place in <5 ms.
            if (atArrival && cells.Count > 0)
            {
                int rx = rng.Next(8, Math.Max(9, layout.GridW - 8));
                int ry = rng.Next(8, Math.Max(9, layout.GridH - 8));
                at = layout.WalkableFor(rx, ry, 2) ? (rx, ry) : null;
            }
            else
            {
                // The cached sites: PopulationSites computes them at most once per AreaPopulated, so the retry loop no
                // longer runs a full-map search on every attempt.
                at = AreaPopulation.EncounterSpot(area, taken, rng);
                if (at is null) break;
            }
            if (at is not { } spot) continue;
            double apart = attempt < wanted * 12 ? RitualTables.SpacingMin : 2 * RitualTables.RadiusCells;
            if (cells.Any(c => Distance(c, spot) < apart)) continue;
            cells.Add(spot);
            taken.Add(spot);
        }
        if (cells.Count == 0) return "no open ground for an altar";

        string owner = instance.Map?.Owner is { Length: > 0 } mapOwner ? mapOwner : CharacterName(player);
        var state = new State
        {
            Owner = owner,
            Seed = area.Seed,
            Level = level,
            ModSet = RitualTables.RuneModSet(rune, level),
            TributePct = S(Stat.MapRitualTributePlusPct),
            TributeMorePct = S(Stat.MapRitualTributePlusPctFinalFromAtlasKeystone),
            UniqueTributeMorePct = S(Stat.MapUniqueMonsterTributeMultiplierFinalPlusPct),
            AdditionalRerolls = S(Stat.MapRitualAdditionalRewardRerolls),
            FreeRerolls = S(Stat.MapRitualNumberOfFreeRerolls),
            RerollCostPctFinal = S(Stat.MapRitualRewardsRerollCostPlusPctFinal),
            CannotReroll = S(Stat.MapRitualCannotRerollRewards) != 0,
            VariableCosts = S(Stat.MapRitualOfferedRewardsHaveAdditionalVariableCost) != 0,
            AllRewardsRevealed = S(Stat.MapRitualAllRewardsRevealed) != 0,
            DeferCostPct = S(Stat.MapRitualDeferRewardTributeCostPlusPct),
            DeferSoonerPct = S(Stat.MapRitualDeferredRewardsAreOfferedAgainPlusPctSooner),
            RewardCountPct = S(Stat.MapRitualRewardCountPlusPct),
            SplinterStackPct = S(Stat.MapRitualOfferedRitualSplinterRewardStackSizePlusPct),
            FreeRerollRewardPermyriad = S(Stat.MapRitualOfferedRewardsFromRerollsHavePermyriadChanceToCostNoTribute),
            SpawnSpeedPct = S(Stat.MapRitualMonsterSpawningSpeedPlusPct),
            MaxAlivePct = S(Stat.MapRitualMaximumMonstersAliveAtOncePlusPct),
            VesselDropChancePct = S(Stat.MapRitualPctChanceToDropVesselFromFinalEncounter),
            SpawnWisp = S(Stat.RitualAltarsSpawnWisp) > 0,
            ExtraCorpses = Math.Max(0, S(Stat.MapRitualXAdditionalItemisedCorpsesEncountered)),
            OpenedWithBloodVessel = vessels.Count > 0,
            VesselCount = vessels.Count,
            Area = player.Area,
        };

        // Resolve captured Blood-filled Vessel monsters by MonsterVarieties HASH16.
        if (vessels.Count > 0)
        {
            var byHash16 = new Dictionary<ushort, MonsterCatalog.Variety>();
            var mvRows = ClientTables.TableOrEmpty("MonsterVarieties");
            for (int r = 0; r < mvRows.Count && r < Monsters.All.Count; r++)
                if (mvRows[r].IntOrNull("HASH16") is int h16 && Monsters.All[r] is { Supported: true } mv)
                    byHash16.TryAdd((ushort)h16, mv);
            foreach (var v in vessels)
                foreach (var m in v.Monsters)
                    if (byHash16.TryGetValue(m.VarietyHash, out var variety))
                        for (int c = 0; c < Math.Min((int)m.Count, 20); c++)
                            state.VesselMonsters.Add((variety.Id, MonsterRarity.Normal));
        }

        foreach (var cell in cells) state.Altars.Add(MakeAltar(cell, rune, level, rng));
        RitualFavours.TickDeferredCooldowns(player);
        RitualFavours.EnsureOffers(player, state);
        _states.AddOrUpdate(instance, state);
        Console.WriteLine($"[ritual] {layout.Id}: {roll} gave {count}; level {level}, {state.Altars.Count} altar(s), rune {rune.Id}, offers {state.Offers.Count}"
            + (state.VesselCount > 0 ? $", {state.VesselCount} vessel(s) ({state.VesselMonsters.Count} captured monsters)" : ""));
        return null;
    }

    public IEnumerable<(string Log, byte[] Message)> EnterMessages(Player player) =>
        Of(player) is { Altars.Count: > 0 } state ? Entry(state) : [];

    static IEnumerable<(string Log, byte[] Message)> Entry(State state)
    {
        yield return ($"0x167 Ritual tribute {state.Tribute}", RitualWire.Tribute(state.Tribute));
        yield return ($"0x166 Ritual favours ({state.Completed}/{state.Altars.Count} done)",
            RitualWire.Favours(state.Tribute, state.RerollsUsed, RitualFavours.BuildRows(state), state.Altars.Count, state.Completed));
    }

    public async Task Placed(Player player)
    {
        if (Of(player) is not { } state) return;
        // AreaPopulated runs after SendPlayer, so flush transient AreaQuestFlags (IsMapOwner) for the owner now.
        if (IsOwner(player, state))
            await SendPlayerQuestFlags(player, "0x02bc player quest flags (Ritual owner)");
        await RitualFavours.SyncSavedRewardsInventory(player);
        await RitualFavours.SendShopState(player, state, openWindow: false);
    }

    static Altar MakeAltar((int X, int Y) cell, RitualTables.Rune rune, int level, Random rng)
    {
        var pool = RitualTables.GuardPacks(level);
        var guards = new List<List<int>>();
        int packs = pool.Count == 0 ? 0 : rng.Next(RitualTables.GuardPacksMin, RitualTables.GuardPacksMax + 1);
        for (int g = 0; g < packs; g++) guards.Add(RitualTables.RollPack(pool[rng.Next(pool.Count)], rng));
        return new Altar { Cell = cell, Rotation = (float)(rng.NextDouble() * Math.Tau), Rune = rune, Guards = guards };
    }

    // ---- Entering & Re-entry Replay ------------------------------------------------------------------------------------

    public async Task Entered(Player player)
    {
        if (Of(player) is not { } state || !Current(player)) return;
        await state.Gate.WaitAsync();
        try
        {
            await Sync(player, state);
            await RitualFavours.SyncSavedRewardsInventory(player);
            if (!state.ShopSentTo.Contains(player.Id))
                await RitualFavours.SendShopState(player, state, openWindow: false);
            foreach (var altar in state.Altars)
                if (altar.Shown && altar.Interactable != 0 && altar.State >= Ready)
                    await Send(player, RitualWire.AltarState(altar.Interactable, altar.Cell, altar.State),
                        $"0x165 Ritual altar 0x{altar.Interactable:x} state {altar.State}");
            if (state.Run is { } run)
            {
                var runningAltar = state.Altars[run.Altar];
                player.SetRingBlocker(run.Blocker, runningAltar.Cell.X, runningAltar.Cell.Y, RitualTables.RadiusCells);
                if (runningAltar.Rune.Environment is int env)
                    await Send(player, InstanceEnvironments.Data(((ushort)env, 0)), $"0xdf Ritual environment {env} on");
            }
        }
        finally { state.Gate.Release(); }
    }

    async Task Sync(Player player, State state)
    {
        if (ReferenceEquals(state.Area, player.Area)) return;
        bool again = state.Area is not null;
        state.Area = player.Area;
        if (!again) return;
        foreach (var altar in state.Altars)
        {
            if (!altar.Shown || altar.Interactable == 0) continue;
            await CreateAltar(player, altar);
            foreach (uint pool in altar.Pools.Concat(altar.Faded.Select(f => f.Pool)))
                await CreatePool(player, altar, pool, altar.Cell);
        }
        foreach (var (id, (k, _, _)) in state.Tie.Where(t => !MonsterShown(player, t.Key)).ToList())
        {
            state.Tie.Remove(id);
            state.Altars[k].Tied.Remove(id);
            state.Altars[k].Dead.Remove(id);
        }
        if (state.Run is { } run)
        {
            foreach (uint id in run.Alive.Keys.Where(id => !MonsterShown(player, id)).ToList()) run.Alive.Remove(id);
            run.Daemons.Clear();
            run.Wisp = await CreateWisp(player, state, state.Altars[run.Altar]);
            await Create(player, run.Blocker, BlockerType, RitualWire.Blocker(state.Altars[run.Altar].Cell, RitualTables.RadiusCells),
                $"0x2bb Ritual blocker 0x{run.Blocker:x}");
        }
    }

    // ---- Tick ----------------------------------------------------------------------------------------------------------

    public async Task Advance(Player runner, double seconds)
    {
        if (runner.Paused || Of(runner) is not { Altars.Count: > 0 } state || (state.Done && !state.WispBuffActive) || !IsMonsterRunner(runner) || !Current(runner)) return;
        if (!await state.Gate.WaitAsync(0)) return;
        try
        {
            if ((state.Done && !state.WispBuffActive) || !Current(runner)) return;
            await Sync(runner, state);
            state.Now += Math.Max(0, seconds);
            if (state.WispBuffActive && state.Now >= state.WispBuffUntil)
            {
                state.WispBuffActive = false;
                foreach (var peer in InstanceClients(runner))
                    await SendCharacterStats(peer, PlayerStatChange.Build);
            }
            if (state.Done) return;
            double now = AreaTime(runner);
            if (state.Now >= state.RevealAt && HasUnshownAltar(state.Altars))
            {
                state.RevealAt = state.Now + RevealSeconds;
                var runnerPos = runner.Walk.Position(now);
                for (int k = 0; k < state.Altars.Count; k++)
                {
                    var altar = state.Altars[k];
                    if (!altar.Shown && (Distance(runnerPos, altar.Cell) <= RitualTables.RevealDistance
                        || AnyPeerNear(runner, altar.Cell, RitualTables.RevealDistance)))
                        await Reveal(runner, state, k);
                }
            }
            await Deaths(runner, state);
            for (int k = 0; k < state.Altars.Count; k++)
                if (state.Altars[k].State == Inactive && state.Altars[k].Shown) await CheckReady(runner, state, k);
            if (state.Run is { } run) await RunStep(runner, state, run);
        }
        finally { state.Gate.Release(); }
    }

    static bool HasUnshownAltar(List<Altar> altars)
    {
        for (int i = 0; i < altars.Count; i++)
            if (!altars[i].Shown) return true;
        return false;
    }

    static bool HasShownInactiveAltar(List<Altar> altars)
    {
        for (int i = 0; i < altars.Count; i++)
        {
            var altar = altars[i];
            if (altar.State == Inactive && altar.Shown) return true;
        }
        return false;
    }

    static bool AnyPeerNear(Player runner, (int X, int Y) cell, double maxDistance)
    {
        foreach (var entry in InGame)
        {
            var c = entry.Key;
            if (ReferenceEquals(c, runner) || !c.HasClient || !ReferenceEquals(c.Instance, runner.Instance) || !Current(c)) continue;
            if (Distance(c.Walk.Position(AreaTime(c)), cell) <= maxDistance) return true;
        }
        return false;
    }

    async Task Reveal(Player player, State state, int k)
    {
        var altar = state.Altars[k];
        altar.Shown = true;
        if (!InMap(player.Layout, altar.Cell))
        {
            Console.WriteLine($"[ritual] altar at {altar.Cell} is outside {player.Layout.Id}'s grid; not sent");
            altar.State = Done;
            return;
        }
        altar.Interactable = player.TakeObjectId();
        altar.Object = player.TakeObjectId();
        altar.Light = player.TakeObjectId();
        await CreateAltar(player, altar);
        await SpawnGuards(player, state, k);

        // Campaign & tutorial quest flags on first Ritual sight.
        var flags = new List<string>();
        if (!ClientHasQuestFlag(player, "RitualFound")) flags.Add("RitualFound");
        if (!ClientHasQuestFlag(player, "RitualAltarSeen")) flags.Add("RitualAltarSeen");
        if (player.Layout.Id == "1_2_8" && !ClientHasQuestFlag(player, "ForcedRitualUsedNorthernForest"))
            flags.Add("ForcedRitualUsedNorthernForest");
        else if (player.Layout.Id == "1_2_13" && !ClientHasQuestFlag(player, "ForcedRitualUsedDreadThicket"))
            flags.Add("ForcedRitualUsedDreadThicket");
        if (flags.Count > 0) await SetQuestFlags(player, flags);
    }

    static async Task CreateAltar(Player player, Altar altar)
    {
        float progress = Progress(altar);
        int enabled = altar.State is Ready or Done ? 1 : 0;
        await Create(player, altar.Interactable, InteractableType,
            RitualWire.Interactable(altar.Cell, altar.Rotation, altar.Rune.Row, progress, altar.State, enabled),
            $"0x2bb Ritual altar 0x{altar.Interactable:x} ({altar.Rune.Id}) at {altar.Cell}");
        await Create(player, altar.Object, ObjectType, RitualWire.Object(altar.Cell, altar.Rotation, altar.Rune.Row, progress, altar.State),
            $"0x2bb Ritual altar object 0x{altar.Object:x}");
        if (altar.Light != 0)
            await Create(player, altar.Light, LightType,
                RitualWire.Light(altar.Cell, altar.Rotation, altar.State, progress > 0 ? LightScale(progress) : null),
                $"0x2bb Ritual altar light 0x{altar.Light:x}");
    }

    static Task Create(Player player, uint id, string type, byte[] blob, string log) =>
        SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), blob), log,
            wake: (blob[0] & 0x02) == 0, awaitBirth: false);

    static Task CreatePool(Player player, Altar altar, uint pool, (int X, int Y) at) =>
        Create(player, pool, PoolType, RitualWire.Pool(at, 0, altar.Interactable), $"0x2bb Ritual BloodPool 0x{pool:x} for 0x{altar.Interactable:x}");

    async Task SpawnGuards(Player player, State state, int k)
    {
        var altar = state.Altars[k];
        if (altar.Guards.Count == 0) return;
        var layout = player.Layout;
        var rng = new Random(HashCode.Combine(state.Seed, altar.Cell.X, altar.Cell.Y, k));
        for (int p = 0; p < altar.Guards.Count; p++)
        {
            var pack = altar.Guards[p];
            (int X, int Y)? spot = null;
            for (int t = 0; t < 20 && spot is null; t++)
            {
                double a = rng.NextDouble() * Math.Tau;
                double r = RitualTables.GuardDistanceMin + rng.NextDouble() * (RitualTables.GuardDistanceMax - RitualTables.GuardDistanceMin);
                var c = ((int)Math.Round(altar.Cell.X + r * Math.Cos(a)), (int)Math.Round(altar.Cell.Y + r * Math.Sin(a)));
                if (Floor(layout, c)) spot = c;
            }
            if (spot is not { } at) continue;
            for (int m = 0; m < pack.Count; m++)
            {
                int row = pack[m];
                var c = Snap(layout, (at.X + rng.Next(-RitualTables.GuardScatter, RitualTables.GuardScatter + 1),
                    at.Y + rng.Next(-RitualTables.GuardScatter, RitualTables.GuardScatter + 1))) ?? at;
                // Promote first monster of pack 0 to Magic/Rare with ScarabRitual4 or naturally for richer tribute.
                var rarity = p == 0 && m == 0 && state.ExtraCorpses > 0 ? MonsterRarity.Rare
                    : m == 0 && rng.Next(4) == 0 ? MonsterRarity.Magic
                    : MonsterRarity.Normal;
                uint id = await Spawn(player, state, row, rarity, c, mine: false, rune: altar.Rune);
                if (id == 0) continue;
                state.Tie[id] = (k, Monsters.All[row].Id, rarity);
                altar.Tied.Add(id);
            }
        }
    }

    async Task Deaths(Player player, State state)
    {
        int radius = RitualTables.RadiusCells;
        if (state.Now >= state.ScanAt && HasShownInactiveAltar(state.Altars))
        {
            state.ScanAt = state.Now + ScanSeconds;
            double now = AreaTime(player);
            for (int a = 0; a < state.Altars.Count; a++)
            {
                var srcAltar = state.Altars[a];
                if (srcAltar.State != Inactive || !srcAltar.Shown) continue;
                foreach (var (id, m) in player.Area.MonstersNear(srcAltar.Cell, radius))
                {
                    if (state.Tie.ContainsKey(id) || state.Mine.Contains(id) || m.Life == 0) continue;
                    if (!m.Hostile || m.OwnerId is not null || m.SummonerId is not null || m.Variety is null) continue;
                    var cell = m.Walk.Position(now);
                    int best = -1;
                    double bestDistance = double.MaxValue;
                    for (int k = 0; k < state.Altars.Count; k++)
                    {
                        if (state.Altars[k].State != Inactive || !state.Altars[k].Shown) continue;
                        double d = Distance(cell, state.Altars[k].Cell);
                        if (d <= radius && d < bestDistance) (best, bestDistance) = (k, d);
                    }
                    if (best < 0) continue;
                    state.Tie[id] = (best, m.Variety.Id, m.Rarity);
                    state.Altars[best].Tied.Add(id);
                }
            }
        }
        List<(int Altar, string Path, MonsterRarity Rarity)>? died = null;
        foreach (var (id, (k, path, rarity)) in state.Tie)
        {
            var altar = state.Altars[k];
            if (altar.Dead.Contains(id) || Alive(player, id)) continue;
            altar.Dead.Add(id);
            (died ??= []).Add((k, path, rarity));
        }
        if (died is not null)
            foreach (var (k, path, rarity) in died) await Credit(player, state, k, path, rarity);
    }

    async Task Credit(Player player, State state, int k, string path, MonsterRarity rarity)
    {
        var altar = state.Altars[k];
        if (path.Length > 0) altar.Credit.Add((path, rarity));
        if (!altar.Shown || altar.Interactable == 0) return;
        var rng = new Random(HashCode.Combine(state.Seed, altar.Cell.X, altar.Cell.Y, altar.Dead.Count));
        int radius = RitualTables.RadiusCells;
        for (int t = 0; t < 12; t++)
        {
            double a = rng.NextDouble() * Math.Tau, r = radius * Math.Sqrt(rng.NextDouble());
            var c = ((int)Math.Round(altar.Cell.X + r * Math.Cos(a)), (int)Math.Round(altar.Cell.Y + r * Math.Sin(a)));
            if (!Floor(player.Layout, c)) continue;
            uint pool = player.TakeObjectId();
            await Create(player, pool, PoolType, RitualWire.Pool(c, (float)(rng.NextDouble() * Math.Tau), altar.Interactable),
                $"0x2bb Ritual BloodPool 0x{pool:x} for 0x{altar.Interactable:x}");
            altar.Pools.Add(pool);
            break;
        }
        await SendWorldRecord(player, altar.Light, InstanceEntities.ComponentUpdate(altar.Light, RitualWire.LightPositioned,
            RitualWire.LightScale(LightScale(Progress(altar)))), $"0x2bc Ritual light 0x{altar.Light:x} scale", null);
    }

    async Task CheckReady(Player player, State state, int k)
    {
        var altar = state.Altars[k];
        if (altar.Dead.Count < altar.Tied.Count) return;
        foreach (uint id in altar.Tied)
            if (!altar.Dead.Contains(id)) return;
        if (altar.Tied.Count == 0 && altar.Credit.Count == 0) return;
        altar.State = Ready;
        await Broadcast(player, RitualWire.AltarState(altar.Interactable, altar.Cell, Ready), $"0x165 Ritual altar 0x{altar.Interactable:x} ready");
        await States(player, altar, 1, [1, 1], 1);
    }

    static async Task States(Player player, Altar altar, byte action, int[] interactable, int state)
    {
        await SendWorldRecord(player, altar.Interactable, InstanceEntities.ComponentUpdate(altar.Interactable,
            RitualWire.InteractableAction, [action]), "0x2bc Ritual interactable 07", null);
        await SendWorldRecord(player, altar.Interactable, InstanceEntities.StateMachineUpdate(altar.Interactable,
            RitualWire.InteractableStates, new StateMachineData(interactable, 0)), "0x2bc Ritual interactable states", null);
        await SendWorldRecord(player, altar.Object, InstanceEntities.StateMachineUpdate(altar.Object,
            RitualWire.ObjectStates, new StateMachineData([state], 0)), "0x2bc Ritual object states", null);
        if (altar.Light != 0)
            await SendWorldRecord(player, altar.Light, InstanceEntities.StateMachineUpdate(altar.Light,
                RitualWire.LightStates, new StateMachineData([state], 0)), "0x2bc Ritual light states", null);
    }

    // ---- Clicking an Altar (Start Ready OR Open Favours on Done) -------------------------------------------------------

    public async Task<bool> OnUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (Of(player) is not { } state || state.Altars.FirstOrDefault(a => a.Interactable != 0 && a.Interactable == use.TargetId) is not { } altar)
            return false;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;

        // Clicking a completed altar opens the Ritual Favour window!
        if (altar.State == Done)
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null);
            await RitualFavours.HandleOpen(player, this);
            return true;
        }

        if (altar.State != Ready || state.Run is not null)
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null);
            return true;
        }
        int size = QuestObjectTypes.Find(InteractableType)?.Size ?? 4;
        if (!TryBeginObjectWalk(player, requestedAt, altar.Cell.X, altar.Cell.Y, size, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionStarted(player.Id, here.X, here.Y,
                altar.Cell.X, altar.Cell.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player starts Ritual altar 0x{altar.Interactable:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s",
                still: () => PlayerActionCurrent(player, action));
        try
        {
            if (arrive > now) await AreaDelay(player, arrive - now);
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
            if (!PlayerWalk.InReach(stop, altar.Cell, size, player.Layout.Walkable)) return true;
            await state.Gate.WaitAsync();
            try
            {
                int k = state.Altars.IndexOf(altar);
                if (k >= 0 && altar.State == Ready && state.Run is null && !state.Done) await Start(player, state, k);
            }
            finally { state.Gate.Release(); }
        }
        finally
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    // ---- Ritual Vessel Bottling (C→S 0x0074 / 0x0079) ------------------------------------------------------------------

    static readonly byte[] CurrencyRows = [PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory];

    public Task<bool> OnUseItemOnObject(Player player, byte[] message) =>
        message.Length != InstanceItems.UseItemOnObjectLength
            ? Task.FromResult(false)
            : BottleAltar(player, InstanceItems.ReadUseItemOnObject(message));

    public Task<bool> OnUseItemTypeOnObject(Player player, byte[] message)
    {
        if (message.Length < InstanceItems.UseItemTypeOnObjectLength) return Task.FromResult(false);
        var req = InstanceItems.ReadUseItemTypeOnObject(message);
        if (Of(player) is not { } state || state.Altars.All(a => a.Interactable != req.TargetObjectId))
            return Task.FromResult(false);
        var items = ItemsOf(player);
        foreach (byte row in CurrencyRows)
            if (items.Items(row).FirstOrDefault(i => i.Item.TypeHash == req.TypeHash && items.RowOf(i.Id) == row) is { } stack)
                return BottleAltar(player, new(PlayerInventories.InventoryId(row), stack.Id, req.TargetObjectId));
        return Task.FromResult(true);
    }

    async Task<bool> BottleAltar(Player player, InstanceItems.UseItemOnObject use)
    {
        if (Of(player) is not { } state) return false;
        var altar = state.Altars.FirstOrDefault(a => a.Interactable != 0 && a.Interactable == use.TargetObjectId);
        if (altar is null) return false;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
        var items = ItemsOf(player);
        if (PlayerInventories.Row(use.InventoryId) is not byte row
            || items.Items(row).FirstOrDefault(i => i.Id == use.ItemId) is not { } stack
            || ItemsFor(player).ByHash(stack.Item.TypeHash) is not { Id: RitualTables.RitualVesselId })
            return false;

        await state.Gate.WaitAsync();
        try
        {
            if (state.OpenedWithBloodVessel)
            {
                await Send(player, InstanceChat.ChatLine("Ritual Vessels cannot be used in a map opened with a Blood-filled Vessel"),
                    "0x0017 Ritual Vessel blocked (blood vessel map)");
                return true;
            }
            if (altar.State != Done)
            {
                await Send(player, InstanceChat.ChatLine("Ritual Vessels can only be used on a completed Ritual Altar"),
                    "0x0017 Ritual Vessel blocked (not done)");
                return true;
            }
            if (altar.Bottled)
            {
                await Send(player, InstanceChat.ChatLine("Ritual Vessels cannot be used on a Ritual Altar more than once"),
                    "0x0017 Ritual Vessel blocked (already bottled)");
                return true;
            }
            var vesselItem = BuildBloodFilledVessel(player, state, altar);
            if (vesselItem is null) return true;

            var consumed = items.ConsumeOne(row, stack.Id);
            if (consumed is null) return true;
            altar.Bottled = true;

            var removed = new List<uint> { stack.Id };
            var added = new List<InventoryItem>();
            if (consumed.Left is { } left) added.Add(left);

            byte depositRow = row == PlayerInventories.ExpandedInventory && !ExpandedInventoryGranted(player)
                ? PlayerInventories.MainInventory : row;
            if (depositRow == row && items.Fits(row, vesselItem, ShapeOf))
            {
                var placed = items.AddStacked(row, vesselItem, ShapeOf);
                removed.AddRange(placed.Changed.Select(i => i.Id));
                added.AddRange(placed.Changed);
                added.AddRange(placed.Added);
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), removed, added),
                    "0x00bf Ritual Vessel bottled -> Blood-filled Vessel");
            }
            else
            {
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), removed, added),
                    "0x00bf Ritual Vessel consumed");
                if (depositRow != row && items.Fits(depositRow, vesselItem, ShapeOf))
                {
                    var placed = items.AddStacked(depositRow, vesselItem, ShapeOf);
                    List<uint> depRemoved = [.. placed.Changed.Select(i => i.Id)];
                    List<InventoryItem> depAdded = [.. placed.Changed, .. placed.Added];
                    await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(depositRow), items.NextRevision(depositRow), depRemoved, depAdded),
                        "0x00bf Ritual Vessel bottled -> Blood-filled Vessel (main)");
                }
                else
                {
                    await DropMadeItem(player, vesselItem, altar.Cell, "Ritual Vessel bottling");
                }
            }
            await Send(player, InstanceChat.ChatLine("Captured the monsters of this Ritual Altar in a Blood-filled Vessel."),
                "0x0017 Ritual Vessel bottled chat");
        }
        finally { state.Gate.Release(); }
        return true;
    }

    static Item? BuildBloodFilledVessel(Player player, State state, Altar altar)
    {
        if (ItemsFor(player).Find(RitualTables.BloodFilledVesselId) is not { Supported: true } type) return null;
        var mvRows = ClientTables.TableOrEmpty("MonsterVarieties");
        var counts = new Dictionary<ushort, int>();
        foreach (var (path, _) in altar.Credit)
        {
            if (Monsters.Find(path) is not { } variety || variety.Row < 0 || variety.Row >= mvRows.Count) continue;
            if (mvRows[variety.Row].IntOrNull("HASH16") is not int hash16) continue;
            ushort h = (ushort)hash16;
            counts[h] = counts.GetValueOrDefault(h) + 1;
        }
        var monsters = counts
            .OrderByDescending(kv => kv.Value)
            .Take(16)
            .Select(kv => new RitualEncounterMonster(kv.Key, (ushort)Math.Clamp(kv.Value, 1, ushort.MaxValue)))
            .ToList();
        return ItemCatalog.Create(type) with
        {
            ItemLevel = (byte)Math.Clamp(state.Level, 1, 100),
            RitualEncounter = new RitualEncounterData(state.Level, player.Layout.Hash, monsters),
        };
    }

    static async Task DropMadeItem(Player player, Item item, (int X, int Y) at, string source)
    {
        var (x, y) = player.Layout.Walkable is { } walks && !walks(at.X, at.Y) ? player.Layout.Near(at) : at;
        var ground = new WorldItemObject(x, y, item);
        uint id = player.Instance.Ground.Add(ground, player, shared: false);
        await SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob(justDropped: true)),
            $"0x02bb Blood-filled Vessel 0x{id:x} from {source} at ({x}, {y})", wake: true, awaitBirth: false);
    }

    // ---- Start, Step, Daemons & Finish ---------------------------------------------------------------------------------

    async Task Start(Player player, State state, int k)
    {
        var altar = state.Altars[k];
        var rng = new Random(HashCode.Combine(state.Seed, altar.Cell.X, altar.Cell.Y, state.Completed));
        var monsters = altar.Credit.Select(c => new Revived { Path = c.Path, Rarity = c.Rarity, N = 1 })
            .Concat(state.Revived.Select(m => new Revived { Path = m.Path, Rarity = m.Rarity, N = m.N + 1 }))
            .Concat(state.VesselMonsters.Select(v => new Revived { Path = v.Path, Rarity = v.Rarity, N = 1 }))
            .ToList();
        Shuffle(monsters, rng);
        var locators = RitualTables.LocatorsOf(altar.Rune.Ao);
        var order = new List<(double X, double Y)>();
        foreach (string part in altar.Rune.Order)
        {
            var points = locators.Part(part).ToList();
            Shuffle(points, rng);
            order.AddRange(points);
        }
        if (order.Count == 0) order = locators.Outside.Count > 0 ? [.. locators.Outside] : [(0, 0)];
        altar.State = Running;
        var run = new Run
        {
            Altar = k,
            StartedAt = state.Now,
            Monsters = monsters,
            Order = order,
            Rng = rng,
            Blocker = player.TakeObjectId(),
            NextHazardAt = 2.5,
        };
        state.Run = run;
        state.Revived = [];
        // Bottled vessel monsters enter once on the first activated altar and revive normally via state.Revived on later altars.
        state.VesselMonsters.Clear();

        // Apply server-side circular arena containment ring + IsInRitual stat refresh for all clients in the instance.
        run.Wisp = await CreateWisp(player, state, altar);
        if (run.Wisp != 0)
        {
            state.WispBuffUntil = state.Now + 60.0;
            state.WispBuffActive = true;
        }
        foreach (var peer in InstanceClients(player))
        {
            peer.SetRingBlocker(run.Blocker, altar.Cell.X, altar.Cell.Y, RitualTables.RadiusCells);
            if (run.Wisp != 0)
                peer.Area.Buffs.Set(peer.Id, new(RitualTables.WispBuffRow, peer.Id, U68: Buff.Seconds(60.0)));
            await SendCharacterStats(peer, PlayerStatChange.Build);
        }

        await Broadcast(player, RitualWire.AltarState(altar.Interactable, altar.Cell, Running), $"0x165 Ritual altar 0x{altar.Interactable:x} starts");
        if (altar.Rune.Environment is int env)
            await Broadcast(player, InstanceEnvironments.Data(((ushort)env, 0)), $"0xdf Ritual environment {env} on");
        await States(player, altar, 0, [2, 0], 2);
        await Create(player, run.Blocker, BlockerType, RitualWire.Blocker(altar.Cell, RitualTables.RadiusCells), $"0x2bb Ritual blocker 0x{run.Blocker:x}");
    }

    async Task RunStep(Player player, State state, Run run)
    {
        var altar = state.Altars[run.Altar];
        var layout = player.Layout;
        double t = state.Now - run.StartedAt;

        // 1. Daemon spawning & combat hazard pulses.
        if (altar.Rune.Daemon is { Varieties.Count: > 0, EveryMs: > 0 } daemon)
        {
            while (run.DaemonsDue < Math.Max(1, daemon.Max) && t >= run.DaemonsDue * daemon.EveryMs / 1000.0)
            {
                var spots = RitualTables.LocatorsOf(altar.Rune.Ao).Daemon;
                var xy = spots.Count > 0 ? spots[run.Rng.Next(spots.Count)] : (0, 0);
                run.DaemonsDue++;
                if (Snap(layout, RitualTables.LocatorCell(altar.Cell, altar.Rotation, xy)) is not { } c) continue;
                int row = daemon.Varieties[run.Rng.Next(daemon.Varieties.Count)];
                if (row >= 0 && row < Monsters.All.Count && Monsters.All[row] is { Supported: true } variety
                    && RitualTables.IsMonsterType(variety)
                    && await Spawn(player, state, row, MonsterRarity.Normal, c, mine: true, rune: altar.Rune) is uint monster and not 0)
                    run.Alive[monster] = new Revived { Path = variety.Id, Rarity = MonsterRarity.Normal, N = 1 };
                else if (await CreateDaemon(player, state, row, c) is uint id and not 0)
                    run.Daemons.Add(id);
            }
            if (run.Daemons.Count > 0 && t >= run.NextHazardAt)
            {
                double period = Math.Clamp(daemon.EveryMs / 1000.0, 2.5, 6.0);
                run.NextHazardAt = t + period;
                await PulseDaemonHazard(player, state, altar, run);
            }
        }

        // 2. Wave spawning with SpawnSpeed +% and MaxAlive +% (RitualBalancePerLevel + Atlas + Blood-filled Vessels).
        double speedScale = Math.Max(0.25, 1.0 + state.SpawnSpeedPct / 100.0);
        int maxAlive = Math.Max(8, RitualTables.MaxAlive(state.Level) * Math.Max(50, 100 + state.MaxAlivePct) / 100);
        double first = RitualTables.FirstWaveFrames * RitualTables.Frame,
            spacing = RitualTables.WaveSpacingFrames * RitualTables.Frame / speedScale,
            gap = RitualTables.WaveGapFrames * RitualTables.Frame / speedScale;
        if (run.Waves.Count == 0 && t >= first) run.Waves.Add(new Wave { At = t });
        if (run.Waves.Count > 0)
        {
            var wave = run.Waves[^1];
            while (wave.Spawned < RitualTables.WaveSize && run.Next < run.Monsters.Count && run.Alive.Count < maxAlive && t >= wave.At + wave.Spawned * spacing)
            {
                var m = run.Monsters[run.Next];
                run.Next++;
                wave.Spawned++;
                (int X, int Y)? cell = null;
                for (int i = 0; i < run.Order.Count && cell is null; i++)
                    cell = Snap(layout, RitualTables.LocatorCell(altar.Cell, altar.Rotation, run.Order[(run.Next + i) % run.Order.Count]));
                if (cell is not { } at || Monsters.Find(m.Path) is not { Supported: true } variety
                    || !variety.Id.Equals(m.Path, StringComparison.Ordinal)) continue;
                uint id = await Spawn(player, state, variety.Row, m.Rarity, at, mine: true, rune: altar.Rune);
                if (id == 0) continue;
                wave.Ids.Add(id);
                run.Alive[id] = m;
                if (altar.Pools.Count > 0)
                {
                    uint pool = altar.Pools[0];
                    altar.Pools.RemoveAt(0);
                    await SendWorldRecord(player, pool, InstanceEntities.ComponentUpdate(pool, RitualWire.PoolTransition, [0x02]),
                        $"0x2bc Ritual BloodPool 0x{pool:x} fades", null);
                    altar.Faded.Add((pool, state.Now));
                }
            }
            if (wave.Spawned >= RitualTables.WaveSize && run.Next < run.Monsters.Count)
            {
                int alive = 0;
                for (int i = 0; i < wave.Ids.Count; i++)
                    if (Alive(player, wave.Ids[i])) alive++;
                if (alive <= RitualTables.NextWaveAlive * Math.Max(1, wave.Ids.Count) || t >= wave.At + wave.Spawned * spacing + gap)
                    run.Waves.Add(new Wave { At = t });
            }
        }

        // 3. Revived monster deaths -> Tribute calculation (collected without allocating while nothing died).
        List<(uint Id, Revived Monster)>? died = null;
        foreach (var (id, m) in run.Alive)
        {
            if (Alive(player, id)) continue;
            (died ??= []).Add((id, m));
        }
        if (died is not null)
        {
            foreach (var (id, m) in died)
            {
                run.Alive.Remove(id);
                run.Killed++;
                int baseTribute = RitualTables.TributeOf(m.Rarity, m.N);
                int incPct = state.TributePct + (state.WispBuffActive && state.WispBuffUntil > state.Now ? 100 : 0);
                double gained = baseTribute * Math.Max(0, 100 + incPct) / 100.0;
                if (state.TributeMorePct != 0)
                    gained *= Math.Max(0, 100 + state.TributeMorePct) / 100.0;
                if (m.Rarity == MonsterRarity.Unique && state.UniqueTributeMorePct != 0)
                    gained *= Math.Max(0, 100 + state.UniqueTributeMorePct) / 100.0;
                state.Tribute += (int)gained;
                await Broadcast(player, RitualWire.Tribute(state.Tribute), $"0x167 Ritual tribute {state.Tribute}");
                state.Revived.Add(m);
            }
        }

        if (altar.Faded.Count > 0)
        {
            for (int i = altar.Faded.Count - 1; i >= 0; i--)
            {
                var faded = altar.Faded[i];
                if (state.Now - faded.At < RitualTables.PoolFadeFrames * RitualTables.Frame) continue;
                await SendWorldRemoval(player, faded.Pool, $"0x2bd Ritual BloodPool 0x{faded.Pool:x}");
                altar.Faded.RemoveAt(i);
            }
        }
        if (run.Next >= run.Monsters.Count && run.Alive.Count == 0 && run.Waves.Count > 0 && t >= first)
            await Finish(player, state, run);
    }

    /// <summary>Executes the rune's daemon arena hazard (elemental/chaos/physical pulses, or golden coin tribute).</summary>
    async Task PulseDaemonHazard(Player player, State state, Altar altar, Run run)
    {
        string id = altar.Rune.Id;
        // Golden/Opulent coin shower grants bonus Tribute on pulse!
        if (id.Contains("Golden", StringComparison.OrdinalIgnoreCase))
        {
            state.Tribute += 15 + state.Level / 4;
            await Broadcast(player, RitualWire.Tribute(state.Tribute), $"0x167 Ritual golden coin +{15 + state.Level / 4} -> {state.Tribute}");
            return;
        }
        DamageType dmgType = id.Contains("Fire", StringComparison.OrdinalIgnoreCase) || id.Contains("Apocalyp", StringComparison.OrdinalIgnoreCase) ? DamageType.Fire
            : id.Contains("Cold", StringComparison.OrdinalIgnoreCase) || id.Contains("Glacial", StringComparison.OrdinalIgnoreCase) ? DamageType.Cold
            : id.Contains("Lightning", StringComparison.OrdinalIgnoreCase) || id.Contains("Volatile", StringComparison.OrdinalIgnoreCase) ? DamageType.Lightning
            : id.Contains("Chaos", StringComparison.OrdinalIgnoreCase) || id.Contains("Malevolent", StringComparison.OrdinalIgnoreCase) || id.Contains("Nameless", StringComparison.OrdinalIgnoreCase) ? DamageType.Chaos
            : DamageType.Physical;

        // Pick a hazard strike point near a random inside player (offset slightly so moving players naturally dodge!).
        var targets = InstanceClients(player)
            .Where(c => c.PlayerLife > 0 && !c.Immune && !InGracePeriod(c)
                && Distance(c.Walk.Position(AreaTime(c)), altar.Cell) <= RitualTables.RadiusCells)
            .ToList();
        if (targets.Count == 0) return;
        var chosen = targets[run.Rng.Next(targets.Count)];
        var pPos = chosen.Walk.Position(AreaTime(chosen));
        var strike = (pPos.X + run.Rng.Next(-14, 15), pPos.Y + run.Rng.Next(-14, 15));
        int baseDmg = Math.Max(8, (int)(state.Level * state.Level * 0.14 + state.Level * 4));
        if (altar.Rune.IsUber) baseDmg = baseDmg * 135 / 100;
        const double hazardRadius = 12.0;

        foreach (var target in targets)
        {
            if (Distance(target.Walk.Position(AreaTime(target)), strike) > hazardRadius) continue;
            if (!TrySyntheticEnemyHit(target, dmgType, baseDmg, block: true, spell: true, suppress: true, out var hit)) continue;
            if (hit.Damage > 0)
            {
                var pStats = CharacterPlayerStats(target);
                ApplyPlayerAilmentsLocked(target, DamagePacket.Of(dmgType, baseDmg, baseDmg), hit.Taken,
                    false, hit.Blocked, AreaTime(target), pStats);
            }
            await SendPlayerEnergyShield(target);
            if (hit.Mana != hit.ManaBefore) await SendPlayerMana(target, "ritual daemon mana");
            if (target.PlayerLife == 0) await PlayerDied(target, $"death from Ritual {altar.Rune.Id} daemon");
        }
    }

    async Task Finish(Player player, State state, Run run)
    {
        var altar = state.Altars[run.Altar];
        altar.State = Done;
        state.Completed++;

        // Clear circular arena blocker & refresh IsInRitual stat for every client in the instance.
        foreach (var peer in InstanceClients(player))
            peer.RemoveBlocker(run.Blocker);

        // Reveal newly unlocked Favour items before broadcasting 0x0166!
        await RitualFavours.RevealForProgress(player, state, InstanceClients);

        await Broadcast(player, RitualWire.AltarState(altar.Interactable, altar.Cell, Done), $"0x165 Ritual altar 0x{altar.Interactable:x} done");
        if (altar.Rune.Environment is int env)
            await Broadcast(player, InstanceEnvironments.Data(((ushort)env, RitualTables.EndEnvironmentSeconds)), $"0xdf Ritual environment {env} off");
        await Broadcast(player, RitualWire.Favours(state.Tribute, state.RerollsUsed, RitualFavours.BuildRows(state), state.Altars.Count, state.Completed),
            $"0x166 Ritual favours ({state.Completed}/{state.Altars.Count} done)");
        await Broadcast(player, RitualWire.Tribute(state.Tribute), $"0x167 Ritual tribute {state.Tribute}");
        await States(player, altar, 1, [3, 1], 3);
        if (run.Wisp != 0) await SendWorldRemoval(player, run.Wisp, $"0x2bd Ritual wisp 0x{run.Wisp:x}");
        foreach (uint daemon in run.Daemons) await SendWorldRemoval(player, daemon, $"0x2bd Ritual daemon 0x{daemon:x}");
        await SendWorldRemoval(player, run.Blocker, $"0x2bd Ritual blocker 0x{run.Blocker:x}");
        await SendWorldRemoval(player, altar.Light, $"0x2bd Ritual light 0x{altar.Light:x}");
        foreach (uint pool in altar.Pools.Concat(altar.Faded.Select(f => f.Pool)))
            await SendWorldRemoval(player, pool, $"0x2bd Ritual BloodPool 0x{pool:x}");
        altar.Pools.Clear();
        altar.Faded.Clear();
        altar.Light = 0;
        state.Run = null;

        // Refresh stats now that state.Run is null so IsInRitual clears immediately.
        foreach (var peer in InstanceClients(player))
            await SendCharacterStats(peer, PlayerStatChange.Build);

        // Final altar completion: Sacrificial Due Atlas passive (15% chance to drop a Blood-filled Vessel).
        bool allDone = state.Altars.All(a => a.State >= Done);
        if (allDone)
        {
            state.Done = true;
            if (state.VesselDropChancePct > 0 && run.Rng.Next(100) < state.VesselDropChancePct
                && BuildBloodFilledVessel(player, state, altar) is { } vessel)
                await DropMadeItem(player, vessel, altar.Cell, "Sacrificial Due final Ritual altar");
            if (!ClientHasQuestFlag(player, "RitualCompleteAll"))
                await SetQuestFlags(player, ["RitualCompleteAll"]);
        }
        if (!ClientHasQuestFlag(player, "RitualFightComplete"))
            await SetQuestFlags(player, ["RitualFightComplete"]);
    }

    // ---- Monsters & Daemons --------------------------------------------------------------------------------------------

    async Task<uint> Spawn(Player player, State state, int row, MonsterRarity rarity, (int X, int Y) cell, bool mine, RitualTables.Rune rune)
    {
        var all = Monsters.All;
        if (row < 0 || row >= all.Count || all[row] is not { Supported: true } variety)
        {
            Console.WriteLine($"[ritual] MonsterVarieties row {row} has no supported variety; not spawned");
            return 0;
        }
        if (!InMap(player.Layout, cell)) return 0;
        uint id = await SpawnMonster(player, variety, (byte)Math.Clamp(state.Level, 1, 100), rarity, cell.X, cell.Y, log: false,
            extraStats: rune.BuffStats is { Count: > 0 } stats ? stats : null, extraMods: state.ModSet);
        if (id != 0 && mine) state.Mine.Add(id);
        return id;
    }

    Task<uint> CreateWisp(Player player, State state, Altar altar) =>
        state.SpawnWisp && Monsters.Find("Metadata/Monsters/LeagueRitual/RitualWispDaemon") is { Supported: true } wisp
            ? CreateDaemon(player, state, wisp.Row, altar.Cell)
            : Task.FromResult(0u);

    async Task<uint> CreateDaemon(Player player, State state, int row, (int X, int Y) cell)
    {
        var all = Monsters.All;
        if (row < 0 || row >= all.Count || all[row] is not { Supported: true } variety || !InMap(player.Layout, cell)) return 0;
        byte level = (byte)Math.Clamp(state.Level, 1, 100);
        if (player.Area.MonsterTypes.Add((variety.Row, level)))
        {
            var types = InstanceEntities.MonsterTypes([((ushort)variety.Row, level)]);
            string log = $"0x0020 {variety.Id}@{level}";
            // The direct send encrypts its buffer in place, so the entry queued for the frame flush keeps the plaintext
            // and the direct send gets a copy; a player sent one directly is left out of the flush, and peers of an area
            // this connection does not own get copies, as no flush reaches them.
            bool direct = !player.Connection.WorldOutput.Started;
            if (player.Area.Owned) player.Area.Delta.Add(0, types, log, AreaDelta.Kind.Message, except: direct ? player : null);
            if (direct) await Send(player, types.ToArray(), log);
            if (!player.Area.Owned)
                foreach (var peer in InstanceClients(player))
                    if (!ReferenceEquals(peer, player))
                        await Send(peer, types.ToArray(), log);
        }
        uint id = player.TakeObjectId();
        state.Mine.Add(id);
        var blob = new MonsterObject(cell.X, cell.Y, 1, variety.ComponentsOrBase, StateMachine: variety.StateMachine).Blob();
        await SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level)), blob),
            $"0x2bb Ritual daemon 0x{id:x} {variety.Id.Split('/')[^1]}", wake: true, awaitBirth: false);
        return id;
    }

    static bool Alive(Player player, uint id) =>
        player.Area.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0 || player.Area.DormantMonsters.ContainsKey(id);

    // ---- Helpers -------------------------------------------------------------------------------------------------------

    static float Progress(Altar altar) => altar.Tied.Count == 0 ? 0 : (float)altar.Dead.Count / altar.Tied.Count;

    static float LightScale(float progress) => (float)(1 + RitualTables.LightGain * Math.Clamp(progress, 0, 1));

    static bool InMap(AreaLayout layout, (int X, int Y) c) => c.X >= 0 && c.Y >= 0 && c.X < layout.GridW && c.Y < layout.GridH;

    static bool Floor(AreaLayout layout, (int X, int Y) c) => InMap(layout, c) && layout.WalkableFor(c.X, c.Y, 0);

    static (int X, int Y)? Snap(AreaLayout layout, (int X, int Y) c)
    {
        if (Floor(layout, c)) return c;
        (int D, (int X, int Y) At)? best = null;
        for (int dx = -6; dx <= 6; dx++)
            for (int dy = -6; dy <= 6; dy++)
            {
                var q = (c.X + dx, c.Y + dy);
                int d = dx * dx + dy * dy;
                if (Floor(layout, q) && (best is null || d < best.Value.D)) best = (d, q);
            }
        return best?.At;
    }

    static void Shuffle<T>(List<T> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
