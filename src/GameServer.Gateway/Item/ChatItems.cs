using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>Items linked in chat as they travel through a chat server: the saved form, not the client's bytes, since a chat
/// server is open to anyone and the client stops at an item it cannot read. Each Gateway builds the bytes itself, from what
/// its own game data knows.</summary>
public static class ChatItems
{
    const int MaxSockets = 6;

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static string Write(Item item, ItemCatalog catalog) => JsonSerializer.Serialize(CharacterSaves.ToSaved(item, catalog.Mods), Options);

    /// <summary>The bytes for the client, or null for what this game data does not know or an item could not be.</summary>
    public static byte[]? Wire(string written, ItemCatalog catalog)
    {
        try
        {
            if (JsonSerializer.Deserialize<CharacterSaves.SavedItem>(written, Options) is not { } saved || !Possible(saved, socketable: true)) return null;
            return CharacterSaves.FromSaved(Known(saved, catalog), catalog).Wire();
        }
        catch (Exception e) when (e is JsonException or IOException or ArgumentException or InvalidOperationException or OverflowException) { return null; }
    }

    // What FromSaved takes on trust from a save of this server.
    static bool Possible(CharacterSaves.SavedItem item, bool socketable)
    {
        var sockets = item.Sockets ?? [];
        if (sockets.Count > (socketable ? MaxSockets : 0)) return false;
        if (item.Links is { } links && (links.Contains((byte)0) || links.Sum(n => n) != sockets.Count)) return false;
        return sockets.All(s => Enum.IsDefined(s.Colour) && (s.Gem is null || Possible(s.Gem, socketable: false)));
    }

    static CharacterSaves.SavedItem Known(CharacterSaves.SavedItem item, ItemCatalog catalog) => item with
    {
        Visual = item.Visual is ushort visual && catalog.IsVisual(visual) ? visual : null,
        Name = item.Name is { } name && name.All(catalog.IsWord) ? name : null,
        AlternateQualityType = item.AlternateQualityType is byte quality && catalog.IsAlternateQuality(quality) ? quality : null,
        GemVariant = catalog.ByHash(item.Type) is { } type && catalog.Gem(type) is { } gem && item.GemVariant >= 0 && item.GemVariant < gem.Variants.Count ? item.GemVariant : 0,
        Sockets = item.Sockets?.Select(s => s with { Gem = s.Gem is null ? null : Known(s.Gem, catalog) }).ToList(),
    };
}
