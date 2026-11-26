using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Reads of client.db's object_types rows (data/object-types.json from scripts/re/ot_components.py, one row per .ot
/// type; a nested object is flattened into <c>&lt;field&gt;_&lt;key&gt;</c> columns: positioned_blocking, life_corpse_usable).
/// </summary>
public static class ObjectTypes
{
    /// <summary>The components the client reads for the type, in order (the first <c>read</c> of its list).</summary>
    public static IReadOnlyList<string> Components(ClientRow type) =>
        [.. type.Texts("components").Take(type.IntOrNull("read") ?? int.MaxValue)];

    /// <summary>A flag the generator writes as a boolean or as the .ot's text ("true"/"false"), or null.</summary>
    public static bool? Flag(ClientRow type, string column) => type.IsNull(column) ? null : type.Get(column) switch
    {
        bool b => b,
        long l => l != 0,
        string s when bool.TryParse(s, out bool b) => b,
        _ => null,
    };

    /// <summary>Bit flags extracted from an object type's common/client .ot StateMachine and GEAL blocks
    /// (client.db <c>object_type_state_meta.flags</c>, object-types v14).</summary>
    [Flags]
    public enum StateFlagCapabilities : byte
    {
        None = 0,
        /// <summary>Type defines a common/client StateMachine handler (<c>on_state_*</c>, <c>create_state_*</c>, etc.).</summary>
        ClientListener = 1,
        /// <summary>Type mutates the state in common/client GEAL (<c>SetStateTo</c>, <c>SetStateFlag</c>, etc.). Never set wire lock bit 0x01.</summary>
        ClientWriter = 2,
        /// <summary>Type calls unconditional <c>DisableRendering()</c> in <c>create_state_&lt;name&gt;_&lt;default&gt;</c> or <c>on_or_create_state_&lt;name&gt;_&lt;default&gt;</c>.</summary>
        DisablesRenderingAtDefault = 4,
    }

    /// <summary>Per-shared-state metadata for an object type (client.db <c>object_type_state_meta</c>).</summary>
    public readonly record struct StateFlagMeta(StateFlagCapabilities Flags, int? EnableValue)
    {
        public bool HasClientListener => (Flags & StateFlagCapabilities.ClientListener) != 0;
        public bool HasClientWriter => (Flags & StateFlagCapabilities.ClientWriter) != 0;
        public bool DisablesRenderingAtDefault => (Flags & StateFlagCapabilities.DisablesRenderingAtDefault) != 0;
    }

    /// <summary>The object_type_shared_states rows by their type's row (<see cref="StateMachine"/>).</summary>
    public static ILookup<long, ClientRow> SharedStates() => ClientTables.TableOrEmpty("object_type_shared_states").By("type");

    /// <summary>The object_type_state_meta rows by their type's row (<see cref="StateMetaFor"/>).</summary>
    public static ILookup<long, ClientRow> StateMeta() => ClientTables.TableOrEmpty("object_type_state_meta").By("type");

    /// <summary>The type's per-shared-state metadata, or null when the type has no rows (or an older client.db lacks the table).</summary>
    public static IReadOnlyDictionary<string, StateFlagMeta>? StateMetaFor(ClientRow type, ILookup<long, ClientRow> stateMeta)
    {
        Dictionary<string, StateFlagMeta>? map = null;
        foreach (var row in stateMeta[type.Index])
        {
            if (row.Text("name") is not { Length: > 0 } name) continue;
            var flags = (StateFlagCapabilities)(row.IntOrNull("flags") ?? 0);
            int? enableValue = row.IntOrNull("enable_value");
            (map ??= [])[name] = new StateFlagMeta(flags, enableValue);
        }
        return map;
    }

    /// <summary>The type's StateMachine defaults (its define_shared_state values, names and shared timer count), or null
    /// for data generated before them (no shared_timers).</summary>
    public static StateMachineData? StateMachine(ClientRow type, ILookup<long, ClientRow> sharedStates)
    {
        if (type.IsNull("shared_timers")) return null;
        var states = sharedStates[type.Index].ToList();
        return new([.. states.Select(s => s.Int("value"))], type.Texts("shared_timers").Length, [.. states.Select(s => s.Text("name") ?? "")]);
    }

    /// <summary>The types by their path (Metadata/...), with each row.</summary>
    public static IEnumerable<(string Type, ClientRow Row)> All() =>
        ClientTables.TableOrEmpty("object_types").Select(r => (r.Text("type")!, r));
}
