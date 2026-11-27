using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Atlas;

/// <summary>
/// 3.28/3.29 Astrolabes (<c>BaseItemTypes 5382–5392</c>), Shaped Regions (<c>AtlasMissionTypes</c> 11 rows,
/// <c>S→C 0x01a3</c> section <c>0x10</c> BE, <c>C→S 0x0075</c>/<c>0x0076</c>), and the 4 Memory Vaults
/// (<c>MapWorldsMemoryVault1..4</c>, <c>ThreadsOfTheOriginator</c> Part B, states 0–20).
/// </summary>
public static class AstrolabesAndVaults
{
    public const byte AtlasMemoryRulesetRow = 160;

    public const string EagonTemplarVaultPath = "Metadata/NPC/Epilogue/EagonTemplarVault";
    public const string TemplarVaultDisplayPath = "Metadata/Chests/TemplarVaultDisplay/TemplarVaultRewardDisplay";

    // LeagueQuestFlags for Part B (Astrolabes & Memory Vaults)
    public const int LqfAstrolabeRewardTaken = 714;
    public const int LqfAstrolabeRewardSeen = 715;
    public const int LqfShapedRegionStarted = 716;
    public const int LqfFirstShapedRegionCompleted = 1686;
    public const int LqfTemplarVaultReaction = 1687;
    public const int LqfAllVaultsWitnessed = 1692;
    public const int LqfZanasPlanSeen = 1693;
    public const int LqfOurNextStepsSeen = 1694;
    public const int LqfVaultAvailable = 1717;
    public const int LqfVaultActive = 1759;
    public const int LqfVaultPortalsSeen = 1760;
    public const int LqfAnyVaultWitnessed = 1755;
    public const int LqfTwoVaultsWitnessed = 1756;
    public const int LqfThreeVaultsWitnessed = 1757;

    // Per-vault dialogue & visited LQFs + transient area QFs
    public sealed record VaultDef(
        int Index, string AreaId, string Name, string TransientQf,
        int DialogueLqf, int VisitedLqf, string GlyphNpcId);

    public static readonly IReadOnlyList<VaultDef> Vaults =
    [
        new(0, "MapWorldsMemoryVault1", "Machinist's Vault", "InMemoryVaultIsla",     710, 1701, "Metadata/NPC/Info/GlyphTemplarVaultIsla"),
        new(1, "MapWorldsMemoryVault2", "Zealot's Vault",    "InMemoryVaultVenarius", 711, 1703, "Metadata/NPC/Info/GlyphTemplarVaultVenarius"),
        new(2, "MapWorldsMemoryVault3", "Arkhon's Vault",    "InMemoryVaultValdo",    712, 1704, "Metadata/NPC/Info/GlyphTemplarVaultValdo"),
        new(3, "MapWorldsMemoryVault4", "Scholar's Vault",   "InMemoryVaultZana",     713, 1705, "Metadata/NPC/Info/GlyphTemplarVaultZana"),
    ];

    public sealed record MissionTypeInfo(
        byte Row, string Id, int AstrolabeBaseRow, string AstrolabeItemId,
        IReadOnlyList<(uint StatWireKey, int Value)> Stats, int MinNodes, int MaxNodes);

    public sealed record SavedShapedRegion(
        byte QuadrantId, byte MissionTypeRow, List<string> RemainingNodeIds,
        ushort ModHash16, int[] ModStatValues);

    public enum NodeApplyKind { None, Astrolabe, MemoryThread }

    public readonly record struct NodeApplyOutcome(
        NodeApplyKind Kind, byte QuadrantId = 0, byte MissionTypeRow = 0, int UnlockLqf = 0, string Label = "");

    const string ShapedRegionsKey = "atlas.shapedRegions";
    const string VaultCursorKey = "atlas.vaultCursor";

    static Dictionary<string, MissionTypeInfo> _byAstrolabeId = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<byte, MissionTypeInfo> _byMissionRow = [];
    static Dictionary<string, int> _memoryThreadRows = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, (ushort Hash16, string Name, int Level)> _vaultAreas = new(StringComparer.OrdinalIgnoreCase);
    static (ushort Hash16, int[] StatValues)? _defaultShapedMod;

