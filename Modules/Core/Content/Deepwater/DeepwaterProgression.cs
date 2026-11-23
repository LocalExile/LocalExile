using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>The Sovereign hub progression, dynamic NPC lifecycle (NPCs 789–795 &amp; <c>CorpseBattery</c>), below-deck
/// Stash/GuildStash click-swap, <c>Q209–211</c> <c>NPCTalk</c> side-effects, <c>QuestRewardOffers#131–138</c> resolution,
/// <c>LQF 1834</c> boat unlock, <c>LQF 1841</c> Auto-Lanterns toggle, and Deepwater quest-item flag synchronization.</summary>
internal static class DeepwaterProgression
{
    public const int ValerieRow = 789,
        ValerieTutorialRow = 790,
        ValerieAfterFairgravesRow = 791,
        ValerieCraftingTutorialRow = 792,
        ValerieVesperReviveRow = 793,
        ValeriePostStoryRow = 794,
        VesperCraftingRow = 795;

    const string NpcPrefix = "Metadata/NPC/League/Deepwater/";
    public const string Valerie = NpcPrefix + "Valerie";
    public const string ValerieTutorial = NpcPrefix + "ValerieTutorial";
    public const string ValerieAfterFairgraves = NpcPrefix + "ValerieBelowDeckAfterFairgraves";
    public const string ValerieCraftingTutorial = NpcPrefix + "ValerieBelowDeckCraftingTutorial";
    public const string ValerieVesperRevive = NpcPrefix + "ValerieVesperRevive";
    public const string ValeriePostStory = NpcPrefix + "ValeriePostStory";
    public const string VesperCrafting = NpcPrefix + "VesperCrafting";
    public const string ValeriePlacerOne = NpcPrefix + "ValeriePlacerOne";
    public const string ValeriePlacerTwo = NpcPrefix + "ValeriePlacerTwo";
    public const string ValeriePlacerThree = NpcPrefix + "ValeriePlacerThree";
    public const string CorpseBattery = "Metadata/Terrain/Leagues/Deepwater/Objects/CorpseBattery";

    // Quest item BaseItemTypes Ids.
    public const string FairgravesAllflame = "Metadata/Items/QuestItems/AllFlameLanternDeepwater"; // #1536, QF 3583
    public const string VelkaAllflame = "Metadata/Items/QuestItems/BossAllflameDeepwater";         // #1537, QF 3607
    public static readonly string[] UpgradePieces =
    [
        "Metadata/Items/QuestItems/Deepwater/DeepwaterUpgradePiece1", // #5426 Deadcaller Bellows, QF 3563
        "Metadata/Items/QuestItems/Deepwater/DeepwaterUpgradePiece2", // #5427 Sulphur Purifier, QF 3564
        "Metadata/Items/QuestItems/Deepwater/DeepwaterUpgradePiece3", // #5428 Spectral Infuser, QF 3565
        "Metadata/Items/QuestItems/Deepwater/DeepwaterUpgradePiece4", // #5429 Alchemical Exchange, QF 3566
        "Metadata/Items/QuestItems/Deepwater/DeepwaterUpgradePiece5", // #5430 Forbidden Scripture, QF 3567
    ];

    // QuestFlags names.
    public const string HaveAllflameFlag = "HaveDeepwaterAllflame";                    // QF 3583
    public const string HaveBossAllflameFlag = "HaveDeepwaterBossAllflame";            // QF 3607
    public const string TutorialRingTakenFlag = "DeepwaterTutorialRingTaken";          // QF 3599
    public const string CraftSeenFlag = "DeepwaterCraftSeen";                          // QF 3605
    public const string VesperPowerGivenFlag = "DeepwaterVesperPowerGiven";            // QF 3610
    public static readonly string[] UpgradeHeldFlags =
    [
        "HaveDeepwaterUpgradeItem1", // QF 3563
        "HaveDeepwaterUpgradeItem2", // QF 3564
        "HaveDeepwaterUpgradeItem3", // QF 3565
        "HaveDeepwaterUpgradeItem4", // QF 3566
        "HaveDeepwaterUpgradeItem5", // QF 3567
    ];

    // LeagueQuestFlags ids.
    public const int BoatUnlockedLqf = 1834;              // DeepwaterBoatUnlocked
    public const int AutoLanternsLqf = 1841;              // DeepwaterAutoLanternsActive
    public const int ShipGraveyardQuestGivenLqf = 1854;   // ValShipGraveyardQuestGiven
    public const int ValAllflameHeldSeenLqf = 1855;       // ValAllflameHeldSeen
    public const int FairgravesSeenLqf = 1856;            // AllflameQuestFairgravesSeen
    public const int CraftingTutorialCompletedLqf = 1857; // DeepwaterCraftingTutorialCompleted
    public const int VesperFlameGivenLqf = 1859;          // DeepwaterValVesperFlameGiven
    public const int VesperRingTakenLqf = 1864;           // Offer#132 taken LQF
    public const int CraftingTutorialStartedLqf = 1869;   // DeepwaterCraftingTutorialStarted
    public const int TutorialRingTakenLqf = 1870;         // Offer#134 taken LQF
    public const int ContinueChartingLqf = 1882;          // DeepwaterTutorialContinueCharting
    public const int TakePitySulphurLqf = 1883;           // DeepwaterTutorialTakePitySulphur
    public const int VelkaFlameGivenLqf = 1885;           // DeepwaterVelkaFlameGiven
    public const int VelkaKilledLqf = 1886;               // DeepwaterVelkaKilled
    public const int VesperRevivedLqf = 1888;             // DeepwaterVesperRevived
    public const int VesperPowerGivenLqf = 1889;          // DeepwaterVesperPowerGivenLeague
    public const int VelkaAllflameFallbackSeenLqf = 1890; // NPCTalk#3973
    public const int ReturnTheFavourLqf = 1891;           // NPCTalk#3915 (unlocks Offers#131–133)
    public const int PurifierFallbackSeenLqf = 1898;      // NPCTalk#3954/#3982
    public const int CursedDucatsSeenLqf = 1910;          // DeepwaterVelkaCursedDucatsSeen

