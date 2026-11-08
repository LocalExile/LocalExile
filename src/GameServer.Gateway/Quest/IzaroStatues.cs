/// <summary>
/// Izaro's Toggle Statues (the wiki's Conduits; LabyrinthSecrets <c>ToggleStatues&lt;tier&gt;1</c>, room boss_statues.arm):
/// in that arena three statues, fire, cold and lightning (LabyrinthSecretEffects <c>ToggleStatues&lt;tier&gt;_&lt;element&gt;</c>:
/// EmpowerStatue1-3), rise one at a time during the fight on the arena's small elevators (Elevator_Player, the wiki's
/// "three small holes"), each giving Izaro its element's buff while on (BuffDefinitions <c>izaro_*_buff_phase1</c>: chance
/// to ignite, freeze or shock, that element's damage taken, physical added as it). A click turns one off for a while.
/// The ones on (or not risen yet) when the trial ends stay on him for the run's later trials: there they stand in an
/// alcove of the arena and on a ledge of the room before it (QuestObjects Rules.IzaroArena.cs) as StatueDoodad1-3 and give the
/// "…2" rows' buffs (the same, with more item rarity and quantity), when the later Izaro room has the
/// <c>ToggleStatues&lt;tier&gt;2</c> secret.
/// The original (live, the user): they rise one at a time, the first with Izaro; a click turns one off for 15 s in
/// Normal (the user thinks less in the higher tiers; not measured, so 15 for all). The rise interval is ours. The ignite,
/// freeze and shock chances are given to the client's buff but not used by the server's monster hits.
/// </summary>
static class IzaroStatues
{
    public const string Mechanic = "ToggleStatues";

    /// <summary>EmpowerStatueBase.ot's states: 1 lowered, 2 raising (lift_raise, 10.7 s), 3 raised and off, 4 on (targetable;
    /// back to 3 plays Deactivate), 5 lowering (lift_lower, 3.333 s), 6 lowered again.</summary>
    public const byte Lowered = 1, Raising = 2, Off = 3, On = 4, Lowering = 5, Down = 6;

    /// <summary>StatueDoodad's on state (2: Activated_idle and its glow).</summary>
    public const byte DoodadOn = 2;

    /// <summary>The Transitionable's index: EmpowerStatue (Positioned, BaseEvents, InteractionAction, Animated,
    /// Transitionable), StatueDoodad (Positioned, BaseEvents, Animated, Transitionable).</summary>
    public const byte StatueTransitionable = 4, DoodadTransitionable = 3;

    static readonly string[] StatueComponents = ["Positioned", "BaseEvents", "InteractionAction", "Animated", "Transitionable"];
    static readonly string[] DoodadComponents = ["Positioned", "BaseEvents", "Animated", "Transitionable"];

    /// <summary>The small elevators' (Elevator_Player: Izaro's elevator's states) and the statues' rise, and their lowering.</summary>
    public const string SmallElevator = "Metadata/Terrain/Labyrinth/Objects/Elevator_Player";
    public const double RiseSeconds = 10.7, LowerSeconds = 3.333;

    /// <summary>The time between the statues' rises (ours), and how long a click turns one off (the original, Normal).</summary>
    public const double RiseInterval = 12, OffSeconds = 15;

    public static bool IsStatue(string type) => IzaroEffects.IsType(type);

    public static byte[] Blob(IzaroEffects.Effect statue, int x, int y, float angle, byte state) => statue.Returning
        ? LabyrinthTrials.Blob(DoodadComponents, "", x, y, angle, 1, state, null, blockage: false, blocked: false)
        : LabyrinthTrials.Blob(StatueComponents, "", x, y, angle, 1, state, null, blockage: false, blocked: false);

    /// <summary>His line after the trial's end line: how many statues stayed on (one: its element's).</summary>
    public static string FinishLine(IReadOnlyList<IzaroEffects.Effect> left) => left.Count == 1
        ? $"IzaroFinishToggleStatues1{left[0].Element switch { 0 => "Fire", 1 => "Cold", _ => "Lightning" }}"
        : $"IzaroFinishToggleStatues{left.Count}";
}
