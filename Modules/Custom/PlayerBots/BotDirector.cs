using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

namespace GameServer.Modules.PlayerBots;

/// <summary>The module's settings (settings.json, beside module.json, with the user's values).</summary>
sealed record BotSettings(int MaxBots = 100, int SpawnIntervalSeconds = 60, int FirstSpawnSeconds = 60,
    string League = "Standard", int[]? QuestMinutes = null, int[]? TownActivities = null, int[]? IdleSeconds = null,
    double DeathChancePerQuest = 0.15, int StatusSeconds = 10,
    bool McpEnabled = false, int McpPort = 6194, int McpLeaseSeconds = 30);

/// <summary>One quest of the bots' guides (story-main.md, story-side.md).</summary>
sealed record BotQuest(string Act, string Town, string Id, string Name, string Where, bool Side)
{
    public static List<BotQuest> Route(string folder)
    {
        var main = Read(Path.Combine(folder, "story-main.md"), side: false);
        var side = Read(Path.Combine(folder, "story-side.md"), side: true);
        // Act by act: its main quests, then its side quests.
        return [.. main.Concat(side).GroupBy(q => q.Act).OrderBy(g => main.FindIndex(q => q.Act == g.Key) is var i and >= 0 ? i : int.MaxValue)
            .SelectMany(g => g.OrderBy(q => q.Side))];
    }

    static List<BotQuest> Read(string path, bool side) => !File.Exists(path) ? [] :
        [.. File.ReadAllLines(path).Where(l => l.StartsWith("| Act") || l.StartsWith("| Epilogue"))
            .Select(l => l.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray())
            .Where(c => c.Length >= 5 && c[2] != "Quest")
            .Select(c => new BotQuest(c[0], c[1], c[2], c[3], c[4], side))];
}

/// <summary>What a bot is doing now.</summary>
enum BotPhase { Arriving, Walking, Idling, Leaving, Questing, Following, Departing }

/// <summary>A bot: its account and character in the save database, its place in the route, and its body in a town.</summary>
sealed class Bot
{
    public required Guid AccountId, CharacterId;
    public required string Account, Name;
    public required CharacterClass Class;
    public required uint PeerId;
    public int Level = 1, QuestIndex, Deaths;
    public BotPhase Phase = BotPhase.Arriving;
    public string Doing = "создаётся";
    public string Town = "1_1_town";
    public double X, Y, TargetX, TargetY;
    public DateTime Until;
    public int ActivitiesLeft, Stuck;
    public string? TargetKind;
    /// <summary>A quick visit (in, hand the quest in to an NPC, out) or a stay (by the stash, a few things to do).</summary>
    public bool Quick;
    /// <summary>What it wears: base item Ids (BotGear), and the level it was picked at.</summary>
    public List<string> Gear = [];
    public int GearLevel;
    /// <summary>The town its X and Y are a place in (null: not placed in its town yet, as after a visit nobody saw).</summary>
    public string? PlacedIn;
    /// <summary>The instances this bot's body is published in (removed from them when it leaves).</summary>
    public readonly HashSet<InstanceState> Shown = [];
    /// <summary>The player whose party it is in (BotParty), and that party's id; the instance it was placed in there.</summary>
    public ClientState? Leader;
    /// <summary>The leader's character: an area change is a new connection, which the bot follows by this name.</summary>
    public string? LeaderName;
    /// <summary>When the leader's connection went without a new one yet (an area change in progress).</summary>
    public DateTime? LeaderGoneSince;
    public uint Party;
    public InstanceState? FollowIn;
    public ClientState? Combat;
    public Guid ControlEpoch = Guid.NewGuid();
    public BotControlLease? Control;
}

/// <summary>Creates bots up to the limit, one per interval, walks them through the route and shows them in the towns
/// where real players are: a bot is a published player (InstancePlayer without a connection) of each live instance of
/// its town, which every client there replicates like another player (LivingCollision.SyncInstancePlayers). Quests are
/// simulated out of sight; a bot that has done every quest of both guides is deleted and a new one takes its place.</summary>
sealed partial class BotDirector
{
    readonly BotSettings _settings;
    readonly List<BotQuest> _route;
    internal readonly BotLog _log;
    readonly Action<string> _console;
    readonly SqliteAccountStore _accounts = new(Saves.Db);
    internal readonly Random _random = new();
    internal readonly List<Bot> _bots = [];
    readonly CancellationTokenSource _stop = new();
    int _stopped;
    Task? _running;
    BotMcpServer? _mcp;
    static int _nextPeer = 0x38000000;
    // In one town at once at most MaxQuick bots on a quick visit and MaxStaying staying; the places they stand are at
    // least StandApart cells from each other's. Bodies do not collide (as players in the game).
    const int MaxQuick = 5, MaxStaying = 5, StandApart = 20;

