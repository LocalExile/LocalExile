using System.Diagnostics;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Accounts;
using GameServer.Storage;
using GameServer.Core.Diagnostics;
using WalkEstimator = GameServer.Core.World.WalkEstimator;

public partial class Program
{
    static readonly Lazy<PacketLogQueue> PacketSummaries = new(() => new(Console.WriteLine));
    public static string CharacterName(Player player) => string.IsNullOrEmpty(player.Character.Name) ? "Exile" : player.Character.Name;

    /// <summary>Whether <paramref name="player"/> owns the hideout instance they are in (same character or same account).</summary>
    public static bool OwnsHideoutInstance(Player player)
    {
        if (HideoutCatalog.ByArea(player.Layout.Hash) is null) return false;
        if (player.InstanceOwner is null || string.Equals(player.InstanceOwner, CharacterName(player), StringComparison.OrdinalIgnoreCase))
            return true;
        if (player.Character.Account is null || !Saves.IsOpen) return false;
        var store = new SqliteAccountStore(Saves.Db);
        return store.FindCharacter(player.InstanceOwner) is { } owner
            && string.Equals(store.GetAccount(owner.AccountId)?.Name, player.Character.Account, StringComparison.OrdinalIgnoreCase);
    }

    // Set while a projectile's output is sent: false drops it (Send).
    public static readonly AsyncLocal<Func<bool>?> ProjectileOutputGuard = new();

    /// <summary>The player whose work this is: set by their handlers (Handle) and the loops and tasks run as theirs,
    /// and flows into what those start. What a player causes in the area can name them as its source by it (a monster's
    /// ailment); the area's own systems run with none.</summary>
    public static readonly AsyncLocal<Player?> ActingPlayer = new();

    public static void SaveUnsavedProgressNow(string? why)
    {
        try
        {
            HardcoreDeathPersistence.RetryPending(Saves.Db);
            CharacterSaves.SaveExperience();
            PlayerInventories.SaveAllUnsaved();
            Saves.FlushAll();
            if (why is not null) Console.WriteLine($"[gateway] unsaved progress saved ({why})");
        }
        catch (Exception e)
        {
            // Kept in memory: the next save tries again.
            Console.WriteLine($"[gateway] unsaved progress not saved{(why is null ? "" : $" ({why})")}: {ServerLog.Describe(e)}");
        }
    }

