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

        // Observers get no action to end the stance with: another player's copy is created standing, in the owner's stead.
        using var observer = new Wire();
        observer.Conn.Instance.PublishPlayer(new(0x20001234, 7, player with { WashedUp = true, Rotation = MathF.PI }, 115, 69, 50));
        await Game.SyncInstancePlayers("opening-scene-test", observer.Stream, observer.Ciphers, observer.Conn);
        byte[] created = InstanceEntities.CreateObject(0x20001234, 7, (player with { Skills = [], QuestFlags = [], LeagueQuestFlags = [] }).Blob());
        Assert.Equal(created, observer.Read(created.Length));
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
        public readonly ClientState Conn = new(new(0, TutorialObjects.Strand, 32, 32, 10, 10, null, []), 0, 1) { Instance = new(), PlayerLife = 50 };
        public NetworkStream Stream => writer.GetStream();
        public Wire()
        {
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
