using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Modules.Survivors;

sealed class SurvivorsState
{
    public CancellationTokenSource? Cancellation;
    public SurvivorsOwnership? Ownership;
    public PendingTransfer? StageTransfer;
    public int Stage;
    public readonly HashSet<uint> Effects = [];
    public Task? Runner;
    public bool Entered;
    /// <summary>The survivors run of this connection (Modules/Survivors), null when none is active.</summary>
    public SurvivorsRun? Survivors;
    /// <summary>This connection's client has the reward inventory from 0xc0: 0xbf before it is fatal
    /// ("Got an inventory set change for an inventory that isn't in the set").</summary>
    public bool SurvivorsOfferInventoryRegistered;
    /// <summary>The level-up window's choice per offered item id, null when the window is closed.</summary>
    public Dictionary<uint, SurvivorsChoice>? SurvivorsOffer;
    /// <summary>The display name of each offered item, for the chat line that confirms a pick.</summary>
    public Dictionary<uint, string>? SurvivorsOfferNames;
    /// <summary>The unique items the window shows by inventory item id (the pick equips that very item).</summary>
    public Dictionary<uint, Item>? SurvivorsOfferItems;
    /// <summary>The window's inventory items as dealt, so a closed window can be sent again.</summary>
    public List<InventoryItem>? SurvivorsOfferList;
    /// <summary>The reward inventory's revision for the level-up window (S→C 0xbf).</summary>
    public uint SurvivorsOfferRevision;
    /// <summary>This connection's clock was stopped for the level-up window and must restart after the pick.</summary>
    public bool SurvivorsPausedByWindow;
    /// <summary>The run's skill catalogue, built once at its start: the window's items, the Actor skills and their ids
    /// all index it, so it must not be rebuilt with a different order mid-run.</summary>
    public IReadOnlyList<SurvivorsSkill>? SurvivorsCatalog;
    /// <summary>The supports the run's level-ups can offer, with their GrantedEffects rows, built at its start.</summary>
    public IReadOnlyList<SurvivorsSupportItem>? SurvivorsSupports;
    /// <summary>The equipped item ids a run granted: their requirements are waived while the run lasts.</summary>
    public readonly HashSet<uint> SurvivorsItemIds = [];
    /// <summary>The herald aura buff instances this run put on the player (S→C 0x1c1), removed when it ends.</summary>
    public readonly List<ushort> SurvivorsHeraldBuffs = [];
    /// <summary>The catalogue indices of the heralds already published as auras.</summary>
    public readonly HashSet<int> SurvivorsHeraldSkills = [];
    /// <summary>The experience orbs on the ground of the current area, oldest first.</summary>
    public readonly List<SurvivorsExperienceOrb> SurvivorsOrbs = [];
    /// <summary>The death ground zones on the ground of the current area (a DOT the player must leave).</summary>
    public readonly List<SurvivorsGroundZone> SurvivorsGroundZones = [];
    /// <summary>When each dead monster was first seen dead, so its body can be removed after a short linger: corpses
    /// otherwise pile up on the server and client and make a long run glitch.</summary>
    public readonly Dictionary<uint, double> SurvivorsCorpses = [];
}

/// <summary>One experience orb: its client object id, where it lies, and the raw experience it pays when collected.</summary>
sealed record SurvivorsExperienceOrb(uint Id, int X, int Y, int Experience);

/// <summary>One death ground zone: its client object id, centre, reach, deadline (connection clock seconds) and the
/// life it drains per second while the player stands inside it.</summary>
sealed record SurvivorsGroundZone(uint Id, int X, int Y, int Radius, double ExpiresAt, int DamagePerSecond);
