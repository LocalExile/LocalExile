using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Modules.Breach;

/// <summary>
/// The numbers, client tables, object metadata, and shared helpers of the Breach ("Brequel") module.
/// Every value says where it comes from: [data] a client table or capture, ESTIMATED a tuned value.
/// </summary>
public static class BreachTables
{
    /// <summary>The Initiator's encounter_state values.</summary>
    public const int Inactive = 0, Expanding = 1, Enraged = 2, ClosingEnraged = 3, Closing = 4;

    /// <summary>Object metadata paths.</summary>
    public const string InitiatorType = "Metadata/MiscellaneousObjects/Brequel/BrequelInitiator";
    public const string StabiliserType = "Metadata/MiscellaneousObjects/Brequel/BrequelStabiliser";
    public const string StabiliserFortressType = "Metadata/MiscellaneousObjects/Brequel/BrequelStabiliserFortress";
    public const string BlockerType = "Metadata/MiscellaneousObjects/Brequel/BrequelBlocker";
    public const string SpawnerType = "Metadata/MiscellaneousObjects/Brequel/BrequelSpawner";
    public const string SpawnerCoverType = "Metadata/MiscellaneousObjects/Brequel/BrequelSpawnerCover";
    public const string StalkFortressType = "Metadata/MiscellaneousObjects/Brequel/BrequelStalkFortress";
    public const string SkillObjectType = "Metadata/MiscellaneousObjects/Brequel/BrequelSkillObject";
    public const string PortalFortressType = "Metadata/MiscellaneousObjects/Brequel/BrequelPortalFortress";
    public const string PortalBossType = "Metadata/MiscellaneousObjects/Brequel/BrequelPortalBoss";
    public const string ChestBaseType = "Metadata/Chests/Breach/BrequelChestBase";
    public const string ChayulaFarmerNpcType = "Metadata/NPC/League/Chayula/ChayulaFarmerWild";
    public const string ChayulaFarmerMonsterType = "Metadata/Monsters/Breach/NPC/ChayulaFarmer";
    public const string ChayulaFarmerWildMonsterId = "Metadata/Monsters/Breach/NPC/ChayulaFarmerWild";
    public const string DialogueControllerType = "Metadata/Monsters/BreachBossFallenKnight/EshTulDialogueTimerController";
    public const string BirthingSackType = "Metadata/Monsters/BreachBossHandMage/BirthingSack";
    public const string BossDoorType = "Metadata/MiscellaneousObjects/BossForceFieldDoor";
    public const string DescensionObjectType = "Metadata/MiscellaneousObjects/Descendancy/BreachlordDescensionObject";

    /// <summary>Chest AO models (Chests.dat rows 2378..2381 Grasping Coffers).</summary>
    public static readonly string[] BrequelChestModels =
    [
        "Metadata/Chests/Breach/BreachBoxChest01.ao",
        "Metadata/Chests/Breach/BreachBoxChest02.ao",
        "Metadata/Chests/Breach/BreachBoxChest03.ao",
        "Metadata/Chests/Breach/BreachBoxChest04.ao",
    ];

    /// <summary>World area IDs and ExtraTerrainFeatures IDs.</summary>
    public const string MonasteryAreaId = "ChayulaLeague";
    public const string FortressAreaId = "ChayulaLeagueTower";
    public const string BossAreaId = "ChayulaLeagueTowerBoss";
    public const string HiveFeatureRow = "BrequelHiveEncounter";
    public const string SkyHiveFeatureRow = "BrequelSkyHiveEncounter";

    /// <summary>Monster variety IDs for Wild Boss &amp; Pinnacle Bosses.</summary>
    public const string WildBossId = "Metadata/Monsters/Breach/WildBreachBoss";
    public const string WildBossBellId = "Metadata/Monsters/Breach/WildBreachBoss/BellBreach";
    public const string WildBossHandId = "Metadata/Monsters/Breach/WildBreachBoss/HandPortal";
    public const string FallenKnightId = "Metadata/Monsters/BreachBossFallenKnight/FallenKnight";
    public const string HandMageId = "Metadata/Monsters/BreachBossHandMage/HandMage";
    public const string VolatileOrbChaosId = "Metadata/Monsters/BreachBossHandMage/VolatileOrbChaos";
    public const string VolatileOrbLightningId = "Metadata/Monsters/BreachBossHandMage/VolatileOrbLightning";

