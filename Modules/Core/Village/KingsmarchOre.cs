using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Modules.Village;

/// <summary>
/// Ore deposits in the maps a Settlers character runs: the client's ore node types
/// (<c>Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/*</c>), which a player claims and which then fill the
/// town's "Ore Marked" counters so its miners have something to mine.
/// <para>
/// A claimed deposit is what the mine panel's pending counters (the <c>Pending*</c> rows 0–4) come from; mining turns
/// them into ore (rows 5–9) and smelting turns the ore into bars (rows 15–19). The map-side half lands here; the
/// production half is <see cref="VillageProduction"/>.
/// </para>
/// <para>
/// Three hooks. <see cref="GameEvents.MapOpening"/> answers the Kalguuran Scarab's named content, <c>KalguuranOre</c> (the
/// name <c>MapScarabContent</c> gives the <c>Settlers</c> scarab family), so the map device reports no unhandled
/// encounter; the scarab needs nothing more than that, because its effect is the client's own stat
/// <c>map_contains_X_additional_village_ores</c> (<c>MapFragmentMods</c> row 197 → <c>Mods</c> row 19327 →
/// <c>Stats</c> row 21183, value 2), which the area's <see cref="MapMods"/> already carries in
/// <see cref="Stat.MapContainsXAdditionalVillageOres"/>. <see cref="GameEvents.AreaGenerated"/> places the deposits and
/// <see cref="GameEvents.AreaTick"/> places them again after a module reload, whose table of placed areas is new.
/// <see cref="GameEvents.ObjectUse"/> claims one: the ore is marked in the town through the callback the module hands in.
/// </para>
/// <para>
/// How many: the client's own league roll for its <c>Settlers</c> <c>CoreLeagues</c> row (row 27, through
/// <see cref="LeagueChances"/>), which on a standard map — <c>WorldAreaLeagueChances</c> row 127, the one
/// <c>MapWorldsStrand</c>, <c>MapWorldsDunes</c> and <c>MapWorldsCourtyard</c> all name — is a 16% chance and a 33%
/// repeat chance, most two per area (<c>CoreLeagues.__col11</c>), plus the scarab's two. That is 0 to
/// <see cref="MostDeposits"/> deposits, and it is the same shape as the Delve sulphite roll in
/// <see cref="MapSulphite.Present(Player, MapDevice.MapArea, MapMods)"/>.
/// </para>
/// </summary>
public static class KingsmarchOre
{
    /// <summary>The content key <c>MapScarabContent</c> gives the <c>Settlers</c> scarab family
    /// (<c>MapScarabContent.Encounters["Settlers"]</c>). That file names no constant for it, so it is named here.</summary>
    public const string Content = "KalguuranOre";

    /// <summary>Where the client keeps its ore node types.</summary>
    const string NodeFolder = "Metadata/Terrain/Leagues/Settlers/Node/Objects/NodeTypes/";

    /// <summary>The five deposits by the ore rank the town's counters use (1 Crimson Iron … 5 Verisium): the client's type
    /// folder and the ore name the wiki's ore-deposit page gives the same deposit. <c>DemonCopper</c> is Orichalcum: its
    /// description there is the demonic Beyond altar.</summary>
    static readonly (string Folder, string Ore)[] Ores =
    [
        ("CrimsonIron", "Crimson Iron"),
        ("DemonCopper", "Orichalcum"),
        ("PetrifiedWood", "Petrified Amber"),
        ("Bismuth", "Bismuth"),
        ("Verisium", "Verisium"),
    ];

    /// <summary>Most deposits an area can hold: the chance roll's two (<c>CoreLeagues</c> row 27's <c>__col11</c>) plus the
    /// Kalguuran Scarab's two.</summary>
    public const int MostDeposits = 4;

    /// <summary>
    /// What one deposit marks before the area's own modifiers — a number this server owns, and says so: the client's
    /// tables carry no ore amount, and the wiki gives none either (it says only that "the amount of ore obtained scales
    /// partially with area item quantity"). 50 units is a little under ten minutes of one rank-1 miner on Crimson Iron at
    /// the client's own rate (3600 × efficiency 18 / BaseProductionTime 120 = 540 units an hour), so a map pays for a
    /// fraction of an hour rather than a whole one.
    /// </summary>
    public const int BaseUnits = 50;

    /// <summary>How much of the area's item quantity the ore takes. The wiki's "partially" is read as half: +100% item
    /// quantity is +50% ore. Server-owned, like <see cref="BaseUnits"/>.</summary>
    public const int QuantityShare = 50;

    /// <summary>Each deposit's chance to be a rank, before the area's per-ore stats: rarer ore, rarer deposit. Server-owned,
    /// because the client keeps no base weights — its per-ore stats are modifiers on a base it holds internally.</summary>
    static readonly int[] RankWeights = [40, 25, 18, 11, 6];

    /// <summary>Each rank's own "chance to be this ore" stat (<c>map_village_crimson_ore_chance_+%</c> and its four
    /// siblings), which the atlas notables add to.</summary>
    static readonly uint[] RankStats =
    [
        Stat.MapVillageCrimsonOreChancePlusPct, Stat.MapVillageOrichalcumOreChancePlusPct,
        Stat.MapVillageAmberOreChancePlusPct, Stat.MapVillageBismuthOreChancePlusPct, Stat.MapVillageVerisiumOreChancePlusPct,
    ];

    /// <summary>One area's deposit ids, fixed per index: a module reload or a second player replaces its own presentation
    /// instead of leaving a duplicate behind (Delve's deposits are numbered the same way, from 0x0d100000).</summary>
    const uint FirstId = 0x0d500000;

    /// <summary>The square of cells a deposit blocks in the server's walk grid, as a Delve deposit blocks its own.</summary>
    const int BlockerSize = 4;

    /// <summary>How near a player has to stand to claim a deposit, in cells (<see cref="IPlayerWorld.WalkUpTo"/>).</summary>
    const int Reach = 4;

    /// <summary>The module id this file registered under, for <see cref="IPlayer.Uses"/> when choosing who a claim pays.
    /// </summary>
    static string? _module;

