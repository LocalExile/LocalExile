using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using PlayerWalk = GameServer.Core.World.PlayerWalk;

namespace GameServer.Modules.Expedition;

/// <summary>
/// The Expedition encounter of a map or campaign area: plan → reveal → placing → chain → fight → over.
/// <para>
/// The encounter belongs to the instance (<see cref="InstanceState"/>), as the Simulacrum does: it is set up once, replayed to
/// every player who enters (the monster types, the ruleset's messages, and the area's own object records for the objects), and
/// goes with the instance or the module's unload, never with a player leaving, except for the faction leader's bodies, which go when the last player leaves. Its objects are the area's: every create,
/// update and removal goes through SendWorldCreate / SendWorldRecord / SendWorldRemoval, so lockstep frames and late joiners
/// stay in order; an object of a new area of the same instance is created again from its piece.
/// </para>
/// <para>
/// The encounter stands where the area's own terrain puts it. An instance that rolls one is played with the
/// ExpeditionPlugin terrain plugin state (<see cref="PluginStates"/>); the client's generation then places the league's
/// encounter room, and so does the Gateway's (LevelGenerator.LeaguePluginFeature). The room's one server object line is
/// the detonator: the cell the official server creates it at (official MapWorldsForbiddenWoods seed 1304805886: the room
/// at tile (35, 26) rotation 0, the line's cell (839, 631) and the detonator create's cell the same), and the only thing
/// that names the detonator's type to the client (<see cref="DetonatorType"/>). So the encounter is placed only where the
/// layout holds that room (<see cref="RoomDetonator"/>).
/// </para>
/// <para>
/// Times are seconds (a capture's 30-frame steps divided by 30); distances are cells.
/// </para>
/// </summary>
public sealed class ExpeditionEncounter : IDisposable
{
    public enum Stage { Hidden, Placing, Chain, Fight, Over }
    public sealed record Reward(bool Runic, int Remnants);

    public sealed record Marker((int X, int Y) Cell, ExpeditionTables.MarkerKind Kind, ExpeditionTables.ChestTier Tier,
        string ChestType, uint Id, double Rotation = 0, string Ao = "");
    /// <summary>A remnant and the modifiers it carries (<see cref="ExpeditionTables.RollRelicMods"/>): they go out in its
    /// create's ObjectMagicProperties, their stats are summed in the <c>0x74 03</c> summary of the remnants a placement
    /// covers, and once a blast destroys the remnant they apply to everything that blast and the later ones dig up
    /// (ClientStrings <c>ExpeditionRemnantModifiersFunctionText</c>).</summary>
    public sealed record Remnant((int X, int Y) Cell, IReadOnlyList<ExpeditionTables.RelicMod> Mods, uint Id, double Rotation = 0);
    public sealed record Plan((int X, int Y) Detonator, float Rotation, string FactionId, int Explosives,
        List<Marker> Markers, List<Remnant> Remnants, (int MinX, int MinY, int MaxX, int MaxY) Box,
        List<(int X, int Y, byte Size)> Dirt)
    {
        public byte Radius { get; init; } = (byte)ExpeditionTables.BlastRadius;
    }

    sealed class Chest
    {
        public required ExpeditionTables.ChestTier Tier;
        public required (int X, int Y) Cell;
        public required string Type;
        /// <summary>The summed stats of the remnants destroyed by the time this chest was dug.</summary>
        public IReadOnlyList<(uint Stat, int Value)> Mods = [];
        public bool Ready;
        public bool Opened;
    }

    /// <summary>An object of the encounter and how its create is written, so a new area of the instance gets it again.</summary>
    sealed class Piece(uint id, string type, string note, Func<byte[]?> blob)
    {
        public uint Id => id;
        public string Type => type;
        public string Note => note;
        public Func<byte[]?> Blob => blob;
    }