    /// <summary>Wombgift &amp; boss-key BaseItemType IDs.</summary>
    public const string ProvisioningWombgift = "Metadata/Items/Chayula/EquipmentFruit";
    public const string LavishWombgift = "Metadata/Items/Chayula/CurrencyFruit";
    public const string AncientWombgift = "Metadata/Items/Chayula/UniqueItemFruit";
    public const string MysteriousWombgift = "Metadata/Items/Chayula/MiscFruit";
    public const string HivebrainGlandId = "Metadata/Items/MapFragments/CurrencyBrequelBossKey";

    /// <summary>The clip the Expanding state plays first (1.0 s) and the end clip (2.666 s). A state never follows state
    /// 1 by less than the activate clip.</summary>
    public const double ClipActivate = 1.000, ClipEnd = 2.666;

    /// <summary>How close a player comes to touch it (cells): 15 [data] (BrequelStabiliser interaction_distance_override).</summary>
    public const double TouchRadius = 15;

    /// <summary>How many Breaches of an area may be open at once: 2 (3.29.0 patch note).</summary>
    public const int MaxOpen = 2;

    /// <summary>How long a Breach stays open after the touch without the enrage: 60 s. ESTIMATED.</summary>
    public const double OpenSeconds = 60;

    /// <summary>How long state 2 (enraged) lasts before state 3 (closing_enraged): 1.0 s. ESTIMATED.</summary>
    public const double EnragedSeconds = 1.0;

    /// <summary>The environment override's blend time (seconds): 2.0, a fade in. ESTIMATED (Delirium's pattern).</summary>
    public const double EnvSeconds = 2.0;

    /// <summary>How long after state 3 / 4 the Initiator and what is left of its wave go: the end clip.</summary>
    public const double RemoveAfter = ClipEnd;

    /// <summary>How far a wave pack's centre stands from the Breach's cell (cells): up to 110 [data].</summary>
    public const double WaveMaxDistance = 110;

    /// <summary>How many members one tick spawns at most: 16.</summary>
    public const int MaxWavePerTick = 16;

    /// <summary>The wave's shape: members per second of the open window, the mean of the two touched official Breaches
    /// (281 and 362 members over ~38 s and ~41 s) [data].</summary>
    public static readonly double[] WaveRates = [18, 23, 15, 8, 16, 14, 8.5, 11.5, 13.5, 5, 6.5, 6, 1, 4.5, 7, 8,
        10.5, 26.5, 18.5, 23, 12.5, 3.5, 11.5, 2, 6, 6, 1, 11, 9, 2.5, 0, 6, 1, 2.5, 1.5, 0.5, 0, 0, 0, 0.5, 0.5];

    /// <summary>How many seconds the wave runs: the last measured member is at s40.</summary>
    public static int WaveSeconds => WaveRates.Length;

    /// <summary>How many members the wave has spawned by <paramref name="age"/> seconds after the touch.</summary>
    public static double ExpectedWaveMembers(double age)
    {
        if (age <= 0) return 0;
        int whole = Math.Min((int)age, WaveRates.Length);
        double total = 0;
        for (int i = 0; i < whole; i++) total += WaveRates[i];
        if (whole < WaveRates.Length) total += WaveRates[whole] * (age - whole);
        return total;
    }

    /// <summary>The members' measured rarity mix (both captures: normal 524, magic 108, rare 11 of 643) [data].</summary>
    public const double NormalShare = 524.0 / 643, MagicShare = 108.0 / 643;

    /// <summary>How far apart the open Breach's ramp lines go out (seconds): 0.2 [data].</summary>
    public const double RulesetGap = 0.2;

    // ---- Cross-subsystem hooks wired by BreachModule.cs (breach-tree) ------------------------------------------------

    /// <summary>Awards raw Hiveblood delta to a player (storage cap, HUD sync, ModuleData persistence).</summary>
    public static Func<Player, int, Task>? AwardHiveblood { get; set; }

