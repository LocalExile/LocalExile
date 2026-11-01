/// <summary>
/// The client's world units (Agents/docs/protocol/11-skills.md §10). Positions in the grid are cells; the rig's animation
/// events, model sockets, projectile speeds and heights are in world units: a tile of 23 cells is 250 units, and a
/// Positioned fixed-height marker or terrain height step is 7.8125 units. The same unit scales models, so a rig event's
/// vertical offset converts to marker steps directly.
/// </summary>
static class WorldUnits
{
    public const double PerCell = 250.0 / 23;
    public const double PerHeightStep = 7.8125;
}