    readonly string _statePath;
    readonly Dictionary<string, int> _townTargets = new(StringComparer.Ordinal);

    public BotDirector(BotSettings settings, List<BotQuest> route, BotLog log, Action<string> console, string statePath) =>
        (_settings, _route, _log, _console, _statePath) = (settings, route, log, console, statePath);

    public void Start()
    {
        Resume();
        foreach (string town in _route.Select(q => q.Town).Distinct(StringComparer.Ordinal))
            _townTargets[town] = _random.Next(2, 6);
        _nextParty = (int)Math.Max(PartyIdBase, _bots.Select(b => (long)b.Party).DefaultIfEmpty(PartyIdBase).Max());
        foreach (var party in _bots.Where(b => b.Leader is not null).GroupBy(b => b.Party))
            _ = SendPartyWhenReady(party.First().Leader!, party.Key);
        _running = Task.Run(Run);
        if (_settings.McpEnabled)
        {
            try { _mcp = new(this, _settings.McpPort, _console); }
            catch (Exception error) when (error is System.Net.HttpListenerException or IOException or UnauthorizedAccessException)
            { _console($"bots MCP not started: {error.Message}"); }
        }
    }

    // The bots a restart left: their progress from state.json; a marked bot account the file does not know is deleted.
    // Everything a bot was doing, so a restart picks it up where it was: the phase, its town and place, its target and the
    // time its quest or pause still had to run.
    sealed record SavedBot(Guid AccountId, Guid CharacterId, string Account, string Name, CharacterClass Class, int Level, int QuestIndex, int Deaths,
        BotPhase Phase = BotPhase.Questing, string? Town = null, double X = 0, double Y = 0, double TargetX = 0, double TargetY = 0,
        string? TargetKind = null, string? Doing = null, double SecondsLeft = 0, int ActivitiesLeft = 0,
        bool Quick = false, List<string>? Gear = null, int GearLevel = 0, string? LeaderName = null, uint Party = 0);

    void Resume()
    {
        var saved = File.Exists(_statePath) ? JsonSerializer.Deserialize<List<SavedBot>>(File.ReadAllText(_statePath)) ?? [] : [];
        foreach (var s in saved)
        {
            if (_accounts.GetCharacter(s.CharacterId) is null) continue;
            var bot = new Bot
            {
                AccountId = s.AccountId, CharacterId = s.CharacterId, Account = s.Account, Name = s.Name, Class = s.Class,
                PeerId = unchecked((uint)Interlocked.Increment(ref _nextPeer)), Level = s.Level, QuestIndex = s.QuestIndex, Deaths = s.Deaths,
                Phase = s.Phase, Town = s.Town ?? "1_1_town", X = s.X, Y = s.Y, TargetX = s.TargetX, TargetY = s.TargetY,
                TargetKind = s.TargetKind, Doing = s.Doing ?? "продолжает", ActivitiesLeft = s.ActivitiesLeft,
                Until = DateTime.UtcNow.AddSeconds(Math.Max(0, s.SecondsLeft)),
                Quick = s.Quick, Gear = s.Gear ?? [], GearLevel = s.GearLevel,
            };
            // A module reload retains a live party. Its members can have earned levels beyond
            // their last town's ambient range, which must not reset their build or experience.
            if (bot.Phase is BotPhase.Following or BotPhase.Departing)
            {
                var leader = bot.Phase == BotPhase.Following ? Program.InGame.Keys.FirstOrDefault(c => !c.Controlled
                    && !c.Closed && !c.LeavingArea && Program.CharacterName(c) == s.LeaderName) : null;
                if (leader is not null && s.Party >= PartyIdBase)
                    (bot.Leader, bot.LeaderName, bot.Party) = (leader, s.LeaderName, s.Party);
                else (bot.Phase, bot.Until, bot.Doing) = (BotPhase.Questing, DateTime.UtcNow, "идёт дальше по квестам");
            }
            if (bot.Leader is null)
            {
                bot.Level = LevelForTown(bot.Level, bot.Town, _random);
            }
            if (NeedsBuildOnResume(bot, CharacterSaves.ModuleData(bot.Name, "PlayerBots.BuildVersion"),
                CharacterSaves.Load(bot.Name)?.Experience))
            { Dress(bot); SaveProgress(bot); }
            _bots.Add(bot);
            // A bot an earlier version left waiting for room in a town goes on with its quests.
            if (bot.Doing.StartsWith("ждёт", StringComparison.Ordinal))
                (bot.Phase, bot.Until, bot.Doing) = (BotPhase.Questing, DateTime.UtcNow, "идёт дальше по квестам");
            _log.Write(bot.Account, bot.Name, $"сервер перезапущен: продолжает с того же места — {bot.Doing}");
        }
        // A restart may bring back more bots in a town than it takes: the extra ones go on with their quests.
        foreach (var town in _bots.Where(b => b.Leader is null && b.Phase != BotPhase.Questing).GroupBy(b => b.Town).ToList())
            foreach (var extra in town.Skip(5).ToList())
            {
                _log.Write(extra.Account, extra.Name, $"после перезапуска в городе {extra.Town} нет места: идёт дальше по квестам");
                BeginQuest(extra, DateTime.UtcNow);
            }
        var known = _bots.Select(b => b.AccountId).ToHashSet();
        foreach (var orphan in _accounts.Accounts().Where(a => IsBot(a) && !known.Contains(a.Id)))
        {
            foreach (var c in _accounts.CharactersOf(orphan.Id)) _accounts.DeleteCharacter(c.Id);
            DeleteAccount(orphan.Id);
            _log.Write(orphan.Name, "-", "аккаунт бота без состояния удалён");
        }
        if (_bots.Count > 0) _console($"bots: resumed {_bots.Count}");
    }

