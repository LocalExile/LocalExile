using GameServer.Core.Protocol.Instance;

/// <summary>
/// Per-instance coordinator for S→C <c>0x00df</c> (<see cref="InstanceEnvironments"/>) area environment overrides.
/// Because <c>0x00df</c> replaces the client's entire override list on every packet, independent league encounters
/// (<c>"delirium"</c>, <c>"breach"</c>, <c>"ritual"</c>, <c>"legion"</c>) register and clear their own keyed layers
/// here so one encounter finishing never wipes another active encounter's environment (e.g. Breach or Ritual ending
/// never clears Delirium's <c>LeagueAfflictionEncounter</c> fog).
/// </summary>
public static class AreaEnvironments
{
    sealed class EnvironmentStack
    {
        public readonly object Gate = new();
        public readonly List<(string Key, (ushort Row, float Seconds)[] Entries)> Layers = [];
    }

    static readonly RuleTable<InstanceState, EnvironmentStack> Stacks = new();

    /// <summary>Updates <paramref name="key"/> in the instance's environment stack without broadcasting (used on
    /// entry/hot-reload before <see cref="Sync"/>).</summary>
    public static void EnsureState(Player player, string key, params (ushort Row, float Seconds)[] entries)
    {
        if (player.Instance is not { } instance) return;
        var stack = Stacks.GetOrCreateValue(instance);
        lock (stack.Gate) SetLocked(stack, key, entries);
    }

    /// <summary>Sets a single environment override for <paramref name="key"/> and broadcasts the combined <c>0x00df</c>
    /// packet to all active players in the instance.</summary>
    public static Task Set(Player player, string key, ushort environmentRow, float transitionSeconds, string? log = null) =>
        Update(player, key, [(environmentRow, transitionSeconds)], null, log);

    /// <summary>Sets multiple environment overrides for <paramref name="key"/> and broadcasts the combined
    /// <c>0x00df</c> packet to all active players in the instance.</summary>
    public static Task SetMany(Player player, string key, IReadOnlyList<(ushort Row, float Seconds)> entries, string? log = null) =>
        Update(player, key, entries, null, log);

    /// <summary>Updates <paramref name="key"/>'s persistent active layers and broadcasts all active layers plus any
    /// one-shot <paramref name="fadeOut"/> entries (whose rows are not active in another layer).</summary>
    public static Task Update(Player player, string key, IReadOnlyList<(ushort Row, float Seconds)> active,
        IReadOnlyList<(ushort Row, float Seconds)>? fadeOut = null, string? log = null)
    {
        if (player.Instance is not { } instance) return Task.CompletedTask;
        byte[] packet;
        var stack = Stacks.GetOrCreateValue(instance);
        lock (stack.Gate)
        {
            SetLocked(stack, key, active);
            packet = BuildPacketLocked(stack, fadeOut);
        }
        return Broadcast(player, packet, log ?? $"0x00df area environments ({key})");
    }

    /// <summary>Removes <paramref name="key"/> from the instance's active environment stack and broadcasts the remaining
    /// active layers (optionally including a one-shot <paramref name="fadeOutSeconds"/> transition for the removed rows).</summary>
    public static Task Clear(Player player, string key, float? fadeOutSeconds = null, string? log = null)
    {
        if (player.Instance is not { } instance) return Task.CompletedTask;
        byte[]? packet = null;
        var stack = Stacks.GetOrCreateValue(instance);
        lock (stack.Gate)
        {
            (ushort Row, float Seconds)[]? removed = null;
            for (int i = 0; i < stack.Layers.Count; i++)
            {
                if (!string.Equals(stack.Layers[i].Key, key, StringComparison.Ordinal)) continue;
                removed = stack.Layers[i].Entries;
                stack.Layers.RemoveAt(i);
                break;
            }
            if (removed is null && fadeOutSeconds is not null) return Task.CompletedTask;
            (ushort Row, float Seconds)[]? fade = fadeOutSeconds is float fadeSecs && removed is { Length: > 0 }
                ? removed.Select(e => (e.Row, fadeSecs)).ToArray()
                : null;
            packet = BuildPacketLocked(stack, fade);
        }
        return Broadcast(player, packet, log ?? $"0x00df clear area environment ({key})");
    }

