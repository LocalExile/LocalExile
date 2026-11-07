using System.Diagnostics;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Accounts;
using GameServer.Storage;
using GameServer.Core.Diagnostics;
using GameServer.Modding;
using WalkEstimator = GameServer.Core.World.WalkEstimator;

partial class Program
{
    static readonly Lazy<PacketLogQueue> PacketSummaries = new(() => new(Console.WriteLine));
    internal static string CharacterName(ClientState conn) => string.IsNullOrEmpty(conn.Character) ? "Exile" : conn.Character;

    // Set while a projectile's output is sent: false drops it (Send).
    internal static readonly AsyncLocal<Func<bool>?> ProjectileOutputGuard = new();

    internal static void SaveUnsavedProgressNow(string? why)
    {
        try
        {
            HardcoreDeathPersistence.RetryPending(Saves.Db);
            CharacterSaves.SaveExperience();
            PlayerInventories.SaveAllUnsaved();
            if (why is not null) Console.WriteLine($"[gateway] unsaved progress saved ({why})");
        }
        catch (Exception e)
        {
            // Kept in memory: the next save tries again.
            Console.WriteLine($"[gateway] unsaved progress not saved{(why is null ? "" : $" ({why})")}: {ServerLog.Describe(e)}");
        }
    }

    internal static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }

    internal static async Task HandleAsync(TcpClient client, InstanceKeyring keyring)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        // Nagle off: it would hold a small 0x1c back until the previous write is acknowledged (up to ~200 ms, issue #46).
        client.NoDelay = true;
        Console.WriteLine($"[gateway] CONNECT {remote} at {DateTime.Now:HH:mm:ss.fff}");
        long total = 0;
        uint ticket = 0;
        ClientState? left = null;
        bool prepared = false;
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
                ticket = opening.Ticket;
                if (GameLogicOrNull is null)
                {
                    Console.WriteLine($"[gateway] {remote} the game module is not loaded; connection refused");
                    return;
                }
                Console.WriteLine($"[gateway] {remote} opening: ticket=0x{opening.Ticket:x8} digest={Convert.ToHexString(opening.Digest)} " +
                                  $"address=0x{opening.Address:x8} u32={opening.U32_128} realm=0x{opening.Realm:x2} flag={opening.Flag}");

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
                    !HardcoreDeathRules.TicketMatches(canonicalCharacter, resolved.CharacterId, resolved.League))
                {
                    Console.WriteLine($"[gateway] {remote} character is dead or its league changed; connection refused");
                    return;
                }
                ushort areaHash = resolved.Area;
                // An opened map's level goes in 0x1d (0: the client takes the area's WorldAreas level).
                string characterName = string.IsNullOrEmpty(resolved.Character) ? "Exile" : resolved.Character;
                byte areaLevel = MapDevice.OpenOf(characterName) is { } openMap && openMap.Area.Hash16 == areaHash
                    && openMap.Area.Level > 0 ? (byte)Math.Clamp(openMap.Area.Level, 1, 100) : (byte)0;
                // The area stats the instance is played with (0x1d's list): the level is generated with them, as the
                // client generates its own from the packet (AreaStats; ServerLevels.PlayedStats).
                var areaStats = AreaStats.Compose(characterName, areaHash, resolved.League);
                // The client generates the area at the seed and level of 0x1d: a seed the Gateway can generate too.
                uint seed = AreaLayouts.PlayableSeed(areaHash, resolved.Seed, areaLevel, areaStats);
                ulong instanceId = resolved.Instance;
                var layout = AreaLayouts.For(areaHash, seed);
                AreaLayouts.Announce(layout, seed);
                Console.WriteLine($"[gateway] {remote} ticket 0x{ticket:x8}: {resolved.Character ?? "?"} → instance {instanceId}, {layout.Describe()}");

                ConnectionCiphers ciphers = keyring.Ciphers(opening.Ticket);
                await stream.WriteAsync(InstanceProtocol.StartEncryption());
                Console.WriteLine($"[gateway] {remote} => 0x0011 (start encryption)");

                // Anything after the opening was already encrypted by the client.
                var conn = new ClientState(layout, ticket, seed) { Character = resolved.Character, CharacterId = resolved.CharacterId, CosmeticPortalVariation = resolved.Character is { Length: > 0 } savedCharacter ? CharacterSaves.PortalCosmetic(savedCharacter) : (ushort)0, PortraitFrame = resolved.PortraitFrame, Account = resolved.Account, League = resolved.League, Hardcore = resolved.Hardcore, Ssf = resolved.Ssf, LeagueForm = resolved.LeagueForm, CameFrom = AreaLayouts.CameFrom.TryRemove(ticket, out var from) ? from : null, SideAreaParent = AreaLayouts.SideAreaParents.TryRemove(ticket, out var sideParent) ? sideParent : null, Instance = Instances.Enter(instanceId, resolved.Created, resolved.Ssf ? resolved.CharacterId?.ToString("N") : null), InstanceNumber = instanceId, LastTown = resolved.LastTown, ClassRow = Skills.RowOfClass(resolved.Class), Pools = CharacterStats.For(Skills.ClassFor(Skills.RowOfClass(resolved.Class)), resolved.Level), Hnc = new HncSender(keyring.Key(opening.Ticket)), Keyring = keyring,
                    LockstepServed = LockstepLink.Confirmed(Lockstep, client.Client.RemoteEndPoint, client.Client.LocalEndPoint, opening.Address) };
                if (conn.LockstepServed)
                {
                    conn.LockstepLatency = Lockstep == LockstepMode.Network ? LockstepLatency.Unconditional() : LockstepLatency.For(resolved.Character ?? "");
                    Console.WriteLine($"[gateway] {remote} a lockstep client is served{(Lockstep == LockstepMode.Network ? "" : " if it is local and its round trip is low")}");
                }
                ConnectionMetrics.Add(ciphers, conn.Performance);
                PacketHistories.Add(ciphers, conn.Packets);
                left = conn;
                Logic.Admitting(conn);
                // Another connection's requests (a trade, a party invite) reach this client through its own send gate.
                conn.Outbox = (bytes, log) => Send(remote, stream, ciphers, bytes, log);
                conn.Wire = (remote, stream, ciphers);
                conn.Commands = Commands(remote, stream, ciphers, conn);
                HotReload.Register(conn);
                InGame[conn] = 0;
                conn.ArriveAtWaypoint = WaypointArrivals.TryRemove(ticket, out _) || HideoutCatalog.ByArea(layout.Hash) is not null;
                conn.ArriveAt = SpotArrivals.TryRemove(ticket, out var spot) ? spot : null;
                int level = Logic.PrepareCharacter(remote, conn, resolved.Level);
                prepared = true;
                var moduleHeader = ServerHooks.Header(PlayerOf(conn));
                // A room of today's Labyrinth names its node (LabyrinthDay): the client then builds the day's layout and
                // applies the room's overrides and secrets, as the server's own generation of it does (ServerLevels).
                var labyrinthNode = moduleHeader?.Terrain is null ? LabyrinthDay.NodeFor(layout.Id, seed) : null;
                // The plaza's door is shut whenever someone comes: each run is chosen at its device again.
                if (layout.Id == LabyrinthDay.Plaza) conn.Instance.ClearState(LabyrinthDay.PlazaDoorOpen);
                // A Vaal side area names the area it was entered from (the alt area) and its parent instance (the extra
                // data's parent record), as the server does.
                var sideAreaParent = conn.SideAreaParent;
                var area = new AreaInstance(InstanceId: (uint)instanceId, AreaHash: areaHash, Seed: seed, Level: areaLevel,
                    Text: resolved.League ?? "", LeagueStats: AreaStats.Merge(areaStats, Logic.AreaLeagueStats(conn)),
                    // The extra data: the Labyrinth node, the terrain plugin states the level was generated with, the parent.
                    TerrainData: moduleHeader?.Terrain ?? (labyrinthNode is null && layout.Level is not { Plugins.Count: > 0 } && sideAreaParent is null ? null
                        : AreaExtraData.Build(labyrinthNode is null ? null : LabyrinthDay.Section(labyrinthNode), layout.Level?.Plugins,
                            sideAreaParent is { } sp ? (sp.Instance, sp.Area) : null)),
                    AchievementSets: moduleHeader?.AchievementSets, AltArea: sideAreaParent?.Area);
                await Send(remote, stream, ciphers, area.Encode(), $"0x001d {area}; ascendancy family {conn.MainAscendancy.Selection?.Family}" +
                    (labyrinthNode is null ? "" : $"; labyrinth day {labyrinthNode.Seed} node {labyrinthNode.Row} difficulty {labyrinthNode.Difficulty}"));
                await Logic.Entered(remote, conn, level, resolved.Level);
                // A Labyrinth run: the rooms been in, for the Labyrinth map (S→C 0x1cb); anywhere else it ends.
                if (resolved.Character is { Length: > 0 } runner)
                {
                    var progress = LabyrinthDay.Enter(runner, layout.Id, seed) ?? (labyrinthNode is null ? null : LabyrinthDay.Visit(labyrinthNode));
                    if (progress is not null)
                        await Send(remote, stream, ciphers, progress, $"0x01cb labyrinth progress: now node {labyrinthNode?.Row}");
                }
                // The read loop answers ticks; everything else runs in order on the handler task (ClientInbox, issue #46).
                var inbox = new ClientInbox(conn.Performance);
                using var reading = new CancellationTokenSource();
                var handlers = RunHandlers(remote, stream, ciphers, conn, inbox, reading);
                try
                {
                    byte[] rest = [.. pending.Skip(InstanceProtocol.OpeningLength)];
                    if (rest.Length > 0) await OnClientData(remote, stream, ciphers, rest, conn, inbox, Stopwatch.GetTimestamp());
                    // Closed early by C→S 0x153 (exit to character selection) or a handler error: the handler task cancels
                    // the read, and leaving the loop closes the connection.
                    while (!conn.Closed)
                    {
                        int n = await stream.ReadAsync(buffer, reading.Token);
                        if (n == 0)
                        {
                            // An area change closes it too; otherwise the client went away by itself: a crash (or a closed
                            // game), whose cause is usually in the last packets (PacketHistory).
                            vanished = conn.CloseReason is null && !conn.LeavingArea && conn.PendingTransfer is null;
                            reason = conn.CloseReason ?? (vanished
                                ? "the client closed the connection (no area change or exit: a client crash or a closed game?)"
                                : "the client closed the connection (area change)");
                            break;
                        }
                        total += n;
                        await OnClientData(remote, stream, ciphers, buffer.AsSpan(0, n).ToArray(), conn, inbox, Stopwatch.GetTimestamp());
                        // A lost stream (ClientInbox.LostAfterUnframedReads): leaving the loop closes the connection.
                        if (conn.UnframedRun >= ClientInbox.LostAfterUnframedReads) { reason = conn.CloseReason; break; }
                    }
                    reason ??= conn.CloseReason ?? "closed by the server";
                }
                catch (OperationCanceledException) when (reading.IsCancellationRequested) { reason = conn.CloseReason ?? "closed by the server"; }
                finally
                {
                    lock (conn.Monsters)
                    {
                        conn.Closed = true;
                        conn.InvalidateIncomingHits();
                        // The handlers below wait for the inbox: it must complete whatever the rules do.
                        try { Logic.Closing(conn); }
                        catch (Exception e) { Console.WriteLine($"[gateway] {remote} closing failed: {ServerLog.Describe(e)}"); }
                    }
                    inbox.Complete();
                    await handlers;
                    // An open trade ends with its owner: the other side gets its items back in view and the window closes.
                    try { await ServerHooks.PlayerLeaving(PlayerOf(conn), "disconnected"); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                }
            }
        }
        catch (Exception ex)
        {
            // A client leaving for another area often resets the connection instead of closing it; otherwise a reset
            // (IOException) is how a crashed client often looks from here, and its packets are the lead.
            if (ex is IOException && left is { } leaving && (leaving.LeavingArea || leaving.PendingTransfer is not null))
                reason = "the client reset the connection (area change)";
            else
            {
                reason = $"error: {ex.GetType().Name}: {ex.Message}";
                Console.WriteLine($"[gateway] {remote} error: {ServerLog.Describe(ex)}");
                left?.Packets.Print(remote, "before the error");
            }
        }
        if (left is not null && vanished)
            left.Packets.Print(remote, "before the client closed the connection");
        if (left is not null)
        {
            // The connection ends (a logout, an area change, a crash of the client): what waits in memory is saved now.
            try
            {
                Logic.Save(left, prepared);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[gateway] {remote} unsaved progress not saved at disconnect (kept for the next save): {ServerLog.Describe(e)}");
            }
            // What follows must run whatever the rules do, or the player stays in the instance.
            try { Logic.Left(left); }
            catch (Exception e) { Console.WriteLine($"[gateway] {remote} leaving failed: {ServerLog.Describe(e)}"); }
            left.Instance.RemovePlayer(left.PeerId);
            InGame.TryRemove(left, out _);
            Instances.Leave(left.Instance);
        }
        if (ticket != 0) await InstanceControl.CallAsync(new ControlRequest { Op = "leave", Ticket = ticket });
        string? dump = left is not null && PacketHistory.DumpFolder is { } folder
            ? left.Packets.Dump(folder, remote, $"{remote} {CharacterName(left)} in {left.Layout.Id} seed {left.Seed}: {reason ?? "?"}")
            : null;
        Console.WriteLine($"[gateway] DISCONNECT {remote} total={total} after {Stopwatch.GetElapsedTime(connectedAt).TotalSeconds:0.0} s: " +
                          $"{reason ?? "?"}{(dump is null ? "" : $" (packets: {dump})")}");
    }

    // The read loop's part: decrypts one read, frames it (InstanceProtocol.SplitClientMessages), answers its 0x1b ticks with
    // 0x1c at once and queues the rest for RunHandlers. readAt is when the read completed: a tick answered more than
    // ClientInbox.SlowAfter later is logged.
    internal static async Task OnClientData(string remote, NetworkStream stream, ConnectionCiphers ciphers, byte[] data, ClientState conn,
        ClientInbox inbox, long readAt)
    {
        ciphers.Receive.Process(data);
        byte[] all = [.. conn.Carry, .. data];
        var messages = InstanceProtocol.SplitClientMessages(all, out conn.Carry, out byte[] unknown);
        foreach (var (id, bytes) in messages)
        {
            conn.Packets.Received(id, bytes);
            if (ServerTrace.Packets && PacketHistory.Kept(sent: false, id))
                Console.WriteLine($"[packets] {remote} <= 0x{id:x4} {bytes.Length} B: {Convert.ToHexString(bytes)}");
        }
        if (unknown.Length > 0) conn.Packets.Received(0xffff, unknown);   // bytes no known message frames ("unframed")
        conn.UnframedRun = ClientInbox.UnframedRun(conn.UnframedRun, messages.Count, unknown.Length);
        if (conn.UnframedRun == ClientInbox.LostAfterUnframedReads)
            conn.CloseReason = $"the client's stream no longer decodes ({conn.UnframedRun} reads in a row framed nothing): closed, so the client can connect again";
        if (MoveTrace)
            foreach (var (id, bytes) in messages)
                if (id is 0x0024 or 0x0025 or 0x0190 or 0x0191)
                    Console.WriteLine($"[move-trace] qpc={readAt} recv 0x{id:x4} {Convert.ToHexString(bytes)}");
        foreach (var bytes in inbox.Post(messages, unknown, readAt))
        {
            // Only a lockstep client's world counts frames behind (0x142183930), so only it reports any.
            if (conn.LockstepServed && bytes[2] != 0) conn.LockstepClient = true;
            if (conn.Ticks++ % 50 == 0 && ServerTrace.Ticks) Console.WriteLine($"[gateway] {remote} <= 0x001b tick #{conn.Ticks}: {Convert.ToHexString(bytes)} (answered with 0x001c)");
            // Answered after the input read before it (ClientInbox.DeferTick); the ack index and the side channel are taken
            // when it goes out, so the answers keep the ticks' order.
            inbox.DeferTick(() => SendPong(stream, ciphers, InstanceProtocol.Pong(serverDelayMs: ClientInbox.ServerDelayMs,
                ackIndex: inbox.NextAckIndex(), sideChannel: conn.Hnc!.NextIdleU32())));
            var waited = Stopwatch.GetElapsedTime(readAt);
            if (waited > ClientInbox.SlowAfter)
                Console.WriteLine($"[gateway] {remote} slow tick #{conn.Ticks}: 0x001c sent {waited.TotalMilliseconds:0} ms after the 0x001b was read");
        }
    }

    // The connection's handler task: every message but the ticks, one read at a time and in order (ClientInbox). A handler
    // that runs longer than ClientInbox.SlowAfter is logged. An error or 0x153 closes the connection by cancelling the read.
    internal static async Task RunHandlers(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ClientInbox inbox, CancellationTokenSource reading)
    {
        await Task.Yield();   // the read loop starts at once; the handlers run beside it
        ClientInbox.Batch? handling = null;
        try
        {
            await foreach (var batch in inbox.ReadAllAsync())
            {
                if (conn.Closed) break;
                handling = batch;
                long start = Stopwatch.GetTimestamp();
                try { await Logic.OnClientMessages(remote, stream, ciphers, batch.Messages, batch.Unknown, conn, batch.ReadAt); }
                finally { inbox.Handled(); }
                handling = null;
                conn.Performance.Elapsed(LatencyPhase.Handler, start);
                conn.Performance.MaybeLog(remote, inbox.PendingBatches);
                var took = Stopwatch.GetElapsedTime(start);
                if (took > ClientInbox.SlowAfter)
                {
                    var ids = batch.Messages.Select(m => $"0x{m.Id:x4}").Concat(batch.Unknown.Length > 1 ? [$"unframed 0x{batch.Unknown[0] << 8 | batch.Unknown[1]:x4}"] : []);
                    Console.WriteLine($"[gateway] {remote} slow handler: {string.Join(", ", ids)} took {took.TotalMilliseconds:0} ms (ticks answered meanwhile)");
                }
                if (conn.Closed) break;
            }
        }
        catch (Exception ex)
        {
            string ids = handling is null ? "" : " handling " + string.Join(", ", handling.Messages.Select(m => $"0x{m.Id:x4}"));
            conn.CloseReason = $"handler error{ids}: {ex.GetType().Name}: {ex.Message}";
            Console.WriteLine($"[gateway] {remote} error{ids}: {ServerLog.Describe(ex)}");
            if (ex is not (IOException or ObjectDisposedException)) conn.Packets.Print(remote, "before the handler error");
        }
        inbox.Complete();
        inbox.DiscardPending();
        lock (conn.Monsters)
        {
            conn.Closed = true;
            conn.InvalidateIncomingHits();
            Logic.Closing(conn);
        }
        reading.Cancel();
    }

    // Monster AI, one decision every MonsterAiCadence.StepMilliseconds of game time per connection (RunMonsterAi). A monster within its aggro range (MonsterVarieties MaxAgroRange, in
    // cells) of the player starts chasing: it walks (Move action 0x1ab, skill 0x2909; the client paths it) to its own cell
    // around the player (MonsterSurround), re-ordered when that cell has moved by 3+ cells. Beyond twice the range it gives up and stops.
    // Positions are the server's estimates (PlayerWalk at MonsterVarieties MovementSpeed cells/s).
    // The server frame clock (Agents/docs/protocol/07 §6): S→C 0x2b6 sets the frame length, then 0x2b5 ends a frame every
    // FrameMs. A lockstep client advances its world by exactly these frames, running the messages of each at its start (it
    // is drawn and its world runs, and its moves get a message box); a predictive client ignores them. A connection that
    // serves lockstep runs Game.RunLockstepFrames instead.
    internal static async Task RunServerFrames(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        try
        {
            await Send(remote, stream, ciphers, InstanceProtocol.FrameLength(FrameMs), $"0x02b6 server frame length {FrameMs} ms");
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(FrameMs));
            while (!conn.Closed && await timer.WaitForNextTickAsync())
            {
                await Send(remote, stream, ciphers, InstanceProtocol.FrameEnd(), null);
                conn.ServerFrame++;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    internal static async Task WhilePaused(ClientState conn)
    {
        while (conn.Paused && !conn.Closed) await Task.Delay(100);
    }

    // One sender at a time per connection: the Salsa20 keystream must be applied and written in the same order (the read
    // loop, the monster AI tick and the transfer timer all send).
    // `still`, when given, is checked inside the connection's send gate and drops the packet when false: a packet decided
    // before another task's packets were sent can check that it still applies (SendFlinch).
    // Projectile impacts commit their short locked damage mutation inside this gate before relocation.
    // A factory samples clock-sensitive state and constructs its packet after earlier sends; null sends nothing.
    /// <summary>Sees every message before it is encrypted and written (the module's mirror of shared monsters to the other
    /// clients); it must copy what it keeps.</summary>
    internal static Action<ConnectionCiphers, byte[]>? Outgoing;

    // How often a state sync that an update loop offers every 16-32 ms may take the send gate. The packets are
    // idempotent snapshots, so a skipped step publishes nothing lost, and the input handlers stop queueing behind them.
    internal const double SyncPublishMilliseconds = 100;

    internal static async Task Send(string remote, NetworkStream stream, ConnectionCiphers ciphers, byte[] bytes, string? log,
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
            sentId = (ushort)(bytes[0] << 8 | bytes[1]);   // Temporary lag instrumentation: the id, read before Write encrypts it
            // What this client is sent may concern others too (the instance's shared monsters: SharedMonsters.cs).
            if (Outgoing is { } outgoing) outgoing(ciphers, bytes);
            string? trace = MoveTrace && bytes.Length >= 6 && (bytes[0] << 8 | bytes[1]) is 0x01ab or 0x01b7 or 0x01ba
                ? Convert.ToHexString(bytes) : null;
            await Write(stream, ciphers, bytes);
            if (trace is not null) Console.WriteLine($"[move-trace] qpc={Stopwatch.GetTimestamp()} send {trace}");
        }
        finally
        {
            long held = Stopwatch.GetTimestamp();
            var hold = Stopwatch.GetElapsedTime(heldAt, held);
            gate.Release();
            performance.Elapsed(LatencyPhase.SendHold, heldAt);
            if (sentId is ushort id) performance.Send(id, held - heldAt);
            else if (ConnectionPerformance.LagProbe) performance.EmptySend(SendCaller(), held - heldAt);
            // Temporary: which send holds the gate while input waits behind it.
            if (ConnectionPerformance.LagProbe && hold > TimeSpan.FromMilliseconds(50))
                Console.WriteLine($"[slow-send] {remote} {log ?? "?"} wait={Stopwatch.GetElapsedTime(waitingAt, heldAt).TotalMilliseconds:0} "
                    + $"hold={hold.TotalMilliseconds:0} bytes={bytes.Length}"
                    + (log is null ? " at=" + string.Join(" <- ", new StackTrace(1, false).GetFrames()
                        .Take(4).Select(frame => frame.GetMethod()?.Name ?? "?")) : ""));
        }
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
    internal static async Task<bool> SendBatched(string remote, NetworkStream stream, ConnectionCiphers ciphers, Func<Task<bool>> body)
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
                if (Outgoing is { } outgoing) outgoing(ciphers, bytes);
                all.AddRange(bytes);
            }
            if (all.Count > 0) await Write(stream, ciphers, [.. all]);
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
    internal static async Task Write(NetworkStream stream, ConnectionCiphers ciphers, byte[] bytes)
    {
        var wire = WireGates.GetValue(ciphers, _ => new SemaphoreSlim(1, 1));
        var performance = MetricsFor(ciphers);
        long waitingAt = Stopwatch.GetTimestamp();
        await wire.WaitAsync();
        performance.Elapsed(LatencyPhase.WireWait, waitingAt);
        long heldAt = Stopwatch.GetTimestamp();
        try
        {
            ciphers.Send.Process(bytes);
            long writeAt = Stopwatch.GetTimestamp();
            try { await stream.WriteAsync(bytes); }
            finally { performance.Elapsed(LatencyPhase.SocketWrite, writeAt); }
        }
        finally { wire.Release(); performance.Elapsed(LatencyPhase.WireHold, heldAt); }
    }

    // A tick's answer (0x1c) only reports that the server is there: it needs no game state, so it takes the wire alone
    // and goes out between two other packets instead of waiting behind their state callbacks (combat, Rage, stats).
    internal static Task SendPong(NetworkStream stream, ConnectionCiphers ciphers, byte[] bytes) => Write(stream, ciphers, bytes);

    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ConnectionCiphers, SemaphoreSlim> WireGates = new();
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ConnectionCiphers, ConnectionPerformance> ConnectionMetrics = new();
    internal static ConnectionPerformance MetricsFor(ConnectionCiphers ciphers) => ConnectionMetrics.GetValue(ciphers, _ => new());
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ConnectionCiphers, PacketHistory> PacketHistories = new();
    internal static PacketHistory HistoryFor(ConnectionCiphers ciphers) => PacketHistories.GetValue(ciphers, _ => new());
}