    sealed class VaultInstanceState
    {
        public bool Spawned;
        public uint EagonId;
        public readonly Dictionary<uint, (int X, int Y, Item Reward, bool Opened)> Displays = [];
        public bool RewardClaimed;
    }

    static readonly Store<InstanceState, VaultInstanceState> VaultInstances = new();
    static readonly string[] FlagNone = [];

    public static IReadOnlyDictionary<string, MissionTypeInfo> Astrolabes => _byAstrolabeId;

    public static void LoadData()
    {
        var baseIdByRow = new Dictionary<int, string>();
        var threadRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (ClientTables.Has("BaseItemTypes"))
        {
            int idx = 0;
            foreach (var row in ClientTables.Table("BaseItemTypes"))
            {
                string id = row.Text("Id") ?? "";
                if (id.Length > 0)
                {
                    baseIdByRow[idx] = id;
                    if (id is "Metadata/Items/Currency/FearMemoryThread"
                        or "Metadata/Items/Currency/BenevolenceMemoryThread"
                        or "Metadata/Items/Currency/IgnoranceMemoryThread")
                        threadRows[id] = idx;
                }
                idx++;
            }
        }
        _memoryThreadRows = threadRows;

        var byId = new Dictionary<string, MissionTypeInfo>(StringComparer.OrdinalIgnoreCase);
        var byRow = new Dictionary<byte, MissionTypeInfo>();
        if (ClientTables.Has("AtlasMissionTypes"))
        {
            int idx = 0;
            foreach (var row in ClientTables.Table("AtlasMissionTypes"))
            {
                string id = row.Text("Id") ?? $"Mission{idx}";
                int astrolabeRow = row.IntOrNull("Astrolabe") ?? -1;
                string itemId = baseIdByRow.GetValueOrDefault(astrolabeRow, "");
                int[] statRows = [.. row.Ints("Stats")];
                int[] statVals = [.. row.Ints("StatValues")];
                var stats = new List<(uint, int)>();
                for (int i = 0; i < Math.Min(statRows.Length, statVals.Length); i++)
                    if (statRows[i] >= 0) stats.Add(((uint)statRows[i] + 1, statVals[i]));
                int minNodes = Math.Clamp(row.IntOrNull("__col4") ?? 6, 3, 12);
                int maxNodes = Math.Clamp(row.IntOrNull("__col5") ?? 9, minNodes, 15);
                var info = new MissionTypeInfo((byte)idx, id, astrolabeRow, itemId, stats, minNodes, maxNodes);
                byRow[(byte)idx] = info;
                if (itemId.Length > 0) byId[itemId] = info;
                idx++;
            }
        }
        _byAstrolabeId = byId;
        _byMissionRow = byRow;

        var vaults = new Dictionary<string, (ushort, string, int)>(StringComparer.OrdinalIgnoreCase);
        if (ClientTables.Has("WorldAreas"))
        {
            foreach (var row in ClientTables.Table("WorldAreas"))
            {
                string id = row.Text("Id") ?? "";
                if (Vaults.Any(v => string.Equals(v.AreaId, id, StringComparison.OrdinalIgnoreCase))
                    && row.IntOrNull("HASH16") is int hash)
                    vaults[id] = ((ushort)hash, row.Text("Name") ?? id, row.IntOrNull("AreaLevel") ?? 84);
            }
        }
        _vaultAreas = vaults;

        // Pick a valid 1-stat or 2-stat MapZanaInfluenced mod for shaped-region display if present
        _defaultShapedMod = null;
        if (ClientTables.Has("Mods"))
        {
            foreach (var row in ClientTables.Table("Mods"))
            {
                if (!string.Equals(row.Text("Id"), "MapZanaInfluenced", StringComparison.Ordinal)) continue;
                ushort hash = (ushort)(row.IntOrNull("HASH16") ?? 0);
                if (hash == 0) break;
                var vals = new List<int>();
                for (int s = 1; s <= 6; s++)
                {
                    if (row.IntOrNull($"StatsKey{s}") is >= 0)
                        vals.Add(row.IntOrNull($"Stat{s}Min") ?? 1);
                }
                _defaultShapedMod = (hash, [.. vals]);
                break;
            }
        }
    }

