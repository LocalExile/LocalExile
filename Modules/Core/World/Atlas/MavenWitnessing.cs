using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Atlas;

/// <summary>
/// Real Maven Witnessing, Invitation UI (<c>0x7b 0x52</c> / <c>C→S 0x0298</c>), <c>MavenHub</c> ("The Maven's
/// Crucible") encounter flow, and <c>MavenBoss</c> ("The Maven's Game", Ruleset 112) progression.
/// </summary>
public static class MavenWitnessing
{
    public const string WitnessedDataKey = "atlas.mavenWitnessed";
    public const string MavenHubAreaId = "MavenHub";
    public const string MavenBossAreaId = "MavenBoss";
    public const string MavenObserverNpcId = "Metadata/NPC/Epilogue/MavenBossObserver";
    public const string MavenSpeakerNpcId = "Metadata/NPC/Epilogue/Maven";
    public const string CrucibleDeviceType = "Metadata/Terrain/EndGame/MapAtlasMaven/Objects/MavenBossRushObject";
    public const string CrescentSplinterId = "Metadata/Items/MapFragments/CurrencyMavenKeyFragment";
    public const string MavenWritId = "Metadata/Items/MapFragments/CurrencyMavenKey";
    public const string MavenBossVariety = "Metadata/Monsters/MavenBoss/TheMaven";
    public const string MavenBrainBossVariety = "Metadata/Monsters/MavenBoss/MavenBrainBoss";

    /// <summary>S→C 0x007b screen kind for <c>MavenInvitationMapDeviceScreen</c> (<c>0x140dcbb20</c>).</summary>
    public const byte MavenInvitationUiKind = 0x52;

    /// <summary>Rulesets.dat row 112 (<c>MavenBossFightRules</c>): handles S→C 0x001e ring strings and 0x02c9 sub-ids 0,1,2.</summary>
    public const byte MavenBossRulesetRow = 112;

    /// <summary>S→C 0x001e ruleset string event opcode (<c>0x1421a44a0</c>).</summary>
    public const ushort RulesetEventId = 0x001e;

    /// <summary>BuffDefinitions rows for Maven map boss witnessing (<c>maven_boss_buff</c>, <c>maven_boss_heal</c>).</summary>
    public const ushort MavenBossBuffRow = 1753;
    public const ushort MavenBossHealBuffRow = 1772;

    public const int LqfAtlasReady = 1019;
    public const int LqfCompletedAnyMavenFight = 374;
    public const int LqfVoidQuestUnlocked = 426;
    public const int LqfMavenWritUnlocked = 884;
    public const int LqfMavenVoidstoneUnlocked = 219;

    // MavenBossRushObject wire components: Positioned(0), BaseEvents(1), Animated(2), InteractionAction(3), StateMachine(4).
    // Preload(5) writes zero wire bytes, so StateMachine sits at component index 4.
    static readonly string[] CrucibleDeviceComponents =
        ["Positioned", "BaseEvents", "Animated", "InteractionAction", "StateMachine"];
    const byte CrucibleStateMachineComponentIndex = 4;
    const int CrucibleDeviceReach = 8;

