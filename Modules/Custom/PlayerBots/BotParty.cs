using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Modules.Social;

namespace GameServer.Modules.PlayerBots;

/// <summary>A bot in a player's party: invited by name where the player sees it, it joins at once and follows the player
/// everywhere (towns and maps), wandering within FollowRange cells of them. Leaving or disbanding the party (or kicking
/// the bot) sends it to the nearest way out, where it goes and takes up its quests again. Party ids are
/// shared with real players through the Social module.</summary>
/// <summary>What an invite from the party panel did: no bot by that name was reachable, the leader's party is full,
/// or the bot joined.</summary>
enum BotInvite : byte { NotFound, Refused, Joined }

sealed partial class BotDirector
{
    const int FollowRange = 50, CatchUp = 150;
    static readonly TimeSpan LeaderGrace = TimeSpan.FromSeconds(30);

    // C→S 0x93 "Invite to Party" with a bot's name: NotFound when no bot the player can reach has that name, Refused when
    // their party is already full (the invite is still consumed, so it does not reach the Social module), Joined
    // otherwise.
    public async Task<BotInvite> Invite(ClientState conn, string name)
    {
        Bot? bot;
        uint party;
        lock (_bots)
        {
            bot = _bots.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)
                && b.Leader is null
                // Visible to this player, or else standing in the town they are in: a module reload clears Shown until
                // the bot's next publish, which otherwise refuses an invite for a bot the player can see. Town alone
                // is not presence — BeginQuest hides the bot and then sets Town to the quest's town — so the fallback
                // asks for a town phase too.
                && (b.Shown.Contains(conn.Instance)
                    || (string.Equals(b.Town, conn.Layout.Id, StringComparison.OrdinalIgnoreCase)
                        && b.Phase is BotPhase.Arriving or BotPhase.Walking or BotPhase.Idling)));
            if (bot is null) return BotInvite.NotFound;