    // The mark a bot account carries in place of a password hash. A real hash is base64 (AccountService), which never
    // holds ':', so no player's account, whatever its name ("some_bot" too), can be taken for a bot's and deleted.
    const string BotMark = "PlayerBots:";
    static bool IsBot(Account account) => account.PasswordHash.StartsWith(BotMark, StringComparison.Ordinal);

    void SaveState()
    {
        List<SavedBot> bots;
        var now = DateTime.UtcNow;
        lock (_bots) bots = [.. _bots.Select(b => new SavedBot(b.AccountId, b.CharacterId, b.Account, b.Name, b.Class, b.Level, b.QuestIndex, b.Deaths,
            b.Phase, b.Town, b.X, b.Y, b.TargetX, b.TargetY, b.TargetKind, b.Doing, (b.Until - now).TotalSeconds, b.ActivitiesLeft,
            b.Quick, b.Gear, b.GearLevel, b.LeaderName, b.Party))];
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath + ".tmp", JsonSerializer.Serialize(bots, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_statePath + ".tmp", _statePath, overwrite: true);
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _mcp?.Dispose();
        _stop.Cancel();
        _running?.GetAwaiter().GetResult();
        lock (_bots) foreach (var bot in _bots) { bot.Control = null; StopCombat(bot); Hide(bot); }
        try { SaveState(); } catch (IOException) { }
    }

    async Task Run()
    {
        var nextSpawn = DateTime.UtcNow.AddSeconds(_settings.FirstSpawnSeconds);
        var nextStatus = DateTime.UtcNow;
        _console($"bots: up to {_settings.MaxBots}, one every {_settings.SpawnIntervalSeconds} s (first in {_settings.FirstSpawnSeconds} s), route of {_route.Count} quest(s)");
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (now >= nextSpawn)
                {
                    nextSpawn = now.AddSeconds(_settings.SpawnIntervalSeconds);
                    int count;
                    lock (_bots) count = _bots.Count;
                    if (count < _settings.MaxBots) Spawn();
                }
                var towns = LiveTowns();
                EnsureTownPopulation();
                lock (_bots)
                    foreach (var bot in _bots.ToList())
                    {
                        try { Step(bot, now, 0.1, towns); }
                        catch (Exception error) { _log.Error(bot.Account, bot.Name, error); bot.Until = now.AddSeconds(5); }
                    }
                Bot[] followers;
                lock (_bots) followers = _bots.Where(b => b.Phase == BotPhase.Following).ToArray();
                foreach (var bot in followers)
                    try { await StepCombat(bot); }
                    catch (Exception error) { StopCombat(bot); _log.Error(bot.Account, bot.Name, error); }
                if (now >= nextStatus)
                {
                    nextStatus = now.AddSeconds(_settings.StatusSeconds);
                    lock (_bots) _log.Status(_bots.Select(b =>
                        $"{b.Account,-30} {b.Name,-24} ур. {b.Level,2}  {Act(b),-32} {b.Doing}"));
                    SaveState();
                }
            }
            catch (Exception error) { _console($"bots: {error.Message}"); }
            try { await Task.Delay(100, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
        }
    }

    string Act(Bot b) => b.QuestIndex < _route.Count ? _route[b.QuestIndex].Act : "сюжет пройден";