    static readonly Regex OpenMavenInvitationRegex =
        new(@"open_maven_invitation\s+(\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Pre-allocated transient AreaQuestFlags arrays (zero allocation on hot path).
    static readonly string[] FlagsNone = [];
    static readonly string[] FlagsMavenBossArea = ["SeenMavenBoss"];
    static readonly string[] FlagsAtlasPortalsOpen = ["AtlasMavenTemp", "MavenPortalsOpen"];
    static readonly string[] FlagsVoidPortalsOpen = ["AtlasMavenVoidTemp", "MavenPortalsOpen"];
    static readonly string[] FlagsHubAtlasReady = ["AtlasMavenTemp", "InMavenHubArea"];
    static readonly string[] FlagsHubAtlasReadyOwner = ["IsMapOwner", "AtlasMavenTemp", "InMavenHubArea"];
    static readonly string[] FlagsHubAtlasStarted = ["AtlasMavenTemp", "InMavenHubArea", "MavenFightStarted"];
    static readonly string[] FlagsHubAtlasStartedOwner = ["IsMapOwner", "AtlasMavenTemp", "InMavenHubArea", "MavenFightStarted"];
    static readonly string[] FlagsHubAtlasComplete = ["AtlasMavenTemp", "InMavenHubArea", "MavenFightStarted", "MavenFightComplete"];
    static readonly string[] FlagsHubAtlasCompleteOwner = ["IsMapOwner", "AtlasMavenTemp", "InMavenHubArea", "MavenFightStarted", "MavenFightComplete"];
    static readonly string[] FlagsHubVoidReady = ["AtlasMavenVoidTemp", "InMavenHubArea"];
    static readonly string[] FlagsHubVoidReadyOwner = ["IsMapOwner", "AtlasMavenVoidTemp", "InMavenHubArea"];
    static readonly string[] FlagsHubVoidStarted = ["AtlasMavenVoidTemp", "InMavenHubArea", "MavenFightStarted"];
    static readonly string[] FlagsHubVoidStartedOwner = ["IsMapOwner", "AtlasMavenVoidTemp", "InMavenHubArea", "MavenFightStarted"];
    static readonly string[] FlagsHubVoidComplete = ["AtlasMavenVoidTemp", "InMavenHubArea", "MavenFightStarted", "MavenFightComplete"];
    static readonly string[] FlagsHubVoidCompleteOwner = ["IsMapOwner", "AtlasMavenVoidTemp", "InMavenHubArea", "MavenFightStarted", "MavenFightComplete"];

    static Dictionary<string, int?[]> _mavenDialogs = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<ushort, (string AreaId, string Name, IReadOnlyList<string> FinalBosses)> _worldAreasByHash = [];
    static HashSet<ushort> _voidWitnessAreaHashes = [];

    sealed class MapWitnessState
    {
        public bool Active;
        public bool FirstWitness;
        public bool SpectatorSpawned;
        public uint SpectatorId;
        public bool BossBuffed;
        public bool BossHealed;
        public bool Completed;
    }

    internal sealed class CrucibleState
    {
        public MapBossFights.Invitation? Fight;
        public uint DeviceId;
        public (int X, int Y) Center;
        public bool Started;
        public bool Completed;
        public HashSet<uint> BossIds = [];
        public List<ushort> UsedWitnessHashes = [];
        public byte AreaLevel = 73;
        public bool SpectatorSpawned;
    }

    static readonly ConditionalWeakTable<InstanceState, MapWitnessState> MapStates = new();
    static readonly ConditionalWeakTable<InstanceState, CrucibleState> CrucibleStates = new();

    /// <summary>Loads <c>MavenDialog</c>, <c>WorldAreas</c> boss mappings, and <c>Void1..6</c> witness hashes.</summary>
    public static void LoadData()
    {
        var dialogs = new Dictionary<string, int?[]>(StringComparer.OrdinalIgnoreCase);
        if (ClientTables.Has("MavenDialog"))
        {
            foreach (var row in ClientTables.Table("MavenDialog"))
            {
                if (row.Text("Id") is not { Length: > 0 } id) continue;
                dialogs[id] =
                [
                    row.IntOrNull("TextAudioT1"),
                    row.IntOrNull("TextAudioT2"),
                    row.IntOrNull("TextAudioT3"),
                    row.IntOrNull("TextAudioT4"),
                    row.IntOrNull("TextAudioT5"),
                    row.IntOrNull("TextAudioT6"),
                ];
            }
        }
        _mavenDialogs = dialogs;

        var varieties = new Dictionary<int, string>();
        if (ClientTables.Has("MonsterVarieties"))
        {
            int idx = 0;
            foreach (var v in ClientTables.Table("MonsterVarieties"))
                varieties[idx++] = v.Text("Id") ?? "";
        }

        var areasByHash = new Dictionary<ushort, (string AreaId, string Name, IReadOnlyList<string> FinalBosses)>();
        if (ClientTables.Has("WorldAreas"))
        {
            foreach (var a in ClientTables.Table("WorldAreas"))
            {
                if (a.IntOrNull("HASH16") is not int hashInt || hashInt <= 0) continue;
                ushort hash = (ushort)hashInt;
                string areaId = a.Text("Id") ?? "";
                string name = a.Text("Name") ?? areaId;
                var bosses = a.Ints("Bosses_MonsterVarietiesKeys")
                    .Select(b => varieties.GetValueOrDefault(b, ""))
                    .Where(id => id.Length > 0)
                    .ToList();
                areasByHash.TryAdd(hash, (areaId, name, bosses));
            }
        }
        _worldAreasByHash = areasByHash;

        var voidHashes = new HashSet<ushort>();
        if (MapBossFights.Current is { } fights)
        {
            foreach (var inv in fights.Invitations)
                if (inv.IsVoid)
                    voidHashes.UnionWith(inv.WitnessAreaHashes);
        }
        _voidWitnessAreaHashes = voidHashes;
        global::Game.IsMavenWitnessableSpecialArea = IsVoidWitnessArea;
    }

    public static bool IsVoidWitnessArea(ushort areaHash16) => _voidWitnessAreaHashes.Contains(areaHash16);

    // ---- Witness persistence -----------------------------------------------------------------------------------------

    public static HashSet<ushort> WitnessedAreas(ISavedData data)
    {
        var set = new HashSet<ushort>();
        string? raw = data.Get(WitnessedDataKey);
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (ushort.TryParse(part, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out ushort h) && h != 0)
                set.Add(h);
        return set;
    }

    public static IReadOnlyList<ushort> WitnessedAreaHashes(ISavedData data) =>
        [.. WitnessedAreas(data).Order()];

    public static bool AddWitnessedArea(ISavedData data, ushort areaHash16)
    {
        if (areaHash16 == 0) return false;
        return data.Change(WitnessedDataKey, old =>
        {
            var set = ParseHashes(old);
            if (!set.Add(areaHash16)) return null;
            return FormatHashes(set);
        });
    }

    public static bool RemoveWitnessedAreas(ISavedData data, IEnumerable<ushort> areaHashes)
    {
        var toRemove = areaHashes.ToHashSet();
        if (toRemove.Count == 0) return false;
        return data.Change(WitnessedDataKey, old =>
        {
            var set = ParseHashes(old);
            int before = set.Count;
            set.ExceptWith(toRemove);
            return set.Count == before ? null : FormatHashes(set);
        });
    }

    public static bool ClearWitnessedAreas(ISavedData data) =>
        data.Change(WitnessedDataKey, old => string.IsNullOrEmpty(old) ? null : "");

    static HashSet<ushort> ParseHashes(string? raw)
    {
        var set = new HashSet<ushort>();
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (ushort.TryParse(part, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out ushort h) && h != 0)
                set.Add(h);
        return set;
    }

    static string FormatHashes(HashSet<ushort> set) =>
        string.Join(',', set.Order().Select(h => h.ToString("x4", System.Globalization.CultureInfo.InvariantCulture)));

    // ---- MavenFights progression helpers -----------------------------------------------------------------------------

    /// <summary>Returns the currently active <c>Atlas1..5</c> Maven fight based on completion LQFs 979..982.</summary>
    public static MapBossFights.Invitation? ActiveAtlasFight(IReadOnlySet<int> leagueFlags)
    {
        var fights = MapBossFights.Current;
        if (fights is null) return null;
        MapBossFights.Invitation? last = null;
        foreach (var inv in fights.Invitations)
        {
            if (inv.IsVoid) continue;
            last = inv;
            if (inv.CompleteLeagueFlag > 0 && !leagueFlags.Contains(inv.CompleteLeagueFlag))
                return inv;
        }
        return last;
    }

    /// <summary>Counts distinct witnessed normal Atlas maps (<c>AtlasNode.__col28 == 1</c>) currently stored.</summary>
    public static int CountWitnessedAtlasMaps(IReadOnlySet<ushort> witnessedAreas)
    {
        int count = 0;
        foreach (ushort hash in witnessedAreas)
            if (AtlasProgress.FindByAreaHash16(hash) is { IsWitnessable: true })
                count++;
        return count;
    }

    /// <summary>Resolves the player's current Maven dialogue escalation tier (0..5 = T1..T6).</summary>
    public static int CurrentMavenTierIndex(IReadOnlySet<int> leagueFlags)
    {
        int completed = 0;
        for (int lqf = 979; lqf <= 983; lqf++)
            if (leagueFlags.Contains(lqf)) completed++;
        return Math.Clamp(completed, 0, 5);
    }

    /// <summary>Plays a <c>MavenDialog</c> row's <c>NPCTextAudio</c> line at the player's current Maven tier.</summary>
    public static async Task<bool> PlayMavenDialog(Player player, string dialogId, uint speakerObjectId = 0)
    {
        if (!_mavenDialogs.TryGetValue(dialogId, out var tiers)) return false;
        int tier = CurrentMavenTierIndex(player.Character.LeagueQuestFlags);
        int? audioRow = null;
        for (int t = Math.Min(tier, tiers.Length - 1); t >= 0; t--)
            if (tiers[t] is int found and >= 0) { audioRow = found; break; }
        if (audioRow is null)
            audioRow = tiers.FirstOrDefault(r => r is >= 0);
        if (audioRow is not int row) return false;
        int speakerRow = global::Game.Npcs.Find(MavenSpeakerNpcId)?.Row ?? 493;
        await Send(player, InstanceNpcs.TextAudio(speakerObjectId, (ushort)speakerRow, (ushort)row),
            $"0x01c7 MavenDialog {dialogId} (T{tier + 1} → NPCTextAudio {row})");
        return true;
    }

    // ---- Ruleset 112 (MavenBossFightRules) wire packets --------------------------------------------------------------

    /// <summary>S→C 0x001e ruleset string event (<c>0x1421a44a0</c>): <c>u16 0x001e, u32 BE entityId, u16 BE len, ASCII</c>.</summary>
    public static byte[] RulesetEventPacket(uint entityId, string eventName)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(eventName);
        return new LoginWriter()
            .U16(RulesetEventId)
            .U32(entityId)
            .U16(checked((ushort)ascii.Length))
            .Bytes(ascii)
            .ToArray();
    }