    /// <summary>Creates a Wombgift item with BrequelFruit component for the given BaseItemType Id and area level.</summary>
    public static Func<Player, string, int, Random, Item?>? CreateWombgift { get; set; }

    /// <summary>Optional quest body-part roller (BodyPart1..10) for ChayulaFindingBodyParts progression.</summary>
    public static Func<Player, int, Random, Item?>? TryRollBodyPart { get; set; }

    /// <summary>Awards Hiveblood to <paramref name="player"/> when wired (safe no-op fallback if unset).</summary>
    public static async Task GiveHiveblood(Player player, int amount)
    {
        if (amount <= 0 || player.Closed || player.LeavingArea) return;
        if (AwardHiveblood is { } award) await award(player, amount);
    }

    /// <summary>Creates a Wombgift via <see cref="CreateWombgift"/> when wired, falling back to a plain BaseType item.</summary>
    public static Item? MakeWombgift(Player player, string baseId, int areaLevel, Random rng)
    {
        if (CreateWombgift is { } factory && factory(player, baseId, areaLevel, rng) is { } created)
            return created;
        if (ItemsFor(player).ById(baseId) is { } bt)
            return ItemCatalog.Create(bt) with { ItemLevel = (byte)Math.Clamp(areaLevel, 1, 100) };
        return null;
    }

    /// <summary>Creates a Hivebrain Gland map fragment item (<c>CurrencyBrequelBossKey</c>).</summary>
    public static Item? MakeHivebrainGland(Player player, int areaLevel) =>
        ItemsFor(player).ById(HivebrainGlandId) is { } bt
            ? ItemCatalog.Create(bt) with { ItemLevel = (byte)Math.Clamp(areaLevel, 1, 100) }
            : null;

    // ---- Loaded client.db data ---------------------------------------------------------------------------------------

    /// <summary>Serializable metadata for a Breach world object.</summary>
    public sealed record ObjectSpec(
        string Type,
        IReadOnlyList<string> Components,
        StateMachineData? States,
        int StateMachineIndex,
        int TransitionableIndex,
        int TriggerableBlockageIndex,
        int InteractionActionIndex,
        int ChestIndex,
        bool Blocking,
        int Size);

    /// <summary>One row of <c>BrequelEncounterSkills.dat</c> (rows 0..9).</summary>
    public sealed record EncounterSkillRow(
        int Row,
        string DaemonId,
        string Description,
        int ParamA,
        int ParamB);

    /// <summary>The environment's row: BreachArtVariations row 0's Encounter (Environments 1187 LeagueBrequelEncounter).</summary>
    public static ushort? EnvRow { get; private set; }

    /// <summary>Rulesets rows: 164 Expanding, 165 Established, 166 Barrier.</summary>
    public static byte? RulesetRow { get; private set; }
    public static byte? EstablishedRulesetRow { get; private set; }
    public static byte? BarrierRulesetRow { get; private set; }

    /// <summary>ClientLeagueAction row for BrequelActivateEncounterSkill (44).</summary>
    public static int? ActivateSkillActionRow { get; private set; }

    /// <summary>The Initiator's components and StateMachine (kept for backward compatibility).</summary>
    public static (IReadOnlyList<string> Components, StateMachineData States, byte Index)? Initiator { get; private set; }

    /// <summary>All preloaded Breach object specs keyed by metadata path.</summary>
    public static IReadOnlyDictionary<string, ObjectSpec> Objects { get; private set; } = new Dictionary<string, ObjectSpec>(StringComparer.Ordinal);

    /// <summary>The 10 Ailith encounter skills from <c>BrequelEncounterSkills</c>.</summary>
    public static IReadOnlyList<EncounterSkillRow> EncounterSkills { get; private set; } = [];

    /// <summary>The wave's packs for Unstable Breaches (<c>brequel-*</c>).</summary>
    public static IReadOnlyList<PackCatalog.Pack> Packs { get; private set; } = [];

    /// <summary>The wave's packs for Established Hives &amp; Hive Fortresses (<c>brequelfort-*</c>).</summary>
    public static IReadOnlyList<PackCatalog.Pack> FortPacks { get; private set; } = [];