    /// <summary>Registers this file's work. The village module calls it once, when it loads.</summary>
    /// <param name="owner">The module's id, for the <c>GameEvents</c> owners its work runs under.</param>
    /// <param name="mark">Puts claimed ore into the town: the player, the ore's rank 1–5, and how many units were claimed.</param>
    public static void Install(string owner, Func<IPlayer, int, int, Task> mark)
    {
        _module = owner;
        // The scarab's encounter is this file's: without a subscriber MapScarabContent.Describe reports it as content no
        // enabled provider implements.
        GameEvents.MapOpening.Add(owner, request => request.HandledContent.Add(Content));
        GameEvents.AreaGenerated.Add(owner, player => Ensure(player, mark));
        // Every tick, and almost always one dictionary lookup: an area's deposits are placed once, and a module reload —
        // which starts this table empty — places them again on the next tick.
        GameEvents.AreaTick.Add(owner, GameEvents.Always(async (player, seconds) =>
        {
            await Ensure(player, mark);
            await Tick(player, seconds, mark);
        }));
        GameEvents.ObjectUse.Add(owner, (player, use, at) => Claim(player, use.TargetId, at, mark));
    }

    /// <summary>One ore node type as this server writes it: the path, the components in the order the client reads them
    /// (<see cref="ObjectTypes.Components"/> — the first <c>read</c> of the type's own list), the type's StateMachine
    /// defaults and where that component sits in the list (for the <c>0x2bc</c> update).</summary>
    sealed record OreType(int Rank, string Path, string Ore, IReadOnlyList<string> Components, StateMachineData States, byte StateIndex)
    {
        /// <summary>The type's own model (<c>object_types.animated_object</c>). Not load-bearing: a type created with the
        /// Animated flag at 0 draws its own art (Delve's sulphite does exactly that), so leaving this null is fine. It is
        /// written when the table has it, because an explicit path is what the town's own doodads do.</summary>
        public string? Model { get; init; }

        /// <summary>The type declares TriggerableBlockage (Petrified Wood: the tree the encounter must not destroy), so the
        /// blob writes it blocked — the same square the server's own blocker covers.</summary>
        public bool Triggerable { get; init; }
    }

    static OreType[]? _types;

    /// <summary>The five types this server can write, in rank order. A type whose components this server cannot write is
    /// left out (with a log line): a blob one byte short takes the client down.</summary>
    static OreType[] Types()
    {
        if (_types is { } cached) return cached;
        var rows = new Dictionary<string, ClientRow>(StringComparer.Ordinal);
        foreach (var (type, row) in ObjectTypes.All()) rows.TryAdd(type, row);
        var shared = ObjectTypes.SharedStates();
        var usable = new List<OreType>();
        for (int rank = 1; rank <= Ores.Length; rank++)
        {
            var (folder, ore) = Ores[rank - 1];
            string path = NodeFolder + folder;
            if (!rows.TryGetValue(path, out var row))
            {
                Console.WriteLine($"[map-content] kingsmarch ore: {path} is not in the object_types table");
                continue;
            }
            var components = ObjectTypes.Components(row);
            var states = ObjectTypes.StateMachine(row, shared);
            int stateIndex = components.ToList().IndexOf("StateMachine");
            if (states is null || stateIndex < 0 || !QuestObject.CanSerialize(components, haveStateMachine: true))
            {
                Console.WriteLine($"[map-content] kingsmarch ore: {path} cannot be written (components: {string.Join(", ", components)})");
                continue;
            }
            usable.Add(new OreType(rank, path, ore, components, states, (byte)stateIndex)
            {
                Triggerable = components.Contains("TriggerableBlockage"),
                Model = row.IsNull("animated_object") ? null : row.Text("animated_object"),
            });
        }
        _types = [.. usable];
        return _types;
    }

    /// <summary>One deposit: the id the client knows it by, its index in the area, its cells and its ore type.</summary>
    sealed record Deposit(uint Id, int Index, int X, int Y, OreType Type);

    /// <summary>What one area holds. The claimed ones are not here: they are the instance's states
    /// (<see cref="MinedKey"/>), which is what a reload and a second player both read.</summary>
    sealed class Encounter(IReadOnlyList<Deposit> deposits)
    {
        public IReadOnlyList<Deposit> Deposits { get; } = deposits;
        /// <summary>The deposits whose encounter is under way, by their index in the area.</summary>
        public readonly Dictionary<int, Running> Active = [];
    }

    /// <summary>
    /// One deposit's encounter, from the moment it is touched until the ore is claimed. The wiki describes four of the
    /// five (Crimson iron's Corrupted Growths and their Flesh Beyond, Orichalcum's altar and its Pale/Demonic Beyond,
    /// Petrified Amber's pack that must not destroy the amber, Verisium's guards); Bismuth has none — touching it only
    /// adds elemental modifiers to the area's monsters.
    /// </summary>
    sealed class Running
    {
        public required Deposit Deposit;
        /// <summary>When the channel finishes (unix seconds), or 0 for a fight. A channel spawns no monsters.</summary>
        public double ChannelEnds;
        /// <summary>The player holding the channel, for the completion line.</summary>
        public Player? Channelling;
        /// <summary>The monsters the encounter made: the objective counts every one of them dead.</summary>
        public readonly List<uint> Monsters = [];
        /// <summary>Petrified Amber's own durability, as a percentage: the pack chips at it while it is alive.</summary>
        public double Durability = 100;
        /// <summary>The Petrified Wood deposit's punching bag, the object that stands for the amber itself (0 when the
        /// deposit has no such target).</summary>
        public uint Amber;
        /// <summary>Seconds of area time left before the pack's next chip at the amber.</summary>
        public double NextBite;
        /// <summary>Orichalcum only: the altar called Pale Beyond rather than Demon, which is what its buff follows.</summary>
        public bool Pale;
    }

    // Per connection (one area load each), as the Delve sulphite encounter is kept.
    static readonly ConditionalWeakTable<Player, Encounter> _ore = [];

    /// <summary>The instance state one claimed deposit leaves behind, so a later player in the same instance finds it
    /// worked out and no click marks it twice.</summary>
    static string MinedKey(int index) => $"kingsmarch.ore.claimed.{index}";