    // ---- accounts and characters ----

    void Spawn(string? town = null)
    {
        string name = BotNames.Pick(_random, n => _accounts.FindCharacter(n) is not null || _accounts.FindAccount(n + "_bot") is not null);
        var cls = (CharacterClass)_random.Next(Enum.GetValues<CharacterClass>().Length);
        var now = DateTimeOffset.UtcNow;
        // No password anyone knows: a bot never logs in through Auth. Its hash is the bot mark (IsBot), not a real hash.
        var account = new Account(Guid.NewGuid(), name + "_bot", BotMark + Guid.NewGuid().ToString("N"),
            Convert.ToBase64String(Guid.NewGuid().ToByteArray()), now);
        _accounts.AddAccount(account);
        town ??= "1_1_town";
        var range = LevelsForTown(town);
        int level = _random.Next(range.Min, range.Max + 1);
        var character = new Character(Guid.NewGuid(), account.Id, name, cls, level, _settings.League, town, now, now, town);
        _accounts.AddCharacter(character);
        var bot = new Bot
        {
            AccountId = account.Id, CharacterId = character.Id, Account = account.Name, Name = name, Class = cls,
            PeerId = unchecked((uint)Interlocked.Increment(ref _nextPeer)),
            Level = level, Town = town, QuestIndex = Math.Max(0, _route.FindIndex(q => q.Town == town)),
        };
        Dress(bot);
        lock (_bots) _bots.Add(bot);
        _log.Write(bot.Account, bot.Name, $"создан: {cls}, лига {_settings.League}; экипировка: {BotGear.Describe(bot.Gear)}; начинает сюжет на Побережье");
        _console($"bots: created {bot.Name} ({cls}), {_bots.Count}/{_settings.MaxBots}");
        bot.Phase = BotPhase.Arriving;
        bot.ActivitiesLeft = 3;
    }

    void Retire(Bot bot)
    {
        Hide(bot);
        _bots.Remove(bot);
        try
        {
            _accounts.DeleteCharacter(bot.CharacterId);
            DeleteAccount(bot.AccountId);
            _log.Write(bot.Account, bot.Name, $"прошёл весь сюжет и все побочные квесты (ур. {bot.Level}, смертей {bot.Deaths}): персонаж и аккаунт удалены");
        }
        catch (Exception error) { _log.Error(bot.Account, bot.Name, error); }
    }

    static void DeleteAccount(Guid id)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Saves.Db.AuthPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM accounts WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    void SaveProgress(Bot bot)
    {
        if (_accounts.GetCharacter(bot.CharacterId) is { } c)
            _accounts.UpdateCharacter(c with { Level = bot.Level, LastTown = bot.Town, LastPlayedUtc = DateTimeOffset.UtcNow });
    }

    // ---- the route ----

    internal void BeginQuest(Bot bot, DateTime now)
    {
        if (bot.QuestIndex >= _route.Count) { Retire(bot); return; }
        var quest = _route[bot.QuestIndex];
        Hide(bot);
        bot.Town = quest.Town;
        int nextLevel = LevelForTown(bot.Level, bot.Town, _random);
        if (nextLevel != bot.Level) { bot.Level = nextLevel; Dress(bot); SaveProgress(bot); }
        bot.Phase = BotPhase.Questing;
        int[] minutes = _settings.QuestMinutes is [var lo, var hi] ? [lo, hi] : [2, 6];
        bot.Until = now.AddSeconds(_random.Next(minutes[0] * 60, minutes[1] * 60 + 1));
        bot.Doing = $"квест «{quest.Name}» ({(quest.Side ? "побочный" : "сюжет")}): {quest.Where}";
        _log.Write(bot.Account, bot.Name, $"взял квест {quest.Id} «{quest.Name}», открывает портал: {quest.Where}");
    }

