using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

// Each loaded stash tab made every later stash open slower by its item count, so the client keeps only the tab the panel
// shows (plus the tabs with shop currency): picking a tab unloads the others (S→C 0xc1) and sends the tab list again with
// them marked not loaded.
[Collection(TestSaves.FailWriteCollection)]
public class StashTabLoadingTests
{
    const int Price = 0, Other = 1, Shown = 2;   // three of the starter normal tabs

    static T Call<T>(string name, params object?[] args) =>
        (T)typeof(Program).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;

    static (ClientState Conn, AccountStash Stash) Character()
    {
        TestSaves.Ensure();
        string account = $"TabLoad{Guid.NewGuid():N}"[..16];
        var conn = new ClientState(new AreaLayout(1, "test", 100, 100, 10, 10, null, []), 1, 1) { Account = account };
        var stash = AccountStash.For(AccountStash.Key(account, null), Program.Items);
        PlayerInventories.Shape? Shape(Item i) => Call<PlayerInventories.Shape?>("ShapeOf", i);
        var scroll = ItemCatalog.Create(Program.Items.Find("Scroll of Wisdom")!) with { StackSize = 5 };
        var ring = ItemCatalog.Create(Program.Items.Find("Iron Ring")!);
        Assert.NotNull(stash.FillCells(Price, [scroll], i => "scroll", Shape, out _));
        Assert.NotNull(stash.FillCells(Other, [ring], i => "ring", Shape, out _));
        return (conn, stash);
    }

    [Fact]
    public void Only_currency_and_grid_tabs_with_shop_currency_load_on_area_entry()
    {
        var (_, stash) = Character();
        bool Preloads(int tab) => Program.PreloadsForPrices(stash.TabType(tab), stash.Inventory(tab).Items, Program.Items);

        Assert.True(Preloads(Price));    // a normal tab with a Scroll of Wisdom
        Assert.False(Preloads(Other));   // a ring is no shop currency
        Assert.False(Program.PreloadsForPrices(StashRules.Fragment, stash.Inventory(Price).Items, Program.Items));   // never a special tab
    }

    static bool Has(byte[] bytes, byte[] part) => bytes.AsSpan().IndexOf(part) >= 0;

    // One loopback transport with persistent cipher state (as ActorScaleGatewayTests).
    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        TcpClient reader = null!;
        readonly ConnectionCiphers decrypt = new(new byte[64], true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        NetworkStream Stream => writer.GetStream();

        public static async Task<Wire> Open()
        {
            var wire = new Wire(); wire.listener.Start();
            var accept = wire.listener.AcceptTcpClientAsync();
            await wire.writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)wire.listener.LocalEndpoint).Port);
            wire.reader = await accept;
            return wire;
        }

        public Task Invoke(string method, ClientState conn, params object?[] tail) =>
            Call<Task>(method, ["stash-tab-loading-test", Stream, Ciphers, conn, .. tail]);

        public async Task<byte[]> Read()
        {
            byte[] sentinel = new LoginWriter().U16(0x7ffe).U32(0x12345678).ToArray();
            await Program.Send("stash-tab-loading-test", Stream, Ciphers, sentinel.ToArray(), null);
            var result = new List<byte>(); var buffer = new byte[4096];
            while (true)
            {
                int n = await reader.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(n > 0);
                byte[] chunk = buffer[..n]; decrypt.Receive.Process(chunk); result.AddRange(chunk);
                if (result.Count >= sentinel.Length && result.TakeLast(sentinel.Length).SequenceEqual(sentinel))
                    return result.Take(result.Count - sentinel.Length).ToArray();
            }
        }

        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Dispose(); }
    }
}
