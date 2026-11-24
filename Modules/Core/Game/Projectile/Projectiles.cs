using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;
using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadProjectilesData()
    {
        ProjectileStateMachines = LoadProjectileStateMachines();
    }

    static Dictionary<string, StateMachineData> ProjectileStateMachines = default!;

    static Dictionary<string, StateMachineData> LoadProjectileStateMachines()
    {
        var states = ObjectTypes.SharedStates();
        var machines = new Dictionary<string, StateMachineData>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, row) in ObjectTypes.All())
        {
            if (!path.StartsWith("Metadata/Projectiles/", StringComparison.OrdinalIgnoreCase)) continue;
            if (ObjectTypes.Components(row) is [.., "Projectile", "StateMachine"]
                && ObjectTypes.StateMachine(row, states) is { } machine)
                machines[path] = machine;
        }
        return machines;
    }

    // Everything a flight object's or an area effect's create sends. A projectile's blob wakes it during the create, so it
    // gets no 0x1a9; an effect is created asleep and gets its 0x1a9 in the same send (07 section 3).
    public static byte[] CreateProjectileObject(uint id, ProjectileObject obj)
    {
        if (obj.StateMachine is null && ProjectileStateMachines.TryGetValue(obj.TypePath, out var machine))
            obj = obj with { StateMachine = machine };
        return InstanceEntities.CreateInWakeZone(id, InstanceEntities.ObjectTypeHash(obj.TypePath), obj.Blob(), headerU32: uint.MaxValue);
    }

    public static byte[] CreateEffectObject(uint id, EffectObject effect) =>
        InstanceEntities.CreateInWakeZone(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob());

    /// <summary>One gameplay pump and one presentation consumer per connection, regardless of projectile count.</summary>
    public sealed partial class ProjectileSession : IProjectileWorld, IProjectileRuntime
    {
        public readonly Player Player;
        readonly int epoch;
        readonly Channel<Func<Task>> output = Channel.CreateBounded<Func<Task>>(
            new BoundedChannelOptions(8192) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        // One experience update for as many kills as arrive before the record is written: a pack kill commits one per
        // corpse, and a hundred of them were a hundred packets and a hundred gate turns queued ahead of the same
        // burst's corpse explosions and loot (live 2026-11-17: the tail of a burst landed about two seconds late).
        uint? experiencePending;
        bool experienceQueued;
        public void PlayerExperience(uint total)
        {
            experiencePending = total;
            if (experienceQueued) return;
            experienceQueued = true;
            WorldRecords(Player.Id, () =>
            {
                experienceQueued = false;
                if (experiencePending is not { } value) return null;
                experiencePending = null;
                return [InstanceEntities.PlayerExperienceUpdate(Player.Id, value)];
            });
        }
        sealed class Visual(uint id, ProjectileHeight height)
        {
            public readonly uint Id = id;
            public ProjectileHeight Height = height;
            public bool Created, Ended, SecondFormStarted;
            public ProjectileArc? Arc;
            // Where the object is created (the release point) and whether its first move has been sent from there.
            public ProjectileVector SpawnedAt;
            public bool Glided;
            // The move the client has to make to follow the flight as it stands now, and whether one intent is already
            // queued to write it. A redirect replaces the pending move instead of being dropped as superseded.
            public byte[][]? PendingGlide;
            public bool GlideQueued;
            // An ended flight whose stop keeps the object (an impact clip, a stuck arrow) is still created when its
            // creation snapshot runs late: the shot then appears at its impact rather than not at all.
            public bool StaysAfterEnd;
        }
        readonly Dictionary<long, Visual> visuals = [];
        readonly Dictionary<long, ProjectileAimHeight.Ray> flightRays = [];
        readonly Dictionary<SpawnedMonster, int> generations = new(ReferenceEqualityComparer.Instance);
        int generation, seed;
        bool invalid;
        readonly IDisposable? lifetime;
        public ProjectileRuntime Runtime { get; }
        public Task OutputCompleted { get; }
        public bool Active => CurrentGameRules && !invalid && Player.ProjectileActive(epoch);

        public ProjectileSession(Player player)
        {
            (Player, epoch) = (player, player.ProjectileEpoch);
            Runtime = new(this);
            OutputCompleted = RunOutput();
            lifetime = CombatLifetime?.Own(new ConnectionProjectileRetirement(player, this));
        }

        public void Start() => _ = Pump();
        async Task Pump()
        {
            try
            {
                // The area's frame steps the flights (AreaFrames, after the other loops), not a timer of their own.
                while (Active)
                {
                    await Player.Frames.Wait(Player, PlayerUpdateRole.Projectiles, 1000 / 30, CancellationToken.None);
                    if (Active) Advance();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[gateway] {Player.Connection.Remote} projectile pump failed: {ex}");
                Player.Closed = true;
            }
            finally { Invalidate(); }
        }

        uint pendingGemXp;
        int remainingMonstersQueued;

        public void AddGemExperience(uint gemXp)
        {
            if (gemXp == 0) return;
            long sum = (long)pendingGemXp + gemXp;
            pendingGemXp = (uint)Math.Min(sum, uint.MaxValue);
        }

        void FlushPendingGemExperience()
        {
            if (pendingGemXp == 0) return;
            uint gain = pendingGemXp;
            pendingGemXp = 0;
            var (gemPackets, leveled) = CommitGemExperience(Player, gain);
            foreach (var packet in gemPackets) Packet(packet);
            if (leveled) Enqueue(() => SendSkills(Player));
        }

        public void QueueRemainingMonsters()
        {
            if (Interlocked.Exchange(ref remainingMonstersQueued, 1) != 0) return;
            EnqueueLater(() =>
            {
                Interlocked.Exchange(ref remainingMonstersQueued, 0);
                return SendRemainingMonsters(Player);
            });
        }

        // Coalesce multi-kill flask charge updates during a projectile step and flush them synchronously at the end of
        // Advance/AdvanceTo under Player.FlaskBarGate so 0x00bf revisions stay strictly ordered with ChangeFlaskBar.
        readonly Lock flaskBarGate = new();
        HashSet<uint>? flasksPending;
        bool flaskFlushQueued;
        bool stepping;
        public void FlaskBarChanged(IReadOnlyList<InventoryItem> changed)
        {
            if (changed.Count == 0) return;
            lock (flaskBarGate)
            {
                flasksPending ??= [];
                foreach (var item in changed) flasksPending.Add(item.Id);
            }
            if (!stepping) FlushPendingFlaskBar();
        }

        byte[]? TakePendingFlaskPacketUnderGate(bool clearQueued)
        {
            HashSet<uint>? pending;
            lock (flaskBarGate)
            {
                if (clearQueued) flaskFlushQueued = false;
                pending = flasksPending;
                flasksPending = null;
            }
            if (pending is not { Count: > 0 }) return null;
            var items = ItemsOf(Player);
            var current = new List<InventoryItem>(pending.Count);
            foreach (var it in items.Items(PlayerInventories.Flask))
                if (pending.Contains(it.Id)) current.Add(it);
            return current.Count == 0 ? null : InstanceItems.InventoryChange(
                PlayerInventories.InventoryId(PlayerInventories.Flask),
                items.NextRevision(PlayerInventories.Flask), [.. current.Select(c => c.Id)], current);
        }

        void FlushPendingFlaskBar()
        {
            lock (flaskBarGate)
                if (flasksPending is not { Count: > 0 } || flaskFlushQueued) return;
            if (!Active) return;
            if (Player.FlaskBarGate.Wait(0))
            {
                try
                {
                    if (TakePendingFlaskPacketUnderGate(clearQueued: false) is { } packet) Packet(packet);
                }
                finally { Player.FlaskBarGate.Release(); }
                return;
            }
            bool queue;
            lock (flaskBarGate)
            {
                queue = !flaskFlushQueued && flasksPending is { Count: > 0 };
                if (queue) flaskFlushQueued = true;
            }
            if (queue)
                Enqueue(async () =>
                {
                    await Player.FlaskBarGate.WaitAsync();
                    try
                    {
                        var packet = TakePendingFlaskPacketUnderGate(clearQueued: true);
                        if (Active && packet is not null) await Send(Player, packet, null);
                    }
                    finally { Player.FlaskBarGate.Release(); }
                });
        }

        public void Advance()
        {
            // Match the existing XP -> connection lock order. Kill reactions may gain XP synchronously.
            lock (LevelUpdatesFor(Player).Gate)
            {
                if (!Active) { Invalidate(); return; }
                if (!Player.Paused)
                {
                    stepping = true;
                    try
                    {
                        DrainPendingExplosiveDetonations();
                        TickChannelTriggers(Player, this, AreaTime(Player));
                        Runtime.AdvanceTo(Math.Max(Runtime.Now, ProjectileRuntime.Time(AreaTime(Player))));
                    }
                    finally
                    {
                        stepping = false;
                        FlushPendingGemExperience();
                        FlushPendingFlaskBar();
                    }
                }
            }
        }

        public void AdvanceTo(long at)
        {
            lock (LevelUpdatesFor(Player).Gate)
            {
                if (!Active) { Invalidate(); return; }
                stepping = true;
                try
                {
                    DrainPendingExplosiveDetonations();
                    Runtime.AdvanceTo(at);
                }
                finally
                {
                    stepping = false;
                    FlushPendingGemExperience();
                    FlushPendingFlaskBar();
                }
            }
        }

        public void Schedule(long at, Func<bool> valid, Func<ProjectileUse?> release) => Schedule(at, valid, release, null);

        internal void Schedule(long at, Func<bool> valid, Func<ProjectileUse?> release, Action? released)
        {
            if (Active)
                Runtime.Schedule(Math.Max(Runtime.Now, at), () =>
                {
                    if (!Active || !valid()) return;
                    try
                    {
                        if (release() is not { } use) return;
                        ReleaseNow(use);
                    }
                    catch (InvalidOperationException ex)
                    {
                        Console.WriteLine($"[gateway] {Player.Connection.Remote} projectile path unavailable: {ex.Message}");
                        return;
                    }
                    released?.Invoke();
                });
        }

        /// <summary>Releases a resolved use at the runtime's current instant. Call in the area's frame.</summary>
        public long ReleaseNow(ProjectileUse use)
        {
            var seeded = use with { Seed = ++seed };
            long useId = Runtime.Release(seeded);
            SacredWispsOnRelease(seeded);
            if (ServerTrace.Projectiles && !Quiet(seeded.Payload))
                Console.WriteLine($"[gateway] {Player.Connection.Remote} projectile release t={Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond:0.000000} use={useId} "
                    + $"skill={PayloadLabel(seeded.Payload)} metadata={seeded.Profile.Id} count={seeded.Profile.Count} "
                    + $"origin={seeded.Origin} aim={seeded.Aim} speed={seeded.Profile.Speed:0.###}");
            return useId;
        }

        public double NowTicks => Runtime.Now;
        public long Release(ProjectileUse use) => ReleaseNow(use);

        public void Invalidate()
        {
            if (invalid) return;
            invalid = true;
            lifetime?.Dispose();
            ClearPods();
            ClearDamageGrounds();
            ClearKnifeBlades();
            ClearEnsnares();
            ClearExplosiveFuses();
            if (archer is { } currentArcher) EndMirage(currentArcher, retire: true);
            EndSacredWisps(retire: true);
            if (ShaperMemories.TryGetValue(Player, out var memory)) memory.RemoveActor?.Invoke(true);
            lock (flaskBarGate)
            {
                flasksPending = null;
                flaskFlushQueued = false;
            }
            Runtime.Clear();
            foreach (var visual in visuals.Values) Player.ProjectileObjects.Remove(visual.Id);
            visuals.Clear(); flightRays.Clear(); later.Clear(); output.Writer.TryComplete();
        }

        public void Enqueue(Func<Task> intent) => Write(intent, keepsWorldGroup: false);

        // keepsWorldGroup: an intent that a world group may be sent ahead of (an immediate packet, a later item's marker);
        // anything else closes the open group, so no record goes out before an intent queued ahead of it.
        void Write(Func<Task> intent, bool keepsWorldGroup)
        {
            if (!keepsWorldGroup) openWorld = null;
            if (!Active || output.Writer.TryWrite(intent)) return;
            // Never stall gameplay or silently lose ordered lifecycle/inventory packets on a slow socket.
            Console.WriteLine($"[gateway] {Player.Connection.Remote} projectile output backlog exceeded 8192 intents; closing connection");
            Player.Closed = true; Invalidate(); Player.Connection.Disconnect();
        }
        // A kill's aftermath (its loot roll, its explosion, the remaining-monsters count) waits behind the presentation
        // queued with it: one output queue ran them in order, so the next monster's life update of the same Fireball
        // explosion waited for the loot roll of the one before (live 2026-11-17: 60-160 ms between the life updates of one
        // explosion's kills). Such work goes back behind whatever was queued when its turn came, each item on its own turn,
        // for at most LaterYield, and the later work keeps its own order. A shorter bound (250 ms) let one pile's twelve
        // loot rolls run back to back and held the next Fireball's hits 300 ms.
        readonly System.Collections.Concurrent.ConcurrentQueue<(Func<Task> Work, long Since)> later = new();
        static readonly TimeSpan LaterYield = TimeSpan.FromSeconds(2);
        public void EnqueueLater(Func<Task> work)
        {
            later.Enqueue((work, System.Diagnostics.Stopwatch.GetTimestamp()));
            Write(RunLater, keepsWorldGroup: true);
        }
        Task RunLater()
        {
            if (!later.TryPeek(out var next)) return Task.CompletedTask;
            // Each later item has one marker in the queue (this one runs): the rest of the queue's count is presentation.
            if (output.Reader.Count > later.Count - 1 && System.Diagnostics.Stopwatch.GetElapsedTime(next.Since) < LaterYield)
            {
                Write(RunLater, keepsWorldGroup: true);
                return Task.CompletedTask;
            }
            return later.TryDequeue(out next) ? next.Work() : Task.CompletedTask;
        }

        public void PresentCharges() => Packet(() =>
        {
            _ = CharacterPlayerStats(Player);
            var (packet, changed) = CurrentChargePresentationLocked(Player, Player.Connection.Remote);
            if (changed) Player.Minions.StatRevision++;
            return packet;
        });
        public void Packet(byte[] bytes, string? log = null) => Packet(() => bytes, log);
        public void Packet(Func<byte[]?> snapshot, string? log = null) => Write(async () =>
        {
            while (Active)
            {
                bool completed = false;
                await Send(Player, [], log, atGate: () =>
                {
                    if (!Active || Player.Paused) return null;
                    completed = true;
                    return snapshot();
                });
                if (completed) return;
                await AreaDelay(Player, 0.016);
            }
        }, keepsWorldGroup: true);

        public void World(uint id, byte[] bytes, string? log = null)
        {
            var prepared = bytes.ToArray();
            World(id, () => prepared, log);
        }
        public void World(uint id, Func<byte[]?> snapshot, string? log = null) =>
            WorldRecords(id, () => snapshot() is { } bytes ? [bytes] : null, log);
        public void Create(uint id, Func<byte[]?> snapshot, bool wake = false, string? log = null) =>
            WorldRecords(id, () => snapshot() is { } bytes ? [bytes] : null, log, create: true, wake: wake);
        // A lockstep client's plain world records queued together (one projectile step's hits: each target's life, its hit
        // message, its recovery) go out in one admission, so they share a frame: one by one, each waited for the gate
        // and the area's frame behind the AI and the other loops, and one explosion's twelve life updates reached the
        // client over two or three frames, 80-130 ms after it (sync trace 2026-11-18). A record for an object whose
        // removal is under way drops out; a group that cannot be admitted goes record by record as before. An object's
        // create or removal queued meanwhile (a flight's end, an explosion's effect) bars only that object: its later
        // records start a group behind it, the other objects' records still join (a create closing the group split one
        // explosion's life updates over three frames).
        sealed class WorldGroup
        {
            public readonly List<(uint Id, Func<byte[][]?> Snapshot, string? Log)> Items = [];
            public readonly HashSet<uint> Barred = [];
        }
        WorldGroup? openWorld;


        // A projectile's create and the move it already owes the client leave in one admission. The client draws the
        // object where it is created and moves it only on 0x1ba, so a shot whose flight was over before a second record
        // was projected stood at its release point until its removal (live 2026-11-17: most Kinetic Blast bolts).
        // Lockstep writes the move in the frame after the create (AfterBirth, ordered by the same admission); a
        // predictive client gets both records in one write. Neither waits for the birth: a lockstep create is written
        // at the next frame end, and holding the session's queue for every object was what left the presentation of a
        // 130 ms volley about a quarter of a second behind the simulation.
        public void CreateFlight(uint id, Func<byte[]?> create, Func<byte[][]?> glide, string? log = null) =>
            Enqueue(() => CreateFlightNow(id, create, glide, log));

        async Task CreateFlightNow(uint id, Func<byte[]?> create, Func<byte[][]?> glide, string? log)
        {
            while (Active)
            {
                bool completed = false;
                byte[][]? Project()
                {
                    if (!Active || Player.Paused) return null;
                    completed = true;
                    if (create() is not { } bytes) return null;
                    if (glide() is not { } moves) return [bytes];
                    return [bytes, .. moves.Where(m => m.Length > 0)];
                }
                if (!Player.Connection.LockstepClient && !InAreaFrame(Player))
                    await Send(Player, [], log, atGate: () => Project() is { } records
                        ? [.. records.SelectMany(p => p)] : null);
                else
                    await SendWorldBatch(Player, [], admission =>
                    {
                        if (Project() is not { } records) return null;
                        var birth = admission.NewObject(id);
                        var created = records[0].ToArray();
                        BinaryPrimitives.WriteUInt32BigEndian(created.AsSpan(6), uint.MaxValue);
                        BinaryPrimitives.WriteUInt16BigEndian(created.AsSpan(10), 0);
                        List<OutputRecord> batch = [OutputRecord.Create(birth, created, log)];
                        foreach (var move in records[1..])
                            batch.Add(OutputRecord.AfterBirth(birth, RecordOrder(move, id), move, log));
                        return new([.. batch]);
                    });
                if (completed || Player.Connection.WorldOutput.Closed) return;
                await AreaDelay(Player, 0.016);
            }
        }
        public void WorldRecords(uint id, Func<byte[][]?> snapshot, string? log = null, bool create = false,
            bool remove = false, bool wake = false)
        {
            if (!Player.Connection.LockstepClient)
            {
                Enqueue(() => SendWorldRecordsNow(id, snapshot, log, create, remove, wake));
                return;
            }
            if (create || remove)
            {
                openWorld?.Barred.Add(id);
                Write(() => SendWorldRecordsNow(id, snapshot, log, create, remove, wake), keepsWorldGroup: true);
                return;
            }
            if (openWorld is { } open && !open.Barred.Contains(id))
            {
                open.Items.Add((id, snapshot, log));
                return;
            }
            var group = new WorldGroup();
            group.Items.Add((id, snapshot, log));
            Write(() => SendWorldGroup(group.Items), keepsWorldGroup: false);
            openWorld = group;
        }

        async Task SendWorldGroup(List<(uint Id, Func<byte[][]?> Snapshot, string? Log)> group)
        {
            if (ReferenceEquals(openWorld?.Items, group)) openWorld = null;
            if (group.Count == 1)
            {
                await SendWorldRecordsNow(group[0].Id, group[0].Snapshot, group[0].Log, false, false, false);
                return;
            }
            var world = Player.Connection.WorldOutput;
            var targets = new Dictionary<uint, WorldObject>();
            foreach (var (id, _, _) in group)
                if (!targets.ContainsKey(id) && world.PublishedObject(id) is { } target
                    && target.State != WorldObject.BirthState.Retired && !target.RemovalRequested) targets[id] = target;
            while (Active)
            {
                bool completed = false, paused = false;
                bool admitted = await SendWorldBatch(Player, [.. targets.Values.Distinct()], _ =>
                {
                    if (!Active || Player.Paused) { paused = true; return null; }
                    completed = true;
                    List<OutputRecord> records = [];
                    foreach (var (id, snapshot, log) in group)
                        if (targets.TryGetValue(id, out var target) && snapshot() is { } bytes)
                            foreach (var record in bytes)
                                if (record.Length > 0) records.Add(PresentedRecord(target, record, log));
                    return records.Count == 0 ? null : new([.. records]);
                });
                // Admitted, or projected with nothing to send: done. Refused before its projection (a target not ready at
                // the gate) and not for a pause: record by record, as before.
                if (admitted || completed || world.Closed) return;
                if (!paused) break;
                await AreaDelay(Player, 0.016);
            }
            if (Active) foreach (var (id, snapshot, log) in group) await SendWorldRecordsNow(id, snapshot, log, false, false, false);
        }

        async Task SendWorldRecordsNow(uint id, Func<byte[][]?> snapshot, string? log, bool create, bool remove, bool wake)
        {
            while (Active)
            {
                bool completed = false;
                byte[][]? Project()
                {
                    if (!Active || Player.Paused) return null;
                    completed = true;
                    return snapshot();
                }
                if (create)
                    await SendWorldCreate(Player, id, () => Project() is { } records ? records.Single() : null,
                        log, wake: wake, awaitBirth: false);
                else if (remove)
                    await SendWorldRecordsEndingInRemoval(Player, id, Project, log);
                else
                    await SendWorldRecords(Player, id, Project, log);
                if (completed || Player.Connection.WorldOutput.Closed) return;
                await AreaDelay(Player, 0.016);
            }
        }

        async Task RunOutput()
        {
            try
            {
                await foreach (var intent in output.Reader.ReadAllAsync())
                    if (Active)
                    {
                        ProjectileOutputGuard.Value = () => Active;
                        try { await intent(); }
                        finally { ProjectileOutputGuard.Value = null; }
                    }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            { Player.Closed = true; Invalidate(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[gateway] {Player.Connection.Remote} projectile output failed: {ex}");
                Player.Closed = true; Invalidate();
            }
        }

        ProjectileActorKey Key(uint id, SpawnedMonster monster)
        {
            if (!generations.TryGetValue(monster, out int value)) generations[monster] = value = ++generation;
            return new(id, value);
        }

        /// <summary>The actor key a monster has in this session's world: a monster's own shot names it as owner, so its
        /// flight never contacts the monster itself.</summary>
        public ProjectileActorKey ActorKey(uint id, SpawnedMonster monster) => Key(id, monster);

        // The runtime asks for the actors at every step of the simulation, thousands of times a second in a crowd, and
        // a new record and position function for each monster each time was a third of what a step cost. A monster keeps
        // its record for as long as what the record says stands: its id, its walk (the function reads that walk), its
        // side and its size. The list itself is new every time: the runtime holds one while a hit changes the next.
        readonly Dictionary<SpawnedMonster, (uint Id, object Walk, bool Hostile, int Size, ProjectileActor Actor)> actorOf =
            new(ReferenceEqualityComparer.Instance);
        readonly List<EntityId> nearActorIds = [];
        ProjectileActor? playerActor;

        ProjectileActor CachedMonsterActor(uint id, SpawnedMonster monster)
        {
            var walk = monster.Walk;
            int size = LivingCollision.Size(monster);
            if (!actorOf.TryGetValue(monster, out var kept) || kept.Id != id || !ReferenceEquals(kept.Walk, walk)
                || kept.Hostile != monster.Hostile || kept.Size != size)
                actorOf[monster] = kept = (id, walk, monster.Hostile, size, MonsterActor(Key(id, monster), walk, monster.Hostile, size));
            return kept.Actor;
        }

        void AddTotemsAndPlayer(List<ProjectileActor> result)
        {
            var totems = Player.Totems.All;
            double clock = AreaTime(Player);
            for (int i = 0; i < totems.Count; i++)
                if (totems[i].Alive(clock)) InOrder(result, TotemActor(totems[i]));
            if (Player.PlayerLife > 0)
                InOrder(result, playerActor ??= new(new(Player.Id, epoch), 1,
                    t => Vector(Player.Walk.Position(t / (double)ProjectileRuntime.QuantaPerSecond)), 0)
                    { Size = LivingCollision.PlayerSize });
        }

        public ProjectileActor? Actor(ProjectileActorKey key, long at)
        {
            if (key.Id == Player.Id && key.Generation == epoch)
                return Player.PlayerLife > 0
                    ? playerActor ??= new(new(Player.Id, epoch), 1,
                        t => Vector(Player.Walk.Position(t / (double)ProjectileRuntime.QuantaPerSecond)), 0)
                        { Size = LivingCollision.PlayerSize }
                    : null;
            var totems = Player.Totems.All;
            double clock = AreaTime(Player);
            for (int i = 0; i < totems.Count; i++)
                if (totems[i].Id == key.Id && key.Generation == epoch && totems[i].Alive(clock))
                    return TotemActor(totems[i]);
            if (Player.Area.Monsters.TryGetValue(key.Id, out var monster)
                && !monster.Dead && monster.Life > 0 && !monster.WorldHidden
                && !ArenaHazards.Untargetable(Player, monster)
                && generations.TryGetValue(monster, out int gen) && gen == key.Generation)
                return CachedMonsterActor(key.Id, monster);
            return null;
        }

        public IReadOnlyList<ProjectileActor> Actors(long from, long until)
        {
            var living = Player.Area.SyncLivingMonsters();
            var result = new List<ProjectileActor>(living.Count + Player.Totems.All.Count + 1);
            for (int i = 0; i < living.Count; i++)
            {
                var (id, monster) = living[i];
                if (monster.Dead || monster.Life == 0 || monster.WorldHidden || ArenaHazards.Untargetable(Player, monster)) continue;
                result.Add(CachedMonsterActor(id, monster));
            }
            // A monster that died or left keeps its record and generation until here; in a long fight (a horde, a
            // Simulacrum run) both tables would hold every monster the area ever had. Pruned now and then, not per step:
            // records only for the living, generations only for the dead that left the list (a corpse keeps its key).
            if (actorOf.Count > 2 * result.Count + 64) PruneActors();
            // The monsters come in id order; the totems and the player go where that order puts them, so the runtime
            // finds the list already in its contact order (id, then generation) and does not sort it again at every step.
            // A totem's id and place never change, so it keeps one record for as long as it is listed.
            AddTotemsAndPlayer(result);
            return result;
        }

        public IReadOnlyList<ProjectileActor> ActorsNear(ProjectileVector centre, double radius, long at)
        {
            if (!Player.Area.Driven || Player.Area.Monsters.Count < 48) return Actors(at, at);
            nearActorIds.Clear();
            Player.Area.Grid.Near(((int)Math.Round(centre.X), (int)Math.Round(centre.Y)),
                (int)Math.Ceiling(radius) + GameArea.WalkSlack + 4, nearActorIds);
            nearActorIds.Sort();
            var result = new List<ProjectileActor>(nearActorIds.Count + Player.Totems.All.Count + 1);
            var monsters = Player.Area.Monsters;
            for (int i = 0; i < nearActorIds.Count; i++)
            {
                uint id = nearActorIds[i].Value;
                if (!monsters.TryGetValue(id, out var monster) || monster.Dead || monster.Life == 0
                    || monster.WorldHidden || ArenaHazards.Untargetable(Player, monster)) continue;
                result.Add(CachedMonsterActor(id, monster));
            }
            AddTotemsAndPlayer(result);
            return result;
        }

        // Its own method: a lambda in the loop above made the loop keep its walk in a new closure at every monster of
        // every call (24 bytes each), also when the kept record was used.
        static ProjectileActor MonsterActor(ProjectileActorKey key, PlayerWalk walk, bool hostile, int size) =>
            new(key, hostile ? 2 : 1, t => Vector(walk.Position(t / (double)ProjectileRuntime.QuantaPerSecond)), 0) { Size = size };

        readonly System.Runtime.CompilerServices.ConditionalWeakTable<TotemState.Totem, ProjectileActor> totemActors = [];
        ProjectileActor TotemActor(TotemState.Totem totem)
        {
            if (totemActors.TryGetValue(totem, out var kept)) return kept;
            totemActors.Add(totem, kept = new(new(totem.Id, epoch), 1, _ => Vector(totem.At), 0));
            return kept;
        }

        static readonly Comparer<ProjectileActor> ContactOrder = Comparer<ProjectileActor>.Create((a, b) =>
            a.Key.Id != b.Key.Id ? a.Key.Id.CompareTo(b.Key.Id) : a.Key.Generation.CompareTo(b.Key.Generation));

        static void InOrder(List<ProjectileActor> actors, ProjectileActor actor)
        {
            int at = actors.Count == 0 || ContactOrder.Compare(actors[^1], actor) <= 0 ? actors.Count
                : actors.BinarySearch(actor, ContactOrder);
            actors.Insert(at < 0 ? ~at : at, actor);
        }

        void PruneActors()
        {
            var listed = new HashSet<SpawnedMonster>(Player.Area.Monsters.Values, ReferenceEqualityComparer.Instance);
            foreach (var monster in actorOf.Keys.Where(m => m.Dead || m.Life == 0 || !listed.Contains(m)).ToArray())
                actorOf.Remove(monster);
            // A living monster that left the list (asleep, held back) keeps its generation, so a flight that already hit
            // it does not see a new actor when it returns.
            foreach (var monster in generations.Keys.Where(m => (m.Dead || m.Life == 0) && !listed.Contains(m)).ToArray())
                generations.Remove(monster);
        }

        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) =>
            ProjectileGeometry.TraceGrid(from, to, Wall);

        // The area's cells and heights, looked up once for the session (a connection plays one area): by the area's name
        // it was a string and two dictionary lookups for every cell every flight crossed at every step. Kept only once
        // found, and the heights asked for again after a capture was forgotten (TerrainHeights.Forget).
        ProjectileGrid.Area? blocking;
        TerrainHeights.Area? heights;
        int heightsAsOf = -1;

        bool Wall(int x, int y) => x < 0 || y < 0 || Player.Layout.GridW > 0 && x >= Player.Layout.GridW
            || Player.Layout.GridH > 0 && y >= Player.Layout.GridH
            || (blocking ??= ProjectileGrid.Of(Player.Layout.Id, Player.Seed))?.Blocks(x, y) == true || Player.ProjectileWalls.Contains((x, y));

        int? Height(int x, int y)
        {
            int asOf = TerrainHeights.Forgotten;
            if (heights is null || heightsAsOf != asOf) (heights, heightsAsOf) = (TerrainHeights.AreaOf(Player.Layout.Id, Player.Seed), asOf);
            return heights?.At(x, y);
        }

        // Ground-crawling skills bind projectile vtable +0x548 to return 0 in 3.29.3c: they sample terrain height each
        // frame (0x7fffffff) and never block on explicit 3D terrain height.
        bool IsTerrainFollowingProjectile(ProjectileEntity p) =>
            p.Use.Payload is ProjectileOffense shot
            && SkillsFor(Player).EffectId(shot.Effect) is
                "Spark" or "SparkAltX" or "SparkAltY" or "VaalSpark" or "Twister"
                or "FreezingPulse" or "FreezingPulseAltX" or "FreezingPulseAltY" or "FreezingPulseRoyale"
                or "LightningStrike" or "LightningStrikeAltX" or "LightningStrikeAltY" or "VaalLightningStrike";

        static int LaunchStepsFor(ProjectileEntity p, ProjectilePresentation presentation) =>
            presentation.LaunchHeight
            ?? (int.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_HEIGHT"), out int up)
                ? up
                : p.Use.Payload is ProjectileOffense { Spell: true }
                    ? ProjectileAimHeight.SpellLaunchSteps
                    : ProjectileHeight.DefaultSteps);

        // 1:1 3.29.3c sloped 3D height ray for explicit-height player projectiles (outbound primary + return/redirect
        // legs). Lobbed arcs, ground-crawling skills, continuation children and monster shots keep the plain grid.
        ProjectileAimHeight.Ray? FlightRayFor(ProjectileEntity p)
        {
            if (flightRays.TryGetValue(p.Id, out var existing)) return existing;
            if (p.ParentId != 0 || p.Phase > 0 || p.Use.Team != 1 || p.Profile.Lobbed || IsTerrainFollowingProjectile(p))
                return null;
            if (ProjectileAimHeight.Primary(p.Use.Origin, p.Use.Aim, LaunchStepsFor(p, PresentationFor(p)), Height) is not { } primary)
                return null;
            var ray = primary.WithLeg(p.Position, p.Direction);
            flightRays[p.Id] = ray;
            return ray;
        }

        public TerrainContact? TraceFlight(ProjectileEntity p, ProjectileVector from, ProjectileVector to) =>
            FlightRayFor(p) is { } ray
                ? ProjectileGeometry.TraceGrid(from, to, (x, y) => Wall(x, y) || ray.Blocks(x, y, Height))
                : TraceTerrain(from, to);

        public ProjectileHitResult Hit(ProjectileDamage contact)
        {
            if (!Active) return ProjectileHitResult.Miss;
            // A monster's shot: the player is the target and the monster's damage pipeline commits the hit.
            if (contact.Projectile.Use.Payload is HostileProjectileOffense hostile) return HostileHit(this, contact, hostile);
            // A player minion's shot: a hostile monster is the target and the existing minion outcome pipeline owns it.
            if (contact.Projectile.Use.Payload is MinionProjectileOffense minion) return MinionProjectileHit(this, contact, minion);
            // A server object's shot (a trial's dart trap): its own rule commits the hit.
            if (contact.Projectile.Use.Payload is ObjectProjectileOffense thrown) return thrown.Hit(this, contact);
            if (contact.Projectile.Use.Payload is not ProjectileOffense shot || shot.Valid?.Invoke() == false)
                return ProjectileHitResult.Miss;
            if (!Player.Area.Monsters.TryGetValue(contact.Target.Id, out var target) || target.Dead || target.Life == 0 || target.WorldHidden || ArenaHazards.Untargetable(Player, target)
                || Key(contact.Target.Id, target) != contact.Target) return ProjectileHitResult.Miss;
            if (contact.Phase == 0 && shot.ExcludedTarget == contact.Target) return ProjectileHitResult.Miss;
            var random = contact.Projectile.RandomSource;
            if (shot.RampageEpoch is long ownedEpoch)
            {
                if (!RampageEffectCurrent(Player, ownedEpoch) || !target.Hostile || target.OwnerId is not null)
                    return ProjectileHitResult.Miss;
                if (!shot.Spell && !RampageAttackHits(Player, contact.Target.Id, target, shot.Accuracy, contact.At / (double)ProjectileRuntime.QuantaPerSecond, random))
                    return ProjectileHitResult.Miss;
                if (MonsterBlocks(target, random, shot.Spell)) return ProjectileHitResult.Blocked;
                var raw = shot.Hit.Scale(contact.Multiplier).Roll(random);
                bool crit = random.Next(10000) < shot.CriticalChance
                    + BrittleCriticalChance(target, contact.At / (double)ProjectileRuntime.QuantaPerSecond);
                var (ownedDamage, _) = MonsterDamage(target, raw, crit, !shot.Spell, default);
                if (crit) ownedDamage = DamageModel.CriticalDamage(ownedDamage, CriticalMultiplierOn(target,
                    shot.CriticalMultiplier, noExtra: shot.NoExtraCriticalDamage));
                var ownedHit = ApplyMonsterHitLocked(Player, target, ownedDamage, rampageSource: true);
                if (ownedHit is null) return ProjectileHitResult.Miss;
                Player.RecentEvents.RecordCriticalOutcome(
                    contact.At / (double)ProjectileRuntime.QuantaPerSecond, crit);
                if (ownedHit.Value.StageStarts) CommitProjectileBossStage(this, contact.Target.Id, target, contact.At);
                var stun = RampageStatusLocked(Player, contact.Target.Id, target, shot.RampageSkill,
                    contact.At / (double)ProjectileRuntime.QuantaPerSecond);
                Note($"Rampage projectile hit 0x{contact.Target.Id:x} for {ownedDamage}");
                if (ownedHit.Value.Life == 0)
                {
                    CommitProjectileKill(this, contact.Target.Id, target, contact.At,
                        contact.Projectile.RewardRandomSource, playerKill: false);
                    if (shot.RampageSkill?.Get(Stat.ExplodeOnKillPctFireDamageToDeal) is int pct and > 0)
                        Enqueue(() => RampageKillExplosion(Player, ownedEpoch,
                            contact.Target.Id, target, pct));
                }
                else if (stun is not null)
                    Enqueue(() => SendRampageStun(Player, stun));
                return ProjectileHitResult.Hit;
            }
            double at = contact.At / (double)ProjectileRuntime.QuantaPerSecond;
            int hitChance = 100;
            if (!shot.Spell)
            {
                var accuracy = shot.AccuracyProfile ?? new(shot.Accuracy, shot.AccuracyMoreUnique, shot.AccuracyMoreClose, shot.AlwaysHits);
                hitChance = AttackHitChanceAt(Player, contact.Target.Id, target, at, accuracy);
                if (!accuracy.CannotBeEvaded && !target.AttackEvasion.Hits(hitChance, at, Player.AttackAccuracyRandom))
                    return ProjectileHitResult.Miss;
            }
            if (MonsterBlocks(target, random, shot.Spell, at, shot.SkillStats)) return ProjectileHitResult.Blocked;
            // Select the profile only for an accepted contact, before this hit can apply Blind or other ailments.
            var conditional = shot.Spider is { } spider ? ContactDamage(Player, target, at, spider.Stats) : default;
            shot = shot.Against(Blinded(Player, contact.Target.Id, at), target.SpiderWebs.Count(at),
                target.Ignites.IgnitedAt(at), target.Bleeds.IgnitedAt(at), EnemyOnLowLife(target), target.MaimedUntil > at,
                conditional, areaContact: contact.IsArea);
            int projectileFreezeChance = 0;
            if (SkillsFor(Player).EffectId(shot.Effect) is "FreezingPulse" or "FreezingPulseAltX" or "FreezingPulseAltY" or "FreezingPulseRoyale")
            {
                // The gem loses up to half its damage over the flight. Its inherent freeze chance fades
                // from 25% to zero during the first quarter, independently of other freeze sources.
                var flight = contact.Projectile;
                double range = Math.Min(flight.Profile.Range,
                    flight.Profile.Speed * flight.Profile.Lifetime / ProjectileRuntime.QuantaPerSecond);
                double progress = Math.Clamp(flight.Distance / range, 0, 1);
                shot = shot.ScaleDamage(1 - progress * .5);
                projectileFreezeChance = (int)Math.Round(25 * Math.Max(0, 1 - 4 * progress));
            }
            int chainDamage = GolemStat(shot.SkillStats, Stat.CobraLashHitAndAilmentDamagePlusPctFinalForEachRemainingChain);
            double chainsMore = Math.Max(0, 1 + (double)chainDamage * contact.Projectile.ChainsRemaining / 100);
            double returnMore = contact.Phase > 0 ? shot.ReturnMultiplier : 1;
            double ailmentMore = chainsMore * returnMore * OverexertionMore(shot.SkillStats, shot.IntimidatingCryExerted);
            if (ailmentMore != 1)
                shot = shot with
                {
                    Ignite = RepeatAilment(shot.Ignite, ailmentMore),
                    Bleed = RepeatAilment(shot.Bleed, ailmentMore),
                    Poison = RepeatAilment(shot.Poison, ailmentMore),
                };
            if (shot.DamageMorePerGroundBounce != 0 && contact.Projectile.GroundBounces > 0)
                shot = shot.ScaleDamage(Math.Max(0, 1 + shot.DamageMorePerGroundBounce * (double)contact.Projectile.GroundBounces / 100));
            double more = contact.Multiplier * (contact.Phase > 0 ? shot.ReturnMultiplier : 1);
            if (ReferenceEquals(shot.BrandedTarget, target))
                more *= Math.Max(0, 1 + GolemStat(shot.SkillStats, Stat.TriggerBrandSupportHitDamagePlusPctFinalVsBrandedEnemy) / 100.0);
            if (!shot.Spell && shot.FarShot) more *= AntiquarianEffects.FarShot(contact.Projectile.Distance);
            if (!shot.Spell && shot.PointBlank) more *= AntiquarianEffects.PointBlank(contact.Projectile.Distance);
            var baseHit = contact.IsArea ? shot.AreaHit ?? shot.Hit : shot.Hit;
            // One stat build per contact: every input below reads the same owner state (about 120 µs per build).
            var player = shot.ActualCaster ?? CharacterPlayerStats(Player);
            var packet = new SkillDamageContext(shot.Spell ? SkillHitKind.Spell : SkillHitKind.Attack, player).Hit(baseHit).Scale(more * chainsMore);
            var gambler = GamblerFor(player);
            var rolled = WithConditionalSupportDamageLocked(Player, shot.SkillStats, !shot.Spell, target,
                GameEvents.WithOutgoingDamage(Player, player, target, WithIntimidatingCryExert((gambler with { DamageMorePct = 0 })
                    .ForSkill(name => name is var k ? shot.SkillStats.GetValueOrDefault(k) : 0, shot.Spell,
                        finalRepeat: contact.Projectile.Use.RepeatsLeft == 0), shot.IntimidatingCryExerted)
                    .ForWeapon(shot.WeaponLocal).RollOutgoingHit(packet, random)), at);
            rolled = WithOverexertion(rolled, shot.SkillStats, shot.IntimidatingCryExerted);
            int chance = contact.Projectile.Form > 0 && shot.SecondFormCriticalChance is int second ? second
                : CriticalChanceOn(Player, contact.Target.Id, shot.CriticalChance, shot.VsBlindedCriticalChance,
                    shot.VsGroundCriticalChance);
            // Base chance/multiplier belong to release; conditional owner state is read at this contact.
            bool critical = RollCriticalStrike(Player, chance, shot.SkillStats, attack: !shot.Spell, skillRoll: shot.CriticalRoll, playerStats: player)
                && (shot.Spell || EvasionEntropy.ConfirmsCritical(hitChance, Player.AttackAccuracyRandom));
            var weaponLocal = shot.WeaponAttack ? shot.WeaponLocal : null;
            int sourceMultiplier = CriticalMultiplierWithWeapon(shot.CriticalMultiplier, target, weaponLocal);
            int multiplier = CriticalMultiplierOn(target, sourceMultiplier, shot.TargetCritical, at, shot.NoExtraCriticalDamage);
            bool canBenefitResonance = !shot.FromTrapOrMine && (shot.ActualCaster is null || shot.Mirage);
            bool canGainResonance = canBenefitResonance && !shot.Mirage;
            var resonance = canBenefitResonance
                ? WithResonanceHitLocked(Player, shot.SkillStats, rolled, shot.ResistanceMods, shot.Penetration, at)
                : (Packet: rolled, Mods: shot.ResistanceMods, Penetration: shot.Penetration);
            rolled = resonance.Packet;
            bool projectileAttack = !shot.Spell
                && (shot.ProjectileAttack ?? (shot.Effect < 0 || SkillsFor(Player).IsProjectile(shot.Effect)));
            int additionalTaken = projectileAttack ? EnsnaredProjectileAttackTaken(target, at) : 0;
            var (damage, _, mitigated) = MonsterHitDamage(target, rolled, critical, !shot.Spell, at, resonance.Mods,
                resonance.Penetration, gambler.InvertsResistance(random), random, trapOrMine: shot.FromTrapOrMine,
                additionalTakenPct: additionalTaken);
            if (critical) damage = DamageModel.CriticalDamage(damage, multiplier);
            int chaosDamage = MonsterHitChaosDamage(target, mitigated, shot.Spell, at, damage,
                additionalTakenPct: additionalTaken + (shot.FromTrapOrMine ? BearTrapDamageTaken(target, at) : 0));
            bool immune = target.Invulnerable;
            if (shot.FromMine)
                LeechPlacedMineHitLocked(Player, target, mitigated, shot.Spell, damage, at, player, contact.Target.Id);
            else if (!shot.FromTrapOrMine)
                LeechOnHitTargetLocked(Player, target, MonsterTakenComponents(target, mitigated, shot.Spell, at), damage,
                    shot.Leech, contact.Target.Id);
            if (!immune)
            {
                var settings = (critical ? shot.CriticalImpaleSettings : null) ?? shot.ImpaleSettings ?? new(shot.ImpaleChance);
                long impaleMultiplier = shot.NoExtraCriticalDamage ? sourceMultiplier
                    : sourceMultiplier + (target.MaxLife > 0 && target.Life >= target.MaxLife ? shot.TargetCritical.FullLife : 0);
                double physical = rolled[DamageType.Physical].Min * (critical ? Math.Max(0, impaleMultiplier) / 100d : 1);
                var (stored, _) = target.Impale.OnHit(physical, at, settings, random, target.Effects[Stat.AvoidImpalePct]);
                damage = ImpaleStats.AddReflected(damage, MonsterImpaleDamage(target, stored, at));
            }
            uint lifeBefore = target.Life;
            var hit = ApplyMonsterHitLocked(Player, target, damage, rampageSource: null, playerStats: player,
                allyAt: shot.ActualCaster is null && !shot.FromTrapOrMine && !shot.Mirage ? null : ((int)contact.Projectile.Use.Origin.X, (int)contact.Projectile.Use.Origin.Y),
                cull: Cull.Of(player, shot.SkillStats, critical, shot.Spell).WithWeapon(weaponLocal),
                cullPlayer: null, chaosDamage: chaosDamage);
            if (hit is null) return ProjectileHitResult.Miss;
            if (canGainResonance && !immune && hit.Value.Life < lifeBefore)
                RecordResonanceHitLocked(Player, target, shot.SkillStats, rolled, at);
            Player.RecentEvents.RecordCriticalOutcome(at, critical);
            if (ElementalOverloadCritLocked(Player, shot.SourceSkill, null, player, critical) is { } eoBuff)
                Packet(eoBuff, $"elemental overload on skill 0x{shot.SourceSkill:x4}");
            // Cruelty: a player projectile's hit grants its skill's buff (a totem's or a monster's never does).
            if (shot.ActualCaster is null && target.Hostile)
                CrueltyOnHitLocked(Player, shot.SourceSkill, damage, lifeBefore, target.MaxLife, shot.SkillStats, at);
            if (!shot.Spell) ReleaseCriticalTrigger(Player, this, shot.CriticalTrigger, critical, target.Hostile,
                target.Walk.Position(at), at);
            else ArcaneSurgeOnSpellHitLocked(Player, player, target);
            // Item Quantity Support: the kill's loot rolls read the supported skill's share.
            if (Stat.BaseKilledMonsterDroppedItemQuantityPlusPct is var quantityKey)
                target.KilledWithItemQuantityPct = shot.SkillStats.GetValueOrDefault(quantityKey);
            // Item Rarity Support: the same kill's drops read the skill's rarity share.
            if (Stat.BaseKilledMonsterDroppedItemRarityPlusPct is var rarityKey)
                target.KilledWithItemRarityPct = shot.SkillStats.GetValueOrDefault(rarityKey);
            // Soul Ascension: this player hit on a rare or unique enemy eats a soul (its own cooldown).
            SoulEaterHit(Player, target);
            if (shot.WeaponAttack && shot.ActualCaster is null && !shot.FromTrapOrMine && !shot.Mirage && target.Hostile)
            {
                RecordWeaponContactLocked(Player, at, shot.SkeletonWeaponContactSource);
                if (critical) ItemMinionsOnWeaponCriticalLocked(Player, shot.SkeletonWeaponContactSource, target, at);
                if (hit.Value.Life == 0) RecordTinctureKill(target, shot.TinctureKill);
            }
            if (shot.ActualCaster is null && !shot.Spell && Player.TinctureEffects.AnyEffective)
                TinctureOnWeaponHitLocked(Player, player, target, lifeBefore, at, contact.Projectile.ReactionRandomSource);
            if (shot.ActualCaster is null && hit.Value.Life == 0) DevourOnKill(Player, target, shot.SkillStats);
            if (shot.MirageSource is not null && target.Hostile && contact.Projectile.Use.Emitter.Id == Player.Id)
                MirageOnArrowHit(shot);
            if (!immune && target.Hostile && contact.Projectile.Use.Emitter.Id == Player.Id
                && shot.ActualCaster is null && !shot.Spell && !shot.FromTrapOrMine && !shot.FromTotem
                && shot.SkillStats.GetValueOrDefault(Stat.SummonSacredWispsOnHit) > 0)
                SacredWispsOnHit(shot);
            if (!immune && shot.SourceSkill == 0 && shot.WeaponAttack && shot.ActualCaster is null && !shot.FromTrapOrMine)
                CaptureEquippedHitExplosionLocked(target, player);
            if (!immune)
            {
                var emitterTotem = shot.FromTotem
                    ? Player.Totems.All.FirstOrDefault(t => t.Id == contact.Projectile.Use.Emitter.Id)
                    : null;
                AilmentsOnHitLocked(Player, contact.Target.Id, target, mitigated, shot.Spell, critical, multiplier, player,
                    shot.SkillStats, at, contact.Projectile.ReactionRandomSource, poison: shot.Poison,
                    sourceSkill: shot.SourceSkill, blind: shot.Blind, playerHit: !shot.FromTotem,
                    attackGains: AttackAilmentGains.Capture(player, shot.ActualCaster is null && !shot.Spell),
                    hitOrigin: emitterTotem?.At,
                    attackerSize: emitterTotem?.BodySize ?? LivingCollision.PlayerSize,
                    attackerHasReversedStat: emitterTotem is null, projectileDamage: true, projectileFreezeChance: projectileFreezeChance);
            }
            if (!immune && hit.Value.Life > 0 && shot.EssenceDrain is { } drain)
                EssenceDrainOnHitLocked(contact.Target.Id, target, drain, at);
            if (!immune && hit.Value.Life > 0 && !shot.Spell && shot.MaimChance > 0)
                TryMaimOnHitLocked(target, shot.MaimChance, 0, at, contact.Projectile.ReactionRandomSource);
            if (!immune && hit.Value.Life > 0 && shot.Ignite.CanIgnite(rolled))
                IgniteOnHitLocked(this, contact.Target.Id, target, shot.Ignite, critical, at,
                    playerIgnite: shot.ActualCaster is null,
                    attackGains: AttackAilmentGains.Capture(player, shot.ActualCaster is null && !shot.Spell));
            if (!immune && hit.Value.Life > 0 && rolled[DamageType.Physical].Max > 0)
                BleedOnHitLocked(this, contact.Target.Id, target, shot.Bleed, critical, at,
                    BleedReflected(shot.ActualCaster is null ? player : CharacterPlayerStats(Player)));
            if (shot.ActualCaster is null) RageOnHitLocked(Player, attack: !shot.Spell, melee: false, critical, shot.SkillStats,
                contact.Projectile.ReactionRandomSource, player);
            if (shot.ActualCaster is null) ChargesOnHit(Player, target, critical, shot.Spell, shot.Effect, shot.Level, shot.Quality, melee: false,
                primary: shot.FirstHit?.Claim() ?? true, skillStats: shot.SkillStats);
            CommitProjectileHit(this, contact, target, hit.Value, damage, shot, critical,
                OwnerStun(Player, shot.Effect, target, damage, mitigated, melee: false, shot.Spell, critical, player, shot.SkillStats, lifeBefore,
                    localStunReduction: shot.LocalStunThresholdReduction, weaponClass: shot.WeaponClass));
            if (critical) Enqueue(() => MaybeCreateProfaneGround(Player, target.Walk.Position(at)));
            return ProjectileHitResult.Hit;
        }

        public void Publish(ProjectileEvent value)
        {
            var p = value.Projectile;
            bool valid = p.Use.Payload is not ProjectileOffense owned || owned.Valid?.Invoke() != false;
            if (!valid && value.Kind != ProjectileEventKind.End) return;
            int x = (int)Math.Round(value.Position.X), y = (int)Math.Round(value.Position.Y);
            var presentation = PresentationFor(p);
            // A gem skill's server-drawn projectile plays its rig's impact clip where it stops (Projectiles.dat
            // ImpactAnimationIds: Fireball's fireball_explode), as a monster's does. Without one the object was only
            // removed, so a triggered, totem, trap or mine Fireball showed no explosion, while the client plays it for
            // its own cast (live 2026-11-17).
            if (value.Kind == ProjectileEventKind.End && presentation.ImpactClip is null && !presentation.StayOnImpact
                && p.Use.Payload is ProjectileOffense
                && SkillsFor(Player).Clips(p.Profile.Id)?.Impact is { } rigImpact)
                presentation = presentation with
                {
                    ImpactClip = rigImpact.Index,
                    ImpactLifetime = ProjectileRuntime.Time(Math.Max(0.1, rigImpact.Ms / 1000.0)),
                };
            if (valid && value.Kind == ProjectileEventKind.End)
            {
                CausticGroundAtEnd(value);
                LeaveKnifeBlade(value);
                ExplosiveArrowAtEnd(value);
                EnsnaringArrowAtEnd(value);
            }
            if (value.Kind == ProjectileEventKind.End && p.ParentId == 0 && ServerTrace.Projectiles && !Quiet(p.Use.Payload))
                Console.WriteLine($"[gateway] {Player.Connection.Remote} projectile end use={p.UseId} entity={p.Id}"
                    + $" reason={value.Reason} position={value.Position} metadata={p.Profile.Id}"
                    + $" grid={Player.Layout.Id}_seed{Player.Seed} blocked={ProjectileGrid.Blocks(Player.Layout.Id, Player.Seed, x, y)?.ToString() ?? "unknown"}");
            if (value.Kind == ProjectileEventKind.End && p.Use.Payload is HostileProjectileOffense { OnEnd: { } onEnd }
                && value.Reason is ProjectileEndReason.Enemy or ProjectileEndReason.Terrain or ProjectileEndReason.Distance
                    or ProjectileEndReason.Lifetime or ProjectileEndReason.Landing)
                Enqueue(() => onEnd((x, y)));
            // Caustic Arrow's caustic ground is placed once on End by CausticGroundAtEnd (CausticGround.cs) using the
            // real multi-piece GroundArt and rig clips; only non-CausticArrow callers use CausticClouds directly.
            if (valid && value.Kind == ProjectileEventKind.Area && p.Use.Payload is ProjectileOffense { CausticCloud: { } cloud }
                && SkillsFor(Player).EffectId(p.Use.Payload is ProjectileOffense o ? o.Effect : -1)?.StartsWith("CausticArrow", StringComparison.Ordinal) != true)
                PlaceCausticCloud(value.Position, cloud);
            if (valid && presentation.EventEffect?.Invoke(value) is { } eventEffect)
            {
                bool spawn = value.Kind == ProjectileEventKind.Spawn;
                // The official rain rig stands at the caster's cell; the payload arrow leaves from the bow, so spawning
                // the rig at the release point shifted the whole rig (and its attached arrow) ahead of the player.
                int ex = x, ey = y;
                if (spawn && presentation.SpawnAtCaster)
                {
                    var caster = p.Use.Emitter.Id == Player.Id
                        ? Vector(Player.Walk.Position(Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond)) : p.Use.Origin;
                    ex = (int)Math.Round(caster.X);
                    ey = (int)Math.Round(caster.Y);
                }
                Effect(ex, ey, eventEffect, presentation.EffectLifetime,
                    value.Kind == ProjectileEventKind.Area ? value.Radius : null, presentation.AttachEffect,
                    presentation.AttachParent, presentation.EffectAnimatedFlags,
                    spawn ? presentation.SpawnFlags0 : (byte)0, spawn ? presentation.SpawnAnimationWord : 0,
                    spawn ? presentation.SpawnAnimationValue : 0, spawn ? presentation.SpawnAnimatedId : 0);
            }
            // Maintain the 3.29.3c 3D flight height ray for both client-drawn and server-drawn projectiles before any
            // visual-hiding return so simulation terrain-height checks and return slopes match the client 1:1.
            ProjectileAimHeight.Ray? endRay = null;
            if (value.Kind == ProjectileEventKind.Spawn)
                _ = FlightRayFor(p);
            else if (value.Kind == ProjectileEventKind.Redirect && flightRays.TryGetValue(p.Id, out var prevRay))
            {
                double currentSteps = prevRay.StepsAt(p.Position);
                if (p.Phase > 0 && p.Use.ReturnAnchor is { } anchorKey && Actor(anchorKey, value.At) is { } anchor)
                    flightRays[p.Id] = ProjectileAimHeight.Return(p.Position, anchor.Position(value.At), currentSteps, Height)
                        ?? prevRay.WithLeg(p.Position, p.Direction);
                else
                    flightRays[p.Id] = new ProjectileAimHeight.Ray(p.Position, p.Direction.Unit, currentSteps, 0);
            }
            else if (value.Kind == ProjectileEventKind.End && flightRays.Remove(p.Id, out var removedRay))
                endRay = removedRay;
            // The client draws its own primary projectiles of a skill use; the server still simulates them (contacts,
            // areas and continuation children, which do get objects). Client-predicted zig-zag secondaries stay hidden too.
            if (value.Kind == ProjectileEventKind.Spawn && p.Profile.HideVisual) return;
            if (value.Kind == ProjectileEventKind.Spawn && p.Use.Payload is ProjectileOffense primary
                && ((p.ParentId == 0 && ClientDrawsPrimaryProjectile(primary.ClientPredicted, SkillsFor(Player).EffectId(primary.Effect)))
                    || (primary.ClientPredicted && p.Profile.HidePredictedVisual))) return;
            if (value.Kind == ProjectileEventKind.Spawn)
            {
                string? flightVisual = (p.Use.Payload as ProjectileOffense)?.FlightVisual ?? presentation.FlightVisual;
                uint id = Player.TakeObjectId();
                var height = flightRays.TryGetValue(p.Id, out var spawnRay)
                    ? spawnRay.MarkerAt(value.Position)
                    : IsTerrainFollowingProjectile(p)
                        ? ProjectileHeight.Terrain
                        : ProjectileHeight.Flight(TerrainHeights.At(Player.Layout.Id, Player.Seed, x, y),
                            Environment.GetEnvironmentVariable("Game_PROJECTILE_HEIGHT"), presentation.LaunchHeight);
                var visual = new Visual(id, height) { SpawnedAt = value.Position };
                visuals[p.Id] = visual; Player.ProjectileObjects.Add(id);
                if (p.Profile.Lobbed) BeginArc(p, visual);
                CreateFlight(id, () =>
                {
                    // A flight that ended before this snapshot is created only when its stop keeps the object (the impact
                    // clip or stuck arrow then shows where it hit); otherwise a create followed by its removal is a flash.
                    if (visual.Ended && !visual.StaysAfterEnd) return null;
                    // Created where it was released, not where the simulation has it when this snapshot is taken: an arrow
                    // covers a cell every 3 ms, so the late snapshot put it one to ten cells past the bow (live 2026-10-29).
                    // The first glide starts here too; the visual trails the simulation by the output delay and catches
                    // up at its stop.
                    var at = visual.SpawnedAt;
                    var obj = new ProjectileObject(p.Profile.Id, (int)Math.Round(at.X), (int)Math.Round(at.Y),
                        (float)Math.Atan2(p.Direction.X, -p.Direction.Y),
                        Team: p.Use.Team == 1 ? (ushort)0 : (ushort)1,
                        Height: (visual.Arc is { } arc ? arc.Height(arc.Fraction(p.Distance)) : visual.Height).ObjectHeight)
                        { AnimatedObject = flightVisual };
                    visual.Created = true;
                    if (p.ParentId == 0 && ServerTrace.Projectiles && !Quiet(p.Use.Payload))
                        Console.WriteLine($"[gateway] {Player.Connection.Remote} projectile visual use={p.UseId} entity={p.Id} object={id}"
                            + $" metadata={p.Profile.Id} created={at} simulated={p.Position} direction={p.Direction}"
                            + $" lagMs={(Runtime.Now - p.CreatedAt) / 120d:0.###} height={(obj.Height is int h ? h.ToString() : "terrain")}");
                    return CreateProjectileObject(id, obj);
                }, () => Projected(visual));
                // No 0x1a9: the projectile blob's first-byte flag 0x02 puts it in the client's wake zone already, and a
                // second entry ("ObjectInWakeZone ... already in the wake zone", every projectile since 2026-11-01, thousands of
                // Poison Arrow Trap bolts in The Upper Sceptre of God) broke its object list until it crashed at 0x1401297ca
                // on an object that was gone (live 2026-11-04 and 2026-11-05, with traps firing).
                if (presentation.FlightClip is { } clip)
                    World(id, () => visual.Created && !visual.Ended ? InstanceProtocol.AnimationUpdate(id, 0, clip, presentation.FlightClipSpeed) : null);
                UpdateForm(p, visual);
                Glide(p, visual);
            }
            else if (value.Kind == ProjectileEventKind.Redirect && visuals.TryGetValue(p.Id, out var redirect))
            {
                if (p.Profile.Lobbed) BeginArc(p, redirect);
                UpdateForm(p, redirect);
                Glide(p, redirect);
            }
            else if (value.Kind == ProjectileEventKind.End && visuals.TryGetValue(p.Id, out var end) && !end.Ended)
            {
                end.Ended = true;
                // A lobbed shot's landing is its impact too (the mortar's "end" clip plays where it comes down).
                end.StaysAfterEnd = (presentation.ImpactClip is not null || presentation.StayOnImpact)
                    && (value.Reason is ProjectileEndReason.Enemy or ProjectileEndReason.Terrain or ProjectileEndReason.Landing
                        || value.Reason == ProjectileEndReason.Lifetime && p.Profile.EndArea is not null
                            && p.Profile.EndAreaReasons?.Contains(ProjectileEndReason.Lifetime) == true)
                    || presentation.StayAtEndOfRange && value.Reason is ProjectileEndReason.Distance or ProjectileEndReason.Terrain;
                var height = end.Arc is { } arc ? arc.Height(arc.Fraction(p.Distance))
                    : endRay is { } ray ? ray.MarkerAt(value.Position)
                    : end.Height;
                if (end.StaysAfterEnd)
                {
                    // The stop ends the glide and the object stays for ImpactLifetime of game time: for the rig's own impact
                    // clip (an ordinary wand bolt, with its particles and sound), or as the arrow left in its target or a
                    // wall. Its removal follows on the same pause- and transfer-aware game clock as an effect's lifetime.
                    WorldRecords(end.Id, () => !end.Created ? null
                        : presentation.ImpactClip is { } impactClip
                            ? [height.StopAt(end.Id, x, y), InstanceProtocol.AnimationUpdate(end.Id, 0, impactClip, 1)]
                            : [height.StopAt(end.Id, x, y)]);
                    Runtime.Schedule(Runtime.Now + presentation.ImpactLifetime, () =>
                    {
                        visuals.Remove(p.Id);
                        WorldRecords(end.Id, () =>
                        {
                            Player.ProjectileObjects.Remove(end.Id);
                            return end.Created ? [InstanceProtocol.RemoveObject(end.Id)] : null;
                        }, remove: true);
                    });
                }
                else
                {
                    visuals.Remove(p.Id);
                    WorldRecords(end.Id, () =>
                    {
                        Player.ProjectileObjects.Remove(end.Id);
                        return end.Created ? [height.StopAt(end.Id, x, y), InstanceProtocol.RemoveObject(end.Id)] : null;
                    }, remove: true);
                }
            }
            else if (value.Kind == ProjectileEventKind.Area)
            {
                // Every area event of every projectile skill comes here, under the instance's lock: trace only.
                if (ServerTrace.Projectiles && !Quiet(p.Use.Payload))
                    Console.WriteLine($"[gateway] {Player.Connection.Remote} projectile area {value.Stage} t={value.At / (double)ProjectileRuntime.QuantaPerSecond:0.000}"
                        + $" offset={(value.Position - p.Position).Length:0.0} radius={value.Radius:0.#} use={p.UseId}");
                if (p.Use.Payload is ProjectileOffense { AreaVisual: { } ao })
                    Effect(x, y, ao, presentation.EffectLifetime, value.Radius);
            }
        }

        void Effect(int x, int y, string ao, long lifetime, double? radius = null, (string Ao, string Bone)? attach = null,
            uint attachParent = 0, byte animatedFlags = 0, byte positionedFlags0 = 0, uint animationWord = 0,
            float animationValue = 0, uint animatedId = 0, int? artRadius = null, (uint Clip, long Length)? endClip = null,
            float rotation = 0, uint? startClip = null, (uint Clip, long After)? thenClip = null)
        {
            // startClip: the clip the art is created playing (Animated bit 3), for art without a default animation or
            // with several; thenClip: the clip that follows it (a ground's loop after its fade-in).
            if (startClip is uint first) (animatedFlags, animatedId) = ((byte)(animatedFlags | 9), first);
            uint id = Player.TakeObjectId();
            // Native area art radii are world units; simulation radii are grid cells. artRadius: the art's radius when
            // MiscAnimated has no row for it (a GroundEffects piece).
            float scale = radius is double r && (Art.ArtRadius(ao) ?? artRadius) is int nativeRadius
                ? (float)(r * (250d / 23) / nativeRadius) : 1;
            var effect = new EffectObject(x, y, ao)
            {
                Scale = scale, AnimatedFlags = animatedFlags, Flags0 = positionedFlags0, Rotation = rotation,
                AnimationWord = animationWord, AnimationValue = animationValue, AnimationId = animatedId
            };
            if (radius is not null && ServerTrace.Projectiles)
                Console.WriteLine($"[gateway] {Player.Connection.Remote} projectile area object={id} position=({x}, {y})"
                    + $" radius={radius:0.###} art={ao} scale={scale:0.###}");
            var visual = new Visual(id, default);
            Player.ProjectileObjects.Add(id);
            Create(id, () =>
            {
                if (visual.Ended) return null;
                visual.Created = true;
                return InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob());
            }, wake: true);
            if (thenClip is { } next && next.After < lifetime)
                Runtime.Schedule(Runtime.Now + next.After, () => WorldRecords(id, () => visual.Created && !visual.Ended
                    ? [InstanceProtocol.AnimationUpdate(id, 0, next.Clip, 1)] : null));
            // endClip: a clip the art plays when its time is up (a ground's fade-out); the object goes after it.
            if (endClip is { } ending)
                Runtime.Schedule(Runtime.Now + lifetime, () => WorldRecords(id, () => visual.Created && !visual.Ended
                    ? [InstanceProtocol.AnimationUpdate(id, 0, ending.Clip, 1)] : null));
            Runtime.Schedule(Runtime.Now + lifetime + (endClip?.Length ?? 0), () =>
            {
                visual.Ended = true;
                WorldRecords(id, () =>
                {
                    Player.ProjectileObjects.Remove(id);
                    return visual.Created ? [InstanceProtocol.RemoveObject(id)] : null;
                }, remove: true);
            });
            if (attach is not { } child) return;
            // The art attached to the effect's bone: the client carries it along with the parent's animation.
            uint childId = Player.TakeObjectId();
            // The official split-arrow art sends kind 0 and mask 3 (the base record of the Animated component), which
            // keeps the parent bone's rotation: the arrow then follows aux_projectile_attach instead of lying flat.
            var childEffect = new EffectObject(x, y, child.Ao)
                { Parent = attachParent != 0 ? attachParent : id, Bone = child.Bone, AttachKind = 0, AttachFlags = 3 };
            var childVisual = new Visual(childId, default);
            Player.ProjectileObjects.Add(childId);
            Create(childId, () =>
            {
                if (childVisual.Ended) return null;
                childVisual.Created = true;
                return InstanceEntities.CreateObject(childId, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), childEffect.Blob());
            });
            World(childId, () => childVisual.Created ? InstanceProtocol.EnterWakeZone(childId) : null);
            Runtime.Schedule(Runtime.Now + lifetime, () =>
            {
                childVisual.Ended = true;
                WorldRecords(childId, () =>
                {
                    Player.ProjectileObjects.Remove(childId);
                    return childVisual.Created ? [InstanceProtocol.RemoveObject(childId)] : null;
                }, remove: true);
            });
        }

        void UpdateForm(ProjectileEntity p, Visual visual)
        {
            if (PresentationFor(p).SecondFormClip is not { } clip) return;
            World(visual.Id, () =>
            {
                if (visual.Ended || !visual.Created || p.Form == 0 || visual.SecondFormStarted) return null;
                // Continuation children may already be in their second form at birth.
                visual.SecondFormStarted = true;
                return InstanceProtocol.AnimationUpdate(visual.Id, 0, clip, 1);
            });
        }

        void BeginArc(ProjectileEntity p, Visual visual)
        {
            var end = p.Position + p.Direction * p.Remaining;
            int? Ground(ProjectileVector at) => TerrainHeights.At(Player.Layout.Id, Player.Seed,
                (int)Math.Round(at.X), (int)Math.Round(at.Y));
            if (Ground(p.Position) is not int startGround || Ground(end) is not int endGround)
            {
                // A guessed absolute parabola can run entirely underground. Let the client sample the ground
                // until both endpoints are known; XY landing/damage and bounce chronology remain authoritative.
                visual.Arc = null;
                visual.Height = ProjectileHeight.Terrain;
                return;
            }
            int start = p.GroundBounces == 0
                ? ProjectileHeight.Flight(startGround, Environment.GetEnvironmentVariable("Game_PROJECTILE_HEIGHT"),
                    PresentationFor(p).LaunchHeight).Marker
                : startGround;
            var arc = new ProjectileArc(p.Distance, p.Remaining, start, endGround, p.Profile.ArcHeight);
            visual.Arc = arc;
            for (int segment = 1; segment < ProjectileArc.Segments; segment++)
            {
                double at = Runtime.Now + Math.Ceiling(arc.Distance * segment / ProjectileArc.Segments / p.Speed * ProjectileRuntime.QuantaPerSecond);
                if (at <= Runtime.Now || at >= p.ExpiresAt) continue;
                Runtime.Schedule((long)at, () =>
                {
                    if (!visual.Ended && visual.Arc == arc) Glide(p, visual);
                });
            }
        }

        void Glide(ProjectileEntity p, Visual visual)
        {
            // The move is built now, from the state the event describes, and replaces any move still queued: an
            // ordinary shot is redirected and ends within a couple of the 33 ms steps the output writes at, so a
            // record built when the queue reached it described a flight that was already over, and a record dropped
            // for being superseded was never replaced (live 2026-11-17: every Kinetic Blast projectile stood at its
            // release point until its removal, and a fast one was never created at all).
            visual.PendingGlide = GlideRecords(p, visual);
            // Before the create is projected the create carries this move (CreateFlight); after it, one intent at a
            // time carries the latest move.
            if (!visual.Created || visual.GlideQueued) return;
            visual.GlideQueued = true;
            WorldRecords(visual.Id, () =>
            {
                visual.GlideQueued = false;
                if (visual.Ended || !visual.Created || visual.PendingGlide is not { } glide)
                {
                    Player.Connection.Performance.Count("projectile move dropped");
                    if (ServerTrace.Projectiles && !Quiet(p.Use.Payload))
                        Console.WriteLine($"[gateway] {Player.Connection.Remote} projectile glide dropped object={visual.Id} entity={p.Id}"
                            + $" ended={visual.Ended} created={visual.Created} position={p.Position} remaining={p.Remaining}");
                    return null;
                }
                return Projected(visual);
            });
        }

        // The pending move as it is projected: the next one built stops at the simulated position first.
        static byte[][]? Projected(Visual visual)
        {
            if (visual.PendingGlide is not null) visual.Glided = true;
            return visual.PendingGlide;
        }

        byte[][]? GlideRecords(ProjectileEntity p, Visual visual)
        {
            var end = p.Position + p.Direction * p.Remaining;
            var startHeight = visual.Height;
            var endHeight = visual.Height;
            if (visual.Arc is { } arc)
            {
                double fraction = arc.Fraction(p.Distance);
                double next = Math.Min(1, (Math.Floor(fraction * ProjectileArc.Segments + .000001) + 1) / ProjectileArc.Segments);
                end = p.Position + p.Direction * Math.Min(p.Remaining, (next - fraction) * arc.Distance);
                startHeight = arc.Height(fraction);
            }
            if (!(p.Phase > 0 && p.Profile.IgnoreTerrainOnReturn) && TraceFlight(p, p.Position, end) is { } wall)
                end = p.Position + (end - p.Position) * wall.Fraction;
            if (visual.Arc is { } path) endHeight = path.Height(path.Fraction(p.Distance + (end - p.Position).Length));
            // The first move starts where the object was created (the release point), without a stop there and with
            // the whole way's duration, so it flies at the projectile's speed. Later moves (arc segments, redirects)
            // first stop at the simulated position. A move replaced before it was projected was never the first.
            bool first = !visual.Glided;
            var start = first ? visual.SpawnedAt : p.Position;
            if (visual.Arc is null && flightRays.TryGetValue(p.Id, out var glideRay))
            {
                startHeight = glideRay.MarkerAt(start);
                endHeight = glideRay.MarkerAt(end);
            }
            float duration = (float)Math.Max(.001, (end - start).Length / p.Speed);
            byte[] move = endHeight.MoveTo(visual.Id, (int)Math.Round(end.X), (int)Math.Round(end.Y), duration);
            if (ServerTrace.Projectiles && !Quiet(p.Use.Payload))
                Console.WriteLine($"[gateway] {Player.Connection.Remote} projectile glide object={visual.Id} entity={p.Id}"
                    + $" from={start} to={end} duration={duration:0.###}"
                    + $" speed={p.Speed:0.##} first={first}");
            return first ? [move]
                : [startHeight.StopAt(visual.Id, (int)Math.Round(p.Position.X), (int)Math.Round(p.Position.Y)), move];
        }
        public static ProjectileVector Vector((int X, int Y) p) => new(p.X, p.Y);
        static string? PayloadLabel(object? payload) => payload switch
        {
            ProjectileOffense offense => offense.Label,
            HostileProjectileOffense hostile => hostile.Label,
            MinionProjectileOffense minion => minion.Label,
            ObjectProjectileOffense thrown => thrown.Label,
            _ => null,
        };
        // A shot that logs itself (a trial's dart traps: 56 every 1.5 s).
        static bool Quiet(object? payload) => payload is ObjectProjectileOffense { Quiet: true };
    }

    public static ProjectileSession ProjectilesFor(Player player)
    {
        if (!CurrentGameWork) throw new OperationCanceledException("The Game generation retired");
        if (player.ProjectileRuntime is { Active: true } current) return current;
        var session = new ProjectileSession(player);
        if (!CurrentGameWork || !session.Active) { session.Invalidate(); throw new OperationCanceledException("The Game generation retired"); }
        player.ProjectileRuntime = session; session.Start();
        // ProjectileWorldTime now reads the runtime's clock, not the connection's: the steps timed on it read again.
        foreach (var step in ProjectileClockSteps) MarkPlayerStats(player, step);
        return session;
    }

    static readonly PlayerStatStep[] ProjectileClockSteps = [PlayerStatStep.PanelDefaults, PlayerStatStep.VaalPrevention, PlayerStatStep.Rage, PlayerStatStep.Rampage, PlayerStatStep.Resonance];

    public static void AdvanceProjectiles(Player player) => player.ProjectileRuntime?.Advance();

    // Advance and replace actor motion/life in one step of the area's frame: the runtime is advanced to now before the
    // change, so the pump cannot read the replacement as past movement.
    public static IDisposable ProjectileMutation(Player player, bool advance = true, string? phase = null) => new ProjectileMutationScope(player, advance, phase);
    public static double ProjectileWorldTime(Player player) => player.ProjectileRuntime is { Active: true } session
        ? session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond : AreaTime(player);

    public sealed class ProjectileMutationScope : IDisposable
    {
        readonly System.Threading.Lock experience;
        public ProjectileMutationScope(Player player, bool advance, string? phase)
        {
            experience = LevelUpdatesFor(player).Gate;
            experience.Enter();
            try
            {
                player.Area.AssertOwner();
                if (advance && !player.Paused && player.ProjectileRuntime is { Active: true } session)
                    session.AdvanceTo(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(AreaTime(player))));
                if (ChargeSessions.TryGetValue(player, out var charges)) charges.SettleDegeneration(ResourceEffectsTime(player));
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() => experience.Exit();
    }
}