    // QuestRewardOffers rows.
    public const int OfferVesperSulphur = 131;
    public const int OfferVesperRing = 132;
    public const int OfferVesperAlchemy = 133;
    public const int OfferTutorialRing = 134;
    public const int OfferTutorialTransmute = 135;
    public const int OfferPitySulphur = 136;
    public const int OfferVelkaAllflame = 137;
    public const int OfferSulphurPurifierFallback = 138;
    public const int OfferCursedDucats = 139;

    // BuffDefinitions#2799 VelkaFightableBuff (2s visual/audio cue from NPCTalk#3911).
    const ushort VelkaFightableBuff = 2799;

    static readonly uint FairgravesAllflameHash = InstanceEntities.ObjectTypeHash(FairgravesAllflame);
    static readonly uint VelkaAllflameHash = InstanceEntities.ObjectTypeHash(VelkaAllflame);
    static readonly uint TsoathaDucatHash = InstanceEntities.ObjectTypeHash(DeepwaterVelka.TsoathaDucatId);
    static readonly uint[] UpgradePieceHashes = [.. UpgradePieces.Select(InstanceEntities.ObjectTypeHash)];
    static readonly byte[] QuestItemRows =
    [
        PlayerInventories.MainInventory,
        PlayerInventories.ExpandedInventory,
        PlayerInventories.Cursor,
        DeepwaterExtraction.CollectionRow,
    ];

    // Pending ground quest-item pickups awaiting walk-up + bag insertion (0-alloc fast-exit on Tick when empty).
    static readonly ConditionalWeakTable<Player, List<(uint GroundId, double Until)>> PendingQuestPickups = new();
    static int _pendingQuestPickupCount;
    const double QuestPickupWaitSeconds = 20;

    static StateMachineData? CorpseBatteryStates;

    public static void LoadGameData() => CorpseBatteryStates =
        ObjectTypes.All().Where(t => t.Type == CorpseBattery)
            .Select(t => ObjectTypes.StateMachine(t.Row, ObjectTypes.SharedStates()))
            .FirstOrDefault();

    sealed class HubScene
    {
        public uint DeckValerieId;
        public NpcCatalog.Npc? DeckValerieNpc;
        public uint BelowValerieId;
        public NpcCatalog.Npc? BelowValerieNpc;
        public uint VesperId;
        public NpcCatalog.Npc? VesperNpc;
        public uint CorpseBatteryId;
        public readonly Dictionary<uint, (int X, int Y)> Stashes = [];
        public readonly Dictionary<uint, (int X, int Y)> GuildStashes = [];
    }

    static readonly ConditionalWeakTable<Player, HubScene> Scenes = new();

    static bool HasLeagueFlag(Player player, int flag) =>
        player.Character.LeagueQuestFlags.Contains(flag);

    /// <summary><c>GameEvents.NpcUsesLeagueFlags</c>: opts all 7 Deepwater NPCs (rows 789–795) into automatic league-flag
    /// <c>NPCTalk</c> filtering, auto-conversations, and <c>LeagueFlagsSet</c> application.</summary>
    public static bool UsesLeagueFlags(Player player, NpcCatalog.Npc npc) =>
        npc.Row is >= ValerieRow and <= VesperCraftingRow;

    /// <summary>Whether The Sovereign is unlocked for the World panel pin (C→S 0x0183) and <c>/boat</c>:
    /// <c>LQF 1834</c> (<c>DeepwaterBoatUnlocked</c>, set by Valerie's Coast #3932), or <c>QF 3556</c> for saves that
    /// boarded before Phase 7.</summary>
    public static bool BoatUnlocked(Player player) =>
        HasLeagueFlag(player, BoatUnlockedLqf) || ClientHasQuestFlag(player, DeepwaterModule.HubVisited);

    // ---- Hub NPCs, CorpseBattery & Below-Deck Stashes ----

    /// <summary>Spawns all dynamic Sovereign hub NPCs (deck Valerie variant, below-deck Valerie, <c>VesperCrafting</c>),
    /// <c>CorpseBattery</c>, and below-deck Stash/GuildStash, and syncs Deepwater quest-item flags.</summary>
    public static async Task SpawnHub(IModuleContext module, Player player)
    {
        await SyncQuestItemFlags(player);
        if (PlayerOf(player).AreaId != DeepwaterModule.HubId) return;
        var scene = new HubScene();
        Scenes.AddOrUpdate(player, scene);
        await SpawnHubNpcs(module, player, scene);
        await SpawnHubStashes(module, player, scene);
    }

    /// <summary>Spawns the hub's dynamic NPCs and <c>CorpseBattery</c> for this player's current quest progression.</summary>
    public static Task SpawnHubNpcs(IModuleContext module, Player player) =>
        SpawnHubNpcs(module, player, Scenes.GetOrCreateValue(player));

