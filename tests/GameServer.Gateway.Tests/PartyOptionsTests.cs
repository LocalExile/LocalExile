using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using GameServer.Modules.Social;

namespace GameServer.Gateway.Tests;

public class PartyOptionsTests
{
    [Fact]
    public async Task Leader_options_survive_roster_updates_rebind_and_handover_but_not_disband()
    {
        await using var f = new Fixture();
        var leader = f.Player("Leader");
        var member = f.Player("Member");
        var newcomer = f.Player("Newcomer");
        await PartyRoster.InviteHuman(leader, member.Character!);
        uint party = PartyRoster.PartyOf(leader);
        await PartyRoster.AcceptHuman(member, party);

        // Native writer 0x141f9bc30: current-party bit, allocation 2, scaling Disabled (1).
        await f.Send(leader, [0, 0x99, 0x82, 1]);
        await PartyRoster.Publish(party);
        f.Options(leader, 2, 1);
        f.Options(member, 2, 1);
        Assert.Empty(f.Packets[newcomer]);

        await PartyRoster.InviteHuman(member, newcomer.Character!);
        f.Options(newcomer, 2, 1);
        await PartyRoster.AcceptHuman(newcomer, party);
        var companion = new PartyCompanion("OptionsBot", "OptionsBot",
            InstanceSocial.KnownPlayer("OptionsBot", "OptionsBot", 1));
        Assert.True(PartyRoster.TryAddCompanion(leader, companion, out uint same));
        Assert.Equal(party, same);
        await PartyRoster.Publish(party);
        foreach (var c in new[] { leader, member, newcomer }) f.Options(c, 2, 1);

        f.Reload();
        leader.Closed = true;
        var replacement = f.Player("Leader");
        await PartyRoster.Rebind(replacement);
        Assert.Same(replacement, PartyRoster.Leader(party));
        f.Options(replacement, 2, 1);
        await PartyRoster.LeaveHuman(replacement, party);
        Assert.Same(member, PartyRoster.Leader(party));
        f.Options(member, 2, 1);

        // The promoted leader can update both selections; all three allocation values round-trip.
        foreach (byte allocation in new byte[] { 1, 0, 2 })
        {
            Assert.True(await f.Send(member, [0, 0x99, (byte)(0x80 | allocation), 0]));
            f.Options(member, allocation, 0);
            f.Options(newcomer, allocation, 0);
        }
        await PartyRoster.LeaveHuman(newcomer, party);
        Assert.True(PartyRoster.RemoveCompanion(party, companion.Account));
        await PartyRoster.Publish(party);
        Assert.Equal(0u, PartyRoster.PartyOf(member));

        // A restored/reused id must not inherit the disbanded party's selections.
        Assert.True(PartyRoster.TryAddCompanion(member, companion, out same, restore: party));
        Assert.Equal(party, same);
        await PartyRoster.Publish(same);
        f.Options(member, 0, 0);
    }

    [Fact]
    public async Task Only_a_current_available_leader_can_change_valid_current_party_options()
    {
        await using var f = new Fixture();
        var leader = f.Player("Leader");
        var member = f.Player("Member");
        var outsider = f.Player("Outsider");
        await PartyRoster.InviteHuman(leader, member.Character!);
        uint party = PartyRoster.PartyOf(leader);
        await PartyRoster.AcceptHuman(member, party);
        Assert.True(await f.Send(leader, [0, 0x99, 0x81, 1]));
        foreach (var packets in f.Packets.Values) packets.Clear();

        Assert.True(await f.Send(leader, [0, 0x99, 0x81, 1])); // An unchanged selection sends no duplicate roster.
        Assert.True(await f.Send(member, [0, 0x99, 0x80, 0]));
        Assert.True(await f.Send(outsider, [0, 0x99, 0x82, 0]));
        // Flag-clear traffic does not grant authority to mutate this party.
        foreach (byte[] packet in new byte[][] {
            [0, 0x99, 0, 0], [0, 0x99, 0x83, 0], [0, 0x99, 0xff, 0],
            [0, 0x99, 0x80, 2], [0, 0x99, 0x80, 0xff], [0, 0x99, 0x80], [0, 0x99, 0x80, 0, 0] })
            Assert.True(await f.Send(leader, packet));
        leader.LeavingArea = true;
        Assert.True(await f.Send(leader, [0, 0x99, 0x80, 0]));
        leader.LeavingArea = false;
        leader.Closed = true;
        Assert.True(await f.Send(leader, [0, 0x99, 0x80, 0]));
        leader.Closed = false;
        Assert.All(f.Packets.Values, packets => Assert.Empty(packets));
        await PartyRoster.Publish(party);
        f.Options(leader, 1, 1);
        f.Options(member, 1, 1);
        Assert.Equal(0u, PartyRoster.PartyOf(outsider));

        var replacement = f.Player("Leader");
        await PartyRoster.Rebind(replacement);
        foreach (var packets in f.Packets.Values) packets.Clear();
        // Even the old connection's matching account/name does not grant it the replacement's authority.
        Assert.True(await f.Send(leader, [0, 0x99, 0x80, 0]));
        Assert.All(f.Packets.Values, packets => Assert.Empty(packets));
        await PartyRoster.Publish(party);
        f.Options(replacement, 1, 1);
    }

