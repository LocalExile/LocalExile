using GameServer.Modding;

namespace LocalExile.Mods.Example;

/// <summary>
/// The example mod: /example tells what it counted since it was loaded.
/// Build it (dotnet build Mods\Source\Example) while the Gateway runs and it reloads; its counts start over, like any
/// mod's state on a reload.
/// </summary>
public sealed class ExampleMod : IMod
{
    int _kills, _areas;
    readonly DateTime _loaded = DateTime.Now;

    public void Load(IModContext mod)
    {
        mod.OnChatCommand("example", "/example (the example mod: kills and areas since it was loaded)", command =>
            command.Player.Tell($"Example mod {mod.Manifest.Version}, loaded at {_loaded:HH:mm:ss}: " +
                $"{_kills} kill(s), {_areas} area(s) entered; {command.Player.Character} is level {command.Player.Level} in {command.Player.AreaId}."));
        mod.OnMonsterKilled(kill =>
        {
            Interlocked.Increment(ref _kills);
            return Task.CompletedTask;
        });
        mod.OnAreaGenerated(player =>
        {
            Interlocked.Increment(ref _areas);
            return Task.CompletedTask;
        });
        mod.Log("ready (/example)");
    }
}
