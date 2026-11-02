using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

sealed partial class ClientState
{
    /// <summary>The survivors run of this connection (Modules/Core/Game/Survivors), null when none is active.</summary>
    public SurvivorsRun? Survivors;
    /// <summary>The level-up window's choice per offered item id, null when the window is closed.</summary>
    public Dictionary<uint, SurvivorsChoice>? SurvivorsOffer;
    /// <summary>The display name of each offered item, for the chat line that confirms a pick.</summary>
    public Dictionary<uint, string>? SurvivorsOfferNames;
    /// <summary>The unique items the window shows by inventory item id (the pick equips that very item).</summary>
    public Dictionary<uint, Item>? SurvivorsOfferItems;
    /// <summary>The reward inventory's revision for the level-up window (S→C 0xbf).</summary>
    public uint SurvivorsOfferRevision;
    /// <summary>This connection's clock was stopped for the level-up window and must restart after the pick.</summary>
    public bool SurvivorsPausedByWindow;
    /// <summary>The run's skill catalogue, built once at its start: the window's items, the Actor skills and their ids
    /// all index it, so it must not be rebuilt with a different order mid-run.</summary>
    public IReadOnlyList<SurvivorsSkill>? SurvivorsCatalog;
    /// <summary>The equipped item ids a run granted: their requirements are waived while the run lasts.</summary>
    public readonly HashSet<uint> SurvivorsItemIds = [];
    /// <summary>The herald aura buff instances this run put on the player (S→C 0x1c1), removed when it ends.</summary>
    public readonly List<ushort> SurvivorsHeraldBuffs = [];
    /// <summary>The catalogue indices of the heralds already published as auras.</summary>
    public readonly HashSet<int> SurvivorsHeraldSkills = [];
}
