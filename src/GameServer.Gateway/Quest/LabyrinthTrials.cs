using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The Trials of Ascendancy (Agents/docs/game/labyrinth-trials.md): LabyrinthTrials.dat names each trial's area. Its
/// rooms (the area's <c>labyrinth_intro</c> / <c>labyrinth_end</c> nodes) hold the door, the levers and doors, the
/// plaque and the portal back as server object lines. The client counts a trial done when the character has the row's
/// quest flag (column 7, e.g. NormalLabyrinthCompletedPrison) or the league quest flag of column 1 (0x141f69230).
/// </summary>
static class LabyrinthTrials
{
    public sealed record Trial(string Area, string Flag, int LeagueFlag);

    public const string IntroDoor = "Metadata/Terrain/Labyrinth/Objects/LabyrinthIntroDoor",
        Door = "Metadata/Terrain/Labyrinth/Objects/Puzzle_Parts/Door_Closed",
        // A plain door, opened by a click (the Crypt trial's bars between its upper spinner room and the plaque room:
        // the only room of the data with one).
        BarDoor = "Metadata/MiscellaneousObjects/Door",
        Lever = "Metadata/Terrain/Labyrinth/Objects/Puzzle_Parts/Switch_Once",
        // The Chamber of Sins trial's toggling lever and the cage it closes (num_states 2: 1 open, 2 closed).
        Switch = "Metadata/Terrain/Labyrinth/Objects/Puzzle_Parts/Switch",
        // The Crypt trial's other toggling lever (num_states 3, state 3 spent: not used).
        MultiSwitch = "Metadata/Terrain/Labyrinth/Objects/Puzzle_Parts/Switch_Multi",
        OpenDoor = "Metadata/Terrain/Labyrinth/Objects/Puzzle_Parts/Door_Open",
        Plaque = "Metadata/QuestObjects/Labyrinth/LabyrinthTrialPlaque",
        // The ElegantHouse tiles' thin walls a hit breaks (Smashable: transition_on_damage_taken, attackable; its
        // transition plays the break and removes its collision).
        SmashableWall = "Metadata/Terrain/Labyrinth/Objects/LabyrinthSmashableDoor",
        // The DryGarden tiles' secret wall (its transition opens it, shows its minimap icon) and the disguised levers by
        // it (hide_hover; num_states 3, 2 pulled).
        HiddenWall = "Metadata/Terrain/Labyrinth/Objects/HiddenDoor_Long",
        HiddenSwitch = "Metadata/Terrain/Labyrinth/Objects/HiddenDoor_Switch",
        Portal = "Metadata/Terrain/Labyrinth/Objects/LabyrinthTrialReturnPortal",
        // The Sarn Encampment statue's state: opened by its plaque with the Normal trials done (QuestObjects Act3Objects).
        // The server's own flag (no QuestFlags row): LabyrinthAccess, set with it, every character has through the plaza's
        // waypoint. /trials off clears both.
        SarnStatueOpened = "SarnLabyrinthStatueOpened", LabyrinthAccess = "LabyrinthAccess",
        // The glyph statue by a Labyrinth room's ways in and out (server_only, "MapLegendCurrentLocation"): a click opens
        // the Labyrinth map (0x7b kind 0x0f, LabyrinthMapWindow), which the client draws from the day's layout.
        Map = "Metadata/Terrain/Labyrinth/Objects/LabyrinthMap",
        SpikeTrap = "Metadata/Terrain/Labyrinth/Traps/LabyrinthCascadeSpikeTrap",
        ArrowTrap = "Metadata/Terrain/Labyrinth/Traps/LabyrinthArrowTrap",
        // The trials' Bronze Monographs (NPCs.dat LabyrinthTrialGlyphPrison, …Sins, …Church, …Crematorium, …Hedge,
        // …Catacombs), placed as NPCs.
        GlyphPrefix = "Metadata/NPC/Labyrinth/LabyrinthTrialGlyph";

    /// <summary>The Labyrinth's rooms' spike traps: the ElegantHouse cascade (<c>_EH</c>, <c>spiketrap_house.ao</c>,
    /// the trial's otherwise) and the ones a step fires (<c>LabyrinthSpikeTrap_Pressure</c>, <c>_EH_Pressure</c>:
    /// <c>Spikes02_Pressure</c>'s one tile and <c>spike_fast</c>, with <c>spiketrap_trigger.ao</c>'s click).</summary>
    public static bool IsSpikeTrap(string type) => type is SpikeTrap or SpikeTrap + "_EH" || IsPressureSpikeTrap(type);

    public static bool IsPressureSpikeTrap(string type) => type is "Metadata/Terrain/Labyrinth/Traps/LabyrinthSpikeTrap_Pressure"
        or "Metadata/Terrain/Labyrinth/Traps/LabyrinthSpikeTrap_EH_Pressure";

    /// <summary>How long after a step a pressure spike trap fires. Live (2026-11-01, the original): a step on one
    /// clicks (<c>spiketrap_trigger.ao</c>'s <c>Trigger</c> sound) and it rises about 1 s later, the others of its room
    /// on their own steps; it fires again only once the player has stepped off it and on again.</summary>
    public static double PressureSpikeDelaySeconds = 1;

    public const string PressureSpikeClick = "Metadata/Terrain/Labyrinth/Traps/spiketrap_trigger.ao";

    public const string Roomba = "Metadata/Terrain/Labyrinth/Traps/LabyrinthRoomba",
        FlyingRoomba = "Metadata/Terrain/Labyrinth/Traps/LabyrinthFlyingRoomba",
        Rail = "Metadata/Terrain/Labyrinth/Traps/Rail_Invisible",
        // The Chamber of Sins trial's sawblades and the track they run on (Rail_Visible: the track pieces, shown).
        Sawblade = "Metadata/Terrain/Labyrinth/Traps/LabyrinthSawblade",
        SlowSawblade = "Metadata/Terrain/Labyrinth/Traps/LabyrinthSawbladeSlow",
        VisibleRail = "Metadata/Terrain/Labyrinth/Traps/Rail_Visible",
        // The Crypt trial's spinners (spinning morning stars) and the track pieces that turn them back or switch their
        // way (SpinnerStopper_Straight, _TriCorner: Transitionable swaps the piece's animation).
        Spinner = "Metadata/Terrain/Labyrinth/Traps/LabyrinthSpinner",
        RailSwitch = "Metadata/Terrain/Labyrinth/Traps/Rail_Visible_Swap_Anims01",
        // The same with its two animations the other way round (state 1 plays its clip 1): the Labyrinth's spinner rooms'
        // SpinnerStopper_Corner2, under the spinner.
        RailSwitchSwapped = "Metadata/Terrain/Labyrinth/Traps/Rail_Visible_Swap_Anims10";

    public static bool IsRailSwitch(string type) => type is RailSwitch or RailSwitchSwapped;

    /// <summary>The way a swap piece (<see cref="RailSwitchSwapped"/>, a corner, <see cref="PlacedAngle"/>) leads in this
    /// state (direction (sin a, −cos a)): in state 1 its first arm, in 2 the other. A spinner on the piece runs the part of
    /// its track that way and turns back at the piece (live 2026-11-01, the original: the Labyrinth's spinner rooms start on
    /// the short part, round the track's corner). Its first arm is the line's angle; a mirrored room's piece, sent turned
    /// a quarter back, has it a quarter on.</summary>
    public static (double X, double Y) SwapWay(float angle, bool mirrored, bool transitioned)
    {
        double a = angle + ((mirrored ? 1 : 0) + (transitioned ? 1 : 0)) % 2 * Math.PI / 2;
        return (Math.Round(Math.Sin(a), 6), Math.Round(-Math.Cos(a), 6));
    }

    /// <summary>The state a swap piece is sent in for its logical one: state 1 (clip 1) shows its other arm (live
    /// 2026-11-01), and a mirrored room's the other way round (the client plays the unmirrored piece's clips, whose arms
    /// a mirrored room swaps).</summary>
    public static byte SwapClientState(bool mirrored, bool transitioned) => transitioned != mirrored ? Closed : Open;

    /// <summary>Whether a lever beside a swap piece stands on the side of the piece's first arm (else its other arm's).
    /// Each part of the track has its lever and the lever's gears; pulling the one by the part the spinner runs sends it
    /// to the other part, and that lever stays pulled, gears turning, until the other is pulled (live 2026-11-01, the
    /// original: the two levers switch each other off).</summary>
    public static bool OnFirstArm((int X, int Y) lever, (int X, int Y) swap, float angle, bool mirrored)
    {
        var (fx, fy) = SwapWay(angle, mirrored, false);
        var (sx, sy) = SwapWay(angle, mirrored, true);
        double dx = lever.X - swap.X, dy = lever.Y - swap.Y;
        return dx * fx + dy * fy >= dx * sx + dy * sy;
    }

