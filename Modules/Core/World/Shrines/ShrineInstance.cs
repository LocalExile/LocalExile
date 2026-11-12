namespace GameServer.Modules.Shrines;

/// <summary>Where one physical shrine is in its life. The server is authoritative for every step.</summary>
public enum ShrineState
{
    /// <summary>Spawned and unclaimed: interactable, running its pre-activation behaviour, granting nothing yet.</summary>
    Inactive,
    /// <summary>A click was accepted; the activation is running and another click must not start a second one.</summary>
    Activating,
    /// <summary>Claimed: its buff is on the player and it cannot be used again.</summary>
    Used,
}

/// <summary>
/// One shrine spawned in an area, with its own state. Two shrines of one type are two instances: activating one never
/// touches the other, and a repeated click on a claimed shrine grants nothing.
/// </summary>
public sealed class ShrineInstance
{
    public required ShrineCatalog.ShrineDefinition Definition { get; init; }

    /// <summary>The cell the shrine object stands on.</summary>
    public required (int X, int Y) At { get; init; }

    /// <summary>The statue model the room line named.</summary>
    public string? Model { get; init; }

    /// <summary>The statue's facing, as the room line placed it.</summary>
    public float Rotation { get; init; }

    /// <summary>The server object id the client was told (0x02bb).</summary>
    public uint ObjectId { get; set; }

    public ShrineState State { get; set; } = ShrineState.Inactive;

    /// <summary>The effects this shrine runs while it is unclaimed (<see cref="ShrineRegistry.Before"/>): its auras,
    /// its guard packs. One instance each, owned by this shrine.</summary>
    public IReadOnlyList<IShrineEffect> Before { get; init; } = [];

    /// <summary>The effects its buff runs while it is active (<see cref="ShrineRegistry.After"/>).</summary>
    public IReadOnlyList<IShrineEffect> After { get; init; } = [];

    /// <summary>The buff's stats, by client stat key: the registered definition's or, when it names none, the Shrines
    /// row's <c>Player_ShrineBuffsKey</c> values.</summary>
    public IReadOnlyList<(uint Key, int Value)> Stats { get; init; } = [];

    /// <summary>The buff's duration in seconds: the registered definition's or the Shrines row's.</summary>
    public int Duration { get; init; }

    /// <summary>The monsters the pre-activation behaviour created (its data guard packs), if any.</summary>
    public List<uint> Guards { get; } = [];

    /// <summary>Monsters currently in the shrine's neighbourhood, for the enter/leave behaviour steps.</summary>
    public HashSet<uint> Nearby { get; } = [];

    /// <summary>The player buff instance and the token that owns it (a replacement must not be removed by an older timer).</summary>
    public ushort BuffInstance { get; set; }
    public int BuffToken { get; set; }

    /// <summary>When the buff was granted, on the connection clock.</summary>
    public double ActivatedAt { get; set; }
}
