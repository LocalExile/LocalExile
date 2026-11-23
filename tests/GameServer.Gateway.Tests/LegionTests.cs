using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using GameServer.Modules.Legion;

namespace GameServer.Gateway.Tests;

/// <summary>Legion: the plan, the tables it reads and the bytes it sends; and the instance's monolith from roll to the end of the
/// fight with two players in it. Nothing waits on real time: the stage clocks are wound back by hand.</summary>
public sealed class LegionTests
{
    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Fact]
    public void Plan_Tables_And_Messages_MatchTheClientAndTheCaptures()
    {
        Assert.Equal((byte)94, LegionTables.RulesetRow);
        Assert.Equal(6, LegionTables.Factions.Count);
        Assert.Equal(15000, LegionTables.FreezeMs(68));
        Assert.Equal(30000, LegionTables.FreezeMs(1));
        Assert.Equal(18, LegionTables.RewardSymbols.Count);
        Assert.NotEqual(0, LegionTables.EnvWorld);
        Assert.True(LegionTables.EnvWorld < LegionTables.EnvBattle && LegionTables.EnvBattle < LegionTables.EnvPostBattle);
        // The army's varieties and the hoards' chests are read from LegionMonsterVarieties and LegionChestTypes.
        Assert.Equal([7672, 7673, 7674, 7675, 7676], LegionTables.VarietyRows("Karui", LegionTables.Rank.Soldier));
        Assert.Equal([7704], LegionTables.VarietyRows("Karui", LegionTables.Rank.General));
        Assert.Equal((7828, "Metadata/Chests/LegionChests/KaruiChest"), LegionTables.ChestType("Karui", epic: false));
        Assert.Equal((7829, "Metadata/Chests/LegionChests/KaruiChestEpic"), LegionTables.ChestType("Karui", epic: true));

        var (factions, hasGeneral, units) = LegionEncounter.Plan((1000, 2000), Math.PI / 2, 68, new Random(1234));
        Assert.Equal(2, factions.Length);
        Assert.NotEqual(factions[0], factions[1]);
        Assert.Equal(hasGeneral, units.Any(u => u.Rank == LegionTables.Rank.General));
        Assert.InRange(units.Count(u => u.Rank == LegionTables.Rank.Soldier && !u.IsHoard), 120, 144);
        Assert.InRange(units.Count(u => u.Rank == LegionTables.Rank.Elite && !u.IsHoard), 48, 72);
        Assert.InRange(units.Count(u => u.Rank == LegionTables.Rank.Sergeant), 6, 10);
        Assert.All(units, u => Assert.InRange(double.Hypot(u.X - 1000, u.Y - 2000), 0, LegionTables.Radius + 1));

        // The first ruleset message: u16 0x2c9, the Rulesets row, a one-byte length and the 37 bytes of data.
        byte[] full = LegionPackets.Full((1234, -5678), 68, 0x00abcdef, Math.PI / 2, 15.0);
        Assert.Equal(41, full.Length);
        Assert.Equal([0x02, 0xc9, 94, 37], full[..4]);
        Assert.Equal(1234, BinaryPrimitives.ReadInt32LittleEndian(full.AsSpan(5)));
        Assert.Equal(-5678, BinaryPrimitives.ReadInt32LittleEndian(full.AsSpan(9)));
        Assert.Equal(0x00abcdefu, BinaryPrimitives.ReadUInt32LittleEndian(full.AsSpan(19)));
        Assert.Equal(15.0f, BinaryPrimitives.ReadSingleLittleEndian(full.AsSpan(37)));

        byte[] env = LegionPackets.Environments((LegionTables.EnvWorld, 0.5), (LegionTables.EnvBattle, -2));
        Assert.Equal(InstanceEnvironments.Data((LegionTables.EnvWorld, 0.5), (LegionTables.EnvBattle, -2)), env);
        Assert.Equal(17, env.Length);
        Assert.Equal(2, env[2]);
        Assert.Equal(LegionTables.EnvWorld, BinaryPrimitives.ReadUInt16BigEndian(env.AsSpan(3)));
        Assert.Equal(-2f, BinaryPrimitives.ReadSingleBigEndian(env.AsSpan(13)));

        // Official 3.29 capture: the ruleset's first message, whole, from its Rulesets row on (the message id is the build's
        // own). It pins the row 94, the length 37 and the field order: u8 0, the cell, the level (68), a u16 0, the
        // monolith's id, 10 zero bytes, the angle (0) and the freeze's 15 seconds. The cell and the monolith's id differ by
        // nature, so they are replaced by the ones the writer is given.
        byte[] official = Convert.FromHexString("5e25004a0200002d040000440000000000f7070000000000000000000000000000000000007041");
        BinaryPrimitives.WriteInt32LittleEndian(official.AsSpan(3), 1234);
        BinaryPrimitives.WriteInt32LittleEndian(official.AsSpan(7), -5678);
        BinaryPrimitives.WriteUInt32LittleEndian(official.AsSpan(17), 0x00abcdef);
        Assert.Equal(Hex(official), Hex(LegionPackets.Full((1234, -5678), 68, 0x00abcdef, 0.0, 15.0)[2..]));

        // Official 3.29 capture: the 0xdf at the end of a fight, whole: the battle environment at 0 seconds, then the
        // post-battle one fading in over 0.2 (f32 big-endian). The captured Environments rows come from another client build
        // (+5 from row 1055 in 3.29.3c), so the two rows are compared by meaning, as this client's battle and post-battle rows.
        byte[] ending = Convert.FromHexString("df" + "02049700000000000498003e4ccccd");
        BinaryPrimitives.WriteUInt16BigEndian(ending.AsSpan(2), LegionTables.EnvBattle);
        BinaryPrimitives.WriteUInt16BigEndian(ending.AsSpan(9), LegionTables.EnvPostBattle);
        Assert.Equal("00" + Hex(ending), Hex(LegionPackets.Environments((LegionTables.EnvBattle, 0.0), (LegionTables.EnvPostBattle, 0.2))));

        // Official 3.29 capture: the whole LegionInitiator (monolith) create, with its type hash and the blob length 38. It pins
        // the body: the General byte 0, Positioned, InteractionAction 1, Animated and MinimapIcon empty, the StateMachine of
        // seven zero states and the empty task list. The object id and the cell differ by nature and are replaced by the ones
        // the writer is given; the world output writes the order key 0xffffffff and checkpoint 0 over the header, so the
        // writer's header is set the same way before the comparison.
        byte[] initiator = Convert.FromHexString("000007f7ffffffff0000c42bd58e002600004a0200002d04000000000000000000000100000700000000000000000000000000000000");
        BinaryPrimitives.WriteUInt32BigEndian(initiator, 0x2a);
        BinaryPrimitives.WriteInt32LittleEndian(initiator.AsSpan(18), 589);
        BinaryPrimitives.WriteInt32LittleEndian(initiator.AsSpan(22), 792);
        byte[] created = InstanceEntities.CreateObject(0x2a, InstanceEntities.ObjectTypeHash(LegionTables.MonolithPath), LegionPackets.MonolithBody((589, 792)))[2..];
        BinaryPrimitives.WriteUInt32BigEndian(created.AsSpan(4), uint.MaxValue);
        BinaryPrimitives.WriteUInt16BigEndian(created.AsSpan(8), 0);
        Assert.Equal(Hex(initiator), Hex(created));
        Assert.Equal(38, LegionPackets.MonolithBody((589, 792)).Length);
        Assert.Equal(49, LegionPackets.MonolithBody((589, 792), 0x4000100).Length);
        Assert.Equal(46, LegionPackets.FlashLightBody((589, 792), 0x400006f).Length);
        Assert.Equal(43, LegionPackets.ChestBody((589, 792), 0f, 0, 0).Length);
    }

    [Fact]
    public async Task The_instances_monolith_runs_from_the_roll_to_the_end_of_the_fight()
    {
        await using var area = await LegionArea.Open();
        var encounter = new LegionEncounter();
        Assert.Null(await encounter.Place(new AreaPopulated(area.A, area.Level, 7, [])));
        var state = encounter.Of(area.Instance)!;
        Assert.Equal(LegionEncounter.Stage.Hidden, state.Stage);
        // A second population places nothing more.
        Assert.NotNull(await encounter.Place(new AreaPopulated(area.A, area.Level, 7, [])));

        // B (not the one that runs the instance) walks within reach: the monolith and the army are created.
        area.B.Walk.Teleport(state.X, state.Y, Game.AreaTime(area.B));
        await encounter.Step(area.A);
        Assert.Equal(LegionEncounter.Stage.Idle, state.Stage);
        Assert.True(area.A.Area.Objects.Has(state.Monolith));
        var army = state.Units.Where(u => u.Id != 0).ToList();
        Assert.NotEmpty(army);
        Assert.All(army, u => Assert.Equal(state.Monolith, area.A.Area.Monsters[u.Id].SummonerId));
        // Held: before the monolith wakes no hit lands at all.
        var first = army.First(u => !u.IsHoard);
        var held = area.A.Area.Monsters[first.Id];
        Assert.Equal(double.PositiveInfinity, held.BusyUntil);
        Assert.Equal(held.Life, Game.TakeDamageLocked(held, 999999).Life);

        // A party member who comes later, and a reloaded module, find the same encounter; the entering client is sent it again.
        Assert.Equal(94, Assert.Single(encounter.EnterMessages(area.B)).Message[2]);
        await encounter.Enter(area.B);
        Assert.Equal(state.Monolith, new LegionEncounter().Of(area.Instance)!.Monolith);

        // The click's charge (45 frames) ends: the monolith wakes and time stops; a lethal hit now stops at the floor and breaks the
        // unit out.
        state.Stage = LegionEncounter.Stage.Charging;
        state.StageAt -= LegionTables.ChargeSeconds;
        await encounter.Step(area.A);
        Assert.Equal(LegionEncounter.Stage.Active, state.Stage);
        Assert.False(held.EntranceGrace);
        (uint life, _) = Game.TakeDamageLocked(held, 999999);
        Assert.True(life > 0 && life < held.MaxLife);
        held.Life = life;
        await encounter.Step(area.A);
        Assert.Equal(LegionEncounter.UnitState.Broken, first.State);
        Assert.True(held.Invulnerable);

        // The freeze ends: the broken-out unit is released whole, every other unit goes.
        state.StageAt -= LegionTables.FreezeMs(state.Level) / 1000.0;
        await encounter.Step(area.A);
        Assert.Equal(LegionEncounter.Stage.Frozen, state.Stage);
        Assert.Equal(LegionEncounter.UnitState.Released, first.State);
        Assert.Equal(held.MaxLife, held.Life);
        Assert.Null(held.LifePhases);
        Assert.All(army.Where(u => u != first), u => Assert.False(area.A.Area.Monsters.ContainsKey(u.Id) || area.Instance.World.MonsterOrigins.ContainsKey(u.Id)));

        // The battle: its state, then the released unit's death, then the end and the environments' fade-outs.
        state.StageAt -= LegionTables.BattleAfterFreezeSeconds;
        await encounter.Step(area.A);
        Assert.Equal(LegionEncounter.Stage.Battle, state.Stage);
        held.Life = 0;
        held.Dead = true;
        await encounter.Step(area.A);
        Assert.NotNull(state.ClearedAt);
        state.ClearedAt -= LegionTables.EndDelaySeconds;
        await encounter.Step(area.A);
        Assert.Equal(LegionEncounter.Stage.Ending, state.Stage);
        state.StageAt -= 4;
        await encounter.Step(area.A);
        Assert.True(state.Done);
        Assert.Empty(state.Environment);
        Assert.True(new LegionEncounter().Of(area.Instance)!.Done);

        // Both clients were sent the world environment in, and the battle and post-battle ones in turn.
        byte[] world = InstanceEnvironments.Data((LegionTables.EnvWorld, 0.5)), battle = InstanceEnvironments.Data((LegionTables.EnvBattle, 0.0));
        byte[] post = InstanceEnvironments.Data((LegionTables.EnvPostBattle, -2.0));
        byte[] a = await area.WireA.Finish(), b = await area.WireB.Finish();
        Assert.True(LegionArea.Find(a, world) is int aw and >= 0 && LegionArea.Find(a, battle) > aw && LegionArea.Find(a, post) > aw, "A: world, battle, post-battle out");
        Assert.True(LegionArea.Find(b, world) is int bw and >= 0 && LegionArea.Find(b, battle) > bw && LegionArea.Find(b, post) > bw, "B: world, battle, post-battle out");
    }
}

/// <summary>Two players in one map instance (MapWorldsReef: a flat, walkable 600 x 600 layout), each with a wire whose decrypted
/// bytes the test reads.</summary>
sealed class LegionArea : IAsyncDisposable
{
    readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
    readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
    readonly string? previousChances = Environment.GetEnvironmentVariable("Game_LEAGUE_CHANCES");
    public readonly InstanceState Instance = new();
    public Player A = null!, B = null!;
    public Wire WireA = null!, WireB = null!;
    public byte Level;

    public static async Task<LegionArea> Open()
    {
        var area = new LegionArea();
        Game.CombatLifetime = new ModuleLifetime("Game");
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        // Every area rolls a monolith: a map's own chance is 8%.
        Environment.SetEnvironmentVariable("Game_LEAGUE_CHANCES", "Legion=100");
        var layout = new AreaLayout(53973, "MapWorldsReef", 600, 600, 300, 300, (_, _) => true, []);
        area.Level = (byte)ServerLevels.AreaLevel(layout.Id);
        (area.A, area.WireA) = await Wire.Open(area.Instance, layout, "LegionA");
        (area.B, area.WireB) = await Wire.Open(area.Instance, layout, "LegionB");
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