    /// <summary>S→C 0x02c9 Ruleset 112 (<c>MavenBossFightRules</c>) sub-id 0: arena ring/memory-game visual state
    /// (<c>u8 0, u8 state, f32 LE x, f32 LE y, f32 LE z, f32 LE w</c>).</summary>
    public static byte[] MavenBossRingStatePacket(byte state, float x, float y, float z, float w)
    {
        var payload = new BlobWriter().U8(0).U8(state).F32(x).F32(y).F32(z).F32(w).ToArray();
        return InstanceRulesets.Data(MavenBossRulesetRow, payload);
    }

    /// <summary>S→C 0x02c9 Ruleset 112 sub-id 1: stop rings.</summary>
    public static byte[] MavenBossStopRingsPacket() =>
        InstanceRulesets.Data(MavenBossRulesetRow, [1]);

    /// <summary>S→C 0x02c9 Ruleset 112 sub-id 2: start rings.</summary>
    public static byte[] MavenBossStartRingsPacket() =>
        InstanceRulesets.Data(MavenBossRulesetRow, [2]);

    // ---- Transient AreaQuestFlags ------------------------------------------------------------------------------------

    /// <summary>Zero-allocation transient quest flags for <c>MavenHub</c> and <c>MavenBoss</c>.</summary>
    internal static IReadOnlyList<string> TransientAreaFlags(Player player)
    {
        if (player.Layout.Id == MavenBossAreaId)
            return FlagsMavenBossArea;
        if (player.Layout.Id == MavenHubAreaId)
        {
            var run = global::Game.MapRunOf(player);
            bool owner = run is null || string.Equals(run.Owner, CharacterName(player), StringComparison.OrdinalIgnoreCase);
            bool isVoid = CrucibleStates.TryGetValue(player.Instance, out var st)
                ? st.Fight?.IsVoid == true
                : (run is { ItemRow: > 0 } && MapBossFights.Current.InvitationOf(run.ItemRow)?.IsVoid == true);
            bool started = st?.Started == true;
            bool complete = st?.Completed == true;
            if (isVoid)
            {
                if (complete) return owner ? FlagsHubVoidCompleteOwner : FlagsHubVoidComplete;
                if (started) return owner ? FlagsHubVoidStartedOwner : FlagsHubVoidStarted;
                return owner ? FlagsHubVoidReadyOwner : FlagsHubVoidReady;
            }
            if (complete) return owner ? FlagsHubAtlasCompleteOwner : FlagsHubAtlasComplete;
            if (started) return owner ? FlagsHubAtlasStartedOwner : FlagsHubAtlasStarted;
            return owner ? FlagsHubAtlasReadyOwner : FlagsHubAtlasReady;
        }
        if (MapDevice.OpenOf(CharacterName(player)) is { PortalsLeft: > 0, Area.Id: MavenHubAreaId } open)
        {
            bool isVoid = open.ItemRow > 0 && MapBossFights.Current.InvitationOf(open.ItemRow)?.IsVoid == true;
            return isVoid ? FlagsVoidPortalsOpen : FlagsAtlasPortalsOpen;
        }
        return FlagsNone;
    }

    // ---- Kirac open_maven_invitation → 0x7b 0x52 + MavenInvitations slot 0..10 ---------------------------------------

