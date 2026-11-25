using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using GameServer.Modules.Blight;

namespace GameServer.Gateway.Tests;

/// <summary>
/// A Blight defence is the instance's: the pump is created once for the area, a party member's click starts it for both clients, the
/// build phase lays the lanes and the foundations, a tower the client asks for is paid from the shared resource and raised, the lanes
/// send their enemies, and the defence ends when they are all dealt with. A client that enters again is not sent the objects a second
/// time (the area's records give them), and a reloaded module carries on from the instance's saved copy. Nothing waits on real time:
/// the area time is passed to the tick.
/// </summary>
public sealed class BlightDefenceTests
{
    [Fact]
    public async Task A_defence_starts_for_the_whole_instance_and_a_tower_is_paid_from_its_resource()
    {
        await using var area = await BlightArea.Open();
        var encounter = new BlightEncounter();
        var d = area.Defend(encounter);
        await encounter.Enter(area.A);
        // The area keeps the pump's records for whoever joins later.
        Assert.True(area.A.Area.Objects.Has(d.Pump));
        await encounter.Start(area.B, d);
        Assert.Equal(Phase.Building, d.Phase);
        Assert.NotNull(d.Plan);
        Assert.NotEmpty(d.Plan!.Lanes);
        Assert.NotEmpty(d.Spots);
        Assert.Equal(BlightEncounter.StartResource, d.Resource);

        // The client's choice of the first Chilling tower on a foundation (BlightTowers row 1), and a message too short to read.
        var spot = d.Spots.Values.First();
        uint foundation = spot.Id;
        byte[] request = [0x02, 0x65, .. BitConverter.GetBytes(foundation).Reverse(), 0, 0, 0, 1];
        Assert.True(await encounter.Build(area.A, request[..6]));
        Assert.Equal(0, spot.Row);
        Assert.True(await encounter.Build(area.A, request));
        Assert.Equal(1, spot.Row);
        Assert.NotEqual(foundation, spot.Id);
        Assert.Equal(BlightEncounter.StartResource - (uint)BlightTables.Towers[1].Cost, d.Resource);
        Assert.False(d.Spots.ContainsKey(foundation));

        // A client that comes back is told the resource and sent no create again: each client was sent the pump once, the state
        // each change made once, and its own bytes (the writer encrypts a buffer in place).
        await encounter.Enter(area.B);
        byte[] a = await area.WireA.Finish(), b = await area.WireB.Finish();
        byte[] pump = BitConverter.GetBytes(InstanceEntities.ObjectTypeHash(BlightEncounter.PumpObject)).Reverse().ToArray();
        byte[] paid = InstanceBlight.Resource(d.Resource);
        Assert.Equal(1, BlightArea.Count(a, pump));
        Assert.Equal(1, BlightArea.Count(b, pump));
        Assert.True(BlightArea.Find(a, paid) >= 0 && BlightArea.Find(b, paid) >= 0, "both clients are told the new resource");
        Assert.True(area.A.Area.Objects.Has(spot.Id) && !area.A.Area.Objects.Has(foundation), "the area's records hold the tower, not the foundation it replaced");
        Assert.Equal("BlightedMapTree1", BlightLayout.Pick(BlightTables.Topologies, 100, new Random(1), blighted: true)?.Id);
        Assert.NotEqual("BlightedMapTree1", BlightLayout.Pick(BlightTables.Topologies, 100, new Random(1))?.Id);
    }

    [Fact]
    public async Task The_lanes_send_their_enemies_and_the_defence_ends_when_they_are_dealt_with()
    {
        await using var area = await BlightArea.Open();
        var encounter = new BlightEncounter();
        var d = area.Defend(encounter);
        await encounter.Enter(area.A);
        await encounter.Start(area.A, d);
        d.MonstersPerLane = 2;
        double now = d.StartedAt + 100;
        for (int i = 0; i < 4; i++) await encounter.Tick(area.A, now += 100);
        Assert.Equal(Phase.Running, d.Phase);
        Assert.Equal(d.Plan!.Lanes.Count * 2, d.Monsters.Count);
        Assert.All(d.Monsters.Keys, id => Assert.Equal(d.Level, area.A.Area.Monsters[id].Level));

        // The module is reloaded: the instance's saved copy gives the same defence, with the same lanes, spots and enemies.
        encounter.Runs.Clear();
        await encounter.Tick(area.A, now += 1);
        Assert.True(encounter.Runs.TryGetValue(area.Instance, out var run));
        var kept = Assert.Single(run.Defences);
        Assert.NotSame(d, kept);
        Assert.Equal((d.Pump, d.Phase, d.Resource, d.Spots.Count), (kept.Pump, kept.Phase, kept.Resource, kept.Spots.Count));
        Assert.Equal(d.Plan.Lanes.Select(l => l.End), kept.Plan!.Lanes.Select(l => l.End));
        Assert.Equal(d.Monsters.Keys.Order(), kept.Monsters.Keys.Order());
        d = kept;

        // One reaches the pump: it costs durability and leaves the instance; the rest are killed.
        uint first = d.Monsters.Keys.First();
        area.A.Area.Monsters[first].Walk.Teleport(d.Cell.X, d.Cell.Y, now);
        await encounter.Tick(area.A, now += 1);
        Assert.Equal(d.MaxDurability - 1, d.Durability);
        Assert.False(area.A.Area.Monsters.ContainsKey(first));
        foreach (uint id in d.Monsters.Keys.Where(id => area.A.Area.Monsters.ContainsKey(id)).ToArray())
            area.A.Area.Monsters[id].Life = 0;
        await encounter.Tick(area.A, now += 1);
        Assert.Equal(Phase.Won, d.Phase);
        Assert.Equal(1, d.PumpStates[5]);
    }
}

