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

    // C→S 0x92: a party row's travel button, with that member's character name (the same reader as the party invite).
    const ushort PartyTravelId = 0x92;

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
        // C→S 0x92 "Travel" on a party member's row: the Social module moves a player to another player, and passes on a
        // name no player in game has — a party bot's, which is where /bot_stay left it (#715).
        module.OnPacket(PartyTravelId, p =>
            director.TravelTo(Program.ConnOf(p.Player), module.World(p.Player), InstanceSocial.ReadPartyInvite(p.Bytes.Span)));
        // A party bot follows its leader everywhere; these park it in the area it is in (kept in the party, its row
        // shows that area) so a member in another area can be tried out, e.g. the party panel's travel button (#715).
        module.OnChatCommand("bot_stay", "/bot_stay (dev: the party bots stop following and keep the area they are in)",
            async command =>
            {
                int n = await director.Park(Program.ConnOf(command.Player), parked: true);
                await command.Player.Tell(n == 0 ? "No bot is following you." : $"{n} bot(s) stay here and keep their place in the party.");
            });
        module.OnChatCommand("bot_follow", "/bot_follow (dev: the party bots follow you again)",
            async command =>
            {
                int n = await director.Park(Program.ConnOf(command.Player), parked: false);
                await command.Player.Tell(n == 0 ? "No bot is in your party." : $"{n} bot(s) follow you again.");
            });
    }

    sealed class StopOnDispose(Action stop) : IDisposable
    {
        Action? remaining = stop;
        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
    }
}
