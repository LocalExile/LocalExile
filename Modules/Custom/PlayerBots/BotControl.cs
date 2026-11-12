using GameServer.Modules.Social;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameServer.Modules.PlayerBots;

sealed class BotControlLease(string controller, int seconds, TimeProvider? clock = null)
{
    readonly TimeProvider time = clock ?? TimeProvider.System;
    long renewed = (clock ?? TimeProvider.System).GetTimestamp();
    internal readonly string Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    internal string Controller { get; } = controller;
    internal long Sequence { get; private set; }
    string? lastFingerprint;
    object? lastReceipt;
    internal double Remaining => Math.Max(0, seconds - time.GetElapsedTime(renewed).TotalSeconds);
    internal bool Active => Remaining > 0;
    internal long Deadline => checked(renewed + seconds * time.TimestampFrequency);
    internal void Renew() => renewed = time.GetTimestamp();
    internal bool Owns(string id) => Active && CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(Id), System.Text.Encoding.UTF8.GetBytes(id));
    internal object Accept(long sequence, string fingerprint, Func<object> apply)
    {
        if (!Active) throw new InvalidOperationException("Control lease expired.");
        if (sequence == Sequence && lastFingerprint == fingerprint) return lastReceipt!;
        if (sequence <= Sequence) throw new InvalidOperationException("Sequence must increase; a retry must use the same payload.");
        var receipt = apply();
        (Sequence, lastFingerprint, lastReceipt) = (sequence, fingerprint, receipt);
        return receipt;
    }
}

sealed partial class BotDirector
{
    static IControlledPlayerCommands CommandRules => Program.GameLogicOrNull as IControlledPlayerCommands
        ?? throw new InvalidOperationException("The current game module does not expose controlled-player commands.");

    internal object McpConfiguration() => new
    {
        module = "PlayerBots", settings = _settings,
        townLevels = Enumerable.Range(1, 10).Select(act =>
        {
            string town = $"{(act <= 5 ? 1 : 2)}_{act}_town";
            var range = LevelsForTown(town);
            return new { act, town, minimum = range.Min, maximum = range.Max };
        }).ToArray(),
        controls = new { maximumLeaderDistance = (Program.GameLogicOrNull as IControlledPlayerCommands)?.MaximumLeaderDistance, gapGraceGameSeconds = 1.25,
            immediateRecoveryDistance = 100, orderLifetimeGameSeconds = 5, observationEnemyLimit = 64,
            leaseSeconds = _settings.McpLeaseSeconds, partyBotsOnly = true, fallback = "ordinary following and combat AI" },
    };

