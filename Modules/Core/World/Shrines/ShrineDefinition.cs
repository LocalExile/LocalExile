namespace GameServer.Modules.Shrines;

/// <summary>
/// One shrine type's gameplay, registered independently with <see cref="ShrineRegistry.Register"/>. It names the effects
/// that run while the shrine is unclaimed and while the player's buff is active, and, only where the client data is not
/// what the game does, its stat list and duration.
/// <para>Everything it does not name comes from its Shrines row: the name, the Shrines row the client draws the icon
/// from, the buff row the player sees, the visual variations, the guard packs and (by default) the buff's stats and
/// duration.</para>
/// <para>A new type is this checklist and nothing else: its client row, one <see cref="ShrineDefinition"/> in
/// <see cref="ShrineRegistry.RegisterDefaults"/> (its <see cref="Model"/>, <see cref="BeforeActivation"/>,
/// <see cref="AfterActivation"/>, <see cref="PlayerStats"/> and <see cref="Duration"/> as needed), and one reusable
/// <see cref="IShrineEffect"/> class per new behaviour. <see cref="ShrineManager"/> and the lifecycle never change for a
/// type, and no type's code is touched by another's.</para>
/// </summary>
public sealed class ShrineDefinition
{
    /// <summary>The Shrines row id this gameplay belongs to (Acceleration, Lightning, …).</summary>
    public required string Id { get; init; }

    /// <summary>The name the tooltip and the log use, when it differs from the row's string.</summary>
    public string? Name { get; init; }

    /// <summary>The buff's duration in seconds; null takes the Shrines row's <c>Duration</c>.</summary>
    public int? Duration { get; init; }

    /// <summary>The statue model this type stands as; null keeps the model the level generator's room line named (the
    /// generic shrine for <c>/shrines</c>). The standard rows carry no <c>ShrineVisualArtVariations</c>, so a type with
    /// its own statue names it here.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// The player buff's stats by client stat name, when the registered gameplay is the authority rather than the row.
    /// Null keeps the row's <c>Player_ShrineBuffsKey</c> values (see <see cref="ShrineCatalog"/>).
    /// </summary>
    public IReadOnlyList<(string Stat, int Value)>? PlayerStats { get; init; }

    /// <summary>The effects that run while the shrine is unclaimed: monster auras, guards, anything that ends on the
    /// click. Factories, because each shrine gets its own instances.</summary>
    public IReadOnlyList<Func<IShrineEffect>> BeforeActivation { get; init; } = [];

    /// <summary>The effects that run while the player's buff is active: novae, summons, clouds. Factories, as above.</summary>
    public IReadOnlyList<Func<IShrineEffect>> AfterActivation { get; init; } = [];
}