    public static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }

    /// <summary>The destination instance's resolved area rules, playable seed and layout: prepared during a transfer
    /// while the client displays "Entering &lt;area&gt;", then reused when the client connects during its loading screen.</summary>
    public sealed record PreparedArea(InstanceState Instance, MapDevice.Run? Map, byte AreaLevel,
        IReadOnlyDictionary<uint, int> AreaStats, int LeagueCategory,
        IReadOnlyList<(ushort Plugin, byte[] State)> PluginStates, uint Seed, AreaLayout Layout);

    /// <summary>Prepares the destination instance's map, area stats, league category, terrain plugin states, playable
    /// seed and generated layout before S→C 0x27 opens the client's loading screen.</summary>
    public static PreparedArea PrepareTransferArea(ulong instanceId, long created, string? soloOwner, uint ticket,
        ushort areaHash, uint authSeed, string characterName, string? league, string? leagueForm) =>
        PrepareInstanceArea(Instances.Prepare(instanceId, created, soloOwner), ticket, areaHash, instanceId, authSeed,
            characterName, league, leagueForm);

    /// <summary>Resolves or reuses the area rules, playable seed and layout for <paramref name="instance"/>.</summary>
    public static PreparedArea PrepareInstanceArea(InstanceState instance, uint ticket, ushort areaHash, ulong instanceId,
        uint authSeed, string characterName, string? league, string? leagueForm)
    {
        var map = MapDevice.Admit(instance, ticket, areaHash, instanceId);
        // An opened map's level goes in 0x1d (0: the client takes the area's WorldAreas level).
        byte areaLevel = map is { Area.Level: > 0 } ? (byte)Math.Clamp(map.Area.Level, 1, 100) : (byte)0;
        // The area stats the instance is played with (0x1d's first list, InstanceEvents.AreaStats): the client
        // generates its level with them, so the Gateway does too. Composed by the instance's first preparation.
        var areaLeague = ServerLeagues.Resolve(leagueForm, league, LeagueCatalog.Startup);
        var areaRequest = new AreaStatsRequest(characterName, league, areaHash, map);
        var areaStats = instance.AreaStats(() => InstanceEvents.ComposeAreaStats(areaLeague, areaRequest));
        // The league content category (0x1d +0x28, InstanceEvents.LeagueCategory): the client picks the topology by it.
        int leagueCategory = instance.LeagueCategory(() => InstanceEvents.ComposeLeagueCategory(areaLeague, areaRequest));
        // The terrain plugin states (0x1d extra data, InstanceEvents.TerrainPluginStates), which a plugin builds its part
        // of the level from. A Labyrinth room's extra data is its node, so it gets none.
        var pluginStates = LabyrinthDay.DifficultyOf(AreaLayouts.IdOf(areaHash)) is not null ? []
            : instance.TerrainPluginStates(() => InstanceEvents.ComposeTerrainPluginStates(areaLeague, areaRequest));
        // The client generates the area at the seed, level, stats, category and plugin states of 0x1d: a seed the
        // Gateway can generate too. Cached on the instance so a transfer that pre-generated it while the client showed
        // "Entering <area>" hands the finished layout straight to HandleAsync without regenerating on the loading screen.
        var (seed, layout) = instance.PreparedLayout(authSeed, areaLevel, () =>
        {
            uint playable = AreaLayouts.PlayableSeed(areaHash, authSeed, areaLevel, areaStats, leagueCategory, pluginStates);
            return (playable, AreaLayouts.For(areaHash, playable));
        }, knownSeed => AreaLayouts.For(areaHash, knownSeed), Instances.Now);
        return new(instance, map, areaLevel, areaStats, leagueCategory, pluginStates, seed, layout);
    }

    public static async Task HandleAsync(TcpClient client, InstanceKeyring keyring)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        // Nagle off: it would hold a small 0x1c back until the previous write is acknowledged (up to ~200 ms, issue #46).
        client.NoDelay = true;
        Console.WriteLine($"[gateway] CONNECT {remote} at {DateTime.Now:HH:mm:ss.fff}");
        long total = 0;
        uint ticket = 0;
        Player? left = null;
        AreaFrames? attached = null;
        InstanceState? entered = null;
        bool prepared = false;
        (string Character, string? Account, string? League)? pinnedSession = null;
        long connectedAt = Stopwatch.GetTimestamp();
        string? reason = null;   // why the connection ended, for the DISCONNECT line
        bool vanished = false;   // the client closed it outside an area change or exit
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var buffer = new byte[8192];
                var pending = new List<byte>();
                InstanceOpening? opening = null;
                while (opening is null)
                {
                    int n = await stream.ReadAsync(buffer);
                    if (n == 0) return;
                    total += n;
                    pending.AddRange(buffer.AsSpan(0, n));
                    opening = InstanceProtocol.TryReadOpening([.. pending]);
                }
                if (GameLogicOrNull is null)
                {
                    Console.WriteLine($"[gateway] {remote} the game module is not loaded; connection refused");
                    return;
                }
                Console.WriteLine($"[gateway] {remote} opening: ticket=0x{opening.Ticket:x8} digest={Convert.ToHexString(opening.Digest)} " +
                                  $"address=0x{opening.Address:x8} u32={opening.U32_128} realm=0x{opening.Realm:x2} flag={opening.Flag}");
                if (!InstanceProtocol.VerifyOpeningDigest(opening, keyring.Data8(opening.Ticket), DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                {
                    Console.WriteLine($"[gateway] {remote} opening digest mismatch for ticket 0x{opening.Ticket:x8}; connection refused");
                    return;
                }
                ticket = opening.Ticket;

                // Which area and instance this ticket was issued for (Auth's instance registry).
                var resolved = await InstanceControl.CallAsync(new ControlRequest { Op = "resolve", Ticket = ticket });
                if (!resolved.Ok || string.IsNullOrEmpty(resolved.Character))
                {
                    // Without the character, its progress would go to another save: refuse instead.
                    Console.WriteLine($"[gateway] {remote} resolve failed ({resolved.Error ?? "no character"}); connection refused");
                    return;
                }
                // A prior lethal write may have failed while Auth still saw the source HC league. Retry it before any
                // world state is sent, then independently reject that now-stale ticket. Normal saves never revive HC.
                HardcoreDeathPersistence.RetryPendingCharacter(Saves.Db, resolved.CharacterId);
                var canonicalCharacter = new SqliteAccountStore(Saves.Db).FindCharacter(resolved.Character);
                if (canonicalCharacter is null ||
                    CharacterBuildImports.IsApplying(canonicalCharacter.Id) ||
                    !HardcoreDeathRules.TicketMatches(canonicalCharacter, resolved.CharacterId, resolved.League))
                {
                    Console.WriteLine($"[gateway] {remote} character is dead or its league changed; connection refused");
                    return;
                }
                ushort areaHash = resolved.Area;
                ulong instanceId = resolved.Instance;
                var instance = Instances.Enter(instanceId, resolved.Created, resolved.Ssf ? resolved.CharacterId?.ToString("N") : null);
                entered = instance;
                string characterName = string.IsNullOrEmpty(resolved.Character) ? "Exile" : resolved.Character;
                var preparedArea = PrepareInstanceArea(instance, ticket, areaHash, instanceId, resolved.Seed,
                    characterName, resolved.League, resolved.LeagueForm);
                byte areaLevel = preparedArea.AreaLevel;
                var areaStats = preparedArea.AreaStats;
                int leagueCategory = preparedArea.LeagueCategory;
                var pluginStates = preparedArea.PluginStates;
                uint seed = preparedArea.Seed;
                var layout = preparedArea.Layout;
                AreaLayouts.Announce(layout, seed);
                Console.WriteLine($"[gateway] {remote} ticket 0x{ticket:x8}: {resolved.Character ?? "?"} → instance {instanceId}, {layout.Describe()}");

                ConnectionCiphers ciphers = keyring.Ciphers(opening.Ticket);
                await stream.WriteAsync(InstanceProtocol.StartEncryption());
                Console.WriteLine($"[gateway] {remote} => 0x0011 (start encryption)");

                // Anything after the opening was already encrypted by the client.
                var player = new Player(layout, ticket, seed) { Character = { Name = resolved.Character, Id = resolved.CharacterId, CosmeticPortalVariation = resolved.Character is { Length: > 0 } savedCharacter ? CharacterSaves.PortalCosmetic(savedCharacter) : (ushort)0, PortraitFrame = resolved.PortraitFrame, Account = resolved.Account, League = resolved.League, Hardcore = resolved.Hardcore, Ssf = resolved.Ssf, LeagueForm = resolved.LeagueForm, LastTown = resolved.LastTown, ClassRow = Skills.RowOfClass(resolved.Class) }, InstanceOwner = resolved.InstanceOwner, CameFrom = AreaLayouts.CameFrom.TryRemove(ticket, out var from) ? from : null, ArrivalEntrance = AreaLayouts.ArrivalEntrances.TryRemove(ticket, out var entrance) ? entrance : null, Instance = instance, InstanceNumber = instanceId, Pools = CharacterStats.For(Skills.ClassFor(Skills.RowOfClass(resolved.Class)), resolved.Level) };
                player.Connection.Session = new(remote, stream, ciphers) { Hnc = new HncSender(keyring.Key(opening.Ticket)), Keyring = keyring };
                ConnectionMetrics.Add(ciphers, player.Connection.Performance);
                PacketHistories.Add(ciphers, player.Connection.Session.Packets);
                left = player;
                // Another connection's requests (a trade, a party invite) reach this client through its own send gate, and
                // a rule that takes this connection over (AdmittingAsync) can tell it why before it is prepared.
                player.Connection.Outbox = (bytes, log) => Send(remote, stream, ciphers, bytes, log);
                // From here the connection is in the area: everything it does to the game runs in the area's frames
                // (AreaFrames), its messages too. The read loop answers ticks; the rest waits in the inbox for the frame's
                // input phase (ClientInbox, issue #46).
                var inbox = new ClientInbox(player.Connection.Performance);
                using var reading = new CancellationTokenSource();
                var frames = player.Frames;
                frames.Attach(player, inbox, reading);
                attached = frames;
                // A rule may end the connection this one replaces first (one connection per character, AdmittingAsync):
                // this connection's character is loaded only after that.
                // Closed: a third login of the character ended this one while it waited for the first.
                bool admitted = await frames.Run(async () =>
                {
                if (!await Logic.AdmittingAsync(player) || player.Closed) return false;
                Logic.Admitting(player);
                player.Commands = Commands(player);
                HotReload.Register(player);
                InGame[player] = 0;
                WindowsTimerResolution.Acquire();
                player.ArriveAtWaypoint = WaypointArrivals.TryRemove(ticket, out _) || HideoutCatalog.ByArea(layout.Hash) is not null;
                player.ArriveAt = SpotArrivals.TryRemove(ticket, out var spot) ? spot : null;
                string pinnedName = CharacterName(player);
                Saves.PinSession(pinnedName, player.Character.Account, player.Character.League);
                pinnedSession = (pinnedName, player.Character.Account, player.Character.League);
                int level = Logic.PrepareCharacter(player, resolved.Level);
                prepared = true;
                byte requestAreaLevel = areaLevel;
                var layoutBeforeHeader = player.Layout;
                var moduleHeader = MergeAreaHeaders(player);
                bool headerReplacedLayout = !ReferenceEquals(player.Layout, layoutBeforeHeader);
                if (headerReplacedLayout) layout = player.Layout;
                byte headerLevel = moduleHeader?.Level ?? areaLevel;
                bool levelChanged = headerLevel > 0 && headerLevel != areaLevel;
                areaLevel = headerLevel;
                // A room of today's Labyrinth names its node (LabyrinthDay): the client then builds the day's layout and
                // applies the room's overrides and secrets, as the server's own generation of it does (ServerLevels).
                var labyrinthNode = moduleHeader?.Terrain is null ? LabyrinthDay.NodeFor(layout.Id, seed) : null;
                // The plaza's door is shut whenever someone comes: each run is chosen at its device again.
                if (layout.Id == LabyrinthDay.Plaza) player.Instance.ClearState(LabyrinthDay.PlazaDoorOpen);
                // A module's own extra data (Delve's) takes the place of the plugin states: the instance and the server's
                // level drop them too, so both sides build the level without them.
                bool droppedPluginStates = false;
                if (pluginStates.Count > 0 && moduleHeader?.Terrain is not null)
                {
                    Console.WriteLine($"[gateway] {remote} {layout.Id}: the module's extra data is sent in place of the terrain plugin states");
                    instance.DropTerrainPluginStates();
                    ServerLevels.PlayWithoutPluginStates(layout.Id, seed);
                    pluginStates = [];
                    droppedPluginStates = true;
                }
                // Refresh player.Layout when AreaHeader dropped plugin states, changed the area level sent in 0x1d,
                // or replaced player.Layout directly (e.g. DelveRoutes.MineHeader), keeping the original requestAreaLevel key
                // so party joins and re-entries into Simulacrum/Delve still hit the cache instead of advancing the seed.
                if (droppedPluginStates || levelChanged)
                {
                    if (levelChanged) ServerLevels.PlayAtLevel(layout.Id, seed, areaLevel);
                    layout = AreaLayouts.For(areaHash, seed);
                    instance.UpdatePreparedLayout(resolved.Seed, requestAreaLevel, seed, layout, Instances.Now);
                    player.Use(layout);
                }
                else if (headerReplacedLayout)
                {
                    instance.UpdatePreparedLayout(resolved.Seed, requestAreaLevel, seed, layout, Instances.Now);
                }
                byte[]? terrainData = moduleHeader?.Terrain ?? (labyrinthNode is not null ? LabyrinthDay.ExtraData(labyrinthNode)
                    : pluginStates.Count > 0 ? InstanceProtocol.TerrainPluginStates(pluginStates) : null);
                // A map area always carries the level it is played at, as the official does: over every first message
                // of the decoded captures, an area whose level comes from the item that opens it carries it, while
                // every campaign zone, town, hideout and league hub carries 0 and lets the client take the WorldAreas
                // level. WorldAreas.IsMapArea is the client's own flag for the first set, so a map entered without a
                // device open — a dev goto, a second entry into the same instance — carries the level the Gateway
                // generated the layout at instead of 0.
                if (areaLevel == 0 && ServerLevels.IsMapArea(layout.Id))
                    areaLevel = (byte)Math.Clamp(ServerLevels.PlayedLevel(layout.Id, seed), 1, 100);
                var area = new AreaInstance(InstanceId: (uint)instanceId, AreaHash: areaHash, Seed: seed, Level: areaLevel,
                    Text: resolved.League ?? "", LeagueStats: InstanceEvents.Merge(areaStats, Logic.AreaLeagueStats(player)),
                    TerrainData: terrainData,
                    AchievementSets: moduleHeader?.AchievementSets, B28: (byte)leagueCategory,
                    HideoutPermissions: OwnsHideoutInstance(player),
                    ReturnAreaHash: player.ReturnAreaHash);
                await Send(remote, stream, ciphers, area.Encode(), $"0x001d {area}; {areaStats.Count} area stat(s); ascendancy family {player.Character.MainAscendancy.Selection?.Family}" +
                    (labyrinthNode is null ? "" : $"; labyrinth day {labyrinthNode.Seed} node {labyrinthNode.Row} difficulty {labyrinthNode.Difficulty}") +
                    string.Concat(pluginStates.Select(p => $"; plugin {p.Plugin} state {Convert.ToHexString(p.State)}")));
                await Logic.Entered(player, level, resolved.Level);
                // A Labyrinth run: the rooms been in, for the Labyrinth map (S→C 0x1cb); anywhere else it ends.
                if (resolved.Character is { Length: > 0 } runner)
                {
                    var progress = LabyrinthDay.Enter(runner, layout.Id, seed) ?? (labyrinthNode is null ? null : LabyrinthDay.Visit(labyrinthNode));
                    if (progress is not null)
                        await Send(remote, stream, ciphers, progress, $"0x01cb labyrinth progress: now node {labyrinthNode?.Row}");
                }
                return true;
                });
                if (!admitted)
                {
                    player.Connection.CloseReason ??= "the game rules refused the connection";
                    Console.WriteLine($"[gateway] {remote} connection refused by the rules");
                    return;
                }
                try
                {
                    byte[] rest = [.. pending.Skip(InstanceProtocol.OpeningLength)];
                    if (rest.Length > 0) await OnClientData(remote, stream, ciphers, rest, player, inbox, Stopwatch.GetTimestamp());
                    // Closed early by C→S 0x153 (exit to character selection) or a handler error: the handler task cancels
                    // the read, and leaving the loop closes the connection.
                    while (!player.Closed)
                    {
                        int n = await stream.ReadAsync(buffer, reading.Token);
                        if (n == 0)
                        {
                            // An area change closes it too; otherwise the client went away by itself: a crash (or a closed
                            // game), whose cause is usually in the last packets (PacketHistory).
                            vanished = player.Connection.CloseReason is null && !player.LeavingArea && player.PendingTransfer is null
                                && !player.PendingCharacterSelectExit;
                            reason = player.Connection.CloseReason ?? (vanished
                                ? "the client closed the connection (no area change or exit: a client crash or a closed game?)"
                                : "the client closed the connection (area change)");
                            break;
                        }
                        total += n;
                        await OnClientData(remote, stream, ciphers, buffer.AsSpan(0, n).ToArray(), player, inbox, Stopwatch.GetTimestamp());
                        // A lost stream (ClientInbox.LostAfterUnframedReads): leaving the loop closes the connection.
                        if (player.Connection.Session.UnframedRun >= ClientInbox.LostAfterUnframedReads) { reason = player.Connection.CloseReason; break; }
                    }
                    reason ??= player.Connection.CloseReason ?? "closed by the server";
                }
                catch (OperationCanceledException) when (reading.IsCancellationRequested) { reason = player.Connection.CloseReason ?? "closed by the server"; }
                finally
                {
                    await frames.Run(() =>
                    {
                        player.Closed = true;
                        player.InvalidateIncomingHits();
                        try { Logic.Closing(player); }
                        catch (Exception e) { Console.WriteLine($"[gateway] {remote} closing failed: {ServerLog.Describe(e)}"); }
                    });
                    inbox.Complete();
                    inbox.DiscardPending();
                    // The batch it was on finishes first.
                    try { await frames.Handling(player); }
                    catch (Exception) { }
                    await frames.Run(async () =>
                    {
                    // An open trade ends with its owner: the other side gets its items back in view and the window closes.
                    try { await GameEvents.RunPlayerLeaving(player, "disconnected"); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                    if (player.BuildImport.Confirmed is { } build)
                    {
                        bool imported = false;
                        try
                        {
                            Logic.Save(player, prepared);
                            if (pinnedSession is { } buildPin)
                            {
                                pinnedSession = null;
                                Saves.UnpinAndFlushSession(buildPin.Character, buildPin.Account, buildPin.League);
                            }
                            Logic.Left(player);
                            player.BuildFinalized = true;
                            CharacterBuildImports.Apply(build);
                            imported = true;
                            Console.WriteLine($"[character-build] {CharacterName(player)}: committed {build.Class} level {build.Level}");
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"[character-build] {CharacterName(player)}: import failed: {e.Message}");
                            try { await player.Commands!.Reply("Build import failed: " + e.Message); }
                            catch (Exception send) when (send is IOException or ObjectDisposedException) { }
                        }
                        if (imported)
                        {
                            try { await player.Commands!.Reply("Build applied. Select your character again."); }
                            catch (Exception send) when (send is IOException or ObjectDisposedException) { }
                        }
                    }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            // A client leaving for another area often resets the connection instead of closing it; otherwise a reset
            // (IOException) is how a crashed client often looks from here, and its packets are the lead.
            if (left is { Closed: true, Connection.CloseReason: { } declared })
                reason = declared;   // the server ended it on purpose (another login took the character, a failed output)
            else if (ex is IOException && left is { } leaving && (leaving.LeavingArea || leaving.PendingTransfer is not null))
                reason = "the client reset the connection (area change)";
            else
            {
                reason = $"error: {ex.GetType().Name}: {ex.Message}";
                Console.WriteLine($"[gateway] {remote} error: {ServerLog.Describe(ex)}");
                left?.Connection.Session.Packets.Print(remote, "before the error");
            }
        }
        if (left is not null && vanished)
            left.Connection.Session.Packets.Print(remote, "before the client closed the connection");
        if (left is not null)
        {
            try
            {
                bool saved = true;
                void Leave()
                {
                    // The connection ends (a logout, an area change, a crash of the client): what waits in memory is saved now.
                    try
                    {
                        if (!left.BuildFinalized) Logic.Save(left, prepared);
                    }
                    catch (Exception e)
                    {
                        saved = false;
                        Console.WriteLine($"[gateway] {remote} unsaved progress not saved at disconnect (kept for the next save): {ServerLog.Describe(e)}");
                    }
                    if (pinnedSession is { } pin)
                    {
                        pinnedSession = null;
                        try { Saves.UnpinAndFlushSession(pin.Character, pin.Account, pin.League); }
                        catch (Exception e)
                        {
                            saved = false;
                            Console.WriteLine($"[gateway] {remote} session save flush failed at disconnect (kept for the next save): {ServerLog.Describe(e)}");
                        }
                    }
                    // What follows must run whatever the rules do, or the player stays in the instance.
                    try { if (!left.BuildFinalized) Logic.Left(left); }
                    catch (Exception e) { Console.WriteLine($"[gateway] {remote} leaving failed: {ServerLog.Describe(e)}"); }
                    left.Instance.RemovePlayer(left.Id);
                }
                if (attached is not null) await attached.Run(Leave); else Leave();
                // The character has left the game with its save written (not an area change, whose next connection holds
                // it already). It is still listed in game here, so a login of it waits on Ended below. Not for a
                // connection that never loaded it (a refused login): another one may still hold the character.
                if (saved && prepared && !left.TransferAccepted) await GameEvents.RunCharacterAtRest(left);
                if (InGame.TryRemove(left, out _)) WindowsTimerResolution.Release();
                ForgetCommands(left);
                Instances.Leave(left.Instance);
                CharacterBuildImports.Release(left);
            }
            finally
            {
                if (pinnedSession is { } pin)
                {
                    pinnedSession = null;
                    try { Saves.UnpinAndFlushSession(pin.Character, pin.Account, pin.League); }
                    catch (Exception e) { Console.WriteLine($"[gateway] {remote} session save flush failed: {ServerLog.Describe(e)}"); }
                }
                attached?.Detach(left);
                // The connection has ended and its save is written: whoever ended it for another connection goes on. Set
                // whatever threw above, or that login (and every later one of the character) would wait for ever.
                left.Connection.Ended.TrySetResult();
            }
        }
        else if (entered is not null) Instances.Leave(entered);   // ended before its Player existed
        if (ticket != 0) await InstanceControl.CallAsync(new ControlRequest { Op = "leave", Ticket = ticket });
        string? dump = left is not null && PacketHistory.DumpFolder is { } folder
            ? left.Connection.Session.Packets.Dump(folder, remote, $"{remote} {CharacterName(left)} in {left.Layout.Id} seed {left.Seed}: {reason ?? "?"}")
            : null;
        Console.WriteLine($"[gateway] DISCONNECT {remote} total={total} after {Stopwatch.GetElapsedTime(connectedAt).TotalSeconds:0.0} s: " +
                          $"{reason ?? "?"}{(dump is null ? "" : $" (packets: {dump})")}");
    }

    // The read loop's part: decrypts one read, frames it (InstanceProtocol.SplitClientMessages), answers its 0x1b ticks with
    // 0x1c at once and queues the rest for the area's input phase (Handle). readAt is when the read completed: a tick answered more than
    // ClientInbox.SlowAfter later is logged.
    public static async Task OnClientData(string remote, NetworkStream stream, ConnectionCiphers ciphers, byte[] data, Player player,
        ClientInbox inbox, long readAt)
    {
        ciphers.Receive.Process(data);
        var session = player.Connection.Session;
        byte[] all = session.Carry.Length == 0 ? data : [.. session.Carry, .. data];
        var messages = InstanceProtocol.SplitClientMessages(all, out session.Carry, out byte[] unknown);
        if (session.Carry.Length > InstanceProtocol.MaxClientCarryBytes)
        {
            unknown = unknown.Length == 0 ? session.Carry : [.. unknown, .. session.Carry];
            session.Carry = [];
        }
        (ushort[] Predictive, ushort[] Lockstep)? mixed = null;
        foreach (var (id, bytes) in messages)
        {
            player.Connection.Session.Packets.Received(id, bytes);
            mixed ??= player.Connection.NotePacket(remote, id);
            // ServerTrace.Client: every C→S message in full, the 0x1b ticks too, to compare what clients send.
            if (ServerTrace.Client || ServerTrace.Packets && PacketHistory.Kept(sent: false, id))
                Console.WriteLine($"[packets] {remote} <= 0x{id:x4} {bytes.Length} B: {Convert.ToHexString(bytes)}");
            // Exit to Log In Screen (C→S 0x001a): the client sends 0x02cd + 0x001a and immediately calls shutdown(SD_SEND),
            // so EOF often arrives before AreaFrames dequeues ClientInbox and DiscardPending() would silently drop 0x001a.
            // Notify Auth to close the login socket immediately so LoginState::Enter lands on the Login Screen (not Character Select).
            if (id == InstanceProtocol.ExitToLoginId && Interlocked.Exchange(ref player.ExitToLoginHandled, 1) == 0)
            {
                Console.WriteLine($"[gateway] {remote} <= 0x001a exit to log in screen: closing login and instance connections");
                player.Connection.CloseReason = "exit to log in screen (0x001a)";
                if (player.Ticket != 0)
                    await InstanceControl.CallAsync(new ControlRequest { Op = "logout", Ticket = player.Ticket });
                player.Closed = true;
            }
        }
        if (mixed is { } both) await ReportMixedModes(remote, stream, ciphers, both.Predictive, both.Lockstep);
        if (unknown.Length > 0) player.Connection.Session.Packets.Received(0xffff, unknown);   // bytes no known message frames ("unframed")
        player.Connection.Session.UnframedRun = ClientInbox.UnframedRun(player.Connection.Session.UnframedRun, messages.Count, unknown.Length);
        if (player.Connection.Session.UnframedRun == ClientInbox.LostAfterUnframedReads)
            player.Connection.CloseReason = $"the client's stream no longer decodes ({player.Connection.Session.UnframedRun} reads in a row framed nothing): closed, so the client can connect again";
        if (MoveTrace)
            foreach (var (id, bytes) in messages)
                // 0x18e click, 0x192 the held button's newest target, 0x193 retarget onto an object, 0x195 release:
                // a lockstep client's whole move input, beside the predictive 0x24/0x25/0x190/0x191.
                if (id is 0x0024 or 0x0025 or 0x018e or 0x0190 or 0x0191 or 0x0192 or 0x0193 or 0x0195)
                    Console.WriteLine($"[move-trace] qpc={readAt} recv 0x{id:x4} {Convert.ToHexString(bytes)}");
        foreach (var bytes in inbox.Post(messages, unknown, readAt))
        {
            if (player.Ticks++ % 50 == 0 && ServerTrace.Ticks) Console.WriteLine($"[gateway] {remote} <= 0x001b tick #{player.Ticks}: {Convert.ToHexString(bytes)} (answered with 0x001c)");
            // Answered after the input read before it (ClientInbox.DeferTick); the ack index and the side channel are taken
            // when it goes out, so the answers keep the ticks' order.
            inbox.DeferTick(() => SendPong(stream, ciphers, InstanceProtocol.Pong(serverDelayMs: ClientInbox.ServerDelayMs,
                ackIndex: inbox.NextAckIndex(), sideChannel: player.Connection.Session.Hnc!.NextIdleU32())));
            var waited = Stopwatch.GetElapsedTime(readAt);
            if (waited > ClientInbox.SlowAfter)
                Console.WriteLine($"[gateway] {remote} slow tick #{player.Ticks}: 0x001c sent {waited.TotalMilliseconds:0} ms after the 0x001b was read");
        }
    }

    // One batch of a connection's messages (every message but the ticks), handled in the area's input phase
    // (AreaFrames.Input), in order. A batch that runs longer than ClientInbox.SlowAfter is logged. An error or 0x153 closes
    // the connection by cancelling its read.
    public static async Task Handle(Player player, ClientInbox inbox, ClientInbox.Batch batch, CancellationTokenSource reading)
    {
        string remote = player.Connection.Session.Remote;
        ActingPlayer.Value = player;
        long start = Stopwatch.GetTimestamp();
        try { await Logic.OnClientMessages(batch.Messages, batch.Unknown, player, batch.ReadAt); }
        catch (Exception ex)
        {
            string ids = " handling " + string.Join(", ", batch.Messages.Select(m => $"0x{m.Id:x4}"));
            player.Connection.CloseReason = $"handler error{ids}: {ex.GetType().Name}: {ex.Message}";
            Console.WriteLine($"[gateway] {remote} error{ids}: {ServerLog.Describe(ex)}");
            if (ex is not (IOException or ObjectDisposedException)) player.Connection.Session.Packets.Print(remote, "before the handler error");
            reading.Cancel();
            return;
        }
        finally { inbox.Handled(); }
        player.Connection.Performance.Elapsed(LatencyPhase.Handler, start);
        foreach (var (id, _) in batch.Messages)
            if (id == GameServer.Core.Protocol.Instance.InstanceProtocol.AreaGeneratedId)
                player.Connection.Performance.AreaLoaded(remote, player.Layout.Id, start);
        player.Connection.Performance.MaybeLog(remote, inbox.PendingBatches);
        var took = Stopwatch.GetElapsedTime(start);
        if (took > ClientInbox.SlowAfter)
        {
            var ids = batch.Messages.Select(m => $"0x{m.Id:x4}").Concat(batch.Unknown.Length > 1 ? [$"unframed 0x{batch.Unknown[0] << 8 | batch.Unknown[1]:x4}"] : []);
            Console.WriteLine($"[gateway] {remote} slow handler: {string.Join(", ", ids)} took {took.TotalMilliseconds:0} ms (ticks answered meanwhile)");
        }
        if (player.Closed) reading.Cancel();
    }

    // Monster AI, one decision every MonsterAiCadence.StepMilliseconds of game time per connection (StartMonsterAi). A monster within its aggro range (MonsterVarieties MaxAgroRange, in
    // cells) of the player starts chasing: it walks (Move action 0x1ab, skill 0x2909; the client paths it) to its own cell
    // around the player (MonsterSurround), re-ordered when that cell has moved by 3+ cells. Beyond twice the range it gives up and stops.
    // Positions are the server's estimates (PlayerWalk at MonsterVarieties MovementSpeed cells/s).
    public static async Task WhilePaused(Player player)
    {
        while (player.Paused && !player.Closed) await Task.Delay(100);
    }

    // One sender at a time per connection: the Salsa20 keystream must be applied and written in the same order (the read
    // loop, the monster AI tick and the transfer timer all send).
    // `still`, when given, is checked inside the connection's send gate and drops the packet when false: a packet decided
    // before another task's packets were sent can check that it still applies (SendFlinch).
    // Projectile impacts commit their short locked damage mutation inside this gate before relocation.
    // A factory samples clock-sensitive state and constructs its packet after earlier sends; null sends nothing.
    /// <summary>Sees every message before it is encrypted and written (a test's or a tool's observer); it must copy what it keeps.</summary>
    public static Action<Player, byte[]>? Outgoing;
    /// <summary>The player each connection's ciphers send for (Player.Session), for <see cref="Outgoing"/>.</summary>
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ConnectionCiphers, Player> PlayersByCiphers = new();

    // How often a state sync that an update loop offers every 16-32 ms may take the send gate. The packets are
    // idempotent snapshots, so a skipped step publishes nothing lost, and the input handlers stop queueing behind them.
    public const double SyncPublishMilliseconds = 100;

    /// <summary>Sends to the player's client (<see cref="Send(string, NetworkStream, ConnectionCiphers, byte[], string?, Func{bool}?, Func{byte[]?}?, string?)"/>).</summary>
    /// A player made without a connection (Session.None: a tool's, a test's) has nobody to send to.
    public static Task Send(Player player, byte[] bytes, string? log,
        Func<bool>? still = null, Func<byte[]?>? atGate = null, string? sync = null)
    {
        // In the area's frame a message about an entity goes to every client in the area, the sender's too, at the frame's end.
        if (player.Area.Owned && player.Connection.WorldOutput.Started && EntityMessage(atGate is null ? bytes : null) is not false)
        {
            if (sync is not null && !MetricsFor(player.Connection.Session.Ciphers).SyncDue(sync, SyncPublishMilliseconds)) return Task.CompletedTask;
            if (still is not null && !still()) return Task.CompletedTask;
            if (atGate is not null) bytes = atGate() ?? [];
            if (bytes.Length == 0) return Task.CompletedTask;
            if (EntityMessage(bytes) is true)
            {
                ToArea(player, bytes, log, only: ViewOf.Value ?? player.Area.Delta.ViewerOf(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)), player));
                return Task.CompletedTask;
            }
            still = null; atGate = null; sync = null;
        }
        return player.Connection.Session.Connected ? Send(player.Connection.Session.Remote, player.Connection.Session.Stream, player.Connection.Session.Ciphers, bytes, log, still, atGate, sync)
            : Task.CompletedTask;
    }

    /// <summary>Whether a message is about one entity (null: not known yet, its bytes are made at the gate).</summary>
    static bool? EntityMessage(byte[]? bytes) => bytes is null ? null : bytes.Length >= 12 && (bytes[0] << 8 | bytes[1]) is
        InstanceProtocol.ActionStartedId or 0x1ac or InstanceProtocol.ActionTargetId or InstanceProtocol.ActionHitId
        or InstanceProtocol.InstantActionId or InstanceProtocol.AnimationUpdateId or InstanceProtocol.ActorAnimationId
        or InstanceEntities.AddBuffId or InstanceEntities.RemoveBuffId or InstanceEntities.StatsUpdateId or InstanceProtocol.TeleportId or InstanceProtocol.MoveToId
        or InstanceProtocol.EnterWakeZoneId or InstanceProtocol.LeaveWakeZoneId
        or InstanceEntities.CreateObjectId or InstanceProtocol.RemoveObjectId;

    /// <summary>Records about the area's entities, for every client in it (or all but one), in this frame's delta.</summary>
    public static void ToArea(Player player, byte[] bytes, string? log, Player? except = null, Player? only = null)
    {
        uint entity = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2));
        ushort id = (ushort)(bytes[0] << 8 | bytes[1]);
        player.Area.Delta.Add(entity, bytes, log, id switch
        {
            InstanceEntities.CreateObjectId => AreaDelta.Kind.Create,
            InstanceProtocol.RemoveObjectId => AreaDelta.Kind.Remove,
            _ => AreaDelta.Kind.World,
        }, except, only);
    }

    public static async Task Send(string remote, NetworkStream stream, ConnectionCiphers ciphers, byte[] bytes, string? log,
        Func<bool>? still = null, Func<byte[]?>? atGate = null, string? sync = null)
    {
        var gate = SendGates.GetValue(ciphers, _ => new SemaphoreSlim(1, 1));
        var performance = MetricsFor(ciphers);
        // A state sync the loops offer many times a second: unless its window is up, do not take the gate or the lock at
        // all. See ConnectionPerformance.SyncDue: the queue behind these is what the input handlers waited in.
        if (sync is not null && !performance.SyncDue(sync, SyncPublishMilliseconds)) return;
        // Inside a batch (SendBatched) the send waits for the batch's one turn at the gate.
        if (CurrentSendBatch.Value is { } batch && ReferenceEquals(batch.Ciphers, ciphers))
        {
            batch.Items.Add(new(bytes, log, still, atGate, ProjectileOutputGuard.Value));
            return;
        }
        long waitingAt = Stopwatch.GetTimestamp();
        await gate.WaitAsync();
        performance.Elapsed(LatencyPhase.SendWait, waitingAt);
        long heldAt = Stopwatch.GetTimestamp();
        ushort? sentId = null;
        try
        {
            if (ProjectileOutputGuard.Value is { } projectileGuard && !projectileGuard()) return;
            if (still is not null && !still()) return;
            if (atGate is not null)
            {
                if (atGate() is not { } current) return;
                bytes = current;
            }
            // Nothing to send (a sync with no change): nothing written, nothing in the history.
            if (bytes.Length == 0) return;
            sentId = (ushort)(bytes[0] << 8 | bytes[1]);
            await WritePacket(remote, stream, ciphers, bytes, log).ConfigureAwait(false);
        }
        finally
        {
            long held = Stopwatch.GetTimestamp();
            var hold = Stopwatch.GetElapsedTime(heldAt, held);
            gate.Release();
            performance.Elapsed(LatencyPhase.SendHold, heldAt);
            if (sentId is ushort id)
            {
                performance.Send(id, held - heldAt, bytes.Length);
                // Why a full player stat packet went out (its log label names the cause), per interval.
                if (ConnectionPerformance.LagProbe && id == 0x01b3)
                    performance.Count(log is not null && log.IndexOf('(') is >= 0 and var open
                        ? "stats sent " + log[open..(log.IndexOfAny([':', ')'], open) is >= 0 and var close ? close : log.Length)].TrimEnd() + ")"
                        : "stats sent (no cause in its label)");
            }
            else if (ConnectionPerformance.LagProbe) performance.EmptySend(SendCaller(), held - heldAt);
            // Temporary: which send holds the gate while input waits behind it.
            if (ConnectionPerformance.LagProbe && hold > TimeSpan.FromMilliseconds(50))
                Console.WriteLine($"[slow-send] {remote} {log ?? "?"} wait={Stopwatch.GetElapsedTime(waitingAt, heldAt).TotalMilliseconds:0} "
                    + $"hold={hold.TotalMilliseconds:0} bytes={bytes.Length}"
                    + (log is null ? " at=" + string.Join(" <- ", new StackTrace(1, false).GetFrames()
                        .Take(4).Select(frame => frame.GetMethod()?.Name ?? "?")) : ""));
        }
    }

    // Called under SendGate. Diagnostics and the shared-object observer see actual transmission, not queueing.
    public static Task WritePacket(Player player, byte[] bytes, string? log, WorldOutput.Packet? record = null) =>
        WritePacket(player.Connection.Session.Remote, player.Connection.Session.Stream, player.Connection.Session.Ciphers, bytes, log, record);

    public static async Task WritePacket(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        byte[] bytes, string? log, WorldOutput.Packet? record = null)
    {
        string? trace = Written(remote, ciphers, bytes, log, record);
        await Write(stream, ciphers, bytes).ConfigureAwait(false);
        if (trace is not null) Console.WriteLine($"[move-trace] qpc={Stopwatch.GetTimestamp()} send {trace}");
    }

    /// <summary>Writes a run of packets (a frame's) in one write, each logged, kept and mirrored as on its own. Under the
    /// send gate.</summary>
    public static Task WritePackets(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        IReadOnlyList<(byte[] Bytes, string? Log, WorldOutput.Packet? Record)> packets)
    {
        int length = 0;
        foreach (var packet in packets) length += packet.Bytes.Length;
        if (length == 0) return Task.CompletedTask;
        var all = new byte[length];
        int at = 0;
        foreach (var (bytes, log, record) in packets)
        {
            if (Written(remote, ciphers, bytes, log, record) is { } trace)
                Console.WriteLine($"[move-trace] qpc={Stopwatch.GetTimestamp()} send {trace}");
            bytes.CopyTo(all, at);
            at += bytes.Length;
        }
        return Write(stream, ciphers, all);
    }

    // What a packet's write tells the rest of the server before the wire: its log line, the history, the mirror to the
    // instance's other clients. Its move trace, if traced, for after the write.
    static string? Written(string remote, ConnectionCiphers ciphers, byte[] bytes, string? log, WorldOutput.Packet? record)
    {
            if (log is not null)
            {
                string line = $"[gateway] {remote} => {log}: {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 96)))}";
                // Explicit traces retain synchronous output; normal summaries cannot stall the wire on disk IO.
                if (ServerTrace.Packets) Console.WriteLine(line);
                else PacketSummaries.Value.Write(line);
            }
            if (ServerTrace.Packets && PacketHistory.Kept(sent: true, PacketHistory.IdOf(bytes)))
                Console.WriteLine($"[packets] {remote} => 0x{PacketHistory.IdOf(bytes):x4} {bytes.Length} B: {Convert.ToHexString(bytes)}");
            HistoryFor(ciphers).Sent(bytes);   // before Write, which encrypts the bytes in place
            if (Outgoing is { } outgoing && PlayersByCiphers.TryGetValue(ciphers, out var player)) outgoing(player, bytes);
            return MoveTrace && bytes.Length >= 6 && (bytes[0] << 8 | bytes[1]) is 0x01ab or 0x01b7 or 0x01ba
                ? Convert.ToHexString(bytes) : null;
    }

    // A run of sends that each would take the send gate on their own (the player tick's eleven state publications: Antiquarian,
    // Onslaught, totems, Spirit Chieftain, recent hits, attacker slows, Lifetap, leech, Petrified Blood, enemy auras,
    // blind) queue in a batch and take the gate once, in their order, each still evaluated at the gate as on its own; the
    // packets go out in one write. The input handlers then wait behind one hold instead of eleven.
    sealed record BatchedSend(byte[] Bytes, string? Log, Func<bool>? Still, Func<byte[]?>? AtGate, Func<bool>? Guard);
    sealed class SendBatch(ConnectionCiphers ciphers)
    {
        public readonly ConnectionCiphers Ciphers = ciphers;
        public readonly List<BatchedSend> Items = [];
    }
    static readonly AsyncLocal<SendBatch?> CurrentSendBatch = new();

    /// <summary>Runs <paramref name="body"/>, whose sends to this connection take the send gate together at its end.
    /// Its result is the body's. A send inside returns before it is published, so the body must not rely on one having
    /// gone out.</summary>
    public static Task<bool> SendBatched(Player player, Func<Task<bool>> body) => player.Connection.Session.Connected
        ? SendBatched(player.Connection.Session.Remote, player.Connection.Session.Stream, player.Connection.Session.Ciphers, body) : body();

    public static async Task<bool> SendBatched(string remote, NetworkStream stream, ConnectionCiphers ciphers, Func<Task<bool>> body)
    {
        if (CurrentSendBatch.Value is not null) return await body();
        var batch = new SendBatch(ciphers);
        bool result;
        CurrentSendBatch.Value = batch;
        try { result = await body(); }
        finally { CurrentSendBatch.Value = null; }
        if (batch.Items.Count == 0) return result;
        var gate = SendGates.GetValue(ciphers, _ => new SemaphoreSlim(1, 1));
        var performance = MetricsFor(ciphers);
        long waitingAt = Stopwatch.GetTimestamp();
        await gate.WaitAsync();
        performance.Elapsed(LatencyPhase.SendWait, waitingAt);
        long heldAt = Stopwatch.GetTimestamp();
        try
        {
            var all = new List<byte>();
            foreach (var item in batch.Items)
            {
                if (item.Guard is { } guard && !guard()) continue;
                if (item.Still is not null && !item.Still()) continue;
                var bytes = item.Bytes;
                if (item.AtGate is not null)
                {
                    if (item.AtGate() is not { } current) continue;
                    bytes = current;
                }
                if (bytes.Length == 0) continue;
                if (item.Log is not null)
                {
                    string line = $"[gateway] {remote} => {item.Log}: {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 96)))}";
                    if (ServerTrace.Packets) Console.WriteLine(line);
                    else PacketSummaries.Value.Write(line);
                }
                HistoryFor(ciphers).Sent(bytes);
                if (Outgoing is { } outgoing && PlayersByCiphers.TryGetValue(ciphers, out var player)) outgoing(player, bytes);
                all.AddRange(bytes);
            }
            if (all.Count > 0) await Write(stream, ciphers, [.. all]).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
            performance.Elapsed(LatencyPhase.SendHold, heldAt);
        }
        return result;
    }

    // Temporary lag instrumentation: the async method whose builder published nothing, from its state machine type.
    static string SendCaller()
    {
        foreach (var frame in new StackTrace(1, false).GetFrames())
        {
            var method = frame.GetMethod();
            string name = method?.Name ?? "?";
            if (name is not ("Start" or "MoveNext"))
            {
                if (name is not "Send") return name;
                continue;
            }
            string type = method?.DeclaringType?.Name ?? "";
            int end = type.IndexOf('>');
            if (!type.StartsWith('<') || end <= 1) continue;
            string owner = type[1..end];
            if (owner is not "Send") return owner;
        }
        return "?";
    }

    // The wire itself: the keystream must be applied and written in one order, so encryption and the write share this
    // short gate. Send's gate orders the packets and runs their state callbacks; it never holds this one while waiting.
    public static async Task Write(NetworkStream stream, ConnectionCiphers ciphers, byte[] bytes)
    {
        if (ControlledPlayers.IsTransport(ciphers)) return;
        var wire = WireGates.GetValue(ciphers, _ => new SemaphoreSlim(1, 1));
        var performance = MetricsFor(ciphers);
        long waitingAt = Stopwatch.GetTimestamp();
        await wire.WaitAsync().ConfigureAwait(false);
        performance.Elapsed(LatencyPhase.WireWait, waitingAt);
        long heldAt = Stopwatch.GetTimestamp();
        try
        {
            ciphers.Send.Process(bytes);
            long writeAt = Stopwatch.GetTimestamp();
            try { await stream.WriteAsync(bytes).ConfigureAwait(false); }
            finally { performance.Elapsed(LatencyPhase.SocketWrite, writeAt); }
        }
        finally { wire.Release(); performance.Elapsed(LatencyPhase.WireHold, heldAt); }
    }

    /// <summary>The client sent both Predictive-only and Lockstep-only messages in this area (ClientPackets), which one
    /// client never does: the log and the client's chat say so, the chat six times so the player sees it.</summary>
    static async Task ReportMixedModes(string remote, NetworkStream stream, ConnectionCiphers ciphers, ushort[] predictive, ushort[] lockstep)
    {
        static string Ids(ushort[] ids) => string.Join(", ", ids.Select(id => $"0x{id:x4}"));
        string line = $"Network mode error: this area received both Predictive-only ({Ids(predictive)}) and Lockstep-only " +
            $"({Ids(lockstep)}) client messages. Please report this to the developers with these packet ids.";
        Console.WriteLine($"[gateway] {remote} {line}");
        // The wire encrypts the message buffer in place. Each diagnostic needs its own plaintext.
        for (int i = 0; i < 6; i++)
            await Send(remote, stream, ciphers, InstanceChat.ChatLine(line), $"0x0017 chat line (network mode error {i + 1}/6)");
    }

    // A tick's answer (0x1c) only reports that the server is there: it needs no game state, so it takes the wire alone
    // and goes out between two other packets instead of waiting behind their state callbacks (combat, Rage, stats).
    public static Task SendPong(NetworkStream stream, ConnectionCiphers ciphers, byte[] bytes) => Write(stream, ciphers, bytes);
    /// <summary>A reply written at once, outside the send gate's order (the lockstep probe's AFK request).</summary>
    public static Task SendPong(Player player, byte[] bytes) => Write(player.Connection.Session.Stream, player.Connection.Session.Ciphers, bytes);

    // Composes every AreaHeader subscriber so an AchievementSets-only contributor cannot shadow a later module's
    // Terrain or Level override (e.g. Delve mine terrain or Simulacrum wave level).
    static AreaHeader? MergeAreaHeaders(Player player)
    {
        byte[]? terrain = null;
        byte? level = null;
        List<byte>? sets = null;
        bool any = false;
        foreach (var (owner, run) in GameEvents.AreaHeader.Subscribers(player))
        {
            AreaHeader? next = null;
            GameEventOwners.Guard(owner, nameof(GameEvents.AreaHeader), () => next = run(player));
            if (next is null) continue;
            any = true;
            terrain ??= next.Terrain;
            if (level is null && next.Level is > 0) level = next.Level;
            if (next.AchievementSets is { Count: > 0 } nextSets)
            {
                sets ??= [];
                foreach (byte set in nextSets)
                    if (!sets.Contains(set)) sets.Add(set);
            }
        }
        return any ? new AreaHeader(terrain, sets) { Level = level } : null;
    }

    public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ConnectionCiphers, SemaphoreSlim> WireGates = new();
    public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ConnectionCiphers, ConnectionPerformance> ConnectionMetrics = new();
    public static ConnectionPerformance MetricsFor(ConnectionCiphers ciphers) => ConnectionMetrics.GetValue(ciphers, _ => new());
    public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ConnectionCiphers, PacketHistory> PacketHistories = new();
    public static PacketHistory HistoryFor(ConnectionCiphers ciphers) => PacketHistories.GetValue(ciphers, _ => new());
}
