using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// Izaro's Aspirant's Trials (roadmap world.md "The Labyrinth", step 1): each section of the day's Labyrinth ends in his
/// arena (the layout node with <c>SectionEnd</c>, <see cref="LabyrinthDay.IzaroRoom"/>; the area's "secret" node takes the
/// day's mechanic room, boss_&lt;mechanic&gt;.arm, and the last section's the labyrinth_final_boss room). He rises on his
/// elevator (Elevator_Izaro, the arena tile's server object) when the player comes into the arena, and his life bar is
/// split between the trials: he starts each with what the earlier ones left (the original, live: a third per trial in
/// Normal) and leaves the first ones on the elevator at the next third; the last one he dies. The arena's doors
/// (LabyrinthIzaroArenaDoor) shut as he rises and open again with its ways out (<see cref="Beaten"/>); the corridor's gate
/// (LabyrinthIzaroDoor) in front of the way into the arena opens when used (<see cref="GateOpen"/>), and he speaks
/// (S→C 0x1c7, <see cref="Speech"/>). The mechanics (the BossObjectiveMarkers' objects, LabyrinthSecretEffects), the
/// Goddess of Justice and his chests are not built yet.
/// </summary>
public static class IzaroFights
{
    /// <summary>The arena's server objects: his elevator and the doors of the arena's ways in and out; the corridor's gate
    /// in front of the way into the arena (LabyrinthIzaroArenaTransition, at the same place).</summary>
    public const string Elevator = "Metadata/Terrain/Labyrinth/Objects/Elevator_Izaro",
        ArenaDoor = "Metadata/Terrain/Labyrinth/Objects/LabyrinthIzaroArenaDoor",
        Gate = "Metadata/Terrain/Labyrinth/Objects/LabyrinthIzaroDoor",
        Railing = "Metadata/Terrain/Labyrinth/Objects/BossObjectiveBlocker",
        ArenaTransition = "Metadata/Terrain/Labyrinth/Objects/LabyrinthIzaroArenaTransition";

    /// <summary>The places of a mechanic's things an earlier trial left: an alcove's (BossObjectiveMarker, three behind each
    /// railing) and a ledge's in the room before the arena (BossEntryObjectiveMarker, in threes).</summary>
    public const string Marker = "Metadata/Terrain/Labyrinth/Objects/BossObjectiveMarker",
        LedgeMarker = "Metadata/Terrain/Labyrinth/Objects/BossEntryObjectiveMarker";

    /// <summary>The railing's lowering (alcove_railing.ao "down").</summary>
    public const double RailingSeconds = 3.3;

    // Positioned, BaseEvents, Animated, Transitionable, TriggerableBlockage (then Render), the gate also InteractionAction
    // (then Render, Targetable), ot_components.py.
    static readonly string[] Components = ["Positioned", "BaseEvents", "Animated", "Transitionable", "TriggerableBlockage"];
    static readonly string[] GateComponents = [.. Components, "InteractionAction"];

    /// <summary>The Transitionable component's index in all three's order.</summary>
    public const byte TransitionableIndex = 3;

    /// <summary>The gate's walk-up size (object_size 9, less one).</summary>
    public const int GateSize = 8;

    /// <summary>Elevator_Izaro.ot's states: 1 lowered, 2 raising (ElevatorLarge.ao "raise", 10.666 s), 3 raised, 4 lowering
    /// ("lower", 3.333 s), 5 lowered.</summary>
    public const byte Lowered = 1, Raising = 2, Raised = 3, Lowering = 4, Down = 5;

    /// <summary>The arena doors' states (LabyrinthIzaroArenaDoor.ot; LargeDoor.ao's clips 0 "closed", 1 "open"): 1 open
    /// (create_untransitioned pauses "closed" at its start), 2 shut (on_transition plays "closed"; back to 1 plays
    /// "open"). The gate's the other way round (LabyrinthIzaroDoor.ot): 1 shut ("open" paused at its start), 2 open.</summary>
    public const byte DoorOpen = 1, DoorShut = 2, GateShut = 1, GateOpened = 2;

    /// <summary>An alcove's railing (BossObjectiveBlocker, alcove_railing.ao; same components as the doors): 1 up and
    /// blocked, 2 lowered (its "down" clip, 3.3 s), for a mechanic's monsters to come out of the alcove.</summary>
    public const byte RailingUp = 1, RailingDown = 2;

    /// <summary>Izaro's Transitionable (LabyrinthBoss.ot, num_states 5): he is created in 5 (the Hide clip at its end:
    /// down on the lowered elevator, untargetable), 2 is the fight (EnableTargetable), 4 the trial won (untargetable,
    /// Boss_Resolve.ogg).</summary>
    public const byte Fighting = 2, Defeated = 4;

    /// <summary>His rise (rig.amd lift_raise_*, 10.733 s; the elevator's raise 10.666 s) and his going down (lift_lower_*,
    /// 3.333 s, the elevator's lower).</summary>
    public const double RiseSeconds = 10.733, LowerSeconds = 3.333;