    static async Task SpawnHubNpcs(IModuleContext module, Player player, HubScene scene)
    {
        if (PlayerOf(player).AreaId != DeepwaterModule.HubId) return;
        var lines = player.Layout.Level?.RoomObjects ?? [];

        // 1. Deck Valerie (exactly one active variant on ValeriePlacerOne/Two/Three or beside CorpseBattery).
        bool velkaKilled = HasLeagueFlag(player, VelkaKilledLqf);
        bool vesperRevived = HasLeagueFlag(player, VesperRevivedLqf);
        bool firstVoyage = HasLeagueFlag(player, DeepwaterConstants.FirstVoyageLeagueFlag);
        bool velkaFlameGiven = HasLeagueFlag(player, VelkaFlameGivenLqf);

        string deckType;
        (int X, int Y, float Rot) deckAt;
        if (velkaKilled && !vesperRevived)
        {
            deckType = ValerieVesperRevive;
            deckAt = FindPlacement(lines, ValerieVesperRevive, 350, 446, 2.48146f);
        }
        else if (vesperRevived)
        {
            deckType = ValeriePostStory;
            deckAt = FindPlacement(lines, ValeriePlacerThree, 289, 430, -1.01836f);
        }
        else if (firstVoyage)
        {
            deckType = Valerie;
            deckAt = FindPlacement(lines, ValeriePlacerTwo, 303, 471, 0.279252f);
        }
        else
        {
            deckType = ValerieTutorial;
            deckAt = FindPlacement(lines, ValeriePlacerOne, 313, 457, 1.5708f);
        }

        if (Npcs.Find(deckType) is { Supported: true } deckNpc)
        {
            if (deckNpc.StateMachine is { } sm)
            {
                // G: placer/otter state mapping per quest stage (server-only in GGG's game).
                sm = deckType switch
                {
                    ValerieVesperRevive => sm.Set("revive_vesper", velkaFlameGiven ? 1 : 0),
                    ValeriePostStory => sm.Set("placement", 3).Set("otter", 4),
                    Valerie => sm.Set("placement", 2).Set("otter", 3),
                    _ => sm.Set("placement", 1).Set("otter", 1),
                };
                deckNpc = deckNpc with { StateMachine = sm };
            }
            scene.DeckValerieId = await SpawnNpc(player, deckNpc, deckAt.X, deckAt.Y, deckAt.Rot);
            scene.DeckValerieNpc = deckNpc;
        }
        else
        {
            module.Log($"{PlayerOf(player).Character}: deck NPC {deckType} unknown or unsupported");
        }

        // 2. Deck CorpseBattery (globals.vesper_tank): always spawned so ValerieVesperRevive's beam has a live target.
        var batteryAt = FindPlacement(lines, CorpseBattery, 364, 459, 0f);
        if (QuestObjectTypes.Find(CorpseBattery) is { } batteryInfo && CorpseBatteryStates is { } batteryStates
            && QuestObject.CanSerialize(batteryInfo.Components, true))
        {
            uint id = player.TakeObjectId();
            var blob = new QuestObject(batteryAt.X, batteryAt.Y, batteryInfo.Components, batteryAt.Rot)
            {
                StateMachine = batteryStates.Set("open", velkaFlameGiven ? 1 : 0),
            }.Blob();
            if (await global::Game.SendWorldCreate(PlayerOf(player), id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(CorpseBattery), blob),
                $"0x02bb CorpseBattery 0x{id:x} at ({batteryAt.X}, {batteryAt.Y}), open {(velkaFlameGiven ? 1 : 0)}"))
            {
                await global::Game.SendWorldRecord(PlayerOf(player), id, InstanceProtocol.EnterWakeZone(id));
                if (batteryInfo.Blocking) player.SetBlocker(id, batteryAt.X, batteryAt.Y, Math.Max(1, batteryInfo.Size));
                scene.CorpseBatteryId = id;
            }
        }

        // 3. Below-deck VesperCrafting (#795, globals.vespercrafting): always spawned; cansee = 1 once LQF 1859 is set.
        bool vesperAwake = HasLeagueFlag(player, VesperFlameGivenLqf);
        var vesperAt = FindPlacement(lines, VesperCrafting, 331, 137, -1.60168f);
        if (Npcs.Find(VesperCrafting) is { Supported: true } vesperNpc)
        {
            if (vesperNpc.StateMachine is { } vsm)
                vesperNpc = vesperNpc with { StateMachine = vsm.Set("cansee", vesperAwake ? 1 : 0) };
            scene.VesperId = await SpawnNpc(player, vesperNpc, vesperAt.X, vesperAt.Y, vesperAt.Rot);
            scene.VesperNpc = vesperNpc;
        }

