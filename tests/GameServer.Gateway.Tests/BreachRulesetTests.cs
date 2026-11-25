using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using GameServer.Modules.Breach;

namespace GameServer.Gateway.Tests;

/// <summary>
/// A Breach is the instance's: rolled and placed once when its population is placed, with the ruleset row (0x1f) before
/// the Initiator and its idle 0x2c9 line; a party member who enters later, and the module after a reload, find the same
/// Breach, and the entering client is sent the row and the line again. The lines are computed: they match an official
/// Breach's measured lines (the constant ones exactly, the enraged one within the fit), and the ramp follows its formulas.
/// </summary>
public sealed class BreachRulesetTests
{
    [Fact]
    public async Task A_breach_is_the_instances_and_is_shown_again_to_whoever_enters()
    {
        await using var area = await BreachArea.Open();
        var encounter = new BreachEncounter();
        Assert.Null(await encounter.Populate(new AreaPopulated(area.A, area.Level, 7, [])));
        var breach = Assert.Single(encounter.Of(area.Instance)!.Breaches);
        Assert.NotNull(await encounter.Populate(new AreaPopulated(area.A, area.Level, 7, [])));
        Assert.Single(encounter.Of(area.Instance)!.Breaches);
        // The Initiator is an area object: a client that joins later is given it with the area's objects.
        Assert.True(area.A.Area.Objects.Has(breach.Initiator));

        // An official Breach's measured lines (index 0 at (1478, 500); row 0xa4, length 0x2c, then the body): idle, the
        // touch, closing enraged and collapsed exactly, enraged within the fit.
        static byte[] Measured(string hex) => Convert.FromHexString(hex)[2..];
        static byte[] Built(int state, int step) => BreachRulesets.Body(0, 1478, 500, state, step);
        static float F(byte[] body, int i) => BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(11 + 4 * i));
        byte[] measuredIdle = Measured("a42c0000c6050000f401000000000000000000000000000000000000000000000000000000000000000000001643");
        Assert.Equal(measuredIdle, Built(BreachTables.Inactive, 0));
        Assert.Equal(Measured("a42c0000c6050000f4010000010000000000001c4200000041022b073d00000000022b07bd000000000000001643"), Built(BreachTables.Expanding, 0));
        Assert.Equal(Measured("a42c0000c6050000f4010000030000c842000000000000a040022b073d00000000022b07bd000000000000001643"), Built(BreachTables.ClosingEnraged, 0));
        Assert.Equal(Measured("a42c0000c6050000f4010000040000c842000000000000a0400000a04000000000022b07bd000000000000001643"), Built(BreachTables.Closing, 0));
        byte[] enraged = Measured("a42c0000c6050000f401000002fa02cf4298281643270f8d40270f8d4000000000022b07bd000000000000001643");
        byte[] built = Built(BreachTables.Enraged, BreachRulesets.Steps - 1);
        Assert.Equal(enraged[..11], built[..11]);
        Assert.Equal(enraged[27..], built[27..]);
        Assert.InRange(F(built, 0) - F(enraged, 0), -11.0f, 11.0f);
        Assert.InRange(F(built, 1) - F(enraged, 1), -0.2f, 0.2f);
        Assert.InRange(F(built, 2) - F(enraged, 2), -0.5f, 0.5f);
        Assert.Equal(F(built, 2), F(built, 3));

        // The ramp: the radius 39 -> 150 in even steps, the progress rising from 0, f32[2] = 8 + 43 g - 0.033 n until the
        // full radius, all inside the measured ranges.
        float last = -1;
        for (int k = 0; k < BreachRulesets.Steps; k++)
        {
            byte[] line = Built(BreachTables.Expanding, k);
            double g = (double)k / (BreachRulesets.Steps - 1);
            Assert.InRange(F(line, 1) - (39 + 111 * g), -1e-3, 1e-3);
            Assert.True(F(line, 0) > last && F(line, 0) <= 112.3f, $"progress at {k}");
            last = F(line, 0);
            double expected = k == BreachRulesets.Steps - 1 ? BreachRulesets.FullValue : 8 + 43 * g - 0.033 * BreachRulesets.CountPerStep * k;
            Assert.InRange(F(line, 2) - expected, -1e-3, 1e-3);
            Assert.InRange(F(line, 2), 3.5f, 18.5f);
            Assert.Equal(Built(BreachTables.Expanding, 0)[23..], line[23..]);
        }

        // This Breach's idle line: the measured one at its cell.
        byte[] aimed = (byte[])measuredIdle.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(aimed.AsSpan(2), breach.X);
        BinaryPrimitives.WriteInt32LittleEndian(aimed.AsSpan(6), breach.Y);
        byte[] idle = InstanceRulesets.Data(164, aimed), row = InstanceProtocol.AddRuleset(164);

        // The party member who comes later: the row before the area's objects, then the row and the line again.
        Assert.Equal(row, Assert.Single(encounter.EnterMessages(area.B)).Message);
        await encounter.Enter(area.B);
        // A reloaded module reads the instance's Breach back.
        var reloaded = new BreachEncounter();
        Assert.Equal(breach.Initiator, Assert.Single(reloaded.Of(area.Instance)!.Breaches).Initiator);

        byte[] a = await area.WireA.Finish(), b = await area.WireB.Finish();
        Assert.True(BreachArea.Find(a, row) is int ruleset && BreachArea.Find(a, idle) > ruleset, "A: the row, then the idle line");
        Assert.True(BreachArea.Find(b, row) >= 0 && BreachArea.Find(b, idle) >= 0, "B: the row and the idle line");
    }
}

/// <summary>Two players in one map instance (MapWorldsReef: a flat, walkable 600 x 600 layout), each with a wire whose
/// decrypted bytes the test reads.</summary>
sealed class BreachArea : IAsyncDisposable
{
    readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
    readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
    readonly string? previousChances = Environment.GetEnvironmentVariable("Game_LEAGUE_CHANCES");
    public readonly InstanceState Instance = new();
    public Player A = null!, B = null!;
    public Wire WireA = null!, WireB = null!;
    public byte Level;

    public static async Task<BreachArea> Open()
    {
        BreachModule.LoadData();
        var area = new BreachArea();
        Game.CombatLifetime = new ModuleLifetime("Game");
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        // Every Breach row rolls: the area's own chance is a map's few percent.
        Environment.SetEnvironmentVariable("Game_LEAGUE_CHANCES", "Breach=100");
        var layout = new AreaLayout(53973, "MapWorldsReef", 600, 600, 300, 300, (_, _) => true, []);
        area.Level = (byte)ServerLevels.AreaLevel(layout.Id);
        (area.A, area.WireA) = await Wire.Open(area.Instance, layout, "BreachA");
        (area.B, area.WireB) = await Wire.Open(area.Instance, layout, "BreachB");
        area.Instance.AddPlayer(area.A);
        area.Instance.AddPlayer(area.B);
        return area;
    }

    public static int Find(byte[] bytes, byte[] part) => bytes.AsSpan().IndexOf(part);

    public async ValueTask DisposeAsync()
    {
        await WireA.DisposeAsync();
        await WireB.DisposeAsync();
        Environment.SetEnvironmentVariable("Game_LEAGUE_CHANCES", previousChances);
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