    public static void Register(string owner)
    {
        GameEvents.AreaQuestFlags.Add(owner, TransientVaultFlags);
        GameEvents.EnterMessages.Add(owner, VaultEnterMessages);
        GameEvents.MapOpening.Add(owner, OnMapOpening);
        GameEvents.PlayerEntered.Add(owner, OnPlayerEntered);
        GameEvents.ObjectUse.Add(owner, OnObjectUse);
        GameEvents.NpcOption.Add(owner, OnNpcOption);
        GameEvents.NpcOptionPicked.Add(owner, OnNpcOptionPicked);
    }

    static IReadOnlyList<string> TransientVaultFlags(Player player)
    {
        var vault = Vaults.FirstOrDefault(v => string.Equals(v.AreaId, player.Layout.Id, StringComparison.OrdinalIgnoreCase));
        return vault is not null ? [vault.TransientQf, MemoryTears.QfIsMapOwner] : FlagNone;
    }

    static IReadOnlyList<(string Log, byte[] Message)> VaultEnterMessages(Player player)
    {
        if (!Vaults.Any(v => string.Equals(v.AreaId, player.Layout.Id, StringComparison.OrdinalIgnoreCase)))
            return [];
        return [($"0x001f ruleset AtlasMemoryRules (row {AtlasMemoryRulesetRow})", InstanceProtocol.AddRuleset(AtlasMemoryRulesetRow))];
    }

    // ---- Shaped Regions Persistence & Wire (S→C 0x01a3 section 0x10) ---------------------------------------------------

