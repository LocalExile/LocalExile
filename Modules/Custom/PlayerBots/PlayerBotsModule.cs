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
        // Social owns the shared roster; this module resolves town actors that have no human socket.
        GameEvents.Packet(InstanceSocial.PartyInviteId).Add(module.Manifest.Id, async (conn, bytes) =>
            await director.Invite(conn, InstanceSocial.ReadPartyInvite(bytes)) != BotInvite.NotFound);
        // The party panel's invite box reaches a bot by name (C→S 0x93 is passed on to this module), but a player who
        // types a command instead needs the same path: '/botinvite' takes the invite, '/botleave' takes the party away,
        // so a bot can be tried out without opening the social panel.
        GameEvents.Command(module.Manifest.Id, "botinvite", "/botinvite <bot name> (invite a bot you can see into your party)", async (conn, args) =>
        {
            string name = string.Join(' ', args).Trim();
            if (name.Length == 0) { await director.Tell(Program.PlayerOf(conn), "Usage: /botinvite <bot name>"); return; }
            var result = await director.Invite(conn, name);
            await director.Tell(Program.PlayerOf(conn), result switch
            {
                BotInvite.Joined => $"{name} joined your party.",
                BotInvite.Refused => $"Your party is full or unavailable, so {name} stayed behind.",
                _ => $"No bot you can see is called \"{name}\" (names are in Modules/Custom/PlayerBots/state/bots.json).",
            });
        });
        GameEvents.Command(module.Manifest.Id, "botleave", "/botleave (the bots following you leave and go back to their quests)", async (conn, args) =>
        {
            bool left = await director.LeaveAny(conn);
            await director.Tell(Program.PlayerOf(conn), left ? "The bots left your party and went back to their quests." : "No bots are following you.");
        });
    }

    sealed class StopOnDispose(Action stop) : IDisposable
    {
        Action? remaining = stop;
        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
    }
}
