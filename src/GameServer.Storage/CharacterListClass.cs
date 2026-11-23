using GameServer.Core;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Storage;

/// <summary>Projects saved ascendancy identity into the login list without copying progression into account records.</summary>
public sealed class CharacterListClass(SaveDatabase db)
{
    readonly Lazy<Dictionary<string, (byte BaseClass, byte Number)>> _classes = new(() =>
    {
        var characters = ClientTables.Table("Characters");
        return ClientTables.Table("Ascendancy").ToDictionary(
            row => row.Text("Id")!,
            row => ((byte)characters[row.Int("Character")].Int("IntegerId"), (byte)row.Int("ClassNo")));
    });

    public byte For(Character character)
    {
        byte baseClass = (byte)character.Class;
        if (db.Ascendancy(character.Name) is not { } id) return baseClass;
        return _classes.Value.TryGetValue(id, out var choice) && choice.BaseClass == baseClass
            ? CharacterClassByte.Encode(baseClass, choice.Number) : baseClass;
    }
}
