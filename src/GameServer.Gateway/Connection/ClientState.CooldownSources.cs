sealed partial class ClientState
{
    // A cooldown's debt is shared by copies of its effect; support inputs come from its last admitted source.
    public readonly Dictionary<int, ushort> CooldownSources = [];
    public readonly Dictionary<int, int> CooldownGroups = [];
}