    /// <summary>The skill a click carries when it means "channel into this deposit": the client compares it against the
    /// node's <c>begun_channelling</c> value to decide start or stop (F-126). The dispatcher's own literal.</summary>
    const ushort ChannelSkill = 23585;

    /// <summary>Stats row <c>map_settlers_verisium_maximum_channel_+%</c>: the area's own mod raises the channel.</summary>
    const uint MaxChannelStat = 21195;

    /// <summary>How long a channel runs, in seconds. The client reads no duration at all (F-128): the number is entirely
    /// the server's, this base over the area's own <c>map_settlers_verisium_maximum_channel_+%</c>.</summary>
    static double ChannelSeconds(Player player) =>
        2.0 * (1.0 + (AreaLeagueStats(player).GetValueOrDefault(MaxChannelStat) / 100.0));

    /// <summary>Where one deposit's running encounter is kept: on the instance, not on a connection, so a second player, a
    /// module reload and a player who walks out and back all read the same fight (ids, amber, durability).</summary>
    static string EncounterKey(int index) => $"kingsmarch.ore.encounter.{index}";

    /// <summary>
    /// Places the area's deposits for one player, once. Generated and tick hooks for one connection run in order, and the
    /// encounter is published before the first send, so a later tick cannot place a second set.
    /// </summary>
    static async Task Ensure(Player player, Func<IPlayer, int, int, Task> mark)
    {
        if (_ore.TryGetValue(player, out _)) return;
        if (!player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Instance is not { } instance) return;
        // A deposit shows up when a map opens: a town or a hideout has none.
        if (MapRunOf(player) is not { } opened) return;
        var types = Types();
        if (types.Length == 0) return;
        var mods = MapModsOf(player) ?? opened.Mods;
        var stats = AreaLeagueStats(player);
        // The ore's own draws, apart from the level generator's and the packs'.
        var rng = new Random(unchecked((int)(player.Seed ^ 0x4b6f7265)));
        int count = DepositCount(player, stats, rng, out string roll);
        var deposits = new List<Deposit>();
        if (count > 0)
        {
            foreach (var (x, y) in Positions(player.Layout, player.Seed, count))
                deposits.Add(new Deposit(FirstId + (uint)deposits.Count, deposits.Count, x, y, Pick(types, stats, rng)));
        }
        var encounter = new Encounter([.. deposits]);
        _ore.AddOrUpdate(player, encounter);
        Hydrate(instance, encounter);
        foreach (var deposit in encounter.Deposits)
            await Show(PlayerOf(player), deposit, instance.HasState(MinedKey(deposit.Index)));
        Console.WriteLine($"[map-content] kingsmarch ore area={player.Layout.Id} level={opened?.Area.Level}: {roll}; " +
            $"placed {encounter.Deposits.Count}" + string.Concat(encounter.Deposits.Select(d =>
                $"; {d.Type.Ore} rank {d.Type.Rank} 0x{d.Id:x} at ({d.X}, {d.Y})")));
    }

    /// <summary>
    /// Reads the encounters this area already has under way (a second player's, or one a module reload dropped) out of the
    /// instance's own store, so the tick can finish them. The text is the amber's number, its id and the roster's ids.
    /// </summary>
    static void Hydrate(InstanceState instance, Encounter encounter)
    {
        encounter.Active.Clear();
        foreach (var deposit in encounter.Deposits)
        {
            if (!instance.ContentState.TryGetValue(EncounterKey(deposit.Index), out string? text) || text.Length == 0)
                continue;
            var parts = text.Split('|');
            if (parts.Length != 3 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double durability))
                continue;
            var running = new Running { Deposit = deposit, Durability = durability };
            if (uint.TryParse(parts[1], out uint amber)) running.Amber = amber;
            foreach (string id in parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (uint.TryParse(id, out uint monster)) running.Monsters.Add(monster);
            encounter.Active[deposit.Index] = running;
        }
    }

