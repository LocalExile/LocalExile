using GameServer.Modding;

/// <summary>A ground-item snapshot. Id is local to this area; Metadata is the stable BaseItemTypes id.</summary>
public sealed record GroundItem(uint Id, string Metadata, string Name, string Class, int StackSize, int MaxStack,
    bool Gold, bool PlayerDropped, int X, int Y, bool Quest = false);

/// <summary>Game-owned operations, not an automatic-pickup policy. Requests refuse retired owners, paused/dead/leaving
/// players and hidden items; pickup requires a nearby player or a worn pet the caller controls.</summary>
public interface IPlayerLoot
{
    /// <summary>Visible items within radius cells of the player (at most 80).</summary>
    IReadOnlyList<GroundItem> Nearby(double radius);
    /// <summary>Takes a visible gold/stackable item through the normal saved pickup path. With petId the caller must
    /// control that worn pet within two cells of the item; otherwise the player must be within two cells. Hand-dropped
    /// and quest items are refused, as are items farther than 80 cells from the player. A full inventory leaves it down.</summary>
    Task<bool> PickUp(uint id, IModuleLifetime owner, uint? petId = null);
}
