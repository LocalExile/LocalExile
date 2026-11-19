using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>The Allflame league (the client's "Deepwater"): its league category, the league's area stat, the ways to The Sovereign
/// (the World panel pin, /boat, the rowboat on The Coast), the hub's ruleset and its quest table (DeepwaterQuests), and the
/// encounter areas' grid (DeepwaterGrid), ruleset and run (DeepwaterRun: the lanterns and their light). Charts as items,
/// voyages' capsules and Allflame crafting are not built yet (Agents/roadmap/world.md).</summary>
public sealed partial class DeepwaterModule : IModule
{
    /// <summary>The Sovereign (<c>WorldAreas</c> DeepwaterHub, area type 53).</summary>
    public const string HubId = "DeepwaterHub";
    /// <summary>The Coast's rowboat to The Sovereign, which only the Deepwater variant of The Coast places (toisland.arm). The
    /// client makes it targetable once the character has <see cref="IntroductionSeen"/>.</summary>
    public const string Rowboat = "Metadata/Terrain/Leagues/Deepwater/Act1_Coast/DeepwaterTutorialBoat";
    internal const string Coast = "1_1_2";
    /// <summary>QuestFlags 3552, set by Valerie's "Ready for Adventure?" (NPCTalk 3932).</summary>
    internal const string IntroductionSeen = "DeepwaterNPCIntroductionSeen";
    /// <summary>QuestFlags 3556, set on the first arrival on The Sovereign (DeepwaterQuests).</summary>
    internal const string HubVisited = "DeepwaterHubVisited";
    // LeagueNames' Id of the Allflame league.
    const string LeagueName = "Deepwater";

    /// <summary>The league category of the Allflame leagues: LeagueCategoryToDisplayName's Id for LeagueNames "Deepwater"
    /// (59). The client picks The Coast's and The Ship Graveyard's Deepwater topologies by it and enables the World panel's
    /// Deepwater pin (SpecialMapPins <c>__col5</c>) only when it is sent.</summary>
    static readonly Lazy<int?> Category = new(() =>
    {
        int? name = ClientTables.TableOrEmpty("LeagueNames").FirstOrDefault(r => r.Text("Id") == LeagueName) is { Table: not null } row ? row.Index : null;
        if (name is null) return null;
        foreach (var r in ClientTables.TableOrEmpty("LeagueCategoryToDisplayName"))
            if (r.LongOrNull("LeagueName") == name) return r.IntOrNull("Id");
        return null;
    });

    // Rulesets row (S→C 0x1f) DeepwaterHubRules (ClientDeepwaterHub; the hub's windows look it up by row and use it without
    // a null check).
    static readonly Lazy<byte?> HubRules = new(() => Ruleset("DeepwaterHubRules"));
    // DeepwaterRules (ClientDeepwater, DeepwaterRuleset.Row): its per-frame update (0x1416851b0) looks the DeepwaterPlugin
    // terrain plugin up (0x141685030) without a null check, so it goes only to the areas whose WorldAreas TerrainPlugin is
    // that plugin: the client creates it there at every generation (0x141eca930). On The Sovereign and The Twilight Strand
    // it closed the client (Agents/docs/protocol/03 §8).

    static byte? Ruleset(string id) =>
        ClientTables.TableOrEmpty("Rulesets").FirstOrDefault(r => r.Text("Id") == id) is { Table: not null } row && row.Index <= byte.MaxValue
            ? (byte)row.Index : null;

    /// <summary>The pin and /boat reach The Sovereign only after the first boarding from The Coast (QuestFlags 3556, set on
    /// arrival): an earlier trip would remove ValerieTutorial before her introduction sets <see cref="IntroductionSeen"/>.
    /// (The real rule is probably league quest flag 1834 DeepwaterBoatUnlocked, which the World panel's pin shows by.)</summary>
    static bool Unlocked(ClientState conn) => ClientHasQuestFlag(conn, HubVisited);

    sealed class State
    {
        public uint Boat;
        public (int X, int Y) BoatAt;
        public int BoatReach;
    }

    readonly ConditionalWeakTable<IPlayer, State> _states = new();
    // One run per encounter instance (a party and a re-entry see the same lanterns).
    readonly ConditionalWeakTable<InstanceState, DeepwaterRun> _runs = new();
    readonly DeepwaterLight _light = new();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        string id = module.Manifest.Id;
        module.Lifetime.Own(Quests.Register(id, [new DeepwaterQuests()]));
        // Every area of the league is played in its category and carries map_deepwater_league (what the client's and the
        // server's extra terrain feature roll for Deepwater encounters read).
        InstanceEvents.LeagueCategory.Add(id, _ => Category.Value);
        InstanceEvents.AreaStats.Add(id, _ => Stats.Key("map_deepwater_league") is uint key ? new Dictionary<uint, int> { [key] = 1 } : null);
        // An encounter area's grid (0x1d extra data), chosen once per instance.
        InstanceEvents.TerrainPluginStates.Add(id, request => DeepwaterGrid.For(request.Area));
        // The hub's ruleset on the hub, the encounter's in an encounter area, before the area's objects.
        GameEvents.EnterMessages.Add(id, conn =>
        {
            string area = PlayerOf(conn).AreaId;
            if (area == HubId && HubRules.Value is { } hub) return [($"0x001f ruleset DeepwaterHubRules (row {hub})", InstanceProtocol.AddRuleset(hub))];
            if (DeepwaterGrid.IsEncounter(area) && DeepwaterRuleset.Row.Value is { } rules)
                return [($"0x001f ruleset DeepwaterRules (row {rules})", InstanceProtocol.AddRuleset(rules))];
            return [];
        });
        // C→S 0x183: the World panel's Deepwater pin (enabled by the league category, disabled on The Sovereign itself).
        GameEvents.Packet(InstanceWaypoints.VisitDeepwaterHubId).Add(id, async (conn, bytes) =>
        {
            uint from = bytes.Length >= 6 ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)) : 0;
            byte flag = bytes.Length >= 7 ? bytes[6] : (byte)0;
            module.Log($"{PlayerOf(conn).Character} <= 0x0183 World panel Deepwater pin (from object 0x{from:x}, u8 {flag})");
            // The u8 (1 with Ctrl held, which skips the client's "already there" test) is ignored, as for the other hub pins:
            // TravelTo opens no new instance, so on The Sovereign itself the click does nothing.
            if (!Unlocked(conn) || PlayerOf(conn).AreaId == HubId) return true;
            await WorldOf(conn).TravelTo(HubId, "World panel Deepwater pin");
            return true;
        });
        // /boat (Commands WarpToDeepwaterHub): every other command goes on to the Game module.
        GameEvents.Packet(InstanceItems.CommandId).Add(id, async (conn, bytes) =>
        {
            if (InstanceItems.ReadCommand(bytes).Id != InstanceItems.WarpToDeepwaterHubCommand) return false;
            if (!Unlocked(conn) || PlayerOf(conn).AreaId == HubId) return true;
            await WorldOf(conn).TravelTo(HubId, "/boat");
            return true;
        });
        GameEvents.AreaGenerated.Add(id, async conn =>
        {
            await SpawnRowboat(module, conn);
            await StartRun(module, conn);
        });
        // C→S 0x2ca: the encounter ruleset's actions (a lantern placed, a capsule's buttons).
        GameEvents.Packet(InstanceRulesets.ActionId).Add(id, (conn, bytes) => RulesetAction(module, conn, bytes));
        GameEvents.Tick.Add(id, (conn, _) => RunOf(conn) is { } run ? _light.Tick(module, conn, run) : Task.CompletedTask);
        GameEvents.PlayerLeaving.Add(id, (conn, _) =>
        {
            _light.Forget(conn);
            return Task.CompletedTask;
        });
        GameEvents.PlayerStatSources.Add(id, _light.WithSlow);
        GameEvents.StateBuilding.Add(id, RunState);
        GameEvents.ObjectUse.Add(id, async (conn, use, at) =>
        {
            var player = PlayerOf(conn);
            if (!_states.TryGetValue(player, out var state) || state.Boat == 0 || use.TargetId != state.Boat) return false;
            // The client offers the boat only after Valerie's introduction; an early click is refused quietly.
            if (!ClientHasQuestFlag(conn, IntroductionSeen)) return true;
            var world = WorldOf(conn);
            if (await world.WalkUpTo(state.BoatAt.X, state.BoatAt.Y, state.BoatReach, at))
                await world.TravelTo(HubId, "The Coast's rowboat");
            return true;
        });
    }

    // The rowboat where the Deepwater Coast's toisland.arm places it, as the real server's object (Transitionable state 1;
    // its StateMachine's defaults). The quest engine does not write StateMachine objects, so the module creates it.
    async Task SpawnRowboat(IModuleContext module, ClientState conn)
    {
        var player = PlayerOf(conn);
        var state = _states.GetOrCreateValue(player);
        state.Boat = 0;
        if (player.AreaId != Coast) return;
        var at = new QuestObjectTypes.AreaPlacements(Coast, conn.Seed, []).Of(Rowboat).FirstOrDefault();
        if (at is null) return;   // the Coast's other variants
        var states = ObjectTypes.All().Where(t => t.Type == Rowboat).Select(t => ObjectTypes.StateMachine(t.Row, ObjectTypes.SharedStates())).FirstOrDefault();
        if (QuestObjectTypes.Find(Rowboat) is not { } info || states is null || !QuestObject.CanSerialize(info.Components, true))
        {
            module.Log($"{player.Character}: the rowboat's components are not written ({string.Join(", ", QuestObjectTypes.Find(Rowboat)?.Components ?? [])})");
            return;
        }
        uint id = conn.TakeObjectId();
        var blob = new QuestObject(at.X, at.Y, info.Components, at.Rotation, at.Z, info.Model ?? (info.OwnModel ? null : at.Model)) { StateMachine = states }.Blob();
        if (!await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(Rowboat), blob),
            $"0x02bb Deepwater rowboat 0x{id:x} at ({at.X}, {at.Y})")) return;
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id));
        (state.Boat, state.BoatAt, state.BoatReach) = (id, (at.X, at.Y), info.Size + 2);
    }
}
