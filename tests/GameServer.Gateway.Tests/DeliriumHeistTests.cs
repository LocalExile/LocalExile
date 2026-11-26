using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using GameServer.Modules.Delirium;
using Xunit;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public class DeliriumHeistTests
{
    [Fact]
    public void Delirium_mirror_ruleset_encoding_progress_and_fog_math()
    {
        AfflictionRuleset.LoadGameData();
        Assert.Equal((byte)102, AfflictionRuleset.EncounterRow);
        Assert.Equal((byte)52, AfflictionRuleset.PreloadGroup);
        Assert.Equal((byte)103, AfflictionRuleset.Row);
        Assert.Equal(Convert.FromHexString("00DF0104A90040000000"), AfflictionRuleset.FogEnvironment(true));
        Assert.Equal(Convert.FromHexString("00DF0104A900C0000000"), AfflictionRuleset.FogEnvironment(false));

        var beach = new AreaStatsRequest("Exile", "Standard", AreaTransitionCatalog.HashOf("MapWorldsBeach")!.Value, null)
        {
            Seed = 314159,
            AreaStats = new Dictionary<uint, int> { [Stat.MapAfflictionLeague] = 1 },
        };
        var terrain = Assert.Single(DeliriumMirrorEncounter.PlanTerrain(beach)!);
        Assert.Equal(2161, terrain.Plugin);
        Assert.Equal(new byte[] { 0 }, terrain.State);
        Assert.True(GameServer.Data.Terrain.Generation.LevelGenerator.SupportsPluginState(terrain.Plugin, terrain.State, null, null));
        Assert.False(GameServer.Data.Terrain.Generation.LevelGenerator.SupportsPluginState(terrain.Plugin, [1], null, null));
        Assert.Null(DeliriumMirrorEncounter.PlanTerrain(beach with
            { AreaStats = new Dictionary<uint, int> { [Stat.MapDisableDeliriumFromChance] = 1 } }));
        Assert.Null(DeliriumMirrorEncounter.PlanTerrain(beach with
            { AreaStats = new Dictionary<uint, int> { [Stat.MapAfflictionLeague] = 1, [Stat.MapDisableLeagues] = 1 } }));

        var islands = new AreaLayout(beach.Area, "MapWorldsBeach", 138, 69, 20, 24,
            (x, y) => x >= 0 && x < 138 && y >= 0 && y < 69 && (x < 30 || x > 75), []);
        var regions = MirrorFogRegions.Build(islands, 20, 24);
        Assert.Equal(2, regions.Length);
        Assert.Equal((20, 24, 0f), (regions[0].OriginX, regions[0].OriginY, regions[0].DistanceOffset));
        Assert.True(regions[0].MaxX < regions[1].MinX && regions[1].DistanceOffset > 0);

        var encounter = new DeliriumMirrorEncounter();
        var player = new Player(islands, 1, beach.Seed) { Instance = new() };
        Assert.Empty(encounter.BeforeEnterMessages(player));
        Assert.Null(encounter.EntryObject(player));
        player.Instance.TerrainPluginStates(() => [terrain]);
        player.Instance.AreaStats(() => beach.AreaStats!);
        var entry = Assert.Single(encounter.BeforeEnterMessages(player));
        var mirror = encounter.For(player);
        Assert.NotEqual(0u, mirror.EntityId);
        var birth = encounter.EntryObject(player)!.Value;
        Assert.Equal((mirror.EntityId, DeliriumMirrorEncounter.MirrorPath), (birth.Id, birth.Type));
        Assert.Equal(2, birth.Object.Blob()[0]);
        Assert.Empty(birth.Object.StateMachine!.Overridden);
        Assert.Equal(AfflictionRuleset.EncounterSnapshot(false, false, -1.8f, -1.8f, float.PositiveInfinity,
            0, false, mirror.MirrorX, mirror.MirrorY, mirror.EntityId, mirror.Regions!, []), entry.Message);
        var peer = new Player(islands, 2, beach.Seed) { Instance = player.Instance };
        Assert.Equal(entry.Message, Assert.Single(encounter.BeforeEnterMessages(peer)).Message);
        Assert.Equal(birth.Id, encounter.EntryObject(peer)!.Value.Id);
        var reloaded = new DeliriumMirrorEncounter();
        Assert.Equal(entry.Message, Assert.Single(reloaded.BeforeEnterMessages(peer)).Message);
        Assert.Equal(birth.Id, reloaded.EntryObject(peer)!.Value.Id);

        var saved = System.Text.Json.Nodes.JsonNode.Parse(player.Instance.ContentState["Delirium.Mirror"])!;
        saved["Stage"] = (int)DeliriumMirrorEncounter.MirrorStage.Active;
        saved["OuterElapsed"] = 27f;
        saved["InnerElapsed"] = 2f;
        saved["InnerWaveSent"] = true; // Maps saved by the previous implementation.
        player.Instance.ContentState["Delirium.Mirror"] = saved.ToJsonString();
        var resumed = Assert.Single(new DeliriumMirrorEncounter().BeforeEnterMessages(peer)).Message;
        Assert.Equal(27f, BinaryPrimitives.ReadSingleLittleEndian(resumed.AsSpan(7)));
        Assert.Equal(float.PositiveInfinity, BinaryPrimitives.ReadSingleLittleEndian(resumed.AsSpan(15)));

        var region = new AfflictionRuleset.FogRegion(0, 0, 200, 200, 50, 60, 0f);
        var tile = new AfflictionRuleset.RewardTile(Type: 0, Current: 12, Max: 18, Count: 2);
        byte[] snap = AfflictionRuleset.EncounterSnapshot(
            active: true, ended: false, outerElapsed: 3.5f, innerElapsed: 0f,
            innerDelay: float.PositiveInfinity, pauseTimer: 0f, paused: false,
            mirrorX: 50, mirrorY: 60, mirrorEntityId: 0x42, [region], [tile])!;

        // S->C 0x02c9 header: u16 BE 0x02c9, u8 row (102), compact-uint payload length (75 = 33 + 28 + 1 + 13).
        Assert.Equal(InstanceRulesets.DataId, BinaryPrimitives.ReadUInt16BigEndian(snap.AsSpan(0, 2)));
        Assert.Equal(102, snap[2]);
        Assert.Equal(75, snap[3]);
        Assert.Equal(0, snap[4]); // mode 0
        Assert.Equal(1, snap[5]); // active
        Assert.Equal(0, snap[6]); // ended
        Assert.Equal(3.5f, BinaryPrimitives.ReadSingleLittleEndian(snap.AsSpan(7, 4)));
        Assert.Equal(float.PositiveInfinity, BinaryPrimitives.ReadSingleLittleEndian(snap.AsSpan(15, 4)));
        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(snap.AsSpan(24, 4)));
        Assert.Equal(60, BinaryPrimitives.ReadInt32LittleEndian(snap.AsSpan(28, 4)));
        Assert.Equal(0x42u, BinaryPrimitives.ReadUInt32LittleEndian(snap.AsSpan(32, 4)));
        Assert.Equal(1, snap[36]); // 1 region
        Assert.Equal(1, snap[65]); // 1 reward tile
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(snap.AsSpan(75, 4))); // count

        byte[] pause = AfflictionRuleset.EncounterPause(4.5f, paused: true)!;
        Assert.Equal(102, pause[2]);
        Assert.Equal(6, pause[3]);
        Assert.Equal(2, pause[4]); // mode 2
        Assert.Equal(4.5f, BinaryPrimitives.ReadSingleLittleEndian(pause.AsSpan(5, 4)));
        Assert.Equal(1, pause[9]);

        // MirrorState kill progression, reward tier cycling, tile unlock, and fog containment.
        var state = new DeliriumMirrorEncounter.MirrorState
        {
            Stage = DeliriumMirrorEncounter.MirrorStage.Active,
            MirrorX = 50,
            MirrorY = 60,
            OuterElapsed = 5f,
            Wave = MirrorFogWave.For(68, new Dictionary<uint, int>()),
            DepthPercent = 40,
            Rewards = [0],
        };
        Assert.True(state.IsCellInFog(70, 60));
        Assert.False(state.IsCellInFog(800, 800));
        Assert.Equal(679f, state.Wave.OuterRadius(7), 3);
        Assert.Equal(1559f, state.Wave.OuterRadius(27), 3);
        Assert.Equal(16.31f, state.Wave.InnerRadius(27), 3);
        state.OuterElapsed = 27;
        Assert.True(state.IsCellInFog(400, 400));
        Assert.False(state.IsCellInFog(55, 60));
        var delayed = MirrorFogWave.For(68, new Dictionary<uint, int>
            { [Stat.MapAfflictionSecondaryWaveDelayMsPlus] = 2000, [Stat.MapAfflictionSecondaryWaveAccelerationPlusPct] = 100 });
        Assert.Equal(14.5f, delayed.InnerRadius(27), 3);
        Assert.Equal(0f, MirrorFogWave.For(68, new Dictionary<uint, int> { [Stat.MapDeliriumFogNeverDissipates] = 1 }).InnerRadius(120));
        for (int i = 0; i < 20; i++) state.RecordKill(MonsterRarity.Rare, progressBonusPct: 0);
        Assert.True(state.RewardCounts[0] >= 3);
        Assert.True(state.Rewards.Length >= 2);
        Assert.True(state.Splinters > 0);
    }

    [Fact]
    public async Task Heist_module_registers_planning_packets_and_cleans_up_on_unload()
    {
        string moduleDir = Path.GetFullPath("Modules/Core/Content/Heist");
        Assert.True(File.Exists(Path.Combine(moduleDir, "bin", "heist.dll")), $"Expected built Heist module in {moduleDir}");

        // Load Heist (+ its Game dependency) via ModHost and verify its packet subscriptions register cleanly.
        using (var host = new ModHost("Modules", _ => { }, modules: true) { Wanted = () => new HashSet<string>(["Game", "Heist"]) })
        {
            host.Start(watch: false);
            Assert.True(host.IsLoaded("Heist"));
            Assert.True(GameEvents.WantsPacket(InstanceHeist.SignContractId));
            Assert.True(GameEvents.WantsPacket(InstanceHeist.RevealBlueprintId));
            Assert.True(GameEvents.WantsPacket(InstanceHeist.ConfirmBlueprintId));
            Assert.True(GameEvents.WantsPacket(InstanceHeist.SetBlueprintCrewId));

            using var fixture = new TriggeredSummonTests.Fixture();
            var player = fixture.Player;
            player.Use(player.Layout with { Id = "HeistHub" });
            player.LeagueModule = new(new GameServer.Core.Accounts.League("heist-review", "Standard", false, false), new HashSet<string>(["Game", "Heist"]));
            var items = Game.ItemsOf(player);
            const byte planningRow = 55;
            var blueprint = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Heist/HeistBlueprintMines")!) with
            {
                HeistBlueprint = new(1, 68, 0, false, [new HeistWing(true, [], [], [])])
            };
            var planned = items.Add(planningRow, 0, 0, blueprint);
            var markers = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Heist/HeistCoin")!) with { StackSize = 2000 };
            var coins = items.Add(PlayerInventories.MainInventory, 1, 0, markers);
            Assert.True(await GameEvents.AnyPacket(player, InstanceHeist.ConfirmBlueprintId, [0x02, 0x7e]));
            Assert.Equal(planned, Assert.Single(items.Items(planningRow)));
            Assert.Equal(coins, Assert.Single(items.Items(PlayerInventories.MainInventory), i => i.Id == coins.Id));
        }
        Assert.False(GameEvents.WantsPacket(InstanceHeist.SignContractId));
        Assert.False(GameEvents.WantsPacket(InstanceHeist.RevealBlueprintId));
        Assert.False(GameEvents.WantsPacket(InstanceHeist.ConfirmBlueprintId));
        Assert.False(GameEvents.WantsPacket(InstanceHeist.SetBlueprintCrewId));

        // Verify client.db Heist tables match the protocol and Stat.* assumptions.
        var npcs = ClientTables.Table("HeistNPCs").Take(InstanceHeist.RogueCount).ToArray();
        Assert.Equal(InstanceHeist.RogueCount, npcs.Length);
        Assert.All(npcs, r => Assert.False(string.IsNullOrEmpty(r.Text("AOFile"))));
        Assert.Equal(3, ClientTables.Table("HeistRevealingNPCs").Count);
        var jobs = ClientTables.Table("HeistJobs");
        Assert.Equal(9, jobs.Count);
        for (int j = 0; j < 9; j++)
            Assert.Equal((int)(Stat.HeistJobLockpickingLevelPlus + j) - 1, jobs[j].IntOrNull("Level_StatsKey"));
    }

    [Fact]
    public async Task Delirium_mirror_coop_broadcasts_encrypt_independently_per_peer_and_keep_last_tiles_plaintext()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        using var context = new MirrorTestContext();
        AfflictionRuleset.LoadGameData();
        var encounter = new DeliriumMirrorEncounter();
        encounter.Attach(context);

        var beachHash = AreaTransitionCatalog.HashOf("MapWorldsBeach")!.Value;
        var beachStats = new AreaStatsRequest("Exile", "Standard", beachHash, null)
        {
            Seed = 314159,
            AreaStats = new Dictionary<uint, int> { [Stat.MapAfflictionLeague] = 1 },
        };
        var terrain = Assert.Single(DeliriumMirrorEncounter.PlanTerrain(beachStats)!);
        var layout = new AreaLayout(beachHash, "MapWorldsBeach", 138, 69, 20, 24, (_, _) => true, []);
        var instance = new InstanceState();
        instance.TerrainPluginStates(() => [terrain]);
        instance.AreaStats(() => beachStats.AreaStats!);
        var league = new LeagueRuntime(new League("delirium-coop", "Standard", false, false),
            new HashSet<string>(["Game", "Delirium"]));

        static async Task<(Player Player, TcpListener Listener, TcpClient Writer, TcpClient Reader, MemoryStream Capture, Task Drain)>
            OpenPeer(uint id, string name, byte keyFill, AreaLayout area, InstanceState inst, LeagueRuntime lr)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var accept = listener.AcceptTcpClientAsync();
            var writer = new TcpClient();
            await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var reader = await accept;
            var capture = new MemoryStream();
            var drain = reader.GetStream().CopyToAsync(capture);
            var key = Enumerable.Repeat(keyFill, 64).ToArray();
            var player = new Player(area, id, 314159)
            {
                Instance = inst,
                PlayerLife = 1000,
                LeagueModule = lr,
                Character = { Name = name, Experience = CharacterStats.ExperienceFor(90) },
                Pools = new(90, 1000, 1000, 2, 0),
                Connection =
                {
                    LockstepClient = false,
                    PlayerSent = true,
                    Session = new(name, writer.GetStream(), new ConnectionCiphers(key, false)),
                },
            };
            player.Connection.Outbox = (bytes, log) => Program.Send(player, bytes, log);
            player.Clock.Start();
            inst.AddPlayer(player);
            Program.InGame[player] = 0;
            return (player, listener, writer, reader, capture, drain);
        }

        var peer1 = await OpenPeer(1, "DeliriumCoop1-" + Guid.NewGuid().ToString("N"), 0x11, layout, instance, league);
        var peer2 = await OpenPeer(2, "DeliriumCoop2-" + Guid.NewGuid().ToString("N"), 0x22, layout, instance, league);
        try
        {
            Assert.NotEmpty(encounter.BeforeEnterMessages(peer1.Player));
            Assert.NotEmpty(encounter.BeforeEnterMessages(peer2.Player));
            Assert.Equal(["IsMapOwner"], encounter.QuestFlags(peer1.Player));
            Assert.Contains(2020, Game.PlayerQuestFlags(peer1.Player));
            var fragmentRewards = DeliriumModule.RollMirrorRewards(peer1.Player, 84,
                Enumerable.Repeat(13, 80).ToArray(), Enumerable.Repeat(1, 80).ToArray(), 0);
            Assert.NotEmpty(fragmentRewards);
            Assert.All(fragmentRewards, item => Assert.True(Program.Items.Stash.Holds(StashRules.Fragment, item.TypeHash)));
            var state = encounter.For(peer1.Player);

            // Walk peer1 onto the mirror so OnTick activates it and broadcasts fog, ruleset snapshot, and animation.
            peer1.Player.Walk.Place(state.MirrorX, state.MirrorY, Game.AreaTime(peer1.Player));
            peer2.Player.Walk.Place(state.MirrorX, state.MirrorY, Game.AreaTime(peer2.Player));
            await encounter.OnTick(peer1.Player, 0.1);
            Assert.Equal(DeliriumMirrorEncounter.MirrorStage.Active, state.Stage);

            // Dirty reward tiles past 3 and push OuterElapsed past trailing clear so both peers leave fog and receive 0x02c9 + 0x0163.
            for (int i = 0; i < 80; i++) state.RecordKill(MonsterRarity.Unique, progressBonusPct: 0);
            Assert.True(state.Rewards.Length >= 5);
            state.OuterElapsed = 200f;
            await encounter.OnTick(peer1.Player, 0.25);

            // End encounter via skip request so EndEncounter broadcasts hide-timer, fog-out, and ended snapshot.
            await encounter.OnEndRequested(peer1.Player);
            Assert.Equal(DeliriumMirrorEncounter.MirrorStage.Ended, state.Stage);

            peer1.Player.Closed = true;
            peer2.Player.Closed = true;
            peer1.Writer.Dispose();
            peer2.Writer.Dispose();
            await peer1.Drain;
            await peer2.Drain;

            byte[] wire1 = peer1.Capture.ToArray();
            byte[] wire2 = peer2.Capture.ToArray();
            new ConnectionCiphers(Enumerable.Repeat((byte)0x11, 64).ToArray(), true).Receive.Process(wire1);
            new ConnectionCiphers(Enumerable.Repeat((byte)0x22, 64).ToArray(), true).Receive.Process(wire2);

            Assert.NotNull(state.LastTiles);
            Assert.Equal(InstanceRulesets.DataId, BinaryPrimitives.ReadUInt16BigEndian(state.LastTiles.AsSpan(0, 2)));
            Assert.Equal(AfflictionRuleset.EncounterRow!.Value, state.LastTiles[2]);

            byte[] fogIn = AfflictionRuleset.FogEnvironment(true)!;
            byte[] fogOut = AfflictionRuleset.FogEnvironment(false)!;
            byte[] countdown = InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Affliction, 5);
            byte[] hideTimer = InstanceTimers.HideMissionTimer(InstanceTimers.MissionTimerType.Affliction);
            foreach (byte[] wire in new[] { wire1, wire2 })
            {
                Assert.True(wire.AsSpan().IndexOf(fogIn) >= 0);
                Assert.True(wire.AsSpan().IndexOf(state.LastTiles) >= 0);
                Assert.True(wire.AsSpan().IndexOf(countdown) >= 0);
                Assert.True(wire.AsSpan().IndexOf(hideTimer) >= 0);
                Assert.True(wire.AsSpan().IndexOf(fogOut) >= 0);
            }
        }
        finally
        {
            Program.InGame.TryRemove(peer1.Player, out _);
            Program.InGame.TryRemove(peer2.Player, out _);
            peer1.Player.Closed = true;
            peer2.Player.Closed = true;
            peer1.Writer.Dispose();
            peer2.Writer.Dispose();
            peer1.Reader.Dispose();
            peer2.Reader.Dispose();
            peer1.Listener.Stop();
            peer2.Listener.Stop();
        }
    }

    sealed class MirrorTestContext : IModuleContext, IDisposable
    {
        readonly ModuleLifetime lifetime = new("Delirium");
        public ModManifest Manifest => new("Delirium", "1.0.0", ModKind.Feature, ["Game"], "delirium.dll");
        public IModuleLifetime Lifetime => lifetime;
        public string Folder => "";
        public void Log(string message) { }
        public T Settings<T>() => throw new NotSupportedException();
        public void SaveSettings<T>(T settings) => throw new NotSupportedException();
        public void ProvideLeagueRules(Func<LeagueRules> rules) { }
        public void Dispose() => lifetime.Stop(e => throw e);
    }
}
