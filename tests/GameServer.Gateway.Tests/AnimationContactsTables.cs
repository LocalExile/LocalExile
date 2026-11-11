using System.Text.Json;
using GameServer.Core;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Rigs written as data/animation-contacts.json has them, turned into the animation_* tables the Gateway reads (the
/// rows scripts/dev/generated_tables.py animation_contacts makes), so a test can state its rigs the readable way.
/// </summary>
static class AnimationContactsTables
{
    public static AnimationContacts From(string json)
    {
        var rigs = new List<Dictionary<string, object?>>();
        var clips = new List<Dictionary<string, object?>>();
        var frames = new List<Dictionary<string, object?>>();
        var attacks = new List<Dictionary<string, object?>>();
        using var doc = JsonDocument.Parse(json);
        foreach (var rig in doc.RootElement.EnumerateObject())
        {
            int r = rigs.Count;
            var value = rig.Value;
            rigs.Add(new() { ["ao"] = rig.Name, ["amd"] = Text(value, "amd"), ["act"] = Text(value, "act") });
            Dictionary<string, JsonElement> Map(string key) => value.TryGetProperty(key, out var m) && m.ValueKind == JsonValueKind.Object
                ? m.EnumerateObject().ToDictionary(p => p.Name, p => p.Value) : [];
            var (animations, positions, cancels, contacts, indices) =
                (Map("animations"), Map("positions"), Map("cancels"), Map("contacts"), Map("indices"));
            foreach (string name in animations.Keys.Concat(positions.Keys).Concat(cancels.Keys).Concat(contacts.Keys)
                         .Concat(indices.Keys).Distinct())
            {
                int clip = clips.Count;
                bool animated = animations.TryGetValue(name, out var a);
                clips.Add(new()
                {
                    ["rig"] = r, ["name"] = name,
                    ["length"] = animated ? a[0].GetInt64() : null, ["contact"] = animated ? a[1].GetInt64() : null,
                    ["position"] = positions.TryGetValue(name, out var p) ? List(p) : null,
                    ["cancel_listed"] = cancels.ContainsKey(name),
                    ["cancel"] = cancels.TryGetValue(name, out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : null,
                    ["rig_index"] = indices.TryGetValue(name, out var i) ? i.GetInt64() : null,
                });
                if (contacts.TryGetValue(name, out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var (frame, ord) in list.EnumerateArray().Select((f, k) => (f, k)))
                        frames.Add(new() { ["clip"] = clip, ["ord"] = ord, ["ms"] = frame[0].GetInt64(), ["position"] = List(frame[1]) });
            }
            foreach (var (stance, entries) in Map("defaultAttack"))
                foreach (var (entry, ord) in entries.EnumerateArray().Select((e, k) => (e, k)))
                {
                    var row = new Dictionary<string, object?> { ["rig"] = r, ["stance"] = stance, ["ord"] = ord };
                    foreach (var field in entry.EnumerateObject())
                        row[field.Name] = field.Value.ValueKind switch
                        {
                            JsonValueKind.String => field.Value.GetString(),
                            JsonValueKind.Number when field.Name == "budget" => field.Value.GetDouble(),
                            JsonValueKind.Number => field.Value.TryGetInt64(out long n) ? n : field.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Array => List(field.Value),
                            _ => null,
                        };
                    attacks.Add(row);
                }
        }
        var tables = new[]
        {
            ClientTable.FromRows("animation_rigs", rigs), ClientTable.FromRows("animation_clips", clips),
            ClientTable.FromRows("animation_clip_contacts", frames), ClientTable.FromRows("animation_default_attacks", attacks),
        }.ToDictionary(t => t.Name);
        return new AnimationContacts(name => tables.GetValueOrDefault(name));
    }

    static string? Text(JsonElement value, string key) =>
        value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double[]? List(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray().Select(v => v.GetDouble())] : null;
}
