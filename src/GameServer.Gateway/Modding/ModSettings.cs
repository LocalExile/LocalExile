using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// A mod's or module's settings (Agents/docs/dev/modding.md, "Settings"): <c>settings.json</c> beside its manifest declares each
/// one with its default, and the user's values (only what they changed) are in a file of their own under <c>data/</c>. The host
/// merges the two and checks every value against its declaration; a value that does not fit is logged and its default used.
/// </summary>
public static class ModSettings
{
    /// <summary>The declarations' file, beside <c>mod.json</c> or <c>module.json</c>.</summary>
    public const string FileName = "settings.json";

    static readonly JsonDocumentOptions Read = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly JsonSerializerOptions Write = new() { WriteIndented = true };

    static readonly string[] Types = ["bool", "integer", "number", "range", "choice", "choices", "text", "list", "map", "group"];
    static readonly string[] Formats = ["percent", "duration", "multiplier"];
    static readonly string[] Applies = ["now", "nextArea", "restart"];
    // What a declaration may hold, beside its type; what is only for an editor (label, unit, ...) is checked for its shape.
    static readonly string[] Attributes = ["type", "default", "label", "description", "min", "max", "step", "unit", "format", "options",
        "source", "minLength", "maxLength", "pattern", "minItems", "maxItems", "of", "item", "keys", "value", "settings", "when",
        "applies", "advanced"];

    /// <summary>One setting as <c>settings.json</c> declares it.</summary>
    public sealed record Declaration(string Type, JsonNode? Default, double? Min, double? Max, double? Step, JsonNode[]? Options,
        string? Source, int? MinLength, int? MaxLength, Regex? Pattern, int? MinItems, int? MaxItems, bool Integers, Declaration? Item,
        Declaration? Value, IReadOnlyDictionary<string, Declaration>? Settings);

    /// <summary>A whole <c>settings.json</c>: its settings, and the names it renamed (old to new).</summary>
    public sealed record Schema(IReadOnlyDictionary<string, Declaration> Settings, IReadOnlyDictionary<string, string> Renamed);

    /// <summary>The game's values of a kind (<c>area</c>, <c>item</c>, ...) for a declaration's <c>source</c>; null when unknown.</summary>
    public delegate IEnumerable<string>? Values(string kind);

