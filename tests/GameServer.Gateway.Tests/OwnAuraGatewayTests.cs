using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name,
        BindingFlags.Static | BindingFlags.NonPublic)!;
    static object? Call(string name, params object?[] args) => Method(name).Invoke(null, args);
    static IReadOnlyList<GrantedSkill> Skills(ClientState conn) =>
        (IReadOnlyList<GrantedSkill>)Call("PlayerSkills", conn)!;

    sealed class Fixture : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        readonly ConnectionCiphers receive = new(new byte[64], isClient: true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public readonly ClientState Conn;
        public readonly InventoryItem Host;
        public NetworkStream Stream => writer.GetStream();
        public PlayerInventories Items => PlayerInventories.For(Conn.Character!, Program.Items);
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public ushort Skill(int index = 0) => Program.GemSkillIdPool.Value[index];
        public int Effect(string id) => Program.Skills.EffectRow(id);

        public Fixture(params string[] gems)
        {
            TestSaves.Ensure();
            Conn = new(new(ushort.MaxValue, "OwnAuraFixture", 128, 128, 32, 32, null, []), 0, 1)
            { Character = "OwnAura" + Guid.NewGuid().ToString("N"), ClassRow = 1, PlayerSent = true,
                PlayerObjectCreated = true, ActionCount = 17, NextBuff = 1000 };
            CharacterSaves.SetExperience(Conn.Character, CharacterStats.ExperienceFor(100));
            var attribute = Program.Items.Mods.All.First(m => m.GenerationType is 1 or 2
                && m.Stats.Count == 1 && m.Stats[0].Id == "additional_all_attributes");
            Items.Add(5, 0, 0, ItemCatalog.Create(Program.Items.Find("Paua Amulet")!) with
                { Mods = [new(ModSection.Explicit, attribute.Hash, [1000])] });
            Host = Items.Add(PlayerInventories.Weapon1, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
                { Sockets = [.. gems.Select(id => new ItemSocket(SocketColour.White,
                    ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGem" + id)!)))] });
            Conn.Pools = Program.PoolsFor(Conn, 100);
            Conn.PlayerLife = Conn.Pools.Life;
            Conn.PlayerMana = Conn.Pools.Mana;
            Conn.SentEnergyShield = 0; Conn.SentRechargeActive = false; Conn.SentEsRecoveryRate = 0;
            foreach (string id in gems) Assert.Null(Call("GemDisabled", Conn, Effect(id)));
            Conn.Clock.Start();
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }

        public Task Use(string route, int index = 0)
        {
            ushort skill = Skill(index);
            if (route == "self")
            {
                var use = InstanceTransfer.ReadUseSkillOnObject(new LoginWriter().U16(0x24).U32(0x1000)
                    .U16(skill).U16(3).U16(0x408).ToArray());
                return ((Task)Call("OnUseObject", "own-aura-test", Stream, Ciphers, Conn, use,
                    Conn.Clock.Elapsed.TotalSeconds)!).WaitAsync(TimeSpan.FromSeconds(5));
            }
            ushort id = route switch { "instant-object" => 0x191, "instant-ground" => 0x190, _ => 0x25 };
            var w = new LoginWriter().U16(id);
            if (id == 0x191) w.U32(0x1000).U16(skill);
            else
            {
                w.U32(38).U32(32).U16(skill);
                if (id == 0x25) w.U16(3).U16(0x408);
            }
            return Program.OnClientMessages("own-aura-test", Stream, Ciphers, [(id, w.ToArray())], [], Conn)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        public Task Refresh(string method = "SendCharacterStats") => method switch
        {
            "SendCharacterStats" => (Task)Call(method, "own-aura-test", Stream, Ciphers, Conn, "fixture")!,
            "SendEquipment" => (Task)Call(method, "own-aura-test", Stream, Ciphers, Conn, Items)!,
            "SendPlayerMana" => (Task)Call(method, "own-aura-test", Stream, Ciphers, Conn, null)!,
            "SendOwnAuraChanges" => (Task)Call(method, "own-aura-test", Stream, Ciphers, Conn, false)!,
            _ => (Task)Call(method, "own-aura-test", Stream, Ciphers, Conn)!
        };

        public async Task Set(uint key, int value)
        {
            Conn.Commands ??= (ChatCommands)Call("Commands", "own-aura-test", Stream, Ciphers, Conn)!;
            Assert.True(await Conn.Commands.RunAsync($"/set_stat {key} {value}").WaitAsync(TimeSpan.FromSeconds(5)));
        }

        public async Task<byte[]> Drain()
        {
            await Task.Delay(20);
            byte[] bytes = new byte[reader.Available];
            if (bytes.Length > 0) await reader.GetStream().ReadExactlyAsync(bytes);
            receive.Receive.Process(bytes);
            return bytes;
        }

        public void Dispose()
        {
            Conn.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop();
            PlayerInventories.Forget(Conn.Character!);
        }
    }

    static int Count(byte[] bytes, byte[] packet)
    {
        int count = 0;
        for (int i = 0; i <= bytes.Length - packet.Length; i++)
            if (bytes.AsSpan(i, packet.Length).SequenceEqual(packet)) { count++; i += packet.Length - 1; }
        return count;
    }

    static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    static void AssertNoAdds(byte[] bytes) => Assert.Equal(0,
        Count(bytes, [0x01, 0xc1, 0, 0, 0x10, 0]));

    static void AddBlasphemyAndUnlinkedDuplicate(Fixture f)
    {
        var support = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemBlasphemy")!);
        var second = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemEnfeeble")!);
        f.Items.Change(PlayerInventories.Weapon1, f.Host.Id, item => item with
        {
            Sockets = [item.Sockets[0], new(SocketColour.White, support), new(SocketColour.White, second)],
            Links = [2, 1]
        });
    }

    [Theory]
    [InlineData("ground")]
    public async Task Duplicate_curse_admission_uses_the_requested_gems_accepted_supports(string route)
    {
        using var f = new Fixture("Enfeeble");
        AddBlasphemyAndUnlinkedDuplicate(f);
        f.Conn.PlayerStats[21148] = 1;
        var curses = Skills(f.Conn).Where(s => s.Id == f.Skill() || s.Id == f.Skill(1)).ToArray();
        Assert.Equal(2, curses.Length);
        var supported = Assert.Single(curses, s => s.Supports.Count == 1);
        var normal = Assert.Single(curses, s => s.Supports.Count == 0);
        Assert.Equal(f.Skill(), supported.Id);
        Assert.Equal(f.Skill(1), normal.Id);
        Assert.Contains((15801u, 1), supported.Stats!);
        Assert.Contains((15798u, 1), supported.Stats!);
        Assert.Contains((15801u, 0), normal.Stats!);
        Assert.DoesNotContain(normal.Stats!, s => s is (15798, 1));
        Assert.Null(Call("GemDisabled", f.Conn, f.Effect("Enfeeble"))); // Effect alone has no link-group context.
        Assert.NotNull(Call("GemSkillInstanceDisabled", f.Conn, f.Effect("Enfeeble"), supported.Id));
        Assert.Null(Call("GemSkillInstanceDisabled", f.Conn, f.Effect("Enfeeble"), normal.Id));
        await f.Use(route, 0);
        Assert.Equal(17, f.Conn.ActionCount);
        Assert.Equal(0, Count(await f.Drain(), [0x01, 0xab, 0, 0, 0x10, 0]));
        await f.Use(route, 1);
        Assert.Equal(18, f.Conn.ActionCount);
        Assert.Equal(1, Count(await f.Drain(), [0x01, 0xab, 0, 0, 0x10, 0]));
        Assert.Empty(f.Conn.Auras); // This tests admission and metadata, not a curse executor.
    }

}