    /// <summary>Handles Kirac's <c>DispatchRulesetEvent("open_maven_invitation &lt;FightId&gt;")</c>:
    /// ensures the invitation item sits in <c>PlayerInventories.MavenInvitations</c> slot <c>0..10</c> and opens the
    /// native Maven Invitation Map Device screen (<c>S→C 0x007b</c> kind <c>0x52</c>, <c>u8 invSlot</c>) so Activate
    /// sends <c>C→S 0x0298</c>.</summary>
    public static async Task<bool> OpenMavenInvitationUi(Player player, string script)
    {
        var match = OpenMavenInvitationRegex.Match(script ?? "");
        if (!match.Success) return false;
        string fightId = match.Groups[1].Value;
        var fight = MapBossFights.Current?.InvitationById(fightId);
        if (fight is null || ItemsFor(player).Row(fight.ItemRow) is not { } type)
            return false;

        byte slot = (byte)Math.Clamp(fight.Slot, 0, 10);
        var items = ItemsOf(player);
        var existing = items.Items(PlayerInventories.MavenInvitations).FirstOrDefault(i => i.X == slot);
        if (existing is null)
        {
            var created = items.Put(PlayerInventories.MavenInvitations,
                new InventoryItem(items.NewItemId(), slot, 0, ItemCatalog.Create(type)));
            await Send(player, InstanceItems.InventoryChange(
                PlayerInventories.InventoryId(PlayerInventories.MavenInvitations),
                items.NextRevision(PlayerInventories.MavenInvitations), [], [created]),
                $"0x00bf MavenInvitations slot {slot}: placed {type.Name} ({fight.Id})");
        }
        await Send(player, InstanceWaypoints.OpenUi(MavenInvitationUiKind, slot),
            $"0x007b open MavenInvitationMapDeviceScreen (kind 0x52, slot {slot} {fight.Id})");
        return true;
    }

    // ---- RulesetEvent hook (set_maven_region_flag, maven_dialog:*, open_maven_invitation) -----------------------------

    internal static async Task<bool> OnRulesetEvent(Player player, uint npcId, SpawnedNpc npc, NpcCatalog.Talk talk, string ev)
    {
        if (ev.StartsWith("open_maven_invitation", StringComparison.OrdinalIgnoreCase))
            return await OpenMavenInvitationUi(player, ev);

        if (string.Equals(ev, "set_maven_region_flag", StringComparison.OrdinalIgnoreCase))
        {
            await ActivateFirstWitnessInCurrentMap(player);
            return true;
        }

        if (ev.StartsWith("maven_dialog:", StringComparison.OrdinalIgnoreCase))
        {
            string dialogId = ev["maven_dialog:".Length..].Trim();
            return await PlayMavenDialog(player, dialogId, npcId);
        }

        return false;
    }

    static async Task ActivateFirstWitnessInCurrentMap(Player player)
    {
        var state = MapStates.GetOrCreateValue(player.Instance);
        state.Active = true;
        state.FirstWitness = !player.Character.LeagueQuestFlags.Contains(EnvoyEncounters.LqfMavenBeacon)
            && !ClientHasQuestFlag(player, "HaveMavenMapDeviceAlteration");
        await EnsureMavenSpectatorSpawned(player, state);
        await PlayMavenDialog(player, "MavenBossFightEncounter", state.SpectatorId);
    }

    // ---- AreaRuleset, PlayerEntered, AreaTick, ObjectUse, Kill, MessagesHandled --------------------------------------

    internal static (byte Row, byte? PreloadGroup)? AreaRulesetFor(Player player) =>
        player.Layout.Id == MavenBossAreaId ? (MavenBossRulesetRow, null) : null;

    internal static async Task OnPlayerEntered(Player player)
    {
        if (player.Layout.Id == MavenHubAreaId)
        {
            await EnterMavenHub(player);
            return;
        }
        if (player.Layout.Id == MavenBossAreaId)
        {
            await EnterMavenBoss(player);
            return;
        }
        await CheckEnterWitnessedMap(player);
    }

    static async Task CheckEnterWitnessedMap(Player player)
    {
        var run = global::Game.MapRunOf(player);
        if (run is null) return;
        var lqfs = player.Character.LeagueQuestFlags;
        bool firstWitness = run.Atlas && run.Area.Tier >= 3 && run.Mods.MapRarity != ItemRarity.Unique
            && lqfs.Contains(EnvoyEncounters.LqfEnvoyMavenWarning)
            && !lqfs.Contains(EnvoyEncounters.LqfMavenBeacon)
            && !ClientHasQuestFlag(player, "HaveMavenMapDeviceAlteration");

        bool regularWitness = false;
        if (run.BossOption == 1 && lqfs.Contains(EnvoyEncounters.LqfMavenBeacon))
        {
            var data = WorldOf(PlayerOf(player)).CharacterData;
            var witnessed = WitnessedAreas(data);
            if (!witnessed.Contains(player.Layout.Hash))
            {
                if (IsVoidWitnessArea(player.Layout.Hash))
                    regularWitness = true;
                else if (AtlasProgress.OfArea(player.Layout.Id) is { IsWitnessable: true })
                {
                    var activeFight = ActiveAtlasFight(lqfs);
                    if (activeFight is null || run.Area.Tier >= activeFight.MinMapTier)
                        regularWitness = true;
                }
            }
        }

        if (!firstWitness && !regularWitness) return;
        var state = MapStates.GetOrCreateValue(player.Instance);
        state.Active = true;
        state.FirstWitness |= firstWitness;
        await EnsureMavenSpectatorSpawned(player, state);
        await PlayMavenDialog(player, IsVoidWitnessArea(player.Layout.Hash)
            ? "MavenOffAtlasBossEncounter" : "MavenBossFightEncounter", state.SpectatorId);
    }

    static async Task EnsureMavenSpectatorSpawned(Player player, MapWitnessState state)
    {
        bool hasObserver;
        lock (player.Npcs)
            hasObserver = player.Npcs.Values.Any(n => string.Equals(n.Npc.Id, MavenObserverNpcId, StringComparison.Ordinal));
        if (hasObserver) return;
        if (global::Game.Npcs.Find(MavenObserverNpcId) is not { Supported: true } observer) return;
        var boss = player.Area.Monsters.Values.FirstOrDefault(m => !m.Dead && m.Rarity == MonsterRarity.Unique);
        var at = boss is not null
            ? player.Layout.Near((boss.Walk.Position(global::Game.AreaTime(player)).X + 6,
                boss.Walk.Position(global::Game.AreaTime(player)).Y - 6))
            : player.Layout.Near((player.Layout.SpawnX + 8, player.Layout.SpawnY));
        state.SpectatorId = await global::Game.SpawnNpc(player, observer, at.X, at.Y, 0f);
        state.SpectatorSpawned = true;
    }