        // 4. Below-deck Valerie (#791 after Fairgraves + craft, or #792 during crafting tutorial) while !LQF 1859.
        await SpawnBelowDeckValerie(module, player, scene, lines);
    }

    static string? DesiredBelowDeckValerie(Player player)
    {
        if (HasLeagueFlag(player, VesperFlameGivenLqf)) return null;
        if (HasLeagueFlag(player, ValAllflameHeldSeenLqf) && HasLeagueFlag(player, CraftingTutorialCompletedLqf))
            return ValerieAfterFairgraves;
        if (HasLeagueFlag(player, CraftingTutorialStartedLqf) || HasLeagueFlag(player, ValAllflameHeldSeenLqf))
            return ValerieCraftingTutorial;
        return null;
    }

    static async Task SpawnBelowDeckValerie(IModuleContext module, Player player, HubScene scene,
        IReadOnlyList<GameServer.Data.Terrain.Generation.PlacedTileObject> lines)
    {
        if (DesiredBelowDeckValerie(player) is not { } type) return;
        var at = type == ValerieAfterFairgraves
            ? FindPlacement(lines, ValerieAfterFairgraves, 302, 155, 0.748982f)
            : FindPlacement(lines, ValerieCraftingTutorial, 306, 155, 0.671311f);
        if (Npcs.Find(type) is not { Supported: true } npc)
        {
            module.Log($"{PlayerOf(player).Character}: below-deck NPC {type} unknown or unsupported");
            return;
        }
        if (type == ValerieAfterFairgraves && npc.StateMachine is { } sm)
            npc = npc with { StateMachine = sm.Set("give_flame", 0) };
        scene.BelowValerieId = await SpawnNpc(player, npc, at.X, at.Y, at.Rot);
        scene.BelowValerieNpc = npc;
    }

    /// <summary>Live-updates below-deck Valerie when quest flags advance on The Sovereign without requiring a relog
    /// (e.g., spawning #792 right after #3926 "Keep Charting", or upgrading #792 → #791 once LQF 1855 &amp; 1857 are both set).</summary>
    public static async Task RefreshHubNpcs(IModuleContext module, Player player)
    {
        if (PlayerOf(player).AreaId != DeepwaterModule.HubId || !Scenes.TryGetValue(player, out var scene)) return;
        string? desired = DesiredBelowDeckValerie(player);
        // Never despawn #791 right after LQF 1859 so her give_flame = 1 beam & ValVesperReleaseConvo play uninterrupted.
        if (desired is null) return;
        if (scene.BelowValerieNpc?.Id == desired) return;
        if (scene.BelowValerieId != 0 && player.TalkingTo == scene.BelowValerieId) return;

        if (scene.BelowValerieId != 0)
        {
            uint oldId = scene.BelowValerieId;
            string oldName = scene.BelowValerieNpc?.Name ?? "Valerie";
            scene.BelowValerieId = 0;
            scene.BelowValerieNpc = null;
            lock (player.Npcs) player.Npcs.Remove(oldId);
            player.RemoveBlocker(oldId);
            await global::Game.SendWorldRemoval(player, oldId,
                $"0x02bd remove below-deck npc 0x{oldId:x} {oldName} (stage changed)");
        }
        await SpawnBelowDeckValerie(module, player, scene, player.Layout.Level?.RoomObjects ?? []);
    }

    /// <summary>Spawns below-deck Stash and GuildStash (from <c>hub_interior.arm</c>) and records both deck and below-deck
    /// stash ids so <see cref="PickHubStash"/> swaps them cleanly on click (exact <c>Rules.LabyrinthStash</c> pattern).</summary>
    public static Task SpawnHubStashes(IModuleContext module, Player player) =>
        SpawnHubStashes(module, player, Scenes.GetOrCreateValue(player));

    static async Task SpawnHubStashes(IModuleContext module, Player player, HubScene scene)
    {
        if (PlayerOf(player).AreaId != DeepwaterModule.HubId || Environment.GetEnvironmentVariable("Game_STASH") == "0") return;

        if (player.StashObject != 0) scene.Stashes[player.StashObject] = player.StashAt;
        if (player.GuildStashObject != 0) scene.GuildStashes[player.GuildStashObject] = player.GuildStashAt;

        var lines = player.Layout.Level?.RoomObjects ?? [];
        var belowStash = lines.FirstOrDefault(o => o.Type == StashObject.MetadataPath
            && (Math.Abs(o.X - player.StashAt.X) > 4 || Math.Abs(o.Y - player.StashAt.Y) > 4));
        var (sx, sy, srot) = belowStash is not null ? (belowStash.X, belowStash.Y, belowStash.Rotation) : (163, 165, 1.26327f);
        uint stashId = player.TakeObjectId();
        byte[] stashCreate = InstanceEntities.CreateObject(stashId, InstanceEntities.ObjectTypeHash(StashObject.MetadataPath),
            new StashObject(sx, sy, srot).Blob());
        if (await global::Game.SendWorldCreate(PlayerOf(player), stashId, stashCreate,
            $"0x02bb create below-deck stash 0x{stashId:x} at ({sx}, {sy})"))
        {
            await global::Game.SendWorldRecord(PlayerOf(player), stashId, InstanceProtocol.EnterWakeZone(stashId));
            await RegisterStationaryCollision(player, stashId, StashObject.MetadataPath, sx, sy, srot);
            scene.Stashes[stashId] = (sx, sy);
            if (player.StashObject == 0) (player.StashObject, player.StashAt) = (stashId, (sx, sy));
        }

        if (!player.Character.Ssf)
        {
            var belowGuild = lines.FirstOrDefault(o => o.Type == StashObject.GuildMetadataPath
                && (Math.Abs(o.X - player.GuildStashAt.X) > 4 || Math.Abs(o.Y - player.GuildStashAt.Y) > 4));
            var (gx, gy, grot) = belowGuild is not null ? (belowGuild.X, belowGuild.Y, belowGuild.Rotation) : (170, 176, 0.922353f);
            uint guildId = player.TakeObjectId();
            byte[] guildCreate = InstanceEntities.CreateObject(guildId, InstanceEntities.ObjectTypeHash(StashObject.GuildMetadataPath),
                new StashObject(gx, gy, grot).Blob());
            if (await global::Game.SendWorldCreate(PlayerOf(player), guildId, guildCreate,
                $"0x02bb create below-deck guild stash 0x{guildId:x} at ({gx}, {gy})"))
            {
                await global::Game.SendWorldRecord(PlayerOf(player), guildId, InstanceProtocol.EnterWakeZone(guildId));
                await RegisterStationaryCollision(player, guildId, StashObject.GuildMetadataPath, gx, gy, grot);
                scene.GuildStashes[guildId] = (gx, gy);
                if (player.GuildStashObject == 0) (player.GuildStashObject, player.GuildStashAt) = (guildId, (gx, gy));
            }
        }
        module.Log($"{PlayerOf(player).Character}: Sovereign stashes registered ({scene.Stashes.Count} stash, {scene.GuildStashes.Count} guild stash)");
    }

    /// <summary>Click-swap for Sovereign deck &amp; below-deck Stash / GuildStash (exact <c>Rules.LabyrinthStash</c> pattern).
    /// When this returns true, the caller returns <c>false</c> from <c>GameEvents.ObjectUse</c> so <c>Game.OpenStash</c> /
    /// <c>Game.OpenGuildStash</c> opens the clicked stash natively.</summary>
    public static bool PickHubStash(Player player, uint targetId)
    {
        if (!Scenes.TryGetValue(player, out var scene)) return false;
        if (scene.Stashes.TryGetValue(targetId, out var at))
        {
            (player.StashObject, player.StashAt) = (targetId, at);
            return true;
        }
        if (scene.GuildStashes.TryGetValue(targetId, out var gat))
        {
            (player.GuildStashObject, player.GuildStashAt) = (targetId, gat);
            return true;
        }
        return false;
    }

    static (int X, int Y, float Rot) FindPlacement(IReadOnlyList<GameServer.Data.Terrain.Generation.PlacedTileObject> lines,
        string type, int fallbackX, int fallbackY, float fallbackRot)
    {
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Type == type) return (lines[i].X, lines[i].Y, lines[i].Rotation);
        return (fallbackX, fallbackY, fallbackRot);
    }

    // ---- NPCTalk Side-Effects (Q209–211, Auto-Lanterns, Tutorial Rings & Fallback Offers) ----

    /// <summary><c>GameEvents.NpcOptionPicked</c> for Deepwater NPCs (789–795): handles Auto-Lanterns toggle (#3950/3951,
    /// #3978/3979), Valerie #792 "Craft" (#3970), tutorial ring offers (#132, #134), fallback quest-item offers (#137, #138),
    /// "The Allflame's Power" (#3964/#3972), "Velka's Allflame" (#3975), and Vesper's "Velka's Barrier" (#3911) buff cue.
    /// Returns false when handled here, true to pass through to <c>RunNpcOption</c>.</summary>
    public static async Task<bool> OnNpcOption(IModuleContext module, Player player, NpcCatalog.Talk talk,
        bool isHubOwner, Func<Player, Task>? onVesperAwoke = null)
    {
        if (talk.Npc is < ValerieRow or > VesperCraftingRow) return true;
        var modPlayer = PlayerOf(player);

        // 1. Auto-Lanterns toggle (NPCTalkCategory RightMenu: #3950/#3978 enable, #3951/#3979 disable).
        if (talk.Row is 3950 or 3978)
        {
            await SetLeagueQuestFlags(player, [AutoLanternsLqf]);
            module.Log($"{modPlayer.Character} <= NPCTalk {talk.Row} \"{talk.Option}\": Auto-Lanterns enabled (LQF {AutoLanternsLqf})");
            await modPlayer.Send(InstanceNpcs.Talk(player.TalkingTo), $"0x0084 npc 0x{player.TalkingTo:x}: options refreshed (Auto-Lanterns on)");
            return false;
        }
        if (talk.Row is 3951 or 3979 || talk.Script.Contains("DeepwaterAutoLanternsActive", StringComparison.Ordinal))
        {
            await SetLeagueQuestFlags(player, [AutoLanternsLqf], on: false);
            module.Log($"{modPlayer.Character} <= NPCTalk {talk.Row} \"{talk.Option}\": Auto-Lanterns disabled (LQF {AutoLanternsLqf} cleared)");
            await modPlayer.Send(InstanceNpcs.Talk(player.TalkingTo), $"0x0084 npc 0x{player.TalkingTo:x}: options refreshed (Auto-Lanterns off)");
            return false;
        }

        // 2. Valerie #792 "Craft" (#3970): opens DeepwaterCraftingWindow (0x69) during the Spirit Flame crafting tutorial.
        if (talk.Npc == ValerieCraftingTutorialRow && talk.Script.Contains("DeepwaterCraftingWindow", StringComparison.Ordinal))
        {
            if (!isHubOwner || ItemsOf(player).Items(DeepwaterCrafting.OutputRow).Count > 0 || player.Character.HasQuestFlag(DeepwaterCrafting.CraftInProgress))
            {
                string why = !isHubOwner ? "not the hub owner" : "unclaimed Ghostly Copies are waiting in the display cases";
                module.Log($"{modPlayer.Character} <= Valerie NPCTalk {talk.Row} \"{talk.Option}\": refused ({why})");
                return false;
            }
            if (!ClientHasQuestFlag(player, CraftSeenFlag))
                await SetQuestFlags(player, [CraftSeenFlag]);
            ClearTalk(player);
            await modPlayer.Send(InstanceNpcs.Talk(0), $"0x0084 object 0: Valerie's dialogue ends (NPCTalk {talk.Row})");
            await modPlayer.Send(InstanceWaypoints.OpenUi(DeepwaterCrafting.CraftingUi),
                $"0x007b open DeepwaterCraftingWindow (kind 0x{DeepwaterCrafting.CraftingUi:x2}, NPCTalk {talk.Row})");
            return false;
        }

        // 3. Normal ring tutorial offers (#132 Coral Ring ilvl 11, #134 Iron/Paua Ring ilvl 3):
        //    QuestRewards has Level > 1 with Rarity = null, which QuestRewardCatalog.Create omits; open the native 0x600 window directly.
        if (talk.RewardOffer is OfferVesperRing or OfferTutorialRing)
            return await OpenNormalRingOffer(module, player, talk, talk.RewardOffer.Value);

        // 4. Fallback quest-item offers (#137 Velka's Allflame, #138 Sulphur Purifier):
        //    Deepwater quest items have no .it quest_grant_flag (only QuestItems.dat TriggeredQuestFlag), so QuestRewardCatalog.Taken
        //    treats them as already taken; grant directly into carried inventory instead.
        if (talk.RewardOffer == OfferVelkaAllflame)
            return await GrantFallbackQuestItem(module, player, talk, VelkaAllflame, HaveBossAllflameFlag,
                requiredLqf: VelkaKilledLqf, refusedLqf: VelkaFlameGivenLqf,
                setLqfs: [VelkaAllflameFallbackSeenLqf]);
        if (talk.RewardOffer == OfferSulphurPurifierFallback)
            return await GrantFallbackQuestItem(module, player, talk, UpgradePieces[1], UpgradeHeldFlags[1],
                requiredLqf: DeepwaterConstants.FirstVoyageLeagueFlag, refusedLqf: 1825,
                setLqfs: [PurifierFallbackSeenLqf]);

        // 5. "The Allflame's Power" (#3964 on #792, #3972 on #791): consume Fairgraves' Allflame, complete Q210 (LQF 1859),
        //    unlock Vesper's tutorial rewards (LQF 1891), play give_flame = 1 + Vesper cansee = 1 + Stage1 disable_targetable = 1.
        if (talk.Row is 3964 or 3972)
        {
            if (HasLeagueFlag(player, VesperFlameGivenLqf))
            {
                module.Log($"{modPlayer.Character} <= NPCTalk {talk.Row} \"{talk.Option}\": refused (Vesper already awakened)");
                return false;
            }
            await RemoveCarriedQuestItem(player, FairgravesAllflameHash, "handed in to awaken Vesper");
            if (ClientHasQuestFlag(player, HaveAllflameFlag))
                await SetQuestFlags(player, [HaveAllflameFlag], on: false);
            await SetLeagueQuestFlags(player, [VesperFlameGivenLqf, ReturnTheFavourLqf]);
            ClearTalk(player);
            await modPlayer.Send(InstanceNpcs.Talk(0), $"0x0084 object 0: The Allflame's Power (NPCTalk {talk.Row})");
            if (Scenes.TryGetValue(player, out var scene))
            {
                if (scene.BelowValerieId != 0 && scene.BelowValerieNpc?.Row == ValerieAfterFairgravesRow)
                    scene.BelowValerieNpc = await SendNpcState(player, scene.BelowValerieId, scene.BelowValerieNpc, "give_flame", 1);
                if (scene.VesperId != 0 && scene.VesperNpc is not null)
                    scene.VesperNpc = await SendNpcState(player, scene.VesperId, scene.VesperNpc, "cansee", 1);
            }
            if (onVesperAwoke is not null) await onVesperAwoke(player);
            module.Log($"{modPlayer.Character} <= NPCTalk {talk.Row} \"{talk.Option}\": Q210 complete (LQF {VesperFlameGivenLqf}, {ReturnTheFavourLqf}), Vesper awakened");
            return false;
        }

        // 6. "Velka's Allflame" (#3975 on ValerieVesperRevive #793): consume Velka's Allflame, complete Q211 (LQF 1885, 1888),
        //    play Valerie revive_vesper = 1 beam + CorpseBattery open = 1 tank animation.
        if (talk.Row == 3975)
        {
            if (HasLeagueFlag(player, VelkaFlameGivenLqf))
            {
                module.Log($"{modPlayer.Character} <= NPCTalk {talk.Row} \"{talk.Option}\": refused (Vesper already revived)");
                return false;
            }
            await RemoveCarriedQuestItem(player, VelkaAllflameHash, "handed in to revive Vesper");
            if (ClientHasQuestFlag(player, HaveBossAllflameFlag))
                await SetQuestFlags(player, [HaveBossAllflameFlag], on: false);
            await SetLeagueQuestFlags(player, [VelkaFlameGivenLqf, VesperRevivedLqf]);
            ClearTalk(player);
            await modPlayer.Send(InstanceNpcs.Talk(0), $"0x0084 object 0: Velka's Allflame (NPCTalk {talk.Row})");
            if (Scenes.TryGetValue(player, out var scene))
            {
                if (scene.DeckValerieId != 0 && scene.DeckValerieNpc?.Row == ValerieVesperReviveRow)
                    scene.DeckValerieNpc = await SendNpcState(player, scene.DeckValerieId, scene.DeckValerieNpc, "revive_vesper", 1);
                if (scene.CorpseBatteryId != 0 && CorpseBatteryStates is { } batteryStates
                    && QuestObjectTypes.Find(CorpseBattery) is { } batteryInfo)
                {
                    int idx = batteryInfo.Components.ToList().IndexOf("StateMachine");
                    if (idx >= 0)
                        await global::Game.SendWorldRecord(modPlayer, scene.CorpseBatteryId,
                            InstanceEntities.StateMachineUpdate(scene.CorpseBatteryId, (byte)idx, batteryStates.Set("open", 1)),
                            $"0x02bc CorpseBattery 0x{scene.CorpseBatteryId:x} open = 1");
                }
            }
            module.Log($"{modPlayer.Character} <= NPCTalk {talk.Row} \"{talk.Option}\": Q211 complete (LQF {VelkaFlameGivenLqf}, {VesperRevivedLqf}), Vesper revived");
            return false;
        }

        // 7. Vesper "Velka's Barrier" (#3911): pass through so RunNpcOption sets QF 3610 + LQF 1889 & plays dialogue,
        //    plus grant optional 2s VelkaFightableBuff (BuffDefinitions#2799) cue.
        if (talk.Row == 3911)
        {
            ushort buff = player.NextBuff++;
            var instance = player.Instance;
            await global::Game.SendWorldRecord(player, player.Id, InstanceEntities.AddBuff(player.Id, buff, VelkaFightableBuff),
                $"0x01c1 VelkaFightableBuff ({VelkaFightableBuff}) from Vesper NPCTalk 3911");
            _ = Task.Run(async () =>
            {
                try
                {
                    await AreaDelay(player, 2.0);
                    if (!player.Closed && !player.LeavingArea && player.Instance == instance)
                        await global::Game.SendWorldRecord(player, player.Id, InstanceEntities.RemoveBuff(player.Id, buff),
                            "0x01c2 VelkaFightableBuff ends");
                }
                catch { }
            });
            return true;
        }

        return true;
    }

    static async Task<bool> OpenNormalRingOffer(IModuleContext module, Player player, NpcCatalog.Talk talk, int offer)
    {
        var modPlayer = PlayerOf(player);
        bool alreadyTaken = offer == OfferVesperRing
            ? HasLeagueFlag(player, VesperRingTakenLqf)
            : ClientHasQuestFlag(player, TutorialRingTakenFlag) || HasLeagueFlag(player, TutorialRingTakenLqf);
        if (alreadyTaken)
        {
            module.Log($"{modPlayer.Character} <= NPCTalk {talk.Row} offer {offer}: refused (already taken)");
            return false;
        }

        var rewardCatalog = QuestRewardCatalog.Load(hardMode: LeagueFor(player).HardMode, supportGems: LeagueFor(player).Rules.SupportGemRewards);
        var choices = rewardCatalog.Choices(offer, player.Character.ClassRow);
        var catalog = ItemsFor(player);
        var rng = AreaRng(player);
        var rewardItems = new List<(uint Id, Item Item)>();
        foreach (var c in choices)
        {
            if (catalog.Row(c.ItemRow) is not { Supported: true } type) continue;
            byte level = (byte)Math.Clamp(c.Level, 1, 100);
            var item = ItemCatalog.Create(type, random: rng) with
            {
                ItemLevel = level,
                Rarity = ItemRarity.Normal,
                Identified = true,
            };
            rewardItems.Add((c.Id, ResolveRewardItem(player, offer, item, type)));
        }
        if (rewardItems.Count == 0) return false;

        var setLqfs = talk.LeagueFlagsSet.Where(f => f is > 1 and <= Quests.League.Count).ToList();
        if (talk.StartLeagueFlag is int slf && !setLqfs.Contains(slf)) setLqfs.Add(slf);
        if (setLqfs.Count > 0) await SetLeagueQuestFlags(player, setLqfs);
        if (talk.StartFlag is { } sf && !ClientHasQuestFlag(player, sf))
            await SetQuestFlags(player, [sf]);

        player.InNpcTalk = false;
        player.NpcRewardOffer = offer;
        player.NpcRewardItems = rewardItems.ToDictionary(x => x.Id, x => x.Item);
        await modPlayer.Send(InstanceInventories.AddInventory(0x600, NpcShops.Tab, NpcShops.Manager,
            QuestRewardCatalog.Arrange(rewardItems, catalog)),
            $"0x00c0 quest reward inventory: offer {offer}, {rewardItems.Count} ring choice(s)");
        await modPlayer.Send(InstanceNpcs.OpenQuestReward(0x600, checked((ushort)offer)),
            $"0x0087 reward inventory 0x600: quest reward offer {offer}");
        return false;
    }

    static async Task<bool> GrantFallbackQuestItem(IModuleContext module, Player player, NpcCatalog.Talk talk,
        string itemId, string questFlag, int requiredLqf, int refusedLqf, int[] setLqfs)
    {
        var modPlayer = PlayerOf(player);
        var catalog = ItemsFor(player);
        if (!HasLeagueFlag(player, requiredLqf) || HasLeagueFlag(player, refusedLqf)
            || catalog.Find(itemId) is not { Supported: true } type)
        {
            module.Log($"{modPlayer.Character} <= NPCTalk {talk.Row} \"{talk.Option}\": refused (not eligible)");
            return false;
        }
        var items = ItemsOf(player);
        if (QuestItems.Carried(items).Any(c => c.Item.Item.TypeHash == type.Hash))
        {
            module.Log($"{modPlayer.Character} <= NPCTalk {talk.Row} \"{talk.Option}\": refused (already carried)");
            await SyncQuestItemFlags(player);
            ClearTalk(player);
            await modPlayer.Send(InstanceNpcs.Talk(0), $"0x0084 object 0: {talk.Option} (already carried)");
            return false;
        }

        var item = ItemCatalog.Create(type, random: AreaRng(player));
        var shape = ShapeOf(item) ?? new(type.Width, type.Height, type.Class);
        byte destRow = PlayerInventories.MainInventory;
        var cell = items.FreeCell(destRow, shape.Width, shape.Height, ShapeOf);
        if (cell is null && ExpandedInventoryGranted(player))
        {
            destRow = PlayerInventories.ExpandedInventory;
            cell = items.FreeCell(destRow, shape.Width, shape.Height, ShapeOf);
        }
        if (cell is not { } dest)
        {
            await modPlayer.Send(InstanceChat.ChatLine($"Make room in your inventory to take {type.Name}."),
                "0x0017 chat line: inventory full for fallback quest item");
            ClearTalk(player);
            await modPlayer.Send(InstanceNpcs.Talk(0), $"0x0084 object 0: {talk.Option} (no room)");
            return false;
        }

        InventoryItem? placed = null;
        Saves.Atomically(() =>
        {
            placed = items.Put(destRow, new InventoryItem(items.NewItemId(), dest.X, dest.Y, item));
        });
        if (placed is not null)
            await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(destRow), items.NextRevision(destRow), [], [placed]),
                $"0x00bf inventory {PlayerInventories.InventoryId(destRow)}: granted fallback {type.Name} ({placed.Id})");
        if (!ClientHasQuestFlag(player, questFlag))
            await SetQuestFlags(player, [questFlag]);
        if (setLqfs.Length > 0)
            await SetLeagueQuestFlags(player, setLqfs);
        ClearTalk(player);
        await modPlayer.Send(InstanceNpcs.Talk(0), $"0x0084 object 0: {talk.Option} granted");
        module.Log($"{modPlayer.Character} <= NPCTalk {talk.Row} \"{talk.Option}\": granted {type.Name}");
        return false;
    }

    static void ClearTalk(Player player)
    {
        player.TalkingTo = 0;
        player.InNpcTalk = false;
        player.NpcFinishFlag = null;
        player.NpcFinishLeagueFlag = null;
        player.NpcTalkScript = null;
    }

    static async Task<NpcCatalog.Npc> SendNpcState(Player player, uint id, NpcCatalog.Npc npc, string stateName, int value)
    {
        if (id == 0 || npc.Components is null || npc.StateMachine is not { } sm) return npc;
        int idx = npc.Components.ToList().IndexOf("StateMachine");
        if (idx < 0) return npc;
        var updated = sm.Set(stateName, value);
        await global::Game.SendWorldRecord(PlayerOf(player), id,
            InstanceEntities.StateMachineUpdate(id, (byte)idx, updated),
            $"0x02bc {npc.Id.Split('/')[^1]} 0x{id:x} {stateName} = {value}");
        return npc with { StateMachine = updated };
    }

    /// <summary>Atomically removes every carried copy of <paramref name="typeHash"/> across player inventories and sends
    /// <c>0x00bf</c> for each modified row.</summary>
    public static async Task<int> RemoveCarriedQuestItem(Player player, uint typeHash, string reason)
    {
        var items = ItemsOf(player);
        var removedByRow = new Dictionary<byte, List<uint>>();
        Saves.Atomically(() =>
        {
            foreach (var (row, it) in QuestItems.Carried(items).Where(c => c.Item.Item.TypeHash == typeHash).ToList())
            {
                if (items.Remove(row, it.Id) is null) continue;
                if (!removedByRow.TryGetValue(row, out var ids)) removedByRow[row] = ids = [];
                ids.Add(it.Id);
            }
        });
        int total = 0;
        var modPlayer = PlayerOf(player);
        foreach (var (row, ids) in removedByRow)
        {
            total += ids.Count;
            await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), ids, []),
                $"0x00bf inventory {PlayerInventories.InventoryId(row)}: removed {ids.Count} quest item(s) ({reason})");
        }
        return total;
    }

    // ---- GameEvents.RewardItem for Offers#131–138 ----

    /// <summary><c>GameEvents.RewardItem</c>: normalizes Deepwater tutorial reward items (Offer#131 100 Sulphur, Offer#132
    /// Normal ilvl 11 Coral Ring, Offer#134 Normal ilvl 3 Iron/Paua Ring, Offer#136 pity Sulphur).</summary>
    public static Item ResolveRewardItem(Player player, int offer, Item item, ItemCatalog.BaseType type) => offer switch
    {
        OfferVesperRing => item with { Rarity = ItemRarity.Normal, ItemLevel = 11, Identified = true },
        OfferTutorialRing => item with { Rarity = ItemRarity.Normal, ItemLevel = 3, Identified = true },
        OfferVesperSulphur => item with { StackSize = Math.Max(item.StackSize, (ushort)100) },
        OfferPitySulphur => item with { StackSize = Math.Max(item.StackSize, (ushort)50) },
        _ => item,
    };

    // ---- Quest-Item Flag Sync & MessagesHandled ----

    /// <summary>Direct 4-row check (Main, Expanded, Cursor, Collection; zero LINQ) for a Deepwater quest item.</summary>
    public static bool CarriesQuestItem(Player player, uint typeHash) => FindCarriedQuestItem(player, typeHash) is not null;

    /// <summary>Finds the first carried copy of <paramref name="typeHash"/> across Main, Expanded, Cursor, and
    /// Collection rows without scanning all 118 player inventories.</summary>
    public static (byte Row, InventoryItem Item)? FindCarriedQuestItem(Player player, uint typeHash)
    {
        var items = ItemsOf(player);
        for (int r = 0; r < QuestItemRows.Length; r++)
        {
            byte row = QuestItemRows[r];
            var list = items.Items(row);
            for (int i = 0; i < list.Count; i++)
                if (list[i].Item.TypeHash == typeHash) return (row, list[i]);
        }
        return null;
    }

    /// <summary>Notes a clicked Deepwater quest item on the ground so <see cref="TickQuestPickups"/> syncs its quest
    /// flags once the pickup actually completes into inventory (never prematurely on cancelled walk or full bag).</summary>
    public static void NoteQuestPickup(Player player, uint groundId)
    {
        var list = PendingQuestPickups.GetValue(player, _ => []);
        double until = AreaTime(player) + QuestPickupWaitSeconds;
        lock (list)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].GroundId == groundId) return;
            list.Add((groundId, until));
            Interlocked.Increment(ref _pendingQuestPickupCount);
        }
    }

    /// <summary><c>GameEvents.AreaTick</c>: syncs Deepwater quest-item flags when a noted ground quest item disappears
    /// (picked up). Zero allocations and O(1) volatile check when no quest pickup is in flight.</summary>
    public static Task TickQuestPickups(Player player)
    {
        if (Volatile.Read(ref _pendingQuestPickupCount) == 0 || !PendingQuestPickups.TryGetValue(player, out var list))
            return Task.CompletedTask;
        if (player.Closed || player.LeavingArea || player.Instance is not { } instance)
            return Task.CompletedTask;
        double now = AreaTime(player);
        bool sync = false;
        lock (list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var (groundId, until) = list[i];
                bool gone = instance.Ground.Get(groundId) is null;
                if (gone || now > until)
                {
                    list.RemoveAt(i);
                    Interlocked.Decrement(ref _pendingQuestPickupCount);
                    if (gone) sync = true;
                }
            }
        }
        return sync ? SyncQuestItemFlags(player) : Task.CompletedTask;
    }

    /// <summary><c>GameEvents.PlayerLeaving</c>: clears pending ground quest-item pickups for <paramref name="player"/>.</summary>
    public static void ForgetQuestPickups(Player player)
    {
        if (Volatile.Read(ref _pendingQuestPickupCount) == 0 || !PendingQuestPickups.TryGetValue(player, out var list))
            return;
        lock (list)
        {
            if (list.Count == 0) return;
            Interlocked.Add(ref _pendingQuestPickupCount, -list.Count);
            list.Clear();
        }
    }

    /// <summary>Syncs Deepwater quest-item flags (<c>QF 3583</c>, <c>QF 3607</c>, <c>QF 3563–3567</c>, and
    /// <c>QF 3631</c>) in a single direct loop over Main, Expanded, Cursor, and Collection rows (zero LINQ). Sends zero
    /// packets when nothing changed.</summary>
    public static async Task SyncQuestItemFlags(Player player)
    {
        if (player.Closed || player.LeavingArea) return;
        var items = ItemsOf(player);
        bool hasAllflame = false, hasBossAllflame = false;
        int upgradeMask = 0;

        for (int r = 0; r < QuestItemRows.Length; r++)
        {
            var list = items.Items(QuestItemRows[r]);
            for (int j = 0; j < list.Count; j++)
            {
                uint h = list[j].Item.TypeHash;
                if (h == FairgravesAllflameHash) hasAllflame = true;
                else if (h == VelkaAllflameHash) hasBossAllflame = true;
                else
                {
                    for (int i = 0; i < UpgradePieceHashes.Length; i++)
                        if (h == UpgradePieceHashes[i]) { upgradeMask |= 1 << i; break; }
                }
            }
        }

        List<string>? setQf = null, clearQf = null;
        void SyncQf(string flag, bool want)
        {
            bool cur = ClientHasQuestFlag(player, flag);
            if (want && !cur) (setQf ??= []).Add(flag);
            else if (!want && cur) (clearQf ??= []).Add(flag);
        }

        SyncQf(HaveAllflameFlag, hasAllflame && !HasLeagueFlag(player, VesperFlameGivenLqf));
        SyncQf(HaveBossAllflameFlag, hasBossAllflame && !HasLeagueFlag(player, VelkaFlameGivenLqf));
        // QF 3631 (HaveDeepwaterQuestDucat3) is owned exclusively by DeepwaterCursedDucats.SyncFlags (which scans rows 115/116 too).
        for (int i = 0; i < UpgradeHeldFlags.Length; i++)
            SyncQf(UpgradeHeldFlags[i], (upgradeMask & (1 << i)) != 0
                && !HasLeagueFlag(player, DeepwaterUpgrades.TurnInLeagueFlags[i]));

        if (setQf is null && clearQf is null) return;
        if (setQf is not null) await SetQuestFlags(player, setQf);
        if (clearQf is not null) await SetQuestFlags(player, clearQf, on: false);
    }

    /// <summary><c>GameEvents.MessagesHandled</c>: zero-allocation fast-exit on normal messages; syncs quest-item flags on
    /// item-moving opcodes and refreshes below-deck Valerie on hub NPC dialogue / crafting actions.</summary>
    public static Task OnMessagesHandled(IModuleContext module, Player player, IReadOnlyList<ushort> ids)
    {
        bool checkItems = false, checkHub = false;
        bool inHub = PlayerOf(player).AreaId == DeepwaterModule.HubId;
        for (int i = 0; i < ids.Count; i++)
        {
            ushort id = ids[i];
            if (!checkItems && MayChangeQuestItems(id)) checkItems = true;
            if (inHub && !checkHub && id is InstanceNpcs.OptionId or InstanceNpcs.DialogueClosedId or InstanceRulesets.ActionId)
                checkHub = true;
        }
        if (!checkItems && !checkHub) return Task.CompletedTask;
        return HandleMessagesAsync(module, player, checkItems, checkHub);
    }

    static async Task HandleMessagesAsync(IModuleContext module, Player player, bool checkItems, bool checkHub)
    {
        if (checkItems) await SyncQuestItemFlags(player);
        if (checkHub) await RefreshHubNpcs(module, player);
    }

    static bool MayChangeQuestItems(ushort id) => id is
        InstanceItems.PickUpItemId or
        InstanceItems.PlaceItemId or
        InstanceItems.DropItemId or
        InstanceItems.ItemActionId or
        InstanceItems.CollectionTakeId or
        InstanceItems.CommandId or
        InstanceNpcs.BuyId or
        InstanceNpcs.CtrlBuyId or
        InstanceNpcs.TakeAllRewardsId or
        InstanceChat.ChatId
        || QuestItems.MovesItems(id);
}