    static readonly string[] PackIds = ["brequel-neutral", "brequel-chaos", "brequel-physical", "brequel-cold", "brequel-fire", "brequel-lightning"];
    static readonly string[] FortPackIds = ["brequelfort-neutral", "brequelfort-chaos", "brequelfort-physical", "brequelfort-cold", "brequelfort-fire", "brequelfort-lightning"];

    static readonly string[] TrackedObjectTypes =
    [
        InitiatorType,
        StabiliserType,
        StabiliserFortressType,
        BlockerType,
        SpawnerType,
        SpawnerCoverType,
        StalkFortressType,
        SkillObjectType,
        ChestBaseType,
        DialogueControllerType,
        BirthingSackType,
        BossDoorType,
        DescensionObjectType,
    ];

    static Dictionary<int, int> LifeScales = [];
    static Dictionary<int, int> BossLifeScales = [];

    /// <summary>The life a Breach monster has more at this level (%, BreachLifeScalingPerLevel).</summary>
    public static int LifeScale(int level) => LifeScales.GetValueOrDefault(Math.Clamp(level, 1, 100), 0);

    /// <summary>The life a Breach boss has more at this level (%, BreachBossLifeScalingPerLevel: L68=112, L83=1044).</summary>
    public static int BossLifeScale(int level)
    {
        if (BossLifeScales.Count == 0) return LifeScale(level);
        int clamped = Math.Clamp(level, 68, 90);
        return BossLifeScales.GetValueOrDefault(clamped, 0);
    }

    /// <summary>Finds an object spec, falling back for BrequelStabiliserFortress -&gt; BrequelStabiliser if needed.</summary>
    public static ObjectSpec? Spec(string type)
    {
        if (Objects.TryGetValue(type, out var spec)) return spec;
        if (type == StabiliserFortressType && Objects.TryGetValue(StabiliserType, out spec)) return spec;
        return null;
    }

    /// <summary>Builds this module's game data when it loads.</summary>
    public static void LoadGameData()
    {
        EnvRow = ClientTables.TableOrEmpty("BreachArtVariations").Where(r => r.Index == 0).Select(r => r.IntOrNull("Encounter")).FirstOrDefault()
            is int env and >= 0 and <= ushort.MaxValue ? (ushort)env : null;

        byte? FindRuleset(string id) => ClientTables.TableOrEmpty("Rulesets")
            .Where(r => r.TextOrNull("Id") == id)
            .Select(r => (int?)r.Index)
            .FirstOrDefault() is int row and >= 0 and <= byte.MaxValue ? (byte)row : null;

        RulesetRow = FindRuleset("ExpandingBrequelEncounterRules");
        EstablishedRulesetRow = FindRuleset("EstablishedBrequelEncounterRules");
        BarrierRulesetRow = FindRuleset("BrequelBarrierRules");

        ActivateSkillActionRow = ClientTables.TableOrEmpty("ClientLeagueAction")
            .Where(r => r.TextOrNull("Id") == "BrequelActivateEncounterSkill")
            .Select(r => (int?)r.Index)
            .FirstOrDefault();

        var scales = new Dictionary<int, int>();
        foreach (var r in ClientTables.TableOrEmpty("BreachLifeScalingPerLevel"))
            if (r.IntOrNull("Level") is int level && r.IntOrNull("Scale") is int scale) scales[level] = scale;
        LifeScales = scales;

        var bossScales = new Dictionary<int, int>();
        foreach (var r in ClientTables.TableOrEmpty("BreachBossLifeScalingPerLevel"))
            if (r.IntOrNull("MonsterLevel") is int level && r.IntOrNull("LifeMultiplier") is int mult) bossScales[level] = mult;
        BossLifeScales = bossScales;

        Packs = [.. PackIds.Select(id => global::Game.Packs.Find(id)).OfType<PackCatalog.Pack>().Where(p => p.Entries.Count > 0)];
        FortPacks = [.. FortPackIds.Select(id => global::Game.Packs.Find(id)).OfType<PackCatalog.Pack>().Where(p => p.Entries.Count > 0)];
        if (FortPacks.Count == 0) FortPacks = Packs;

        var skills = new List<EncounterSkillRow>();
        foreach (var r in ClientTables.TableOrEmpty("BrequelEncounterSkills"))
        {
            string daemon = r.TextOrNull("Id") ?? "";
            if (daemon.Length == 0) continue;
            skills.Add(new EncounterSkillRow(
                r.Index,
                daemon,
                r.TextOrNull("Description") ?? "",
                r.IntOrNull("__col7") ?? 20,
                r.IntOrNull("__col8") ?? 50));
        }
        EncounterSkills = skills;

        var shared = ObjectTypes.SharedStates();
        var byType = ObjectTypes.All().ToDictionary(t => t.Type, t => t.Row, StringComparer.Ordinal);
        var specs = new Dictionary<string, ObjectSpec>(StringComparer.Ordinal);
        foreach (string typePath in TrackedObjectTypes)
        {
            if (QuestObjectTypes.Find(typePath) is not { } info) continue;
            StateMachineData? sm = byType.TryGetValue(typePath, out var row) ? ObjectTypes.StateMachine(row, shared) : null;
            if (!QuestObject.CanSerialize(info.Components, sm is not null)) continue;
            var comps = info.Components.ToList();
            specs[typePath] = new ObjectSpec(
                typePath,
                info.Components,
                sm,
                comps.IndexOf("StateMachine"),
                comps.IndexOf("Transitionable"),
                comps.IndexOf("TriggerableBlockage"),
                comps.IndexOf("InteractionAction"),
                comps.IndexOf("Chest"),
                info.Blocking,
                info.Size);
        }
        Objects = specs;

        Initiator = specs.TryGetValue(InitiatorType, out var initSpec)
            && initSpec.States is { } initStates
            && initStates.IndexOf("encounter_state") >= 0
            && initSpec.StateMachineIndex >= 0
            ? (initSpec.Components, initStates, (byte)initSpec.StateMachineIndex)
            : null;
    }

