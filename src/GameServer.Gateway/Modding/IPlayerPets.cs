using GameServer.Modding;

/// <summary>A worn cosmetic pet's current position, in cells.</summary>
public sealed record PetInfo(uint Id, int X, int Y);

/// <summary>Optional cosmetic-pet controls. These do not choose or pick up items.</summary>
public interface IPlayerPets
{
    /// <summary>Snapshots of the player's worn pets.</summary>
    IReadOnlyList<PetInfo> Nearby { get; }
    /// <summary>Orders a pet within 80 cells of its player. Control expires after one game second. Repeating the
    /// same target renews control without restarting the walk; another active owner's control is not taken over.</summary>
    Task<bool> MovePet(uint id, int x, int y, IModuleLifetime owner);
    /// <summary>Releases this owner's control and returns the pet to following.</summary>
    void ReleasePet(uint id, IModuleLifetime owner);
}