    /// <summary>A room line's angle as the Gateway sends it: a corner piece (<c>spinner_rail_corner</c>,
    /// <c>SpinnerStopper_Corner</c>) in a mirrored room (orientation codes 4–7) turned a quarter back. The room's angle
    /// only mirrors the line's angle, and the client draws the piece unmirrored: a mirrored L is the L turned a quarter
    /// (live 2026-11-01: the Labyrinth's spinner rooms showed their corners facing away from the track).</summary>
    public static float PlacedAngle(string model, float angle, int code)
    {
        string name = Path.GetFileName(model);
        if (code is < 4 or >= 8 || !(name.StartsWith("spinner_rail_corner", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("SpinnerStopper_Corner", StringComparison.OrdinalIgnoreCase))) return angle;
        return (angle - MathF.PI / 2 + MathF.PI * 2) % (MathF.PI * 2);
    }

    /// <summary>The Crematorium trial's hot plates (one tile, three by three, one by three) and the gears a lever sets
    /// turning (TimerGears, <c>Cogs04_Quiet.ao</c>). The plates half in the walls (<c>_inert</c>: no Buffs) heat with the
    /// others but burn nothing themselves: an invisible <c>LabyrinthLavaAura_half</c> beside each (no model) burns their
    /// floor side (live 2026-11-01, the original: the lava glows under the grates all round the walls and the stairs'
    /// feet, and burns there, not on the stairs).</summary>
    public const string Lava = "Metadata/Terrain/Labyrinth/Traps/LabyrinthLavaAura",
        Lava3x3 = "Metadata/Terrain/Labyrinth/Traps/LabyrinthLavaAura_3x3",
        Lava1x3 = "Metadata/Terrain/Labyrinth/Traps/LabyrinthLavaAura_1x3",
        InertLava = "Metadata/Terrain/Labyrinth/Traps/LabyrinthLavaAura_inert",
        HalfLava = "Metadata/Terrain/Labyrinth/Traps/LabyrinthLavaAura_half",
        Gears = "Metadata/Terrain/Labyrinth/Objects/Puzzle_Parts/TimerGears";

    /// <summary>Whether this plate heats and cools (the client shows it).</summary>
    public static bool IsHotPlate(string type) => Plate(type) is Lava or Lava3x3 or Lava1x3 or InertLava;

    /// <summary>The Labyrinth's ElegantHouse rooms' plates (<c>LabyrinthLavaAura_EH</c>, <c>_1x3_EH</c>, <c>_3x3_EH</c>,
    /// <c>_inert_EH</c>) extend the trial's, with their own <c>hotplate*_EH.ao</c> (the room line's model): the
    /// trial's plate they are.</summary>
    public static string Plate(string type) => type.EndsWith("_EH", StringComparison.Ordinal) && type.StartsWith(Lava, StringComparison.Ordinal) ? type[..^3] : type;

    public static bool IsInertPlate(string type) => Plate(type) == InertLava;

    /// <summary>Whether its lava burns a player on it.</summary>
    public static bool Burns(string type) => Plate(type) is Lava or Lava3x3 or Lava1x3 or HalfLava;

    /// <summary>A plate's half sizes in cells, along its angle and across: a tile (23 cells) a side, the 3x3 three tiles,
    /// the 1x3 three tiles along its angle by one (the room's plates and walls meet them so, edge to edge); a half line
    /// (<c>invisible_halftile.ao</c>) half a tile along its angle, standing a quarter tile off its wall plate's centre.</summary>
    public static (double X, double Y) LavaHalf(string type) =>
        Plate(type) switch { Lava3x3 => (34.5, 34.5), Lava1x3 => (34.5, 11.5), HalfLava => (5.75, 11.5), _ => (11.5, 11.5) };

    /// <summary>Whether a cell lies on this hot plate.</summary>
    public static bool OnPlate(string type, int x, int y, float angle, (int X, int Y) at)
    {
        var (halfX, halfY) = LavaHalf(type);
        double dx = at.X - x, dy = at.Y - y, cos = Math.Cos(angle), sin = Math.Sin(angle);
        return Math.Abs(dx * cos + dy * sin) <= halfX && Math.Abs(-dx * sin + dy * cos) <= halfY;
    }

    /// <summary>How a hot plate cycles. Transitionable 2 plays <c>hotplate_start</c> (its rig's clip 2, 5958 ms) and then
    /// <c>hotplate_idle</c> (958 ms, looped: at its peak); 1 plays <c>hotplate_end</c> (6833 ms, cooling). How long it
    /// stays at its peak and cold is the server's timer: live (2026-11-01, the original) the room's plates go together,
    /// about 5 s at the peak and 6 s cold once cooled. <c>/trial_lava</c> (dev) changes them.</summary>
    public const double LavaHeatSeconds = 5.958, LavaCoolSeconds = 6.833;
    public static double LavaBurnSeconds = 5, LavaRestSeconds = 6;

    /// <summary>When the lava lies on a plate, which burns while it does (live 2026-11-01, the original: from when it
    /// spills over the plate until it has drained, the peak and part of the heating and cooling): the plate's sounds
    /// (<c>hotplate.ao</c>), its burning from 2812 ms into <c>hotplate_start</c> to its <c>End</c> at 3269 ms into
    /// <c>hotplate_end</c>.</summary>
    public const double LavaSpillSeconds = 2.812, LavaDrainSeconds = 3.269;

    /// <summary>What a plate deals a second while the player stands in its lava: BuffDefinitions
    /// <c>labyrinth_lava_aura</c> ("Lava", invisible, BuffVisuals <c>lava_aura_ignited</c>: the burning look) takes
    /// <c>base_fire_damage_%_of_maximum_life_taken_per_minute_in_normal</c> and <c>…_of_maximum_es_…</c>, whose values
    /// the client data lacks: these shares are ours, the roomba's, as fire damage (wiki: less the player's fire
    /// resistance), energy shield first.</summary>
    public static (double Life, double EnergyShield) LavaDamagePerSecond = (0.40, 0.50);

    /// <summary>The <c>labyrinth_lava_aura</c> BuffDefinitions row, or null without the table.</summary>
    public static ushort? LavaBuffRow => BuffRows.Value.TryGetValue("labyrinth_lava_aura", out ushort row) ? row : null;

    public static bool IsSawblade(string type) => type is Sawblade or SlowSawblade;

    /// <summary>A roomba's, sawblade's or spinner's speed in cells a second: its .ot's Movement <c>base_speed</c> (the
    /// player's Pathfinding base_speed 37 is 37 cells a second), and how long it waits at each rail
    /// (<c>pause_at_waypoint_duration</c>: the roombas 100 ms, the sawblades and spinners none).</summary>
    public static double RoombaSpeed(string type) => type switch { FlyingRoomba => 25, Sawblade => 28, SlowSawblade => 16, Spinner => 24, _ => 35 };
    public static double RoombaPauseSeconds(string type) => IsSawblade(type) || type == Spinner ? 0 : 0.1;

    /// <summary>Whether it turns to where it goes (the roombas' <c>rotate_to_movement_direction</c>): a sawblade or a
    /// spinner keeps its room line's angle.</summary>
    public static bool TurnsToMovement(string type) => type is Roomba or FlyingRoomba;

    /// <summary>How near a roomba's centre the player is in it (our number, from its look: about two tiles across; the
    /// model has no Hull, and it does not block: live 2026-10-31).</summary>
    public static double RoombaRadius = 10;

    /// <summary>What a roomba deals while the player is in it (live 2026-10-31, the original: damage all the time, no
    /// buff shown, no knockback): BuffDefinitions <c>roomba_damage</c> ("Death Rhoomba", invisible; the flying one's
    /// <c>flying_roomba_damage</c>) takes <c>base_physical_damage_%_of_maximum_life_taken_per_minute</c> and
    /// <c>…_of_maximum_energy_shield_…</c>, whose values the client data lacks: these shares a second are ours, physical,
    /// energy shield first, fitted live to the original's (2026-11-01).
    /// <c>/trial_roomba_damage</c> (dev) changes them.</summary>
    public static (double Life, double EnergyShield) RoombaDamagePerSecond = (0.40, 0.50);

    /// <summary>A sawblade's cut, as a roomba's (<see cref="RoombaDamagePerSecond"/>): BuffDefinitions
    /// <c>sawblade_damage</c> (invisible, BuffVisuals <c>sawblade_damage</c> playing
    /// <c>labyrinth/traps/sawblade/sawblood.epk</c>) takes the same two stats, with no values in the client data. Our
    /// numbers: the roomba's shares, within a lane of its track (the tracks' two lanes are 9 cells apart).
    /// <c>/trial_saw_damage</c> (dev) changes them.</summary>
    public static (double Life, double EnergyShield) SawDamagePerSecond = (0.40, 0.50);
    public static double SawRadius = 5;

    /// <summary>How near a spinner's centre its morning stars reach: its mesh's bounding box
    /// (<c>spinning_morning_star_v01_01.sm</c>, about ±100 units). It cuts as a roomba does (its .ot is the roomba's,
    /// with <c>roomba.ao</c> for its model, and names no buff of its own), our reading.</summary>
    public static double SpinnerRadius = 9;

    public static (double Life, double EnergyShield) TrapDegenShares(string type) => IsSawblade(type) ? SawDamagePerSecond : RoombaDamagePerSecond;
    public static double TrapRadius(string type) => IsSawblade(type) ? SawRadius : type == Spinner ? SpinnerRadius : RoombaRadius;

    /// <summary>A roomba's 0x2b9 blob: LabyrinthRoomba.ot reads Positioned, Animated, BaseEvents, Stats, Buffs, Movement,
    /// Transitionable; LabyrinthFlyingRoomba.ot Positioned, BaseEvents, Animated, Stats, Buffs, Movement; a sawblade's
    /// (LabyrinthSawblade.ot, …Slow) Positioned, BaseEvents, Animated, Stats, Buffs, Movement, Transitionable, the same
    /// bytes. Movement (<c>0x141e741e0</c>): u8 (+0x30), i32 x, i32 y (the cell it sets Positioned to), four u32 (+0x3c,
    /// +0x40, +0x4c, +0x50), u8 +0x76 (non-zero starts its own movement, <c>0x141e74970</c>: not sent), u8 +0x77. The
    /// server moves it with 0x1ba. Transitionable 2 is a stopped sawblade (<c>create_transitioned</c>: its spin wound down).
    /// </summary>
    public static byte[] RoombaBlob(string type, int x, int y, float angle, string model, byte state = Closed)
    {
        var w = new BlobWriter().U8(0).U8(0);
        w.I32(x).I32(y).F32(angle).U16(14).U8(0).U8(0x02).U8(0).U8(1);    // Positioned: the .ot's team 14
        w.U8(1).WStr32(model);                                              // Animated (BaseEvents reads nothing)
        w.Stats(null).U32(0).U32(0).U8(0);                                  // Stats
        w.U16(0);                                                           // Buffs
        w.U8(0).I32(x).I32(y).U32(0).U32(0).U32(0).U32(0).U8(0).U8(0);      // Movement
        if (type != FlyingRoomba) w.U8(state);                              // Transitionable: 1 active
        return w.U8(0).ToArray();                                           // tasks
    }

    /// <summary>Transitionable's index in a roomba's or sawblade's components (<see cref="RoombaBlob"/>).</summary>
    public const byte RoombaTransitionableIndex = 6;

    /// <summary>A spinner's 0x2b9 blob: LabyrinthSpinner.ot reads Positioned, Animated, BaseEvents, Stats, Movement,
    /// Buffs, StateMachine; written as the roomba's, with the room line's model (<c>spinning_morning_star_v01_01.ao</c>:
    /// <c>spin</c> plays by default) and StateMachine's one shared state, <c>killswitch</c> 0 (1 plays its
    /// <c>breakage</c> clip to the end: not sent).</summary>
    public static byte[] SpinnerBlob(int x, int y, float angle, string model)
    {
        var w = new BlobWriter().U8(0).U8(0);
        w.I32(x).I32(y).F32(angle).U16(14).U8(0).U8(0x02).U8(0).U8(1);    // Positioned: the .ot's team 14
        w.U8(1).WStr32(model);                                              // Animated (BaseEvents reads nothing)
        w.Stats(null).U32(0).U32(0).U8(0);                                  // Stats
        w.U8(0).I32(x).I32(y).U32(0).U32(0).U32(0).U32(0).U8(0).U8(0);      // Movement
        w.U16(0);                                                           // Buffs
        new StateMachineData([0], 0).Write(w);                              // StateMachine: killswitch 0
        return w.U8(0).ToArray();                                           // tasks
    }

    /// <summary>A visible rail's 0x2b9 blob (Rail_Visible.ot: Positioned, BaseEvents, Animated): the room line's track
    /// piece, at its angle.</summary>
    public static byte[] RailBlob(int x, int y, float angle, string model)
    {
        var w = new BlobWriter().U8(0).U8(0);
        w.I32(x).I32(y).F32(angle).U16(0).U8(0).U8(0x02).U8(0).U8(1);      // Positioned
        w.U8(1).WStr32(model);                                              // Animated
        return w.U8(0).ToArray();                                           // tasks
    }

    /// <summary>The sawblades a Chamber of Sins lever stops. Live (2026-11-01, the original): each lever stops one
    /// sawblade of every pair (two side by side on a track's lanes, 9 cells apart), the lever nearer the trial's door the
    /// inner lane's (nearer the levers), the other the outer lane's; a sawblade alone on its track runs on. No file links
    /// them (the real server's are its own), so this is fitted to the original: levers ranked by their distance from the
    /// trial's door, lanes by theirs from the levers' middle.</summary>
    public static List<int> SwitchedSaws(IReadOnlyList<(int X, int Y)> saws, IReadOnlyList<(int X, int Y)> levers, int lever,
        (int X, int Y) entrance)
    {
        static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        int rank = Enumerable.Range(0, levers.Count).OrderBy(i => Distance(levers[i], entrance)).ToList().IndexOf(lever);
        (double X, double Y) middle = (levers.Average(l => l.X), levers.Average(l => l.Y));
        var stopped = new List<int>();
        for (int i = 0; i < saws.Count; i++)
            for (int j = i + 1; j < saws.Count; j++)
            {
                if (Distance(saws[i], saws[j]) > 12) continue;
                int[] lanes = [.. new[] { i, j }.OrderBy(k => Distance(saws[k], middle))];
                if (rank is >= 0 and < 2) stopped.Add(lanes[rank]);
            }
        return [.. stopped.Order()];
    }

    /// <summary>A roomba's way along the trial's invisible rails: its rails in order, and whether they close a ring.</summary>
    /// <remarks>A ring goes on from its rail <see cref="Loop"/> after its last (the rails before it are a way in, run
    /// once); <see cref="Jumps"/> are its legs a roomba teleports along (the room links' <c>teleport</c>); with
    /// <see cref="FromFirst"/> it heads for its first rail first (its <c>reset</c> rail), else for the far rail of the run
    /// it stands on.</remarks>
    public sealed record RoombaPath(IReadOnlyList<(int X, int Y)> Rails, bool Ring)
    {
        public int Loop { get; init; }
        public IReadOnlySet<((int X, int Y) From, (int X, int Y) To)> Jumps { get; init; } = new HashSet<((int, int), (int, int))>();
        public bool FromFirst { get; init; }

        /// <summary>A chain turned so that it ends at its end nearer this cell, which a sawblade heads for first: the
        /// Chamber of Sins trial's first two, each nearer one end of their track, part from its middle (live 2026-11-01,
        /// the original), and each pair goes together. A sawblade's room line angle lies along its track either way
        /// (<c>orient_to_first_movement</c>).</summary>
        public RoombaPath TowardNearerEnd((int X, int Y) at)
        {
            static long Distance((int X, int Y) a, (int X, int Y) b) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y);
            return Ring || Distance(at, Rails[^1]) <= Distance(at, Rails[0]) ? this : this with { Rails = [.. Rails.Reverse()] };
        }
    }

    /// <summary>Each roomba's path (null when no rail runs under it), its rails in the order it goes. Rails join their
    /// nearest rail along each axis, within 6° (one of the Catacombs' runs is 5° off), when each is the other's nearest
    /// that way. A roomba stands on a run and faces along it, the way it sets out (its room line's angle, as a projectile
    /// object's rotation: direction (sin a, −cos a)): where two runs cross under it (the Catacombs' first ground roomba
    /// and the flying one's) the one along its facing is its own. Round a ring it keeps going that way (the Catacombs'
    /// square of four all face round it; live 2026-10-31, the original: clockwise on the screen); a chain it follows to
    /// its end and back (<c>path_following_method = "reverse_when_path_ends"</c>).</summary>
    /// <remarks>A visible rail (the Chamber of Sins' track pieces, <paramref name="railAngles"/>; NaN for an invisible one)
    /// joins along its own piece only, so the ends of a track's two lanes, 9 cells apart, do not close a ring.
    /// In the Labyrinth's own rooms (<paramref name="scratches"/>, the rooms' roomba scratch decals) the runs go any way:
    /// a roomba takes the nearest run of its kind (<see cref="ScratchedRuns"/>), not only one under it.</remarks>
    /// <remarks>With <paramref name="joins"/> (the room links between the rails, either way) the rails join so, and
    /// nothing else (no scratches, no angles); a <c>teleport</c> join (<paramref name="jumps"/>) is a leg the roomba jumps.
    /// </remarks>
    public static RoombaPath?[] RoombaPaths(IReadOnlyList<(int X, int Y)> rails, IReadOnlyList<(int X, int Y, float Angle)> roombas,
        IReadOnlyList<float>? railAngles = null, IReadOnlyList<(int X, int Y)>? scratches = null, IReadOnlyList<bool>? flying = null,
        IReadOnlyList<(int A, int B)>? joins = null, IReadOnlyList<(int A, int B)>? jumps = null)
    {
        const double MaxDeviation = 6 * Math.PI / 180, OnRail = 4;
        (int X, int Y)[] axes = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        int? Best(int i, (int X, int Y) d)
        {
            if (railAngles is not null && !float.IsNaN(railAngles[i])
                && Math.Abs(d.X * Math.Sin(railAngles[i]) - d.Y * Math.Cos(railAngles[i])) < Math.Cos(Math.PI / 4)) return null;
            int? best = null;
            (double Deviation, double Distance) bestKey = (double.MaxValue, double.MaxValue);
            for (int j = 0; j < rails.Count; j++)
            {
                if (j == i) continue;
                double vx = rails[j].X - rails[i].X, vy = rails[j].Y - rails[i].Y;
                double along = vx * d.X + vy * d.Y, across = Math.Abs(vx * d.Y - vy * d.X);
                if (along <= 0) continue;
                double deviation = Math.Atan2(across, along);
                if (deviation > MaxDeviation) continue;
                var key = (deviation, along);
                if (key.CompareTo(bestKey) < 0) (best, bestKey) = (j, key);
            }
            return best;
        }
        var joined = Enumerable.Range(0, rails.Count).Select(_ => new List<int>()).ToArray();
        void Join(int i, int j)
        {
            if (i == j || joined[i].Contains(j)) return;
            joined[i].Add(j);
            joined[j].Add(i);
        }
        if (joins is not null)
        {
            foreach (var (a, b) in joins) Join(a, b);
            scratches = null;
        }
        else
            for (int i = 0; i < rails.Count; i++)
                foreach (var d in axes)
                    if (Best(i, d) is int j && Best(j, (-d.X, -d.Y)) == i) Join(i, j);
        var leaps = new HashSet<((int, int), (int, int))>();
        foreach (var (a, b) in jumps ?? []) { leaps.Add((rails[a], rails[b])); leaps.Add((rails[b], rails[a])); }
        static double ToSegment((int X, int Y) p, (int X, int Y) a, (int X, int Y) b) => OnSegment(p, a, b).Distance;
        var runs = scratches is null ? default : ScratchedRuns(rails, scratches);
        var paths = new RoombaPath?[roombas.Count];
        for (int r = 0; r < roombas.Count; r++)
        {
            var (x, y, angle) = roombas[r];
            var network = joined;
            double reach = OnRail;
            if (runs.Scratched is not null && (flying?[r] == true ? runs.Flown : runs.Scratched) is var own
                && Enumerable.Range(0, rails.Count).Any(i => own[i].Any(j => ToSegment((x, y), rails[i], rails[j]) <= RunReach)))
                (network, reach) = (own, RunReach);
            double fx = Math.Sin(angle), fy = -Math.Cos(angle);
            // How far a run's way (A to B) lies along the facing: 1 straight ahead, -1 straight behind.
            double Along(int a, int b)
            {
                double dx = rails[b].X - rails[a].X, dy = rails[b].Y - rails[a].Y, len = Math.Sqrt(dx * dx + dy * dy);
                return len == 0 ? 0 : (dx * fx + dy * fy) / len;
            }
            var under = Enumerable.Range(0, rails.Count).SelectMany(i => network[i].Where(j => j > i).Select(j => (A: i, B: j)))
                .Select(e => (e.A, e.B, Distance: ToSegment((x, y), rails[e.A], rails[e.B]), Along: Along(e.A, e.B)))
                .Where(e => e.Distance <= reach)
                .OrderByDescending(e => reach == OnRail && Math.Abs(e.Along) >= Math.Cos(Math.PI / 6)).ThenBy(e => e.Distance).FirstOrDefault();
            if (under == default) continue;
            if (under.Along < 0) (under.A, under.B) = (under.B, under.A);
            // The run's rails in order: from one end of a chain, or round a ring.
            var seen = new HashSet<int> { under.A };
            var order = new List<int> { under.A };
            for (int at = under.A, next = under.B; next >= 0 && seen.Add(next); )
            {
                order.Add(next);
                (at, next) = (next, network[next].FirstOrDefault(j => j != at && !seen.Contains(j), -1));
            }
            for (int at = under.B, back = under.A; ; )
            {
                int prev = network[back].FirstOrDefault(j => j != at && !seen.Contains(j), -1);
                if (prev < 0) break;
                seen.Add(prev);
                order.Insert(0, prev);
                (at, back) = (back, prev);
            }
            bool ring = order.Count > 2 && network[order[0]].Contains(order[^1]);
            paths[r] = new RoombaPath([.. order.Select(i => rails[i])], ring) { Jumps = leaps };
        }
        return paths;
    }