    /// <summary>Reads a <c>settings.json</c>. Throws <see cref="FormatException"/> for a declaration that is wrong (its default
    /// included), so the mod's author sees it at the load.</summary>
    public static Schema Parse(string json, Values? values = null)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: Read); }
        catch (JsonException e) { throw new FormatException(e.Message); }
        if (root is not JsonObject file) throw new FormatException("not a JSON object");
        foreach (var (key, _) in file)
            if (key is not ("version" or "renamed" or "settings")) throw new FormatException($"\"{key}\" is not version, renamed or settings");
        if (file["version"] is { } version && (version.GetValueKind() != JsonValueKind.Number || !IsWhole(version)))
            throw new FormatException("\"version\" is not a whole number");
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (file["renamed"] is { } names)
        {
            if (names is not JsonObject map) throw new FormatException("\"renamed\" is not an object of old name to new name");
            foreach (var (old, now) in map)
                renamed[old] = now?.GetValueKind() == JsonValueKind.String ? now.GetValue<string>() : throw new FormatException($"\"renamed\".\"{old}\" is not a name");
        }
        if (file["settings"] is not JsonObject settings) throw new FormatException("no \"settings\" object");
        var declared = Group(settings, "", values);
        foreach (var (old, now) in renamed)
            if (!declared.ContainsKey(now)) throw new FormatException($"\"renamed\" names \"{now}\", which is not declared");
        return new(declared, renamed);
    }

    static Dictionary<string, Declaration> Group(JsonObject settings, string path, Values? values)
    {
        var declared = new Dictionary<string, Declaration>(StringComparer.Ordinal);
        foreach (var (name, node) in settings)
        {
            if (name.Length == 0) throw new FormatException($"{path}: a setting has no name");
            declared[name] = Declare(node, path + name, needsDefault: true, values);
        }
        return declared;
    }

    static Declaration Declare(JsonNode? node, string path, bool needsDefault, Values? values)
    {
        if (node is not JsonObject d) throw new FormatException($"\"{path}\" is not an object");
        foreach (var (key, _) in d)
            if (!Attributes.Contains(key)) throw new FormatException($"\"{path}\": \"{key}\" is not a setting's attribute");
        string type = Text(d, "type", path) ?? throw new FormatException($"\"{path}\" has no type");
        if (!Types.Contains(type)) throw new FormatException($"\"{path}\": type \"{type}\" is not one of {string.Join(", ", Types)}");
        foreach (string text in new[] { "label", "description", "unit" }) Text(d, text, path);
        if (Text(d, "format", path) is { } format && !Formats.Contains(format)) throw new FormatException($"\"{path}\": format \"{format}\" is not one of {string.Join(", ", Formats)}");
        if (Text(d, "applies", path) is { } applies && !Applies.Contains(applies)) throw new FormatException($"\"{path}\": applies \"{applies}\" is not one of {string.Join(", ", Applies)}");
        if (d["advanced"] is { } advanced && advanced.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException($"\"{path}\": advanced is not true or false");
        if (d["when"] is { } when && when is not JsonObject) throw new FormatException($"\"{path}\": when is not an object of setting to value");

        string? of = Text(d, "of", path);
        if (of is not (null or "integer" or "number")) throw new FormatException($"\"{path}\": of is not integer or number");
        if (of is not null && type != "range") throw new FormatException($"\"{path}\": only a range has of");
        string? pattern = Text(d, "pattern", path);
        Regex? regex;
        try { regex = pattern is null ? null : new Regex($"^(?:{pattern})$", RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
        catch (ArgumentException e) { throw new FormatException($"\"{path}\": pattern: {e.Message}"); }
        JsonNode[]? options = null;
        if (d["options"] is { } list)
        {
            if (list is not JsonArray array || array.Count == 0) throw new FormatException($"\"{path}\": options is not a list of values");
            // An option is a value, or {"value", "label"} for an editor's text.
            options = [.. array.Select(o => o is JsonObject labelled && labelled.ContainsKey("value") ? labelled["value"]! : o ?? throw new FormatException($"\"{path}\": an option is null"))];
        }
        string? source = Text(d, "source", path);
        if (options is not null && source is not null) throw new FormatException($"\"{path}\": options and source both");
        if ((options is not null || source is not null) && type is not ("choice" or "choices" or "map")) throw new FormatException($"\"{path}\": only a choice, choices or map has options or a source");
        if (type is "choice" or "choices" && options is null && source is null) throw new FormatException($"\"{path}\": a {type} needs options or a source");
        if (d["keys"] is not null) throw new FormatException($"\"{path}\": a map's keys come from options or a source");

        var declaration = new Declaration(type, d["default"]?.DeepClone(), Number(d, "min", path), Number(d, "max", path), Number(d, "step", path),
            options, source, Whole(d, "minLength", path), Whole(d, "maxLength", path), regex, Whole(d, "minItems", path), Whole(d, "maxItems", path),
            of == "integer",
            type == "list" ? Declare(d["item"] ?? throw new FormatException($"\"{path}\": a list needs item"), path + "[]", needsDefault: false, values) : null,
            type == "map" ? Declare(d["value"] ?? throw new FormatException($"\"{path}\": a map needs value"), path + "{}", needsDefault: false, values) : null,
            type == "group" ? Group(d["settings"] as JsonObject ?? throw new FormatException($"\"{path}\": a group needs settings"), path + ".", values) : null);
        if (type != "list" && d["item"] is not null) throw new FormatException($"\"{path}\": only a list has item");
        if (type != "map" && d["value"] is not null) throw new FormatException($"\"{path}\": only a map has value");
        if (type != "group" && d["settings"] is not null) throw new FormatException($"\"{path}\": only a group has settings");
        if (declaration.Min > declaration.Max) throw new FormatException($"\"{path}\": min is above max");
        if (declaration.Step is <= 0) throw new FormatException($"\"{path}\": step is not above 0");

        // A group's default is its settings'; every other setting's is its own, and must fit.
        if (type == "group")
        {
            if (d["default"] is not null) throw new FormatException($"\"{path}\": a group's default is its settings'");
            return declaration with { Default = Defaults(declaration.Settings!) };
        }
        if (declaration.Default is null)
            return needsDefault ? throw new FormatException($"\"{path}\" has no default") : declaration;
        if (Check(declaration, declaration.Default, values) is { } wrong) throw new FormatException($"\"{path}\": default {wrong}");
        return declaration;
    }

    static string? Text(JsonObject d, string key, string path) => d[key] switch
    {
        null => null,
        var v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => throw new FormatException($"\"{path}\": {key} is not text"),
    };

    static double? Number(JsonObject d, string key, string path) => d[key] switch
    {
        null => null,
        var v when v.GetValueKind() == JsonValueKind.Number => Num(v),
        _ => throw new FormatException($"\"{path}\": {key} is not a number"),
    };

    static int? Whole(JsonObject d, string key, string path) => Number(d, key, path) switch
    {
        null => null,
        double n when n >= 0 && n == Math.Floor(n) => (int)n,
        _ => throw new FormatException($"\"{path}\": {key} is not a whole number of 0 or more"),
    };

    static bool IsWhole(JsonNode node) => Num(node) is var n && n == Math.Floor(n);

    // Any JSON number: one parsed from a file, or one made from a C# long or int (which GetValue<double> refuses).
    static double Num(JsonNode node) => double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    // Equal as values: 0.2 is 0.20 (JSON text differs, the number does not).
    static bool Same(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (JsonArray x, JsonArray y) => x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second)),
        (JsonObject x, JsonObject y) => x.Count == y.Count && x.All(p => y.TryGetPropertyValue(p.Key, out var other) && Same(p.Value, other)),
        (JsonValue x, JsonValue y) when x.GetValueKind() == JsonValueKind.Number && y.GetValueKind() == JsonValueKind.Number => Num(x) == Num(y),
        (JsonValue x, JsonValue y) => JsonNode.DeepEquals(x, y),
        _ => false,
    };

    static JsonObject Defaults(IReadOnlyDictionary<string, Declaration> settings)
    {
        var values = new JsonObject();
        foreach (var (name, d) in settings) values[name] = d.Default?.DeepClone();
        return values;
    }

    /// <summary>What is wrong with a value for this declaration; null when it fits.</summary>
    public static string? Check(Declaration d, JsonNode? value, Values? values = null)
    {
        if (value is null) return "is null";
        var kind = value.GetValueKind();
        switch (d.Type)
        {
            case "bool":
                return kind is JsonValueKind.True or JsonValueKind.False ? null : "is not true or false";
            case "integer":
                if (kind != JsonValueKind.Number || !IsWhole(value)) return "is not a whole number";
                return Limits(d, Num(value));
            case "number":
                if (kind != JsonValueKind.Number || !double.IsFinite(Num(value))) return "is not a number";
                return Limits(d, Num(value));
            case "range":
                if (value is not JsonArray { Count: 2 } pair || pair.Any(p => p?.GetValueKind() != JsonValueKind.Number)) return "is not a pair of numbers [low, high]";
                double low = Num(pair[0]!), high = Num(pair[1]!);
                if (d.Integers && (!IsWhole(pair[0]!) || !IsWhole(pair[1]!))) return "is not a pair of whole numbers";
                if (low > high) return "has its low above its high";
                return Limits(d, low) ?? Limits(d, high);
            case "choice":
                return Allowed(d, value, values);
            case "choices":
                if (value is not JsonArray chosen) return "is not a list";
                if (chosen.Select(c => c?.ToJsonString()).Distinct().Count() != chosen.Count) return "lists a choice twice";
                return Count(d, chosen.Count) ?? chosen.Select(c => Allowed(d, c, values)).FirstOrDefault(e => e is not null);
            case "text":
                if (kind != JsonValueKind.String) return "is not text";
                string text = value.GetValue<string>();
                if (text.Length < d.MinLength) return $"is shorter than {d.MinLength}";
                if (text.Length > d.MaxLength) return $"is longer than {d.MaxLength}";
                try { return d.Pattern?.IsMatch(text) == false ? "does not match its pattern" : null; }
                catch (RegexMatchTimeoutException) { return "does not match its pattern"; }
            case "list":
                if (value is not JsonArray items) return "is not a list";
                return Count(d, items.Count) ?? items.Select((item, i) => Check(d.Item!, item, values) is { } e ? $"[{i}] {e}" : null).FirstOrDefault(e => e is not null);
            case "map":
                if (value is not JsonObject map) return "is not an object";
                foreach (var (key, item) in map)
                {
                    if ((d.Options is not null || d.Source is not null) && Allowed(d, JsonValue.Create(key), values) is not null) return $"has the key \"{key}\", which is not one of its keys";
                    if (Check(d.Value!, item, values) is { } e) return $".{key} {e}";
                }
                return null;
            case "group":
                if (value is not JsonObject group) return "is not an object";
                foreach (var (name, inner) in d.Settings!)
                    if (group[name] is { } set && Check(inner, set, values) is { } e) return $".{name} {e}";
                return null;
        }
        return $"has the unknown type {d.Type}";
    }

    static string? Limits(Declaration d, double n)
    {
        if (n < d.Min) return $"is below {d.Min}";
        if (n > d.Max) return $"is above {d.Max}";
        if (d.Step is { } step && Math.Abs((n - (d.Min ?? 0)) / step - Math.Round((n - (d.Min ?? 0)) / step)) > 1e-9) return $"is not a step of {step}";
        return null;
    }

    static string? Count(Declaration d, int count) =>
        count < d.MinItems ? $"has fewer than {d.MinItems}" : count > d.MaxItems ? $"has more than {d.MaxItems}" : null;

    static string? Allowed(Declaration d, JsonNode? value, Values? values)
    {
        if (value is null) return "is null";
        if (d.Options is { } options) return options.Any(o => Same(o, value)) ? null : $"{value.ToJsonString()} is not one of its options";
        if (value.GetValueKind() != JsonValueKind.String) return "is not text";
        // A source is checked when the game knows it (the game module loaded).
        if (d.Source is { } source && values?.Invoke(source) is { } known && !known.Contains(value.GetValue<string>(), StringComparer.OrdinalIgnoreCase))
            return $"\"{value.GetValue<string>()}\" is not a known {source}";
        return null;
    }

    /// <summary>The settings now: the user's values over the defaults, each checked; what does not fit is logged and left at its
    /// default, and a value no longer declared is logged and kept in the user's file.</summary>
    public static JsonObject Merge(Schema settings, string? valuesJson, Action<string> log, Values? values = null)
    {
        var merged = Defaults(settings.Settings);
        if (valuesJson is null) return merged;
        JsonObject user;
        try { user = JsonNode.Parse(valuesJson, documentOptions: Read) as JsonObject ?? throw new JsonException("not a JSON object"); }
        catch (JsonException e) { log($"the user's settings are not readable, defaults used: {e.Message}"); return merged; }
        foreach (var (old, now) in settings.Renamed)
            if (user[old] is { } moved && !user.ContainsKey(now)) { user.Remove(old); user[now] = moved; }
        Apply(settings.Settings, user, merged, "", log, values);
        return merged;
    }

    static void Apply(IReadOnlyDictionary<string, Declaration> declared, JsonObject user, JsonObject merged, string path, Action<string> log, Values? values)
    {
        foreach (var (name, value) in user)
        {
            if (!declared.TryGetValue(name, out var d)) { log($"setting {path}{name} is no longer used (kept in the user's file)"); continue; }
            if (d.Type == "group" && value is JsonObject inner) { Apply(d.Settings!, inner, (JsonObject)merged[name]!, path + name + ".", log, values); continue; }
            if (Check(d, value, values) is { } wrong) { log($"setting {path}{name} {wrong}: its default {d.Default?.ToJsonString()} is used"); continue; }
            merged[name] = value?.DeepClone();
        }
    }

    /// <summary>The merged settings as the mod's own type: a record's constructor by its parameters' names, then its settable
    /// properties, case not mattering. By reflection, not System.Text.Json, whose accessors stay cached by type for a while
    /// and kept an unloaded mod in memory.</summary>
    public static T Bound<T>(JsonObject merged) => (T)Bind(typeof(T), merged, typeof(T).Name)!;

    static object? Bind(Type type, JsonNode? node, string path)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (node is null) return null;
        try
        {
            if (typeof(JsonNode).IsAssignableFrom(target)) return node.DeepClone();
            if (target == typeof(string)) return node.GetValue<string>();
            if (target == typeof(bool)) return node.GetValue<bool>();
            if (target.IsEnum) return Enum.Parse(target, node.GetValue<string>(), ignoreCase: true);
            if (Type.GetTypeCode(target) is >= TypeCode.SByte and <= TypeCode.Decimal)
                return Convert.ChangeType(Num(node), target, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            throw new FormatException($"setting {path} does not read as {target.Name}: {e.Message}");
        }
        if (target.IsArray && node is JsonArray items)
        {
            var element = target.GetElementType()!;
            var array = Array.CreateInstance(element, items.Count);
            for (int i = 0; i < items.Count; i++) array.SetValue(Bind(element, items[i], $"{path}[{i}]"), i);
            return array;
        }
        if (target.IsGenericType && target.GetGenericArguments() is var generic)
        {
            var open = target.GetGenericTypeDefinition();
            if (node is JsonArray list && generic.Length == 1)
            {
                var made = open == typeof(HashSet<>) || open == typeof(ISet<>) || open == typeof(IReadOnlySet<>)
                    ? typeof(HashSet<>).MakeGenericType(generic) : typeof(List<>).MakeGenericType(generic);
                if (target.IsAssignableFrom(made))
                {
                    var add = made.GetMethod("Add")!;
                    var collection = Activator.CreateInstance(made)!;
                    for (int i = 0; i < list.Count; i++) add.Invoke(collection, [Bind(generic[0], list[i], $"{path}[{i}]")]);
                    return collection;
                }
            }
            if (node is JsonObject map && generic.Length == 2 && generic[0] == typeof(string)
                && target.IsAssignableFrom(typeof(Dictionary<,>).MakeGenericType(generic)))
            {
                var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(generic))!;
                foreach (var (key, item) in map) dictionary[key] = Bind(generic[1], item, $"{path}.{key}");
                return dictionary;
            }
        }
        if (node is JsonObject values && !target.IsAbstract && !target.IsInterface) return Construct(target, values, path);
        throw new FormatException($"setting {path} does not read as {target.Name}");
    }

    static object Construct(Type type, JsonObject values, string path)
    {
        bool Find(string name, out JsonNode? node)
        {
            var found = values.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            node = found.Value;
            return found.Key is not null;
        }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A record's primary constructor (its widest), not the copy constructor.
        var constructor = type.GetConstructors().Where(c => c.GetParameters() is not [var only] || only.ParameterType != type)
            .OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        object instance;
        if (constructor is null) instance = Activator.CreateInstance(type)!;
        else
        {
            var arguments = constructor.GetParameters().Select(p =>
            {
                if (Find(p.Name!, out var node)) { used.Add(p.Name!); return Bind(p.ParameterType, node, $"{path}.{p.Name}"); }
                if (p.HasDefaultValue) return p.DefaultValue;
                return p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
            }).ToArray();
            instance = constructor.Invoke(arguments);
        }
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.SetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0 && !used.Contains(property.Name)
                && Find(property.Name, out var node))
                property.SetValue(instance, Bind(property.PropertyType, node, $"{path}.{property.Name}"));
        return instance;
    }

    // The values as JSON, by reflection for the same reason as Bind: public properties by name, collections, numbers, text.
    static JsonNode? Unbind(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        Enum named => JsonValue.Create(named.ToString()),
        float or double or decimal => JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        sbyte or byte or short or ushort or int or uint or long => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong whole => JsonValue.Create(whole),
        IDictionary map => new JsonObject(map.Keys.Cast<object>().Select(k => KeyValuePair.Create(k.ToString()!, Unbind(map[k])))),
        IEnumerable items => new JsonArray([.. items.Cast<object?>().Select(Unbind)]),
        _ => new JsonObject(value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .Select(p => KeyValuePair.Create(p.Name, Unbind(p.GetValue(value))))),
    };

    /// <summary>The user's file for these values: what differs from the defaults, with what the file had that is no longer
    /// declared. Throws <see cref="ArgumentException"/> for a value that does not fit.</summary>
    public static string Saved<T>(Schema settings, T values, string? previousJson, Values? known = null)
    {
        var given = Named(Unbind(values) as JsonObject ?? throw new ArgumentException("the settings are not an object"), settings.Settings);
        var saved = new JsonObject();
        // Kept: what is no longer declared (another version's), as Merge keeps it.
        if (previousJson is not null && JsonNode.Parse(previousJson, documentOptions: Read) is JsonObject previous)
            foreach (var (name, value) in previous)
                if (!settings.Settings.ContainsKey(name) && !settings.Renamed.ContainsKey(name)) saved[name] = value?.DeepClone();
        foreach (var (name, d) in settings.Settings)
        {
            if (given[name] is not { } value) continue;
            if (Check(d, value, known) is { } wrong) throw new ArgumentException($"setting {name} {wrong}");
            if (!Same(value, d.Default)) saved[name] = value.DeepClone();
        }
        return saved.ToJsonString(Write);
    }

    // A C# type's names (MaxBots) as declared (maxBots), in its groups too; a map's keys are its data and stay.
    static JsonObject Named(JsonObject given, IReadOnlyDictionary<string, Declaration> declared)
    {
        var named = new JsonObject();
        foreach (var (name, d) in declared)
        {
            var value = given.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            if (value is null) continue;
            named[name] = d.Type == "group" && value is JsonObject group ? Named(group, d.Settings!) : value.DeepClone();
        }
        return named;
    }
}
