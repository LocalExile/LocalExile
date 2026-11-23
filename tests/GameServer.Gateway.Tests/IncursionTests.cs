using System.Buffers.Binary;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Incursion;
using Xunit;

namespace GameServer.Gateway.Tests;

/// <summary>The Incursion module: its create bodies and messages against the official captures' layouts, and the
/// character's temple plan over the client's own tables, rooms and temple graph.</summary>
public class IncursionTests
{
    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    // The object flag, no base data, then Positioned's x, y and rotation 0, little-endian.
    static string Cell(byte flag, (int X, int Y) c)
    {
        var b = new byte[14];
        b[0] = flag;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(2), c.X);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(6), c.Y);
        return Hex(b);
    }

    [Fact]
    public void Roll_follows_the_map_stats_and_the_creates_and_messages_have_the_captured_layouts()
    {
        // CoreLeagues Incursion is a row the league chances never roll, so the module reads the map's master-mission stats.
        Assert.True(LeagueChances.Current.LeagueOf(IncursionTables.LeagueRow)?.NotRolled);
        Assert.Equal(0, LeagueChances.Current.Count("MapAtziri", IncursionTables.LeagueRow, new Random(1)));
        var alva = new Dictionary<uint, int> { [Stat.MapMasterMissionAlvaPctChance] = 100 };
        Assert.False(IncursionEncounter.Rolled(new Dictionary<uint, int>(), true, false, new Random(1)));
        Assert.True(IncursionEncounter.Rolled(alva, true, false, new Random(1)));
        Assert.False(IncursionEncounter.Rolled(alva, false, false, new Random(1)));
        Assert.False(IncursionEncounter.Rolled(alva, true, true, new Random(1)));
        Assert.True(IncursionEncounter.Rolled(new Dictionary<uint, int> { [Stat.MapMasterMissionAlvaPctChance] = 50, [Stat.MapMasterMissionAlvaChancePlusPct] = 100 }, true, false, new Random(1)));
        Assert.True(IncursionEncounter.Rolled(new Dictionary<uint, int> { [Stat.MapIncursionLeague] = 1 }, true, false, new Random(1)));
        foreach (uint off in new[] { Stat.MapDisableMissions, Stat.AtlasKeystoneNoExtraContent, Stat.MapNukeEverything, Stat.MapAreaUnderlevelled })
            Assert.False(IncursionEncounter.Rolled(new Dictionary<uint, int>(alva) { [off] = 1 }, true, false, new Random(1)));
        Assert.False(IncursionEncounter.Rolled(new Dictionary<uint, int>(alva) { [Stat.MapDisableLeagues] = 1 << IncursionTables.LeagueRow }, true, false, new Random(1)));
        Assert.Equal(100, IncursionEncounter.ForcedChance("Anarchy=50,Incursion=100"));
        Assert.True(IncursionEncounter.Rolled(new Dictionary<uint, int>(), false, false, new Random(1), forced: 100));

        var at = (X: 300, Y: 412);
        // Alva [capture 44/44]: Positioned's team and flags, Animated, InteractionAction, NPC, MinimapIcon, then her
        // StateMachine (3 states, no timers: lookat 1, open_room 0, open_temple 0) and the empty task list.
        Assert.Equal(Cell(0x02, at) + "000000000001010100" + "0300" + "0100" + "0000" + "0000" + "00",
            Hex(IncursionWire.AlvaBody(at, [1, 0, 0])));
        // IncursionPortal1 [capture 57/57], created asleep (flag 0) for the 0x1a9 that wakes it.
        Assert.Equal(Cell(0x00, at) + "0000000000" + "0100710f000000000000004040010800000000" + "0101" + "00",
            Hex(IncursionWire.Portal1Body(at, flag: 0)));
        // A cursed treasure [capture 75/75] and a closed door [capture 107/107], rotation 0.
        Assert.Equal(Cell(0x02, at) + "0000000040010101000100" + "0044" + "000000000000000000000000" + "40400100",
            Hex(IncursionWire.TreasureBody(at, 0f)));
        Assert.Equal(Cell(0x02, at) + "000004" + "0011000000ccb60bc340" + "0101" + "0000", Hex(IncursionWire.DoorBody(at, 0f)));
        // Alva's arrival effect: 4 zero bytes, Animated with the .ao (u32 UTF-16 units, then the path), no tasks.
        byte[] effect = IncursionWire.EffectBody(at, IncursionWire.TeleportAo);
        Assert.Equal(Cell(0x02, at) + "00000000" + "01", Hex(effect[..19]));
        Assert.Equal((uint)IncursionWire.TeleportAo.Length, BinaryPrimitives.ReadUInt32LittleEndian(effect.AsSpan(19)));
        Assert.Equal(19 + 4 + 2 * IncursionWire.TeleportAo.Length + 1, effect.Length);
        // Her state machine after the option (open_room) and after the run (lookat 2).
        Assert.Equal("0300" + "0100" + "0100" + "0000", Hex(IncursionWire.StateMachine([1, 1, 0])));
        // The whole update, as the official server's 0x2bc to component 6 (as captured: 0300 0100 0100 0000).
        Assert.Equal("02bc" + "00000007" + "00000000" + "0000" + "0009" + "06" + "0300" + "0100" + "0100" + "0000",
            Hex(IncursionWire.StateMachineUpdate(7, IncursionWire.AlvaStateMachine, [1, 1, 0])));
        Assert.Equal("0300" + "0200" + "0100" + "0000", Hex(IncursionWire.StateMachine([2, 1, 0])));
        // The timer (kind 2, whole seconds) and the progress bar (f32 big-endian, kind 0) [capture].
        Assert.Equal("01630200000011", Hex(IncursionWire.Timer(17)));
        Assert.Equal("01630200000000", Hex(IncursionWire.Timer(-3)));
        Assert.Equal("01643f00000000", Hex(IncursionWire.Progress(0.5f)));
        // The temple panel: 0x7b kind 0x18, 1 opens it and 0 closes it.
        Assert.Equal("007b1800000001000000000000000000000000", Hex(InstanceWaypoints.OpenUi(IncursionWire.TempleUiKind, 1)));
        Assert.Equal("007b1800000000000000000000000000000000", Hex(InstanceWaypoints.OpenUi(IncursionWire.TempleUiKind, 0)));
    }

    [Fact]
    public void The_temple_plan_and_the_past_rooms_come_from_the_clients_tables_and_files()
    {
        // The rooms a temple slot starts with when it is not a family's tier-1 room: tier 0 with a past room, not the
        // Entrance, the Apex or a TEMPLATE_ door piece.
        Assert.Equal(["ChasmRoom", "Passageways", "Halls", "Tunnels", "ThePits", "BanquetHall", "Tombs", "Antechamber", "Cellar", "Cloister"],
            IncursionTables.FillerRooms.Select(f => f.Id));
        // Their room files: 19 × 13 tiles, a telepad, both architect spawns, 18 pack spots and 10 cursed treasures each.
        foreach (var filler in IncursionTables.FillerRooms)
        {
            var room = IncursionRoom.Read(filler.PastArm);
            Assert.NotNull(room);
            Assert.Equal((19, 13), (room.Width, room.Height));
            Assert.NotNull(room.Telepad);
            Assert.NotNull(room.Spawn1);
            Assert.NotNull(room.Spawn2);
            Assert.Equal(18, room.PackSpots.Count);
            Assert.Equal(10, room.Chests.Count);
        }
        var chasm = IncursionRoom.Read(IncursionTables.FillerRooms[0].PastArm)!;
        Assert.Equal((0, -2), chasm.Telepad);          // the "telepad" point (218, 147), from the centre (218, 149)
        Assert.Equal((-165, 98), chasm.Spawn1);        // ArchitectSpawn1 at (53, 247)
        Assert.Equal((-81, -55), chasm.Chests[0]);     // the first AlvaCursedTreasure point (137, 94)
        // The hashes the 0x223 state carries, against the first capture's run: its slot went from GemRoomI to
        // GemRoomII, and its architects were rows 13 and 23.
        Assert.Equal(0x7206, IncursionTables.RoomHash16(IncursionTables.AllRooms.First(r => r.Id == "GemRoomI").Row));
        Assert.Equal(0x835c, IncursionTables.RoomHash16(IncursionTables.AllRooms.First(r => r.Id == "GemRoomII").Row));
        Assert.Equal(0xbdfa, IncursionTables.ArchitectHash16(13));
        Assert.Equal(0xf0a6, IncursionTables.ArchitectHash16(23));
        // Alva is an NPC whose "Enter Incursion" row needs IncursionOwner, and the client has the IncursionRules ruleset.
        var alva = Game.Npcs.Find(IncursionWire.AlvaType)!;
        var enter = Game.Npcs.TalksOf(alva).Single(t => t.Category == "OpenIncursionPortal");
        Assert.False(enter.Available(new HashSet<string>()));
        Assert.True(enter.Available(new HashSet<string> { IncursionEncounter.OwnerFlag }));
        Assert.NotNull(IncursionTables.RulesetRow);

        // The client's temple graph: 11 room slots and 26 doors, which its temple generator requires.
        Assert.Equal(IncursionTemple.RoomNodes, IncursionTemple.TempleGraph!.Slots.Count);
        Assert.Equal(IncursionTemple.DoorNodes, IncursionTemple.TempleGraph.Edges.Count);
        // A character's plan is drawn from its name, never holds a room twice, and differs between characters.
        var temple = IncursionTemple.Create("Exile", 68)!;
        var other = IncursionTemple.Create("Other", 68)!;
        Assert.Equal(temple.Rooms, IncursionTemple.Create("Exile", 68)!.Rooms);
        Assert.NotEqual(temple.Slots.Select(s => temple.Rooms[s]), other.Slots.Select(s => other.Rooms[s]));
        for (int i = 0; i < 100; i++)
        {
            var plan = IncursionTemple.Create($"Character{i}", 68)!;
            var rooms = plan.Slots.Select(s => plan.Rooms[s]).ToList();
            Assert.Equal(rooms.Count, rooms.Distinct().Count());
        }
        // 12 incursions over 11 slots: every one draws a slot (a slot opened before included), and the 12th opens it.
        var rng = new Random(12);
        IncursionTemple.PickedRoom? last = null;
        for (int run = 0; run < IncursionTemple.RunsToOpen; run++)
        {
            Assert.False(temple.IsOpen);
            last = temple.Pick(68, rng);
            Assert.NotNull(last);
            Assert.True(temple.Tier(last.Slot) < 3);
            temple.Apply(last, [], 68);
        }
        Assert.True(temple.IsOpen);
        // Killing the resident gives the room to the other architect's family; killing the other raises it a tier.
        var fresh = IncursionTemple.Create("Exile", 68)!;
        var withResident = Enumerable.Range(0, 50).Select(_ => fresh.Pick(68, rng)).First(p => p?.Resident is not null)!;
        Assert.Equal(IncursionTables.TempleFamilies[withResident.First][1], fresh.Apply(withResident, [withResident.Resident!.Value], 68));
        Assert.Contains(withResident.Resident.Value, fresh.KilledArchitects);

        // The save under the character gives back the rooms, the doors, the count and the levels; text that is not
        // a temple of this graph is refused.
        var reloaded = IncursionTemple.FromJson(temple.ToJson())!;
        Assert.Equal(temple.Rooms, reloaded.Rooms);
        Assert.Equal(temple.Slots.Select(temple.DoorsFor), reloaded.Slots.Select(reloaded.DoorsFor));
        Assert.Equal(temple.Done, reloaded.Done);
        Assert.Equal(temple.Levels, reloaded.Levels);
        Assert.Equal(temple.ToJson(), reloaded.ToJson());
        Assert.Null(IncursionTemple.FromJson(null));
        Assert.Null(IncursionTemple.FromJson("not json"));
        Assert.Null(IncursionTemple.FromJson(temple.ToJson().Replace("\"v\":1", "\"v\":2")));
        Assert.Null(IncursionTemple.FromJson("{\"v\":1,\"rooms\":[],\"doors\":[],\"done\":0,\"levels\":[],\"killed\":[]}"));

        // The temple state at a run's start [capture 8/8]: the constants, the 11 rooms' hashes in slot order, the two
        // architects' hashes big-endian (the resident first) and the slot's index; at its end 0, 0 and 0xffffffff.
        byte[] open = IncursionWire.TempleState(temple, open: true, last);
        Assert.Equal(46, open.Length);
        Assert.Equal("0223" + "00e0" + "817d", Hex(open[..6]));
        for (int i = 0; i < 11; i++)
            Assert.Equal(IncursionTables.RoomHash16(temple.Rooms[temple.Slots[i]]), BinaryPrimitives.ReadUInt16LittleEndian(open.AsSpan(6 + i * 2)));
        Assert.Equal("00000060900000000000", Hex(open[28..38]));
        Assert.Equal(IncursionTables.ArchitectHash16(last!.Resident ?? last.First), BinaryPrimitives.ReadUInt16BigEndian(open.AsSpan(38)));
        Assert.Equal((uint)temple.Slots.ToList().IndexOf(last.Slot), BinaryPrimitives.ReadUInt32BigEndian(open.AsSpan(42)));
        byte[] close = IncursionWire.TempleState(temple, open: false, last);
        Assert.Equal(open[..38], close[..38]);
        Assert.Equal("00000000" + "ffffffff", Hex(close[38..]));

        // One instance's incursions from the first spot to the last: the portal and Alva come in steps, the run starts once
        // the portal is open, a kill of anything else or one already taken changes nothing, an architect's kill is not in
        // the bar, the clock runs to 0 and the next spot is next, a dropped run keeps its spot, and a second look at the
        // run (a party member, a re-entry) reads the same state.
        var incursion = new IncursionRun([(100, 100), (500, 500)], 68, 7);
        Assert.Equal(IncursionRun.Stage.Hidden, incursion.Current);
        Assert.Equal((100, 100), incursion.Spot);
        Assert.False(incursion.Start([1, 2], new Dictionary<uint, int>(), 10));
        Assert.True(incursion.Step(IncursionRun.Stage.Hidden, IncursionRun.Stage.PortalShown));
        Assert.False(incursion.Step(IncursionRun.Stage.Hidden, IncursionRun.Stage.PortalShown));
        Assert.True(incursion.Step(IncursionRun.Stage.PortalShown, IncursionRun.Stage.Shown));
        Assert.True(incursion.Step(IncursionRun.Stage.Shown, IncursionRun.Stage.Open));
        Assert.True(incursion.Start([1, 2, 3, 4, 5], new Dictionary<uint, int> { [5] = 13 }, 10));
        Assert.Equal((4, 0, 10.0), (incursion.Total, incursion.Killed, incursion.Left));
        Assert.Equal(10, incursion.SecondsToSend());
        Assert.Null(incursion.SecondsToSend());
        Assert.False(incursion.Kill(99, 1));
        Assert.True(incursion.Kill(2, 0.5));
        Assert.False(incursion.Kill(2, 0.5));
        Assert.True(incursion.Kill(5, 0));
        Assert.Equal<int>([13], incursion.KilledArchitects);
        Assert.Equal((1, 10.5), (incursion.Killed, incursion.Left));
        Assert.Equal(0.25f, incursion.ProgressToSend());
        Assert.True(incursion.Cost(8));
        Assert.False(incursion.Tick(2.0));
        Assert.True(incursion.Tick(1.0));
        // A dropped incursion (its owner left): the survivors are handed back, the spot stays and the portal is open again.
        var dropped = new IncursionRun([(1, 1)], 68, 7);
        dropped.Step(IncursionRun.Stage.Hidden, IncursionRun.Stage.PortalShown);
        dropped.Step(IncursionRun.Stage.PortalShown, IncursionRun.Stage.Shown);
        dropped.Step(IncursionRun.Stage.Shown, IncursionRun.Stage.Open);
        dropped.Start([1, 2], new Dictionary<uint, int>(), 10);
        Assert.Equal<uint>([1, 2], dropped.Abandon().Order().ToArray());
        Assert.Equal((IncursionRun.Stage.Open, 0), (dropped.Current, dropped.Used));
        Assert.True(dropped.Start([3], new Dictionary<uint, int>(), 10));
        // The finished incursion: its leftovers are handed back, the next spot is next, and after the last none is.
        Assert.Equal<uint>([1, 3, 4], incursion.Finish().Order().ToArray());
        Assert.Equal((IncursionRun.Stage.Hidden, 1, (500, 500)), (incursion.Current, incursion.Used, incursion.Spot ?? (0, 0)));
        incursion.Step(IncursionRun.Stage.Hidden, IncursionRun.Stage.PortalShown);
        incursion.Step(IncursionRun.Stage.PortalShown, IncursionRun.Stage.Shown);
        incursion.Step(IncursionRun.Stage.Shown, IncursionRun.Stage.Open);
        incursion.Start([8], new Dictionary<uint, int>(), 10);
        Assert.Equal<uint>([8], incursion.Finish());
        Assert.Equal(IncursionRun.Stage.Over, incursion.Current);
        Assert.Null(incursion.Spot);
    }
}