    // ---- Shared world & drop helpers ---------------------------------------------------------------------------------

    public static byte AreaLevel(Player player) =>
        (byte)Math.Clamp(MapRunOf(player) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);

    public static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    public static List<Player> Members(Player player) => [.. player.Instance.PlayersExcept(player.Id).Prepend(player)
        .Where(c => Current(c) && ReferenceEquals(c.Instance, player.Instance)).Distinct()];

    public static async Task Broadcast(List<Player> members, byte[] message, string? log)
    {
        for (int i = 0; i < members.Count; i++)
            await Send(members[i], message.ToArray(), i == 0 ? log : null);
    }

    public static async Task DropGroundItem(Player player, (int X, int Y) near, Item item, string log)
    {
        var at = player.Layout.Near(near);
        var ground = new WorldItemObject(at.X, at.Y, item);
        uint id = player.Instance.Ground.Add(ground, player);
        await SendGroundItem(player, id, ground, log, justDropped: true);
    }

    public static QuestObject? BuildObject(string type, int x, int y, float rotation = 0, string? model = null,
        bool opened = false, bool blocked = false, byte transitionState = 1, params (string Name, int Value)[] states)
    {
        if (Spec(type) is not { } spec) return null;
        var sm = spec.States;
        if (sm is not null)
            foreach (var (name, value) in states)
                if (sm.IndexOf(name) >= 0) sm = sm.Set(name, value);
        return new QuestObject(x, y, spec.Components, rotation, Model: model,
            TransitionState: transitionState, Opened: opened, Blocked: blocked)
        {
            StateMachine = sm,
        };
    }

