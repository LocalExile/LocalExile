using System.Diagnostics;
using GameServer.Core;

/// <summary>
/// Builds the game data the Gateway serves from, once, in a fixed order, before it listens (as TrinityCore's
/// <c>World::SetInitialWorldSettings</c> calls each store's loader in turn): every client table is already in memory
/// (<see cref="ClientTables.LoadAll"/>), and each catalog here reads them into the plain fields its game rules use. No
/// catalog loads on first use: a player's first area, cast or shop costs what every later one does. A catalog that reads
/// another one comes after it here; one read before its loader ran is a null at startup, not a slow first use in game.
/// The modules build their own data the same way, in their module load (before the Gateway listens too).
/// </summary>
public static class WorldLoader
{
    /// <summary>Whether <see cref="Load"/> ran.</summary>
    public static bool Loaded { get; private set; }

    /// <summary>Builds every catalog in order, writing one line per step to <paramref name="log"/>. Runs once.</summary>
    public static void Load(Action<string>? log = null)
    {
        if (Loaded) return;
        var total = Stopwatch.StartNew();
        void Step(string name, Action load)
        {
            var watch = Stopwatch.StartNew();
            load();
            log?.Invoke($"[gateway] loaded {name} in {watch.ElapsedMilliseconds} ms");
        }

        // The catalogs every other one reads.
        Step("stats", () =>
        {
            // Made by the first Stat.Of when game data (GameData.Open) read stat names earlier: its server ids stay.
            Program.Stats ??= new();
            // The rules name stats by the generated keys (Stat.g.cs): they must be this build's.
            if (Program.Stats.Names.Count > 0 && Program.Stats.Checksum != Stat.Checksum)
                throw new InvalidOperationException("The client's Stats table differs from Stat.g.cs; run python scripts/dev/stat_ids.py and rebuild.");
        });
        Step("skills", () => Program.Skills = new());
        // The jewels the item types are built with: the radius jewels before the ones built on them.
        Step("cluster jewels", ClusterJewels.LoadGameData);
        Step("radius jewels", RadiusJewelCatalogue.LoadGameData);
        Step("timeless jewels", TimelessJewelCatalog.LoadGameData);
        Step("item types", () => Program.Items = new());
        if (Program.Items.Error is { } error) log?.Invoke($"[gateway] game data: item reference data unavailable: {error}");
        Step("item rolls", () => Program.Rolls = new(Program.Items));
        Step("crafting bench", () => Program.Bench = new(Program.Items, Program.Rolls));
        Step("passive tree", () => Program.Passives = new());
        Step("Forbidden jewels", ForbiddenJewelNotables.LoadGameData);
        Step("character stats", CharacterStats.LoadGameData);
        Step("character audio", CharacterAudioCatalog.LoadGameData);
        Step("pantheon", PantheonCatalog.LoadGameData);
        Step("passive buffs", PassiveBuffAuras.LoadGameData);
        Step("spirit chieftains", SpiritChieftains.LoadGameData);
        Step("skill animations", SkillAnimations.LoadGameData);
        Step("buff rows", BuffRows.LoadGameData);
        Step("bladefall volleys", BladefallVolleys.LoadGameData);
        Step("impossible escape", ImpossibleEscapeCatalogue.LoadGameData);

        // The world: areas, their layouts and objects, then the quests placed in them.
        Step("labyrinth trials", LabyrinthTrials.LoadGameData);
        Step("terrain plugins", AreaTerrainPlugins.LoadGameData);
        Step("server levels", ServerLevels.LoadGameData);
        Step("quest object types", QuestObjectTypes.LoadGameData);
        Step("area transitions", AreaTransitionCatalog.LoadGameData);
        Step("waypoints", WaypointCatalog.LoadGameData);
        Step("hideouts", HideoutCatalog.LoadGameData);
        Step("shrines", ShrineCatalog.LoadGameData);
        Step("area population", AreaPopulation.LoadGameData);
        Step("map boss fights", MapBossFights.LoadGameData);
        Step("quests", Quests.LoadGameData);
        Step("quest states", QuestStates.LoadGameData);
        Step("quest rewards", QuestStaticRewards.LoadGameData);
        Step("izaro fights", IzaroFights.LoadGameData);
        Step("izaro effects", IzaroEffects.LoadGameData);
        Step("izaro gargoyles", IzaroGargoyles.LoadGameData);

        // Vendors.
        Step("vendors", VendorCatalog.LoadGameData);
        Step("vendor prices", () => VendorPrices.For(Program.Items));
        Step("kirac", KiracProgress.LoadGameData);

        Loaded = true;
        log?.Invoke($"[gateway] world loaded in {total.ElapsedMilliseconds} ms");
    }
}