    /// <summary>The running encounter as the instance stores it.</summary>
    static string Describe(Running running) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{running.Durability:0.###}|{running.Amber}|{string.Join(',', running.Monsters)}");

    /// <summary>
    /// How many deposits the area holds, and the roll written out for the log. The client's own league roll for its
    /// <c>Settlers</c> row (<c>0x141ea8800</c> through <see cref="LeagueChances"/>), with the row's two extra-chance stats
    /// added (<c>map_settlers_ore_chance_+%</c>, which the atlas notables' "+N% chance to contain Ore Deposits" carries,
    /// and <c>map_settlers_ore_additional_chance_%</c>), plus the Kalguuran Scarab's
    /// <c>map_contains_X_additional_village_ores</c>. <c>map_disable_settlers_ore</c> takes the whole area out;
    /// <c>map_disable_settlers_ore_from_chance</c> leaves only the scarab's and a forced <c>map_spawn_settlers_ore</c>.
    /// <para>
    /// Both stats go in as the roll's <c>added</c> percentage points, not its <c>increased</c> multiplier: the client's
    /// own wording for them is "+%" and "additional", and a hardmode league's alternate chance row is not consulted —
    /// the Anarchy module's roll does not consult it either.
    /// </para>
    /// </summary>
    static int DepositCount(Player player, IReadOnlyDictionary<uint, int> stats, Random rng, out string roll)
    {
        int S(uint stat) => stats.GetValueOrDefault(stat);
        if (S(Stat.MapDisableSettlersOre) > 0)
        {
            roll = "map_disable_settlers_ore: no deposits";
            return 0;
        }
        int natural = 0;
        if (S(Stat.MapDisableSettlersOreFromChance) > 0) roll = "the chance roll is disabled";
        else
        {
            natural = LeagueChances.Current.Count(player.Layout.Id, LeagueChances.Settlers, rng,
                added: S(Stat.MapSettlersOreChancePlusPct) + S(Stat.MapSettlersOreAdditionalChancePct));
            roll = $"Settlers roll {natural}";
        }
        int scarab = Math.Max(0, S(Stat.MapContainsXAdditionalVillageOres));
        int forced = Math.Max(0, S(Stat.MapSpawnSettlersOre));
        roll += $", scarab +{scarab}" + (forced > natural + scarab ? $", forced {forced}" : "");
        return Math.Clamp(Math.Max(natural + scarab, forced), 0, MostDeposits);
    }


    /// <summary>The ore one deposit is: the rank weights, each shifted by the area's own chance for that ore.</summary>
    static OreType Pick(OreType[] types, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        Span<long> weights = stackalloc long[types.Length];
        long total = 0;
        for (int i = 0; i < types.Length; i++)
        {
            int chance = stats.GetValueOrDefault(RankStats[types[i].Rank - 1]);
            weights[i] = Math.Max(0L, (long)RankWeights[types[i].Rank - 1] * Math.Max(0, 100 + chance) / 100);
            total += weights[i];
        }
        if (total <= 0) return types[rng.Next(types.Length)];
        long roll = rng.NextInt64(total);
        for (int i = 0; i < types.Length; i++)
            if ((roll -= weights[i]) < 0) return types[i];
        return types[^1];
    }

    /// <summary>
    /// The cells the deposits stand on: <see cref="MapSulphite.Positions"/>' picks, which respect the walkable grid, the
    /// native clearance (a radius above 4 can never pass <c>WalkableFor</c>) and a 25-cell separation, plus a second
    /// seed's picks when the area asks for more than one pass returns. Positions derives its candidates from the seed, so
    /// another seed offers cells the first pass did not; the separation is re-checked across both passes.
    /// </summary>
    static (int X, int Y)[] Positions(AreaLayout layout, uint seed, int count)
    {
        var first = MapSulphite.Positions(layout, seed);
        if (first.Length >= count) return first[..Math.Max(count, 0)];
        var all = new List<(int X, int Y)>(first);
        foreach (var p in MapSulphite.Positions(layout, seed ^ 0x9e3779b1u))
        {
            if (all.Count >= count) break;
            if (all.All(q => MapSulphite.Distance(p, q) >= 25)) all.Add(p);
        }
        return [.. all];
    }

    /// <summary>
    /// The monsters a deposit's encounter spawns, from the client's own Settlers varieties
    /// (<c>MonsterVarieties</c>): Crimson Iron's growths and the Flesh Beyond the wiki names, the Orichalcum altar's
    /// demons (Demon <em>or</em> Pale Beyond, which is also what picks the buff the fight leaves behind), the Petrified
    /// Amber pack, and the Verisium guards. Bismuth's list is empty: touching it has no encounter.
    /// </summary>
    /// <param name="rank">The ore's rank, 1 (Crimson Iron) to 5 (Verisium).</param>
    /// <param name="pale">Orichalcum only: whether the altar called Pale Beyond rather than Demon
    /// (<see cref="Faction"/>).</param>
    /// <summary>
    /// Whether the client survives a variety this server spawns. <c>MapBeyond</c> spawns the Hellscape flesh on every
    /// Beyond map, so that one is proven; the Kalguur varieties are not — spawning the Petrified Amber's pack crashed the
    /// client (<c>e0000001</c>, an unhandled exception, ~20 s into the fight, 2026-11-24) — so a fight made only of
    /// unproven varieties is not started and the deposit pays on touch, as Bismuth's does. <c>Game_ORE_ENCOUNTERS=all</c>
    /// spawns them anyway, which is how each one gets proven.
    /// </summary>
    static bool AllEncounters => Environment.GetEnvironmentVariable("Game_ORE_ENCOUNTERS") == "all";

    /// <summary>The varieties proven safe to spawn (see <see cref="AllEncounters"/>).</summary>
    static readonly HashSet<string> Proven = new(StringComparer.Ordinal)
    {
        "Metadata/Monsters/LeagueHellscape/FleshFaction/HellscapeFleshFodder1",
    };

    static bool Safe(string variety) => AllEncounters || Proven.Contains(variety);

    static IReadOnlyList<(string Variety, int Count, MonsterRarity Rarity)> EncounterFor(int rank, bool pale) =>
        EncounterRoster(rank, pale).Where(entry => Safe(entry.Variety)).ToList();

    /// <summary>What the fight would spawn, proven or not: <see cref="EncounterFor"/> filters this by the whitelist.</summary>
    static IReadOnlyList<(string Variety, int Count, MonsterRarity Rarity)> EncounterRoster(int rank, bool pale) => rank switch
    {
        1 =>
        [
            ("Metadata/Monsters/LeagueHellscape/FleshFaction/HellscapeFleshFodder1", 6, MonsterRarity.Normal),
            ("Metadata/Monsters/LeagueKalguur/CrimsonIron/SmallGrowthMaps", 3, MonsterRarity.Normal),
        ],
        2 when pale =>
        [
            ("Metadata/Monsters/LeagueKalguur/PaleFaction/DemonCopperPaleFodder1", 5, MonsterRarity.Normal),
            // K'Tash's Anger's meteors: the page's "fiery meteors will fall down in an area near the deposit".
            ("Metadata/Monsters/LeagueKalguur/Daemon/CopperHeraldOfSkyfalllDaemon", 1, MonsterRarity.Normal),
        ],
        2 =>
        [
            ("Metadata/Monsters/LeagueKalguur/DemonFaction/DemonCopperDemonFodder1_", 5, MonsterRarity.Normal),
            // Beidat's Wrath's lightning ("rains down lightning towards nearby enemies").
            ("Metadata/Monsters/LeagueKalguur/Daemon/CopperHeraldOfThunderDaemon_", 1, MonsterRarity.Normal),
        ],
        3 =>
        [
            ("Metadata/Monsters/LeagueKalguur/PetrifiedWood/PetrifiedWoodFungusZombie_", 8, MonsterRarity.Normal),
            ("Metadata/Monsters/LeagueKalguur/PetrifiedWood/PetrifiedWoodPrimordialMonster1", 4, MonsterRarity.Normal),
        ],
        5 =>
        [
            ("Metadata/Monsters/LeagueKalguur/VerisiumGuardBoss/VerisiumGuardBoss", 1, MonsterRarity.Unique),
            ("Metadata/Monsters/LeagueKalguur/VerisiumGuardBoss/VerisiumGuardDaemon", 2, MonsterRarity.Normal),
        ],
        _ => [],
    };

    /// <summary>
    /// The two BuffDefinitions rows the Orichalcum fight leaves behind, by the Beyond faction that answered the altar:
    /// <b>2492</b> <c>demon_copper_lightning</c> ("Beidat's Wrath": lightning, the page's "only when the encounter is
    /// Demonic Beyond monsters") and <b>2491</b> <c>demon_copper_meteors</c> ("K'Tash's Anger": fiery meteors, Pale). Both
    /// rows carry their own BuffVisuals (2160 and 2159) and are 30 seconds on the page.
    /// </summary>
    static (ushort Row, string Name) Blessing(bool pale) =>
        pale ? ((ushort)2491, "K'Tash's Anger") : ((ushort)2492, "Beidat's Wrath");

    /// <summary>How long the page says the Orichalcum fight's buff lasts: "for 30 seconds".</summary>
    const double BlessingSeconds = 30;

    /// <summary>The Petrified Amber's stand-in object — the tree the pack is trying to destroy — as the client's own
    /// punching-bag variety.</summary>
    const string AmberVariety = "Metadata/Monsters/LeagueKalguur/PetrifiedWood/PetrifiedWoodPunchingBag";

    /// <summary>How much of the amber one bite of the pack takes, in percent (the wiki counts hits; this counts the
    /// seconds in which the pack is alive and the amber is not dead).</summary>
    const double AmberBite = 4;


    /// <summary>Shows one deposit to one player: remove what that client holds under the id first (a reload or a second
    /// visit replaces its own presentation), then the create, the wake and the server-side blocker.</summary>
    static async Task Show(IPlayer modPlayer, Deposit deposit, bool claimed)
    {
        await SendWorldRemoval(modPlayer, deposit.Id);
        await SendWorldCreate(modPlayer, deposit.Id, InstanceEntities.CreateObject(deposit.Id,
            InstanceEntities.ObjectTypeHash(deposit.Type.Path), Blob(deposit.Type, deposit.X, deposit.Y, claimed)),
            $"Kingsmarch {deposit.Type.Ore} deposit {deposit.Index + 1} at ({deposit.X}, {deposit.Y}), claimed={claimed}");
        await SendWorldRecord(modPlayer, deposit.Id, InstanceProtocol.EnterWakeZone(deposit.Id));
        WorldOf(modPlayer).SetBlocker(deposit.Id, deposit.X, deposit.Y, BlockerSize);
    }

    /// <summary>
    /// The deposit's <c>0x2bb</c> component blob, written by <see cref="QuestObject"/>: the object flags, the base-data
    /// entry count, then one component per entry of the type's own read list, in that order. Every component these five
    /// types read is one that serializer already writes —
    /// <list type="bullet">
    /// <item>Positioned: x, y, rotation, u16 team, flags0, flags1 (16 bytes with no fixed height and no blocking
    /// override; reader <c>0x141e323f0</c>);</item>
    /// <item>Animated: u8 flags, 0 = the type's own <c>mining_feature_*_node.ao</c> (1 byte; <c>0x141e5afd0</c>);</item>
    /// <item>MinimapIcon: u8 flags, 0 = the type's icon (1 byte; <c>0x141e75c60</c>);</item>
    /// <item>InteractionAction: u8 flags, 0 = the type's own interaction (1 byte; <c>0x141e218a0</c>);</item>
    /// <item>StateMachine: u8 state count, u8 timer count, then per state a compact int and a flags byte (the type's
    /// <c>define_shared_state</c>s, which the client throws unless they match; <c>0x141e42430</c>);</item>
    /// <item>TriggerableBlockage: u8 blocked, and while blocked a u8 count (Petrified Wood only; <c>0x141e47880</c>);</item>
    /// <item>Buffs: u16 count, 0 (Bismuth only);</item>
    /// <item>ControlZone, BaseEvents, Functions: no bytes at all — their readers are the <c>ret</c> family
    /// <c>0x1400f1180</c>.</item>
    /// </list>
    /// Then the object's tasks (u8 count, 0). The components after the type's <c>read</c> — <c>Render</c>,
    /// <c>Targetable</c>, <c>WorldDescription</c>, <c>Preload</c> — are ones the client never deserializes, so they cost
    /// nothing and are not written; that is why <c>WorldDescription</c> never had to be established.
    /// </summary>
    static byte[] Blob(OreType type, int x, int y, bool claimed) =>
        new QuestObject(x, y, type.Components)
        {
            // A channelled ore is created with its channel states written explicitly at zero: the client's Verisium gate
            // answers 0 -- which starts nothing -- unless the node's state machine carries begun_channelling AND
            // encounter with a value at most 0 (F-126), and a state left at the type's default is not carried.
            StateMachine = IsChannelled(type) ? Zeroed(States(type, claimed)) : States(type, claimed),
            Blocked = type.Triggerable,
            Model = type.Model,
        }.Blob();

    /// <summary>
    /// The type's shared states as the server sets them: the client's own defaults (all 0) until the deposit is claimed,
    /// then <c>ore_empty = 1</c>. That name is the client's own for a worked-out deposit (<c>OreBase</c>'s
    /// <c>define_shared_state</c> rows, <c>object_type_shared_states</c>), and the .ot's <c>on_state_</c> bodies are not
    /// readable from the server, so the name is all the evidence there is. <c>activated</c> is deliberately left at 0:
    /// its event is the deposit's encounter, which this server does not run.
    /// </summary>
    /// <summary>
    /// The type's states for what the deposit is doing, under the names the client's own
    /// <c>object_type_shared_states</c> rows give it (F-122): <c>activated</c> once it is touched, <c>in_progress</c>
    /// while its fight runs (every one of the five types declares it), <c>ore_empty</c> once it is spent. A type that
    /// does not declare one of them keeps its default for it (<see cref="StateMachineData.Set"/> ignores unknown
    /// names), so <c>PetrifiedWood</c>'s <c>am_dead</c> and <c>Verisium</c>'s <c>channelling</c> are left to the paths
    /// that own them.
    /// </summary>
    static StateMachineData States(OreType type, bool claimed, bool activated = false, bool inProgress = false,
        bool begunChannelling = false, bool channelling = false, bool inZone = false)
    {
        var states = type.States;
        if (claimed)
            return states.Set("ore_empty", 1).Set("interactable", 0).Set("growths_alive", 0);
        // A fresh node is interactable: the type's own client block disables targeting on
        // create_state_interactable_0 and enables it on on_state_interactable_1, so a node created with the state at
        // its default 0 is a node the client refuses to click (live 2026-11-24: every ore was unclickable).
        states = states.Set("interactable", 1);
        // Crimson Iron's rock is a growth: create_state_growths_alive_0 plays the node's Death animation, so an
        // unclaimed node created at the default 0 draws no rock at all.
        states = states.Set("growths_alive", 1);
        if (activated) states = states.Set("activated", 1);
        if (inProgress) states = states.Set("in_progress", 1);
        // Verisium's four channel states (F-126): the client drops a Verisium click in silence unless the node carries
        // begun_channelling at all, and reads the channel as a toggle of its value. The other four types have none of
        // these names, and Set() leaves an unknown name alone.
        if (begunChannelling) states = states.Set("begun_channelling", 1);
        if (channelling) states = states.Set("channelling", 1);
        if (inZone) states = states.Set("in_zone", 1);
        return states;
    }

    /// <summary>A copy of these states with the channel states written explicitly as 0 (present, not running).</summary>
    static StateMachineData Zeroed(StateMachineData states) =>
        states.Set("begun_channelling", 0).Set("encounter", 0).Set("channelling", 0).Set("in_zone", 0);

    /// <summary>Whether this ore is the one a player channels into (Verisium). Read from the type's own states, so a
    /// server without the Verisium row needs nothing here.</summary>
    static bool IsChannelled(OreType type) => type.States.IndexOf("begun_channelling") >= 0;

    /// <summary>
    /// GameEvents.ObjectUse: a player uses a deposit. Walks to it (a claim from across the map is refused), then claims it
    /// through the instance's state — <c>SetState</c> is a set insert under a lock, so a second click, a second player and
    /// a tick cannot mark the same deposit twice — and finally marks the ore for every player the claim pays and shows
    /// them all the worked-out state.
    /// </summary>
    static async Task<bool> Claim(Player player, uint objectId, double requestedAt, Func<IPlayer, int, int, Task> mark)
    {
        if (!_ore.TryGetValue(player, out var encounter)) return false;
        if (encounter.Deposits.FirstOrDefault(d => d.Id == objectId) is not { } deposit) return false;   // not this file's object
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea || player.Instance is not { } instance) return true;
        if (instance.HasState(MinedKey(deposit.Index)))
        {
            // Already worked out: say so rather than walking the player there and going quiet.
            await PlayerOf(player).Tell($"Kingsmarch: that {deposit.Type.Ore} deposit is already worked out");
            return true;
        }
        // A second click while the encounter runs only re-announces it: the deposit is touched, not claimed twice.
        if (encounter.Active.ContainsKey(deposit.Index)) return true;
        if (!await WorldOf(PlayerOf(player)).WalkUpTo(deposit.X, deposit.Y, Reach, requestedAt)) return true;
        if (DepositStage(deposit) == Stage.Touch)
        {
            // Bismuth has no encounter (the wiki: touching it only adds elemental modifiers to the area's monsters), so
            // its ore is claimed on the spot. The others start their fight and are paid when it is won.
            await Finish(player, deposit, 1.0, false, mark);
            return true;
        }
        if (IsChannelled(deposit.Type))
        {
            // Verisium: the client has already toggled begun_channelling off (it toggles on the click it sends, and the
            // server sets the value), so the server's job is the duration and the `channelling` flag while it runs.
            await Channel(player, encounter, deposit);
            return true;
        }
        await Begin(player, encounter, deposit, mark);
        return true;
    }