    /// <summary>A Labyrinth room's roomba's way by the room links: from its <c>reset</c> rail along each rail's <c>next</c>
    /// (else its <c>teleport</c>, a jump) until a rail comes again, where it goes round from then on, or none follows,
    /// where it goes back (<c>reverse_when_path_ends</c>). The links are its way: indoor_trap_var2_2's ground roomba comes
    /// in from its rail to a ring of three, one side slanting, and its flying one to the same three the other way round;
    /// 2x2_trap_05's ring of four jumps from its third rail back to its first.</summary>
    public static RoombaPath LinkedPath(IReadOnlyList<(int X, int Y)> rails, IReadOnlyList<(int From, int To, bool Jump)> links, int start)
    {
        var order = new List<int> { start };
        var jumps = new HashSet<((int, int), (int, int))>();
        for (int at = start; ;)
        {
            var steps = links.Where(l => l.From == at && l.To != at).OrderBy(l => l.Jump).ToList();
            if (steps.Count == 0) return new RoombaPath([.. order.Select(i => rails[i])], false) { Jumps = jumps, FromFirst = true };
            var step = steps[0];
            if (step.Jump) jumps.Add((rails[at], rails[step.To]));
            if (order.IndexOf(step.To) is int seen and >= 0)
                return new RoombaPath([.. order.Select(i => rails[i])], true) { Loop = seen, Jumps = jumps, FromFirst = true };
            order.Add(step.To);
            at = step.To;
        }
    }

    /// <summary>How far from its run a Labyrinth room's roomba may stand (its room line is placed roughly: the
    /// OutdoorHouse indoor_trap_var2_2's ground roomba stands 13 cells off its ring).</summary>
    const double RunReach = 25;

    /// <summary>The runs of a Labyrinth room's invisible rails. No file links them (the real server's paths are its
    /// own): a ground roomba's runs are the ones its room's scratch decals (<c>LabyrinthRoombaKit/R_scratch_*</c>) lie
    /// along, any way (diagonals too), between rails at most 100 cells apart with no rail between them, the most scratched
    /// first and none crossing another; a flying roomba scratches nothing, and the rails left join shortest first, at most
    /// two runs a rail. Live (2026-11-01, the original):
    /// OutdoorHouse's 2x2_trap_05 runs chains of four with right-angle turns and a slant, and its indoor_trap_var2_2 a
    /// ground ring of three (one diagonal) beside a flying ring of four, with scratches along every ground run.</summary>
    public static (List<int>[] Scratched, List<int>[] Flown) ScratchedRuns(IReadOnlyList<(int X, int Y)> rails, IReadOnlyList<(int X, int Y)> scratches)
    {
        const double Lane = 3, Longest = 100;
        var scratched = Enumerable.Range(0, rails.Count).Select(_ => new List<int>()).ToArray();
        var flown = Enumerable.Range(0, rails.Count).Select(_ => new List<int>()).ToArray();
        static bool Inside((double T, double Distance) at) => at.T is > 0.05 and < 0.95 && at.Distance <= Lane;
        var candidates = new List<(int A, int B)>();
        for (int i = 0; i < rails.Count; i++)
            for (int j = i + 1; j < rails.Count; j++)
            {
                double dx = rails[i].X - rails[j].X, dy = rails[i].Y - rails[j].Y;
                if (rails[i] != rails[j] && dx * dx + dy * dy <= Longest * Longest
                    && !Enumerable.Range(0, rails.Count).Any(k => rails[k] != rails[i] && rails[k] != rails[j] && Inside(OnSegment(rails[k], rails[i], rails[j]))))
                    candidates.Add((i, j));
            }
        // Runs do not cross: the most scratched first, and none across one taken (a run over a ring's slant passes a
        // scratch of it too).
        var taken = new List<(int A, int B)>();
        foreach (var (i, j, _) in candidates.Select(e => (e.A, e.B, Marks: scratches.Count(c => Inside(OnSegment(c, rails[e.A], rails[e.B])))))
                     .Where(e => e.Marks > 0).OrderByDescending(e => e.Marks).ThenBy(e => e.A).ThenBy(e => e.B))
        {
            if (taken.Any(t => Cross(rails[i], rails[j], rails[t.A], rails[t.B]))) continue;
            taken.Add((i, j));
            scratched[i].Add(j);
            scratched[j].Add(i);
        }
        // The rest, shortest first: at most two runs a rail, none crossing (a flying roomba's ring of four).
        long Distance(int a, int b) => (long)(rails[a].X - rails[b].X) * (rails[a].X - rails[b].X) + (long)(rails[a].Y - rails[b].Y) * (rails[a].Y - rails[b].Y);
        var flights = new List<(int A, int B)>();
        foreach (var (i, j) in candidates.Where(e => scratched[e.A].Count == 0 && scratched[e.B].Count == 0).OrderBy(e => Distance(e.A, e.B)))
        {
            if (flown[i].Count >= 2 || flown[j].Count >= 2 || flights.Any(t => Cross(rails[i], rails[j], rails[t.A], rails[t.B]))) continue;
            flights.Add((i, j));
            flown[i].Add(j);
            flown[j].Add(i);
        }
        return (scratched, flown);
    }

    /// <summary>Whether two segments cross away from their ends.</summary>
    static bool Cross((int X, int Y) a, (int X, int Y) b, (int X, int Y) c, (int X, int Y) d)
    {
        static long Side((int X, int Y) p, (int X, int Y) q, (int X, int Y) r) => Math.Sign((long)(q.X - p.X) * (r.Y - p.Y) - (long)(q.Y - p.Y) * (r.X - p.X));
        return Side(a, b, c) * Side(a, b, d) < 0 && Side(c, d, a) * Side(c, d, b) < 0;
    }

