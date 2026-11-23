using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The Life component of the area's entities (S→C 0x1b1: life, mana and energy shield pools with their reserved amounts).
/// A monster or player whose pools may have changed is marked; the frame's end writes each pool that differs from what
/// the area wrote the frame before, once, in the frame's last phase (after the frame's hits and actions; the client fires
/// the death slot when life drops below 1).
/// </summary>
public sealed class AreaLife(GameArea area)
{
    public const byte LifePool = 0, ManaPool = 1, EnergyShieldPool = 2;

    /// <summary>A player's reserved mana (the game's reservation rules); 0 without them.</summary>
    public static Func<Player, uint>? ReservedMana;

    readonly ConcurrentDictionary<uint, byte> monsters = new();
    readonly ConcurrentDictionary<Player, bool> players = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<(uint Id, byte Pool), (uint Current, uint Reserved)> written = [];
    readonly ConcurrentDictionary<(uint Id, byte Pool), (uint Current, uint Reserved)> set = new();

    internal void Changed(uint monster) => monsters.TryAdd(monster, 0);

    public void Changed(Player player) => players.TryAdd(player, false);

    /// <summary>The client changed its own pools ahead of the server (a cost it paid, Eldritch Battery): the frame's end
    /// writes them even when the server's values have not changed.</summary>
    public void Resend(Player player) => players[player] = true;

    /// <summary>A pool the area keeps outside its monsters and players (a totem's life, a golem's energy shield).</summary>
    public void Set(uint id, byte pool, uint current, uint reserved = 0) => set[(id, pool)] = (current, reserved);

    /// <summary>An entity left the area: a later object with its id starts from nothing.</summary>
    public void Removed(uint id)
    {
        area.AssertOwner();
        foreach (byte pool in (byte[])[LifePool, ManaPool, EnergyShieldPool]) { written.Remove((id, pool)); set.TryRemove((id, pool), out _); }
        monsters.TryRemove(id, out _);
    }

    public void Publish()
    {
        area.AssertOwner();
        foreach (var id in monsters.Keys)
        {
            monsters.TryRemove(id, out _);
            if (area.Monsters.TryGetValue(id, out var m)) Write(id, LifePool, m.Life, 0, false);
        }
        foreach (var key in set.Keys)
            if (set.TryRemove(key, out var value)) Write(key.Id, key.Pool, value.Current, value.Reserved, false);
        foreach (var player in players.Keys)
        {
            players.TryRemove(player, out bool force);
            if (player.Closed || !ReferenceEquals(player.Frames.Area, area) || !player.Connection.PlayerSent) continue;
            Write(player.Id, LifePool, player.PlayerLife, player.PlayerReservedLife, force);
            Write(player.Id, ManaPool, player.PlayerMana, ReservedMana?.Invoke(player) ?? 0, force);
            if (player.EnergyShield.Maximum > 0 || written.ContainsKey((player.Id, EnergyShieldPool)))
                Write(player.Id, EnergyShieldPool, player.EnergyShield.Current, 0, force);
        }
    }

    void Write(uint id, byte pool, uint current, uint reserved, bool force)
    {
        if (!force && written.TryGetValue((id, pool), out var was) && was == (current, reserved)) return;
        written[(id, pool)] = (current, reserved);
        area.Delta.Add(id, InstanceProtocol.LifeUpdate(id, current, reserved, pool, orderKey: uint.MaxValue));
    }
}