    /// <summary>
    /// Runs one channel on a deposit: the node takes <c>channelling</c> for the area's own length, the running entry is
    /// published (so a tick can finish it and a second click can stop it), and the ore is paid when the time is up.
    /// <para>The client does none of this (F-129): it toggles <c>begun_channelling</c> on the click it sends and shows
    /// the beam, and everything after that is the server's.</para>
    /// </summary>
    static async Task Channel(Player player, Encounter encounter, Deposit deposit)
    {
        if (!player.Instance.ContentState.TryAdd(EncounterKey(deposit.Index), "")) return;
        var running = new Running { Deposit = deposit, Channelling = player, ChannelEnds = Now() + ChannelSeconds(player) };
        encounter.Active[deposit.Index] = running;
        foreach (var recipient in Recipients(player))
            await SendWorldRecord(recipient, deposit.Id,
                InstanceEntities.StateMachineUpdate(deposit.Id, deposit.Type.StateIndex,
                    States(deposit.Type, claimed: false, begunChannelling: true, channelling: true, inZone: true)),
                $"Kingsmarch {deposit.Type.Ore} deposit {deposit.Index + 1} channelling");
        player.Instance.ContentState[EncounterKey(deposit.Index)] = Describe(running);
        await PlayerOf(player).Tell($"Kingsmarch: channelling into the {deposit.Type.Ore} deposit");
        Console.WriteLine($"[map-content] kingsmarch ore: {deposit.Type.Ore} (rank {deposit.Type.Rank}) deposit " +
            $"{deposit.Index + 1} at ({deposit.X}, {deposit.Y}) channelled by {CharacterName(player)} for " +
            $"{ChannelSeconds(player):0.0} s");
    }

