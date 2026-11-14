using Rules = global::Game;

namespace GameServer.Gateway.Tests;

/// <summary>The game's login rule: one connection holds a character, and a newer login takes it from the older one.</summary>
public sealed class CharacterSessionTests
{
    static ClientState Connection(Guid character) =>
        new(new(1, "session", 1000, 1000, 100, 100, null, []), 1, 1) { CharacterId = character, Character = "Held" };

    [Fact]
    public void A_newer_connection_displaces_the_one_holding_the_character_and_only_that_one()
    {
        var character = Guid.NewGuid();
        var first = Connection(character);
        var second = Connection(character);
        var other = Connection(Guid.NewGuid());

        Assert.Empty(Rules.ClaimCharacter(first));
        Assert.Same(first, Assert.Single(Rules.ClaimCharacter(second)));
        // Another character is its own hold...
        Assert.Empty(Rules.ClaimCharacter(other));
        // ...and a connection that lost the character cannot release the one holding it now.
        Rules.ReleaseCharacter(first);
        var third = Connection(character);
        Assert.Same(second, Assert.Single(Rules.ClaimCharacter(third)));

        // A connection without a character (a refused admission, a bot) holds nothing, and the holder claiming its own
        // character displaces nobody.
        var anonymous = new ClientState(new(1, "session", 1000, 1000, 100, 100, null, []), 1, 1);
        Assert.Empty(Rules.ClaimCharacter(anonymous));
        Assert.Empty(Rules.ClaimCharacter(third));

        // Releasing drops the hold: the next login has nothing to end.
        Rules.ReleaseCharacter(third);
        var after = Connection(character);
        Assert.Empty(Rules.ClaimCharacter(after));

        Rules.ReleaseCharacter(after);
        Rules.ReleaseCharacter(other);
        Rules.ReleaseCharacter(first);
    }

    [Fact]
    public async Task Ending_a_connection_waits_until_its_save_is_written()
    {
        var conn = Connection(Guid.NewGuid());
        conn.Closed = true;   // already ending: what is left is the wait for its own cleanup
        var ending = Program.EndAsync(conn, "the test ends it");
        Assert.False(ending.IsCompleted);
        conn.Ended.TrySetResult();   // the connection's thread saved and left
        await ending;
    }
}
