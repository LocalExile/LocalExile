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

    /// <summary>The object_type_shared_states rows by their type's row (<see cref="StateMachine"/>).</summary>
    public static ILookup<long, ClientRow> SharedStates() => ClientTables.TableOrEmpty("object_type_shared_states").By("type");

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