    /// <summary>Unix seconds, the clock every other timer in this module keeps.</summary>
    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>What touching a deposit does: the four encounters the wiki describes, or an immediate claim.</summary>
    enum Stage { Touch, Fight }

    static Stage DepositStage(Deposit deposit) => deposit.Type.Rank == 4 ? Stage.Touch : Stage.Fight;

    /// <summary>
    /// Starts a deposit's encounter: the node is switched to "activated", the roster's monsters are placed around it, and
    /// the encounter is published before the first spawn so a second click or a second player cannot start a second one.
    /// </summary>
    static async Task Begin(Player player, Encounter encounter, Deposit deposit, Func<IPlayer, int, int, Task> mark)
    {
        // The instance holds the one claim on this deposit: TryAdd fails for a second player or a second click in the
        // same frame, and the entry stays there until the fight is paid.
        if (!player.Instance.ContentState.TryAdd(EncounterKey(deposit.Index), "")) return;
        var running = new Running { Deposit = deposit };
        if (deposit.Type.Rank == 3 && AllEncounters)
        {
            // Petrified Amber: the tree itself is the client's own punching-bag variety; the pack attacks it, and an
            // amber that is destroyed pays nothing (the wiki: "must be defeated without the Petrified Amber being
            // destroyed"). Its own life is not read — the encounter keeps a durability percentage instead, because the
            // wiki's "each hit reduces durability" is per hit and no client table carries the number.
            var (amberId, _) = await Spawn(player, AmberVariety, MonsterRarity.Normal, deposit, 4);
            running.Amber = amberId;
        }
        int rank = deposit.Type.Rank;
        running.Pale = rank == 2 && rng.Next(2) == 1;
        var roster = EncounterFor(rank, running.Pale);
        if (roster.Count == 0 && running.Amber == 0)
        {
            // No proven monster for this ore: pay it on touch rather than spawn something the client may crash on.
            await Finish(player, deposit, 1.0, running.Pale, mark);
            Console.WriteLine($"[map-content] kingsmarch ore: {deposit.Type.Ore} (rank {rank}) has no proven encounter " +
                "monster (Game_ORE_ENCOUNTERS=all spawns the unproven ones); the deposit pays on touch");
            return;
        }
        encounter.Active[deposit.Index] = running;
        foreach (var (variety, count, rarity) in roster)
            await SpawnPack(player, running, variety, count, rarity, deposit);
        foreach (var recipient in Recipients(player))
            await SendWorldRecord(recipient, deposit.Id,
                InstanceEntities.StateMachineUpdate(deposit.Id, deposit.Type.StateIndex,
                    States(deposit.Type, claimed: false, activated: true, inProgress: true)),
                $"Kingsmarch {deposit.Type.Ore} deposit {deposit.Index + 1} touched");
        player.Instance.ContentState[EncounterKey(deposit.Index)] = Describe(running);
        Console.WriteLine($"[map-content] kingsmarch ore encounter: {deposit.Type.Ore} (rank {rank}) deposit " +
            $"{deposit.Index + 1} at ({deposit.X}, {deposit.Y}) by {CharacterName(player)}: {running.Monsters.Count} " +
            $"monster(s){(running.Amber != 0 ? $", amber 0x{running.Amber:x}" : "")}" +
            $"{(rank == 2 ? $", {(running.Pale ? "Pale" : "Demon")} Beyond" : "")}");
    }

