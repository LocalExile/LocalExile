using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.PlayerBots;

/// <summary>Bots the server plays, so a new server feels lived in (BotDirector). On while a league lists the module (no league
/// does by default). Settings in settings.json, the route in story-main.md and story-side.md, the log in logs/ (all beside
/// this file). The economy snapshot of tools/collect_economy.py is in economy/&lt;league&gt;/ for the trading to come.</summary>
public sealed class PlayerBotsModule : IModule
{
    // One director for the process: a hot reload loads this assembly again, and the old one's bots must go first.
    const string Running = "LocalExile.PlayerBots.Stop";

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        (AppContext.GetData(Running) as Action)?.Invoke();
        var settings = module.Settings<BotSettings>();
        var route = BotQuest.Route(module.Folder);
        if (route.Count == 0) { module.Log("bots: no route (story-main.md, story-side.md)"); return; }
        var director = new BotDirector(settings, route, new BotLog(Path.Combine(module.Folder, "logs")), module.Log,
            Path.Combine(module.Folder, "state", "bots.json"));
        AppContext.SetData(Running, (Action)director.Stop);
        module.Lifetime.Own(new StopOnDispose(director.Stop));
        director.Start();
        // Parties with bots (BotParty): the Social module passes on the invites and parties that are not its own.
        module.OnPacket(InstanceSocial.PartyInviteId, p => director.Invite(Program.ConnOf(p.Player), InstanceSocial.ReadPartyInvite(p.Bytes.ToArray())));
        module.OnPacket(InstanceSocial.PartyLeaveId, p => director.Leave(Program.ConnOf(p.Player), InstanceSocial.ReadPartyAccept(p.Bytes.ToArray())));
        module.OnPacket(InstanceSocial.PartyKickId, p => director.Kick(Program.ConnOf(p.Player), InstanceSocial.ReadPartyKick(p.Bytes.Span)));
    }

    sealed class StopOnDispose(Action stop) : IDisposable
    {
        Action? remaining = stop;
        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
    }
}