    internal object McpCall(string tool, JsonElement args)
    {
        lock (_bots)
        {
            foreach (var b in _bots) ExpireControl(b);
            if (tool == "get_configuration") return McpConfiguration();
            if (tool == "list_bots") return new { bots = _bots.Select(b => new
            {
                botId = b.CharacterId, name = b.Name, level = b.Level, characterClass = b.Class.ToString(),
                phase = b.Phase.ToString(), leader = b.LeaderName, area = b.Combat?.Layout.Id ?? b.Town,
                controllable = Ready(b) && b.Combat!.PlayerLife > 0, controller = b.Control?.Controller, leaseSecondsLeft = b.Control?.Remaining ?? 0,
            }).ToArray() };
            var bot = _bots.SingleOrDefault(b => b.CharacterId == Guid.Parse(BotMcpProtocol.Text(args, "botId", 36)))
                ?? throw new InvalidOperationException("Unknown bot. Refresh list_bots.");
            if (tool == "observe_bot")
            {
                if (!Ready(bot)) return new { botId = bot.CharacterId, ready = false, phase = bot.Phase.ToString(),
                    controller = bot.Control?.Controller, message = "Bot is joining/transferring or outside a live human party. Refresh later." };
                return new { botId = bot.CharacterId, ready = true, epoch = bot.ControlEpoch,
                    controller = bot.Control?.Controller, leaseSecondsLeft = bot.Control?.Remaining ?? 0,
                    party = _bots.Where(b => b.Party == bot.Party && b.LeaderName == bot.LeaderName).Select(b => new
                        { botId = b.CharacterId, name = b.Name, peerId = b.PeerId, controller = b.Control?.Controller }).ToArray(),
                    players = PartyRoster.Players(bot.Party).Select(c => new
                        { name = Program.CharacterName(c), peerId = c.PeerId, leader = ReferenceEquals(c, bot.Leader) }).ToArray(),
                    observation = CommandRules.ObserveControlledPlayer(bot.Combat!, bot.Leader!) };
            }
            if (tool == "claim_bot")
            {
                if (!Ready(bot) || bot.Combat!.PlayerLife == 0) throw new InvalidOperationException("Only a ready living bot in a live human player's party can be claimed.");
                if (bot.Control is not null) throw new InvalidOperationException("Bot is already leased; its controller must release it or let the lease expire.");
                var claimed = new BotControlLease(BotMcpProtocol.Text(args, "controller", 80), _settings.McpLeaseSeconds);
                var conn = bot.Combat!;
                CommandRules.QueueControlledIntent(conn, new(Guid.NewGuid().ToString("N"), "hold",
                    conn.Clock.Elapsed.TotalSeconds + 3, WallDeadline: claimed.Deadline));
                bot.Control = claimed;
                _console($"bots MCP: {bot.Name} claimed by {bot.Control.Controller}");
                return new { botId = bot.CharacterId, leaseId = bot.Control.Id, seconds = bot.Control.Remaining,
                    epoch = bot.ControlEpoch, nextSequence = 1 };
            }
            string leaseId = BotMcpProtocol.Text(args, "leaseId", 96);
            if (bot.Control is not { } lease || !lease.Owns(leaseId))
                throw new InvalidOperationException("Lease is missing, expired or belongs to another controller. Observe and claim again.");
            if (tool == "renew_control") { lease.Renew(); return new { botId = bot.CharacterId, seconds = lease.Remaining }; }
            if (tool == "release_bot")
            {
                if (bot.Combat is { } old) CommandRules.ClearControlledIntent(old);
                bot.Control = null;
                _console($"bots MCP: {bot.Name} released to ordinary party AI");
                return new { botId = bot.CharacterId, released = true };
            }
            if (!Ready(bot)) throw new InvalidOperationException("Bot is not ready. Observe after the transfer/death.");
            string epoch = BotMcpProtocol.Text(args, "epoch", 36);
            if (epoch != bot.ControlEpoch.ToString()) throw new InvalidOperationException("Stale area/session epoch. Observe the bot again before ordering.");
            long sequence = BotMcpProtocol.Integer(args, "sequence", 1, long.MaxValue);
            string action = BotMcpProtocol.Text(args, "action", 16);
            ushort skill = (ushort)BotMcpProtocol.OptionalInteger(args, "skillId", 0, ushort.MaxValue);
            uint target = (uint)BotMcpProtocol.OptionalInteger(args, "targetId", 0, uint.MaxValue);
            int x = (int)BotMcpProtocol.OptionalInteger(args, "x", 0, int.MaxValue);
            int y = (int)BotMcpProtocol.OptionalInteger(args, "y", 0, int.MaxValue);
            int flask = (int)BotMcpProtocol.OptionalInteger(args, "flaskSlot", 1, 5);
            double seconds = args.TryGetProperty("seconds", out var duration) ? duration.GetDouble() : 3;
            if (!double.IsFinite(seconds) || seconds is < .1 or > 5) throw new ArgumentException("seconds must be between 0.1 and 5.");
            if (action is "attack" or "focus" or "pickup") _ = BotMcpProtocol.Integer(args, "targetId", 1, uint.MaxValue);
            if (action is "attack" or "cast") _ = BotMcpProtocol.Integer(args, "skillId", 1, ushort.MaxValue);
            if (action == "move" || action == "cast" && target == 0)
            { _ = BotMcpProtocol.Integer(args, "x", 0, int.MaxValue); _ = BotMcpProtocol.Integer(args, "y", 0, int.MaxValue); }
            if (action == "cast" && target != 0 && target != bot.Combat!.PeerId && target != bot.Leader!.PeerId
                && !PartyRoster.Players(bot.Party).Any(c => c.PeerId == target)
                && !_bots.Any(b => b.Party == bot.Party && b.Phase == BotPhase.Following && b.PeerId == target))
            {
                lock (bot.Combat.Monsters)
                    if (!bot.Combat.Monsters.TryGetValue(target, out var hostile) || !hostile.Hostile)
                        throw new InvalidOperationException("Cast targets are limited to this party and its nearby enemies.");
            }
            if (action == "flask") _ = BotMcpProtocol.Integer(args, "flaskSlot", 1, 5);
            string fingerprint = JsonSerializer.Serialize(new { epoch, action, skill, target, x, y, flask, seconds });
            return lease.Accept(sequence, fingerprint, () =>
            {
                var conn = bot.Combat!;
                string command = Guid.NewGuid().ToString("N");
                CommandRules.QueueControlledIntent(conn, new(command, action, conn.Clock.Elapsed.TotalSeconds + seconds,
                    skill, target, x, y, flask, lease.Deadline));
                return new { botId = bot.CharacterId, commandId = command, sequence, status = "queued",
                    detail = "Observe lastCommand for execution; queuing does not certify a cast, hit or flask effect." };
            });
        }
    }

    static bool Ready(Bot b) => PartyRoster.ContainsCompanion(b.Party, b.Account) && Program.GameLogicOrNull is IControlledPlayerCommands && b.Phase == BotPhase.Following && b.Leader is { Controlled: false, Closed: false, LeavingArea: false, PlayerSent: true }
        && b.Combat is { Closed: false, LeavingArea: false, ControlledPrepared: true, PlayerSent: true }
        && ReferenceEquals(b.Combat.Instance, b.Leader.Instance);

    static void ExpireControl(Bot bot)
    {
        if (bot.Control is not { Active: false }) return;
        if (bot.Combat is { } conn) CommandRules.ClearControlledIntent(conn);
        bot.Control = null;
    }
}