    /// <summary>Places one variety's share of the encounter around the deposit, at a radius that keeps the fight around
    /// it rather than in the map's spawn.</summary>
    static async Task SpawnPack(Player player, Running running, string variety, int count, MonsterRarity rarity, Deposit deposit)
    {
        for (int i = 0; i < count; i++)
        {
            var (id, _) = await Spawn(player, variety, rarity, deposit, 14 + i % 3 * 5);
            if (id != 0) running.Monsters.Add(id);
        }
    }

    /// <summary>One monster of an encounter: the client's variety at the map's level, on a walkable cell near the
    /// deposit.</summary>
    static async Task<(uint Id, int Slot)> Spawn(Player player, string variety, MonsterRarity rarity, Deposit deposit, int radius)
    {
        if (Monsters.Find(variety) is not { Supported: true } catalog || player.Layout is not { } layout)
        {
            if (_unsupported.Add(variety))
                Console.WriteLine($"[map-content] kingsmarch ore: {variety} is not a supported monster variety; that part " +
                    "of the encounter is not placed");
            return (0, 0);
        }
        double angle = rng.NextDouble() * Math.Tau;
        var at = layout.Near((deposit.X + (int)(radius * Math.Cos(angle)), deposit.Y + (int)(radius * Math.Sin(angle))));
        byte level = (byte)Math.Clamp(MapRunOf(player)?.Area.Level ?? 68, 1, 100);
        uint id = await global::Game.SpawnMonster(player, catalog, level, rarity, at.X, at.Y,
            name: null, friendly: false, log: false);
        return (id, 0);
    }

    static readonly Random rng = new();
    /// <summary>Varieties already reported as unsupported, so the log carries one line each rather than one a spawn.</summary>
    static readonly HashSet<string> _unsupported = new(StringComparer.Ordinal);

    /// <summary>
    /// GameEvents.AreaTick for the encounters that are under way: the amber's durability falls while the pack is alive,
    /// and a deposit whose monsters are all dead is paid. A destroyed amber pays nothing and the deposit is spent.
    /// </summary>
    static async Task Tick(Player player, double seconds, Func<IPlayer, int, int, Task> mark)
    {
        if (!_ore.TryGetValue(player, out var encounter) || encounter.Active.Count == 0) return;
        if (player.Closed || player.LeavingArea || player.Area is null) return;
        foreach (var (index, running) in encounter.Active.ToList())
        {
            if (running.ChannelEnds > 0)
            {
                // A channel finishes on the clock alone and pays a full deposit: the client never computes a yield (F-129).
                if (Now() >= running.ChannelEnds)
                {
                    encounter.Active.Remove(index);
                    await Finish(player, running.Deposit, 1.0, running.Pale, mark);
                }
                continue;
            }
            bool anyAlive = running.Monsters.Any(id => player.Area.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0);
            if (running.Amber != 0)
            {
                bool amberAlive = player.Area.Monsters.TryGetValue(running.Amber, out var amber) && !amber.Dead && amber.Life > 0;
                if (!amberAlive)
                {
                    encounter.Active.Remove(index);
                    await Spend(player, running.Deposit, "the Petrified Amber was destroyed");
                    continue;
                }
                if (anyAlive)
                {
                    running.NextBite -= seconds;
                    bool bit = false;
                    while (running.NextBite <= 0)
                    {
                        running.NextBite += 1;
                        running.Durability = Math.Max(0, running.Durability - AmberBite);
                        bit = true;
                    }
                    if (bit && player.Instance is { } live) live.ContentState[EncounterKey(index)] = Describe(running);
                    if (running.Durability <= 0)
                    {
                        encounter.Active.Remove(index);
                        await Spend(player, running.Deposit, "the Petrified Amber broke");
                        continue;
                    }
                }
            }
            if (anyAlive) continue;
            encounter.Active.Remove(index);
            await Finish(player, running.Deposit, running.Amber != 0 ? running.Durability / 100.0 : 1.0,
                running.Pale, mark);
        }
    }