    /// <summary>When a trial ends he walks to the elevator's middle (the monsters' Move, 0x2909) and stands there a moment
    /// before it lowers, at least as long as his line takes (ours, about its length; live, the original).</summary>
    public const ushort MoveSkill = 0x2909;
    public const double StandSeconds = 1.5, LineSeconds = 3.5;

    /// <summary>The longest wait for the swing or skill he is in when a trial ends, before he walks (his slowest skills
    /// take about 3.8 s).</summary>
    public const double SwingWaitSeconds = 3.8;

    /// <summary>Izaro's three weapons (MonsterVarieties), in the order of the official pick
    /// (<see cref="GameServer.Data.Terrain.Generation.LabyrinthLayout.BossWeapon"/>): the two-handed mace, sword and shield,
    /// two swords.</summary>
    /// <summary>How he moves: LabyrinthBoss.ot's <c>base_movement_velocity_+%</c> 15 (the client walks him at 41 cells/s where
    /// the row's speed says 36, measured with trace-sync.js), through his minions (the original, live), and ordered again at
    /// most once a second while he is on his way (ours).</summary>
    public static readonly MonsterMovement Movement = new() { SpeedFactor = 1.15, ThroughBodies = true, ReorderSeconds = 1 };

    public static readonly string[] Varieties =
    [
        "Metadata/Monsters/Labyrinth/LabyrinthBossMaul",
        "Metadata/Monsters/Labyrinth/LabyrinthBossOneHandAndShield",
        "Metadata/Monsters/Labyrinth/LabyrinthBossDualWield",
    ];

    /// <summary>His going-down clip for a weapon: Art/Models/MONSTERS/Izaro/IzaroBody/rig.amd's lift_lower_maul,
    /// lift_lower_sword, lift_lower_sword_sword (40-42; the Hide animation his .act names, which the server's
    /// on_transition_to_4 plays).</summary>
    public static uint LowerClip(int weapon) => (uint)(40 + weapon);

    /// <summary>The instance's state once its trial is won: the doors stay open, the ways out shown, no Izaro.</summary>
    public static readonly string Beaten = Quests.InstanceFlag("IzaroBeaten");

    /// <summary>The flag the last trial of a tier sets (Labyrinths QuestFlag: LabyrinthComplete1, 2, 3, the Eternal's
    /// LabyrinthCompleteMaps), with LabyrinthKilledIzaro.</summary>
    public static IReadOnlyList<string> CompleteFlags(int tier) =>
        ["LabyrinthKilledIzaro", tier switch { 1 => "LabyrinthComplete1", 2 => "LabyrinthComplete2", 3 => "LabyrinthComplete3", _ => "LabyrinthCompleteMaps" }];

    /// <summary>An Aspirant's Trial: its number of the layout's sections (<see cref="Of"/>), Izaro's weapon and the tier.</summary>
    public sealed record Trial(int Number, int Of, int Weapon, int Tier, string? Mechanic = null, IReadOnlyList<string>? Secrets = null)
    {
        public bool Last => Number >= Of;
        /// <summary>His life when it starts: what the earlier trials left (100, 66, 33 of 100 with three).</summary>
        public int StartPercent => 100 * (Of - Number + 1) / Of;
        /// <summary>Where he leaves (66, 33; 0 for the last: he dies).</summary>
        public int LeavesAtPercent => 100 * (Of - Number) / Of;
        public string Variety => Varieties[Weapon];
    }

    /// <summary>The trial in this instance of an area, or null outside Izaro's arenas of today's Labyrinth.</summary>
    public static Trial? For(string areaId, uint instanceSeed) =>
        LabyrinthDay.IzaroRoom(areaId, instanceSeed) is { } room
            ? new Trial(room.Number, room.Of, WeaponOf(room.Seed, room.Difficulty), room.Difficulty, SecretId(room.Secret),
                [.. new[] { SecretId(room.Secret), SecretId(room.Secret2) }.OfType<string>()]) : null;

    /// <summary>The trials of a day's Labyrinth in section order, with their rooms' areas.</summary>
    public static IEnumerable<(string? Area, Trial Trial)> Trials(uint seed, int difficulty)
    {
        var day = LabyrinthDay.For(seed, difficulty);
        foreach (var (node, number, of) in LabyrinthDay.IzaroRooms(day))
        {
            var n = day.Layout.Nodes[node];
            yield return (day.Areas[node], new Trial(number, of, WeaponOf(seed, difficulty), difficulty, SecretId(n.Secret1),
                [.. new[] { SecretId(n.Secret1), SecretId(n.Secret2) }.OfType<string>()]));
        }
    }

    /// <summary>The day's weapon for a difficulty, as the official server picks it.</summary>
    public static int WeaponOf(uint seed, int difficulty) => GameServer.Data.Terrain.Generation.LabyrinthLayout.BossWeapon(seed, difficulty);

    /// <summary>The NPCs row that speaks his lines (Metadata/NPC/Izaro, 147).</summary>
    public static ushort Npc => Tables.Value.Npc;

