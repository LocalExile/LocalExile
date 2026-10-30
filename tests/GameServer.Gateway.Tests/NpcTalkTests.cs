namespace GameServer.Gateway.Tests;

public class NpcTalkTests
{
    [Theory]
    [InlineData("triggerer.SendOpenScreen( HideoutSelectionWindow, 1 ); triggerer.FinishNPCChat();", true)]
    public void Only_the_guild_argument_opens_the_guild_hideout_list(string script, bool guild)
        => Assert.Equal(guild, new NpcCatalog.Talk(0, 0, 0, "", "SelectHideout", script, []).OpensGuildHideouts);
}