    /// <summary>Pays a won encounter's ore to everyone in the instance and marks the deposit worked out.</summary>
    static async Task Finish(Player player, Deposit deposit, double share, bool runningPale,
        Func<IPlayer, int, int, Task> mark)
    {
        int units = MapModsOf(player) is { } mods
            ? Math.Max(1, (int)Math.Round(Units(mods, AreaLeagueStats(player)) * Math.Clamp(share, 0.05, 1.0)))
            : Math.Max(1, (int)Math.Round(BaseUnits * Math.Clamp(share, 0.05, 1.0)));
        var recipients = Recipients(player).ToList();
        foreach (var recipient in recipients)
            await mark(PlayerOf(recipient), deposit.Type.Rank, units);
        await Mark(deposit, claimed: true, activated: true, recipients);
        // The Orichalcum fight's blessing: "Defeating the enemies imbues players with a temporary buff", by the Beyond
        // faction that answered the altar. Nobody else's fight leaves one.
        string? blessing = null;
        if (deposit.Type.Rank == 2 && runningPale)
        {
            var (row, name) = Blessing(runningPale);
            foreach (var recipient in recipients) Bless(recipient, deposit, row);
            blessing = name;
        }
        // The claim has to be visible where the player is standing: the ore goes into the town's "Ore Marked" counters,
        // which are drawn in Kingsmarch and nowhere else, so without this line a deposit that paid looks like a click
        // that did nothing (live 2026-11-24: eighteen clicks on a deposit that had already been paid).
        string said = blessing is null
            ? $"{units} x {deposit.Type.Ore} ore marked for the town"
            : $"{units} x {deposit.Type.Ore} ore marked; {blessing} for {BlessingSeconds:0} s";
        foreach (var recipient in recipients)
            await PlayerOf(recipient).Tell("Kingsmarch: " + said);
        Console.WriteLine($"[map-content] kingsmarch ore claimed: {deposit.Type.Ore} (rank {deposit.Type.Rank}) " +
            $"deposit {deposit.Index + 1} at ({deposit.X}, {deposit.Y}) by {CharacterName(player)}: {units} unit(s) " +
            $"({share:0%} of a full deposit), marked for {recipients.Count} player(s)" +
            $"{(blessing is null ? "" : $", {blessing} for {BlessingSeconds:0} s")}");
    }

    /// <summary>
    /// Grants one player the fight's blessing: its <c>BuffDefinitions</c> row on the player's own object, counted down
    /// from <see cref="BlessingSeconds"/>, sourced from the deposit so the buff goes with it. The client runs the
    /// countdown itself and ends the buff; nothing here has to (the row is <c>CancelOnDeath = 1</c> besides).
    /// </summary>
    static void Bless(Player player, Deposit deposit, ushort row)
    {
        if (player.Area is not { } area) return;
        area.Buffs.Set(player.Id, new(row, deposit.Id, U68: Buff.Seconds(BlessingSeconds)));
    }

    /// <summary>A deposit that was lost (the amber broke): the ore is not paid, and the deposit is spent so it cannot be
    /// touched again.</summary>
    static async Task Spend(Player player, Deposit deposit, string why)
    {
        await Mark(deposit, claimed: true, activated: true, Recipients(player).ToList());
        Console.WriteLine($"[map-content] kingsmarch ore lost: {deposit.Type.Ore} deposit {deposit.Index + 1} at " +
            $"({deposit.X}, {deposit.Y}): {why}");
    }

    /// <summary>The deposit's last word to every client: the state the panel reads and the instance's own "claimed"
    /// mark, which is what a reload and a second player find.</summary>
    static async Task Mark(Deposit deposit, bool claimed, bool activated, IReadOnlyList<Player> recipients)
    {
        foreach (var recipient in recipients)
        {
            if (recipient.Instance is { } instance)
            {
                instance.SetState(MinedKey(deposit.Index));
                instance.ContentState.TryRemove(EncounterKey(deposit.Index), out _);
            }
            await SendWorldRecord(recipient, deposit.Id,
                InstanceEntities.StateMachineUpdate(deposit.Id, deposit.Type.StateIndex,
                    States(deposit.Type, claimed, activated)),
                $"Kingsmarch {deposit.Type.Ore} deposit {deposit.Index + 1} {(claimed ? "claimed" : "touched")}");
        }
    }

    /// <summary>Who a claim pays: the wiki's "all party members will receive the resources", which here is every player
    /// in the map's instance that this module runs for.</summary>
    static IEnumerable<Player> Recipients(Player player)
    {
        var others = player.Instance?.PlayersExcept(player.Id) ?? [];
        return others.Prepend(player)
            .Where(p => !p.Closed && !p.LeavingArea && p.Instance == player.Instance
                && (_module is null || PlayerOf(p).Uses(_module)))
            .Distinct();
    }

    /// <summary>
    /// What one claim marks for one player: <see cref="BaseUnits"/> scaled by the area's item quantity (the wiki's
    /// "scales partially with area item quantity", at <see cref="QuantityShare"/> of it) and by the ore-found stat
    /// <c>map_settlers_ore_found_+%</c>. Never 0: a claim that marked nothing would look like a bug to the player.
    /// </summary>
    public static int Units(MapMods mods, IReadOnlyDictionary<uint, int> stats)
    {
        double quantity = Math.Max(0, 100 + mods.Quantity * (QuantityShare / 100.0)) / 100.0;
        double found = Math.Max(0, 100 + stats.GetValueOrDefault(Stat.MapSettlersOreFoundPlusPct)) / 100.0;
        return Math.Max(1, (int)Math.Round(BaseUnits * quantity * found));
    }
}