    /// <summary>What Izaro says (S→C 0x1c7, from <paramref name="objectId"/>, 0: globally): the NPCTextAudio row of the
    /// id, or null when the table does not have it.</summary>
    public static byte[]? Speech(uint objectId, string line) =>
        Tables.Value.Lines.TryGetValue(line, out ushort row) ? InstanceNpcs.TextAudio(objectId, Npc, row) : null;

    /// <summary>His line as he rises: the arena's mechanic's (IzaroStart&lt;mechanic&gt;, by its LabyrinthSecrets Id); none
    /// for a mechanic without one (Power Symbols, Pedestals) or a mechanic's second part (the "…2" rows, no room).</summary>
    public static string? StartLine(Trial trial) => trial.Mechanic switch
    {
        string m when m.EndsWith('2') => null,
        string m when m.StartsWith("Lieutenants", StringComparison.Ordinal) => "IzaroStartLieutenants",
        string m when m.StartsWith("Gargoyles", StringComparison.Ordinal) => "IzaroStartGargoyles",
        string m when m.StartsWith("ToggleStatues", StringComparison.Ordinal) => "IzaroStartToggleStatues",
        string m when m.StartsWith("PowerPylons", StringComparison.Ordinal) => "IzaroStartPylons",
        string m when m.StartsWith("Elemental", StringComparison.Ordinal) => "IzaroStartElementals",
        string m when m.StartsWith("Portals", StringComparison.Ordinal) => "IzaroStartPortals",
        string m when m.StartsWith("ChronoStatues", StringComparison.Ordinal) => "IzaroStartChronosStatues",
        string m when m.StartsWith("ChargeDisruptors", StringComparison.Ordinal) => "IzaroStartChargeDisruptors",
        _ => null,
    };

    /// <summary>His line as he leaves a trial (IzaroFlee1-6: Iz_03_Fight1*_Outro*.ogg; 7-8: Fight2C) or dies in the last
    /// (IzaroDeath1-6), at random.</summary>
    public static string EndLine(Trial trial) =>
        trial.Last ? $"IzaroDeath{Random.Shared.Next(1, 7)}" : trial.Number == 1 ? $"IzaroFlee{Random.Shared.Next(1, 7)}" : $"IzaroFlee{Random.Shared.Next(7, 9)}";

    /// <summary>The instance's state once the corridor's gate is open: it stays open, the way into the arena shows.</summary>
    public static readonly string GateOpen = Quests.InstanceFlag("IzaroGate");

    static string? SecretId(int row) => row < 0 ? null : Tables.Value.Secrets.ElementAtOrDefault(row);

    // NPCs (his row), NPCTextAudio (his lines by Id) and LabyrinthSecrets (Ids), from the client tables.
    static readonly Lazy<(ushort Npc, Dictionary<string, ushort> Lines, string[] Secrets)> Tables = new(() =>
    {
        ushort npc = 147;
        var lines = new Dictionary<string, ushort>(StringComparer.Ordinal);
        string[] secrets = [];
        static IEnumerable<string?> Ids(string table) => [.. ClientTables.Table(table).Select(r => r.Has("Id") ? r.Text("Id") : null)];
        try
        {
            if (Ids("NPCs").ToList().IndexOf("Metadata/NPC/Izaro") is int row and >= 0) npc = (ushort)row;
            int r = 0;
            foreach (string? id in Ids("NPCTextAudio"))
            {
                if (id is not null && id.StartsWith("Izaro", StringComparison.Ordinal)) lines.TryAdd(id, (ushort)r);
                r++;
            }
            secrets = [.. Ids("LabyrinthSecrets").Select(s => s ?? "")];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Console.WriteLine($"[gateway] Izaro's lines: {ex.Message}");
        }
        return (npc, lines, secrets);
    });

    /// <summary>The 0x2bb blob of the elevator or a door (<see cref="LabyrinthTrials.Blob(IReadOnlyList{string}, string, int,
    /// int, float, float, byte, float?, bool, bool)"/>): a shut door blocks; the elevator never does.</summary>
    public static byte[] Blob(string type, string model, int x, int y, float angle, byte state) =>
        LabyrinthTrials.Blob(type == Gate ? GateComponents : Components, model, x, y, angle, 1, state, null, blockage: true,
            blocked: type == ArenaDoor ? state == DoorShut : type == Gate ? state == GateShut : type == Railing ? state == RailingUp
                : LiftMoving(state));

    /// <summary>An elevator (Izaro's or a small one: their states) cannot be stood on while it moves (the original, live
    /// 2026-11-07): its blockage is on while it raises or lowers.</summary>
    public static bool LiftMoving(byte state) => state is Raising or Lowering;

    /// <summary>How far round an elevator's middle it blocks while moving (cells: its .ao's walk_tri hull, ElevatorLarge
    /// about 21, ElevatorSmall about 11).</summary>
    public static int LiftRadius(string type) => type == Elevator ? 21 : 11;
}