    /// <summary>Where along a segment (0 to 1) a cell lies nearest, and how far from it.</summary>
    static (double T, double Distance) OnSegment((int X, int Y) p, (int X, int Y) a, (int X, int Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
        double t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
        return (t, Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2)));
    }

    /// <summary>The Crypt trial's spinner track: its server track pieces (Rail_Visible and the Rail_Visible_Swap_Anims01
    /// stoppers; the straight pieces between them are the client's doodads) and the ways each leads, by its model and
    /// angle (direction (sin a, −cos a)): an end one, back from a; a corner a and a + 90°; a crossing four; a straight
    /// stopper a and a + 180°; the TriCorner stopper a and a ± 90°. A way joins the nearest piece along it (within 6°,
    /// the X of two diagonal runs too) that has a way back. A spinner goes piece to piece and decides at each
    /// (<see cref="Next"/>): back at an end and at a straight stopper in state 1 (its wedge up, <c>activate</c>), on
    /// round a corner, and at a junction straight on when the junction's switch (a TriCorner in state 2,
    /// <c>switch_straight</c>; a crossing follows its track's TriCorner) lets it, else round the way it turns its corners.
    /// Live (2026-11-01, the original): the two spinners in the upper room go round two small rings (the middle of the
    /// square), and its lever switches them to the square.</summary>
    public sealed class SpinnerTrack
    {
        public IReadOnlyList<(int X, int Y)> Rails { get; }
        readonly List<(int Rail, double X, double Y)>[] ways;
        readonly bool[] stopper;
        readonly int[] gate;
        readonly ((double X, double Y) First, (double X, double Y) Other)?[] swaps;

        SpinnerTrack(IReadOnlyList<(int X, int Y)> rails, List<(int, double, double)>[] ways, bool[] stopper, int[] gate,
            ((double X, double Y), (double X, double Y))?[] swaps) =>
            (Rails, this.ways, this.stopper, this.gate, this.swaps) = (rails, ways, stopper, gate, swaps);

        /// <summary>The track of these pieces (others than a spinner track's are left out: null when none is one).</summary>
        public static SpinnerTrack? Build(IReadOnlyList<(int X, int Y, float Angle, string Model)> pieces, IReadOnlyList<bool>? mirrored = null)
        {
            static (double X, double Y) Way(double a) => (Math.Round(Math.Sin(a), 6), Math.Round(-Math.Cos(a), 6));
            static double[]? Turns(string model) => Path.GetFileNameWithoutExtension(model).ToLowerInvariant() switch
            {
                var m when m.StartsWith("spinner_rail_end") => [Math.PI],
                var m when m.StartsWith("spinner_rail_corner") || m.StartsWith("spinnerstopper_corner") => [0, Math.PI / 2],
                var m when m.StartsWith("spinner_rail_crossing") => [0, Math.PI / 2, Math.PI, -Math.PI / 2],
                var m when m.StartsWith("spinner_rail_straight") || m.StartsWith("spinnerstopper_straight") => [0, Math.PI],
                var m when m.StartsWith("spinnerstopper_tricorner") => [0, Math.PI / 2, -Math.PI / 2],
                _ => null,
            };
            var kept = pieces.Where(p => Turns(p.Model) is not null).ToList();
            if (kept.Count == 0) return null;
            var arms = kept.Select(p => Turns(p.Model)!.Select(t => Way(p.Angle + t)).ToList()).ToList();
            const double MaxDeviation = 6 * Math.PI / 180;
            int? Best(int i, (double X, double Y) d)
            {
                int? best = null;
                double nearest = double.MaxValue;
                for (int j = 0; j < kept.Count; j++)
                {
                    double vx = kept[j].X - kept[i].X, vy = kept[j].Y - kept[i].Y;
                    double along = vx * d.X + vy * d.Y, across = Math.Abs(vx * d.Y - vy * d.X);
                    if (j == i || along <= 0 || Math.Atan2(across, along) > MaxDeviation || along >= nearest
                        || !arms[j].Any(e => e.X * -d.X + e.Y * -d.Y > 0.99)) continue;
                    (best, nearest) = (j, along);
                }
                return best;
            }
            var ways = kept.Select(_ => new List<(int, double, double)>()).ToArray();
            for (int i = 0; i < kept.Count; i++)
                foreach (var d in arms[i])
                    if (Best(i, d) is int j && Best(j, (-d.X, -d.Y)) == i) ways[i].Add((j, d.X, d.Y));
            var stopper = kept.Select(p => Path.GetFileName(p.Model).StartsWith("SpinnerStopper_Straight", StringComparison.OrdinalIgnoreCase)).ToArray();
            bool[] switches = [.. kept.Select(p => Path.GetFileName(p.Model).StartsWith("SpinnerStopper_TriCorner", StringComparison.OrdinalIgnoreCase))];
            // A junction follows the TriCorner on its own track (itself, for a TriCorner), nearest first.
            var part = new int[kept.Count];
            Array.Fill(part, -1);
            for (int start = 0, n = 0; start < kept.Count; start++)
            {
                if (part[start] >= 0) continue;
                var todo = new Stack<int>([start]);
                part[start] = n;
                while (todo.TryPop(out int at))
                    foreach (var (next, _, _) in ways[at])
                        if (part[next] < 0) { part[next] = n; todo.Push(next); }
                n++;
            }
            long Distance(int a, int b) => (long)(kept[a].X - kept[b].X) * (kept[a].X - kept[b].X) + (long)(kept[a].Y - kept[b].Y) * (kept[a].Y - kept[b].Y);
            int[] gate = [.. Enumerable.Range(0, kept.Count).Select(i => ways[i].Count < 3 ? -1
                : Enumerable.Range(0, kept.Count).Where(k => switches[k] && part[k] == part[i]).OrderBy(k => Distance(i, k)).DefaultIfEmpty(-1).First())];
            var mirror = pieces.Select((p, i) => (p, mirrored?[i] ?? false)).Where(e => Turns(e.p.Model) is not null).Select(e => e.Item2).ToList();
            var swaps = kept.Select((p, i) => Path.GetFileName(p.Model).StartsWith("SpinnerStopper_Corner", StringComparison.OrdinalIgnoreCase)
                ? (((double, double), (double, double))?)(SwapWay(p.Angle, mirror[i], false), SwapWay(p.Angle, mirror[i], true)) : null).ToArray();
            return new SpinnerTrack([.. kept.Select(p => (p.X, p.Y))], ways, stopper, gate, swaps);
        }

        // The way a swap piece leads now (open: in state 2), or null for any other piece.
        (int Rail, double X, double Y)? SwapLead(int at, Func<int, bool> open)
        {
            if (swaps[at] is not { } swap) return null;
            var (x, y) = open(at) ? swap.Other : swap.First;
            foreach (var way in ways[at])
                if (way.Item2 * x + way.Item3 * y > 0.99) return way;
            return null;
        }

        /// <summary>The run a spinner stands on (within 4 cells), from the piece behind it to the one it faces (its room
        /// line's angle); where two runs cross under it, the one along its facing. Null when none.</summary>
        public (int From, int To)? Start((int X, int Y) at, float angle, Func<int, bool>? open = null)
        {
            double fx = Math.Sin(angle), fy = -Math.Cos(angle);
            var runs = Enumerable.Range(0, Rails.Count).SelectMany(i => ways[i].Where(w => w.Rail > i).Select(w => (A: i, B: w.Rail, Along: w.X * fx + w.Y * fy)))
                .Select(e => (e.A, e.B, e.Along, Distance: ToSegment(at, Rails[e.A], Rails[e.B])))
                .Where(e => e.Distance <= 4 && Leads(e.A, e.B) && Leads(e.B, e.A))
                .OrderByDescending(e => Math.Abs(e.Along) >= Math.Cos(Math.PI / 6)).ThenBy(e => e.Distance).ToList();
            // A swap piece's run only the way it leads now.
            bool Leads(int piece, int to) => open is null || swaps[piece] is null || SwapLead(piece, open)?.Rail == to;
            if (runs.Count == 0) return null;
            var run = runs[0];
            return run.Along < 0 ? (run.B, run.A) : (run.A, run.B);
        }

        /// <summary>The piece after <paramref name="at"/> for a spinner come from <paramref name="from"/>, and the way it
        /// turns its corners now (−1 or 1 by the turn's sign, 0 not yet known: a reversal turns it about).
        /// <paramref name="open"/> says whether a stopper piece is in state 2.</summary>
        public (int Next, int Turn) Next(int from, int at, int turn, Func<int, bool> open)
        {
            double hx = Rails[at].X - Rails[from].X, hy = Rails[at].Y - Rails[from].Y, length = Math.Sqrt(hx * hx + hy * hy);
            (hx, hy) = length == 0 ? (0, 0) : (hx / length, hy / length);
            int Turn((int Rail, double X, double Y) w) { double cross = hx * w.Y - hy * w.X; return cross > 0.5 ? 1 : cross < -0.5 ? -1 : 0; }
            // A swap piece sends a spinner its way, and turns it back when it comes from there.
            if (SwapLead(at, open) is { } lead) return lead.Rail == from ? (from, -turn) : (lead.Rail, turn);
            var onward = ways[at].Where(w => w.Rail != from).ToList();
            if (onward.Count == 0 || stopper[at] && !open(at)) return (from, -turn);
            var ahead = onward.Where(w => w.X * hx + w.Y * hy > 0.9).ToList();
            var way = onward.Count == 1 ? onward[0]
                : (gate[at] < 0 || open(gate[at])) && ahead.Count > 0 ? ahead[0]
                : onward.Where(w => turn != 0 && Turn(w) == turn).Concat(ahead).Concat(onward).First();
            int turned = Turn(way);
            return (way.Rail, turned == 0 ? turn : turned);
        }

        static double ToSegment((int X, int Y) p, (int X, int Y) a, (int X, int Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
            double t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
            return Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2));
        }
    }

    /// <summary>The rail switches (Rail_Visible_Swap_Anims01) a lever moves: each goes with its nearest toggling lever
    /// (Switch, Switch_Multi). Live (2026-11-01, the original): the Crypt trial's Switch_Multi between the first two
    /// spinners lifts both their stoppers, so they run on to their track's ends; the upper room's Switch moves its
    /// TriCorner.</summary>
    public static List<int> SwitchedRails(IReadOnlyList<(int X, int Y)> rails, IReadOnlyList<(int X, int Y)> levers, int lever)
    {
        static long Distance((int X, int Y) a, (int X, int Y) b) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y);
        return [.. Enumerable.Range(0, rails.Count).Where(i => levers.Count > 0
            && Enumerable.Range(0, levers.Count).MinBy(k => Distance(rails[i], levers[k])) == lever)];
    }

    /// <summary>The gears a Crematorium lever (Switch_Once) sets turning, as a rail switch goes with its lever: each with
    /// its nearest lever (live 2026-11-01, the original: every lever's own gears beside it start turning as its door
    /// opens).</summary>
    public static List<int> TurnedGears(IReadOnlyList<(int X, int Y)> gears, IReadOnlyList<(int X, int Y)> levers, int lever) =>
        SwitchedRails(gears, levers, lever);

    /// <summary>A trial room's server spawners (points of its room file, with no .ot to tell what they spawn) and the
    /// monster each places, rare: the Lower Prison trial's PrisonPoacherSpawner, on the plaque's platform, a Brittle
    /// Poacher (live 2026-10-31, the original: a rare skeleton archer beside the platform's pack); the Crypt trial's
    /// CryptFrostPriestSpawner, by the plaque, a Frost Priest (its name; live 2026-11-01, the original: rare skeletons
    /// there); the Crematorium trial's CremPyroSpawner, by the plaque, a Cinder Elemental (live 2026-11-01, the original:
    /// a rare pack of what looked like stone golems there; the area's burning rock monster). Its four
    /// <c>Metadata/Monsters/LavaTrap/LabyrinthElementalSpawner</c> points are monsters themselves, the Immolators that
    /// bring Ember Claimants (MonsterAttack.Summon).</summary>
    public static readonly IReadOnlyDictionary<string, string> Spawners = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Metadata/QuestObjects/Labyrinth/PrisonPoacherSpawner"] = "Metadata/Monsters/Skeletons/SkeletonBowLightning",
        ["Metadata/QuestObjects/Labyrinth/CryptFrostPriestSpawner"] = "Metadata/Monsters/Skeletons/SkeletonCasterCold4",
        ["Metadata/QuestObjects/Labyrinth/CremPyroSpawner"] = "Metadata/Monsters/MossMonster/FireMonster",
    };

    /// <summary>Pack points a trial room has in the original that its room file lacks (room cells, as the file's points;
    /// one of the area's packs of that rarity each): the Chamber of Sins trial's two packs in the open ground before the
    /// upper lever room's cage, where the way comes down the one-lane track (live 2026-11-01, the original: two packs
    /// there; the area's scattered packs keep <see cref="TrapClearance"/> from its tracks, so none came); the Crypt
    /// trial's pack just past its door and a second in the plaque room (live 2026-11-01, the original: skeletons and
    /// spiders at the way in, spider packs beside the rare skeletons by the plaque); the Crematorium trial's pack in the
    /// first room, before its first Locked Door, and one past its second (live 2026-11-01, the original: skeletons at the
    /// way in, monsters in the rooms past the second door; none among the hall's plates), on open floor clear of the
    /// plates.</summary>
    public static readonly IReadOnlyDictionary<string, AreaPopulation.RoomPoint[]> RoomPacks =
        new Dictionary<string, AreaPopulation.RoomPoint[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Metadata/Terrain/Labyrinth/RoomSets/RuinedTemple/Rooms/Unique/intro.arm"] = [new("normal", 67, 260), new("normal", 62, 293)],
            ["Metadata/Terrain/Labyrinth/RoomSets/Crypt/Rooms/Unique/crypt_intro.arm"] = [new("normal", 95, 338), new("normal", 120, 75)],
            ["Metadata/Terrain/Labyrinth/RoomSets/Prison/Rooms/Unique/crematorium_intro.arm"] = [new("normal", 60, 206), new("normal", 163, 111)],
        };

    /// <summary>How far from a trial's traps (the rails its roombas run on among them) the area's scattered packs keep
    /// their centres (our number: a pack's members stand within 9 cells of it, a spike tile reaches 11 from its trap).
    /// Live 2026-10-31, the original: no monsters among the traps.</summary>
    public const int TrapClearance = 25;

    /// <summary>A pack of the Labyrinth's own rooms: its monsters (MonsterVarieties ids, one weight each), its size, and
    /// the one that leads it, always one (a summoner among its minions: live 2026-11-02, the original).</summary>
    public sealed record LabyrinthPack(string Id, string[] Varieties, int Min, int Max, string[]? Leaders = null);

    /// <summary>How much denser than an ordinary area's the Labyrinth's packs stand (live 2026-11-02: about a fifth more
    /// than the ordinary density looked right).</summary>
    public const double PackDensity = 1.2;

    const string Mon = "Metadata/Monsters/";

    static readonly LabyrinthPack Contenders = new("contenders", [Mon + "Skeletons/SkeletonMeleeLabyrinth", Mon + "Skeletons/SkeletonBowLabyrinth", Mon + "Skeletons/SkeletonMeleeLabyrinth2"], 4, 7),
        Claimants = new("claimants", [Mon + "Skeletons/SkeletonMeleeFireLabyrinth", Mon + "Skeletons/SkeletonMeleeFireShieldLabyrinth", Mon + "Skeletons/SkeletonMeleeColdLabyrinth",
            Mon + "Skeletons/SkeletonMeleeColdShieldLabyrinth", Mon + "Skeletons/SkeletonMeleeLightningLabyrinth", Mon + "Skeletons/SkeletonMeleeLightningShieldLabyrinth"], 4, 7),
        Deceivers = new("deceivers", [Mon + "Skeletons/SkeletonCasterColdLabyrinth", Mon + "Skeletons/SkeletonCasterFireLabyrinth", Mon + "Skeletons/SkeletonCasterLightningLabyrinth"], 3, 5),
        Reanimators = new("reanimators", [Mon + "Skeletons/SkeletonMeleeLabyrinth", Mon + "Skeletons/SkeletonBowLabyrinth"], 3, 5, [Mon + "Necromancer/NecromancerLabyrinth"]),
        Accursed = new("accursed", [Mon + "Skeletons/SkeletonMeleeLabyrinth2"], 3, 5,
            [Mon + "Necromancer/NecromancerFireLabyrinth", Mon + "Necromancer/NecromancerColdLabyrinth", Mon + "Necromancer/NecromancerLightningLabyrinth"]),
        Zombies = new("zombies", [Mon + "ZombieDismember/ZombieTarLabyrinth", Mon + "ZombieDismember/ZombieTarLabyrinth", Mon + "ZombieDismember/ZombieMoldy1Labyrinth",
            Mon + "ZombieDismember/ZombieMoldy2Labyrinth", Mon + "ZombieDismember/ZombieMoldy3Labyrinth", Mon + "ZombieDismember/ZombieMoldyPoison1Labyrinth",
            Mon + "ZombieDismember/ZombieMoldyPoison2Labyrinth", Mon + "ZombieDismember/ZombieMoldyPoison3Labyrinth"], 4, 7),
        Crushers = new("crushers", [Mon + "Skeletons/SkeletonMeleeLargeLabyrinth"], 1, 2),
        Ghosts = new("ghosts", [Mon + "GhostPirates/GhostPirateBlackLabyrinth", Mon + "GhostPirates/GhostPirateFlickerStrikeBlackLabyrinth", Mon + "GhostPirates/GhostPirateBlackBowLabyrinth"], 3, 5),
        Spiders = new("spiders", [Mon + "Spiders/SpiderScale2Labyrinth", Mon + "Spiders/SpiderScale5Labyrinth"], 5, 8),
        Needlebacks = new("needlebacks", [Mon + "Spiker/SpikerLabyrinth"], 2, 4),
        Lurkers = new("lurkers", [Mon + "Grappler/GrapplerLabyrinth"], 2, 4),
        Servants = new("servants", [Mon + "Undying/CityStalkerMaleLabyrinth", Mon + "Undying/CityStalkerFemaleLabyrinth", Mon + "Undying/CityStalkerMaleFreezingPulseLabyrinth",
            Mon + "Undying/CityStalkerFemaleFreezingPulseLabyrinth"], 3, 5),
        Dwellers = new("dwellers", [Mon + "Undying/UndyingOutcastWhirlingBladesLabyrinth", Mon + "Undying/UndyingOutcastPunctureLabyrinth"], 3, 6),
        Slitherers = new("slitherers", [Mon + "Snake/SnakeScorpionIncaLabyrinth_"], 3, 5),
        Marionettes = new("marionettes", [Mon + "Mannequin/MannequinLabyrinth"], 3, 5),
        Devourers = new("devourers", [Mon + "RootSpiders/RootSpiderHiddenLabyrinth"], 1, 2),
        Masses = new("masses", [Mon + "MossMonster/MossMonsterEmergeRollLabyrinth"], 1, 2),
        Muck = new("muck", [Mon + "WaterElemental/WaterElementalPureLabyrinth"], 1, 3);

    /// <summary>The packs scattered in a Labyrinth room area (ours): no MonsterPacks or MonsterGroups row names a Labyrinth
    /// area, its monsters are the real server's choice among the <c>*Labyrinth</c> varieties. By the area's room set (its id's
    /// code): live (2026-11-02, the original) the underground ones (Prison, Crypt) have undead and the like, the ones above
    /// (ElegantHouse, DryGarden, OutdoorHouse, RuinedTemple) Maze Needlebacks, Shadow Lurkers and Devourers. None for
    /// Izaro's rooms and the airlock.</summary>
    public static IReadOnlyList<LabyrinthPack> RoomSetPacks(string areaId) =>
        !InLabyrinth(areaId) ? [] : areaId.Split('_') is [_, "Labyrinth", var set, ..] ? set switch
        {
            "P" => [Contenders, Zombies, Reanimators, Crushers, Spiders],
            "C" => [Claimants, Deceivers, Accursed, Ghosts, Spiders],
            // ElegantHouse's Muck Elementals rise out of its fountains' water (live 2026-11-02).
            "EH" => [Servants, Lurkers, Marionettes, Dwellers, Muck],
            "DG" => [Needlebacks, Lurkers, Devourers, Masses, Slitherers],
            "OH" => [Needlebacks, Lurkers, Dwellers, Muck, Slitherers],
            "RT" => [Slitherers, Dwellers, Lurkers, Needlebacks],
            _ => [],
        } : [];

    /// <summary>A monograph's NPCTalk index (column 1): each has one row, 0 "Read" (NPCTalk 777–782).</summary>
    public const byte GlyphTalk = 0;

    /// <summary>A spike trap's square, where it hits and walls a player: its model's Hull (<c>spiketrap_prison.ao</c>,
    /// ±11.27) is one tile (23 cells), and the traps of a trial stand a tile apart, so the squares meet without a gap (live
    /// 2026-10-31: ±9 cells left four-cell strips between traps where nothing hit).</summary>
    public const int SpikeHalf = 11;

    /// <summary>How a spike trap fires: S→C 0x1d1 with Animation.dat <see cref="EmergeAnimation"/>, which its .act plays as
    /// <c>spike_fast</c> (<c>Spikes02/rig.amd</c>: 1000 ms; its sounds put the spikes up at 0 ms and start them down at
    /// 500 ms), stretched over this long. Every spike trap .act names that clip only. Live (2026-10-31): the Emerge action
    /// (0x1ab) played the plain clip, about a second late, with two traps of a 450 ms cascade up at once; stretched to
    /// 2000 ms, four stand up at once and lower slowly, as in the original. <c>/trial_spike_anim</c> (dev) changes it.
    /// </summary>
    public static double SpikeAnimationSeconds = 2;

    /// <summary>Animation.dat row <c>Emerge</c>.</summary>
    public const byte EmergeAnimation = 43;

    /// <summary>When after its firing (seconds) the spikes are up: a player in the square then is hit, once. Up from when
    /// the client shows them risen until the stretched clip starts them down, at its half (live 2026-11-01: from 100 ms
    /// the hit came before the spikes showed; 400 ms matched. 2026-10-31: a hit only at 1200–1300 ms missed a player who
    /// ran in while they were up). <c>/trial_spike_hit</c> (dev) moves it.</summary>
    public static (double From, double To) SpikeHitWindow = (0.4, 1.0);

    /// <summary>When after its firing (seconds) the square is impassable: from the spikes' rise until the stretched clip has
    /// lowered them (live 2026-10-31: the wall stands while any spike shows, lowering ones too).</summary>
    public static (double From, double To) SpikeWallWindow => (SpikeHitWindow.From, SpikeAnimationSeconds);

    /// <summary>How the spikes cycle (our own numbers: the real server's timers are the rooms' <c>Trigger_*</c> lines,
    /// server objects the client has no file for). A chain of spike traps fires as a wave, one trap
    /// <see cref="SpikeStepSeconds"/> after the other, so several stand up at once (live 2026-10-31, the original game: a
    /// cascade towards the door), and starts again as soon as it has gone round (live 2026-10-31: it
    /// does not stop). The cascade traps fire
    /// on their timer only; the spikes that answer a step (wiki) are another trap, in Izaro's rooms.</summary>
    /// <remarks><c>/trial_spike_step</c> (dev) changes it from the next wave on.</remarks>
    public static double SpikeStepSeconds = 0.45;

    /// <summary>A checkerboard chain's step: its two halves fire two seconds apart, every four (our reading of the room's
    /// <c>Trigger_Delay_4_0WG</c> / <c>Trigger_Delay_4_2WG</c> lines; live 2026-10-31: they rise together, as a
    /// checkerboard).</summary>
    public const double SpikeCheckerSeconds = 2;

    /// <summary>A chain's step: <see cref="SpikeCheckerSeconds"/> for a checkerboard, else <see cref="SpikeStepSeconds"/>.</summary>
    public static double SpikeStep(bool checker) => checker ? SpikeCheckerSeconds : SpikeStepSeconds;

    /// <summary>One chain's cycle: its wave of so many steps of so long.</summary>
    public static double SpikeWaveSeconds(int steps, double step) => steps * step;

    /// <summary>Each spike trap's chain and place in its wave: chains are traps a tile apart (8-neighbour, at most
    /// <see cref="SpikeHalf"/> × 2 + 2 cells). A chain's wave starts at its trap nearest an origin (the trial's levers;
    /// the real start is a <c>Trigger_PulsePrisonWG</c> line, which the room data lacks). A ring (every trap side by side
    /// with two others) goes once round, the way that reaches its trap nearest an end (the trial's doors) first (live
    /// 2026-10-31, checked against the original: from the lever straight towards the door, then on round), one <see cref="SpikeStepSeconds"/> step a trap. Any other chain
    /// fires as a checkerboard: the tiles of the start's colour, then the others, <see cref="SpikeCheckerSeconds"/>
    /// apart.</summary>
    public static (int Chain, int Order, bool Checker)[] SpikeChains(IReadOnlyList<(int X, int Y)> traps, IReadOnlyList<(int X, int Y)> origins,
        IReadOnlyList<(int X, int Y)> ends)
    {
        var placed = new (int Chain, int Order, bool Checker)[traps.Count];
        var done = new bool[traps.Count];
        int chain = 0;
        static bool Near((int X, int Y) a, (int X, int Y) b) => Math.Abs(a.X - b.X) <= SpikeHalf * 2 + 2 && Math.Abs(a.Y - b.Y) <= SpikeHalf * 2 + 2;
        static long Distance((int X, int Y) a, (int X, int Y) b) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y);
        foreach (int first in Enumerable.Range(0, traps.Count).OrderBy(i => traps[i].Y).ThenBy(i => traps[i].X))
        {
            if (done[first]) continue;
            var members = new List<int> { first };
            done[first] = true;
            for (int k = 0; k < members.Count; k++)
                foreach (int i in Enumerable.Range(0, traps.Count).Where(i => !done[i] && Near(traps[members[k]], traps[i])))
                {
                    done[i] = true;
                    members.Add(i);
                }
            int start = origins.Count == 0 ? first
                : members.OrderBy(i => origins.Min(o => Distance(traps[i], o))).ThenBy(i => i).First();
            // Side by side: a corner is a step, not a diagonal shortcut.
            static bool Beside((int X, int Y) a, (int X, int Y) b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) <= SpikeHalf * 2 + 2;
            var beside = members.ToDictionary(i => i, i => members.Where(j => j != i && Beside(traps[i], traps[j])).ToList());
            if (members.Count > 2 && beside.Values.All(b => b.Count == 2))
            {
                int end = ends.Count == 0 ? start
                    : members.OrderBy(i => ends.Min(o => Distance(traps[i], o))).ThenBy(i => i).First();
                List<int> Round(int next)
                {
                    var path = new List<int> { start };
                    for (int previous = start, at = next; at != start; (previous, at) = (at, beside[at].First(j => j != previous)))
                        path.Add(at);
                    return path;
                }
                var (one, other) = (Round(beside[start][0]), Round(beside[start][1]));
                var round = one.IndexOf(end) <= other.IndexOf(end) ? one : other;
                for (int k = 0; k < round.Count; k++) placed[round[k]] = (chain, k, false);
                chain++;
                continue;
            }
            // A checkerboard by tiles (SpikeHalf × 2 + 1 cells) from the start.
            int Tile(int d) => (int)Math.Round(d / (SpikeHalf * 2 + 1.0));
            foreach (int i in members)
                placed[i] = (chain, (Tile(traps[i].X - traps[start].X) + Tile(traps[i].Y - traps[start].Y)) & 1, true);
            chain++;
        }
        return placed;
    }

    /// <summary>Each spike trap's chain, place in it and trigger (−1: none) by the room links: a trap's <c>next</c> is the
    /// trap after it, a <c>Trigger_*</c> line's <c>next</c> its chain's first. A chain without a trigger fires as a wave
    /// along its links (the Lower Prison trial's ring of 16: from beside its door round to beside its lever); side by side
    /// with another (its traps a tile from the other's), the two are a checkerboard's colours, each rising at once,
    /// <see cref="SpikeCheckerSeconds"/> apart (the same trial's two zigzag chains; live 2026-10-31: they rise together).
    /// A trigger that is one of the room's points (the <c>…WG</c> lines: no links) takes the chains whose traps it would
    /// take by place (<paramref name="nearTrigger"/>, <see cref="TriggeredSpikes"/>).
    /// </summary>
    public static (int Chain, int Order, bool Checker, int Trigger)[] LinkedSpikes(IReadOnlyList<(int X, int Y)> traps,
        IReadOnlyList<(int From, int To)> next, IReadOnlyList<(int Trigger, int Trap)> triggered, IReadOnlyList<int>? nearTrigger = null)
    {
        var after = new int[traps.Count];
        var before = new bool[traps.Count];
        Array.Fill(after, -1);
        foreach (var (from, to) in next)
            if (after[from] < 0 && from != to) (after[from], before[to]) = (to, true);
        var placed = new (int Chain, int Order, bool Checker, int Trigger)[traps.Count];
        var chainOf = new int[traps.Count];
        Array.Fill(chainOf, -1);
        var chains = new List<(List<int> Traps, int Trigger)>();
        var starts = triggered.Select(t => (t.Trap, t.Trigger))
            .Concat(Enumerable.Range(0, traps.Count).Where(i => !before[i]).Select(i => (Trap: i, Trigger: -1)))
            .Concat(Enumerable.Range(0, traps.Count).Select(i => (Trap: i, Trigger: -1)));
        foreach (var (start, trigger) in starts)
        {
            if (chainOf[start] >= 0) continue;
            var members = new List<int>();
            for (int at = start; at >= 0 && chainOf[at] < 0; at = after[at])
            {
                chainOf[at] = chains.Count;
                members.Add(at);
            }
            int own = trigger < 0 && nearTrigger is not null ? members.Select(i => nearTrigger[i]).FirstOrDefault(t => t >= 0, -1) : trigger;
            chains.Add((members, own));
        }
        static bool Near((int X, int Y) a, (int X, int Y) b) => Math.Abs(a.X - b.X) <= SpikeHalf * 2 + 2 && Math.Abs(a.Y - b.Y) <= SpikeHalf * 2 + 2;
        var group = Enumerable.Range(0, chains.Count).ToArray();
        int Root(int c) => group[c] == c ? c : group[c] = Root(group[c]);
        for (int a = 0; a < chains.Count; a++)
            for (int b = a + 1; b < chains.Count; b++)
                if (chains[a].Trigger < 0 && chains[b].Trigger < 0
                    && chains[a].Traps.Any(i => chains[b].Traps.Any(j => Near(traps[i], traps[j])))) group[Root(b)] = Root(a);
        for (int c = 0; c < chains.Count; c++)
        {
            var colours = Enumerable.Range(0, chains.Count).Where(k => Root(k) == Root(c)).ToList();
            bool checker = colours.Count > 1;
            for (int k = 0; k < chains[c].Traps.Count; k++)
                placed[chains[c].Traps[k]] = (Root(c), checker ? colours.IndexOf(c) : k, checker, chains[c].Trigger);
        }
        return placed;
    }

    /// <summary>A room's <c>Trigger_*</c> line (server-only .ot, LabyrinthTrigger <c>trigger_method "timed"</c>,
    /// <c>next_only_path 1</c>): it fires its traps every <paramref name="Period"/> seconds (<c>trigger_frequency</c>), one
    /// <paramref name="Step"/> after the other along their path (<c>delay_between_traps</c>), first
    /// <paramref name="Delay"/> after the start (<c>start_delay</c>).</summary>
    public sealed record SpikeTrigger(double Period, double Step, double Delay);

    static readonly Dictionary<string, SpikeTrigger> Triggers = new(StringComparer.Ordinal)
    {
        ["Trigger_CascadeFast"] = new(5, 0.2, 0),
        ["Trigger_Delay_4_0"] = new(5, 0, 0),
        ["Trigger_Delay_4_2"] = new(5, 0, 2.5),
        ["Trigger_Delay_3_0"] = new(3, 0, 0),
        ["Trigger_PulseSlow"] = new(6, 0.45, 0),
        ["Trigger_PulseSlow_Delay"] = new(6, 0.45, 3),
        ["Trigger_PulsePrison"] = new(7.2, 0.45, 0),
    };

    /// <summary>The trigger a room line is, or null: its type's last part, less the line's <c>WG</c> flags.</summary>
    public static SpikeTrigger? Trigger(string type)
    {
        string name = type[(type.LastIndexOf('/') + 1)..];
        if (name.EndsWith("WG", StringComparison.Ordinal)) name = name[..^2];
        return Triggers.GetValueOrDefault(name);
    }

    /// <summary>Which trigger fires each spike trap and the trap's place on its path, (−1, 0) for none: a trigger takes
    /// its nearest trap (within two tiles) and the traps joined to it a tile apart (as <see cref="SpikeChains"/>), each
    /// as many steps on as it lies tiles from that one (live 2026-11-01, the original: a line of traps rises one after
    /// the other from its trigger's end). Triggers that take the same traps (<c>Trigger_Delay_4_0</c> and <c>_4_2</c>)
    /// share them as a checkerboard of the traps' own tiles (their angle), each the colour of its nearest.</summary>
    public static (int Trigger, int Order)[] TriggeredSpikes(IReadOnlyList<(int X, int Y, float Angle)> traps, IReadOnlyList<(int X, int Y)> triggers)
    {
        var placed = Enumerable.Repeat((-1, 0), traps.Count).ToArray();
        static bool Near((int X, int Y, float) a, (int X, int Y, float) b) => Math.Abs(a.X - b.X) <= SpikeHalf * 2 + 2 && Math.Abs(a.Y - b.Y) <= SpikeHalf * 2 + 2;
        static long Distance(int ax, int ay, int bx, int by) => (long)(ax - bx) * (ax - bx) + (long)(ay - by) * (ay - by);
        var group = new int[traps.Count];
        Array.Fill(group, -1);
        for (int first = 0, n = 0; first < traps.Count; first++)
        {
            if (group[first] >= 0) continue;
            var todo = new Stack<int>([first]);
            group[first] = n;
            while (todo.TryPop(out int at))
                for (int i = 0; i < traps.Count; i++)
                    if (group[i] < 0 && Near(traps[at], traps[i])) { group[i] = n; todo.Push(i); }
            n++;
        }
        const long Reach = (SpikeHalf * 4 + 2) * (SpikeHalf * 4 + 2);
        var nearest = triggers.Select(t => traps.Count == 0 ? -1
            : Enumerable.Range(0, traps.Count).MinBy(i => Distance(traps[i].X, traps[i].Y, t.X, t.Y)) is var i
              && Distance(traps[i].X, traps[i].Y, t.X, t.Y) <= Reach ? i : -1).ToArray();
        for (int t = 0; t < triggers.Count; t++)
        {
            int start = nearest[t];
            if (start < 0) continue;
            var (sx, sy, angle) = traps[start];
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            int Colour(int i)
            {
                double dx = traps[i].X - sx, dy = traps[i].Y - sy;
                return ((int)Math.Round((dx * cos + dy * sin) / (SpikeHalf * 2 + 1.0)) + (int)Math.Round((-dx * sin + dy * cos) / (SpikeHalf * 2 + 1.0))) & 1;
            }
            if (Enumerable.Range(0, triggers.Count).Any(u => u != t && nearest[u] >= 0 && group[nearest[u]] == group[start]))
            {
                // A colour's tiles meet at their corners only: they rise together.
                for (int i = 0; i < traps.Count; i++)
                    if (group[i] == group[start] && Colour(i) == 0) placed[i] = (t, 0);
                continue;
            }
            var steps = new Dictionary<int, int> { [start] = 0 };
            var queue = new Queue<int>([start]);
            while (queue.TryDequeue(out int at))
                for (int i = 0; i < traps.Count; i++)
                    if (group[i] == group[start] && !steps.ContainsKey(i) && Near(traps[at], traps[i]))
                    {
                        steps[i] = steps[at] + 1;
                        queue.Enqueue(i);
                    }
            foreach (var (i, order) in steps) placed[i] = (t, order);
        }
        return placed;
    }

    /// <summary>A spike trap's hit only deals damage: live (2026-10-31, checked against the original) it neither stuns nor
    /// shows a buff; the risen spikes hold the player off as a wall (<see cref="SpikeHitWindow"/>). BuffDefinitions
    /// <c>pinned</c>, <c>trap_pinned</c> ("Spike Pinned", invisible, <c>base_physical_damage_taken_per_minute</c>) and
    /// <c>labyrinth_trap_bleed</c> exist; the last two carry damage over time whose values the client data lacks: not sent.
    /// </summary>
    /// <summary>A spike trap's hit always bleeds (its .ot: <c>main_hand_local_chance_to_bleed_on_hit_%</c> 100,
    /// <c>base_bleed_duration_+%</c> −80): the wiki's bleed is 70% of the hit's base physical damage a second, 10% when an
    /// enemy monster applied it (the trap is one: <c>is_daemon</c>), three times that while the target moves, for 5 s
    /// (1 s here), not reduced by armour; only the strongest bleed deals damage. Shown with BuffDefinitions
    /// <c>bleeding</c>; <c>labyrinth_trap_bleed</c> takes shares of the pools whose values the client data lacks.</summary>
    public const double SpikeBleedPerSecond = 0.10, SpikeBleedMovingFactor = 3, SpikeBleedSeconds = 5 * (1 - 0.8);

    /// <summary>The <c>bleeding</c> BuffDefinitions row, or null without the table.</summary>
    public static ushort? BleedingBuffRow => BuffRows.Value.TryGetValue("bleeding", out ushort row) ? row : null;

    /// <summary>The <c>poison</c> BuffDefinitions row ("Poisoned", a count of its stacks), or null without the table.</summary>
    public static ushort? PoisonBuffRow => BuffRows.Value.TryGetValue("poison", out ushort row) ? row : null;

    /// <summary>The buff a roomba of this type puts on the player in it (<c>roomba_damage</c>, <c>flying_roomba_damage</c>:
    /// invisible, BuffVisuals <c>rhoomba_damage</c> playing <c>labyrinth/traps/roomba/roomba.epk</c>), or null without the
    /// table.</summary>
    public static ushort? RoombaBuffRow(string type) =>
        BuffRows.Value.TryGetValue(type == FlyingRoomba ? "flying_roomba_damage" : IsSawblade(type) ? "sawblade_damage" : "roomba_damage", out ushort row) ? row : null;

    static readonly Lazy<Dictionary<string, ushort>> BuffRows = new(() =>
    {
        const string file = "data/tables/BuffDefinitions.json";
        var rows = new Dictionary<string, ushort>(StringComparer.Ordinal);
        if (!File.Exists(file)) return rows;
        using var table = GameTables.Open(file);
        int row = 0;
        foreach (var buff in table.RootElement.EnumerateArray())
        {
            if (buff.GetProperty("Id").GetString() is { } id and ("bleeding" or "poison" or "labyrinth_arrow_slow" or "roomba_damage"
                    or "flying_roomba_damage" or "sawblade_damage" or "labyrinth_lava_aura")) rows.TryAdd(id, (ushort)row);
            row++;
        }
        return rows;
    });

    /// <summary>A spike trap's hit on a player (GrantedEffectStatSets <c>LbRetractableTrap</c>):
    /// <c>%_of_life_to_deal_as_damage</c> of maximum life plus <c>%_of_es_to_deal_as_damage</c> of maximum energy
    /// shield, with <c>damage_+%_vs_players</c>; it always hits and cannot be blocked. Null without the table. Armour and
    /// resistances are not applied (our reading: the trap deals a share of the pools).</summary>
    public static int? SpikeDamage(uint maxLife, uint maxEnergyShield) => TrapDamage(SpikeStats.Value, maxLife, maxEnergyShield);

    static readonly Lazy<(int Life, int EnergyShield, int VsPlayers)?> SpikeStats = new(() => TrapStats("LbRetractableTrap"));

    static int? TrapDamage((int Life, int EnergyShield, int VsPlayers)? stats, uint maxLife, uint maxEnergyShield) =>
        stats is { } s ? (int)Math.Min(int.MaxValue,
            ((long)maxLife * s.Life + (long)maxEnergyShield * s.EnergyShield) * (100 + s.VsPlayers) / 10000) : null;

    // A trap's stat set (GrantedEffectStatSets): its shares of the pools and its damage_+%_vs_players.
    static (int Life, int EnergyShield, int VsPlayers)? TrapStats(string id)
    {
        const string file = "data/tables/GrantedEffectStatSets.json";
        try
        {
            if (!File.Exists(file) || !File.Exists("data/tables/Stats.json")) return null;
            using var stats = JsonDocument.Parse(File.ReadAllBytes("data/tables/Stats.json"));
            var names = stats.RootElement.EnumerateArray().Select(s => s.GetProperty("Id").GetString() ?? "").ToList();
            using var sets = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (var set in sets.RootElement.EnumerateArray())
            {
                if (set.GetProperty("Id").GetString() != id) continue;
                var values = set.GetProperty("ConstantStats").EnumerateArray().Select(k => names[k.GetInt32()])
                    .Zip(set.GetProperty("ConstantStatsValues").EnumerateArray().Select(v => v.GetInt32()))
                    .ToDictionary(p => p.First, p => p.Second);
                return (values.GetValueOrDefault("%_of_life_to_deal_as_damage"), values.GetValueOrDefault("%_of_es_to_deal_as_damage"),
                    values.GetValueOrDefault("damage_+%_vs_players"));
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            Console.WriteLine($"[gateway] {file}: {ex.Message}");
            return null;
        }
    }

    /// <summary>A spike trap's 0x2b9 blob (LabyrinthCascadeSpikeTrap.ot: Positioned, Animated, BaseEvents, Pathfinding,
    /// TriggerableBlockage, Transitionable, Stats, Buffs, Life, Actor), as a monster writes these components: the .ot's
    /// team 14, its own model, unblocked, state 1, one life, and no skills (it fires with the Emerge basic action, whose
    /// clip is its <c>spike_fast</c>).</summary>
    /// <remarks>A pressure spike trap (<see cref="IsPressureSpikeTrap"/>) reads Transitionable before
    /// TriggerableBlockage. A doorway roller (<see cref="IsRoller"/>) has the cascade trap's components, at its angle and
    /// its tile line's height.</remarks>
    public static byte[] SpikeTrapBlob(int x, int y, string? type = null, float angle = 0, float? height = null)
    {
        var w = new BlobWriter().U8(0).U8(0);
        bool pressure = type is not null && IsPressureSpikeTrap(type);
        // A pressure trap's object_size 11 made it a wall round its tile (live 2026-11-01): flags1 0x08 with its u8, blocks
        // no walker (the doors' too, Blob).
        // f0 0x04: a fixed height (a doorway roller's tile line), as Blob sends the plates'.
        w.I32(x).I32(y).F32(angle).U16(14).U8((byte)(height is null ? 0 : 0x04)).U8((byte)(pressure ? 0x0a : 0x02));
        if (height is float z) w.I32(InstanceProtocol.HeightMarker(z)).F32(z);
        w.U8(0).U8(1);                                                  // Positioned
        if (pressure) w.U8(1);
        w.U8(0);                                                        // Animated: the .ot's
        if (pressure) w.U8(1).U8(0);                                    // Transitionable, TriggerableBlockage: unblocked
        else w.U8(0).U8(1);                                             // TriggerableBlockage: unblocked, Transitionable
        w.Stats(null).U32(0).U32(0).U8(0);                              // Stats
        w.U16(0);                                                       // Buffs
        foreach (uint pool in new uint[] { 1, 0, 0 }) w.U32(pool).U32(0).U16(0).U32(0).U32(0);
        w.U8(0);                                                        // Life flags
        w.WStr32("").U16(0).U8(0).U32(0).U32(0).U8(0).U8(0);            // Actor
        return w.U8(0).ToArray();                                       // tasks
    }

    /// <summary>The Labyrinth's rooms' blades at an abyss's edge (<c>LabyrinthAbyssEdgeBlade</c> and its <c>_Rhythm1</c>–
    /// <c>3</c>, <c>_Offset1s</c>, <c>_Offset2s</c>, <c>_Short</c>: one .ot, <c>_Short</c> with
    /// <c>ShatteredAbyssEdgeBlade.ao</c>). Its Emerge plays <c>blade.ao</c>'s <c>hit</c>: the mechanism at 0 ms, the swing
    /// at 421, the impact at 601 (the blade line's particles), the end at 1795.</summary>
    public const string EdgeBladePrefix = "Metadata/Terrain/Labyrinth/Traps/LabyrinthAbyssEdgeBlade";

    public static bool IsEdgeBlade(string type) => type.StartsWith(EdgeBladePrefix, StringComparison.Ordinal);

    public const double EdgeBladeClipSeconds = 1.8, EdgeBladeImpactSeconds = 0.601;

    /// <summary>The ElegantHouse doorways' roller traps (tile objects: <c>LabyrinthRollerTrap</c>, <c>_02</c>, <c>_03</c>,
    /// each with its own <c>elegant_doorway_trap*.ao</c> rig). A spike trap's .ot (<c>is_spike_trap</c>, its hit
    /// <c>LbRetractableTrap</c>, 100% knockback, no bleed) whose Emerge plays <c>spike</c>: the roller slides down,
    /// spins and goes back up (live 2026-11-01, the original: on a timer).</summary>
    public const string RollerPrefix = "Metadata/Terrain/Labyrinth/Traps/LabyrinthRollerTrap";

    public static bool IsRoller(string type) => type.StartsWith(RollerPrefix, StringComparison.Ordinal);

    /// <summary>Whether it is the first roller, a spinning roller that hangs under the lintel (its tile line 226 up) and
    /// drops to close the doorway (live 2026-11-01, the original: it cuts whoever is near it for as long as it is down,
    /// as a roomba); <c>_02</c> and <c>_03</c> are spikes that rise from the floor and strike, <c>_02</c> twice.</summary>
    public static bool IsDroppingRoller(string type) => type == RollerPrefix;

    /// <summary>The dropping roller's rig clips (<c>doorway spikes/rig.amd</c>, by their order there; 0x1b2 kind 0 plays
    /// one): <c>down</c> (1000 ms, on the floor at 541), <c>idle_bottom</c>, <c>idle_top</c> (its rest, the .act's Idle),
    /// <c>up</c> (2041 ms, off the floor at 624).</summary>
    public const uint RollerDownClip = 0, RollerBottomClip = 1, RollerTopClip = 3, RollerUpClip = 5;
    public const double RollerDownSeconds = 1, RollerLandsSeconds = 0.541, RollerUpSeconds = 2.041, RollerLiftsSeconds = 0.624;

    /// <summary>How long the dropping roller stays down: its .ot's <c>base_skill_effect_duration</c> (3500 ms; our
    /// reading).</summary>
    public const double RollerHoldSeconds = 3.5;

    /// <summary>The rising spikes' rig clips (<c>doorway spikes_02</c>, <c>_03/rig.amd</c>, by their order there; their
    /// .ao's sounds are on these, none on <c>spike</c>): <c>down_medium</c> (back into the floor), <c>idle_bottom</c> (their
    /// rest, the .act's Idle), <c>idle_up</c> (out), <c>up</c> (rising).</summary>
    public const uint SpikesDownClip = 1, SpikesRestClip = 3, SpikesOutClip = 4, SpikesUpClip = 6;

    /// <summary>A rising roller's go: its <c>up</c> clip (2041 ms, <c>_03</c> 1960), out for the .ot's
    /// <c>trap_idle_duration</c> (2000 ms, 800) looping <c>idle_up</c>, then <c>down_medium</c> (2458 ms, 1960), and its
    /// strikes, one a swing at its clips' contacts: <c>_03</c> once, at <c>up</c>'s (160 ms); <c>_02</c> rising at
    /// <c>up</c>'s <c>contact</c> (208 ms), swinging at its <c>secondary</c> (1291 ms) and at each round's
    /// <c>secondary</c> of <c>idle_up</c> (250 of 625 ms, with its sound) while out. Live (2026-11-01, the original):
    /// <c>_02</c> rises and swings four times, one hit a swing; <c>_03</c> strikes once.</summary>
    public static (double Up, double[] Strikes, double Out, double Down) RisingSpikes(string type)
    {
        if (type == RollerPrefix + "_03") return (1.96, [0.16], 0.8, 1.96);
        const double up = 2.041, held = 2, round = 0.625, swing = 0.25;
        return (up, [0.208, 1.291, .. Enumerable.Range(0, 8).Select(k => up + swing + k * round).Where(s => s < up + held)], held, 2.458);
    }

    /// <summary>How long a tile's trap stays hidden between two goes (live 2026-11-01, the original: a good while; the
    /// user's 5 s).</summary>
    public static double TileTrapRestSeconds = 5;

    /// <summary>Where a roller hits and closes the doorway: its Hull across the doorway (the first's walk strip about
    /// ±3.3 × ±8.4 cells, the others' projectile strips ±1.5 × ±7.5 and −2.4…0.6 × −9.5…12.6), widened to its sweep, as
    /// half sizes for <see cref="HullCells"/> (our numbers).</summary>
    /// <remarks><c>_02</c>'s spikes are long and reach a player beside the doorway too (live 2026-11-01): ±7 across, while
    /// the doorway they close is its Hull's (<see cref="RollerWallHalf"/>).</remarks>
    public static (double Across, double Along) RollerHalf(string type) => type[RollerPrefix.Length..] switch
    {
        "_02" => (7, 8.5),
        "_03" => (3.5, 11),
        _ => (3.5, 8.5),
    };

    /// <summary>The strip a roller closes while out: <see cref="RollerHalf"/>, but <c>_02</c>'s only its Hull's ±2.5
    /// across (live 2026-11-01: a wall as wide as its reach kept the player out of it).</summary>
    public static (double Across, double Along) RollerWallHalf(string type) => type == RollerPrefix + "_02" ? (2.5, 8.5) : RollerHalf(type);

    /// <summary>How far a tile trap's hit throws the player back, away from its middle (live 2026-11-01: a little, back
    /// from the doorway; ours), and how far beyond its strip the dropping roller throws one under it out.</summary>
    public const double KnockbackCells = 3, ThrowOutCells = 4;

    /// <summary>How often a blade swings and how long after the start it first does (the server's: ours, every 3 s; a
    /// rhythm's or an offset's its number of seconds later).</summary>
    public static (double Period, double Delay) EdgeBladeTimer(string type) => type[EdgeBladePrefix.Length..] switch
    {
        "_Rhythm2" or "_Offset1s" => (3, 1),
        "_Rhythm3" or "_Offset2s" => (3, 2),
        _ => (3, 0),
    };

    /// <summary>Where a blade cuts: its Hull's walk strip (<c>blade.ao</c>: about ±2.4 cells across, −11 to 14 along its
    /// angle), widened to its swing (live 2026-11-01, the original: a little wider than the Hull), as half sizes for
    /// <see cref="HullCells"/>.</summary>
    public const double EdgeBladeHalfAcross = 4, EdgeBladeHalfAlong = 14;

    /// <summary>A blade's hit bleeds (live 2026-11-01, the original): the spike's bleed (<see cref="SpikeBleedPerSecond"/>,
    /// 5 s) with <c>LbEdgeTrap</c>'s <c>active_skill_bleeding_damage_+%_final</c> −45 and <c>base_bleed_duration_+%</c>
    /// −40.</summary>
    public const double EdgeBladeBleedPerSecond = SpikeBleedPerSecond * (1 - 0.45), EdgeBladeBleedSeconds = 5 * (1 - 0.4);

    /// <summary>A blade's hit on a player (GrantedEffectStatSets <c>LbEdgeTrap</c>: 25% of maximum life plus 15% of
    /// maximum energy shield, 20% more against players), read as the spike trap's (<see cref="SpikeDamage"/>).</summary>
    public static int? EdgeBladeDamage(uint maxLife, uint maxEnergyShield) => TrapDamage(EdgeStats.Value, maxLife, maxEnergyShield);

    static readonly Lazy<(int Life, int EnergyShield, int VsPlayers)?> EdgeStats = new(() => TrapStats("LbEdgeTrap"));

    /// <summary>A blade's 0x2b9 blob (Positioned, BaseEvents, Animated, TriggerableBlockage, Pathfinding, Stats, Buffs, Life,
    /// Transitionable, Actor): the room line's angle, team 14, flags1 0x08 (blocks no walker, as the pressure spikes),
    /// the .ot's model, unblocked, one life, state 1.</summary>
    public static byte[] EdgeBladeBlob(int x, int y, float angle)
    {
        var w = new BlobWriter().U8(0).U8(0);
        w.I32(x).I32(y).F32(angle).U16(14).U8(0).U8(0x0a).U8(0).U8(1).U8(1); // Positioned
        w.U8(0);                                                        // Animated: the .ot's
        w.U8(0);                                                        // TriggerableBlockage: unblocked
        w.Stats(null).U32(0).U32(0).U8(0);                              // Stats
        w.U16(0);                                                       // Buffs
        foreach (uint pool in new uint[] { 1, 0, 0 }) w.U32(pool).U32(0).U16(0).U32(0).U32(0);
        w.U8(0);                                                        // Life flags
        w.U8(1);                                                        // Transitionable
        w.WStr32("").U16(0).U8(0).U32(0).U32(0).U8(0).U8(0);            // Actor
        return w.U8(0).ToArray();                                       // tasks
    }

    /// <summary>How often the dart traps fire (live 2026-10-31, the original's Imperial Gardens: every turret at once,
    /// about 1.5 s apart; the real timer is the server's).</summary>
    public const double ArrowVolleySeconds = 1.5;

    /// <summary>The Labyrinth's rooms' dart traps: the trial's turret, the wall darts (<c>walldart_*.ao</c>: <c>_Single</c>,
    /// <c>_SingleSlow</c>, <c>_SingleSlowOffset</c>, <c>_Delayed</c>, and <c>_Triggered</c>, which its room's pressure
    /// plates fire) and the turrets of <c>AlternateArrowTraps/</c>.</summary>
    public const string TriggeredArrowTrap = "Metadata/Terrain/Labyrinth/Traps/LabyrinthArrowTrap_Triggered";

    public static bool IsArrowTrap(string type) => type.StartsWith(ArrowTrap, StringComparison.Ordinal)
        || type.StartsWith("Metadata/Terrain/Labyrinth/Traps/AlternateArrowTraps/LabyrinthArrowTrap", StringComparison.Ordinal);

    /// <summary>A dart trap's volley: every <paramref name="Period"/> seconds, first <paramref name="Delay"/> after the
    /// start, each shot a turn (radians, clockwise from its facing) at a time into the volley.</summary>
    public sealed record DartVolley(double Period, double Delay, IReadOnlyList<(double Turn, double At)> Shots);

    /// <summary>A dart trap's volley, or null for one its pressure plates fire (<see cref="TriggeredArrowTrap"/>: one dart
    /// ahead each time). The .ot's: the trial's four (<c>number_of_additional_projectiles</c> 3, a nova); one dart ahead
    /// for the wall darts (<c>projectiles_nova 0</c>); a spiral nova of <c>base_number_of_projectiles_in_spiral_nova</c>
    /// darts over <c>projectile_spiral_nova_angle</c> in <c>projectile_spiral_nova_time_ms</c>: Two90 two over 90° in
    /// 756 ms, Two180 two over 180° at once, Three three over 180° in 1024 ms. The periods and the wall darts' delays are
    /// the server's: ours, the trial's 1.5 s, the slow ones 3 s, a delayed or offset one half a period later.</summary>
    public static DartVolley? Volley(string type)
    {
        static DartVolley Spiral(int count, double degrees, double ms) =>
            new(ArrowVolleySeconds, 0, [.. Enumerable.Range(0, count).Select(k => (k * degrees / (count - 1) * Math.PI / 180, k * ms / (count - 1) / 1000))]);
        return type[(type.LastIndexOf('/') + 1)..] switch
        {
            "LabyrinthArrowTrap" => new(ArrowVolleySeconds, 0, [.. Enumerable.Range(0, 4).Select(k => (k * Math.PI / 2, 0.0))]),
            "LabyrinthArrowTrap_Single" => new(ArrowVolleySeconds, 0, [(0, 0)]),
            "LabyrinthArrowTrap_Delayed" => new(ArrowVolleySeconds, ArrowVolleySeconds / 2, [(0, 0)]),
            "LabyrinthArrowTrap_SingleSlow" => new(ArrowVolleySeconds * 2, 0, [(0, 0)]),
            "LabyrinthArrowTrap_SingleSlowOffset" => new(ArrowVolleySeconds * 2, ArrowVolleySeconds, [(0, 0)]),
            "LabyrinthArrowTrapTwo90DegreeArrows" => Spiral(2, 90, 756),
            "LabyrinthArrowTrapTwo180DegreeArrows" => Spiral(2, 180, 1),
            "LabyrinthArrowTrapThreeArrows" => Spiral(3, 180, 1024),
            _ => null,
        };
    }

    /// <summary>The Labyrinth's rooms' pressure plates (<c>PressurePlate_Reset</c>, <c>_Reset_4</c>, <c>_ResetMulti_4</c>;
    /// Transitionable 1 up, 2 down): a player stepping on one presses it and fires the <see cref="TriggeredArrowTrap"/>s
    /// of its room. <c>Reset</c> comes up when the player steps off, the <c>_4</c> ones 4 s after (<c>_ResetMulti_4</c>
    /// again and again while the player stays); our reading of the names, no file says.</summary>
    public const string PressurePlatePrefix = "Metadata/Terrain/Labyrinth/Objects/Puzzle_Parts/PressurePlate_Reset";

    public static bool IsPressurePlate(string type) => type.StartsWith(PressurePlatePrefix, StringComparison.Ordinal);

    /// <summary>How long a pressed plate stays down, or null until the player steps off.</summary>
    public static double? PlateDownSeconds(string type) => type.EndsWith("_4", StringComparison.Ordinal) ? 4 : null;

    /// <summary>How near a plate's centre a player presses it: half its tile (its .ot's <c>object_size</c> 8).</summary>
    public const int PlateHalf = 8;

    /// <summary>A dart trap's shot (Projectiles.dat): the poison darts, for its skill's <c>global_poison_on_hit</c>.</summary>
    public const string ArrowProjectile = "Metadata/Projectiles/PoisonArrowTrapProjectile";

    /// <summary>A dart trap's four shots: its .ot's <c>number_of_additional_projectiles</c> 3 and <c>projectiles_nova</c>,
    /// so a quarter turn apart from the way the turret faces (its room line's angle, as a projectile object's rotation:
    /// direction (sin a, −cos a)). Each leaves <c>projectile_nova_extend_projectiles_forward_by_x_units</c> (50) ahead
    /// of the turret, clear of its statue.</summary>
    public static IEnumerable<(double X, double Y)> ArrowDirections(float angle) =>
        Enumerable.Range(0, 4).Select(k => angle + k * Math.PI / 2).Select(a => (Math.Sin(a), -Math.Cos(a)));

    public static double ArrowForwardCells => 50 / WorldUnits.PerCell;

    /// <summary>A turret's Hull (<c>statueturret_circleclean.ao</c>, as every turret model but <c>_noblock</c>): squares
    /// of ±3.25 cells for walking and ±2.26 for projectiles, turned with the turret. The client stamps it, so a dart stops
    /// at another turret and a player walks round one (live 2026-10-31: the darts vanished at the turrets while the
    /// server's flew on).</summary>
    public const double ArrowTrapWalkHalf = 3.25, ArrowTrapProjectileHalf = 2.26;

    /// <summary>The cells whose centres lie in an object's Hull rectangle of these half sizes (cells, its model's
    /// walk_tri / proj_tri), turned by its angle.</summary>
    public static IEnumerable<(int X, int Y)> HullCells(int x, int y, float angle, double halfX, double halfY)
    {
        int reach = (int)Math.Ceiling(Math.Sqrt(halfX * halfX + halfY * halfY));
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        for (int dy = -reach; dy <= reach; dy++)
            for (int dx = -reach; dx <= reach; dx++)
                if (Math.Abs(dx * cos + dy * sin) <= halfX && Math.Abs(-dx * sin + dy * cos) <= halfY)
                    yield return (x + dx, y + dy);
    }

    /// <summary>A trial door's walk Hull, half sizes in cells (across the way, through it), by its room line's model:
    /// <c>LabyrinthDoor.ao</c> (the trial's door) ±14 × ±6, <c>preview_labyrinthdoornofx.ao</c> (the Catacombs' Locked
    /// Door) ±14 × ±3.6, <c>doorbar_prison.ao</c> ±2 × ±5.5, <c>thinwallbar_door.ao</c> (the Crypt trial's plain door)
    /// ±2.3 × ±5.5.</summary>
    public static (double X, double Y) DoorHull(string model) => Path.GetFileName(model).ToLowerInvariant() switch
    {
        "preview_labyrinthdoornofx.ao" => (14, 3.6),
        "doorbar_prison.ao" => (2, 5.5),
        "thinwallbar_door.ao" => (2.3, 5.5),
        "doorbar_ruinedtemple.ao" => (2.2, 8),
        // A Door_Counter gate in a fence line, a tile wide (our estimate from the fence pieces a tile apart); a smashable
        // thin wall and a secret wall, a tile of wall each (ours).
        "ruinedtemple_smallgate.ao" or "drygarden_smallgate.ao" => (11.5, 3),
        "eleganthouse_thinwallstraight_smashable_openanimation_v01_01_animation.ao" => (11.5, 2.5),
        "gardendry_wall_straight_secretwall_v01_01_openanimation.ao" => (11.5, 4),
        _ => (14, 6),
    };

    /// <summary>Where the darts leave the turret: its rig's <c>cast</c> contact (<c>StatueTurret/rig.amd</c>, 118.757 up).</summary>
    public static int ArrowLaunchHeight => new ContactPoint(0, 0, -118.757f).HeightSteps;

    /// <summary>Animation.dat <c>SpellProjectile</c>, which the turret's .act plays as its <c>cast</c> clip (1000 ms, the
    /// contact at 0 ms).</summary>
    public const byte CastAnimation = 20;

    /// <summary>A dart's hit on a player (GrantedEffectStatSets <c>LbArrowTrap</c>: 15% of maximum life plus 9% of maximum
    /// energy shield, 33% more against players), read as the spike trap's (<see cref="SpikeDamage"/>).</summary>
    public static int? ArrowDamage(uint maxLife, uint maxEnergyShield) => TrapDamage(ArrowStats.Value, maxLife, maxEnergyShield);

    static readonly Lazy<(int Life, int EnergyShield, int VsPlayers)?> ArrowStats = new(() => TrapStats("LbArrowTrap"));

    /// <summary>What a dart's poison is reckoned from: its hit's share of maximum life alone. The poison goes past energy
    /// shield, so the whole hit's would turn an energy shield character's own pool against their life (live 2026-10-31:
    /// about 100 000 energy shield took a 13 135 hit, whose poison, 3 940 over 2 s, killed 3 707 life; our reading).</summary>
    public static int? ArrowPoisonBase(uint maxLife) => ArrowDamage(maxLife, 0);

    /// <summary>A dart's hit poisons (<c>global_poison_on_hit</c>) for 2 s; every hit adds a stack of its own. Chaos
    /// damage: past energy shield, less the player's chaos resistance. Its base is the hit's life share only
    /// (<see cref="ArrowPoisonBase"/>), and a stack deals this much of it a second, fitted by eye: the wiki's 30% with
    /// the .ot's <c>monster_poison_damage_+%_final</c> −50 made 111 a second for a 3 707 life character, too much
    /// against the original, which looked about 70 (live 2026-10-31). <c>/trial_dart_poison</c> (dev) changes it.</summary>
    public static double ArrowPoisonPerSecond = 0.10;
    public const double ArrowPoisonSeconds = 2;

    /// <summary>A dart's slow: BuffDefinitions <c>labyrinth_arrow_slow</c> (invisible), whose stat is this, for the skill's
    /// <c>base_skill_effect_duration</c> (2 s) from the latest hit. Its value is not in the client data: live
    /// (2026-10-31, the original) a character's movement speed went from +19% to −52% under the darts,
    /// (1 − 0.52) / 1.19 ≈ 0.40 of it, so 60% less, final.</summary>
    public const string ArrowSlowStat = "labyrinth_arrow_movement_speed_+%_final";
    public const int ArrowSlowPercent = -60;
    public const double ArrowSlowSeconds = 2;

    /// <summary>The <c>labyrinth_arrow_slow</c> BuffDefinitions row, or null without the table.</summary>
    public static ushort? ArrowSlowBuffRow => BuffRows.Value.TryGetValue("labyrinth_arrow_slow", out ushort row) ? row : null;

    /// <summary>A dart trap's 0x2b9 blob (LabyrinthArrowTrap.ot: Positioned, Transitionable, Animated, Pathfinding,
    /// BaseEvents, Stats, Buffs, Life, Actor), as the spike trap's (<see cref="SpikeTrapBlob"/>): the .ot's team 14, the
    /// room line's angle and model, state 1, one life, no skills (the server fires its darts).</summary>
    /// <remarks><see cref="TriggeredArrowTrap"/>'s .ot reads Animated before Transitionable.</remarks>
    public static byte[] ArrowTrapBlob(int x, int y, float angle, string model, string? type = null)
    {
        var w = new BlobWriter().U8(0).U8(0);
        w.I32(x).I32(y).F32(angle).U16(14).U8(0).U8(0x02).U8(0).U8(1); // Positioned
        if (type == TriggeredArrowTrap) w.U8(1).WStr32(model).U8(1);    // Animated: the room line's, Transitionable
        else w.U8(1).U8(1).WStr32(model);                               // Transitionable, Animated: the room line's
        w.Stats(null).U32(0).U32(0).U8(0);                              // Stats
        w.U16(0);                                                       // Buffs
        foreach (uint pool in new uint[] { 1, 0, 0 }) w.U32(pool).U32(0).U16(0).U32(0).U32(0);
        w.U8(0);                                                        // Life flags
        w.WStr32("").U16(0).U8(0).U32(0).U32(0).U8(0).U8(0);            // Actor
        return w.U8(0).ToArray();                                       // tasks
    }

    /// <summary>The components each type's 0x2b9 blob reads, in its order (scripts/re/ot_components.py; BaseEvents reads
    /// nothing but counts for the 0x2ba component index).</summary>
    static readonly Dictionary<string, string[]> Components = new(StringComparer.Ordinal)
    {
        [IntroDoor] = ["Positioned", "BaseEvents", "Animated", "InteractionAction", "Transitionable", "TriggerableBlockage"],
        [BarDoor] = ["Positioned", "BaseEvents", "Animated", "InteractionAction", "Transitionable", "TriggerableBlockage"],
        [Door] = ["Positioned", "InteractionAction", "BaseEvents", "Animated", "Transitionable", "TriggerableBlockage", "MinimapIcon"],
        [Lever] = ["Positioned", "Transitionable", "InteractionAction", "BaseEvents", "Animated", "MinimapIcon"],
        [Switch] = ["Positioned", "Transitionable", "InteractionAction", "BaseEvents", "Animated", "MinimapIcon"],
        [MultiSwitch] = ["Positioned", "Transitionable", "InteractionAction", "BaseEvents", "Animated", "MinimapIcon"],
        [RailSwitch] = ["Positioned", "BaseEvents", "Transitionable", "Animated"],
        [RailSwitchSwapped] = ["Positioned", "BaseEvents", "Transitionable", "Animated"],
        [OpenDoor] = ["Positioned", "InteractionAction", "BaseEvents", "Animated", "Transitionable", "TriggerableBlockage", "MinimapIcon"],
        [Plaque] = ["Positioned", "BaseEvents", "Animated", "Transitionable", "MinimapIcon", "InteractionAction"],
        [Portal] = ["Positioned", "InteractionAction", "BaseEvents", "Animated", "Transitionable", "MinimapIcon"],
        [Map] = ["Positioned", "BaseEvents", "Animated", "InteractionAction"],
        [Gears] = ["Positioned", "BaseEvents", "Animated", "Transitionable"],
        [Lava] = ["Positioned", "Animated", "BaseEvents", "Transitionable", "Buffs"],
        [Lava3x3] = ["Positioned", "Animated", "BaseEvents", "Transitionable", "Buffs"],
        [Lava1x3] = ["Positioned", "Animated", "BaseEvents", "Transitionable", "Buffs"],
        [InertLava] = ["Positioned", "Animated", "BaseEvents", "Transitionable"],
        [PressurePlatePrefix] = ["Positioned", "Transitionable", "BaseEvents", "Animated"],
        [PressurePlatePrefix + "_4"] = ["Positioned", "Transitionable", "BaseEvents", "Animated"],
        [PressurePlatePrefix + "Multi_4"] = ["Positioned", "Transitionable", "BaseEvents", "Animated"],
        [LabyrinthWiring.CounterDoor] = ["Positioned", "InteractionAction", "BaseEvents", "Animated", "Transitionable", "TriggerableBlockage", "MinimapIcon"],
        [SmashableWall] = ["Positioned", "BaseEvents", "Animated", "Stats", "InteractionAction", "Transitionable", "TriggerableBlockage"],
        [HiddenWall] = ["Positioned", "BaseEvents", "Animated", "Transitionable", "TriggerableBlockage", "MinimapIcon"],
        [HiddenSwitch] = ["Positioned", "Transitionable", "BaseEvents", "Animated", "InteractionAction"],
        [StatueSpawner] = ["Positioned", "BaseEvents", "Animated", "Transitionable", "TriggerableBlockage"],
        [LabyrinthWiring.OnceMultiSwitch] = ["Positioned", "Transitionable", "InteractionAction", "BaseEvents", "Animated", "MinimapIcon"],
        [LabyrinthWiring.CrankPrefix] = ["Positioned", "Transitionable", "InteractionAction", "BaseEvents", "Animated", "MinimapIcon"],
        [LabyrinthWiring.CrankPrefix + "_10"] = ["Positioned", "Transitionable", "InteractionAction", "BaseEvents", "Animated", "MinimapIcon"],
        [LabyrinthWiring.CrankPrefix + "_16"] = ["Positioned", "Transitionable", "InteractionAction", "BaseEvents", "Animated", "MinimapIcon"],
    };

    /// <summary>Transitionable states: 1 runs create_untransitioned (closed, unpulled, dark), 2 the transition (num_states 2).
    /// A Door_Open is the other way round: 1 open, 2 closed (its .ot's <c>create_state_1</c> plays the open bars).</summary>
    public const byte Closed = 1, Open = 2;

    /// <summary>A crank's (Switch_Reset*, num_states 3) third state: down for good, its minimap icon hidden.</summary>
    public const byte Spent = 3;

    /// <summary>Whether it is a door a lever or a counter opens (Door_Closed, Door_Counter), or the trial's own door or a
    /// plain one.</summary>
    public static bool IsDoor(string type) => type is IntroDoor or Door or BarDoor or LabyrinthWiring.CounterDoor or SmashableWall or HiddenWall;

    /// <summary>Whether an opened Locked Door goes once its opening has played (the trials' bars, lifted out of the way);
    /// the Labyrinth's rooms' gates (<c>DryGarden_SmallGate</c>, <c>RuinedTemple_SmallGate</c>) swing open and stay (live
    /// 2026-11-01, the original).</summary>
    public static bool GoesWhenOpened(string type, string model) =>
        type == Door && !Path.GetFileName(model).Contains("SmallGate", StringComparison.OrdinalIgnoreCase);

    /// <summary>How near a cast the player makes a smashable wall breaks (cells from its middle: half its tile and a
    /// melee reach; ours). The Gateway does not trace a skill's hits on objects, so a cast in reach stands for one.</summary>
    public const int SmashReach = 15;

    /// <summary>Whether a tile object is sent at its tile line's height (the smashable walls hang 101 up, their upper part
    /// in the floor without it; live 2026-11-01).</summary>
    public static bool AtTileHeight(string type) => type is SmashableWall or HiddenWall || IsStatueSpawner(type);

    /// <summary>Whether a door in this Transitionable state blocks the way.</summary>
    public static bool Blocks(string type, byte state) => type == OpenDoor ? state == Open : state == Closed;

    /// <summary>Positioned sizes (object_size − 1): the doors and the lever 4, the plaque and the portal 6, a smashable
    /// wall 6, a hidden switch 8.</summary>
    public static int Size(string type) => type switch { Plaque or Portal or SmashableWall => 5, HiddenSwitch => 7, _ => 3 };

    public static bool Handles(string type) => Components.ContainsKey(Kind(type));

    // The Components row of a type: a plate's (Plate), a statue spawner's (they share StatueSpawner.ot's).
    static string Kind(string type) => IsStatueSpawner(type) ? StatueSpawner : Plate(type);

    /// <summary>The Labyrinth tiles' statues that wake (<c>Metadata/Monsters/Statue/*/Spawner_LabyrinthStatue*</c>,
    /// extending <c>StatueSpawner.ot</c>: server-only, Transitionable, blocked; transitioned, its stone is hidden), and the
    /// monster of its name (<see cref="StatueOf"/>), a stone statue itself with its glow (its .ao's attached
    /// <c>statue_balls</c>), which waits on the pedestal in its Emerge clip's first frame and steps down when it wakes.</summary>
    public const string StatueFolder = "Metadata/Monsters/Statue/", StatueSpawner = StatueFolder + "StatueSpawner";

    public static bool IsStatueSpawner(string type) =>
        type.StartsWith(StatueFolder, StringComparison.Ordinal) && type[(type.LastIndexOf('/') + 1)..].StartsWith("Spawner_", StringComparison.Ordinal);

    /// <summary>The monster a statue spawner wakes into: <c>Spawner_X</c>'s <c>Metadata/Monsters/Statue/X</c> (every
    /// Labyrinth one has its MonsterVarieties row so: Overgrown Colossus, Fencer, Archer, Maul and Martial Statues).</summary>
    public static string StatueOf(string type) => StatueFolder + type[(type.LastIndexOf('/') + 1 + "Spawner_".Length)..];

    /// <summary>How near the player a statue wakes (cells; ours).</summary>
    public static double StatueWakeCells = 30;

    /// <summary>The Transitionable component's index in the type's component order.</summary>
    public static byte TransitionableIndex(string type) => (byte)Array.IndexOf(Components[Kind(type)], "Transitionable");

    /// <summary>The trial in this area, or null.</summary>
    public static Trial? For(string areaId) => All.Value.GetValueOrDefault(areaId);

    /// <summary>Whether the area is one of the Labyrinth's own (WorldAreas IsLabyrinthArea: the <c>1_Labyrinth_*</c> rooms
    /// of a run and Izaro's; not the plaza, not a trial): its rooms have the trials' traps.</summary>
    public static bool InLabyrinth(string areaId) => LabyrinthAreas.Value.Contains(areaId);

    static readonly Lazy<HashSet<string>> LabyrinthAreas = new(() =>
    {
        try
        {
            if (!File.Exists("data/tables/WorldAreas.json")) return [];
            using var areas = JsonDocument.Parse(File.ReadAllBytes("data/tables/WorldAreas.json"));
            return [.. areas.RootElement.EnumerateArray().Where(a => a.GetProperty("IsLabyrinthArea").GetBoolean())
                .Select(a => a.GetProperty("Id").GetString() ?? "")];
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine($"[gateway] data/tables/WorldAreas.json: {ex.Message}");
            return [];
        }
    });

    /// <summary>Every trial of the table (Normal, Cruel, Merciless and Eternal).</summary>
    public static IReadOnlyCollection<Trial> Trials => All.Value.Values;

    /// <summary>The Labyrinth tier that needs the trial (Labyrinths RequiredTrials): 1 Normal's six, 2 Cruel's three,
    /// 3 Merciless's three, 4 Eternal's six (the map trials), by its quest flag (NormalLabyrinthCompleted…, Cruel…,
    /// Merciless…, Map…).</summary>
    public static int Tier(Trial trial) =>
        trial.Flag.StartsWith("Normal", StringComparison.Ordinal) ? 1 : trial.Flag.StartsWith("Cruel", StringComparison.Ordinal) ? 2
        : trial.Flag.StartsWith("Merciless", StringComparison.Ordinal) ? 3 : 4;

    static readonly Lazy<Dictionary<string, Trial>> All = new(() =>
    {
        try
        {
            if (!File.Exists("data/tables/LabyrinthTrials.json") || !File.Exists("data/tables/WorldAreas.json")) return [];
            using var areas = JsonDocument.Parse(File.ReadAllBytes("data/tables/WorldAreas.json"));
            var areaIds = areas.RootElement.EnumerateArray().Select(a => a.GetProperty("Id").GetString() ?? "").ToList();
            var flags = Quests.ParseRows(File.Exists("data/tables/QuestFlags.json") ? File.ReadAllText("data/tables/QuestFlags.json") : "[]")
                .ToDictionary(kv => kv.Value, kv => kv.Key);
            return Parse(File.ReadAllText("data/tables/LabyrinthTrials.json"), areaIds, flags).ToDictionary(t => t.Area);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine($"[gateway] data/tables/LabyrinthTrials.json: {ex.Message}");
            return [];
        }
    });

    /// <summary>LabyrinthTrials rows: WorldAreas key, column 1 the league quest flag id, column 7 the QuestFlags row.</summary>
    public static List<Trial> Parse(string json, IReadOnlyList<string> areaIds, IReadOnlyDictionary<int, string> flagIds)
    {
        using var doc = JsonDocument.Parse(json);
        var trials = new List<Trial>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            int area = row.GetProperty("WorldAreas").GetInt32(), flag = row.GetProperty("__col7").GetInt32();
            if (area < 0 || area >= areaIds.Count || !flagIds.TryGetValue(flag, out var name)) continue;
            trials.Add(new Trial(areaIds[area], name, row.GetProperty("__col1").GetInt32()));
        }
        return trials;
    }

    /// <summary>A trial object's 0x2b9 blob: its type's components (<see cref="Components"/>) with the room line's model,
    /// angle, scale and fixed height (world z, down: the Crematorium's hot plates 125 below the floor's grates, which
    /// they glow through; on the terrain they stood above it). Only the plates are sent theirs: the doors and levers,
    /// checked live on the terrain, keep standing on it. TriggerableBlockage (0x141e44290) is blocked with no
    /// sections while closed.</summary>
    public static byte[] Blob(string type, string model, int x, int y, float angle, float scale, byte state, float? height = null)
    {
        var w = new BlobWriter().U8(0).U8(0);
        foreach (string c in Components[Kind(type)])
            switch (c)
            {
                // x, y, rotation, u16, flags0 (0x20: uniform scale, then the x / y stretches), flags1 0x02 (shown). A door
                // also has flags1 0x20 (+0x1e7 bit 1: no static position), or the client's object pass (0x141d88690)
                // stamps its Hull whatever its TriggerableBlockage says and an opened door stays closed to walking, and
                // 0x08 with 1 (+0x1e6 bit 0x40: blocks no walker), so only the blockage closes its cells.
                // f0 0x04: the fixed height, i32 whole steps of 7.8125 and the f32 z the client uses (as an area
                // transition's record height).
                case "Positioned":
                    // A smashable wall keeps its static position: the client stamps its Hull (walking and projectiles) and
                    // its transition removes it (RemoveCollidability).
                    bool scaled = scale != 1, blockage = Components[Kind(type)].Contains("TriggerableBlockage") && type != SmashableWall;
                    w.I32(x).I32(y).F32(angle).U16(0).U8((byte)((scaled ? 0x20 : 0) | (height is null ? 0 : 0x04))).U8((byte)(blockage ? 0x2a : 0x02));
                    if (scaled) w.F32(scale).F32(1).F32(1);
                    if (height is float z) w.I32(InstanceProtocol.HeightMarker(z)).F32(z);
                    w.U8(0).U8(1);
                    if (blockage) w.U8(1);
                    break;
                case "Animated": if (model.EndsWith(".ao", StringComparison.OrdinalIgnoreCase)) w.U8(1).WStr32(model); else w.U8(0); break;
                case "InteractionAction": w.U8(0); break;
                case "Transitionable": w.U8(state); break;
                case "TriggerableBlockage": if (Blocks(type, state)) w.U8(1).U8(0); else w.U8(0); break;
                case "MinimapIcon": w.U8(0); break;
                case "Stats": w.Stats(null).U32(0).U32(0).U8(0); break;
                case "Buffs": w.U16(0); break;
            }
        return w.U8(0).ToArray();   // tasks
    }
}