    /// <summary>AreaTick binding: applies Maven's Grace buff and heal to the witnessed map boss during combat.</summary>
    internal static Func<Player, double, Task>? TickBinding(Player player)
    {
        if (player.Layout.Id is MavenHubAreaId or MavenBossAreaId) return null;
        if (global::Game.MapRunOf(player) is null) return null;
        return async (p, _) =>
        {
            if (p.Closed || p.LeavingArea) return;
            if (!MapStates.TryGetValue(p.Instance, out var state) || !state.Active || state.Completed) return;
            await EnsureMavenSpectatorSpawned(p, state);
            if (state.BossBuffed && state.BossHealed) return;

            var node = AtlasProgress.OfArea(p.Layout.Id);
            KeyValuePair<uint, SpawnedMonster> target = default;
            foreach (var kv in p.Area.Monsters)
            {
                var m = kv.Value;
                if (m.Dead || m.Life == 0 || m.Rarity != MonsterRarity.Unique) continue;
                if (node is not null && m.Variety is { } v && !node.FinalBosses.Contains(v.Id)) continue;
                target = kv;
                break;
            }
            if (target.Key == 0) return;
            var boss = target.Value;
            if (boss.MaxLife == 0) return;

            if (!state.BossBuffed && boss.Life < boss.MaxLife * 85L / 100L)
            {
                state.BossBuffed = true;
                ushort buffInst = p.NextBuff++;
                await global::Game.SendWorldRecord(p, target.Key,
                    InstanceEntities.AddBuff(target.Key, buffInst, MavenBossBuffRow, state.SpectatorId),
                    $"0x01c1 Maven's Grace buff on boss 0x{target.Key:x}");
                await PlayMavenDialog(p, "MavenGuardSkill", state.SpectatorId);
            }
            else if (!state.BossHealed && boss.Life < boss.MaxLife * 60L / 100L)
            {
                state.BossHealed = true;
                uint heal = Math.Max(1u, boss.MaxLife * 15u / 100u);
                boss.Life = Math.Min(boss.MaxLife, boss.Life + heal);
                ushort buffInst = p.NextBuff++;
                await global::Game.SendWorldRecord(p, target.Key,
                    InstanceEntities.AddBuff(target.Key, buffInst, MavenBossHealBuffRow, state.SpectatorId),
                    $"0x01c1 Maven boss heal buff on boss 0x{target.Key:x}");
                await global::Game.SendWorldRecord(p, target.Key,
                    InstanceProtocol.LifeUpdate(target.Key, boss.Life),
                    $"0x01b1 Maven heals witnessed boss 0x{target.Key:x} to {boss.Life}/{boss.MaxLife}");
                await PlayMavenDialog(p, "MavenHealSkill", state.SpectatorId);
            }
        };
    }

    // ---- Map boss kill: first witness Beacon drop OR regular witness recording ---------------------------------------

    /// <summary>Processes Maven witnessing when a map's final boss dies. Returns true when <paramref name="peer"/>
    /// newly witnessed this map (so the caller can include flag <c>0x04</c> in <c>0x01a4</c>).</summary>
    internal static async Task<bool> OnMapBossKilled(Player killer, Player peer, MonsterInfo monster, AtlasProgress.Node? node)
    {
        if (!MapStates.TryGetValue(killer.Instance, out var state) || !state.Active)
            return false;

        if (state.FirstWitness && !peer.Character.LeagueQuestFlags.Contains(EnvoyEncounters.LqfMavenBeacon)
            && !ClientHasQuestFlag(peer, "HaveMavenMapDeviceAlteration"))
        {
            state.Completed = true;
            await global::Game.DropQuestItem(peer, EnvoyEncounters.MavenBeaconItem, (monster.X, monster.Y), "The Maven");
            if (!ClientHasQuestFlag(peer, "PickUpMavenAlteration"))
                await SetQuestFlags(peer, ["PickUpMavenAlteration"]);
            await PlayMavenDialog(peer, "MavenBossVictory", state.SpectatorId);
            return false;
        }

        if (!peer.Character.LeagueQuestFlags.Contains(EnvoyEncounters.LqfMavenBeacon))
            return false;

        var data = WorldOf(PlayerOf(peer)).CharacterData;
        ushort areaHash = killer.Layout.Hash;
        bool added = AddWitnessedArea(data, areaHash);
        state.Completed = true;
        if (!added) return false;

        var newlyReady = await EvaluateWitnessThresholds(peer, data);
        if (newlyReady.Count > 0)
        {
            bool isVoid = newlyReady.Any(f => f.IsVoid);
            await PlayMavenDialog(peer, isVoid ? "MavenOffAtlasInvitation" : "MavenInvitationToFight", state.SpectatorId);
            foreach (var ready in newlyReady)
                await PlayerOf(peer).Tell($"The Maven's Invitation ({ready.Id}) is ready — speak to Kirac in your Hideout.");
        }
        else
        {
            await PlayMavenDialog(peer, IsVoidWitnessArea(areaHash)
                ? "MavenOffAtlasBossVictory" : "MavenBossVictory", state.SpectatorId);
        }
        return true;
    }

    /// <summary>Evaluates <c>Atlas1..5</c> and <c>Void1..6</c> witness thresholds and sets ready LQFs
    /// (<c>1019</c> for Atlas, <c>1100..1104, 1161</c> for Void1..6).</summary>
    public static async Task<List<MapBossFights.Invitation>> EvaluateWitnessThresholds(Player player, ISavedData data)
    {
        var readyNow = new List<MapBossFights.Invitation>();
        var witnessed = WitnessedAreas(data);
        var lqfs = player.Character.LeagueQuestFlags;
        var toSet = new List<int>();

        if (ActiveAtlasFight(lqfs) is { } activeAtlas && !lqfs.Contains(LqfAtlasReady))
        {
            if (CountWitnessedAtlasMaps(witnessed) >= activeAtlas.Count)
            {
                toSet.Add(LqfAtlasReady);
                readyNow.Add(activeAtlas);
            }
        }

        if (MapBossFights.Current is { } fights)
        {
            foreach (var inv in fights.Invitations)
            {
                if (!inv.IsVoid || inv.ReadyLeagueFlag <= 0 || inv.WitnessAreaHashes.Count == 0) continue;
                if (lqfs.Contains(inv.ReadyLeagueFlag)) continue;
                if (inv.WitnessAreaHashes.All(witnessed.Contains))
                {
                    toSet.Add(inv.ReadyLeagueFlag);
                    readyNow.Add(inv);
                }
            }
        }

        if (toSet.Count > 0)
            await SetLeagueQuestFlags(player, toSet);
        return readyNow;
    }