    public static async Task<bool> CreateWorldObject(Player player, uint id, string type, int x, int y,
        float rotation = 0, string? model = null, bool opened = false, bool blocked = false,
        byte transitionState = 1, string? log = null, params (string Name, int Value)[] states)
    {
        if (Spec(type) is not { } spec) return false;
        if (BuildObject(type, x, y, rotation, model, opened, blocked, transitionState, states) is not { } obj) return false;
        byte[] blob = InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(spec.Type), obj.Blob());
        await SendWorldCreate(player, id, () => blob, log ?? $"0x02bb Breach {spec.Type} 0x{id:x} at ({x}, {y})",
            wake: true, awaitBirth: false);
        if (blocked && spec.Size > 0)
            player.SetObjectBlocker(id, x, y, spec.Size, false);
        return true;
    }

    public static async Task SetObjectStates(Player runner, uint id, string type, params (string Name, int Value)[] states)
    {
        if (Spec(type) is not { StateMachineIndex: >= 0, States: { } sm } spec) return;
        foreach (var (name, value) in states)
            if (sm.IndexOf(name) >= 0) sm = sm.Set(name, value);
        await SendWorldRecord(runner, id, InstanceEntities.StateMachineUpdate(id, (byte)spec.StateMachineIndex, sm), null);
    }

    public static async Task SetObjectBlocked(Player runner, uint id, string type, bool blocked)
    {
        if (Spec(type) is not { TriggerableBlockageIndex: >= 0 } spec) return;
        byte[] data = blocked ? [1, 0] : [0];
        await SendWorldRecord(runner, id, InstanceEntities.ComponentUpdate(id, (byte)spec.TriggerableBlockageIndex, data), null);
        if (!blocked)
            foreach (var m in Members(runner)) m.RemoveBlocker(id);
    }

    public static async Task SetMonsterStates(Player runner, uint id, params (string Name, int Value)[] changes)
    {
        if (!runner.Area.Monsters.TryGetValue(id, out var monster) || monster.States is not { } current || monster.Variety is not { } variety) return;
        foreach (var (name, value) in changes)
            if (current.IndexOf(name) >= 0) current = current.Set(name, value);
        var states = monster.States = current;
        if (runner.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
            runner.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = states } };
        int comp = variety.ComponentsOrBase.ToList().IndexOf("StateMachine");
        if (comp < 0) return;
        foreach (var member in Members(runner))
        {
            if (!member.Area.Monsters.TryGetValue(id, out var m) || m.Asleep) continue;
            await SendWorldRecord(member, id, InstanceEntities.StateMachineUpdate(id, (byte)comp, states), null);
        }
    }

    /// <summary>Drops loot from a Breach Clasped Hand / Grasping Coffer (<c>BrequelChestBase</c>).</summary>
    public static async Task DropBreachChestRewards(Player player, (int X, int Y) cell, int level,
        BreachAtlas atlas, Random rng, int brokenSpawners = 0, bool isLarge = false, bool isFortress = false)
    {
        // Hiveblood reward (boosted if map_breach_hive_chests_grant_hiveblood_instead_of_gold or large/fortress chest)
        int baseBlood = isFortress ? rng.Next(45, 85) : isLarge ? rng.Next(35, 65) : rng.Next(15, 30);
        if (atlas.HiveChestsGrantHivebloodInsteadOfGold) baseBlood = (int)Math.Round(baseBlood * 1.65);
        await GiveHiveblood(player, atlas.ScaleHiveblood(player, baseBlood, brokenSpawners));

        // Wombgift roll
        double baseGiftChance = isFortress ? 0.35 : isLarge ? 0.40 : 0.14;
        double giftChance = Math.Min(0.95, baseGiftChance * atlas.WombgiftChanceFactor(brokenSpawners, fortressChest: isFortress));
        if (rng.NextDouble() < giftChance)
        {
            string giftId = atlas.RollWombgiftBaseId(player, rng);
            if (MakeWombgift(player, giftId, level, rng) is { } gift)
                await DropGroundItem(player, cell, gift, "Breach chest Wombgift");
        }

        // Standard container items
        int want = isFortress ? rng.Next(4, 7) : isLarge ? rng.Next(4, 7) : rng.Next(2, 4);
        var items = DropsFor(player).Container(level, want, atlas.BrequelMonsterRarityPct, rng);
        int idx = 0;
        foreach (var drop in items)
        {
            double angle = idx * 2.399963229728653, radius = 5 + Math.Sqrt(idx) * 2;
            var at = ((int)Math.Round(cell.X + Math.Cos(angle) * radius), (int)Math.Round(cell.Y + Math.Sin(angle) * radius));
            await DropGroundItem(player, at, drop.Item, "Breach chest drop");
            idx++;
        }
    }
}
