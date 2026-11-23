namespace GameServer.Gateway.Tests;

public class ChatCommandsTests
{
    // A mod built for another LocalExile version throws MissingMethodException where its command first reaches the
    // changed member. That is the mod's fault, not the connection's: the player is told and stays connected.
    [Fact]
    public async Task A_command_of_a_mod_built_for_another_version_tells_the_player_instead_of_throwing()
    {
        var replies = new List<string>();
        var commands = new ChatCommands
        {
            Send = (_, _) => Task.CompletedTask,
            Reply = text => { replies.Add(text); return Task.CompletedTask; },
        };
        commands.Add("stale", "/stale", _ => throw new MissingMethodException("Method not found: 'Game.Changed'"));
        commands.Add("broken", "/broken", _ => throw new InvalidOperationException("half done"));

        Assert.True(await commands.RunAsync("/stale"));
        Assert.Contains("Update the mod", Assert.Single(replies));
        // Any other failure still ends the handler: the command may have left its work half done.
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.RunAsync("/broken"));
    }
}
