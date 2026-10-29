using GameServer.Core.Protocol.Instance;

/// <summary>Per-entry protection. Never saved; the deadline uses the connection's game clock.</summary>
sealed class GracePeriod
{
    public const ushort BuffRow = 6; // BuffDefinitions: grace_period; BuffVisuals 8 (invulnerability.dds).
    public const int Seconds = 60;
    readonly Lock _lock = new();
    ushort? _buff;
    bool _protecting;
    double _until;
    (int X, int Y) _origin;

    public bool Start(double now, (int X, int Y) origin, ushort buff, bool town, bool hideout)
    {
        lock (_lock)
        {
            _buff = town || hideout ? null : buff;
            _protecting = _buff is not null;
            _origin = origin;
            _until = now + Seconds;
            return _buff is not null;
        }
    }

    public bool Protects(double now, (int X, int Y) position)
    {
        lock (_lock)
        {
            _protecting &= now < _until && position == _origin;
            return _protecting;
        }
    }

    // Expired/moved buffs still need one remove packet, even though protection already stopped.
    public ushort? End(double now, (int X, int Y) position, bool action = false)
    {
        lock (_lock)
        {
            if (!action && _protecting && now < _until && position == _origin) return null;
            var buff = _buff;
            _buff = null;
            _protecting = false;
            return buff;
        }
    }

    public static byte[] BeginPacket(uint player, ushort buff) =>
        InstanceEntities.AddBuff(player, buff, BuffRow, player, u68: BitConverter.SingleToUInt32Bits(Seconds));
}