    // ---- MavenHub ("The Maven's Crucible") ---------------------------------------------------------------------------

    static async Task EnterMavenHub(Player player)
    {
        var state = CrucibleStates.GetOrCreateValue(player.Instance);
        var run = global::Game.MapRunOf(player);
        if (state.Fight is null)
        {
            state.Fight = (run is { ItemRow: > 0 } ? MapBossFights.Current.InvitationOf(run.ItemRow) : null)
                ?? ActiveAtlasFight(player.Character.LeagueQuestFlags);
            state.AreaLevel = (byte)Math.Clamp(state.Fight?.AreaLevel ?? 73, 68, 85);
            state.Center = FindCrucibleCenter(player);
        }

        if (state.DeviceId == 0)
            state.DeviceId = player.TakeObjectId();
        int smVal = state.Completed ? 2 : state.Started ? 1 : 0;
        var device = new QuestObject(state.Center.X, state.Center.Y, CrucibleDeviceComponents)
        {
            StateMachine = new StateMachineData([smVal], 0, ["current_state"]),
        };
        await global::Game.SendWorldCreate(player, state.DeviceId,
            InstanceEntities.CreateObject(state.DeviceId, InstanceEntities.ObjectTypeHash(CrucibleDeviceType), device.Blob()),
            $"0x02bb MavenBossRushObject 0x{state.DeviceId:x} at ({state.Center.X}, {state.Center.Y}) state={smVal}");
        await global::Game.SendWorldRecord(player, state.DeviceId, InstanceProtocol.EnterWakeZone(state.DeviceId), null);

        bool hasObserver;
        lock (player.Npcs)
            hasObserver = player.Npcs.Values.Any(n => string.Equals(n.Npc.Id, MavenObserverNpcId, StringComparison.Ordinal));
        if (!hasObserver && global::Game.Npcs.Find(MavenObserverNpcId) is { Supported: true } observer)
        {
            state.SpectatorSpawned = true;
            var at = player.Layout.Near((state.Center.X, state.Center.Y - 12));
            await global::Game.SpawnNpc(player, observer, at.X, at.Y, 0f);
        }

        await FlushQuestFlags(player, "MavenHub entry");
        if (!state.Started)
            await PlayMavenDialog(player, "MavenIntroMultiBossArena");
    }

