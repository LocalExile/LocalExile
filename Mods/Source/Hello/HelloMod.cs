using GameServer.Modding;

namespace LocalExile.Mods.Hello;

/// <summary>The smallest mod: /hello [name] answers in chat with what the server knows of the player.</summary>
public sealed class HelloMod : IMod
{
    public void Load(IModContext mod) =>
        mod.OnChatCommand("hello", "/hello [name] (says hello, and where you are)", command =>
            command.Player.Tell($"Hello, {(command.Args.Count > 0 ? string.Join(' ', command.Args) : command.Player.Character)}! " +
                $"Level {command.Player.Level} in {command.Player.AreaId}, league {command.Player.League}."));
}