            if (!PartyRoster.TryAddCompanion(conn, Companion(bot, conn.Layout.Hash), out party)) return BotInvite.Refused;
            try
            {
                // Select from the inviting town once. Later area changes preserve earned levels and experience.
                int level = LevelForTown(bot.Level, bot.Town, _random);
                if (level != bot.Level) { bot.Level = level; Dress(bot); }
                SaveProgress(bot);
            }
            catch
            {
                PartyRoster.RemoveCompanion(party, bot.Account);
                _ = PartyRoster.Publish(party);
                throw;
            }
            var leader = PartyRoster.Leader(party)!;
            Hide(bot);
            (bot.Leader, bot.Party, bot.Phase, bot.FollowIn) = (leader, party, BotPhase.Following, null);
            (bot.LeaderName, bot.LeaderGoneSince) = (Program.CharacterName(leader), null);
            bot.Doing = $"в группе с {Program.CharacterName(conn)}";
            _log.Write(bot.Account, bot.Name, $"принял приглашение в группу от {Program.CharacterName(conn)}: идёт следом");
        }
        await SendParty(conn, party);
        return BotInvite.Joined;
    }

    // Out of the party: to the nearest way out of the area it is in, then on with its quests (StepWithLeader).
    void Depart(Bot bot, string why)
    {
        bot.Control = null;
        StopCombat(bot);
        bot.Phase = BotPhase.Departing;
        bot.Doing = $"{why}: уходит к ближайшему выходу";
        if (bot.Leader is { } leader)
        {
            var layout = leader.Layout;
            var exits = layout.AreaTransitions.Select(t => ((double)t.Transition.GridX, (double)t.Transition.GridY))
                .Concat(layout.Waypoint is { } wp ? [((double)wp.GridX, (double)wp.GridY)] : []).ToList();
            if (exits.Count == 0) exits.Add((layout.SpawnX, layout.SpawnY));
            (bot.TargetX, bot.TargetY) = exits.MinBy(e => Math.Abs(e.Item1 - bot.X) + Math.Abs(e.Item2 - bot.Y));
        }
        _log.Write(bot.Account, bot.Name, bot.Doing);
    }

    // A following bot is in its leader's area, and that is the area its row shows: 0xa7 +0xa8 is the WorldAreas HASH16 the
    // party panel resolves for the member's area name (a level showed "Travelling...", #715).
    static PartyCompanion Companion(Bot bot, ushort area)
    {
        var cls = Program.Skills.ClassFor(Program.Skills.RowOfClass((int)bot.Class));
        return new(bot.Account, bot.Name, InstanceSocial.KnownPlayer(bot.Account, bot.Name, area,
            CharacterClassByte.Encode((byte)cls.IntegerId, 0)));
    }

    async Task SendParty(ClientState leader, uint party)
    {
        lock (_bots)
            foreach (var bot in _bots.Where(b => b.Party == party && b.Phase == BotPhase.Following))
                PartyRoster.UpdateCompanion(party, Companion(bot, leader.Layout.Hash));
        await PartyRoster.Publish(party);
    }

    // Social's first-entry tick publishes the roster when a connection is not ready yet.
    Task SendPartyWhenReady(ClientState conn, uint party) =>
        conn is { Closed: false, PlayerSent: true } ? SendParty(conn, party) : Task.CompletedTask;

    static Task Send(ClientState conn, byte[] bytes, string? log) => conn.Outbox is { } outbox ? outbox(bytes, log) : Task.CompletedTask;

    /// <summary>A chat line back to the player who ran a bot command.</summary>
    public Task Tell(IPlayer player, string text) =>
        Send(Program.ConnOf(player), InstanceChat.ChatLine(text), $"0x0017 {text}");

    /// <summary>'/botleave': every bot following this player leaves and goes back to its quests.</summary>
    public async Task<bool> LeaveAny(ClientState conn)
    {
        uint party = PartyRoster.PartyOf(conn);
        if (!PartyRoster.IsLeader(conn, party)) return false;
        bool left = false;
        lock (_bots)
            foreach (var bot in _bots.Where(b => b.Party == party && b.Phase == BotPhase.Following))
            {
                PartyRoster.RemoveCompanion(party, bot.Account);
                Depart(bot, "группа распущена");
                left = true;
            }
        if (left) await PartyRoster.Publish(party);
        return left;
    }

    // A party bot's step (under the bots' lock): beside its leader wherever they are, wandering within FollowRange and
    // catching up when further; leaving, to its way out. A leader gone takes it back to its quests.
    void StepWithLeader(Bot bot, DateTime now, double seconds)
    {
        if (bot.Phase == BotPhase.Following)
        {
            if (!PartyRoster.ContainsCompanion(bot.Party, bot.Account)) Depart(bot, "исключён из группы");
            else if (PartyRoster.Leader(bot.Party) is { } current && !ReferenceEquals(current, bot.Leader))
            {
                StopCombat(bot);
                bot.Control = null;
                (bot.Leader, bot.LeaderName, bot.LeaderGoneSince) = (current, Program.CharacterName(current), null);
            }
        }
        var leader = bot.Leader;
        if (leader is null || leader.Closed || !Program.InGame.ContainsKey(leader))
        {
            StopCombat(bot);
            // An area change is a new connection of the same character: the bot goes on with it, and its client gets the
            // party again. Only a leader not back within LeaderGrace leaves it.
            Hide(bot);
            var next = Program.InGame.Keys.FirstOrDefault(c => !c.Closed && !ReferenceEquals(c, leader)
                && string.Equals(Program.CharacterName(c), bot.LeaderName, StringComparison.Ordinal));
            if (next is null)
            {
                bot.LeaderGoneSince ??= now;
                if (bot.Phase == BotPhase.Departing) Unparty(bot, now, "ушёл через выход");
                else if (now - bot.LeaderGoneSince > LeaderGrace)
                {
                    if (leader is not null) PartyRoster.RemoveHuman(leader, bot.Party);
                    _ = PartyRoster.Publish(bot.Party);
                    if (PartyRoster.Leader(bot.Party) is null) Unparty(bot, now, "лидер группы вышел из игры");
                }
                return;
            }
            bool first = !_bots.Any(b => b != bot && ReferenceEquals(b.Leader, next));
            foreach (var member in _bots.Where(b => b.Party == bot.Party)) (member.Leader, member.LeaderGoneSince) = (next, null);
            if (first) _ = PartyRoster.Rebind(next);
            leader = next;
        }
        if (leader.LeavingArea || !leader.PlayerSent || leader.Instance is not { } instance
            || instance.Player(leader.PeerId) is not { } pose)
        {
            Hide(bot);
            return;
        }
        var layout = leader.Layout;
        if (bot.Phase == BotPhase.Following) return;
        if (bot.Phase == BotPhase.Departing)
        {
            // The leader left the area first: it goes with nobody watching.
            if (!ReferenceEquals(bot.FollowIn, instance) || Walk(bot, layout, seconds)) { Unparty(bot, now, "ушёл через выход"); return; }
            Show(bot, [leader]);
            return;
        }
        double far = Math.Max(Math.Abs(bot.X - pose.X), Math.Abs(bot.Y - pose.Y));
        // A new area (or a jump: a portal, a waypoint, a blink): it comes out beside the leader.
        if (!ReferenceEquals(bot.FollowIn, instance) || far > CatchUp)
        {
            Hide(bot);
            (bot.X, bot.Y) = Near(layout, pose.X, pose.Y, 6, 14);
            (bot.TargetX, bot.TargetY) = (bot.X, bot.Y);
            (bot.FollowIn, bot.Town, bot.PlacedIn) = (instance, layout.Id, layout.Id);
            bot.Until = now.AddSeconds(_random.Next(2, 6));
        }
        else if (far > FollowRange)
        {
            // Too far: runs to the leader's side.
            (bot.TargetX, bot.TargetY) = Near(layout, pose.X, pose.Y, 8, 20);
            bot.Until = now.AddSeconds(_random.Next(2, 6));
        }
        else if (now >= bot.Until && Math.Abs(bot.TargetX - bot.X) + Math.Abs(bot.TargetY - bot.Y) < 2)
        {
            // Wanders about the leader now and then.
            (bot.TargetX, bot.TargetY) = Near(layout, pose.X, pose.Y, 10, FollowRange - 10);
            bot.Until = now.AddSeconds(_random.Next(3, 9));
        }
        double speed = far > FollowRange ? 1.6 : 1;
        if (Walk(bot, layout, seconds, speed)) (bot.TargetX, bot.TargetY) = (bot.X, bot.Y);
        Show(bot, [leader]);
    }

    // Back to its quests (from where it left them), out of the party.
    void Unparty(Bot bot, DateTime now, string why)
    {
        bot.Control = null;
        StopCombat(bot);
        Hide(bot);
        _log.Write(bot.Account, bot.Name, $"покинул группу ({why}): продолжает квесты");
        uint party = bot.Party;
        if (PartyRoster.RemoveCompanion(party, bot.Account)) _ = PartyRoster.Publish(party);
        (bot.Leader, bot.LeaderName, bot.Party, bot.FollowIn) = (null, null, 0, null);
        BeginQuest(bot, now);
    }

    // A walkable cell between min and max cells from (x, y), else (x, y) itself.
    (double X, double Y) Near(AreaLayout layout, double x, double y, int min, int max)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            double angle = _random.NextDouble() * 2 * Math.PI, radius = _random.Next(min, max + 1);
            double nx = x + Math.Cos(angle) * radius, ny = y + Math.Sin(angle) * radius;
            if (nx < 1 || ny < 1 || layout.GridW > 0 && nx >= layout.GridW - 1 || layout.GridH > 0 && ny >= layout.GridH - 1) continue;
            if (Walkable(layout, nx, ny)) return (nx, ny);
        }
        return (x, y);
    }
}