    void FinishQuest(Bot bot, DateTime now)
    {
        var quest = _route[bot.QuestIndex];
        if (_random.NextDouble() < _settings.DeathChancePerQuest)
        {
            bot.Deaths++;
            _log.Write(bot.Account, bot.Name, $"погиб на квесте «{quest.Name}» ({quest.Where}); воскресает в городе и пробует снова");
        }
        else
        {
            int gained = quest.Side ? 1 : _random.Next(1, 3);
            bot.Level = Math.Min(LevelsForTown(bot.Town).Max, bot.Level + gained);
            _log.Write(bot.Account, bot.Name, $"выполнил квест {quest.Id} «{quest.Name}», уровень {bot.Level}");
            Loot(bot);
            bot.QuestIndex++;
            SaveProgress(bot);
        }
        // Into town to hand the quest in, quickly, or to stay a while by the stash; when the town has no room for either, on
        // to the next quest at once (it hands this one in on a later visit).
        bot.Quick = _random.Next(10) < 7;
        if (InTown(bot) >= Cap(bot)) bot.Quick = !bot.Quick;
        if (InTown(bot) >= Cap(bot))
        {
            _log.Write(bot.Account, bot.Name, $"в городе {bot.Town} нет места: не заходит, идёт дальше по квестам");
            BeginQuest(bot, now);
            return;
        }
        bot.Phase = BotPhase.Arriving;
        int[] activities = _settings.TownActivities is [var lo, var hi] ? [lo, hi] : [2, 4];
        bot.ActivitiesLeft = bot.Quick ? 1 : _random.Next(activities[0], activities[1] + 1);
        bot.Doing = bot.Quick ? "прилетает в город сдать квест" : "возвращается в город";
    }

    int InTown(Bot bot) => _bots.Count(b => b != bot && b.Town == bot.Town && b.Leader is null && b.Phase != BotPhase.Questing);
    static int Cap(Bot bot) => 5;

    // What the out-of-sight quest did, as the log tells it: the zone cleared and its kills (simulated), and new gear when
    // the level has grown enough for better bases (real: the model shows it from the next town visit).
    void Loot(Bot bot)
    {
        var quest = _route[bot.QuestIndex];
        _log.Write(bot.Account, bot.Name, $"зачистил зону ({quest.Where}): убито монстров {_random.Next(40, 220)}");
        if (bot.Level != bot.GearLevel)
        {
            Dress(bot);
            _log.Write(bot.Account, bot.Name, $"надел предметы лучше (ур. {bot.Level}): {BotGear.Describe(bot.Gear)}");
        }
        string[] events =
        [
            "вставил камень умения в гнездо", "переставил камни поддержки, сделал связь", "использовал сферу златокузнеца на кольцо",
            "перекрасил гнёзда хроматической сферой", "добавил гнездо сферой мастерства", "соединил гнёзда сферой соединения",
            "подобрал валюту и карту",
        ];
        if (_random.Next(3) == 0) _log.Write(bot.Account, bot.Name, events[_random.Next(events.Length)]);
    }

    void Dress(Bot bot)
    {
        bot.Gear = BotGear.Pick(bot.Class, bot.Level, _random);
        bot.GearLevel = bot.Level;
        if (Program.GameLogicOrNull is IControlledPlayerRules rules)
        {
            var build = rules.BuildControlled(bot.Class, bot.Level);
            Saves.Atomically(() =>
            {
                CharacterSaves.Write(bot.Name, build);
                PlayerInventories.Invalidate(bot.Name);
                CharacterEquipmentState.Replaced(bot.Name);
            });
            bot.Gear = build.Inventories.Values.SelectMany(i => i).Select(i => Program.Items.ByHash(i.Item.Type)?.Id)
                .Where(id => id is not null).Select(id => id!).ToList();
        }
    }

    internal static bool NeedsBuildOnResume(Bot bot, string? version, uint? experience) =>
        bot.Gear.Count == 0 || version != "1" || bot.Leader is null && (bot.GearLevel != bot.Level
            || experience is uint xp && (xp < CharacterStats.ExperienceFor(bot.Level)
                || xp >= CharacterStats.ExperienceFor(bot.Level + 1)));

    // ---- the town ----

    void Step(Bot bot, DateTime now, double seconds, Dictionary<string, List<ClientState>> towns)
    {
        if (bot.Phase is BotPhase.Following or BotPhase.Departing) { StepWithLeader(bot, now, seconds); return; }
        if (bot.Phase == BotPhase.Questing)
        {
            if (now >= bot.Until) FinishQuest(bot, now);
            return;
        }
        towns.TryGetValue(bot.Town, out var observers);
        var layout = observers?.FirstOrDefault()?.Layout;
        switch (bot.Phase)
        {
            case BotPhase.Arriving:
                if (layout is not null)
                {
                    // Out of its own portal right at its place (by the stash, the guild stash or an NPC), as a player's town
                    // portal puts it where it is going: nobody gathers at the waypoint.
                    (bot.X, bot.Y) = PickSpot(bot, observers!.First(), out var kind);
                    (bot.TargetX, bot.TargetY, bot.TargetKind, bot.PlacedIn) = (bot.X, bot.Y, kind, bot.Town);
                    bot.ActivitiesLeft--;
                    _log.Write(bot.Account, bot.Name, $"вышел из портала в городе {bot.Town}");
                    Arrive(bot, now);
                }
                else
                {
                    bot.PlacedIn = null;
                    NextActivity(bot, now, observers);
                }
                break;
            case BotPhase.Walking:
                if (layout is null || Walk(bot, layout, seconds)) Arrive(bot, now);
                break;
            case BotPhase.Idling:
                if (now >= bot.Until) NextActivity(bot, now, observers);
                break;
            case BotPhase.Leaving:
                if (layout is null || Walk(bot, layout, seconds)) { BeginQuest(bot, now); return; }
                break;
        }
        if (layout is not null && bot.PlacedIn != bot.Town && bot.Phase != BotPhase.Questing)
        {
            var any = observers!.First();
            (bot.X, bot.Y) = PickSpot(bot, any, out var kind);
            (bot.TargetX, bot.TargetY, bot.TargetKind, bot.PlacedIn) = (bot.X, bot.Y, kind, bot.Town);
            if (bot.Phase is BotPhase.Walking) Arrive(bot, now);
        }
        Show(bot, observers);
    }

