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
            var player = new Player(new(0, area, 256, 256, 10, 10, null, []), 0, 1) { Instance = new() };
            var m = new SpawnedMonster("wake-boundary", new(100, 100, 1), aggro, 2, 23) { Variety = type };
            Assert.True(MonsterEmerge.AddLocked(player, 7, m, EmergeKind.Buried, true));
            Assert.Empty(emerges.RiseLocked(player, (100 - range - 1, 100), 10, out bool watching));
            Assert.True(watching);
            Assert.Single(emerges.RiseLocked(player, (100 - range, 100), 10, out watching));
            Assert.False(watching);
            Assert.Same(m, player.Area.Monsters[7]);
            Assert.True(m.BusyUntil > 10);
            Assert.Empty(emerges.RiseLocked(player, (100, 100), 11, out _)); // Exactly once, without advancing real time.
        }
    }

    static void CheckOpeningPopulationBudget()
    {
        var strand = new PackCatalog().For(TutorialObjects.Strand)!;
        bool Walk(int x, int y) => x is > 5 and < 595 && y is > 5 and < 595;
        List<AreaPopulation.Spawn> Plan(PackCatalog.AreaPacks area, int seed) => AreaPopulation.Plan(area,
            600, 600, Walk, (50, 50), [], [new("mapboss", 500, 500)], seed, _ => true, []);
        foreach (int seed in new[] { 1, 2, 3, 4 })
        {
            var spawns = Plan(strand, seed);
            Assert.Equal(AreaPopulation.StrandMonsterTarget - 1, spawns.Count); // Tutorial zombie is spawned separately.
            Assert.Single(spawns, s => s.Rarity == MonsterRarity.Unique && strand.Bosses.Contains(s.Variety));
            Assert.Equal(spawns, Plan(strand, seed));
            // Identical pack data outside the opening area retains the ordinary density and rarity floor.
            var ordinary = Plan(strand with { AreaId = "population-regression" }, seed);
            Assert.True(ordinary.Count > spawns.Count);
            Assert.True(ordinary.Count(s => s.Rarity == MonsterRarity.Rare) >= AreaPopulation.MinRares);
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
            var player = new Player(new(0, TutorialObjects.Strand, 32, 32, 10, 10, null, []), 0, 1)
            {
                TalkingTo = 123,
                TutorialSceneRunning = running,
                TutorialDialogueSkipped = new(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            player.Npcs[123] = new(exile ? Exile : Exile with { Id = "OtherNpc" }, 10, 10);

            TutorialObjects.SkipDialogue(player);

            Assert.Equal(skips, player.TutorialDialogueSkipped.Task.IsCompleted);
        }
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        readonly ConnectionCiphers decrypt = new(new byte[64], true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public readonly Player Player;
        public NetworkStream Stream => writer.GetStream();
        public Wire(InstanceState? instance = null)
        {
            Player = new(new(0, TutorialObjects.Strand, 32, 32, 10, 10, null, []), 0, 1) { Connection = { LockstepClient = false }, Instance = instance ?? new(), PlayerLife = 50 };
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            reader.ReceiveTimeout = 5000;   // Bytes are already sent when a test reads: a missing one fails, not hangs.
            Player.Connection.Session = new("opening-scene-test", Stream, Ciphers);
        }
        public byte[] Read(int count)
        {
            var bytes = new byte[count];
            reader.GetStream().ReadExactly(bytes);
            decrypt.Receive.Process(bytes);
            return bytes;
        }
        public void Dispose() { Player.Closed = true; reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
