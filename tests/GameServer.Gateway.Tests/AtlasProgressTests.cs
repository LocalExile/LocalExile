using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using GameServer.Modules.Atlas;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

/// <summary>The Atlas's completed maps (21-hideouts.md §4): the nodes from AtlasNode.dat and the S→C 0x1a3 / 0x1a4 messages.</summary>
public sealed class AtlasProgressTests
{
    static AtlasProgress.Node Chateau => AtlasProgress.Find("Chateau")!;

    static MapDevice.Run MapRun(int tier, ItemRarity rarity, bool corrupted = false) =>
        new("Tester", new MapDevice.MapArea(Chateau.Hash16, Chateau.Id, Chateau.Name, tier),
            new MapMods(new Dictionary<uint, int>()) { MapRarity = rarity, Corrupted = corrupted });

    [Fact]
    public void A_regular_completion_leaves_the_bonus_available_and_a_second_bonus_cannot_award_twice()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var data = Program.WorldOf(player).CharacterData;

        // T1-5: Magic+, T6-10: Rare+, T11-16: Rare+Corrupted, Unique: always, Tier 0 / T17: false.
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(1, ItemRarity.Normal)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(1, ItemRarity.Magic)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(5, ItemRarity.Normal)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(5, ItemRarity.Magic)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(5, ItemRarity.Rare)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(6, ItemRarity.Magic)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(6, ItemRarity.Rare)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(10, ItemRarity.Magic)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(10, ItemRarity.Rare)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(11, ItemRarity.Rare)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(11, ItemRarity.Magic, corrupted: true)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(11, ItemRarity.Rare, corrupted: true)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(16, ItemRarity.Rare)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(16, ItemRarity.Rare, corrupted: true)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(1, ItemRarity.Unique)));
        Assert.True(AtlasProgress.MeetsBonusObjective(MapRun(16, ItemRarity.Unique)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(0, ItemRarity.Rare, corrupted: true)));
        Assert.False(AtlasProgress.MeetsBonusObjective(MapRun(17, ItemRarity.Rare, corrupted: true)));

        Assert.True(AtlasProgress.Complete(data, [Chateau]));
        Assert.Contains(Chateau.Id, AtlasProgress.Completed(data));
        Assert.Empty(AtlasProgress.BonusCompleted(data));

        Assert.True(AtlasProgress.CompleteBonus(data, [Chateau]));
        Assert.False(AtlasProgress.CompleteBonus(data, [Chateau]));
        Assert.Single(AtlasProgress.BonusCompleted(data));
    }

    [Fact]
    public void Old_completion_credit_is_preserved_and_a_new_qualified_map_earns_the_next_point()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var data = Program.WorldOf(player).CharacterData;
        var next = AtlasProgress.Find("Alleyways")!;
        data.Change("completed", _ => Chateau.Id);
        player.Character.AtlasPassivePoints = 1;

        Assert.Contains(Chateau.Id, AtlasProgress.BonusCompleted(data));
        Assert.True(AtlasProgress.Complete(data, [next]));
        Assert.Single(AtlasProgress.BonusCompleted(data));
        Assert.False(AtlasProgress.CompleteBonus(data, [Chateau]));
        Assert.True(AtlasProgress.CompleteBonus(data, [next]));
        Assert.Equal(2, AtlasProgress.BonusCompleted(data).Count);
    }

    [Fact]
    public void MessagesCarryTheNodesRowMasksAndHash()
    {
        var alleyways = AtlasProgress.Find("Alleyways")!;
        byte[] split = AtlasProgress.FullMessage(
            new HashSet<string> { Chateau.Id, alleyways.Id },
            new HashSet<string> { Chateau.Id });
        Assert.Equal(7 + 2 * 22, split.Length);
        Assert.Equal(new byte[] { 0x01, 0xa3, 0, 0, 0, 0x03, 0 }, split[..7]);
        // First mask (+0xd030) has both completed maps; second mask (+0xd070) has only bonus-completed Chateau.
        Assert.NotEqual(0, split[7 + Chateau.Row / 8] & (1 << (Chateau.Row & 7)));
        Assert.NotEqual(0, split[7 + alleyways.Row / 8] & (1 << (alleyways.Row & 7)));
        Assert.NotEqual(0, split[7 + 22 + Chateau.Row / 8] & (1 << (Chateau.Row & 7)));
        Assert.Equal(0, split[7 + 22 + alleyways.Row / 8] & (1 << (alleyways.Row & 7)));
        Assert.All(AtlasProgress.FullMessage(new HashSet<string>(), new HashSet<string>())[7..], b => Assert.Equal(0, b));
        Assert.Equal(new byte[] { 0x01, 0xa4, 0x9a, 0x23, 0, 0, 0x02 }, AtlasProgress.NodeMessage(Chateau));
        Assert.Equal(new byte[] { 0x01, 0xa4, 0x9a, 0x23, 0, 0, 0x00 }, AtlasProgress.NodeMessage(Chateau, bonus: false));
        byte[] empty = AtlasProgress.EmptyMessage();
        Assert.Equal(split.Length, empty.Length);
        Assert.Equal(split[..7], empty[..7]);
        Assert.All(empty[7..], b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Minion_kill_of_map_boss_awards_atlas_completion_and_respects_phased_boss_order()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        using var moduleContext = new AtlasTestContext();
        var atlas = new AtlasModule();
        try
        {
            atlas.Load(moduleContext);
            var league = new LeagueRuntime(new League("atlas-minion", "Standard", false, false), new HashSet<string>(["Game", "Atlas"]));
            var data = Program.WorldOf(fixture.Player).CharacterData;

            async Task<(AtlasProgress.Node Node, byte[] Wire)> KillBossWithMinion(string areaId, string nodeName, string varietyId)
            {
                var node = AtlasProgress.Find(nodeName)!;
                var layout = new AreaLayout(node.AreaHashes.First(), areaId, 300, 300, 50, 50, (_, _) => true, []);
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                using var writer = new TcpClient();
                var accept = listener.AcceptTcpClientAsync();
                await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                using var reader = await accept;
                using var capture = new MemoryStream();
                var drain = reader.GetStream().CopyToAsync(capture);
                var player = new Player(layout, 1, 1)
                {
                    Instance = new(),
                    PlayerLife = 1000,
                    LeagueModule = league,
                    Character = { Name = fixture.Player.Character.Name, Experience = CharacterStats.ExperienceFor(90) },
                    Pools = new(90, 1000, 1000, 2, 0),
                    Connection =
                    {
                        PlayerSent = true,
                        Session = new("test", writer.GetStream(), new ConnectionCiphers(new byte[64], false)),
                    },
                };
                try
                {
                    player.Clock.Start();
                    var mapArea = new MapDevice.MapArea(layout.Hash, areaId, node.Name, 16) { Level = 83 };
                    var mods = new MapMods(new Dictionary<uint, int>()) { MapRarity = ItemRarity.Rare, Corrupted = true };
                    player.Instance.OpenedWith(new MapDevice.Run(player.Character.Name!, mapArea, mods));

                    var variety = Game.Monsters.Find(varietyId)!;
                    var boss = new SpawnedMonster(variety.Name, new(layout.SpawnX, layout.SpawnY, 1), 1000, 5, 100)
                    {
                        Hostile = true,
                        Variety = variety,
                        Level = 83,
                        Rarity = MonsterRarity.Unique,
                        ObjectId = 0x8000001,
                    };
                    player.Area.Monsters[boss.ObjectId] = boss;
                    Game.CommitAcceptedKillLocked(player, boss.ObjectId, boss, 1.0, playerKill: false);
                    await Game.PublishMinionOutcomes(player);
                }
                finally
                {
                    player.Closed = true;
                    writer.Dispose();
                    await drain;
                }
                byte[] wire = capture.ToArray();
                new ConnectionCiphers(new byte[64], true).Receive.Process(wire);
                return (node, wire);
            }

            var (tomb, tombWire) = await KillBossWithMinion(
                "MapWorldsArachnidTomb", "Arachnid Tomb", "Metadata/Monsters/Spiders/MapSpiderBossSinsSimplified");
            Assert.Contains(tomb.Id, AtlasProgress.Completed(data));
            Assert.Contains(tomb.Id, AtlasProgress.BonusCompleted(data));
            Assert.True(tombWire.AsSpan().IndexOf(AtlasProgress.NodeMessage(tomb, bonus: true)) >= 0);

            var (desertPhase1, desertPhase1Wire) = await KillBossWithMinion(
                "MapWorldsDesertSpring", "Desert Spring", "Metadata/Monsters/Shakari/ShakariMap");
            Assert.DoesNotContain(desertPhase1.Id, AtlasProgress.Completed(data));
            Assert.True(desertPhase1Wire.AsSpan().IndexOf(AtlasProgress.NodeMessage(desertPhase1, bonus: true)) < 0);

            var (desertPhase2, desertPhase2Wire) = await KillBossWithMinion(
                "MapWorldsDesertSpring", "Desert Spring", "Metadata/Monsters/Shakari/Shakari2Map");
            Assert.Contains(desertPhase2.Id, AtlasProgress.Completed(data));
            Assert.Contains(desertPhase2.Id, AtlasProgress.BonusCompleted(data));
            Assert.True(desertPhase2Wire.AsSpan().IndexOf(AtlasProgress.NodeMessage(desertPhase2, bonus: true)) >= 0);
        }
        finally
        {
            for (int rank = 0; rank <= 3; rank++) GameEventOwners.Release("Atlas", rank);
        }
    }

    sealed class AtlasTestContext : IModuleContext, IDisposable
    {
        readonly ModuleLifetime lifetime = new("Atlas");
        public ModManifest Manifest => new("Atlas", "1.0.0", ModKind.Feature, ["Game"], "atlas.dll");
        public IModuleLifetime Lifetime => lifetime;
        public string Folder => "";
        public void Log(string message) { }
        public T Settings<T>() => throw new NotSupportedException();
        public void SaveSettings<T>(T settings) => throw new NotSupportedException();
        public void ProvideLeagueRules(Func<LeagueRules> rules) { }
        public void Dispose() => lifetime.Stop(e => throw e);
    }
}
