using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.PlayerBots;

/// <summary>A bot in a player's party: invited by name where the player sees it, it joins at once and follows the player
/// everywhere (towns and maps), wandering within FollowRange cells of them. Leaving or disbanding the party (or kicking
/// the bot) sends it to the nearest way out, where it goes and takes up its quests again. Party ids are this module's
/// own (from PartyIdBase), apart from the Social module's.</summary>
sealed partial class BotDirector
{
    const int FollowRange = 50, CatchUp = 150, PartyIdBase = 0x7b000000;
    static readonly TimeSpan LeaderGrace = TimeSpan.FromSeconds(30);
    static int _nextParty = PartyIdBase;

    // C→S 0x93 "Invite to Party" with a bot's name: true when a bot the player sees took it.
    public async Task<bool> Invite(ClientState conn, string name)
    {
        Bot? bot;
        uint party;
        lock (_bots)
        {
            bot = _bots.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)
                && b.Leader is null && b.Shown.Contains(conn.Instance));
            if (bot is null) return false;
            if (_bots.Count(b => ReferenceEquals(b.Leader, conn) && b.Phase == BotPhase.Following) >= 5)
                return true;
            // Select from the inviting town once. Later area changes preserve earned levels and experience.
            int level = LevelForTown(bot.Level, bot.Town, _random);
            if (level != bot.Level) { bot.Level = level; Dress(bot); }
            SaveProgress(bot);
            party = _bots.FirstOrDefault(b => ReferenceEquals(b.Leader, conn))?.Party ?? unchecked((uint)Interlocked.Increment(ref _nextParty));
            Hide(bot);
            (bot.Leader, bot.Party, bot.Phase, bot.FollowIn) = (conn, party, BotPhase.Following, null);
            (bot.LeaderName, bot.LeaderGoneSince) = (Program.CharacterName(conn), null);
            bot.Doing = $"в группе с {Program.CharacterName(conn)}";
            _log.Write(bot.Account, bot.Name, $"принял приглашение в группу от {Program.CharacterName(conn)}: идёт следом");
        }
        await SendParty(conn, party);
        return true;
    }

    // C→S 0x96 "Leave Party" on a bot party: the party goes, its bots leave.
    public async Task<bool> Leave(ClientState conn, uint party)
    {
        List<Bot> members;
        lock (_bots)
        {
            members = [.. _bots.Where(b => b.Party == party && ReferenceEquals(b.Leader, conn))];
            if (members.Count == 0) return false;
            foreach (var bot in members) Depart(bot, "группа распущена");
        }
        await Send(conn, InstanceSocial.PartyRemove(party), $"0x00aa bot party 0x{party:x} removed (left)");
        await Program.SetPartySize(conn, 1);
        return true;
    }

    public async Task<bool> Kick(ClientState conn, string name)
    {
        Bot? kicked;
        uint party;
        lock (_bots)
        {
            var members = _bots.Where(b => ReferenceEquals(b.Leader, conn) && b.Phase == BotPhase.Following).ToList();
            if (members.Count == 0) return false;
            kicked = members.FirstOrDefault(b => string.Equals(name, b.Account, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, b.Name, StringComparison.OrdinalIgnoreCase));
            if (kicked is null) return false;
            party = kicked.Party;
            Depart(kicked, "исключён из группы");
        }
        await SendParty(conn, party);
        return true;
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

    // The party (0xa9) to its leader: the leader and every bot still in it, each known to the client first (0xa7, the
    // leader too: without its own entry the client crashed building the panel, SocialModule.SendParty). None left: removed.
    async Task SendParty(ClientState leader, uint party)
    {
        List<Bot> members;
        lock (_bots) members = [.. _bots.Where(b => b.Party == party && ReferenceEquals(b.Leader, leader) && b.Phase == BotPhase.Following)];
        if (members.Count == 0)
        {
            await Send(leader, InstanceSocial.PartyRemove(party), $"0x00aa bot party 0x{party:x} removed");
            await Program.SetPartySize(leader, 1);
            return;
        }
        string lead = Program.CharacterName(leader), account = leader.Account ?? lead;
        var cls = Program.Skills.ClassFor(leader.ClassRow);
        await Send(leader, InstanceSocial.KnownPlayer(account, lead, leader.Pools.Level, CharacterClassByte.Encode((byte)cls.IntegerId, 0)), null);
        foreach (var bot in members)
        {
            var botClass = Program.Skills.ClassFor(Program.Skills.RowOfClass((int)bot.Class));
            await Send(leader, InstanceSocial.KnownPlayer(bot.Account, bot.Name, bot.Level, CharacterClassByte.Encode((byte)botClass.IntegerId, 0)),
                $"0x00a7 known player {bot.Name} (bot)");
        }
        (string, byte)[] entries = [(account, PartyLeaderByte), .. members.Select(b => (b.Account, PartyMemberByte))];
        await Send(leader, InstanceSocial.PartyInfo(party, lead, account, 0, 0, 0, 0, entries),
            $"0x00a9 bot party 0x{party:x}: {lead}, {string.Join(", ", members.Select(b => b.Name))}");
        await Program.SetPartySize(leader, 1 + members.Count);
    }

    // The party to a leader's new connection (an area change) once its player is in the area.
    async Task SendPartyWhenReady(ClientState conn, uint party)
    {
        for (int i = 0; i < 300 && !conn.Closed && !conn.PlayerSent; i++) await Task.Delay(100).ConfigureAwait(false);
        if (!conn.Closed) await SendParty(conn, party);
    }

    // 0xa9 member bytes as the Social module sends them: 1 member, 2 leader.
    const byte PartyMemberByte = 1, PartyLeaderByte = 2;

    static Task Send(ClientState conn, byte[] bytes, string? log) => conn.Outbox is { } outbox ? outbox(bytes, log) : Task.CompletedTask;

    // A party bot's step (under the bots' lock): beside its leader wherever they are, wandering within FollowRange and
    // catching up when further; leaving, to its way out. A leader gone takes it back to its quests.
    void StepWithLeader(Bot bot, DateTime now, double seconds)
    {
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
                if (bot.Phase == BotPhase.Departing || now - bot.LeaderGoneSince > LeaderGrace) Unparty(bot, now, "лидер группы вышел из игры");
                return;
            }
            bool first = !_bots.Any(b => b != bot && ReferenceEquals(b.Leader, next));
            foreach (var member in _bots.Where(b => b.Party == bot.Party)) (member.Leader, member.LeaderGoneSince) = (next, null);
            if (first) _ = SendPartyWhenReady(next, bot.Party);
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
        (bot.Leader, bot.Party, bot.FollowIn) = (null, 0, null);
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
