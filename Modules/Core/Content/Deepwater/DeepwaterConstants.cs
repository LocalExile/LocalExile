using GameServer.Core;

namespace GameServer.Modules.Deepwater;

/// <summary>The numbers, rows and names an Allflame run uses: the client's DeepwaterConstants table (with its 3.29.3
/// values as fallbacks), object paths, GameObjectTasks ids, BuffDefinitions rows and QuestFlags names.</summary>
internal static class DeepwaterConstants
{
    static readonly Lazy<Dictionary<string, int>> Rows = new(() => ClientTables.TableOrEmpty("DeepwaterConstants")
        .Where(r => r.Text("Id") is not null).ToDictionary(r => r.Text("Id")!, r => r.Int("Value")));

    static int Value(string id, int fallback) => Rows.Value.TryGetValue(id, out int v) ? v : fallback;

    /// <summary>DeepwaterConstants 0: a lantern's full light radius in grid cells (the client's circle, <c>0x141ebe600</c>).</summary>
    public static int BaseLanternRadius => Value("BaseLanternRadius", 110);
    /// <summary>DeepwaterConstants 1: how far from a lantern the client snaps a new one (<c>0x141fc5800</c>).</summary>
    public static int BaseLanternPlacementRange => Value("BaseLanternPlacementRange", 115);
    /// <summary>DeepwaterConstants 2: a Chart's lanterns.</summary>
    public static int BaseLanternCount1x1 => Value("BaseLanternCount1x1", 6);
    /// <summary>DeepwaterConstants 3: a Voyage's lanterns.</summary>
    public static int BaseLanternCountVoyage => Value("BaseLanternCountVoyage", 25);
    /// <summary>DeepwaterConstants 4-6: a border reroll costs Base × (GrowthPercent / 100)^k Sulphur, at most Max times.</summary>
    public static int RerollBorderCostBase => Value("RerollBorderCostBase", 3000);
    public static int RerollBorderCostGrowthPercent => Value("RerollBorderCostGrowthPercent", 200);
    public static int RerollBorderMax => Value("RerollBorderMax", 5);

    /// <summary>The smallest radius a lit lantern draws (the client's lerp from 10 to <see cref="BaseLanternRadius"/>).</summary>
    public const int MinimumLanternRadius = 10;

    public const string ChartArea = "DeepwaterEncounter", VoyageArea = "DeepwaterEncounterVoyage";

    const string Objects = "Metadata/Terrain/Leagues/Deepwater/Objects/";
    public const string Lantern = Objects + "Lantern";
    public const string ExtractionObject = Objects + "ExtractionObject";
    public const string SummonExtractionObject = Objects + "SummonExtractionObject";
    public const string SummonExtractionObjectFallback = Objects + "SummonExtractionObjectFallback";

    /// <summary>GameObjectTasks 149 <c>DeepwaterLantern</c> (HASH16): f32 a, rate_a, b, rate_b (LE).</summary>
    public const ushort LanternTask = 0xc4df;
    /// <summary>GameObjectTasks 155 <c>DeepwaterBathysphereDrop</c> (HASH16): u32 stages, u32 stage (LE; handler
    /// <c>0x141640fd0</c>, writer <c>0x141ebe4f0</c>). The height is (1 − stage/(stages − 1))·1500; a stage ≥ stages plays
    /// <c>land_01</c> and the task ends itself (<c>0x141641310</c>).</summary>
    public const ushort BathysphereDropTask = 0x2387;
    /// <summary>The <c>this.RandomNumber</c> script variable (<c>QuestObject.VariableHash("RandomNumber")</c>) a Sulphur
    /// corpse's <c>ResourceChest*.ot</c> picks its <c>set_animated_0..7</c> pose with; without it the client logs a GEAL
    /// error and draws the bare corpse.</summary>
    public const uint RandomNumberVariable = 0xe70679eb;

    /// <summary>BuffDefinitions 2716 <c>deepwater_lantern_buff</c> (invisible; grants in_range_of_deepwater_lantern).</summary>
    public const ushort LanternBuff = 2716;
    /// <summary>BuffDefinitions 2736 <c>deepwater_drowning</c> ("Drowning", the Tangle drowning visuals).</summary>
    public const ushort DrowningBuff = 2736;
    /// <summary>BuffDefinitions 2791 <c>deepwater_lantern_wider_buff</c> (monsters near a lantern).</summary>
    public const ushort LanternWiderBuff = 2791;
    /// <summary>BuffDefinitions 2798 <c>deepwater_golden_lantern</c>.</summary>
    public const ushort GoldenLanternBuff = 2798;

    /// <summary>Seconds outside the light before the player drowns (the wiki; the buff's own 5.0 s).</summary>
    public const double DrowningSeconds = 5;
    /// <summary>The Drowning slow at its end. G: BuffTemplates 752 (deepwater_drowning_action_speed_+%_maximum_value) still
    /// says −50 in 3.29.3, but the wiki says 3.29.0 hotfix 3 cut the slow by 33 %; a server-side hotfix leaves the client's
    /// data as it was, so 50 × 2/3.</summary>
    public const int DrowningMaximumSlow = 33;
    public const string ActionSpeedReduction = "action_speed_-%";

    /// <summary>QuestFlags 3550: the player who holds the lanterns (the client's placement gate <c>0x1416835e0</c>).</summary>
    public const string LanternController = "IsDeepwaterLanternController";
    /// <summary>QuestFlags 2020: the capsule window's Send to Surface gate.</summary>
    public const string MapOwner = "IsMapOwner";
    /// <summary>QuestFlags 3562: the Bathysphere is targetable.</summary>
    public const string CanExtract = "DeepwaterCanExtract";
    /// <summary>QuestFlags 3547: items wait in the collection (114); the deck's CollectionChest is targetable with it.</summary>
    public const string StoredItems = "DeepwaterStoredItemsInExtractionChest";
    /// <summary>QuestFlags 3572: a Voyage's called Bathysphere is coming (the quest tracker's "hold the line").</summary>
    public const string VoyageDefend = "DeepwaterVoyageDefendUntilExtraction";
    /// <summary>QuestFlags 3579.</summary>
    public const string FirstLanternPlaced = "FirstLanternPlaced";
    /// <summary>QuestFlags 3625: the player is on the encounter's own deck.</summary>
    public const string InPseudoHub = "InMyDeepwaterPseudoHub";
    /// <summary>The run's flags, cleared at any entry outside the encounter areas (a crash or a relog left them on).</summary>
    public static readonly string[] RunFlags = [LanternController, MapOwner, CanExtract, InPseudoHub, VoyageDefend];

    /// <summary>League quest flag 1893 <c>Deepwater3x3VoyagesEnabled</c>: a Voyage then has 40 more lanterns (the wiki's
    /// 65 after the first Voyage; DeepwaterUpgrades 5, stat 23281).</summary>
    public const int VoyagesEnabledLeagueFlag = 1893;
    public const int VoyagesEnabledExtraLanterns = 40;
    /// <summary>League quest flag 1834 <c>DeepwaterBoatUnlocked</c> (Valerie's "Ready for Adventure?" sets it): the
    /// ChartLocker is targetable with it.</summary>
    public const int BoatUnlockedLeagueFlag = 1834;
    /// <summary>League quest flag 1820 <c>DeepwaterQuestChartTaken</c>.</summary>
    public const int ChartTakenLeagueFlag = 1820;
}
