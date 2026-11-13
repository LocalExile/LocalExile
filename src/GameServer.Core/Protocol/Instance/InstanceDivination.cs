namespace GameServer.Core.Protocol.Instance;

/// <summary>The native NPC divination window.</summary>
public static class InstanceDivination
{
    public const byte InventoryRow = 27, Ui = 0x0c;
    public const ushort TurnInId = 0x19a;
    public const int TurnInLength = 2;

    public static byte[] Open(uint visual = 0) => InstanceWaypoints.OpenUi(Ui, visual);
}