    void NextActivity(Bot bot, DateTime now, List<ClientState>? observers)
    {
        var any = observers?.FirstOrDefault();
        if (bot.ActivitiesLeft-- <= 0)
        {
            // Out through a portal where it stands, as players leave town: nobody queues at the waypoint.
            bot.Phase = BotPhase.Leaving;
            bot.Doing = "открывает портал";
            (bot.TargetX, bot.TargetY) = (bot.X, bot.Y);
            return;
        }
        bot.Phase = BotPhase.Walking;
        bot.Stuck = 0;
        if (any is null) { bot.TargetKind = "spot"; (bot.TargetX, bot.TargetY) = (bot.X, bot.Y); }
        else { (bot.TargetX, bot.TargetY) = PickSpot(bot, any, out var kind); bot.TargetKind = kind; }
        bot.Doing = bot.TargetKind switch { "npc" => "идёт к NPC", "stash" => "идёт к тайнику", "object" => "идёт по городу", _ => "бродит по городу" };
    }

    static bool Walkable(AreaLayout layout, double x, double y) => layout.Walkable?.Invoke((int)x, (int)y) ?? true;

    // A walkable place in the town, at least StandApart cells from where every other bot of the town stands or is going:
    // mostly in a ring around the stash and the guild stash (as players stand by theirs), else by an NPC, else anywhere
    // walkable within reach of the waypoint. A quick visit goes to an NPC.
    (double X, double Y) PickSpot(Bot bot, ClientState town, out string kind)
    {
        var layout = town.Layout;
        var spots = town.StationaryBlockers;
        var stashes = spots.Where(b => b.Size == ClientState.StashBlockerSize).ToList();
        var npcs = spots.Where(b => b.Size == ClientState.NpcBlockerSize).ToList();
        var taken = _bots.Where(b => b != bot && b.Town == bot.Town && b.Phase != BotPhase.Questing && b.PlacedIn == bot.Town)
            .SelectMany(b => new[] { (b.X, b.Y), (b.TargetX, b.TargetY) }).ToList();
        (int X, int Y) centre = layout.Waypoint is { } wp ? (wp.GridX, wp.GridY) : (layout.SpawnX, layout.SpawnY);
        (double X, double Y) best = (bot.X, bot.Y);
        kind = "spot";
        for (int attempt = 0; attempt < 60; attempt++)
        {
            int roll = _random.Next(10);
            string k;
            double x, y;
            if (!bot.Quick && stashes.Count > 0 && roll < 8)
            {
                var stash = stashes[_random.Next(stashes.Count)];
                double angle = _random.NextDouble() * 2 * Math.PI, radius = _random.Next(12, 30);
                (k, x, y) = ("stash", stash.X + Math.Cos(angle) * radius, stash.Y + Math.Sin(angle) * radius);
            }
            else if (npcs.Count > 0 && (bot.Quick || roll < 9))
            {
                var npc = npcs[_random.Next(npcs.Count)];
                double angle = _random.NextDouble() * 2 * Math.PI, radius = _random.Next(npc.Size + 4, npc.Size + 14);
                (k, x, y) = ("npc", npc.X + Math.Cos(angle) * radius, npc.Y + Math.Sin(angle) * radius);
            }
            else
                (k, x, y) = ("spot", centre.X + _random.Next(-120, 121), centre.Y + _random.Next(-120, 121));
            if (x < 1 || y < 1 || layout.GridW > 0 && x >= layout.GridW - 1 || layout.GridH > 0 && y >= layout.GridH - 1 || !Walkable(layout, x, y)) continue;
            (best, kind) = ((x, y), k);
            if (taken.All(t => Math.Max(Math.Abs(t.Item1 - x), Math.Abs(t.Item2 - y)) >= StandApart)) break;
        }
        return best;
    }

