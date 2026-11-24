using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Heist;

/// <summary>
/// Spawns the nine <c>Metadata/NPC/League/Heist/PlanningNPC</c> entities around the Rogue Harbour planning table
/// (<c>ops_1.arm</c>, placed by <c>hub.dgr</c> into <c>Level.RoomObjects</c>), matching each rogue's
/// <c>HeistNPCs.AOFile</c>. Spawned ids persist in <see cref="InstanceState.ContentState"/> so re-entry, party join,
/// and module hot-reload do not duplicate entities.
/// </summary>
public sealed class HeistPlanningNpcs
{
    const string StateKey = "Heist.PlanningNpcs";
    static readonly string[] PlanningComponents = ["Positioned", "Animated"];
    static readonly JsonSerializerOptions Json = new() { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

    sealed class HubState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public object? ShownArea;
        public readonly List<uint> EntityIds = [];
    }

    readonly ConditionalWeakTable<InstanceState, HubState> _hubs = new();

    public void Clear() => _hubs.Clear();

    public async Task OnPlayerEntered(IModuleContext module, Player player)
    {
        if (!HeistTables.IsRogueHarbour(player.Layout.Id)) return;
        var hub = _hubs.GetOrCreateValue(player.Instance);
        await hub.Gate.WaitAsync();
        try
        {
            if (ReferenceEquals(hub.ShownArea, player.Area) && hub.EntityIds.Count > 0 && hub.EntityIds.All(player.Area.Objects.Has))
                return;

            // Recover ids persisted before a module hot-reload so re-entry does not duplicate existing area objects.
            if (player.Instance.ContentState.TryGetValue(StateKey, out var raw) && !string.IsNullOrEmpty(raw))
            {
                try
                {
                    var persisted = JsonSerializer.Deserialize<uint[]>(raw, Json);
                    if (persisted is { Length: > 0 } && persisted.All(player.Area.Objects.Has))
                    {
                        hub.EntityIds.Clear();
                        hub.EntityIds.AddRange(persisted);
                        hub.ShownArea = player.Area;
                        return;
                    }
                }
                catch (JsonException) { }
            }

            hub.EntityIds.Clear();

            var fromRoom = (player.Layout.Level?.RoomObjects ?? [])
                .Where(o => o.Type.Equals(HeistTables.PlanningNpcPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (fromRoom.Length == 0) return;

            var validModels = HeistTables.Rogues
                .Select(r => r.AoFile)
                .Where(a => !string.IsNullOrEmpty(a))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            uint typeHash = InstanceEntities.ObjectTypeHash(HeistTables.PlanningNpcPath);
            foreach (var p in fromRoom)
            {
                string model = p.Model ?? "";
                if (string.IsNullOrEmpty(model)) continue;
                if (validModels.Count > 0 && !validModels.Contains(model)) continue;
                uint id = WorldOf(player).NewObjectId();
                var obj = new QuestObject(p.X, p.Y, PlanningComponents, Rotation: p.Rotation, Model: model);
                await SendWorldCreate(player, id,
                    () => InstanceEntities.CreateObject(id, typeHash, obj.Blob()),
                    $"0x02bb PlanningNPC 0x{id:x} ({model.Split('/')[^1]}) at ({p.X}, {p.Y})",
                    wake: true, awaitBirth: false);
                hub.EntityIds.Add(id);
            }
            hub.ShownArea = player.Area;
            if (hub.EntityIds.Count > 0)
            {
                player.Instance.ContentState[StateKey] = JsonSerializer.Serialize(hub.EntityIds, Json);
                module.Log($"HeistHub: spawned {hub.EntityIds.Count} PlanningNPC entity(ies) around planning table");
            }
        }
        finally { hub.Gate.Release(); }
    }
}