    public static List<SavedShapedRegion> LoadShapedRegions(ISavedData data)
    {
        string? json = data.Get(ShapedRegionsKey);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<SavedShapedRegion>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static void SaveShapedRegions(ISavedData data, IReadOnlyList<SavedShapedRegion> regions) =>
        data.Change(ShapedRegionsKey, _ => JsonSerializer.Serialize(regions));

    public static IReadOnlyList<AtlasProgress.ShapedRegionWire> ShapedRegionsWire(ISavedData data)
    {
        var saved = LoadShapedRegions(data);
        if (saved.Count == 0) return [];
        var wire = new List<AtlasProgress.ShapedRegionWire>(saved.Count);
        foreach (var region in saved)
        {
            var nodes = new List<AtlasProgress.ShapedNodeWire>(region.RemainingNodeIds.Count);
            IReadOnlyList<AtlasProgress.ShapedNodeModWire> mods = region.ModHash16 != 0
                ? [new AtlasProgress.ShapedNodeModWire(region.ModHash16, region.ModStatValues)]
                : [];
            foreach (string nodeId in region.RemainingNodeIds)
                if (AtlasProgress.Find(nodeId) is { } node)
                    nodes.Add(new AtlasProgress.ShapedNodeWire(node.Hash16, mods));
            if (nodes.Count > 0)
                wire.Add(new AtlasProgress.ShapedRegionWire(region.QuadrantId, region.MissionTypeRow, nodes));
        }
        return wire;
    }

    public static Task SendShapedRegions(Player player)
    {
        var modPlayer = PlayerOf(player);
        var data = WorldOf(modPlayer).CharacterData;
        var slots = AtlasFeature.SlotsOf(modPlayer);
        var (tangle, cleansing) = AtlasFeature.PrimordialTrackerCounts(data);
        var regions = ShapedRegionsWire(data);
        return Send(player, AtlasProgress.ShapedRegionsMessage(regions, AtlasProgress.VoidstoneMask(slots), tangle, cleansing,
            AtlasProgress.ScryPairs(data)),
            $"0x01a3 Atlas shaped regions ({regions.Count} active)");
    }

    // ---- Applying Astrolabes & Memory Threads via C→S 0x0075 / 0x0076 --------------------------------------------------

    /// <summary>True when <paramref name="itemId"/> is one of the 11 Astrolabes or 3 Memory Threads applicable to Atlas nodes.</summary>
    public static bool IsAtlasNodeConsumable(string? itemId) =>
        itemId is not null && (_byAstrolabeId.ContainsKey(itemId) || _memoryThreadRows.ContainsKey(itemId));

    /// <summary>Applies an Astrolabe or Memory Thread to <paramref name="node"/> inside the atomic <c>0x0075</c>/<c>0x0076</c>
    /// consumption transaction. Returns true if accepted so the caller commits item consumption.</summary>
    public static bool TryApplyInTransaction(ISavedData data, AtlasProgress.Node node, ItemCatalog.BaseType type, out NodeApplyOutcome outcome)
    {
        outcome = default;
        if (_byAstrolabeId.TryGetValue(type.Id, out var mission))
        {
            byte quadrant = (byte)(node.Region is >= 0 and < 4 ? node.Region : 0);
            var regionNodes = AtlasProgress.Maps
                .Where(n => n.IsNormalMap && !n.RequiresSpecificItem && (n.Region == quadrant || n.Id == node.Id))
                .OrderBy(n => n.Id == node.Id ? 0 : 1)
                .ThenBy(n => n.Row)
                .Take(mission.MinNodes)
                .Select(n => n.Id)
                .ToList();
            if (regionNodes.Count == 0) regionNodes.Add(node.Id);

            var (modHash, modStats) = _defaultShapedMod ?? ((ushort)0, Array.Empty<int>());
            var regions = LoadShapedRegions(data);
            regions.RemoveAll(r => r.QuadrantId == quadrant);
            regions.Add(new SavedShapedRegion(quadrant, mission.Row, regionNodes, modHash, modStats));
            SaveShapedRegions(data, regions);
            outcome = new NodeApplyOutcome(NodeApplyKind.Astrolabe, quadrant, mission.Row, LqfShapedRegionStarted,
                $"{type.Name} shaped quadrant {quadrant + 1} ({mission.Id}, {regionNodes.Count} maps)");
            return true;
        }

        if (_memoryThreadRows.TryGetValue(type.Id, out int currencyRow))
        {
            var created = MemoryTears.CreateLineFromThread(data, node, currencyRow);
            if (created is null) return false;
            outcome = new NodeApplyOutcome(NodeApplyKind.MemoryThread, UnlockLqf: created.Value.UnlockLqf,
                Label: $"{type.Name} applied at {node.Name}");
            return true;
        }

        return false;
    }

    /// <summary>Sends protocol updates and league quest flags after an atomic <c>0x0075</c>/<c>0x0076</c> application.</summary>
    public static async Task NotifyApplied(Player player, NodeApplyOutcome outcome)
    {
        if (outcome.Kind == NodeApplyKind.Astrolabe)
        {
            if (outcome.UnlockLqf > 0 && !player.Character.LeagueQuestFlags.Contains(outcome.UnlockLqf))
                await SetLeagueQuestFlags(player, [outcome.UnlockLqf]);
            await SendShapedRegions(player);
            await PlayerOf(player).Tell($"Atlas: {outcome.Label}.");
        }
        else if (outcome.Kind == NodeApplyKind.MemoryThread)
        {
            if (outcome.UnlockLqf > 0 && !player.Character.LeagueQuestFlags.Contains(outcome.UnlockLqf))
                await SetLeagueQuestFlags(player, [outcome.UnlockLqf]);
            await MemoryTears.SendMemoryLines(player);
            await PlayerOf(player).Tell($"Atlas: {outcome.Label}.");
        }
    }

    // ---- Running & Completing Maps inside a Shaped Region --------------------------------------------------------------

    static void OnMapOpening(MapOpening request)
    {
        if (request.Campaign || request.Opening is not { Area.UsesAtlas: true } opening) return;
        if (AtlasProgress.OfArea(opening.Area.Id) is not { } node) return;
        var data = WorldOf(PlayerOf(request.Player)).CharacterData;
        var regions = LoadShapedRegions(data);
        var region = regions.FirstOrDefault(r => r.RemainingNodeIds.Contains(node.Id));
        if (region is null || !_byMissionRow.TryGetValue(region.MissionTypeRow, out var mission)) return;

        var extra = new Dictionary<uint, int>();
        foreach (var (statKey, val) in mission.Stats)
            extra[statKey] = extra.GetValueOrDefault(statKey) + val;
        extra[Stat.MonsterInAstrolabeMap] = 1;

        var content = opening.ContentData is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(opening.ContentData, StringComparer.Ordinal);
        content["atlas.shapedQuadrant"] = region.QuadrantId.ToString();
        content["atlas.shapedNode"] = node.Id;

        request.Opening = opening with
        {
            Mods = opening.Mods.WithFragmentStats(extra),
            ContentData = content,
        };
    }

    /// <summary>Advances Shaped Region progress when a map node in an active Shaped Region is completed.
    /// Completing the final map in a Shaped Region unlocks a Memory Vault (<c>LQF 1717</c>).</summary>
    public static async Task OnMapCompleted(Player player, AtlasProgress.Node node)
    {
        var data = WorldOf(PlayerOf(player)).CharacterData;
        bool regionUpdated = false;
        bool regionCompleted = false;
        byte completedQuadrant = 0;

        Saves.Atomically(() =>
        {
            var regions = LoadShapedRegions(data);
            for (int i = 0; i < regions.Count; i++)
            {
                if (!regions[i].RemainingNodeIds.Remove(node.Id)) continue;
                regionUpdated = true;
                if (regions[i].RemainingNodeIds.Count == 0)
                {
                    regionCompleted = true;
                    completedQuadrant = regions[i].QuadrantId;
                    regions.RemoveAt(i);
                }
                SaveShapedRegions(data, regions);
                break;
            }
        });

        if (!regionUpdated) return;
        await SendShapedRegions(player);

        if (regionCompleted)
        {
            var toSet = new List<int> { LqfVaultAvailable };
            if (!player.Character.LeagueQuestFlags.Contains(LqfFirstShapedRegionCompleted))
                toSet.Add(LqfFirstShapedRegionCompleted);
            var missing = toSet.Where(f => !player.Character.LeagueQuestFlags.Contains(f)).ToList();
            if (missing.Count > 0) await SetLeagueQuestFlags(player, missing);
            await PlayerOf(player).Tell($"Shaped Region (quadrant {completedQuadrant + 1}) completed! Speak to Eagon in your Hideout to open a Memory Vault.");
        }
    }

    // ---- Eagon NPCTalk 3728 (Apply Astrolabe) & 3729 (Open Memory Vault) -----------------------------------------------

    static async Task<bool> OnNpcOption(NpcOption option)
    {
        var player = option.Player;
        if (string.Equals(option.Category, "ApplyAtlasMission", StringComparison.OrdinalIgnoreCase)
            || option.Row == 3728)
        {
            await Send(player, InstanceWaypoints.OpenUi(InstanceWaypoints.AtlasScryingUi, 0),
                $"0x007b open Atlas for Astrolabe/Memory Thread application (NPCTalk {option.Row})");
            return true;
        }
        if (string.Equals(option.Category, "OpenAtlasMissionVault", StringComparison.OrdinalIgnoreCase)
            || option.Row == 3729)
        {
            await OpenNextMemoryVault(player);
            return true;
        }
        return false;
    }

    static async Task<bool> OnNpcOptionPicked(Player player, NpcCatalog.Talk talk)
    {
        // EagonTemplarVault (NPC 714) vault lore dialogues (NPCTalk 3673..3676 -> LQFs 710..713)
        if (talk.Row is >= 3673 and <= 3676)
        {
            int lqf = 710 + (talk.Row - 3673);
            if (!player.Character.LeagueQuestFlags.Contains(lqf))
                await SetLeagueQuestFlags(player, [lqf]);
            await UpdateVaultWitnessMilestones(player);
        }
        return true;
    }

    public static VaultDef SelectNextVault(Player player)
    {
        var lqfs = player.Character.LeagueQuestFlags;
        foreach (var vault in Vaults)
            if (!lqfs.Contains(vault.DialogueLqf) && !lqfs.Contains(vault.VisitedLqf))
                return vault;
        var data = WorldOf(PlayerOf(player)).CharacterData;
        int cursor = int.TryParse(data.Get(VaultCursorKey), out int c) ? c : 0;
        var chosen = Vaults[Math.Abs(cursor) % Vaults.Count];
        data.Change(VaultCursorKey, _ => ((cursor + 1) % Vaults.Count).ToString());
        return chosen;
    }

    public static async Task<VaultDef?> OpenNextMemoryVault(Player player)
    {
        if (!player.Character.LeagueQuestFlags.Contains(LqfVaultAvailable))
        {
            await PlayerOf(player).Tell("Complete a Shaped Region to unlock a Memory Vault first.");
            return null;
        }
        var vault = SelectNextVault(player);
        if (!_vaultAreas.TryGetValue(vault.AreaId, out var areaInfo))
        {
            await PlayerOf(player).Tell($"Memory Vault area {vault.AreaId} is unavailable.");
            return null;
        }
        var mapArea = new MapDevice.MapArea(areaInfo.Hash16, vault.AreaId, areaInfo.Name, 16)
        {
            Level = areaInfo.Level,
            UsesAtlas = false,
        };
        bool opened = false;
        if (MapDevice.OpenPortalsFromDevice is { } opener)
            opened = await opener(player, mapArea, MapMods.None,
                new Dictionary<string, string> { ["atlas.memoryVault"] = vault.AreaId }, MapDevice.PortalCount) is not null;
        if (!opened)
        {
            var (px, py) = player.Layout.Near((player.Layout.SpawnX + 8, player.Layout.SpawnY));
            opened = await global::Game.SpawnCustomPortal(player, px, py, areaInfo.Hash16,
                new PortalOptions(UsesLeft: MapDevice.PortalCount, SpawnReturnPortal: true, Owner: CharacterName(player))) is not null;
        }
        if (!opened) return null;

        await SetLeagueQuestFlags(player, [LqfVaultAvailable], on: false);
        var toSet = new[] { LqfVaultActive, LqfVaultPortalsSeen }
            .Where(f => !player.Character.LeagueQuestFlags.Contains(f)).ToList();
        if (toSet.Count > 0) await SetLeagueQuestFlags(player, toSet, on: true);
        await PlayerOf(player).Tell($"Eagon opens portals to the {vault.Name}.");
        return vault;
    }

    // ---- Inside Memory Vaults (MapWorldsMemoryVault1..4) ---------------------------------------------------------------

    static async Task OnPlayerEntered(Player player)
    {
        var vault = Vaults.FirstOrDefault(v => string.Equals(v.AreaId, player.Layout.Id, StringComparison.OrdinalIgnoreCase));
        if (vault is null) return;

        if (player.Character.LeagueQuestFlags.Contains(LqfVaultActive))
            await SetLeagueQuestFlags(player, [LqfVaultActive], on: false);
        var setFlags = new List<int>();
        if (!player.Character.LeagueQuestFlags.Contains(vault.VisitedLqf))
            setFlags.Add(vault.VisitedLqf);
        if (!player.Character.LeagueQuestFlags.Contains(vault.DialogueLqf))
            setFlags.Add(vault.DialogueLqf);
        if (setFlags.Count > 0) await SetLeagueQuestFlags(player, setFlags, on: true);
        await UpdateVaultWitnessMilestones(player);

        if (player.Instance is not { } instance) return;
        var state = VaultInstances.GetOrCreateValue(instance);
        if (state.Spawned) return;
        state.Spawned = true;

        // 1. Spawn EagonTemplarVault (NPC 714) with StateMachine activated=1, targetable=1
        var (ex, ey) = player.Layout.Near((player.Layout.SpawnX + 14, player.Layout.SpawnY));
        if (global::Game.Npcs.Find(EagonTemplarVaultPath) is { Supported: true } eagonNpc)
        {
            var sm = (eagonNpc.StateMachine ?? new StateMachineData([0, 0], 0, ["activated", "targetable"]))
                .Set("activated", 1)
                .Set("targetable", 1);
            state.EagonId = await global::Game.SpawnNpc(player, eagonNpc with { StateMachine = sm }, ex, ey, 0f);
        }

        // 2. Spawn 3 TemplarVaultRewardDisplay cases with rolled rewards
        if (QuestObjectTypes.Find(TemplarVaultDisplayPath) is { } displayInfo
            && QuestObject.CanSerialize(displayInfo.Components, haveStateMachine: true))
        {
            var rng = global::Game.AreaRng(player);
            (int Dx, int Dy)[] offsets = [(24, -10), (28, 0), (24, 10)];
            foreach (var (dx, dy) in offsets)
            {
                var (cx, cy) = player.Layout.Near((player.Layout.SpawnX + dx, player.Layout.SpawnY + dy));
                var reward = RollVaultReward(player, rng);
                if (reward is null) continue;
                uint id = player.TakeObjectId();
                var obj = new QuestObject(cx, cy, displayInfo.Components, Rotation: 0f)
                {
                    StateMachine = new StateMachineData([0, 0], 0, ["opened", "deactivated"]),
                    RewardDisplay = new RewardDisplayData(reward, null, OpenAttempts: 0),
                };
                await global::Game.SendWorldCreate(player, id,
                    () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(TemplarVaultDisplayPath), obj.Blob()),
                    $"0x02bb create TemplarVaultRewardDisplay 0x{id:x} at ({cx}, {cy})", wake: true);
                state.Displays[id] = (cx, cy, reward, false);
            }
        }
    }

