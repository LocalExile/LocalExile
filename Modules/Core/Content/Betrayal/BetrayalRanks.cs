using System.Text.Json;

namespace GameServer.Modules.Betrayal;

public static class BetrayalRanks
{
    public const string SaveKey = "Betrayal.ranks.v1";
    public sealed record Target(int Rank, int Turns = 0, bool KeepRank = false);

    public static Dictionary<int, Target> Read(string? text) => text is null ? []
        : JsonSerializer.Deserialize<Dictionary<int, Target>>(text) ?? [];

    public static Target Of(IReadOnlyDictionary<int, Target> board, BetrayalTables.Member member) =>
        board.GetValueOrDefault(member.Row) ?? new(member.Rank);

    public static void Advance(IDictionary<int, Target> board, IReadOnlySet<int>? eligible = null)
    {
        foreach (var (row, target) in board.ToArray())
            if (target.Turns > 0 && (eligible is null || eligible.Contains(row)))
                board[row] = target.Turns == 1
                    ? new(target.KeepRank ? target.Rank : Math.Max(0, target.Rank - 1))
                    : target with { Turns = target.Turns - 1 };
    }

    public static Target Interrogate(Target target, int retentionChance, Random rng) =>
        target.Turns > 0 ? target : target with { Turns = 3, KeepRank = rng.Next(100) < Math.Clamp(retentionChance, 0, 100) };

    public static bool Begin(Player owner, IReadOnlySet<int> eligible) => WorldOf(owner).CharacterData.Change(SaveKey, old =>
    {
        var board = Read(old);
        Advance(board, eligible);
        return JsonSerializer.Serialize(board);
    });

    public static bool Interrogate(Player owner, BetrayalTables.Member member, int chance, Random rng) =>
        WorldOf(owner).CharacterData.Change(SaveKey, old =>
        {
            var board = Read(old);
            board[member.Row] = Interrogate(Of(board, member), chance, rng);
            return JsonSerializer.Serialize(board);
        });
}
