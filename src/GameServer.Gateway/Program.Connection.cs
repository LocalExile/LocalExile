using System.Diagnostics;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using WalkEstimator = GameServer.Core.World.WalkEstimator;

partial class Program
{
    static void SaveUnsavedProgressNow(string? why)
    {
        try
        {
            CharacterSaves.SaveExperience();
            PlayerInventories.SaveAllUnsaved();
            if (why is not null) Console.WriteLine($"[gateway] unsaved progress saved ({why})");
        }
        catch (Exception e)
        {
            // Kept in memory: the next save tries again.
            Console.WriteLine($"[gateway] unsaved progress not saved{(why is null ? "" : $" ({why})")}: {e.GetType().Name}: {e.Message}");
        }
    }

    static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }

    // The real connection's admission path, shared with regression tests before any area packets are published.
    internal static int PrepareCharacterForArea(string remote, ClientState conn, int level)
    {
        CheckAscendancyClientCompatibility(conn);
        ValidateSavedProgress(conn, CharacterSaves.Load(CharacterName(conn)));
        PlayerInventories.SetClass(CharacterName(conn), conn.ClassRow);   // before its items are first used (start kit)
        // Existing items must load successfully before LoadProgress can migrate gold or normalize passives.
        // A new character's start kit is built and saved only after the read-only progress preflight above succeeds.
        var startItems = ItemsOf(conn);
        level = LoadProgress(conn, level);
        // Towns and hideouts refill the flasks, before the inventories go to the client.
        if ((WaypointCatalog.Areas.Find(conn.Layout.Hash) is { IsTown: true } || HideoutCatalog.Hideouts.Any(h => h.AreaHash16 == conn.Layout.Hash))
            && FillFlasks(startItems, int.MaxValue).Count > 0)
            Console.WriteLine($"[gateway] {remote} flasks refilled in town");
        // A sale still open when the last connection ended (a disconnect, a crash) ended as a cancel: every offered
        // item is in its cell (it never left it).
        if (startItems.CancelOffers().Count is > 0 and var cancelled)
            Console.WriteLine($"[gateway] {remote} {cancelled} item(s) offered in an unfinished sale are back in their cells");
        return level;
    }

    static async Task HandleAsync(TcpClient client, InstanceKeyring keyring)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        // Nagle off: it would hold a small 0x1c back until the previous write is acknowledged (up to ~200 ms, issue #46).
        client.NoDelay = true;
        Console.WriteLine($"[gateway] CONNECT {remote} at {DateTime.Now:HH:mm:ss.fff}");
        long total = 0;
        uint ticket = 0;
        ClientState? left = null;
        bool prepared = false;
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
                ushort areaHash = resolved.Area;
                uint seed = resolved.Seed;
                ulong instanceId = resolved.Instance;
                var layout = AreaLayouts.For(areaHash, seed);
                AreaLayouts.Announce(layout, seed);
                Console.WriteLine($"[gateway] {remote} ticket 0x{ticket:x8}: {resolved.Character ?? "?"} → instance {instanceId}, {layout.Describe()}");

                ConnectionCiphers ciphers = keyring.Ciphers(opening.Ticket);
                await stream.WriteAsync(InstanceProtocol.StartEncryption());
                Console.WriteLine($"[gateway] {remote} => 0x0011 (start encryption)");

                // Anything after the opening was already encrypted by the client.
                var conn = new ClientState(layout, ticket, seed) { Character = resolved.Character, CosmeticPortalVariation = resolved.Character is { Length: > 0 } savedCharacter ? CharacterSaves.PortalCosmetic(savedCharacter) : (ushort)0, Account = resolved.Account, League = resolved.League, Hardcore = resolved.Hardcore, CameFrom = AreaLayouts.CameFrom.TryRemove(ticket, out var from) ? from : null, Instance = Instances.Enter(instanceId, resolved.Created), InstanceNumber = instanceId, LastTown = resolved.LastTown, ClassRow = Skills.RowOfClass(resolved.Class), Pools = CharacterStats.For(Skills.ClassFor(Skills.RowOfClass(resolved.Class)), resolved.Level), Hnc = new HncSender(keyring.Key(opening.Ticket)), Keyring = keyring };
                left = conn;
                // Another connection's requests (a trade, a party invite) reach this client through its own send gate.
                conn.Outbox = (bytes, log) => Send(remote, stream, ciphers, bytes, log);
                conn.Commands = Commands(remote, stream, ciphers, conn);
                conn.ArriveAtWaypoint = WaypointArrivals.TryRemove(ticket, out _) || HideoutCatalog.ByArea(layout.Hash) is not null;
                conn.ArriveAt = SpotArrivals.TryRemove(ticket, out var spot) ? spot : null;
                int level = PrepareCharacterForArea(remote, conn, resolved.Level);
                prepared = true;
                var area = new AreaInstance(InstanceId: (uint)instanceId, AreaHash: areaHash, Seed: seed, Level: 0,
                    Text: resolved.League ?? "", LeagueStats: LeagueStatsFor(conn.MainAscendancy));
                await Send(remote, stream, ciphers, area.Encode(), $"0x001d {area}; ascendancy family {conn.MainAscendancy.Selection?.Family}");
                if (level != resolved.Level)
                {
                    // The saved experience is the character's level: Auth's (the ticket's) was not updated at the last level-up.
                    var fixedLevel = await SynchronizeAccountLevel(conn);
                    Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: level {level} from the saved experience (the account had {resolved.Level})" +
                        (fixedLevel.Ok ? "" : $"; not saved to the account: {fixedLevel.Error}"));
                }
                conn.PlayerLife = conn.PlayerMaxLife;
                conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, refill: true);
                conn.AuraDeathHandled = false;
                conn.PlayerMana = (uint)UnreservedMana(conn);
                Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: {Skills.ClassFor(conn.ClassRow).ObjectType} (Characters row {conn.ClassRow}), level {conn.Pools.Level} (experience {conn.Experience}, {conn.Passives.Count} passive(s), life {conn.Pools.Life}, mana {conn.Pools.Mana}, attack speed +{conn.Pools.AttackSpeedPct}%), league {conn.League ?? "?"}");
                // The read loop answers ticks; everything else runs in order on the handler task (ClientInbox, issue #46).
                var inbox = new ClientInbox();
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
                        if (n == 0) break;
                        total += n;
                        await OnClientData(remote, stream, ciphers, buffer.AsSpan(0, n).ToArray(), conn, inbox, Stopwatch.GetTimestamp());
                    }
                }
                catch (OperationCanceledException) when (reading.IsCancellationRequested) { }
                finally
                {
                    lock (conn.Monsters)
                    {
                        conn.Closed = true;
                        conn.InvalidateIncomingHits();
                        FortificationOf(conn).Clear();
                        conn.Rage.Clear();
                    }
                    inbox.Complete();
                    await handlers;
                    // An open trade ends with its owner: the other side gets its items back in view and the window closes.
                    try { await CancelPlayerTrade(conn, "disconnected"); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gateway] {remote} error: {ex.GetType().Name}: {ex.Message}");
        }
        if (left is not null)
        {
            // The connection ends (a logout, an area change, a crash of the client): what waits in memory is saved now.
            try
            {
                // A refused admission must not load/start inventories or flush a previous connection's cached copy.
                if (prepared)
                {
                    CharacterSaves.SaveExperience(CharacterName(left));
                    ItemsOf(left).SaveUnsaved();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[gateway] {remote} unsaved progress not saved at disconnect (kept for the next save): {e.GetType().Name}: {e.Message}");
            }
            // The instance keeps its monsters as they are now, for the next connection (#12).
            if (left.MonstersPlaced)
                lock (left.Monsters) left.Instance.Monsters = SavedMonster.Save(left.Monsters.Values, left.Clock.Elapsed.TotalSeconds, MonsterEmerge.Unrisen(left));
            left.Instance.RemovePlayer(left.PeerId);
            Instances.Leave(left.Instance);
        }
        if (ticket != 0) await InstanceControl.CallAsync(new ControlRequest { Op = "leave", Ticket = ticket });
        Console.WriteLine($"[gateway] DISCONNECT {remote} total={total}");
    }

    // The read loop's part: decrypts one read, frames it (InstanceProtocol.SplitClientMessages), answers its 0x1b ticks with
    // 0x1c at once and queues the rest for RunHandlers. readAt is when the read completed: a tick answered more than
    // ClientInbox.SlowAfter later is logged.
    static async Task OnClientData(string remote, NetworkStream stream, ConnectionCiphers ciphers, byte[] data, ClientState conn,
        ClientInbox inbox, long readAt)
    {
        ciphers.Receive.Process(data);
        byte[] all = [.. conn.Carry, .. data];
        var messages = InstanceProtocol.SplitClientMessages(all, out conn.Carry, out byte[] unknown);
        if (MoveTrace)
            foreach (var (id, bytes) in messages)
                if (id is 0x0024 or 0x0025 or 0x0190 or 0x0191)
                    Console.WriteLine($"[move-trace] qpc={readAt} recv 0x{id:x4} {Convert.ToHexString(bytes)}");
        foreach (var bytes in inbox.Post(messages, unknown, readAt))
        {
            if (conn.Ticks++ % 50 == 0) Console.WriteLine($"[gateway] {remote} <= 0x001b tick #{conn.Ticks}: {Convert.ToHexString(bytes)} (answered with 0x001c)");
            await SendPong(stream, ciphers, InstanceProtocol.Pong(serverDelayMs: ClientInbox.ServerDelayMs, ackIndex: inbox.NextAckIndex(), sideChannel: conn.Hnc!.NextIdleU32()));
            var waited = Stopwatch.GetElapsedTime(readAt);
            if (waited > ClientInbox.SlowAfter)
                Console.WriteLine($"[gateway] {remote} slow tick #{conn.Ticks}: 0x001c sent {waited.TotalMilliseconds:0} ms after the 0x001b was read");
        }
    }

    // The connection's handler task: every message but the ticks, one read at a time and in order (ClientInbox). A handler
    // that runs longer than ClientInbox.SlowAfter is logged. An error or 0x153 closes the connection by cancelling the read.
    static async Task RunHandlers(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ClientInbox inbox, CancellationTokenSource reading)
    {
        await Task.Yield();   // the read loop starts at once; the handlers run beside it
        try
        {
            await foreach (var batch in inbox.ReadAllAsync())
            {
                if (conn.Closed) break;
                long start = Stopwatch.GetTimestamp();
                await OnClientMessages(remote, stream, ciphers, batch.Messages, batch.Unknown, conn, batch.ReadAt);
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
            Console.WriteLine($"[gateway] {remote} error: {ex.GetType().Name}: {ex.Message}");
        }
        lock (conn.Monsters)
        {
            conn.Closed = true;
            conn.InvalidateIncomingHits();
            FortificationOf(conn).Clear();
            conn.Rage.Clear();
        }
        reading.Cancel();
    }

    // Monster AI, one tick every MonsterAiTickMs per connection. A monster within its aggro range (MonsterVarieties MaxAgroRange, in
    // cells) of the player starts chasing: it walks (Move action 0x1ab, skill 0x2909; the client paths it) to its own cell
    // around the player (MonsterSurround), re-ordered when that cell has moved by 3+ cells. Beyond twice the range it gives up and stops.
    // Positions are the server's estimates (WalkEstimator at MonsterVarieties MovementSpeed cells/s).
    // The server frame clock (Agents/docs/protocol/07 §6): S→C 0x2b6 sets the frame length, then 0x2b5 ends a frame every
    // FrameMs. A lockstep client advances its world by exactly these frames, running the messages of each at its start (it
    // is drawn and its world runs; its moves are not served yet); a predictive client ignores them.
    static async Task RunServerFrames(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
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

    // The Escape menu (C→S 0x1d4, 22-pause.md) pauses the area outside towns and hideouts, where the client does not ask
    // anyway: the Clock stops, the monster AI and the player tick skip their steps, and S→C 0x1d3 pauses the client's world.
    static async Task SetPaused(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool pause)
    {
        ushort here = conn.Layout.Hash;
        if (pause && (WaypointCatalog.Areas.Find(here) is { IsTown: true } || HideoutCatalog.Hideouts.Any(h => h.AreaHash16 == here)))
        {
            Console.WriteLine($"[gateway] {remote} <= 0x01d4 pause: in a town or hideout, not paused");
            return;
        }
        // Pause is a barrier: commit the preceding combat interval before freezing the world clock.
        using (ProjectileMutation(conn)) { if (pause) conn.Clock.Stop(); else conn.Clock.Start(); }
        await Send(remote, stream, ciphers, InstancePause.State(pause), $"0x01d3 {(pause ? "paused" : "resumed")} (C→S 0x01d4 {(pause ? 1 : 0)})");
    }

    static async Task WhilePaused(ClientState conn)
    {
        while (conn.Paused && !conn.Closed) await Task.Delay(100);
    }

    // One sender at a time per connection: the Salsa20 keystream must be applied and written in the same order (the read
    // loop, the monster AI tick and the transfer timer all send).
    // `still`, when given, is checked inside the connection's send gate and drops the packet when false: a packet decided
    // before another task's packets were sent can check that it still applies (SendFlinch).
    // Projectile impacts commit their short locked damage mutation inside this gate before relocation.
    // A factory samples clock-sensitive state and constructs its packet after earlier sends; null sends nothing.
    internal static async Task Send(string remote, NetworkStream stream, ConnectionCiphers ciphers, byte[] bytes, string? log,
        Func<bool>? still = null, Func<byte[]?>? atGate = null)
    {
        var gate = SendGates.GetValue(ciphers, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (ProjectileOutputGuard.Value is { } projectileGuard && !projectileGuard()) return;
            if (still is not null && !still()) return;
            if (atGate is not null)
            {
                if (atGate() is not { } current) return;
                bytes = current;
            }
            if (log is not null) Console.WriteLine($"[gateway] {remote} => {log}: {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 96)))}");
            string? trace = MoveTrace && bytes.Length >= 6 && (bytes[0] << 8 | bytes[1]) is 0x01ab or 0x01b7 or 0x01ba
                ? Convert.ToHexString(bytes) : null;
            await Write(stream, ciphers, bytes);
            if (trace is not null) Console.WriteLine($"[move-trace] qpc={Stopwatch.GetTimestamp()} send {trace}");
        }
        finally { gate.Release(); }
    }

    // The wire itself: the keystream must be applied and written in one order, so encryption and the write share this
    // short gate. Send's gate orders the packets and runs their state callbacks; it never holds this one while waiting.
    static async Task Write(NetworkStream stream, ConnectionCiphers ciphers, byte[] bytes)
    {
        var wire = WireGates.GetValue(ciphers, _ => new SemaphoreSlim(1, 1));
        await wire.WaitAsync();
        try
        {
            ciphers.Send.Process(bytes);
            await stream.WriteAsync(bytes);
        }
        finally { wire.Release(); }
    }

    // A tick's answer (0x1c) only reports that the server is there: it needs no game state, so it takes the wire alone
    // and goes out between two other packets instead of waiting behind their state callbacks (combat, Rage, stats).
    static Task SendPong(NetworkStream stream, ConnectionCiphers ciphers, byte[] bytes) => Write(stream, ciphers, bytes);

    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ConnectionCiphers, SemaphoreSlim> WireGates = new();
}
