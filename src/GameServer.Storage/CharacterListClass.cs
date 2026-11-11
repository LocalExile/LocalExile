using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Storage;

/// <summary>Projects saved ascendancy identity into the login list without copying progression into account records.</summary>
public sealed class CharacterListClass(SaveDatabase db)
{
    readonly Lazy<Dictionary<string, (byte BaseClass, byte Number)>> _classes = new(() =>
    {
        using var characters = JsonDocument.Parse(ClientTables.ReadAllText("data/tables/Characters.json"));
        using var ascendancies = JsonDocument.Parse(ClientTables.ReadAllText("data/tables/Ascendancy.json"));
        return ascendancies.RootElement.EnumerateArray().ToDictionary(
            row => row.GetProperty("Id").GetString()!,
            row => ((byte)characters.RootElement[row.GetProperty("Character").GetInt32()].GetProperty("IntegerId").GetInt32(),
                (byte)row.GetProperty("ClassNo").GetInt32()));
    });

    public byte For(Character character)
    {
        byte baseClass = (byte)character.Class;
        string? saved = db.Get(SaveDatabase.Table.Characters, character.Name);
        if (saved is null) return baseClass;
        using var save = JsonDocument.Parse(saved);
        if (!save.RootElement.TryGetProperty("MainAscendancy", out var progress) || progress.ValueKind == JsonValueKind.Null
            || !progress.TryGetProperty("Selection", out var selection) || selection.ValueKind == JsonValueKind.Null)
            return baseClass;
        string id = selection.GetProperty("Id").GetString()!;
        return _classes.Value.TryGetValue(id, out var choice) && choice.BaseClass == baseClass
            ? CharacterClassByte.Encode(baseClass, choice.Number) : baseClass;
    }
}
