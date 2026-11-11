public sealed partial class ClientState
{
    /// <summary>Winter Orb's orb, its stages and its firing loop (Modules Core, WinterOrb.cs). It lives past the channel
    /// that made it, so it is retired with the rest of the connection's combat work: an object slot, so a module reload's
    /// new types start over instead of being handed a retired runtime.</summary>
    public object? WinterOrbState;
}
