using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class OpeningSceneTests
{
    static readonly NpcCatalog.Npc Exile = new(6, TutorialObjects.Exile, "Dying Exile", ["Transitionable"], null, 1);
    // A save written before WakeUpShown and WeaponPromptHeard existed.
    static CharacterSaves.OpeningTutorial Untouched() =>
        JsonSerializer.Deserialize<CharacterSaves.OpeningTutorial>("""{"SkillGem":{"Type":123},"SupportGem":{"Type":123}}""")!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_untouched_character_lies_down_facing_the_room_and_gets_up_in_place_once(bool withSkills)
    {
        if (!withSkills) { await CheckOpeningHiddenState(); await CheckSharedMessageOwnership(); }
        var tutorial = Untouched();
        Assert.True(TutorialObjects.ShouldStartWashedUp(TutorialObjects.Strand, true, () => tutorial));
        Assert.False(TutorialObjects.ShouldStartWashedUp(TutorialObjects.Strand, true, () => null));
        // Every other area and every picked-up weapon decide without reading the save.
        Assert.False(TutorialObjects.ShouldStartWashedUp("1_1_town", true, () => throw new InvalidOperationException()));
        Assert.False(TutorialObjects.ShouldStartWashedUp(TutorialObjects.Strand, false, () => throw new InvalidOperationException()));
        foreach (var progressed in new[] { tutorial with { WakeUpShown = true }, tutorial with { WeaponPromptHeard = true },
            tutorial with { Talked = true }, tutorial with { ZombieKilled = true }, tutorial with { ChestOpened = true },
            tutorial with { SkillClaimed = true }, tutorial with { SupportClaimed = true } })
            Assert.False(TutorialObjects.ShouldStartWashedUp(TutorialObjects.Strand, true, () => progressed));

        // The start room's angle, through a doodad fit and through a known placement, in all eight orientations.
        for (int code = 0; code < 8; code++)
        {
            var placement = new TutorialObjects.RoomPlacement(0, 0, 100, 100, code);
            var (origin, x, y) = (placement.Place(new("", 0, 0)), placement.Place(new("", 1, 0)), placement.Place(new("", 0, 1)));
            float fitted = AreaPopulation.TurnAngle(x.X - origin.X, y.X - origin.X, x.Y - origin.Y, y.Y - origin.Y, MathF.PI * 1.5f);
            Assert.Equal(0, MathF.IEEERemainder(fitted - placement.Place(new("", 0, 0, MathF.PI * 1.5f)).Angle, MathF.Tau), 4);
        }

        var player = new PlayerObject("Ab", 1, 115, 69, 50, 40,
            Skills: withSkills ? [new(0x103, 532, 42)] : null, Souls: [new(87, 30, 7)]);
        byte[] standing = player.Blob();
        byte[] washed = (player with { WashedUp = true, Rotation = MathF.PI }).Blob();
        Assert.Equal(MathF.PI, BitConverter.ToSingle(washed, 10));
        Assert.Equal((byte)1, washed[126]); // Player's WashedUp flag.
        Assert.Equal(PlayerObject.WashedUpStance, washed[145]); // Actor stance, before its variable-length skill/soul list.
        // Exactly one (row, capacity, current) tuple, followed by the cooldown count and tasks.
        Assert.EndsWith("01570000001E000000070000000000000000", Convert.ToHexString(washed));
        Array.Clear(washed, 10, 4);
        washed[126] = 0;
        washed[145] = 0;
        Assert.Equal(standing, washed);

        using var wire = new Wire();
        var conn = wire.Conn;
        Task GetUp() => Game.WakeUpOnBeach("opening-scene-test", wire.Stream, wire.Ciphers, conn, (20, 25), 1, 0, conn.Clock.Elapsed.TotalSeconds);
        conn.PeerTemplate = player;
        await GetUp();
        Assert.False(conn.TutorialGettingUp);

        // A stun between accepting the click and sending its echo drops the echo; the next click still works.
        conn.PeerTemplate = player with { WashedUp = true };
        var gate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
        await gate.WaitAsync();
        var dropped = GetUp();
        conn.ActionCount++;
        gate.Release();
        await dropped;
        Assert.False(conn.TutorialGettingUp);

        await GetUp();
        Assert.True(conn.TutorialGettingUp);
        Assert.Equal((10, 10), conn.Walk.Destination);
        byte[] echo = InstanceProtocol.ActionStarted(Program.PlayerId, 10, 10, 20, 25, TutorialObjects.WashedUpSkillId, 1, 0);
        Assert.Equal(echo, wire.Read(echo.Length));
        int accepted = conn.ActionCount;
        await GetUp();
        Assert.Equal(accepted, conn.ActionCount);

        // Asking to walk to an object gets the character up instead, as the server's own action.
        using var clicked = new Wire();
        clicked.Conn.PeerTemplate = player with { WashedUp = true };
        await Game.OnUseObject("opening-scene-test", clicked.Stream, clicked.Ciphers, clicked.Conn,
            new InstanceTransfer.UseSkillOnObject(999, SkillCatalog.FirstSkillId, 1, 0), clicked.Conn.Clock.Elapsed.TotalSeconds);
        Assert.Equal((10, 10), clicked.Conn.Walk.Destination);
        byte[] serverAction = InstanceProtocol.ActionStarted(Program.PlayerId, 10, 10, 10, 10, TutorialObjects.WashedUpSkillId, TutorialObjects.GetUpSequence, 0);
        Assert.Equal(serverAction, clicked.Read(serverAction.Length));

        // Observers get no action to end the stance with: another player's copy is created standing, in the owner's stead,
        // with the owner's skills (their uses play on it) but not the wake-up.
        using var observer = new Wire();
        observer.Conn.Instance.PublishPlayer(new(0x20001234, 7, player with { WashedUp = true, Rotation = MathF.PI }, 115, 69, 50));
        await Game.SyncInstancePlayers("opening-scene-test", observer.Stream, observer.Ciphers, observer.Conn);
        byte[] created = InstanceEntities.CreateObject(0x20001234, 7, (player with { Skills = player.Skills?.Where(s => s.Id != TutorialObjects.WashedUpSkillId).ToList(),
            QuestFlags = [], LeagueQuestFlags = [] }).Blob());
        Assert.Equal(created, observer.Read(created.Length));
    }

    static async Task CheckOpeningHiddenState()
    {
        const uint id = 0x08000099;
        const ushort buff = 0x1234;
        var template = new MonsterObject(10, 10, 23, [.. MonsterObject.BaseComponents, "Transitionable"], TransitionState: 1);
        byte[] ordinary = template.Blob(), hidden = (template with { HiddenBuffInstance = buff }).Blob();
        int offset = Enumerable.Range(0, ordinary.Length).First(i => ordinary[i] != hidden[i]);
        // Native fixed creation layout with a synthetic instance id, including infinite duration and empty list.
        byte[] entry = Convert.FromHexString("3412028A0000000000807F0000803F010000000000000000000000000000000000000000");
        Assert.Equal(36, entry.Length);
        Assert.Equal(ordinary.Length + entry.Length, hidden.Length);
        Assert.Equal(new byte[] { 1, 0 }, hidden[offset..(offset + 2)]);
        Assert.Equal(entry, hidden[(offset + 2)..(offset + 2 + entry.Length)]);
        Assert.Equal(ordinary[(offset + 2)..], hidden[(offset + 2 + entry.Length)..]);

        var stats = new StatCatalog();
        uint baseStun = stats.Key("base_cannot_be_stunned")!.Value, stun = stats.Key("cannot_be_stunned")!.Value;
        template = template with { Stats = [(baseStun, 1), (stun, 1), (StatKeys.MaximumLife, 23)] };
        var hiddenState = MonsterEmerge.WithHiddenStats(template with { HiddenBuffInstance = buff }, stats);
        var restored = MonsterEmerge.RestoreHiddenStats(stats, template.Stats);
        Assert.Equal(6, restored.Count);
        Assert.Equal(7, hiddenState.Stats!.Count); // One key per hidden stat plus the unrelated maximum life.
        Assert.All(restored, s => Assert.Contains((s.Key, 1), hiddenState.Stats));
        Assert.Contains((StatKeys.MaximumLife, 23), hiddenState.Stats);
        Assert.Equal(1, restored.Single(s => s.Key == baseStun).Value);
        Assert.Equal(1, restored.Single(s => s.Key == stun).Value);
        Assert.All(restored.Where(s => s.Key != baseStun && s.Key != stun), s => Assert.Equal(0, s.Value));
        Assert.Equal(3, template.Stats!.Count); // Hidden overrides never mutate the reconnect template.

        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            using var wire = new Wire();
            var variety = new MonsterCatalog().Find("Metadata/Monsters/ZombieDismember/ZombieDrownedStrandWake1");
            Assert.NotNull(variety);
            CheckOpeningWakeRange(variety);
            var m = new SpawnedMonster("hidden", wire.Conn.NewMonsterWalk(id, 10, 10, 1, 1, false), 24, 2, 23)
                { Variety = variety, HiddenBuffInstance = buff };
            wire.Conn.Monsters[id] = m;
            wire.Conn.MonsterViews[id] = new((10, 10), (10, 10), 23);
            wire.Conn.Instance.World.MonsterOrigins[id] = new(123, template with { HiddenBuffInstance = buff }, (ushort)variety.Row, 1, MonsterRarity.Normal);
            byte index = (byte)variety.ComponentsOrBase.ToList().IndexOf("Transitionable");
            var messages = MonsterEmerge.RiseMessages(id, (10, 10), 0x101, 0x8001, index, 2, buff, restored);
            Assert.Equal(InstanceEntities.RemoveBuff(id, buff), messages[0]);
            Assert.Equal(InstanceEntities.StatsUpdate(id, restored), messages[1]);
            Assert.Equal(InstanceProtocol.ActionStartedNow(id, 10, 10, 10, 10, 0x101, 0x8001, 0x0800, id), messages[2]);
            Assert.Equal(InstanceEntities.TransitionableUpdate(id, index, 2), messages[3]);
            await Game.Emerge("hidden-test", wire.Stream, wire.Ciphers, wire.Conn, id, m, (10, 10), 1);
            byte[] rise = [.. messages.SelectMany(p => p)];
            Assert.Equal(rise, wire.Read(rise.Length));

            // Rejected work cannot remove only the buff or consume the next action's sequence.
            var gate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
            await gate.WaitAsync();
            var pending = Game.Emerge("hidden-test", wire.Stream, wire.Ciphers, wire.Conn, id, m, (10, 10), 1);
            m.Dead = true;
            gate.Release();
            await pending;
            Assert.Equal((ushort)0x8001, m.Sequence);

            // A new viewer after the rise gets a targetable creation without the old hidden buff.
            m.Dead = false;
            wire.Conn.PlayerSent = true;
            wire.Conn.MonsterViews.Remove(id);
            await Game.SyncInstanceMonsters("hidden-test", wire.Stream, wire.Ciphers, wire.Conn);
            byte[] snapshot = [.. InstanceEntities.MonsterTypes([((ushort)variety.Row, (byte)1)]),
                .. InstanceEntities.CreateObject(id, 123, (template with { TransitionState = 2 }).Blob()),
                .. InstanceProtocol.EnterWakeZone(id)];
            Assert.Equal(snapshot, wire.Read(snapshot.Length));
            await CheckWaitingMonsterReplay(variety);
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            if (previousLogic is not null) Program.SetGameLogic(previousLogic);
        }
    }

    static async Task CheckWaitingMonsterReplay(MonsterCatalog.Variety variety)
    {
        const uint id = 0x08000077;
        const ushort buff = 0x4321;
        var template = new MonsterObject(20, 20, 23, variety.ComponentsOrBase, TransitionState: 1) { HiddenBuffInstance = buff };
        using var wire = new Wire();
        var conn = wire.Conn;
        conn.PlayerSent = true;
        var m = new SpawnedMonster("waiting-replay", conn.NewMonsterWalk(id, 20, 20, 1, 1, false), 60, 2, 23)
            { Variety = variety, HiddenBuffInstance = buff };
        conn.Instance.World.MonsterOrigins[id] = new(123, template, (ushort)variety.Row, 1, MonsterRarity.Normal);
        MonsterEmerge.AddLocked(conn, id, m, EmergeKind.Buried, true);
        // This test steps the watcher explicitly; no real-time worker or delay is started.
        MonsterEmerge.StartWatch(conn);
        await Game.SyncInstanceMonsters("waiting-replay", wire.Stream, wire.Ciphers, conn);
        byte[] creation = [.. InstanceEntities.MonsterTypes([((ushort)variety.Row, (byte)1)]),
            .. InstanceEntities.CreateObject(id, 123, MonsterEmerge.WithHiddenStats(template, new()).Blob()),
            .. InstanceProtocol.EnterWakeZone(id)];
        Assert.Equal(creation, wire.Read(creation.Length));
        Assert.True(conn.MonsterViews.ContainsKey(id));
        Assert.False(conn.Monsters.ContainsKey(id));
        await Game.SyncInstanceMonsters("waiting-replay", wire.Stream, wire.Ciphers, conn);
        Assert.True(conn.MonsterViews.ContainsKey(id)); // A buried view must survive the next snapshot.

        var delivered = new List<byte[]>();
        var rejected = new List<byte[]>();
        foreach (var (known, asleep) in new[] { (true, false), (false, false), (true, true) })
        {
            var other = new ClientState(conn.Layout, 0, 1) { Instance = conn.Instance, PlayerSent = true };
            other.Outbox = (bytes, _) => { (known && !asleep ? delivered : rejected).Add(bytes); return Task.CompletedTask; };
            if (known) other.MonsterViews[id] = new((20, 20), (20, 20), 23);
            if (asleep) other.AsleepMonsters.Add(id);
            // AwakeMonsters is deliberately empty: the actor has only just left the buried roster.
            conn.Instance.PublishPlayer(new(other.PeerId, 7, new("Peer", 1, 10, 10, 50, 40), 10, 10, 50, other));
        }
        Assert.Single(new MonsterEmerge().RiseLocked(conn, (20, 20), 10, out _));
        byte index = (byte)variety.ComponentsOrBase.ToList().IndexOf("Transitionable");
        byte[] rise = [.. MonsterEmerge.RiseMessages(id, (20, 20), 0x101, 0x8001, index, 2, buff,
            MonsterEmerge.RestoreHiddenStats(new(), null)).SelectMany(p => p)];
        await Game.Emerge("waiting-replay", wire.Stream, wire.Ciphers, conn, id, m, (20, 20), 1);
        Assert.Equal(rise, wire.Read(rise.Length)); // No duplicate creation/removal was emitted by the second snapshot.
        Assert.Equal(rise, Assert.Single(delivered));
        Assert.Empty(rejected);
        MonsterEmerge.StopWatch(conn);

        // If a new runner has not received any snapshot yet, updates cannot precede its creation.
        using var unseen = new Wire();
        unseen.Conn.PlayerSent = true;
        unseen.Conn.Monsters[id] = new("already-rising", unseen.Conn.NewMonsterWalk(id, 20, 20, 1, 1, false), 60, 2, 23)
            { Variety = variety, HiddenBuffInstance = buff };
        unseen.Conn.Instance.World.MonsterOrigins[id] = new(123, template, (ushort)variety.Row, 1, MonsterRarity.Normal);
        await Game.Emerge("unseen-replay", unseen.Stream, unseen.Ciphers, unseen.Conn, id, unseen.Conn.Monsters[id], (20, 20), 1);
        await Game.SyncInstanceMonsters("unseen-replay", unseen.Stream, unseen.Ciphers, unseen.Conn);
        byte[] risenCreation = [.. InstanceEntities.MonsterTypes([((ushort)variety.Row, (byte)1)]),
            .. InstanceEntities.CreateObject(id, 123, (template with { HiddenBuffInstance = null, TransitionState = 2 }).Blob()),
            .. InstanceProtocol.EnterWakeZone(id)];
        Assert.Equal(risenCreation, unseen.Read(risenCreation.Length));
    }

    static void CheckOpeningWakeRange(MonsterCatalog.Variety variety)
    {
        var emerges = new MonsterEmerge();
        foreach (var (area, type, aggro, range) in new[]
        {
            (TutorialObjects.Strand, variety, 60, 60),
            (TutorialObjects.Strand, variety, 35, 35), // Shorter aggro caps still apply.
            (TutorialObjects.Strand, variety, 0, 60),
            ("1_1_2", variety, 60, 24),
            (TutorialObjects.Strand, variety with { Id = "Metadata/Monsters/Other" }, 60, 24),
        })
        {
            var conn = new ClientState(new(0, area, 256, 256, 10, 10, null, []), 0, 1) { Instance = new() };
            var m = new SpawnedMonster("wake-boundary", new(100, 100, 1), aggro, 2, 23) { Variety = type };
            Assert.True(MonsterEmerge.AddLocked(conn, 7, m, EmergeKind.Buried, true));
            Assert.Empty(emerges.RiseLocked(conn, (100 - range - 1, 100), 10, out bool watching));
            Assert.True(watching);
            Assert.Single(emerges.RiseLocked(conn, (100 - range, 100), 10, out watching));
            Assert.False(watching);
            Assert.Same(m, conn.Monsters[7]);
            Assert.True(m.BusyUntil > 10);
            Assert.Empty(emerges.RiseLocked(conn, (100, 100), 11, out _)); // Exactly once, without advancing real time.
        }
    }

    static async Task CheckSharedMessageOwnership()
    {
        const uint id = 0x08000055;
        using var sender = new Wire();
        using var first = new Wire(sender.Conn.Instance);
        using var second = new Wire(sender.Conn.Instance);
        var pending = new List<Task>();
        foreach (var peer in new[] { first, second })
        {
            peer.Conn.AwakeMonsters = [id];
            peer.Conn.Outbox = (bytes, log) =>
            {
                var send = Program.Send("fanout-test", peer.Stream, peer.Ciphers, bytes, log);
                pending.Add(send);
                return send;
            };
            sender.Conn.Instance.PublishPlayer(new(peer.Conn.PeerId, 7, new("Peer", 1, 10, 10, 50, 40), 10, 10, 50, peer.Conn));
        }
        byte[] bytes = InstanceEntities.RemoveBuff(id, 7), expected = (byte[])bytes.Clone();
        Game.ToOthers(sender.Conn, id, bytes);
        await Task.WhenAll(pending);
        Assert.Equal(2, pending.Count);
        Assert.Equal(expected, bytes); // Neither peer's cipher may mutate the owner's plaintext.
        Assert.Equal(expected, first.Read(expected.Length));
        Assert.Equal(expected, second.Read(expected.Length));

        // Rewritten combat packets also need a copy per peer, not one copy for the fan-out.
        sender.Conn.Instance.World.MonsterOrigins[id] = new(123, new(10, 10, 23, []), 0, 1, MonsterRarity.Normal);
        sender.Conn.Wire = ("fanout-test", sender.Stream, sender.Ciphers);
        Program.InGame[sender.Conn] = 0;
        foreach (var peer in new[] { first, second })
            peer.Conn.SeenPlayers[sender.Conn.PeerId] = new(sender.Conn.PeerId, 7, new("Owner", 1, 10, 10, 50, 40), 10, 10, 50, sender.Conn);
        try
        {
            await Check(InstanceProtocol.ActionEnded(id), InstanceProtocol.ActionEnded(id),
                packet => Game.BroadcastMonsterMessage(sender.Conn, id, packet));
            sender.Conn.PeerActing = true;
            await Check(InstanceProtocol.ActionEnded(0x1000), InstanceProtocol.ActionEnded(sender.Conn.PeerId),
                packet => Game.MirrorSharedMonsters(sender.Ciphers, packet));
            await Check(InstanceProtocol.ActionHit(0x1000, 12, id, kind: 1),
                InstanceProtocol.ActionHit(sender.Conn.PeerId, 0xffff, id, kind: 1),
                packet => Game.MirrorSharedMonsters(sender.Ciphers, packet));
        }
        finally { Program.InGame.TryRemove(sender.Conn, out _); }

        async Task Check(byte[] original, byte[] mirrored, Action<byte[]> broadcast)
        {
            pending.Clear();
            var unchanged = (byte[])original.Clone();
            broadcast(original);
            await Task.WhenAll(pending);
            Assert.Equal(2, pending.Count);
            Assert.Equal(unchanged, original);
            Assert.Equal(mirrored, first.Read(mirrored.Length));
            Assert.Equal(mirrored, second.Read(mirrored.Length));
        }
    }

    [Fact]
    public void The_exile_picks_its_voice_line_and_closing_it_skips_only_the_running_scene()
    {
        Assert.Equal(((byte)1, (double?)null), TutorialObjects.Dialogue(false, false));
        Assert.Equal(((byte)1, (double?)null), TutorialObjects.Dialogue(false, true));
        Assert.Equal(((byte)2, (double?)22.8), TutorialObjects.Dialogue(true, Untouched().WeaponPromptHeard));
        Assert.Equal(((byte)3, (double?)8.2), TutorialObjects.Dialogue(true, true));

        foreach (var (exile, running, skips) in new[] { (true, true, true), (true, false, false), (false, true, false) })
        {
            var conn = new ClientState(new(0, TutorialObjects.Strand, 32, 32, 10, 10, null, []), 0, 1)
            {
                TalkingTo = 123,
                TutorialSceneRunning = running,
                TutorialDialogueSkipped = new(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            conn.Npcs[123] = new(exile ? Exile : Exile with { Id = "OtherNpc" }, 10, 10);

            TutorialObjects.SkipDialogue(conn);

            Assert.Equal(skips, conn.TutorialDialogueSkipped.Task.IsCompleted);
        }
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        readonly ConnectionCiphers decrypt = new(new byte[64], true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public readonly ClientState Conn;
        public NetworkStream Stream => writer.GetStream();
        public Wire(InstanceState? instance = null)
        {
            Conn = new(new(0, TutorialObjects.Strand, 32, 32, 10, 10, null, []), 0, 1) { Instance = instance ?? new(), PlayerLife = 50 };
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            reader.ReceiveTimeout = 5000;   // Bytes are already sent when a test reads: a missing one fails, not hangs.
        }
        public byte[] Read(int count)
        {
            var bytes = new byte[count];
            reader.GetStream().ReadExactly(bytes);
            decrypt.Receive.Process(bytes);
            return bytes;
        }
        public void Dispose() { Conn.Closed = true; reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