    static Item? RollVaultReward(Player player, Random rng)
    {
        var catalog = ItemsFor(player);
        string[] candidates =
        [
            "Metadata/Items/Currency/CurrencyRerollRare",
            "Metadata/Items/Currency/CurrencyAddModToRare",
            "Metadata/Items/Currency/CurrencyModValues",
            "Metadata/Items/Currency/CurrencyCorrupt",
            "Metadata/Items/Currency/AstrolabeGeneric",
        ];
        string pick = candidates[rng.Next(candidates.Length)];
        var type = catalog.Find(pick) ?? catalog.All.FirstOrDefault(t => t.Supported && t.Class == "StackableCurrency");
        if (type is null) return null;
        return ItemCatalog.Create(type) with { ItemLevel = 84 };
    }

    static async Task<bool> OnObjectUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Instance is not { } instance || !VaultInstances.TryGetValue(instance, out var state)) return false;
        if (!state.Displays.TryGetValue(use.TargetId, out var display) || state.RewardClaimed || display.Opened)
            return false;

        if (!TryBeginObjectWalk(player, requestedAt, display.X, display.Y, 4, out var walk)) return true;
        var (action, now, _, _, arrive) = walk;
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action) || state.RewardClaimed) return true;
        state.RewardClaimed = true;
        state.Displays[use.TargetId] = display with { Opened = true };

        var info = QuestObjectTypes.Find(TemplarVaultDisplayPath);
        int smIdx = info?.Components.ToList().IndexOf("StateMachine") ?? -1;
        if (smIdx >= 0)
        {
            foreach (var (id, entry) in state.Displays)
            {
                bool isChosen = id == use.TargetId;
                var sm = new StateMachineData([isChosen ? 1 : 0, 1], 0, ["opened", "deactivated"]);
                await global::Game.SendWorldRecord(player, id,
                    InstanceEntities.StateMachineUpdate(id, (byte)smIdx, sm),
                    $"0x02bc TemplarVaultRewardDisplay 0x{id:x} opened={isChosen}");
            }
        }

        var at = player.Layout.Near((display.X, display.Y));
        var ground = new WorldItemObject(at.X, at.Y, display.Reward);
        uint dropId = player.Instance.Ground.Add(ground, player);
        await global::Game.SendGroundItem(player, dropId, ground, "Templar Vault display reward", justDropped: true);
        return true;
    }

    public static async Task UpdateVaultWitnessMilestones(Player player)
    {
        var lqfs = player.Character.LeagueQuestFlags;
        int witnessed = Vaults.Count(v => lqfs.Contains(v.DialogueLqf) || lqfs.Contains(v.VisitedLqf));
        var toSet = new List<int>();
        if (witnessed >= 1) { toSet.Add(LqfAnyVaultWitnessed); toSet.Add(LqfTemplarVaultReaction); }
        if (witnessed >= 2) toSet.Add(LqfTwoVaultsWitnessed);
        if (witnessed >= 3) toSet.Add(LqfThreeVaultsWitnessed);
        if (witnessed >= 4) toSet.Add(LqfAllVaultsWitnessed);
        var missing = toSet.Where(f => !lqfs.Contains(f)).Distinct().ToList();
        if (missing.Count > 0) await SetLeagueQuestFlags(player, missing, on: true);
    }
}
