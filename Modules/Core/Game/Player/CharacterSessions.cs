public static partial class Game
{
    // The connection that holds each character. GGG's servers let one connection hold a character at a time, so a newer
    // login takes it from the older one instead of the two playing the same character; the map says which connection
    // claims it even before that connection is in game (Program.InGame holds only what entered an area).
    static readonly Dictionary<Guid, Player> CharacterHolder = new();

    /// <summary>
    /// Makes <paramref name="conn"/> the connection holding its character and returns every connection that has to end
    /// first: the one it takes the character from, and one the map does not know (a module reload between the two) still
    /// in game. The caller ends them (Program.EndAsync) and only then loads the character.
    /// </summary>
    public static IReadOnlyList<Player> ClaimCharacter(Player player)
    {
        if (player.Character.Id is not { } id) return [];
        var displaced = new List<Player>();
        lock (CharacterHolder)
        {
            if (CharacterHolder.TryGetValue(id, out var previous) && !ReferenceEquals(previous, player)) displaced.Add(previous);
            CharacterHolder[id] = player;
        }
        foreach (var other in Program.InGame.Keys)
            if (!ReferenceEquals(other, player) && other.HasClient && other.Character.Id == id
                && !displaced.Contains(other))
                displaced.Add(other);
        return displaced;
    }

    /// <summary>Forgets the connection's hold, if it still has it: a newer login may have taken the character already.</summary>
    public static void ReleaseCharacter(Player player)
    {
        if (player.Character.Id is not { } id) return;
        lock (CharacterHolder)
            if (CharacterHolder.TryGetValue(id, out var holder) && ReferenceEquals(holder, player)) CharacterHolder.Remove(id);
    }

    /// <summary>Drops every hold (the module unloading: its claims die with it, and the next generation takes them from the
    /// connections in game).</summary>
    public static void ForgetClaimedCharacters()
    {
        lock (CharacterHolder) CharacterHolder.Clear();
    }
}
