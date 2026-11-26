using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Bestiary;

/// <summary>The account/league's captured beasts, independent of the connection and the map that supplied them.</summary>
public static class BestiaryCaptures
{
    public const string StorageKey = "Bestiary/CapturedBeasts";
    public const int Capacity = 1000;

    public sealed record Beast(ulong Id, string Capture, int CapturableRow, ushort Variety, byte Level,
        bool Red, MonsterRarity Rarity, IReadOnlyList<ItemMod> Mods, IReadOnlyList<uint> NameWords);
    sealed record Store(ulong NextId, List<Beast> Beasts);

    static Store Read(string? text) => text is null ? new(1, []) : JsonSerializer.Deserialize<Store>(text)!;

    public static IReadOnlyList<Beast> Load(ISavedData account) => Read(account.Get(StorageKey)).Beasts;

    public static IReadOnlyList<Beast> Credit(ISavedData account, string capture, BestiaryTables.CaptureDefinition definition,
        SpawnedMonster monster, bool red, int copies)
    {
        List<Beast> added = [];
        account.Change(StorageKey, text =>
        {
            var store = Read(text);
            if (store.Beasts.Any(b => b.Capture == capture)) return null;
            ulong next = store.NextId;
            for (int i = 0; i < Math.Clamp(copies, 1, 2); i++)
            {
                var beast = new Beast(next++, capture, definition.Row, definition.Variety, monster.Level, red,
                    monster.Rarity, [.. monster.Mods.Mods], [.. monster.Mods.NameWords]);
                added.Add(beast);
                store.Beasts.Add(beast);
            }
            if (store.Beasts.Count > Capacity) store.Beasts.RemoveRange(0, store.Beasts.Count - Capacity);
            return JsonSerializer.Serialize(new Store(next, store.Beasts));
        });
        return added;
    }
}
