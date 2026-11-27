using GameServer.Core.Protocol.Instance;

/// <summary>
/// Core gameplay policy for monster <c>StateMachine</c> shared states (PR #1618 S5):
/// computes default visibility overrides for varieties whose <c>.ot</c> calls unconditional <c>DisableRendering()</c>
/// at their default state value, and broadcasts runtime state changes via <see cref="MonsterStateMachine"/>.
/// </summary>
public static class MonsterStates
{
    /// <summary>
    /// Applies data-driven visibility defaults from <see cref="MonsterCatalog.Variety.StateMeta"/> for varieties whose
    /// <c>create_state_&lt;name&gt;_&lt;default&gt;</c> (or <c>on_or_create_state_</c>) calls unconditional
    /// <c>DisableRendering()</c> and has a known <c>EnableRendering()</c> state value (e.g. <c>visible=0→1</c>,
    /// <c>render=1→2</c>, <c>norendering=1→0</c>).
    /// <para>
    /// Returns <paramref name="states"/> unchanged (zero allocations) when no state disables rendering at default.
    /// Must only be called as a fallback after bespoke encounter/boss <c>StartStates</c> and never for scripted or
    /// custom-shaped spawns (so hidden-on-spawn encounters retain full control).
    /// </para>
    /// </summary>
    public static StateMachineData? DefaultVisibleStates(MonsterCatalog.Variety variety, StateMachineData? states)
    {
        if (states is null || variety.StateMeta.Count == 0) return states;
        StateMachineData current = states;
        foreach (var (name, meta) in variety.StateMeta)
        {
            if (meta.DisablesRenderingAtDefault && meta.EnableValue is int enableValue)
                current = current.Set(name, enableValue);
        }
        return current;
    }
}

public static partial class Game
{
    /// <summary>
    /// Updates a single shared state on a spawned monster and broadcasts the resulting S→C <c>0x2bc</c> snapshot to
    /// players in the area. Zero-allocation no-op when the state already holds <paramref name="value"/> or the variety
    /// has no matching shared state.
    /// </summary>
    public static Task SetMonsterStateAsync(Player player, uint id, SpawnedMonster monster, string name, int value, string? log = null)
    {
        if (!MonsterStateMachine.TrySet(player, id, monster, name, value, out var packet) || packet is null)
            return Task.CompletedTask;
        return SendWorldRecord(player, id, packet, log ?? $"0x02bc monster 0x{id:x} state {name}={value}");
    }

    /// <summary>
    /// Atomically updates multiple shared states on a spawned monster in one S→C <c>0x2bc</c> snapshot and broadcasts
    /// it to players in the area. Zero-allocation no-op when no named state changes.
    /// </summary>
    public static Task SetMonsterStateAsync(Player player, uint id, SpawnedMonster monster,
        ReadOnlySpan<(string Name, int Value)> changes, string? log = null)
    {
        if (!MonsterStateMachine.TrySet(player, id, monster, changes, out var packet) || packet is null)
            return Task.CompletedTask;
        return SendWorldRecord(player, id, packet, log ?? $"0x02bc monster 0x{id:x} state update");
    }
}