    /// <summary>Sends the combined active environment stack (when non-empty) to a single entering player.</summary>
    public static Task Sync(Player player, string? log = null)
    {
        if (player.Instance is not { } instance) return Task.CompletedTask;
        byte[]? packet = null;
        if (Stacks.TryGetValue(instance, out var stack))
        {
            lock (stack.Gate)
            {
                if (HasActiveLocked(stack))
                    packet = BuildPacketLocked(stack, null);
            }
        }
        return packet is null ? Task.CompletedTask : Program.Send(player, packet, log ?? "0x00df sync area environments");
    }

    /// <summary>Builds the combined <c>0x00df</c> packet for the instance's currently active layers.</summary>
    public static byte[] BuildPacket(Player player)
    {
        if (player.Instance is not { } instance || !Stacks.TryGetValue(instance, out var stack))
            return InstanceEnvironments.Data();
        lock (stack.Gate) return BuildPacketLocked(stack, null);
    }

    static bool HasActiveLocked(EnvironmentStack stack)
    {
        for (int i = 0; i < stack.Layers.Count; i++)
            if (stack.Layers[i].Entries.Length > 0) return true;
        return false;
    }

    static void SetLocked(EnvironmentStack stack, string key, IReadOnlyList<(ushort Row, float Seconds)> entries)
    {
        var copy = entries.Where(e => e.Row != 0).ToArray();
        for (int i = 0; i < stack.Layers.Count; i++)
        {
            if (!string.Equals(stack.Layers[i].Key, key, StringComparison.Ordinal)) continue;
            if (copy.Length == 0) stack.Layers.RemoveAt(i);
            else stack.Layers[i] = (key, copy);
            return;
        }
        if (copy.Length > 0) stack.Layers.Add((key, copy));
    }

    static byte[] BuildPacketLocked(EnvironmentStack stack, IReadOnlyList<(ushort Row, float Seconds)>? fadeOut)
    {
        var combined = new List<(ushort Row, double Seconds)>();
        var activeRows = new HashSet<ushort>();
        for (int i = 0; i < stack.Layers.Count; i++)
        {
            foreach (var (row, seconds) in stack.Layers[i].Entries)
            {
                if (row == 0 || !activeRows.Add(row)) continue;
                combined.Add((row, seconds));
            }
        }
        if (fadeOut is { Count: > 0 })
        {
            foreach (var (row, seconds) in fadeOut)
            {
                if (row == 0 || activeRows.Contains(row)) continue;
                activeRows.Add(row);
                combined.Add((row, seconds));
            }
        }
        if (combined.Count > byte.MaxValue) combined.RemoveRange(byte.MaxValue, combined.Count - byte.MaxValue);
        return InstanceEnvironments.Data([.. combined]);
    }

    static async Task Broadcast(Player player, byte[] packet, string log)
    {
        var members = Clients(player);
        for (int i = 0; i < members.Count; i++)
            await Program.Send(members[i], (byte[])packet.Clone(), i == 0 ? log : null);
    }

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static List<Player> Clients(Player player)
    {
        var instance = player.Instance;
        var members = new List<Player>();
        if (Current(player)) members.Add(player);
        if (instance is not null)
        {
            foreach (var p in instance.PlayersExcept(player.Id))
                if (Current(p) && ReferenceEquals(p.Instance, instance) && !members.Contains(p))
                    members.Add(p);
            foreach (var p in Program.InGame.Keys)
                if (p.HasClient && Current(p) && ReferenceEquals(p.Instance, instance) && !members.Contains(p))
                    members.Add(p);
        }
        return members;
    }
}