/// <summary>Two players in one map instance (a flat, walkable 600 x 600 layout), each with a wire whose decrypted bytes the test reads.</summary>
sealed class BlightArea : IAsyncDisposable
{
    readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
    readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
    public readonly InstanceState Instance = new();
    public Player A = null!, B = null!;
    public Wire WireA = null!, WireB = null!;
    public byte Level;

    public static async Task<BlightArea> Open()
    {
        BlightModule.LoadData();
        var area = new BlightArea();
        Game.CombatLifetime = new ModuleLifetime("Game");
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var layout = new AreaLayout(53973, "MapWorldsReef", 600, 600, 300, 300, (_, _) => true, []);
        area.Level = (byte)ServerLevels.AreaLevel(layout.Id);
        (area.A, area.WireA) = await Wire.Open(area.Instance, layout, "BlightA");
        (area.B, area.WireB) = await Wire.Open(area.Instance, layout, "BlightB");
        area.Instance.AddPlayer(area.A);
        area.Instance.AddPlayer(area.B);
        Program.InGame[area.A] = 0;
        Program.InGame[area.B] = 0;
        return area;
    }

    /// <summary>The instance's defence, as the level's pump would make it (the layout is not a generated one).</summary>
    public Defence Defend(BlightEncounter encounter) => encounter.Place(A, [(300, 300, 0f)]).Defences[0];

    public static int Find(byte[] bytes, byte[] part) => bytes.AsSpan().IndexOf(part);

    public static int Count(byte[] bytes, byte[] part)
    {
        int n = 0, from = 0;
        while (bytes.AsSpan(from).IndexOf(part) is var at && at >= 0) { n++; from += at + part.Length; }
        return n;
    }

    public async ValueTask DisposeAsync()
    {
        Program.InGame.TryRemove(A, out _);
        Program.InGame.TryRemove(B, out _);
        await WireA.DisposeAsync();
        await WireB.DisposeAsync();
        Game.CombatLifetime = previousLifetime;
        Program.SetGameLogic(previousLogic!);
    }

    public sealed class Wire(TcpClient writer, TcpClient reader, Player player, MemoryStream captured, Task drain) : IAsyncDisposable
    {
        public static async Task<(Player, Wire)> Open(InstanceState instance, AreaLayout layout, string name)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            var writer = new TcpClient();
            await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var reader = await accepting;
            var captured = new MemoryStream();
            var client = new ConnectionCiphers(new byte[64], true);
            var player = new Player(layout, 1, 3316637604u)
            {
                Character = { Name = name }, Instance = instance, PlayerLife = 1000,
                Connection = { LockstepClient = false, PlayerSent = true, Session = new(name, writer.GetStream(), new ConnectionCiphers(new byte[64], false)) },
            };
            var drain = Task.Run(async () =>
            {
                byte[] buffer = new byte[4096];
                try
                {
                    int n;
                    while ((n = await reader.GetStream().ReadAsync(buffer)) > 0)
                    {
                        client.Receive.Process(buffer.AsSpan(0, n));
                        lock (captured) captured.Write(buffer, 0, n);
                    }
                }
                catch (IOException) { }
            });
            return (player, new(writer, reader, player, captured, drain));
        }

        /// <summary>Everything sent so far, once the server side is closed.</summary>
        public async Task<byte[]> Finish()
        {
            player.Closed = true;
            try { writer.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
            await drain;
            lock (captured) return captured.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            player.Closed = true;
            writer.Dispose();
            await drain;
            reader.Dispose();
        }
    }
}
