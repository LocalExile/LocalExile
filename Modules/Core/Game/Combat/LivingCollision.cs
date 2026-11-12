using System.Diagnostics;
using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public static async Task RunLivingCollisions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerUpdatesState updates)
    {
        if (!PlayerUpdatesCurrent(conn, updates)) return;
        int frames = 0;
        try
        {
            while (PlayerUpdatesCurrent(conn, updates) && conn.PendingTransfer is null)
            {
                if (!conn.Paused)
                {
                    if (IsMonsterRunner(conn)) await CorrectMonsterCollisions(remote, stream, ciphers, conn);
                    if (!PlayerUpdatesCurrent(conn, updates)) return;
                    await PushOutOfCollision(remote, stream, ciphers, conn, living: true);
                    if (!PlayerUpdatesCurrent(conn, updates)) return;
                    await ShowFreeze(remote, stream, ciphers, conn);
                    if (!PlayerUpdatesCurrent(conn, updates)) return;
                    await ReleaseHeldMove(remote, stream, ciphers, conn);
                    if (!PlayerUpdatesCurrent(conn, updates)) return;
                    await KeepWakeZone(remote, stream, ciphers, conn);
                    if (!PlayerUpdatesCurrent(conn, updates)) return;
                }
                PublishPlayerPose(conn);
                if (ServerTrace.Sync) TraceSync(conn);
                PublishAwakeMonsters(conn);
                await ApplyKillShares(remote, stream, ciphers, conn);
                if ((frames++ & 1) == 0)
                {
                    await SyncInstancePlayers(remote, stream, ciphers, conn);
                    await SyncInstanceMonsters(remote, stream, ciphers, conn);
                    await SyncSharedGround(remote, stream, ciphers, conn);
                    await SyncPeerMinions(remote, stream, ciphers, conn);
                }
                await updates.Schedule.Wait(PlayerUpdateRole.LivingCollisions, 50, updates.Stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        finally { if (conn.Closed || conn.LeavingArea) conn.Instance.RemovePlayer(conn.PeerId); }
    }

    // The client does not stop for is_frozen (live 2026-11-01), and virtual_action_speed_+% at -100, which would stop it,
    // divides its action clips by zero. A hit's recovery for as long as the freeze lasts stops it where it stands. A
    // predictive client goes on with its walk after, as the server's walk does; a lockstep one stands until its next order
    // (PlayerHitRecoveryRecordLocked: the same message and handler; the freeze itself is not traced).
    public static async Task ShowFreeze(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.FreezeShown == conn.FreezeGeneration) return;
        }
        await Send(remote, stream, ciphers, [], "0x01d1 player frozen", atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.FreezeShown == conn.FreezeGeneration) return null;
                double now = conn.Clock.Elapsed.TotalSeconds, left = conn.ElementalAilments.FreezeUntil(now) - now;
                if (left <= 0 || conn.PlayerLife == 0 || conn.LeavingArea || conn.Paused) return null;
                conn.FreezeShown = conn.FreezeGeneration;
                if (conn.LockstepClient) conn.Walk.Cancel(now);
                return [.. InstanceProtocol.ActorAnimation(PlayerId, (float)left, PlayerHitRecovery.TakeHitAnimation),
                    .. ResyncPacketLocked(conn, now)];
            }
        });
    }

    // A click that came during a hit's recovery or a freeze: the walk starts when that ends, as the client's does.
    public static async Task ReleaseHeldMove(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        lock (conn.Monsters)
            if (!CurrentGameWork || conn.HeldMove is null) return;
        // Accept the order and its cipher output together; otherwise leave the held click for replacement.
        await Send(remote, stream, ciphers, [], "0x01ab held move", atGate: () =>
        {
            using (ProjectileMutation(conn))
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0
                    || conn.HeldMove is not { } use) return null;
                double now = ProjectileWorldTime(conn);
                if (PlayerActionBlocked(conn, now, now)) return null;
                conn.HeldMove = null;
                Interlocked.Increment(ref conn.ActionCount);
                var from = conn.Walk.Order(use.X, use.Y, Math.Max(conn.HitRecovery.Until, now - MonsterAiCadence.StepSeconds));
                conn.ChannelSkill = 0;
                return InstanceProtocol.ActionStarted(PlayerId, from.X, from.Y, use.X, use.Y, use.SkillId, use.Slot, use.Flags);
            }
        });
    }

    // Game_TRACE=sync: where the server has the player and the monsters now, on the QPC clock of
    // scripts/re/frida/trace-sync.js. scripts/re/sync_report.py compares the two.
    public static void TraceSync(ClientState conn)
    {
        if (!conn.PlayerSent) return;
        var line = new System.Text.StringBuilder();
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var p = conn.Walk.Position(now);
            line.Append($"[sync] qpc={System.Diagnostics.Stopwatch.GetTimestamp()} peer={conn.PeerId:x} speed={conn.Walk.Speed:0.###} ");
            line.Append($"moving={(conn.Walk.Moving(now) ? 1 : 0)} p={p.X},{p.Y} m=");
            foreach (var (id, m) in conn.Monsters)
            {
                if (m.Dead || m.Life <= 0) continue;
                var at = m.Walk.Position(now);
                line.Append($"{id:x}:{at.X},{at.Y};");
            }
        }
        Console.WriteLine(line);
    }

    public static void PublishPlayerPose(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameWork) return;
            if (conn.Closed || conn.LeavingArea) { conn.Instance.RemovePlayer(conn.PeerId); return; }
            if (conn.PeerTemplate is null || !conn.PlayerSent) return;
            RefreshPeerScale(conn, CharacterPlayerStats(conn)[ActorScale.Final]);
        }
    }

    // Called under the owner's lock. Publish experience and final scale in an immutable template, retaining other creation stats,
    // equipment and skills. Observers read published snapshots and never lock another connection's simulation.
    public static void RefreshPeerScale(ClientState conn, int scale)
    {
        if (conn.PeerTemplate is not { } template) return;
        if (template.Experience != conn.Experience)
            conn.PeerTemplate = template = template with { Experience = conn.Experience };
        if (Stats.Key("level") is uint levelKey)
        {
            int level = Progression.LevelFor(conn.Experience, CharacterStats.LevelStarts);
            if ((template.Stats ?? []).SingleOrDefault(s => s.Key == levelKey).Value != level)
                conn.PeerTemplate = template = template with
                { Stats = [.. (template.Stats ?? []).Where(s => s.Key != levelKey), (levelKey, level)] };
        }
        if ((template.Stats ?? []).SingleOrDefault(s => s.Key == ActorScale.Final).Value != scale)
            conn.PeerTemplate = template = template with
            { Stats = [.. (template.Stats ?? []).Where(s => s.Key != ActorScale.Final), (ActorScale.Final, scale)] };
        if (!conn.PlayerSent || conn.Closed || conn.LeavingArea || conn.Instance is null) return;
        var p = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        conn.Instance.PublishPlayer(new(conn.PeerId, conn.PeerType, template, p.X, p.Y, conn.PlayerLife, conn)
            { Destination = conn.Walk.Destination, Auras = conn.PlayerLife == 0 || InTown(conn) ? []
                : conn.Auras.Values.Where(a => AuraCatalog.Shared(a.EffectId)).Select(a => new SharedAura(a,
                    AuraRadius.Of(a, conn.Pools.AuraAreaPct, SkillsFor(conn), Stats),
                    conn.Pools.AuraEffectPct + a.SupportEffectPct)).Concat(SharedAvianAuras(conn)).ToArray() });
    }

    // Stats.dat use_town_stance (row 21140; a stat key is its row + 1).
    const uint TownStanceStat = 21141;

    public static async Task SyncInstancePlayers(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var ids = conn.Instance.PlayersExcept(conn.PeerId).Select(p => p.Id)
            .Concat(conn.SeenPlayers.Keys).Distinct().ToArray();
        foreach (uint id in ids)
        {
            // A resurrected peer is a new incarnation. Release the send gate while its old removal crosses the wire.
            if (conn.LockstepClient && conn.SeenPlayers.TryGetValue(id, out var old) && old.Life == 0
                && conn.Instance.Player(id) is { Life: > 0 })
                await SendWorldRemoval(remote, stream, ciphers, conn, id, atGate: () =>
                {
                    if (!conn.SeenPlayers.TryGetValue(id, out var current) || current.Life != 0
                        || conn.Instance.Player(id) is not { Life: > 0 }) return null;
                    conn.SeenPlayers.Remove(id);
                    lock (conn.Monsters) conn.PeerWalks.Remove(id);
                    return InstanceProtocol.RemoveObject(id);
                });
            await SendWorldBatch(conn, [], admission =>
            {
                if (CombatLifetime is not null && !CurrentGameWork || conn.Closed || conn.LeavingArea) return null;
                var packets = new WorldRecordBuilder(conn, admission);
                // Read after waiting: queued creation/change must not restore an older scale or departed owner.
                if (conn.Instance.Player(id) is not { } peer)
                {
                    if (conn.SeenPlayers.Remove(id)) packets.Remove(id);
                    lock (conn.Monsters) conn.PeerWalks.Remove(id);
                    return packets.Build();
                }
                bool exists = conn.SeenPlayers.TryGetValue(peer.Id, out var previous);
                if (conn.LockstepClient && exists && (previous!.Life == 0 && peer.Life > 0
                    || !conn.WorldOutput.PublishedObject(peer.Id).IsReady)) return null;
                if (!exists || previous!.Life == 0 && peer.Life > 0)
                {
                    if (exists) packets.Remove(peer.Id);
                    // The beach wake-up is the owner's alone: observers get no action to end that stance with.
                    // Its skills, for the actions the owner's uses play on it (SharedMonsters.cs MirrorPlayerAction), all
                    // but the wake-up's.
                    var player = peer.Template with { GridX = peer.X, GridY = peer.Y, Life = peer.Life,
                        Skills = peer.Template.Skills?.Where(s => s.Id != TutorialObjects.WashedUpSkillId).ToList(),
                        QuestFlags = [], LeagueQuestFlags = [], WashedUp = false, Rotation = 0 };
                    // In a town another player stands and walks in the town stance (Stats.dat use_town_stance): without it
                    // the client drew every other player in its combat stance there.
                    if (InTown(conn))
                        player = player with { Stats = [.. (player.Stats ?? []).Where(s => s.Key != TownStanceStat), (TownStanceStat, 1)] };
                    packets.Create(peer.Id, InstanceEntities.CreateObject(peer.Id, peer.Type, player.Blob()));
                    // A lockstep client runs the player's copy itself from here on: so does its estimate (PeerWalks).
                    if (conn.LockstepClient)
                        lock (conn.Monsters)
                            conn.PeerWalks[peer.Id] = conn.NewPeerWalk(peer.Id, peer.X, peer.Y, peer.Owner?.Walk.Speed ?? Program.WalkSpeed);
                    packets.Presentation(peer.Id, InstanceProtocol.EnterWakeZone(peer.Id));
                    // The client picks an actor's stance (animation set: "Town" in a town, else the weapon's; 0x141e9da30)
                    // when its equipment changes, not when it is created: without this every other player stood in the
                    // combat stance (Frida 2026-11-01: the pick ran only for the local player, on its own 0x2bc).
                    packets.Presentation(peer.Id, InstanceEntities.PlayerEquipmentUpdate(peer.Id, player.Equipped ?? []));
                }
                else
                {
                    // A walk is the Move action the client paths and animates (as monsters walk), started when the walk's
                    // destination changes; a glide (MoveTo) slid a standing body across the floor. A jump without a walk
                    // (a /tp, a resurrection) stays a teleport.
                    const ushort Move = 0x2909;
                    var destination = peer.Destination ?? (peer.X, peer.Y);
                    var heading = previous!.Destination ?? (previous.X, previous.Y);
                    // A player on a connection of its own walks here by the orders its client is sent (SharedMonsters.cs
                    // MirrorPlayerAction), and its copy stops where those end. Only a player without one (a
                    // server-driven one) is walked from its published pose.
                    bool ordered = peer.Owner is { } owner && Program.InGame.ContainsKey(owner);
                    if (!ordered && destination != heading && destination != (peer.X, peer.Y))
                        packets.World(peer.Id, InstanceProtocol.ActionStarted(peer.Id, peer.X, peer.Y, destination.X, destination.Y, Move, 0xffff, 0x0408));
                    else if (destination == (peer.X, peer.Y) && (previous.X, previous.Y) != (peer.X, peer.Y)
                        && Math.Max(Math.Abs(previous.X - peer.X), Math.Abs(previous.Y - peer.Y)) > 20 && heading == (previous.X, previous.Y))
                    {
                        packets.World(peer.Id, InstanceProtocol.Teleport(peer.Id, peer.X, peer.Y, 0x7fffffff));
                        lock (conn.Monsters)
                            if (conn.PeerWalks.TryGetValue(peer.Id, out var jumped)) jumped.Teleport(peer.X, peer.Y, conn.Clock.Elapsed.TotalSeconds);
                    }
                    else if (!ordered && destination == (peer.X, peer.Y) && heading != destination)
                    {
                        // Stopped short of where it was going (a body in the way): the observers' walk stops there too.
                        packets.World(peer.Id, InstanceProtocol.ActionEnded(peer.Id));
                        packets.World(peer.Id, InstanceProtocol.Teleport(peer.Id, peer.X, peer.Y, 0x7fffffff));
                    }
                    if (previous.Life != peer.Life)
                        packets.World(peer.Id, InstanceProtocol.LifeUpdate(peer.Id, peer.Life));
                    if (previous.Template.Experience != peer.Template.Experience)
                        packets.World(peer.Id, InstanceEntities.PlayerExperienceUpdate(peer.Id, peer.Template.Experience));
                    if (Stats.Key("level") is uint levelKey)
                    {
                        int level = (peer.Template.Stats ?? []).SingleOrDefault(s => s.Key == levelKey).Value;
                        int oldLevel = (previous.Template.Stats ?? []).SingleOrDefault(s => s.Key == levelKey).Value;
                        if (level != oldLevel) packets.World(peer.Id, InstanceEntities.StatsUpdate(peer.Id, [(levelKey, level)]));
                    }
                    if (Progression.LevelFor(peer.Template.Experience, CharacterStats.LevelStarts)
                        > Progression.LevelFor(previous.Template.Experience, CharacterStats.LevelStarts))
                    {
                        uint effect = conn.TakeObjectId();
                        conn.TemporarySkillEffects[effect] = AreaTime(conn) + 3;
                        packets.Create(effect, InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                            new EffectObject(peer.X, peer.Y, "Metadata/Effects/misc/levelup_new/rig.ao")
                            { Parent = peer.Id }.Blob()));
                        packets.Presentation(effect, InstanceProtocol.EnterWakeZone(effect));
                    }
                    int scale = (peer.Template.Stats ?? []).SingleOrDefault(s => s.Key == ActorScale.Final).Value;
                    int before = (previous.Template.Stats ?? []).SingleOrDefault(s => s.Key == ActorScale.Final).Value;
                    if (scale != before)
                        packets.World(peer.Id, InstanceEntities.StatsUpdate(peer.Id, [(ActorScale.Final, scale)]));
                }
                conn.SeenPlayers[peer.Id] = peer;
                return packets.Build();
            }, transport: (remote, stream, ciphers));
        }
    }

    public static Task CorrectMonsterCollisions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        using (ProjectileMutation(conn, phase: "collision"))
        {
            if (!CurrentGameWork) return Task.CompletedTask;
            StepWalkers(conn, ProjectileWorldTime(conn));
        }
        return Task.CompletedTask;
    }

    // The client steps every walker a cell at a time and checks each step (0x141e35a50): the cell's walk value must be at
    // least the walker's size + 1, and no other body may be within reach (0x141e4b140 → 0x141e4b3a0: the Chebyshev distance
    // less that body's size below the walker's size + 1). A blocked step ends the walk where it stands, with no new path
    // (0x141e82310); a chaser's next order paths it again. A walk whose path began on a blocked cell steps through bodies
    // until its first free step; terrain still stops it. A path cut short by the search is walked to its end and then paths
    // again from there at once (0x141e825b0 → 0x141e82010). The client moves nobody for any other reason, so this replays
    // the steps taken since the last pass, the player's and every monster's, each against the cells the others hold at
    // that moment: on a predictive client in time order; on a lockstep client frame by frame as its world ticks
    // (StepFrames). Called under conn.Monsters.
    public static void StepWalkers(ClientState conn, double now)
    {
        double from = conn.WalkersSteppedTo;
        // A lockstep client's walkers move only in its frames, so whole frames are replayed, up to the start of the frame
        // being sent (its 0x2b7 is on the wire: the frame advances in that send's gate under this lock); the frame's own
        // steps wait for its end (PlayerWalk.Position reads the frame's start as well).
        (double Start, double End)? frame = conn.LockstepClient ? conn.LockstepFrame : null;
        if (frame is { } lockstep) now = lockstep.Start;
        if (now <= from) return;
        conn.WalkersSteppedTo = now;
        var walks = new Dictionary<uint, GameServer.Core.World.PlayerWalk>();
        // A lockstep client's killed monster walks until its death reaches the client (ClientState.Corpse).
        foreach (var (id, m) in conn.Monsters)
            if (LivingCollision.StepsWithBodies(m) || conn.CorpseWalks(id, m)) walks[id] = m.Walk;
        if (conn.PlayerSent && conn.PlayerLife > 0) walks[PlayerId] = conn.Walk;
        // The other players' copies on a lockstep client: walkers like any other, ticked in id order.
        if (frame is not null)
            foreach (var (id, copy) in conn.PeerWalks) walks[id] = copy;
        if (frame is { } f)
        {
            StepFrames(conn, walks, from, now, f.End - f.Start);
            return;
        }
        var pending = new Dictionary<uint, double>();
        foreach (var (id, walk) in walks)
            if (walk.ArrivesAt > from) pending[id] = from;
        if (pending.Count == 0 && walks.Values.All(w => w.Reached)) return;
        using var bodies = conn.BeginMonsterBlockerFrame(from);
        for (int round = 0; round < 8; round++)
        {
            var steps = new List<(double At, uint Id, (int X, int Y) Cell)>();
            foreach (var (id, since) in pending)
                foreach (var (at, cell) in walks[id].Steps(since, now)) steps.Add((at, id, cell));
            steps.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Id.CompareTo(b.Id));
            var stopped = new HashSet<uint>();
            foreach (var (at, id, cell) in steps)
            {
                if (stopped.Contains(id)) continue;
                if (!Step(bodies, id, walks[id], at, cell)) stopped.Add(id);
            }
            pending.Clear();
            foreach (var (id, walk) in walks)
            {
                if (walk.Reached || walk.ArrivesAt > now) continue;
                double at = walk.ArrivesAt;
                walk.Repath(at);
                pending[id] = at;
            }
            if (pending.Count == 0) break;
        }
    }

    // A lockstep client's world ticks each server frame with every object in turn, in ascending object id (the object
    // tree of 0x14218a650, 0x14218aff0 per object; the player, 0x1000, before every monster, 0x3000 and up), and a walker's
    // Positioned (its vtable slot +0x40 = 0x141e35a50) takes all of its steps due in the frame before the next object
    // ticks: its accumulator (+0x244) gains the frame's length (33 ms as float seconds, nothing rounded) and steps while
    // it holds a step time, each step checked against the other objects' current cells (0x141e4b3a0 reads +0x294/+0x298),
    // so a walker ticked earlier in the frame is seen where this frame's steps put it and a later one where the frame
    // before left it (live 2026-11-03, scripts/re/frida/trace-lockstep-tick.js; 07 §5). The frame a step falls in is the
    // one the walk's times give (the
    // accumulator carries the remainder between steps and segments, and a new order mid-step keeps the step's fraction:
    // 0x141e50880 rescales it by the new step time), so the same steps are taken here, grouped by frame and ordered by
    // object within it. A path the search cut short paths again at its end within the tick. A walk-up the client runs
    // (a monster's chase, the player's attack on a monster) also tests its goal in its own turn of each frame
    // (StepWalkUp), whether its body blocks or not.
    static void StepFrames(ClientState conn, Dictionary<uint, GameServer.Core.World.PlayerWalk> walks, double from, double to, double length)
    {
        var chases = new Dictionary<uint, SpawnedMonster>();
        foreach (var (id, m) in conn.Monsters)
            if (WalkUpOpen(conn, m)) chases[id] = m;
        var attack = PlayerWalkUpOpen(conn);
        if (chases.Count == 0 && attack is null && walks.Values.All(w => w.ArrivesAt <= from && w.Reached)) return;
        using var bodies = conn.BeginMonsterBlockerFrame(from);
        var order = walks.Keys.Union(chases.Keys).Order().ToArray();
        // Frame ends at to, to - length, ...; the oldest one replayed starts at from (a pass far behind lumps the rest).
        int frames = Math.Clamp((int)Math.Ceiling((to - from) / length - 1e-9), 1, 100);
        for (int i = frames; i >= 1; i--)
        {
            double end = to - (i - 1) * length, start = i == frames ? from : end - length;
            foreach (uint id in order)
            {
                if (id == PlayerId && attack is not null && PlayerWalkUpOpen(conn) == attack)
                {
                    var (result, timer) = StepWalkUp(bodies, new(PlayerId, conn.Walk, true, attack.TargetId, attack.Target?.Walk,
                        attack.Reach, attack.Tolerance, attack.TimerFrom, attack.GroundTarget), start, end, length);
                    attack.TimerFrom = timer;
                    if (result == WalkUpResult.Swings) LockstepAttackSwings(conn, attack, start, end);
                    else if (result == WalkUpResult.Fails) attack.Fails();
                    continue;
                }
                if (chases.TryGetValue(id, out var chaser) && WalkUpOpen(conn, chaser))
                {
                    var (result, timer) = StepWalkUp(bodies, new(id, chaser.Walk, walks.ContainsKey(id), PlayerId,
                        walks.GetValueOrDefault(PlayerId) ?? conn.Walk, (int)(chaser.Attack!.Range - 1), LivingCollision.PlayerSize,
                        chaser.ClientChaseTimerFrom), start, end, length);
                    chaser.ClientChaseTimerFrom = timer;
                    if (result != WalkUpResult.Walking) WalkUpEnds(chaser, start, result == WalkUpResult.Swings);
                    continue;
                }
                if (!walks.TryGetValue(id, out var walk)) continue;
                // The client's Actor ended an in-place action in the frame before: its stop ends the walk that was left
                // going under the action, with this tick's steps (SpawnedMonster.ClientActionWalkStop).
                if (conn.Monsters.GetValueOrDefault(id) is { ClientActionWalkStop: { } stopAt } acting && start >= stopAt - 1e-6)
                {
                    acting.ClientActionWalkStop = null;
                    if (walk.Moving(start)) walk.StopAfter(end, start);
                }
                for (int round = 0; round < 8; round++)
                {
                    bool stopped = false;
                    foreach (var (at, cell) in walk.Steps(start, end))
                        if (!Step(bodies, id, walk, at, cell)) { stopped = true; break; }
                    if (stopped || walk.Reached || walk.ArrivesAt > end) break;
                    walk.Repath(walk.ArrivesAt);
                }
                // A corpse's death clip clears its body in its own turn: the walkers after it step without it.
                if (conn.CorpseClears(id, start)) bodies.Remove(id);
            }
            // Game_TRACE=sync: every walker's cell after the frame, with how far it is into its step and the step's length,
            // on the QPC clock of scripts/re/frida/trace-sync.js.
            if (ServerTrace.Sync)
            {
                var line = new System.Text.StringBuilder();
                line.Append($"[frame] qpc={Stopwatch.GetTimestamp()} n={conn.ServerFrame - 1 - (long)Math.Round((to - end) / length)} end={end:0.000} now={to:0.000} m=");
                foreach (uint id in order)
                {
                    if (!walks.TryGetValue(id, out var traced)) continue;
                    var at = traced.Position(end);
                    var (into, step) = traced.StepPhase(end);
                    line.Append($"{id:x}:{at.X},{at.Y}/{into:0.0000}/{step:0.0000};");
                }
                Console.WriteLine(line);
            }
        }
    }

    // A lockstep client's monster walking up to the player on its chase order (the attack aimed at him) that has neither
    // swung nor failed yet: the order went out (PlaceLockstepChase) and nothing the client gets has replaced it. A planned
    // order carries it to its own generation until the gate places it, and a give-up or a freeze, which send nothing,
    // keep it (NextPlannedAction); a swing order, a flinch, a stage and a transfer move the action generation on and end
    // it. Out of the wake zone its copy does not tick.
    public static bool WalkUpOpen(ClientState conn, SpawnedMonster m) =>
        conn.LockstepClient && m.ClientChase == m.ActionGeneration && !m.Dead && m.Life > 0 && !m.Asleep;

    // A lockstep client's attack on a monster that is walking up (placed at its gate, goal not met) while it is still the
    // player's action: an order the client takes ends it (a Move within two cells is none: PlaceLockstepPlayerMove places
    // nothing), as do a hit's recovery and a transfer; a target gone from the area ends it too (not traced). A closed one
    // is failed here, so the attack's task wakes. Under conn.Monsters.
    public static LockstepAttack? PlayerWalkUpOpen(ClientState conn)
    {
        if (!conn.LockstepClient || conn.LockstepAttack is not { Open: true } attack) return null;
        if (conn.ActionCount == attack.Action && conn.PlayerSent && conn.PlayerLife > 0 && !conn.Closed && !conn.LeavingArea
            && (attack.Target is null || ReferenceEquals(conn.Monsters.GetValueOrDefault(attack.TargetId), attack.Target))) return attack;
        attack.Fails();
        return null;
    }

    // A walk-up a lockstep client's MoveTo runs toward an object, in its walker's turn of a frame: a monster's chase at the
    // player (WalkUpOpen) or the player's attack on a monster (PlayerWalkUpOpen).
    readonly record struct WalkUpTurn(uint Id, GameServer.Core.World.PlayerWalk Walk, bool Blocks, uint TargetId,
        GameServer.Core.World.PlayerWalk? Target, int Reach, int Tolerance, double TimerFrom, (int X, int Y) GroundTarget = default);
    enum WalkUpResult { Walking, Swings, Fails }

    // A walk-up's turn in a lockstep frame (07 §5). Its steps are checked as any walker's (none for a body that blocks
    // nothing), and its MoveTo's goal (0x141e81950: within the reach less one, widened by the target's size, with the line
    // to it) is tested against the target object's own cell (0x14123f9c0) as the tick order leaves it: a target with a
    // lower id has ticked this frame, one with a higher id stands where the frame before left it (objects tick in
    // ascending id: 2 951 of 2 951 traced monster tests used the player's cell after his steps,
    // 181 of 181 of the player's on the monster's before its own). It is tested at each waypoint the walker steps into
    // (0x141e825b0) and after the frame's steps (the Pathfinding tick, 0x141e826a0). Met, the swing starts in this frame
    // (0x141e82310 → 0x141e9ab90 → 0x141e9afc0, which waits for no 0x1ac in lockstep): at a waypoint or the path's end the
    // walker stands there at once; mid-piece its stop (0x141e38420) waits for the next tick's steps. A blocked step tests
    // the goal at the cell it stopped on (its callback is 0x141e82310 too): met, it swings; else the action ends. Out of
    // reach at the path's end, cut short by the search or not, it searches again from there toward the target's cell in
    // the same tick (0x141e82010); no path ends the action. Mid-path, out of goal, it searches again only toward a target
    // that has left the cell it last searched toward, 2.0 s after that search, or 0.7 s after it once the target is 64
    // cells from that cell (0x141e826a0: each tick adds its frame to +0x55c before the test, and 0x141e82010 zeroes it;
    // decompiled, not reached live). Returns the turn's outcome and when the search timer last started.
    static (WalkUpResult Result, double TimerFrom) StepWalkUp(ClientState.MonsterBlockerFrame bodies, WalkUpTurn up,
        double start, double end, double length)
    {
        var walk = up.Walk;
        var target = up.Target?.Position(up.Id < up.TargetId ? start : end) ?? up.GroundTarget;
        double timerFrom = up.TimerFrom;
        bool InGoal((int X, int Y) cell) => walk.InReachOf(cell, target, up.Reach, up.Tolerance);
        for (int round = 0; round < 8; round++)
        {
            foreach (var (at, cell) in walk.Steps(start, end))
            {
                if (up.Blocks && !Step(bodies, up.Id, walk, at, cell))
                    return (InGoal(walk.Position(end)) ? WalkUpResult.Swings : WalkUpResult.Fails, timerFrom);
                if (walk.EndsPiece(at) && InGoal(cell))
                {
                    walk.StopAfter(at);
                    return (WalkUpResult.Swings, timerFrom);
                }
            }
            bool walking = walk.ArrivesAt > end;
            if (InGoal(walk.Position(end)))
            {
                walk.StopAfter(walking ? end + length : walk.ArrivesAt, end);
                return (WalkUpResult.Swings, timerFrom);
            }
            if (walking)
            {
                var searched = walk.SearchedFor;
                long dx = searched.X - target.X, dy = searched.Y - target.Y;
                double timer = end - timerFrom;
                if (searched == target || timer < 2.0 && (timer < 0.7 || dx * dx + dy * dy < 4096))
                    return (WalkUpResult.Walking, timerFrom);
                var before = walk.Save();
                walk.Approach(target.X, target.Y, up.Reach, end, up.Tolerance, walk.Precision);
                // Searched after this tick's add: the timer counts again from the frame's end.
                if (walk.ArrivesAt > end) return (WalkUpResult.Walking, end);
                // No step to take (no path, or the goal at precision 1 where he stands): 0x141e82010 → 0x141e82310, a stop
                // after the next tick's steps in this replayed frame (the order's own stop would wait for the end of the
                // frame being sent).
                bool met = walk.ReachedGoal;
                walk.Restore(before);
                walk.StopAfter(end + length, end);
                return (met ? WalkUpResult.Swings : WalkUpResult.Fails, end);
            }
            walk.Approach(target.X, target.Y, up.Reach, Math.Max(walk.ArrivesAt, start), up.Tolerance, walk.Precision);
            // Searched inside the Positioned tick, before this frame's add.
            timerFrom = start;
            if (walk.Reached && !walk.ReachedGoal) return (WalkUpResult.Fails, timerFrom);
        }
        return (WalkUpResult.Walking, timerFrom);
    }

    // A walk-up that swung, from the frame's start, for the AI to take (PlanMonsterAi), or failed: the client's action
    // ends with no swing.
    static void WalkUpEnds(SpawnedMonster m, double start, bool swings)
    {
        if (swings) (m.ClientSwingAt, m.ClientSwingAction) = (start, m.ActionGeneration);
        else m.LastTarget = null;
        m.ClientChase = null;
    }

    // One step of a walk, as the client checks it: false when it was blocked and ended the walk.
    static bool Step(ClientState.MonsterBlockerFrame bodies, uint id, GameServer.Core.World.PlayerWalk walk, double at, (int X, int Y) cell)
    {
        bool floor = walk.Floor(cell.X, cell.Y);
        bool body = floor && ClientPathfinder.ObjectBlocks(id == PlayerId ? bodies.ForPlayer : bodies.For(id),
            cell.X, cell.Y, walk.Size + 1, walk.Size + 4);
        if (!floor || body && !walk.WalksThrough)
        {
            walk.StopAt(at);
            return false;
        }
        if (!body) walk.WalksThrough = false;
        if (id == PlayerId) bodies.MovePlayer(cell.X, cell.Y);
        else bodies.Move(id, cell.X, cell.Y);
        return true;
    }
}