    sealed class State
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        // What the layout says (decided once).
        public bool Checked, Placed;
        public string Owner = "";
        public bool Made, Failed;
        public Plan? Plan;
        public GameArea? Area;
        public Stage Stage = Stage.Hidden;
        public bool Revealed;
        public uint DetonatorId;
        public readonly Dictionary<uint, Piece> Pieces = [];
        public readonly List<(int X, int Y)> Explosives = [];
        public readonly List<uint> ExplosiveIds = [];
        /// <summary>The fuses and poles that lead to each explosive, removed with its blast.</summary>
        public readonly List<List<uint>> Chains = [];
        public int Left;
        public double Clock;
        public readonly List<(double At, string Action, int Index)> Queue = [];
        public readonly HashSet<int> Dug = [];
        public readonly HashSet<int> Broken = [];
        public readonly Dictionary<uint, Chest> Chests = [];
        public int Blasts;
        public readonly System.Collections.Concurrent.ConcurrentDictionary<uint, SpawnedMonster> Monsters = [];
        /// <summary>The faction leader spawned as a monster in this area: 0 when not spawned.</summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<uint, Reward> Rewards = [];
        public SavedBody[]? PendingBodies;
        public uint LeaderId;
        /// <summary>Where the leader stands, and the talk entry its monster body is registered under.</summary>
        public (int X, int Y) LeaderCell;
        public NpcCatalog.Npc? LeaderNpc;
        public int LeaderBodySize;
    }

    const string SaveKey = "Expedition.encounter.v1";
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    sealed record SavedPiece(uint Id, string Type, string Note, byte[] Blob);
    sealed record SavedBody(uint Id, int Variety, int X, int Y, Reward Reward);
    sealed record Saved(string Owner, Plan? Plan, Stage Stage, bool Revealed, uint Detonator,
        SavedPiece[] Pieces, (int X, int Y)[] Explosives, uint[] ExplosiveIds, List<uint>[] Chains,
        int Left, double Clock, (double At, string Action, int Index)[] Queue, int[] Dug, int[] Broken,
        Dictionary<uint, Chest> Chests, int Blasts, SavedBody[] Bodies);

    static void Persist(Player player, State state)
    {
        if (state.Plan is null) return;
        SavedBody[] bodies;
        lock (state)
        {
            var live = new List<SavedBody>();
            foreach (var (id, reward) in state.Rewards)
                if (player.Area.Monsters.TryGetValue(id, out var body) && !body.Dead && body.Life > 0 && body.Variety is { } variety)
                {
                    var at = body.Walk.Position(AreaTime(player));
                    live.Add(new(id, variety.Row, at.X, at.Y, reward));
                }
            if (state.PendingBodies is { } pending) live.AddRange(pending);
            bodies = [.. live];
        }
        player.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(new Saved(state.Owner, state.Plan, state.Stage,
            state.Revealed, state.DetonatorId,
            [.. state.Pieces.Values.Select(p => (Piece: p, Blob: p.Blob())).Where(p => p.Blob is not null)
                .Select(p => new SavedPiece(p.Piece.Id, p.Piece.Type, p.Piece.Note, p.Blob!))],
            [.. state.Explosives], [.. state.ExplosiveIds], [.. state.Chains], state.Left, state.Clock, [.. state.Queue],
            [.. state.Dug], [.. state.Broken], state.Chests, state.Blasts, bodies), Json);
    }

    static void BindBodies(Player player, State state)
    {
        if (state.PendingBodies is null) return;
        lock (state)
        {
            if (state.PendingBodies is not { } pending) return;
            var used = new HashSet<uint>(state.Rewards.Keys);
            var held = HeldBackLocked(player);
            var remaining = new List<SavedBody>();
            foreach (var body in pending)
            {
                var match = player.Area.Monsters.FirstOrDefault(p => !used.Contains(p.Key) && !p.Value.Dead
                    && p.Value.Variety?.Row == body.Variety && (p.Key == body.Id
                        || Distance(p.Value.Walk.Position(AreaTime(player)), (body.X, body.Y)) <= 4));
                if (match.Value is not null)
                {
                    used.Add(match.Key); state.Monsters[match.Key] = match.Value; state.Rewards[match.Key] = body.Reward;
                }
                else if (held.Any(h => h.Variety == body.Variety && Distance((h.X, h.Y), (body.X, body.Y)) <= 4))
                {
                    remaining.Add(body);
                }
            }
            state.PendingBodies = remaining.Count > 0 ? [.. remaining] : null;
        }
    }

    static State Restore(Player player, string text)
    {
        var saved = JsonSerializer.Deserialize<Saved>(text, Json)!;
        byte areaLevel = AreaLevel(player);
        var state = new State
        {
            Checked = true, Placed = true, Made = true, Owner = saved.Owner, Plan = saved.Plan,
            Stage = saved.Stage, Revealed = saved.Revealed, DetonatorId = saved.Detonator,
            Left = saved.Left, Clock = saved.Clock, Blasts = saved.Blasts,
            PendingBodies = saved.Bodies.Length > 0 ? saved.Bodies : null,
        };
        state.Explosives.AddRange(saved.Explosives); state.ExplosiveIds.AddRange(saved.ExplosiveIds);
        state.Chains.AddRange(saved.Chains); state.Queue.AddRange(saved.Queue);
        state.Dug.UnionWith(saved.Dug); state.Broken.UnionWith(saved.Broken);
        foreach (var (id, chest) in saved.Chests) state.Chests[id] = chest;
        foreach (var piece in saved.Pieces)
        {
            Func<byte[]?> blob = () => piece.Blob;
            if (piece.Type == DetonatorType && state.Plan is { } plan)
                blob = () => ExpeditionWire.Object(DetonatorType, plan.Detonator, plan.Rotation, animated: 0x40,
                    interaction: (byte)(state.Stage < Stage.Chain ? 1 : 0), stateValues: DetonatorStates(state));
            else if (state.Chests.TryGetValue(piece.Id, out var chest))
                blob = () => ExpeditionWire.Object(chest.Type, chest.Cell, 0, areaLevel,
                    interaction: (byte)(chest.Ready && !chest.Opened ? 1 : 0), stateValues: [chest.Opened ? 3 : chest.Ready ? 2 : 1]);
            else if (piece.Type == RelicType && state.Plan?.Remnants.FirstOrDefault(r => r.Id == piece.Id) is { } remnant)
                blob = () => ExpeditionWire.Object(RelicType, remnant.Cell, remnant.Rotation, ao: ExpeditionTables.RelicAo,
                    mods: [.. remnant.Mods.Select(m => new ItemMod(ModSection.Explicit, m.Hash, m.Values))],
                    stateValues: state.Broken.Contains(state.Plan.Remnants.IndexOf(remnant)) ? BrokenRemnant : null);
            state.Pieces[piece.Id] = new(piece.Id, piece.Type, piece.Note, blob);
        }
        return state;
    }

    /// <summary>The detonator's type. It is not in the client's static object register: the one file that names it is the
    /// encounter room <c>Metadata/Terrain/Leagues/Expedition/encounter.arm</c>, so it is created where that room is in the
    /// layout and nowhere else.</summary>
    public const string DetonatorType = "Metadata/MiscellaneousObjects/Expedition/ExpeditionDetonator";
    const string ExplosiveType = "Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosive";
    const string PoleType = "Metadata/MiscellaneousObjects/Expedition/ExpeditionConnectorPole";
    const string FuseType = "Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosiveFuse";
    const string MarkerType = "Metadata/MiscellaneousObjects/Expedition/ExpeditionMarker";
    const string RelicType = "Metadata/MiscellaneousObjects/Expedition/ExpeditionRelic";
    const string EffectType = ExpeditionTables.EffectType;
    const string MapOwnerFlag = "IsMapOwner";

    // The encounter's own draws, apart from the packs' ("EXPD").
    const int Salt = 0x45585044;

    readonly ConditionalWeakTable<InstanceState, State> _states = new();
    readonly Func<Player, double, Task> _advance;

    public ExpeditionEncounter() => _advance = Advance;

    /// <summary>The module's unload: the encounters go with it.</summary>
    public void Dispose() => _states.Clear();

    /// <summary>The instance's encounter state, made at the first ask of an area that declares ExpeditionPlugin; null for an
    /// area that does not (a town, a hideout, any other area), which never gets one, so the checks that run for every area
    /// allocate nothing there.</summary>
    State? StateOf(Player player)
    {
        if (player.Instance is not { } instance) return null;
        if (_states.TryGetValue(instance, out var state)) return state;
        if (DeclaredFaction(player) < 0) return null;
        return _states.GetValue(instance, _ => instance.ContentState.TryGetValue(SaveKey, out var text)
            ? Restore(player, text) : new State());
    }

    public bool IsActivePause(Player player) =>
        player.Instance is { } instance && _states.TryGetValue(instance, out var state) && state.Stage is Stage.Chain or Stage.Fight;

    /// <summary>
    /// The terrain plugin state a new instance of <paramref name="areaId"/> is played with: ExpeditionPlugin's, for one of
    /// the four factions, when the area's chance (its WorldAreaLeagueChances column for CoreLeagues row 21: 8 in a map, 12
    /// to 15 in Acts 6 to 10, 0 in Acts 1 to 5 and a town) gives it an encounter. Null otherwise. CoreLeagues marks the row
    /// "not rolled" (+0xa0), so the client's own roll (<see cref="LeagueChances.Count"/>) never gives one: the encounter is
    /// what the server declares with the plugin. [estimate] the faction is drawn uniformly. The roll also honours what a map
    /// switches off (MapDisableLeagues, AtlasKeystoneNoExtraContent, MapExtraContentHasNoBaseChanceToSpawn): the request names the
    /// area's stats.
    /// </summary>
    public static IReadOnlyList<(ushort Plugin, byte[] State)>? PluginStates(string areaId, Random rng, IReadOnlyDictionary<uint, int>? areaStats = null, bool hardMode = false)
    {
        if (ExpeditionTables.PluginId is not { } plugin || ExpeditionTables.RulesetRow is null) return null;
        int S(uint stat) => areaStats?.GetValueOrDefault(stat) ?? 0;
        // What a map switches off, as the client's own roll reads it (0x141ea8b40): the league's bit of the disabled-leagues
        // mask, nothing-more maps, the keystone, a Blighted map (seven Blight encounters and nothing else), and the league's
        // own disable stat, which only a stat that forces or adds to the league's chance outweighs.
        if ((S(Stat.MapDisableLeagues) & (1 << LeagueChances.Expedition)) != 0 || S(Stat.MapNukeEverything) != 0
            || S(Stat.AtlasKeystoneNoExtraContent) != 0 || S(Stat.IsBlightedMap) != 0) return null;
        bool guaranteed = S(Stat.MapExpeditionLeague) > 0;
        if (!guaranteed && S(Stat.MapDisableExpeditionFromChance) != 0
            && S(Stat.MapExpeditionEncounterChancePlusPct) <= 0) return null;
        // The area's own chance (WorldAreaLeagueChances, the Ruthless column in a hard-mode league). A test's forced chance
        // replaces it only where the area has one: a town, a hideout and a boss arena have a row of zeros and never roll.
        int natural = LeagueChances.Current.ChanceOf(areaId, LeagueChances.Expedition, hardMode).Chance;
        int baseChance = S(Stat.MapExtraContentHasNoBaseChanceToSpawn) > 0 || natural <= 0 ? 0 : ForcedChance() ?? natural;
        if (!guaranteed)
        {
            long chance = baseChance * Math.Max(0L, 100L + S(Stat.MapLeagueBaseChancePlusPctFinal)) / 100
                + S(Stat.MapLeagueContentAdditionalChancePct) + S(Stat.MapExpeditionEncounterAdditionalChancePct);
            long multiplier = Math.Max(0L, 100L + S(Stat.MapExpeditionEncounterChancePlusPct));
            // A product of 10000 already guarantees a roll, including for stacked map modifiers.
            long percent = Math.Clamp(chance, 0L, 10000L) * Math.Min(multiplier, 10000L) / 100;
            if (percent == 0 || (percent < 100 && rng.Next(100) >= percent)) return null;
        }
        return [(plugin, ExpeditionTables.PluginState(rng.Next(ExpeditionTables.FactionIds.Length)))];
    }

    /// <summary>Game_LEAGUE_CHANCES=Expedition=100: a test's chance, as LeagueChances.Count reads it for the rolled rows.</summary>
    static int? ForcedChance()
    {
        foreach (var entry in (Environment.GetEnvironmentVariable("Game_LEAGUE_CHANCES") ?? "").Split(',', StringSplitOptions.TrimEntries))
            if (entry.Split('=') is [var id, var value] && id.Equals("Expedition", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int c))
                return c;
        return null;
    }

    /// <summary>The faction the instance's ExpeditionPlugin state names, or −1 when the area is played without one.</summary>
    static int DeclaredFaction(Player player) =>
        ExpeditionTables.PluginId is { } plugin && ServerLevels.PluginStatesOf(player.Layout.Id, player.Seed) is { } states
            && states.TryGetValue(plugin, out var state) ? ExpeditionTables.FactionOf(state) : -1;

    /// <summary>The detonator the area's own layout holds: the <see cref="DetonatorType"/> line of the encounter room the
    /// generation placed, with a negative angle taken as the same turn above zero (the official create's f32 is 3π/2 for
    /// the line's −π/2). Null when the layout has no such room.</summary>
    public static ((int X, int Y) Cell, float Rotation)? RoomDetonator(AreaLayout layout)
    {
        foreach (var o in layout.Level?.RoomObjects ?? [])
            if (o.Type == DetonatorType)
                return ((o.X, o.Y), o.Rotation < 0 ? o.Rotation + MathF.Tau : o.Rotation);
        return null;
    }

    /// <summary>Whether this instance's area gets the encounter: it declares ExpeditionPlugin and the layout holds its room.
    /// Decided once per instance. The first to ask also names the owner: the map's, or the player whose entry came first.</summary>
    static bool Placed(Player player, State state)
    {
        if (state.Checked) return state.Placed;
        lock (state)
        {
            if (state.Checked) return state.Placed;
            state.Placed = DeclaredFaction(player) >= 0 && RoomDetonator(player.Layout) is not null;
            state.Owner = MapRunOf(player)?.Owner is { Length: > 0 } owner ? owner : CharacterName(player);
            state.Checked = true;
            return state.Placed;
        }
    }

    /// <summary>The client's own IsMapOwner (QuestFlags row 2020) for the owner of an encounter's instance: the client sends
    /// no placement without it. It is the area's grant (GameEvents.AreaQuestFlags), not the character's saved flag, so the
    /// Deepwater clean-up of that flag at every other entry does not touch it. The two official map and campaign captures
    /// show the flag in the owner's flag set in every area.</summary>
    public IReadOnlyList<string> QuestFlags(Player player) =>
        StateOf(player) is { } state && Placed(player, state) && IsOwner(player, state) ? OwnerFlags : NoFlags;

    // Kept arrays: the answer is read for every quest flag the combat code asks for (GameEvents.HasAreaQuestFlag).
    static readonly string[] OwnerFlags = [MapOwnerFlag], NoFlags = [];

    static bool IsOwner(Player player, State state) =>
        string.Equals(state.Owner, CharacterName(player), StringComparison.OrdinalIgnoreCase);

    /// <summary>S→C 0x1f ExpeditionRules where the encounter is placed, before the area's objects. The ruleset needs the
    /// plugin: without ExpeditionPlugin the client stops with "ExpeditionPlugin not found!".</summary>
    public IEnumerable<(string Log, byte[] Message)> RulesetMessages(Player player) =>
        StateOf(player) is { } state && Placed(player, state) && ExpeditionTables.RulesetRow is { } row
            ? [($"0x001f ruleset ExpeditionRules (row {row})", InstanceProtocol.AddRuleset(row))] : [];

    static byte AreaLevel(Player player) =>
        (byte)Math.Clamp(MapRunOf(player) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);

    static Random Rng(Player player, int purpose) =>
        new(AreaPopulation.PopulationSeed(player.Layout.Hash, player.Seed) ^ Salt ^ purpose);

    static byte[] Rules(byte[] payload) => InstanceRulesets.Data(ExpeditionTables.RulesetRow!.Value, payload);

    static double Distance((int X, int Y) a, (int X, int Y) b) =>
        Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static IEnumerable<Player> Clients(Player player) =>
        InGame.Keys.Where(c => c.HasClient && ReferenceEquals(c.Instance, player.Instance) && Current(c));

    /// <summary>One message of the ruleset to every client of the instance.</summary>
    static async Task Broadcast(Player player, byte[] payload, string log)
    {
        foreach (var peer in Clients(player)) await Send(peer, Rules(payload), log);
    }

    // ---- Setup and replay ----------------------------------------------------------------------------------------------

    /// <summary>The area was generated (its first player's entry): the encounter is made once for the instance, and set in
    /// this area when it is a new one.</summary>
    public async Task OnAreaGenerated(Action<string> log, Player player)
    {
        if (StateOf(player) is not { } state || !Placed(player, state)) return;
        await state.Gate.WaitAsync();
        try { await Ensure(log, player, state); }
        finally { Persist(player, state); state.Gate.Release(); }
    }

    /// <summary>A player entered: the monster types the markers can raise, and the ruleset's messages as the encounter stands.
    /// The objects reach the player through the area's object records.</summary>
    public async Task OnPlayerEntered(Action<string> log, Player player)
    {
        if (StateOf(player) is not { } state || !Placed(player, state)) return;
        await state.Gate.WaitAsync();
        try
        {
            // A module loaded into a running area has had no AreaGenerated.
            await Ensure(log, player, state);
            if (state.Plan is not { } plan) return;
            var types = new List<(ushort Row, byte Level)>();
            byte level = AreaLevel(player);
            foreach (int row in ExpeditionTables.VarietiesOf(plan.FactionId))
                if (Monsters.Find(row.ToString()) is { Supported: true } variety) types.Add(((ushort)variety.Row, level));
            if (types.Count > 0) await Send(player, InstanceEntities.MonsterTypes(types), "0x0020 Expedition monsters");
            await SpawnLeader(log, player, state, plan, level);
            RegisterLeader(player, state);
            foreach (var (what, message) in Replay(state, plan)) await Send(player, message, what);
        }
        finally { Persist(player, state); state.Gate.Release(); }
    }

    /// <summary>A player is about to leave the area or the game: the leader stops being theirs to talk to at once,
    /// and when nobody stays in the instance its bodies go too, so a later entry spawns one leader only. A party
    /// member who stays keeps the leader standing.</summary>
    public async Task OnPlayerLeaving(Action<string> log, Player player, string why)
    {
        if (StateOf(player) is not { } state || !Placed(player, state)) return;
        await state.Gate.WaitAsync();
        try
        {
            BindLeader(player, state);
            if (state.LeaderId == 0) return;
            uint leader = state.LeaderId;
            // This player's talk entry goes whatever the others do: the client leaving has no use for it.
            lock (player.Npcs) player.Npcs.Remove(leader);
            if (player.TalkingTo == leader) { player.TalkingTo = 0; player.InNpcTalk = false; }
            // Anyone still in the instance keeps the leader: only the last one out removes its bodies.
            if (Clients(player).Any(c => !ReferenceEquals(c, player))) return;
            if (player.Area.Monsters.Remove(leader, out var body)) { body.Dead = true; body.Life = 0; body.ActionGeneration++; }
            player.Instance.World.MonsterOrigins.TryRemove(leader, out _);
            await SendWorldRemoval(player, leader, $"Expedition leader 0x{leader:x} removed: {why}");
            state.LeaderId = 0;
            state.LeaderNpc = null;
            log($"{player.Layout.Id}: expedition leader 0x{leader:x} goes: {why}");
        }
        finally
        {
            Persist(player, state);
            if (!Clients(player).Any(c => !ReferenceEquals(c, player))) _states.Remove(player.Instance);
            state.Gate.Release();
        }
    }

    async Task Ensure(Action<string> log, Player player, State state)
    {
        if (!state.Made && !state.Failed)
        {
            var plan = MakePlan(player, AreaLevel(player));
            if (plan is null)
            {
                log($"{player.Layout.Id}: no Expedition site in this layout");
                state.Failed = true;
                return;
            }
            state.Plan = plan;
            state.Left = plan.Explosives;
            state.DetonatorId = player.TakeObjectId();
            state.Made = true;
        }
        // A new area of the instance (every one of its clients left and it was generated again) has none of the objects.
        if (state.Made && !ReferenceEquals(state.Area, player.Area))
        {
            state.Area = player.Area;
            state.LeaderId = 0;
            state.LeaderNpc = null;
            foreach (var piece in state.Pieces.Values.ToList())
                if (!player.Area.Objects.Has(piece.Id)) await Show(player, piece);
        }
        BindBodies(player, state);
    }

    // ---- The faction leader ----------------------------------------------------------------------------------------------

    /// <summary>The leader's cell from the detonator's, and the way it faces: the same in every measured encounter.</summary>
    const int LeaderCellDx = 17, LeaderCellDy = 3;
    const float LeaderFacing = 5.2360f;

    /// <summary>The leader's type for a measured faction, else null: an unmeasured faction spawns no leader.</summary>
    static string? LeaderType(string factionId) => factionId switch
    {
        "Faction2" => "Metadata/Monsters/LeagueExpedition/NPC/ExpeditionTujen",
        "Faction4" => "Metadata/Monsters/LeagueExpedition/NPC/ExpeditionDannig",
        _ => null,
    };

    /// <summary>The talk entry for a leader spawned as a monster: the talk-owning NPC row of the faction
    /// (Haggler for Faction2, Saga for Faction4), so TalkSource resolves its dialogue options. Only when the
    /// catalog has no such row does the entry fall back to the synthetic one, whose Row -1 resolves nothing;
    /// the interaction size comes from the monster body, not the entry.</summary>
    static NpcCatalog.Npc LeaderNpc(string factionId, MonsterCatalog.Variety variety)
    {
        if (LeaderTalkId(factionId) is { } talkId && Npcs.Find(talkId) is { } npc && npc.Id == talkId) return npc;
        return new(-1, variety.Id, variety.Name, null, null, 1);
    }

    /// <summary>The talk-owning NPC row for a measured faction, else null: the row the leader's own NPCs row
    /// points at with __col10 (ExpeditionTujen 517 → Haggler 513, ExpeditionDannig 515 → Saga 511).</summary>
    static string? LeaderTalkId(string factionId) => factionId switch
    {
        "Faction2" => "Metadata/NPC/League/Expedition/Haggler",
        "Faction4" => "Metadata/NPC/League/Expedition/Saga",
        _ => null,
    };

    /// <summary>Spawns the faction's leader once per area, at the detonator's cell + (17, 3) and facing
    /// 5.2360 rad. The leader is a friendly, scripted monster with the talk flag, so it stands where it is put and can be
    /// talked to; its id is registered for every player who enters (see <see cref="RegisterLeader"/>). An unmeasured faction,
    /// or a leader type the catalog does not know, spawns nothing.</summary>
    static bool BindLeader(Player player, State state)
    {
        if (state.Plan is not { } plan || LeaderType(plan.FactionId) is not { } type) return false;
        var found = player.Area.Monsters.FirstOrDefault(p => !p.Value.Dead && p.Value.Life > 0
            && !p.Value.Hostile && p.Value.Variety?.Id == type);
        if (found.Value?.Variety is not { } variety) return false;
        state.LeaderId = found.Key;
        state.LeaderCell = found.Value.Walk.Position(AreaTime(player));
        state.LeaderNpc = LeaderNpc(plan.FactionId, variety);
        state.LeaderBodySize = Math.Clamp(variety.ObjectSize - 1, 0, 127);
        return true;
    }

    async Task SpawnLeader(Action<string> log, Player player, State state, Plan plan, byte level)
    {
        if (BindLeader(player, state) || state.LeaderId != 0 || LeaderType(plan.FactionId) is not { } type) return;
        if (Monsters.Find(type) is not { Supported: true } variety)
        {
            log($"{player.Layout.Id}: expedition leader {type}: unknown or unsupported variety");
            return;
        }
        var at = Floor(player.Layout, (plan.Detonator.X + LeaderCellDx, plan.Detonator.Y + LeaderCellDy));
        uint id = await SpawnMonster(player, variety, level, MonsterRarity.Normal, at.X, at.Y,
            friendly: true, unrisen: false, rotation: LeaderFacing, scripted: true, instanceOwned: true,
            shape: m => m with { HasDialogue = true });
        if (id == 0) return;
        if (player.Area.Monsters.TryGetValue(id, out var body)) at = body.Walk.Position(AreaTime(player));
        state.LeaderId = id;
        state.LeaderCell = at;
        state.LeaderNpc = LeaderNpc(plan.FactionId, variety);
        state.LeaderBodySize = Math.Clamp(variety.ObjectSize - 1, 0, 127);
    }

    /// <summary>Registers the leader's monster body in this player's talk dictionary, so a click on it opens the dialogue.
    /// One entry per player: the body is the instance's, the dictionary is the player's.</summary>
    static void RegisterLeader(Player player, State state)
    {
        if (state.LeaderId == 0 || state.LeaderNpc is null) return;
        lock (player.Npcs)
        {
            if (player.Npcs.ContainsKey(state.LeaderId)) return;
            player.Npcs[state.LeaderId] = new SpawnedNpc(state.LeaderNpc, state.LeaderCell.X, state.LeaderCell.Y)
            {
                MonsterBodySize = state.LeaderBodySize,
            };
        }
    }

    /// <summary>The ruleset's messages for the encounter as it stands, in the captures' entry order: 00 state, 04, 03, 08, and
    /// what the explosives placed add.</summary>
    static IEnumerable<(string Log, byte[] Message)> Replay(State state, Plan plan)
    {
        var end = state.Explosives.Count == 0 ? plan.Detonator : state.Explosives[^1];
        yield return ("0x2c9 Expedition 00 state", Rules(state.Stage switch
        {
            Stage.Hidden => ExpeditionWire.Detonator(0, plan.Detonator),
            Stage.Placing => ExpeditionWire.Active(plan.Detonator, end, (ushort)state.Left),
            _ => ExpeditionWire.Detonator(2, plan.Detonator),
        }));
        yield return ($"0x2c9 Expedition 04 dirt {plan.Dirt.Count}", Rules(ExpeditionWire.Dirt(plan.Dirt)));
        var covered = Covered(plan, state.Explosives);
        yield return ("0x2c9 Expedition 03", Rules(ExpeditionWire.Summary(
            ExpeditionTables.SumStats(covered.SelectMany(r => r.Mods)), covered.Count, plan.Remnants.Count)));
        yield return ("0x2c9 Expedition 08 box", Rules(ExpeditionWire.MarkersBox(plan.Box)));
        if (state.Explosives.Count > 0)
            yield return ("0x2c9 Expedition 01", Rules(ExpeditionWire.Placed(end, state.Explosives)));
        if (state.Stage >= Stage.Chain && state.Explosives.Count > 0)
        {
            var xs = state.Explosives.Select(e => e.X).ToList();
            var ys = state.Explosives.Select(e => e.Y).ToList();
            yield return ("0x2c9 Expedition 06", Rules(ExpeditionWire.ExplosivesBox((xs.Min(), ys.Min(), xs.Max() + 1, ys.Max() + 1))));
        }
    }

    static List<Remnant> Covered(Plan plan, IReadOnlyList<(int X, int Y)> explosives) =>
        [.. plan.Remnants.Where(r => explosives.Any(e => Distance(r.Cell, e) < plan.Radius))];

    // ---- The area's objects ----------------------------------------------------------------------------------------------

    /// <summary>Creates a piece in the area: the create is written with the object's create in the frame, so every client of
    /// the area has it and a later one is given it as the area's record.</summary>
    static async Task<bool> Show(Player player, Piece piece)
    {
        if (piece.Blob() is null)
        {
            Console.WriteLine($"[gateway] Expedition: no create for {piece.Type} ({piece.Note}): the type has no object_types row, "
                + "or it reads a component with no measured body");
            return false;
        }
        return await SendWorldCreate(player, piece.Id,
            () => piece.Blob() is { } blob ? InstanceEntities.CreateObject(piece.Id, InstanceEntities.ObjectTypeHash(piece.Type), blob) : null,
            $"Expedition {piece.Note}", awaitBirth: false);
    }

    /// <summary>A piece the encounter keeps (created again for a new area of the instance).</summary>
    static async Task<Piece?> Keep(Player player, State state, uint id, string type, string note, Func<byte[]?> blob)
    {
        var piece = new Piece(id, type, note, blob);
        if (!await Show(player, piece)) return null;
        state.Pieces[id] = piece;
        return piece;
    }

    /// <summary>A transient effect. It is the area's object while it lasts, so the encounter removes it as the official server
    /// does (<see cref="PlaceFrames"/> and the others, from the Advance clock); one that is not removed would be replayed to
    /// everyone who enters later. It is neither kept nor created again for a new area.</summary>
    static async Task Effect(Player player, State state, string ao, (int X, int Y) cell, string note, bool baseData, int frames)
    {
        uint id = player.TakeObjectId();
        if (await Show(player, new Piece(id, EffectType, note, () => ExpeditionWire.Object(EffectType, cell, 0, ao: ao,
                baseData: baseData ? ExpeditionTables.EffectBaseData : null))))
            Schedule(state, state.Clock + frames / ExpeditionTables.FramesPerSecond, "gone", (int)id);
    }

    /// <summary>The frames the official server keeps each effect before it removes it [capture: 5 placed explosives 30, 8
    /// blasts 46, 3 shockwaves 61].</summary>
    const int PlaceFrames = 30, BlastFrames = 46, ShockwaveFrames = 61;

    static void Schedule(State state, double at, string action, int index)
    {
        state.Queue.Add((at, action, index));
        state.Queue.Sort((a, b) => a.At.CompareTo(b.At));
    }

    static async Task Drop(Player player, State state, uint id)
    {
        state.Pieces.Remove(id);
        await SendWorldRemoval(player, id, $"Expedition object 0x{id:x} removed");
    }

    static Task Update(Player player, uint id, byte[]? bytes, string log) =>
        bytes is null ? Task.CompletedTask : SendWorldRecord(player, id, bytes, log);

    /// <summary>Reveals the detonator, the markers and the remnants.</summary>
    async Task Reveal(Player player, State state)
    {
        if (state.Plan is not { } plan || state.Revealed) return;
        state.Revealed = true;
        // The detonator's official creates: Animated 0x40, and InteractionAction 0x01 while it can be used.
        await Keep(player, state, state.DetonatorId, DetonatorType, "detonator", () => ExpeditionWire.Object(DetonatorType, plan.Detonator,
            plan.Rotation, animated: 0x40, interaction: (byte)(state.Stage < Stage.Chain ? 0x01 : 0x00), stateValues: DetonatorStates(state)));
        for (int i = 0; i < plan.Markers.Count; i++)
        {
            var marker = plan.Markers[i] with { Id = player.TakeObjectId() };
            plan.Markers[i] = marker;
            await Keep(player, state, marker.Id, MarkerType, "marker", () => ExpeditionWire.Object(MarkerType, marker.Cell, marker.Rotation,
                ao: marker.Ao, scale: marker.Kind == ExpeditionTables.MarkerKind.Elite ? ExpeditionTables.EliteMarkerScale : 0));
        }
        for (int i = 0; i < plan.Remnants.Count; i++)
        {
            var remnant = plan.Remnants[i] with { Id = player.TakeObjectId() };
            plan.Remnants[i] = remnant;
            int index = i;
            // Its modifiers as explicit mods with one value per stat [capture: all 13 remnant creates]; a destroyed one is
            // created as it stands (its StateMachine at 0, 1, 1), for an area made again.
            await Keep(player, state, remnant.Id, RelicType, "remnant", () => ExpeditionWire.Object(RelicType, remnant.Cell, remnant.Rotation,
                ao: ExpeditionTables.RelicAo, mods: [.. remnant.Mods.Select(m => new ItemMod(ModSection.Explicit, m.Hash, m.Values))],
                stateValues: state.Broken.Contains(index) ? BrokenRemnant : null));
        }
    }

    static readonly int[] BrokenRemnant = [0, 1, 1];

    /// <summary>The detonator's StateMachine values for the stage [capture: 0, 1, 0 while explosives are placed, 0, 2, 1 once
    /// it is set off and 1, 2, 1 when the chain is done]; null for the type's own.</summary>
    static IReadOnlyList<int>? DetonatorStates(State state) => state.Stage switch
    {
        Stage.Placing => [0, 1, 0],
        Stage.Chain or Stage.Fight => state.Blasts >= state.Explosives.Count && state.Explosives.Count > 0 ? [1, 2, 1] : [0, 2, 1],
        Stage.Over => [1, 2, 1],
        _ => null,
    };

    static Task UpdateDetonator(Player player, State state) => Update(player, state.DetonatorId,
        DetonatorStates(state) is { } values ? ExpeditionWire.StateUpdate(state.DetonatorId, DetonatorType, values, false) : null,
        "Expedition detonator state");

    // ---- The encounter's clock -------------------------------------------------------------------------------------------

    /// <summary>Whether this player runs the instance's encounter: the live client with the lowest id, as the other league
    /// encounters do, so a party has one clock.</summary>
    static bool Drives(Player player) =>
        Current(player) && !player.Paused && player.PlayerLife > 0
        && ReferenceEquals(Clients(player).Where(c => c.PlayerLife > 0).MinBy(c => c.Id), player);

    /// <summary>The AreaTick work of an area that holds the encounter; null elsewhere.</summary>
    public Func<Player, double, Task>? TickFor(Player player) =>
        StateOf(player) is { } state && Placed(player, state) ? _advance : null;

    async Task Advance(Player player, double seconds)
    {
        if (!_states.TryGetValue(player.Instance, out var state) || state.Plan is not { } plan || state.Stage == Stage.Over) return;
        if (!Drives(player)) return;
        await state.Gate.WaitAsync();
        var before = (state.Stage, state.Queue.Count, state.Blasts);
        try
        {
            if (state.Stage == Stage.Over) return;
            BindBodies(player, state);
            state.Clock += seconds;
            var near = Clients(player).Select(c => PlayerOf(c).Position).ToList();
            if (!state.Revealed && near.Any(p => Distance(p, plan.Detonator) <= ExpeditionTables.RevealDist)) await Reveal(player, state);
            if (state.Revealed && state.Stage == Stage.Hidden && near.Any(p => Distance(p, plan.Detonator) <= ExpeditionTables.StartDist))
            {
                state.Stage = Stage.Placing;
                await Broadcast(player, ExpeditionWire.Active(plan.Detonator, plan.Detonator, (ushort)state.Left), "0x2c9 Expedition 00 state 1");
                await UpdateDetonator(player, state);
            }
            while (state.Queue.Count > 0 && state.Queue[0].At <= state.Clock)
            {
                var (_, action, index) = state.Queue[0];
                state.Queue.RemoveAt(0);
                if (action == "blast") await Blast(player, state, index);
                else if (action == "chest") await ReadyChest(player, state, (uint)index);
                else if (action == "gone") await SendWorldRemoval(player, (uint)index, $"Expedition effect 0x{index:x} removed");
            }
            // The dictionary is walked as it is (removing while walking is safe for it): no copy per frame, and nothing at all
            // while no dug monster is alive.
            if (!state.Monsters.IsEmpty)
                foreach (var (id, body) in state.Monsters)
                    if (body.Dead || body.Life == 0) state.Monsters.TryRemove(id, out _);
            // Every blast went off, none of the dug monsters lives and nothing is still queued (a chest that is not ready yet,
            // an effect not removed yet): Over stops the clock.
            if (state.Stage == Stage.Fight && state.Blasts >= state.Explosives.Count && state.Monsters.IsEmpty && state.PendingBodies is null && state.Queue.Count == 0)
            {
                state.Stage = Stage.Over;
                await UpdateDetonator(player, state);
            }
        }
        finally
        {
            if (before != (state.Stage, state.Queue.Count, state.Blasts)) Persist(player, state);
            state.Gate.Release();
        }
    }

    // The client data exposes the Infusion switch, not its scaling curve. These values are server calibration.
    public static IReadOnlyList<(uint Stat, int Value)> Enhancement(int remnants) =>
        remnants <= 0 ? [] : [(Stat.MaximumLifePlusPct, remnants * 20), (Stat.DamagePlusPct, remnants * 10),
            (Stat.BaseItemFoundQuantityPlusPct, remnants * 20), (Stat.BaseItemFoundRarityPlusPct, remnants * 20)];

    public void Drops(DropRoll roll)
    {
        if (StateOf(roll.Player) is not { } state) return;
        BindBodies(roll.Player, state);
        if (!state.Rewards.TryGetValue(roll.Monster.ObjectId, out var reward)
            || !reward.Runic) return;
        var rng = AreaRng(roll.Player);
        // Baseline runic logbook chance is a server calibration; remnant quantity and Infusion scale the same roll.
        int quantity = roll.Player.Area.Monsters.GetValueOrDefault(roll.Monster.ObjectId)?.Effects[Stat.ExpeditionMonstersLogbookChancePlusPct] ?? 0;
        double chance = 0.02 * Math.Max(0, 1 + quantity / 100d) * Math.Max(0, 1 + reward.Remnants * 0.2);
        if (rng.NextDouble() >= chance) return;
        if (ExpeditionLogbooks.Create(roll.Monster.Level, rng, false) is { } book) roll.Made.Add(book);
    }

    /// <summary>GameEvents.Kill: a dug monster that died, by anyone's hand (a minion's too), is out of the count at once.</summary>
    public void OnKill(KillContext kill)
    {
        if (kill.Player.Instance is null || !_states.TryGetValue(kill.Player.Instance, out var state)) return;
        BindBodies(kill.Player, state);
        foreach (var (id, body) in state.Monsters)
            if (ReferenceEquals(body, kill.Monster)) state.Monsters.TryRemove(id, out _);
    }

    // ---- The owner's actions ---------------------------------------------------------------------------------------------

    /// <summary>C→S 0x2ca for ExpeditionRules: type 00 places an explosive. Only the map owner places them (the client
    /// sends placements only with the IsMapOwner quest flag, which <see cref="QuestFlags"/> gives it). True when the action was
    /// this ruleset's.</summary>
    public async Task<bool> RulesetAction(Player player, byte[] message)
    {
        if (ExpeditionTables.RulesetRow is not { } rules) return false;
        byte row;
        byte[] payload;
        try { (row, payload) = InstanceRulesets.ReadAction(message); }
        catch (InvalidDataException) { return false; }
        if (row != rules) return false;
        if (player.Instance is null || !_states.TryGetValue(player.Instance, out var state) || state.Plan is null
            || ExpeditionWire.ReadPlacement(payload) is not { } cell) return true;
        if (!Current(player) || player.PlayerLife == 0 || !ClientHasQuestFlag(player, MapOwnerFlag) || !IsOwner(player, state)) return true;
        await state.Gate.WaitAsync();
        try { await Place(player, state, cell); }
        finally { Persist(player, state); state.Gate.Release(); }
        return true;
    }

    static bool InMap(AreaLayout layout, (int X, int Y) cell) => cell.X >= 0 && cell.Y >= 0 && cell.X < layout.GridW && cell.Y < layout.GridH;

    /// <summary>A cell inside the map and on floor: the cell itself, else the nearest floor.</summary>
    static (int X, int Y) Floor(AreaLayout layout, (int X, int Y) cell)
    {
        cell = (Math.Clamp(cell.X, 0, layout.GridW - 1), Math.Clamp(cell.Y, 0, layout.GridH - 1));
        return layout.WalkableFor(cell.X, cell.Y, 0) ? cell : layout.Near(cell);
    }

    /// <summary>An explosive: clamped to 90 cells from the chain's end, a pole every 17.5 cells of the chain as placed and
    /// a fuse per gap, then 0x74 01 and 03 [capture: 15 placements]. The client's cell is checked first: inside the map and on
    /// floor, or the placement is dropped.</summary>
    async Task Place(Player player, State state, (int X, int Y) want)
    {
        if (state.Plan is not { } plan || state.Stage != Stage.Placing || state.Left <= 0) return;
        var layout = player.Layout;
        if (!InMap(layout, want) || !layout.WalkableFor(want.X, want.Y, 0)) return;
        var end = state.Explosives.Count == 0 ? plan.Detonator : state.Explosives[^1];
        double d = Distance(end, want);
        (int X, int Y) cell = d <= ExpeditionTables.PlaceRange ? want
            : Floor(layout, ((int)Math.Round(end.X + (want.X - end.X) * ExpeditionTables.PlaceRange / d),
                             (int)Math.Round(end.Y + (want.Y - end.Y) * ExpeditionTables.PlaceRange / d)));
        state.Explosives.Add(cell);
        state.Left--;
        await Broadcast(player, ExpeditionWire.Placed(cell, state.Explosives), "0x2c9 Expedition 01");
        var covered = Covered(plan, state.Explosives);
        await Broadcast(player, ExpeditionWire.Summary(ExpeditionTables.SumStats(covered.SelectMany(r => r.Mods)), covered.Count, plan.Remnants.Count),
            "0x2c9 Expedition 03");
        await Effect(player, state, ExpeditionTables.PlaceAo, cell, "place effect", baseData: false, PlaceFrames);
        uint eid = player.TakeObjectId();
        state.ExplosiveIds.Add(eid);
        await Keep(player, state, eid, ExplosiveType, "explosive", () => ExpeditionWire.Object(ExplosiveType, cell, 0));
        // The chain from the previous end to this explosive: ceil(length / 17.5) gaps [capture: chains of 38.3 / 72.0 /
        // 46.6 / 43.1 / 89.8 cells carry 2 / 4 / 2 / 2 / 5 poles], one fuse per gap.
        int gaps = Math.Max(1, (int)Math.Ceiling(Distance(end, cell) / ExpeditionTables.PoleGap - 1e-9));
        var links = new List<uint>();
        var chain = new List<(ExpeditionWire.BeamEnd End, uint Kind)>
        {
            (new ExpeditionWire.BeamEnd(state.Explosives.Count == 1 ? state.DetonatorId : state.ExplosiveIds[^2], end),
             state.Explosives.Count == 1 ? ExpeditionWire.BeamDetonator : ExpeditionWire.BeamExplosive),
        };
        for (int k = 1; k < gaps; k++)
        {
            var pole = Floor(layout, ((int)Math.Round(end.X + (cell.X - end.X) * (double)k / gaps),
                                      (int)Math.Round(end.Y + (cell.Y - end.Y) * (double)k / gaps)));
            uint pid = player.TakeObjectId();
            links.Add(pid);
            await Keep(player, state, pid, PoleType, "pole", () => ExpeditionWire.Object(PoleType, pole, 0));
            chain.Add((new ExpeditionWire.BeamEnd(pid, pole), ExpeditionWire.BeamPole));
        }
        chain.Add((new ExpeditionWire.BeamEnd(eid, cell), ExpeditionWire.BeamExplosive));
        for (int k = 0; k + 1 < chain.Count; k++)
        {
            uint fid = player.TakeObjectId();
            links.Add(fid);
            var (from, fromKind) = chain[k];
            var (to, toKind) = chain[k + 1];
            await Keep(player, state, fid, FuseType, "fuse", () => ExpeditionWire.Object(FuseType, from.Cell, 0,
                beam: new ExpeditionWire.Beam(from, fromKind, to, toKind)));
        }
        state.Chains.Add(links);
    }

    /// <summary>A click on the detonator with an explosive placed: 0x74 00 state 2 and 06, then the chain is scheduled
    /// (the first fuse 13 frames on, 6-frame steps, the first blast 4 frames after its explosive lights, the others 91).</summary>
    async Task Detonate(Player player, State state)
    {
        if (state.Plan is not { } plan || state.Stage != Stage.Placing || state.Explosives.Count < 1) return;
        state.Stage = Stage.Chain;
        await Broadcast(player, ExpeditionWire.Detonator(2, plan.Detonator), "0x2c9 Expedition 00 state 2");
        var xs = state.Explosives.Select(e => e.X).ToList();
        var ys = state.Explosives.Select(e => e.Y).ToList();
        await Broadcast(player, ExpeditionWire.ExplosivesBox((xs.Min(), ys.Min(), xs.Max() + 1, ys.Max() + 1)), "0x2c9 Expedition 06");
        // The detonator is spent: its state, and no more clicks [capture: InteractionAction 00].
        await UpdateDetonator(player, state);
        await Update(player, state.DetonatorId, ExpeditionWire.ComponentUpdate(state.DetonatorId, DetonatorType, "InteractionAction", 0x00),
            "Expedition detonator interaction off");
        double t = state.Clock + ExpeditionTables.FirstFuseFrames / ExpeditionTables.FramesPerSecond;
        for (int k = 0; k < state.Explosives.Count; k++)
        {
            t += ExpeditionTables.StepFrames / ExpeditionTables.FramesPerSecond;
            t += (k == 0 ? ExpeditionTables.FirstBlastFrames : ExpeditionTables.BlastFrames) / ExpeditionTables.FramesPerSecond;
            state.Queue.Add((t, "blast", k));
        }
        state.Queue.Sort((a, b) => a.At.CompareTo(b.At));
        state.Stage = Stage.Fight;
    }

    static int StatOf(IEnumerable<(uint Stat, int Value)> stats, uint stat) => stats.FirstOrDefault(s => s.Stat == stat).Value;

    /// <summary>A blast: 0x74 07 and its effect, a shockwave per remnant in range, and every marker in range dug: a chest
    /// marker becomes its chest, a monster marker raises the pack its faction's tables give 2 to 10 cells around it. The
    /// explosive and its chain go with the blast.</summary>
    async Task Blast(Player player, State state, int index)
    {
        if (state.Plan is not { } plan || index >= state.Explosives.Count) return;
        var cell = state.Explosives[index];
        state.Blasts++;
        await Broadcast(player, ExpeditionWire.Blast(cell, plan.Radius), "0x2c9 Expedition 07 blast");
        await Effect(player, state, ExpeditionTables.BlastAo, cell, "blast", baseData: true, BlastFrames);
        var rng = Rng(player, 0x100 + index);
        // The remnants this blast destroys join the ones whose modifiers apply from here on, to what this blast digs up
        // too ("the explosive that destroys the Remnant and all subsequent Explosives").
        for (int i = 0; i < plan.Remnants.Count; i++)
            if (Distance(plan.Remnants[i].Cell, cell) < plan.Radius && state.Broken.Add(i))
            {
                var remnant = plan.Remnants[i];
                await Effect(player, state, ExpeditionTables.ShockwaveAo, remnant.Cell, "shockwave", baseData: true, ShockwaveFrames);
                // [capture] a destroyed remnant's StateMachine goes to 0, 1, 1.
                await Update(player, remnant.Id, ExpeditionWire.StateUpdate(remnant.Id, RelicType, BrokenRemnant, false), "Expedition remnant destroyed");
            }
        var broken = state.Broken.Select(i => plan.Remnants[i].Mods).SelectMany(m => m).ToList();
        var active = ExpeditionTables.SumStats(broken);
        byte level = AreaLevel(player);
        // The removals go out together: each one waits for its frame, and one after the other held the encounter's gate (and
        // its clock) for a frame per object.
        var gone = new List<Task>();
        for (int i = 0; i < plan.Markers.Count; i++)
        {
            var marker = plan.Markers[i];
            if (state.Dug.Contains(i) || Distance(marker.Cell, cell) >= plan.Radius) continue;
            state.Dug.Add(i);
            if (marker.Id != 0) gone.Add(Drop(player, state, marker.Id));
            if (marker.Kind == ExpeditionTables.MarkerKind.Chest)
            {
                if (marker.ChestType.Length == 0) continue;
                uint chestId = player.TakeObjectId();
                var chest = new Chest { Tier = marker.Tier, Cell = marker.Cell, Type = marker.ChestType, Mods = active };
                // The official dug chest: the area level in its Chest component, its one shared state (encounter) at 1; it
                // goes on as the chest becomes ready (2) and is opened (3).
                if (await Keep(player, state, chestId, marker.ChestType, "chest", () => ExpeditionWire.Object(chest.Type, chest.Cell, 0, level,
                    interaction: (byte)(chest.Ready && !chest.Opened ? 0x01 : 0x00), stateValues: [chest.Opened ? 3 : chest.Ready ? 2 : 1])) is null) continue;
                state.Chests[chestId] = chest;
                Schedule(state, state.Clock + ExpeditionTables.ChestReadyFrames / ExpeditionTables.FramesPerSecond, "chest", (int)chestId);
                continue;
            }
            await Raise(player, state, marker, broken, rng, level);
        }
        // The explosive, its poles and its fuses are spent [capture: the fuse burns (state 1) and goes with the explosive].
        await Update(player, state.ExplosiveIds[index], ExpeditionWire.StateUpdate(state.ExplosiveIds[index], ExplosiveType, [1, 2], false),
            "Expedition explosive state");
        foreach (uint id in state.Chains[index].Append(state.ExplosiveIds[index])) gone.Add(Drop(player, state, id));
        await Task.WhenAll(gone);
        if (state.Blasts >= state.Explosives.Count) await UpdateDetonator(player, state);
    }

    /// <summary>A dug monster marker's monsters: the pack's members by their weights, the destroyed remnants' modifiers
    /// in the spawn (<c>extraMods</c>, so the server's life, damage and defences read them), twinned elites twice.</summary>
    static async Task Raise(Player player, State state, Marker marker, IReadOnlyList<ExpeditionTables.RelicMod> broken, Random rng, byte level)
    {
        bool elite = marker.Kind == ExpeditionTables.MarkerKind.Elite;
        var packs = elite ? ExpeditionTables.ElitePacks(state.Plan!.FactionId) : ExpeditionTables.RegularPacks;
        if (packs.Count == 0) return;
        var pack = packs[rng.Next(packs.Count)];
        var sums = ExpeditionTables.SumStats(broken);
        int count = rng.Next(pack.MinCount, pack.MaxCount + 1);
        if (!elite) count = Math.Max(1, (int)Math.Round(count * (100.0 + StatOf(sums, Stat.MapPackSizePlusPct)) / 100.0));
        else if (StatOf(sums, Stat.ExpeditionTwinnedElites) > 0) count *= 2;
        // The remnants' stats on the monsters (category 0), and on the elites (category 1) as well.
        var effects = ExpeditionTables.SumStats(broken.Where(m => m.Category == 0 || elite && m.Category == 1))
            .ToDictionary(s => s.Stat, s => s.Value);
        int enhanced = MapModsOf(player)?.Get(Stat.MapExpeditionRemenantsEnhanceMonsters) > 0 ? state.Broken.Count : 0;
        foreach (var (stat, value) in Enhancement(enhanced)) effects[stat] = effects.GetValueOrDefault(stat) + value;
        var extraMods = effects.Count == 0 ? null : new MonsterModSet([], [], new MonsterModEffects(effects));
        double magic = ExpeditionTables.MagicChance * (100.0 + StatOf(sums, Stat.MapNumberOfMagicPacksPlusPct)) / 100.0;
        double rare = ExpeditionTables.RareChance * (100.0 + StatOf(sums, Stat.MapNumberOfRarePacksPlusPct)) / 100.0;
        int total = pack.Members.Sum(m => m.Weight);
        for (int m = 0; m < count; m++)
        {
            int roll = rng.Next(total), pick = pack.Members[0].Variety;
            foreach (var (variety, weight) in pack.Members)
                if ((roll -= weight) < 0) { pick = variety; break; }
            if (Monsters.Find(pick.ToString()) is not { Supported: true } found) continue;
            var at = SpotNear(player.Layout, marker.Cell, 2, 10, rng);
            double r = rng.NextDouble();
            var rarity = elite ? MonsterRarity.Rare : r < rare ? MonsterRarity.Rare : r < rare + magic ? MonsterRarity.Magic : MonsterRarity.Normal;
            uint id = await SpawnMonster(player, found, level, rarity, at.X, at.Y, unrisen: false, alone: false, extraMods: extraMods,
                owned: (spawned, body) => { body.Aggro = true; state.Monsters[spawned] = body; state.Rewards[spawned] = new(elite, enhanced); });
            if (id == 0) continue;
        }
    }

    async Task ReadyChest(Player player, State state, uint chestId)
    {
        if (!state.Chests.TryGetValue(chestId, out var chest)) return;
        chest.Ready = true;
        // InteractionAction on, then the StateMachine's full update (4 bytes: 01 00 02 02).
        await Update(player, chestId, ExpeditionWire.ComponentUpdate(chestId, chest.Type, "InteractionAction", 0x01), "Expedition chest interaction on");
        await Update(player, chestId, ExpeditionWire.StateUpdate(chestId, chest.Type, [2], true), "Expedition chest ready");
    }

    /// <summary>A click on a ready chest: ObjectMagicProperties 08000000 and the StateMachine at 3 [capture], then its items on
    /// the floor.</summary>
    async Task OpenChest(Player player, State state, uint chestId)
    {
        if (!state.Chests.TryGetValue(chestId, out var chest) || !chest.Ready || chest.Opened) return;
        chest.Opened = true;
        await Update(player, chestId, ExpeditionWire.ComponentUpdate(chestId, chest.Type, "InteractionAction", 0x00), "Expedition chest interaction off");
        await Update(player, chestId, ExpeditionWire.ComponentUpdate(chestId, chest.Type, "ObjectMagicProperties", 0x08, 0x00, 0x00, 0x00),
            "Expedition chest opened");
        await Update(player, chestId, ExpeditionWire.StateUpdate(chestId, chest.Type, [3], true), "Expedition chest state 3");
        await DropReward(player, chest, chestId);
    }

    /// <summary>The chest's items: <see cref="ExpeditionTables.ChestItems"/> at the area level, with the quantity and rarity
    /// the remnants destroyed before it was dug give it (<c>chest_item_quantity_+%</c> and
    /// <c>chest_item_rarity_+%_final_from_mod</c>, the two chest stats the remnant mods carry).</summary>
    static async Task DropReward(Player player, Chest chest, uint chestId)
    {
        var rng = Rng(player, (int)chestId);
        int level = AreaLevel(player);
        var (min, max) = ExpeditionTables.ChestItems(chest.Tier);
        int want = rng.Next(min, max + 1);
        int quantity = StatOf(chest.Mods, Stat.ChestItemQuantityPlusPct), rarity = StatOf(chest.Mods, Stat.ChestItemRarityPlusPctFinalFromMod);
        if (quantity != 0) want = Math.Max(1, (int)Math.Round(want * (100.0 + quantity) / 100.0));
        var items = new List<Item>();
        for (int attempt = 0; attempt < want * 4 && items.Count < want; attempt++)
            items.AddRange(DropsFor(player).Container(level, want - items.Count, rarity, rng).Select(d => d.Item));
        var layout = player.Layout;
        for (int i = 0; i < items.Count; i++)
        {
            double angle = i * 2.399963229728653, radius = 7 + Math.Sqrt(i) * 2;
            var at = Floor(layout, ((int)Math.Round(chest.Cell.X + Math.Cos(angle) * radius), (int)Math.Round(chest.Cell.Y + Math.Sin(angle) * radius)));
            var ground = new WorldItemObject(at.X, at.Y, items[i]);
            uint id = player.Instance.Ground.Add(ground, player);
            await SendGroundItem(player, id, ground, "Expedition reward on the floor", justDropped: true);
        }
    }

    /// <summary>ObjectUse: the detonator starts the chain, a ready chest opens.</summary>
    public async Task<bool> OnUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Instance is null || !_states.TryGetValue(player.Instance, out var state) || state.Plan is null) return false;
        if (use.TargetId == state.DetonatorId && state.Revealed)
            return await Interact(player, state, use, requestedAt, state.Plan.Detonator, 4, () => Detonate(player, state));
        if (!state.Chests.TryGetValue(use.TargetId, out var chest)) return false;
        return await Interact(player, state, use, requestedAt, chest.Cell, 2, () => OpenChest(player, state, use.TargetId));
    }

    /// <summary>A click on an object as the other walked-up objects take it (MapStrongboxes.Use): the walk up, the action's start
    /// and end, nothing for a dead, closing or leaving player, and the work only if the player is still in reach once there.</summary>
    static async Task<bool> Interact(Player player, State state, InstanceTransfer.UseSkillOnObject use, double requestedAt,
        (int X, int Y) at, int reach, Func<Task> act)
    {
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
        if (!TryBeginObjectWalk(player, requestedAt, at.X, at.Y, reach, out var walk)) return true;
        var (action, now, here, _, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, at.X, at.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), null,
                still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        await state.Gate.WaitAsync();
        try
        {
            if (PlayerActionCurrent(player, action) && player.PlayerLife > 0 && !player.Closed && !player.LeavingArea
                && PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), at, reach))
                await act();
        }
        finally
        {
            Persist(player, state);
            state.Gate.Release();
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    // ---- The plan ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The whole encounter, decided once from the instance's own draws: the faction is the one the instance's plugin state
    /// names, the site the encounter room's detonator line, the counts the level's ExpeditionBalancePerLevel row. Null when
    /// the layout holds no such room or nothing fits around it.
    /// </summary>
    public static Plan? MakePlan(Player player, int level)
    {
        var layout = player.Layout;
        int faction = DeclaredFaction(player);
        if (faction < 0 || RoomDetonator(layout) is not { } room) return null;
        var (chests, explosives, remnantCount) = ExpeditionTables.Balance(level);
        var rng = Rng(player, 0);
        var mods = MapModsOf(player);
        int S(uint stat) => mods?.Get(stat) ?? 0;
        explosives = ScaleCount(explosives, S(Stat.MapExpeditionExplosivesPlusPct));
        int radius = Math.Clamp((int)Math.Floor(ExpeditionTables.BlastRadius * Math.Max(0, 100L + S(Stat.MapExpeditionExplosionRadiusPlusPct)) / 100), 1, 255);
        var site = room.Cell;
        string factionId = ExpeditionTables.FactionIds[faction];
        var markers = new List<Marker>();
        var remnants = new List<Remnant>();
        int baseElites = rng.Next(ExpeditionTables.ElitesMin, ExpeditionTables.ElitesMax + 1);
        int elites = ScaleCount(baseElites, S(Stat.MapExpeditionEliteMarkerCountPlusPct));
        int regular = ScaleCount(ExpeditionTables.Markers - chests - baseElites, S(Stat.MapExpeditionNumberOfMonsterMarkersPlusPct));
        for (int i = 0; i < chests + elites + regular; i++)
        {
            var kind = i < chests ? ExpeditionTables.MarkerKind.Chest
                : i < chests + elites ? ExpeditionTables.MarkerKind.Elite
                : ExpeditionTables.MarkerKind.Regular;
            var cell = Scatter(layout, rng, site, kind == ExpeditionTables.MarkerKind.Chest
                ? (ExpeditionTables.ChestDistMin, ExpeditionTables.ChestDistMax)
                : (ExpeditionTables.MarkerDistMin, ExpeditionTables.MarkerDistMax),
                markers.Select(m => m.Cell), ExpeditionTables.MarkerSepMin);
            if (cell is null) continue;
            var tier = kind == ExpeditionTables.MarkerKind.Chest && rng.NextDouble() < ExpeditionTables.ChestMidChance
                ? ExpeditionTables.ChestTier.Mid : ExpeditionTables.ChestTier.Low;
            string chestType = kind == ExpeditionTables.MarkerKind.Chest ? ExpeditionTables.ChestPath(factionId, tier) : "";
            markers.Add(new Marker(cell.Value, kind, tier, chestType, 0, rng.NextDouble() * Math.Tau, ExpeditionTables.MarkerAo(kind, rng)));
        }
        for (int i = 0; i < remnantCount; i++)
        {
            var cell = Scatter(layout, rng, site, (ExpeditionTables.RemnantDistMin, ExpeditionTables.RemnantDistMax),
                remnants.Select(r => r.Cell), ExpeditionTables.RemnantSepMin);
            if (cell is null) continue;
            remnants.Add(new Remnant(cell.Value, ExpeditionTables.RollRelicMods(rng, S(Stat.MapExpeditionXExtraRelicPrefixes), S(Stat.MapExpeditionXExtraRelicSuffixes)), 0, rng.NextDouble() * Math.Tau));
        }
        var all = markers.Select(m => m.Cell).Concat(remnants.Select(r => r.Cell)).ToList();
        if (all.Count == 0) return null;
        var box = (all.Min(c => c.X), all.Min(c => c.Y), all.Max(c => c.X) + 1, all.Max(c => c.Y) + 1);
        var dirt = new List<(int X, int Y, byte Size)>();
        int dirtCount = (int)Math.Round(ExpeditionTables.DirtPerMarker * all.Count);
        for (int i = 0; i < dirtCount; i++)
        {
            var near = all[rng.Next(all.Count)];
            double angle = rng.NextDouble() * 2 * Math.PI, r = rng.NextDouble() * 20;
            // The lattice is a byte per axis (a cell / 10): a marker near the map's edge stays at 0.
            var lattice = (Math.Clamp((int)((near.X + r * Math.Cos(angle)) / 10), 0, 255), Math.Clamp((int)((near.Y + r * Math.Sin(angle)) / 10), 0, 255));
            double roll = rng.NextDouble();
            byte size = roll < ExpeditionTables.DirtLargeChance ? (byte)3
                : roll < ExpeditionTables.DirtLargeChance + ExpeditionTables.DirtMediumChance ? (byte)2 : (byte)1;
            if (!dirt.Any(d => d.X == lattice.Item1 && d.Y == lattice.Item2)) dirt.Add((lattice.Item1, lattice.Item2, size));
        }
        dirt.Sort((a, b) => b.Size.CompareTo(a.Size));
        return new Plan(site, room.Rotation, factionId, explosives, markers, remnants, box, dirt) { Radius = (byte)radius };
    }

    /// <summary>A cell at a distance in <paramref name="range"/> from the detonator, inside the map, on floor and at least
    /// <paramref name="sep"/> from every cell already placed; null after 8 tries (a create outside the map closes the
    /// client).</summary>
    public static int ScaleCount(int count, int percent) => (int)Math.Clamp((long)count * Math.Max(0L, 100L + percent) / 100, 0, ushort.MaxValue);

    static (int X, int Y)? Scatter(AreaLayout layout, Random rng, (int X, int Y) site, (double Min, double Max) range,
        IEnumerable<(int X, int Y)> placed, double sep)
    {
        var taken = placed.ToList();
        for (int attempt = 0; attempt < 8; attempt++)
        {
            double angle = rng.NextDouble() * 2 * Math.PI;
            double dist = range.Min + rng.NextDouble() * (range.Max - range.Min);
            (int X, int Y) cell = ((int)Math.Round(site.X + dist * Math.Cos(angle)), (int)Math.Round(site.Y + dist * Math.Sin(angle)));
            if (!InMap(layout, cell)) continue;
            if (!layout.WalkableFor(cell.X, cell.Y, 0)) continue;
            if (taken.All(c => Distance(c, cell) >= sep)) return cell;
        }
        return null;
    }

    static (int X, int Y) SpotNear(AreaLayout layout, (int X, int Y) center, double low, double high, Random rng)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            double angle = rng.NextDouble() * Math.Tau, radius = low + rng.NextDouble() * (high - low);
            (int X, int Y) at = ((int)Math.Round(center.X + radius * Math.Cos(angle)), (int)Math.Round(center.Y + radius * Math.Sin(angle)));
            if (InMap(layout, at) && layout.WalkableFor(at.X, at.Y, 0)) return at;
        }
        return center;
    }
}