    void Arrive(Bot bot, DateTime now)
    {
        int[] idle = _settings.IdleSeconds is [var lo, var hi] ? [lo, hi] : [8, 75];
        bot.Phase = BotPhase.Idling;
        bot.Until = now.AddSeconds(bot.Quick ? _random.Next(3, 9) : _random.Next(idle[0], idle[1] + 1));
        var quest = bot.QuestIndex > 0 ? _route[bot.QuestIndex - 1] : null;
        bot.Doing = bot.TargetKind switch
        {
            "npc" => quest is not null && (bot.Quick || _random.Next(2) == 0) ? $"сдаёт квест «{quest.Name}» NPC" : "разговаривает с NPC",
            "stash" => _random.Next(2) == 0 ? "у сундука: крафтит предметы" : "у сундука: перекладывает вещи",
            _ => "стоит и отдыхает",
        };
        _log.Write(bot.Account, bot.Name, $"{bot.Town}: {bot.Doing}");
    }

    // A step toward the target, cell by cell on the town's walk map; true when there, or after being stuck a while (which
    // gives up and stands where it is). Bodies do not block (as players in the game).
    internal static bool Walk(Bot bot, AreaLayout layout, double seconds, double speed = 1)
    {
        double dx = bot.TargetX - bot.X, dy = bot.TargetY - bot.Y, distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < 2) { (bot.X, bot.Y) = (bot.TargetX, bot.TargetY); return true; }
        double step = Math.Min(distance, Program.WalkSpeed * speed * seconds);
        double nx = bot.X + dx / distance * step, ny = bot.Y + dy / distance * step;
        bool Free(double x, double y) => layout.Walkable?.Invoke((int)x, (int)y) ?? true;
        (double X, double Y)[] tries = [(nx, ny), (nx, bot.Y), (bot.X, ny),
            (bot.X - dy / distance * step, bot.Y + dx / distance * step), (bot.X + dy / distance * step, bot.Y - dx / distance * step)];
        foreach (var (x, y) in tries)
            if (Free(x, y)) { (bot.X, bot.Y) = (x, y); bot.Stuck = 0; return false; }
        if (++bot.Stuck > 30) { (bot.TargetX, bot.TargetY) = (bot.X, bot.Y); return true; }
        return false;
    }

    // ---- being seen ----

    // The towns players are in now: every live in-game connection by its area.
    static Dictionary<string, List<ClientState>> LiveTowns()
    {
        var towns = new Dictionary<string, List<ClientState>>();
        foreach (var conn in Program.InGame.Keys)
        {
            if (conn.Closed || conn.LeavingArea || !conn.PlayerSent || conn.Instance is null) continue;
            if (!conn.Layout.Id.Contains("_town", StringComparison.Ordinal)) continue;
            (towns.TryGetValue(conn.Layout.Id, out var list) ? list : towns[conn.Layout.Id] = []).Add(conn);
        }
        return towns;
    }

    internal void Show(Bot bot, List<ClientState>? observers)
    {
        var instances = (observers ?? []).Select(c => c.Instance).Distinct().ToHashSet();
        foreach (var gone in bot.Shown.Where(i => !instances.Contains(i)).ToList()) { gone.RemovePlayer(bot.PeerId); bot.Shown.Remove(gone); }
        if (instances.Count == 0) return;
        var template = Template(bot);
        foreach (var instance in instances)
        {
            instance.PublishPlayer(new InstancePlayer(bot.PeerId, template.Type, template.Player, (int)bot.X, (int)bot.Y, template.Player.Life)
            {
                // Walking: observers start one Move action to the target, which their client animates and paths.
                Destination = bot.Phase is BotPhase.Walking or BotPhase.Leaving or BotPhase.Following or BotPhase.Departing
                    ? ((int)bot.TargetX, (int)bot.TargetY) : ((int)bot.X, (int)bot.Y),
            });
            bot.Shown.Add(instance);
        }
    }

    internal static void Hide(Bot bot)
    {
        foreach (var instance in bot.Shown) instance.RemovePlayer(bot.PeerId);
        bot.Shown.Clear();
    }

    internal static (int Min, int Max) LevelsForTown(string town)
    {
        int act = town switch
        {
            "1_1_town" => 1, "1_2_town" => 2, "1_3_town" => 3, "1_4_town" => 4, "1_5_town" => 5,
            "2_6_town" => 6, "2_7_town" => 7, "2_8_town" => 8, "2_9_town" => 9, "2_10_town" => 10, _ => 0,
        };
        if (act == 0) return (68, 80);
        if (act == 1) return (1, 3);
        if (act == 10) return (59, 75);
        int center = 2 + (int)Math.Round((act - 1) * 65.0 / 9, MidpointRounding.AwayFromZero);
        return (center - 5, center + 5);
    }

    internal static int LevelForTown(int current, string town, Random random)
    {
        var range = LevelsForTown(town);
        return current >= range.Min && current <= range.Max ? current : random.Next(range.Min, range.Max + 1);
    }

    void EnsureTownPopulation()
    {
        foreach (var (town, wanted) in _townTargets)
        {
            int present;
            lock (_bots) present = _bots.Count(b => b.Town == town && b.Leader is null && b.Phase is not (BotPhase.Questing or BotPhase.Leaving));
            while (present++ < wanted)
            {
                Bot? visitor;
                lock (_bots) visitor = _bots.Where(b => b.Leader is null && b.Phase == BotPhase.Questing)
                    .OrderByDescending(b => b.Town == town).FirstOrDefault();
                if (visitor is null)
                {
                    lock (_bots) if (_bots.Count >= _settings.MaxBots) break;
                    Spawn(town);
                }
                else
                {
                    if (visitor.Town != town)
                    {
                        visitor.Town = town; visitor.QuestIndex = Math.Max(0, _route.FindIndex(q => q.Town == town));
                        visitor.Level = LevelForTown(visitor.Level, town, _random);
                        Dress(visitor); SaveProgress(visitor);
                    }
                    visitor.Phase = BotPhase.Arriving; visitor.ActivitiesLeft = 3;
                }
            }
        }
    }

    static void StopCombat(Bot bot)
    {
        if (bot.Combat is { } old) { (Program.GameLogicOrNull as IControlledPlayerCommands)?.ClearControlledIntent(old); ControlledPlayers.Close(old); }
        bot.Combat = null;
    }

    async Task StepCombat(Bot bot)
    {
        lock (_bots) ExpireControl(bot);
        var leader = bot.Leader;
        if (_stop.IsCancellationRequested || bot.Phase != BotPhase.Following || leader is not { Closed: false, PlayerSent: true }
            || leader.LeavingArea || Program.GameLogicOrNull is not IControlledPlayerRules rules)
        { StopCombat(bot); return; }
        if (bot.Combat is null || bot.Combat.Closed || !ReferenceEquals(bot.Combat.Instance, leader.Instance))
        {
            StopCombat(bot);
            Hide(bot);
            var record = _accounts.GetCharacter(bot.CharacterId);
            if (record is null) return;
            var player = ControlledPlayers.Open(record, bot.Account, leader);
            bot.Combat = player;
            bot.ControlEpoch = Guid.NewGuid();
            await rules.InitializeControlled(player, leader);
            if (_stop.IsCancellationRequested || bot.Phase != BotPhase.Following || !ReferenceEquals(bot.Leader, leader))
            { StopCombat(bot); return; }
            bot.PeerId = player.PeerId;
        }
        var current = bot.Combat;
        if (current is null) return;
        await rules.StepControlled(current, leader);
        if (bot.Phase != BotPhase.Following || !ReferenceEquals(bot.Combat, current)) return;
        var position = current.Walk.Position(current.Clock.Elapsed.TotalSeconds);
        (bot.X, bot.Y, bot.Level) = (position.X, position.Y, current.Pools.Level);
    }

    // The player object other clients create for the bot: its class's body, name and pools; no gear drawn yet.
    // Stats.dat use_town_stance (row 21140; a stat key is its row + 1): the relaxed stance and walk players have in towns.
    const uint TownStance = 21141;

    internal static (uint Type, PlayerObject Player) Template(Bot bot)
    {
        var cls = Program.Skills.ClassFor(Program.Skills.RowOfClass((int)bot.Class));
        uint life = (uint)(cls.BaseMaxLife + 12 * (bot.Level - 1)), mana = (uint)(cls.BaseMaxMana + 6 * (bot.Level - 1));
        var player = new PlayerObject(bot.Name, CharacterClassByte.Encode((byte)cls.IntegerId, 0), (int)bot.X, (int)bot.Y, life, mana,
            Experience: CharacterStats.ExperienceFor(bot.Level),
            AnimatedObject: cls.AnimatedObject, Skills: [], Stats: bot.Town.Contains("_town", StringComparison.Ordinal) ? [(TownStance, 1)] : [],
            Equipped: BotGear.Looks(bot.Gear));
        return (InstanceEntities.ObjectTypeHash(cls.ObjectType), player);
    }
}
