using GameServer.Core.Protocol.Instance;

/// <summary>
/// Framework-level plumbing for mutating a spawned monster's networked <c>StateMachine</c> component
/// (typeId 209 / <c>ClientInternalStateMachine</c> typeId 125; S→C <c>0x2bc</c> slot 20 reader <c>0x141e42a90</c>).
/// <para>
/// Contains zero game rules: validates the component index and shared-state names, skips no-op updates without
/// allocating, keeps <see cref="SpawnedMonster.States"/> and <c>MonsterOrigins</c> (and <c>MinionOrigins</c>) in sync
/// so late joiners receive the updated state in <c>0x2bb</c>, and encodes the full shared-state snapshot wire packet.
/// </para>
/// </summary>
public static class MonsterStateMachine
{
    /// <summary>Precomputed index of the <c>StateMachine</c> component on <paramref name="variety"/>, or −1.</summary>
    public static int ComponentIndex(MonsterCatalog.Variety? variety) => variety?.StateMachineComponentIndex ?? -1;

    /// <summary>
    /// Updates a single named shared state on <paramref name="monster"/> and builds the S→C <c>0x2bc</c> update packet.
    /// Returns <c>false</c> with <c>packet = null</c> (zero allocations) if the variety has no <c>StateMachine</c>,
    /// the state name does not exist, or the state already holds <paramref name="value"/>.
    /// </summary>
    public static bool TrySet(Player player, uint id, SpawnedMonster monster, string name, int value, out byte[]? packet)
    {
        packet = null;
        if (monster.Variety is not { } variety || monster.States is not { } current) return false;
        int componentIndex = variety.StateMachineComponentIndex;
        if (componentIndex < 0) return false;
        int stateIndex = current.IndexOf(name);
        if ((uint)stateIndex >= (uint)current.SharedStates.Count) return false;
        if (current.SharedStates[stateIndex] == value
            && (current.Overridden.Contains(stateIndex) || !IsClientWriter(variety, name)))
            return false;

        var updated = current.Set(name, value);
        Commit(player, id, monster, updated);
        packet = InstanceEntities.StateMachineUpdate(id, (byte)componentIndex, updated);
        return true;
    }

    /// <summary>
    /// Atomically updates multiple named shared states on <paramref name="monster"/> in one S→C <c>0x2bc</c> snapshot.
    /// Returns <c>false</c> with <c>packet = null</c> (zero allocations) when no named state changes.
    /// </summary>
    public static bool TrySet(Player player, uint id, SpawnedMonster monster, ReadOnlySpan<(string Name, int Value)> changes, out byte[]? packet)
    {
        packet = null;
        if (changes.IsEmpty || monster.Variety is not { } variety || monster.States is not { } current) return false;
        int componentIndex = variety.StateMachineComponentIndex;
        if (componentIndex < 0) return false;

        StateMachineData updated = current;
        bool changed = false;
        for (int i = 0; i < changes.Length; i++)
        {
            var (name, value) = changes[i];
            int stateIndex = updated.IndexOf(name);
            if ((uint)stateIndex >= (uint)updated.SharedStates.Count) continue;
            if (updated.SharedStates[stateIndex] == value
                && (updated.Overridden.Contains(stateIndex) || !IsClientWriter(variety, name)))
                continue;
            updated = updated.Set(name, value);
            changed = true;
        }
        if (!changed) return false;

        Commit(player, id, monster, updated);
        packet = InstanceEntities.StateMachineUpdate(id, (byte)componentIndex, updated);
        return true;
    }

    static bool IsClientWriter(MonsterCatalog.Variety variety, string name) =>
        variety.StateMeta.TryGetValue(name, out var meta) && meta.HasClientWriter;

    static void Commit(Player player, uint id, SpawnedMonster monster, StateMachineData updated)
    {
        monster.States = updated;
        if (player.Instance?.World.MonsterOrigins is { } origins && origins.TryGetValue(id, out var origin))
            origins[id] = origin with { Object = origin.Object with { StateMachine = updated } };
        if (player.MinionOrigins.TryGetValue(id, out var minionOrigin))
            player.MinionOrigins[id] = minionOrigin with { Object = minionOrigin.Object with { StateMachine = updated } };
    }
}