    internal static async Task<bool> OnObjectUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Layout.Id != MavenHubAreaId) return false;
        if (!CrucibleStates.TryGetValue(player.Instance, out var state) || state.DeviceId != use.TargetId)
            return false;
        if (state.Started || state.Completed) return true;

        var run = global::Game.MapRunOf(player);
        if (run is not null && !string.Equals(run.Owner, CharacterName(player), StringComparison.OrdinalIgnoreCase))
        {
            await PlayerOf(player).Tell("Only the map owner can activate the Maven's Crucible.");
            return true;
        }

        if (!global::Game.TryBeginObjectWalk(player, requestedAt, state.Center.X, state.Center.Y, CrucibleDeviceReach, out var walk))
            return true;
        var (action, now, _, _, arrive) = walk;
        if (arrive > now) await global::Game.AreaDelay(player, arrive - now);
        if (!global::Game.PlayerActionCurrent(player, action) || state.Started) return true;

        await StartCrucibleFight(player, state);
        return true;
    }

    internal static async Task StartCrucibleFight(Player player, CrucibleState state)
    {
        state.Started = true;
        var activeSm = new StateMachineData([1], 0, ["current_state"]);
        foreach (var peer in InstancePeers(player))
        {
            await global::Game.SendWorldRecord(peer, state.DeviceId,
                InstanceEntities.StateMachineUpdate(state.DeviceId, CrucibleStateMachineComponentIndex, activeSm),
                $"0x02bc MavenBossRushObject 0x{state.DeviceId:x} current_state=1 (started)");
            await FlushQuestFlags(peer, "Maven Crucible started");
        }

        var (bossVarieties, usedHashes) = ResolveCrucibleBosses(player, state.Fight);
        state.UsedWitnessHashes = usedHashes;
        state.BossIds.Clear();

        int n = bossVarieties.Count;
        for (int i = 0; i < n; i++)
        {
            string varietyId = bossVarieties[i];
            if (global::Game.Monsters.Find(varietyId) is not { Supported: true } variety || variety.Id != varietyId)
                continue;
            double angle = 2.0 * Math.PI * i / Math.Max(1, n);
            var pos = player.Layout.Near((
                state.Center.X + (int)Math.Round(Math.Cos(angle) * 14),
                state.Center.Y + (int)Math.Round(Math.Sin(angle) * 14)));
            uint bossId = await global::Game.SpawnMonster(player, variety, state.AreaLevel, MonsterRarity.Unique,
                pos.X, pos.Y, log: true, alone: false);
            if (bossId != 0) state.BossIds.Add(bossId);
        }

        await PlayMavenDialog(player, "MavenReleaseBossIntoArena");
    }

    /// <summary>Resolves the boss MonsterVarieties to spawn inside <c>MavenHub</c>: uses the player's actual witnessed
    /// Atlas areas for <c>Atlas1..5</c> (falling back to <see cref="MapBossFights.CrucibleWitnesses"/> if fewer than
    /// required), or the fight's <c>WitnessAreas</c> bosses for <c>Void1..6</c>.</summary>
    public static (List<string> Bosses, List<ushort> UsedWitnessHashes) ResolveCrucibleBosses(
        Player player, MapBossFights.Invitation? fight)
    {
        var bosses = new List<string>();
        var usedHashes = new List<ushort>();
        var data = WorldOf(PlayerOf(player)).CharacterData;
        var witnessed = WitnessedAreas(data);
        int need = Math.Max(1, fight?.Count ?? 3);

        if (fight is { IsVoid: true })
        {
            usedHashes.AddRange(fight.WitnessAreaHashes);
            bosses.AddRange(MapBossFights.Current.CrucibleWitnesses(fight.ItemRow));
            return (bosses, usedHashes);
        }

        foreach (ushort areaHash in witnessed.Order())
        {
            if (bosses.Count >= need) break;
            if (AtlasProgress.FindByAreaHash16(areaHash) is not { IsWitnessable: true }) continue;
            if (_worldAreasByHash.TryGetValue(areaHash, out var areaInfo) && areaInfo.FinalBosses.Count > 0)
            {
                string finalBoss = areaInfo.FinalBosses[^1];
                if (!bosses.Contains(finalBoss, StringComparer.Ordinal))
                {
                    bosses.Add(finalBoss);
                    usedHashes.Add(areaHash);
                }
            }
        }

        if (bosses.Count < need && fight is not null)
        {
            foreach (string fallback in MapBossFights.Current.CrucibleWitnesses(fight.ItemRow))
            {
                if (bosses.Count >= need) break;
                if (!bosses.Contains(fallback, StringComparer.Ordinal))
                    bosses.Add(fallback);
            }
        }
        return (bosses, usedHashes);
    }

    static async Task CheckCrucibleCompletion(Player player, MonsterInfo monster)
    {
        if (!CrucibleStates.TryGetValue(player.Instance, out var state) || !state.Started || state.Completed)
            return;
        if (monster.ObjectId != 0) state.BossIds.Remove(monster.ObjectId);
        // Check if any tracked Crucible boss (or any other unique boss in the arena) is still alive.
        foreach (var kv in player.Area.Monsters)
        {
            var m = kv.Value;
            if (m.Dead || m.Life <= 0) continue;
            if (monster.ObjectId != 0 && kv.Key == monster.ObjectId) continue;
            if (state.BossIds.Contains(kv.Key) || m.Rarity == MonsterRarity.Unique)
                return;
        }
        await CompleteCrucibleFight(player, state, (monster.X, monster.Y));
    }

    internal static async Task CompleteCrucibleFight(Player player, CrucibleState state, (int X, int Y) dropAt)
    {
        if (state.Completed) return;
        state.Completed = true;
        var fight = state.Fight;
        bool anyFirstClear = false;

        foreach (var peer in InstancePeers(player))
        {
            try
            {
                var data = WorldOf(PlayerOf(peer)).CharacterData;
                var lqfs = peer.Character.LeagueQuestFlags;
                bool firstClear = fight is { CompleteLeagueFlag: > 0 } && !lqfs.Contains(fight.CompleteLeagueFlag);
                anyFirstClear |= firstClear;

                // Clear witnessed areas consumed by this fight.
                if (fight is { IsVoid: true })
                {
                    RemoveWitnessedAreas(data, fight.WitnessAreaHashes);
                    if (fight.ReadyLeagueFlag > 0 && lqfs.Contains(fight.ReadyLeagueFlag))
                        await SetLeagueQuestFlags(peer, [fight.ReadyLeagueFlag], on: false);
                }
                else
                {
                    var atlasWitnessed = WitnessedAreas(data)
                        .Where(h => AtlasProgress.FindByAreaHash16(h) is { IsWitnessable: true })
                        .ToList();
                    RemoveWitnessedAreas(data, atlasWitnessed);
                    if (lqfs.Contains(LqfAtlasReady))
                        await SetLeagueQuestFlags(peer, [LqfAtlasReady], on: false);
                }

                // Advance completion LQFs + CompletedAnyMavenFight (374) + MavenVoidQuestUnlocked (426).
                var toSet = new List<int>();
                if (fight is { CompleteLeagueFlag: > 0 } && !lqfs.Contains(fight.CompleteLeagueFlag))
                    toSet.Add(fight.CompleteLeagueFlag);
                if (!lqfs.Contains(LqfCompletedAnyMavenFight)) toSet.Add(LqfCompletedAnyMavenFight);
                if (!lqfs.Contains(LqfVoidQuestUnlocked)) toSet.Add(LqfVoidQuestUnlocked);
                if (toSet.Count > 0)
                    await SetLeagueQuestFlags(peer, toSet);

                if (firstClear)
                {
                    ushort nextRespec = (ushort)Math.Min(ushort.MaxValue, global::Game.AtlasRespecPoints(peer) + 2);
                    global::Game.SetAtlasRespecPoints(peer, nextRespec);
                    await AtlasModule.SyncAtlasPassivePoints(peer, $"Maven Crucible {fight!.Id} completed");
                }

                var doneSm = new StateMachineData([firstClear ? 2 : 3], 0, ["current_state"]);
                if (state.DeviceId != 0)
                {
                    await global::Game.SendWorldRecord(peer, state.DeviceId,
                        InstanceEntities.StateMachineUpdate(state.DeviceId, CrucibleStateMachineComponentIndex, doneSm),
                        $"0x02bc MavenBossRushObject 0x{state.DeviceId:x} current_state={(firstClear ? 2 : 3)} (complete)");
                }
                await FlushQuestFlags(peer, "Maven Crucible complete");
                await AtlasFeature.Refresh(peer);
                await PlayMavenDialog(peer, firstClear ? "MavenPassivesWon" : "MavenAllBossesDead");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }

        await DropCrescentSplinters(player, fight, dropAt);
    }

    static async Task DropCrescentSplinters(Player player, MapBossFights.Invitation? fight, (int X, int Y) at)
    {
        if (!GameEvents.AllowsLoot(player)) return;
        if (ItemsFor(player).Find(CrescentSplinterId) is not { Supported: true } type) return;
        ushort count = fight?.Id switch
        {
            "Void5" => 8,
            "Void1" or "Void2" or "Void3" or "Void4" or "Void6" => 5,
            "Atlas5" => 3,
            _ => 2,
        };
        var pos = player.Layout.Near(at);
        var item = ItemCatalog.Create(type) with { StackSize = count };
        var ground = new WorldItemObject(pos.X, pos.Y, item);
        uint id = player.Instance.Ground.Add(ground, player);
        await global::Game.SendGroundItem(player, id, ground,
            $"Maven Crucible drops {count}× Crescent Splinter", justDropped: true);
    }

    // ---- MavenBoss ("The Maven's Game", Ruleset 112) -----------------------------------------------------------------

    static async Task EnterMavenBoss(Player player)
    {
        if (!ClientHasQuestFlag(player, "SeenMavenBoss"))
            await SetQuestFlags(player, ["SeenMavenBoss"]);
        if (!player.Character.LeagueQuestFlags.Contains(LqfMavenWritUnlocked))
            await SetLeagueQuestFlags(player, [LqfMavenWritUnlocked]);
        await Send(player, MavenBossRingStatePacket(0, player.Layout.SpawnX, player.Layout.SpawnY, 0f, 0f),
            "0x02c9 MavenBossFightRules (112) sub-id 0 initial ring state");
    }

    /// <summary>Handles kills inside <c>MavenHub</c>, <c>MavenBoss</c>, and off-Atlas <c>Void1..6</c> boss areas.</summary>
    internal static async Task OnMonsterKilled(Player player, MonsterInfo monster)
    {
        if (player.Layout.Id == MavenHubAreaId)
        {
            await CheckCrucibleCompletion(player, monster);
            return;
        }

        if (player.Layout.Id == MavenBossAreaId)
        {
            if (monster.VarietyId == MavenBossVariety)
            {
                foreach (var peer in InstancePeers(player))
                {
                    await Send(peer, RulesetEventPacket(0, "maven_boss_start_rings"),
                        "0x001e MavenBossFightRules maven_boss_start_rings");
                    await Send(peer, MavenBossStartRingsPacket(),
                        "0x02c9 MavenBossFightRules (112) sub-id 2 start rings");
                }
            }
            else if (monster.VarietyId == MavenBrainBossVariety)
            {
                foreach (var peer in InstancePeers(player))
                {
                    await Send(peer, RulesetEventPacket(0, "maven_boss_stop_rings"),
                        "0x001e MavenBossFightRules maven_boss_stop_rings");
                    await Send(peer, MavenBossStopRingsPacket(),
                        "0x02c9 MavenBossFightRules (112) sub-id 1 stop rings");
                    if (!peer.Character.LeagueQuestFlags.Contains(LqfMavenVoidstoneUnlocked))
                        await SetLeagueQuestFlags(peer, [LqfMavenVoidstoneUnlocked]);
                }
            }
            return;
        }

        // Off-Atlas Void1..6 witnessable areas (e.g. Shaper, Elder, Uber Atziri, Chayula, Guardians) have no AtlasNode
        // handled by AtlasModule.CompleteMapOnBossKill — record their Maven witness here when their final boss dies.
        if (IsVoidWitnessArea(player.Layout.Hash) && AtlasProgress.OfArea(player.Layout.Id) is null
            && IsAreaFinalBossKill(player, monster))
        {
            foreach (var peer in InstancePeers(player))
            {
                if (await OnMapBossKilled(player, peer, monster, null))
                    await AtlasFeature.Refresh(peer);
            }
        }
    }

    /// <summary>When the player carries <c>The Maven's Writ</c> (e.g. combined from 10 Crescent Splinters),
    /// unlock <c>LQF 884</c> to start <c>maven_boss</c> ("The Maven's Game").</summary>
    internal static async Task OnMessagesHandled(Player player, IReadOnlyList<ushort> opcodes)
    {
        if (player.Character.LeagueQuestFlags.Contains(LqfMavenWritUnlocked)) return;
        if (!opcodes.Any(QuestItems.MovesItems)) return;
        if (ItemsFor(player).Find(MavenWritId) is not { } writ) return;
        bool hasWrit = ItemsOf(player).Items(PlayerInventories.MainInventory).Any(i => i.Item.TypeHash == writ.Hash)
            || ItemsOf(player).Items(PlayerInventories.MapDevice).Any(i => i.Item.TypeHash == writ.Hash);
        if (hasWrit)
            await SetLeagueQuestFlags(player, [LqfMavenWritUnlocked]);
    }

    static bool IsAreaFinalBossKill(Player player, MonsterInfo monster)
    {
        if (!_worldAreasByHash.TryGetValue(player.Layout.Hash, out var info) || info.FinalBosses.Count == 0)
            return false;
        if (!info.FinalBosses.Contains(monster.VarietyId, StringComparer.Ordinal))
            return false;
        foreach (var m in player.Area.Monsters.Values)
        {
            if (m.Dead || m.Life <= 0) continue;
            if (monster.ObjectId != 0 && m.ObjectId == monster.ObjectId) continue;
            if (m.Variety is { Id.Length: > 0 } v && info.FinalBosses.Contains(v.Id, StringComparer.Ordinal))
                return false;
        }
        return true;
    }

    static (int X, int Y) FindCrucibleCenter(Player player)
    {
        if (player.Layout.Level?.RoomObjects is { Count: > 0 } rooms)
        {
            var found = rooms.FirstOrDefault(o =>
                o.Type.Contains("MavenBossRush", StringComparison.OrdinalIgnoreCase)
                || o.Type.Contains("CentrePiece", StringComparison.OrdinalIgnoreCase));
            if (found is not null) return (found.X, found.Y);
        }
        return player.Layout.Near((player.Layout.SpawnX + 12, player.Layout.SpawnY));
    }

    static Task FlushQuestFlags(Player player, string reason) =>
        player.Connection.PlayerSent
            ? global::Game.SendWorldRecord(player, player.Id,
                InstanceEntities.PlayerQuestFlagsUpdate(player.Id, global::Game.PlayerQuestFlags(player)),
                $"0x02bc {reason}")
            : Task.CompletedTask;

    static IEnumerable<Player> InstancePeers(Player player)
    {
        var peers = InGame.Keys.Where(c => c.HasClient && ReferenceEquals(c.Instance, player.Instance)
            && c.Connection.PlayerSent && !c.LeavingArea && !c.Closed).ToList();
        return peers.Contains(player) ? peers : [player, .. peers];
    }
}