    sealed class Fixture : IAsyncDisposable
    {
        readonly string saves = Path.Combine(Path.GetTempPath(), $"localexile-options-{Guid.NewGuid():N}.db");
        readonly string suffix = Guid.NewGuid().ToString("N")[..8];
        readonly PartyFollowerTests.Runtime runtime = new();
        readonly PartyFollowerTests.Wire wire = new();
        readonly InstanceState instance = new();
        readonly ModuleLifetime lifetime = new("PartyOptionsTests");
        public readonly Dictionary<ClientState, List<byte[]>> Packets = [];
        public Fixture() { Saves.Open(saves); Reload(); }
        public void Reload()
        {
            GameEventOwners.Release(lifetime.Id, GameEventOwners.ModuleRank);
            var context = DispatchProxy.Create<IModuleContext, Context>();
            ((Context)(object)context).Lifetime = lifetime;
            new SocialModule().Load(context);
        }
        public ClientState Player(string name)
        {
            var c = new ClientState(new(1, "party-options", 100, 100, 20, 20, null, []), 1, 1)
            {
                Instance = instance, Character = name + suffix, Account = name + suffix,
                PlayerLife = 1000, PlayerSent = true, Wire = ("party-options", wire.Stream, wire.Ciphers)
            };
            Program.InGame.TryAdd(c, 0);
            var packets = new List<byte[]>();
            c.Outbox = (bytes, _) => { packets.Add(bytes); return Task.CompletedTask; };
            Packets[c] = packets;
            return c;
        }
        public async Task<bool> Send(ClientState c, byte[] packet)
        {
            foreach (var handler in GameEvents.Packet(0x99).All)
                if (await handler(c, packet)) return true;
            return false;
        }
        public void Options(ClientState c, byte allocation, byte scaling)
        {
            var bytes = Packets[c].Last(p => p.Length >= 2 && p[0] == 0 && p[1] == 0xa9);
            var reader = new LoginReader(bytes);
            reader.U16(); reader.U32(); reader.WStr(); reader.WStr(); reader.U32(); reader.U8();
            Assert.Equal(allocation, reader.U8());
            Assert.Equal(scaling, reader.U8());
            Assert.Equal(0, reader.U8());
            Assert.False(reader.Short);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var c in Packets.Keys)
            {
                await PartyRoster.LeaveHuman(c, PartyRoster.PartyOf(c));
                c.Closed = true;
                Program.InGame.TryRemove(c, out _);
            }
            GameEventOwners.Release(lifetime.Id, GameEventOwners.ModuleRank);
            lifetime.Stop(ex => throw ex);
            runtime.Dispose(); wire.Dispose(); TestEnvironment.ReleaseSaves(saves);
        }
    }

    public class Context : DispatchProxy
    {
        public IModuleLifetime Lifetime = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Lifetime" => Lifetime,
            "get_Manifest" => new ModManifest(Lifetime.Id, "0", ModKind.Feature, [], Lifetime.Id + ".dll"),
            _ => throw new NotSupportedException(method.Name)
        };
    }
}
