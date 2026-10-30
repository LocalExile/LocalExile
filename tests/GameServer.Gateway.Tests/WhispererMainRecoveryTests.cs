using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{
    static void BeginLethalIgnite(ClientState state)
    {
        state.PlayerLife = 1;
        double now = state.Clock.Elapsed.TotalSeconds;
        var fire = DamagePacket.Of(DamageType.Fire, 100, 100);
        state.ElementalAilments.Apply(fire, fire, true, state.PlayerMaxLife, now, _ => 0, new Random(1));
    }

    [Fact]
    public async Task Lethal_ignite_discards_Whisperer_recoup_and_Blind_before_death_publication()
    {
        using var f = new Fixture();
        double now = f.Conn.Clock.Elapsed.TotalSeconds;
        f.Conn.PlayerMana = 0;
        Program.CharacterPlayerStats(f.Conn);
        Program.LeechOf(f.Conn).Mana.Grant(now - 1, 100, f.Conn.Pools.Mana, 100);
        f.Conn.Whisperer.Hit(now - 3, 1000, 100);
        f.Conn.Whisperer.Spent(199, 10);
        Assert.True(Program.InflictPlayerBlind(f.Conn, 4));
        BeginLethalIgnite(f.Conn);
        var recovery = (Task)Call("RunResourceRecovery", "combined-ailment-death", f.Stream, f.Ciphers, f.Conn)!;
        try
        {
            await Until(() => f.Conn.DeathNotificationEpoch >= 0);
            await Task.Delay(100);
            byte[] bytes = await f.Drain();
            Assert.Equal(1, Count(bytes, InstanceProtocol.DeathScreen(Program.DeathScreenFlags(f.Conn))));
            Assert.Equal(0u, f.Conn.PlayerLife);
            Assert.Equal(0u, f.Conn.PlayerMana);
            Assert.Equal(0, Program.LeechOf(f.Conn).TakeRecovery(now + 8).Mana);
            Assert.False(Program.LeechOf(f.Conn).Mana.Active(now + 8));
            Assert.Equal(0, f.Conn.Whisperer.Take(now + 8));
            Assert.Equal(0, f.Conn.Whisperer.SpentRemainder);
            Assert.Equal(0, f.Conn.Blind.Read(now).Less);
        }
        finally { f.Conn.Closed = true; await recovery.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Held_lethal_publication_has_already_cleared_Whisperer_and_Blind_before_carry()
    {
        using var f = new Fixture();
        await f.Gate.WaitAsync();
        double now = f.Conn.Clock.Elapsed.TotalSeconds;
        f.Conn.PlayerMana = 0;
        Program.CharacterPlayerStats(f.Conn);
        Program.LeechOf(f.Conn).Mana.Grant(now - 1, 100, f.Conn.Pools.Mana, 100);
        f.Conn.Whisperer.Hit(now - 3, 1000, 100);
        f.Conn.Whisperer.Spent(199, 10);
        Assert.True(Program.InflictPlayerBlind(f.Conn, 4));
        BeginLethalIgnite(f.Conn);
        var recovery = (Task)Call("RunResourceRecovery", "held-combined-death", f.Stream, f.Ciphers, f.Conn)!;
        try
        {
            await Until(() => f.Conn.DeathNotificationEpoch >= 0);
            Assert.Equal(0u, f.Conn.PlayerMana);
            Assert.Equal(0, Program.LeechOf(f.Conn).TakeRecovery(now + 8).Mana);
            Assert.False(Program.LeechOf(f.Conn).Mana.Active(now + 8));
            Assert.Equal(0, f.Conn.Whisperer.Take(now + 8));
            Assert.Equal(0, f.Conn.Whisperer.SpentRemainder);
            Assert.Equal(0, f.Conn.Blind.Read(now).Less);
            const uint ticket = 0xA1B2C3;
            Program.CarryWhisperer(f.Conn, ticket);
            var arriving = new ClientState(f.Conn.Layout, ticket, 1)
            { Character = f.Conn.Character, PlayerLife = 100, Pools = f.Conn.Pools };
            Program.RestoreWhisperer(arriving);
            Assert.Equal(0, arriving.Whisperer.Take(now + 8));
            Assert.Equal(0, arriving.Whisperer.SpentRemainder);
            Assert.Equal(0, arriving.Blind.Read(now).Less);
        }
        finally { f.Conn.Closed = true; f.Gate.Release(); await recovery.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Righteous_Fire_remains_nonlethal_and_does_not_publish_an_ailment_death()
    {
        using var f = new Fixture("RighteousFire");
        await f.Use("ground");
        await Until(() => f.Conn.RighteousFire is not null);
        await f.Drain();
        f.Conn.PlayerLife = 1;
        var recovery = (Task)Call("RunResourceRecovery", "rf-nonlethal", f.Stream, f.Ciphers, f.Conn)!;
        try
        {
            await Until(() => f.Conn.RighteousFire is null);
            Assert.Equal(1u, f.Conn.PlayerLife);
            Assert.Equal(-1, f.Conn.DeathNotificationEpoch);
            Assert.Equal(0, Count(await f.Drain(), InstanceProtocol.DeathScreen(Program.DeathScreenFlags(f.Conn))));
        }
        finally { f.Conn.Closed = true; await recovery.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Full_Mana_flask_admission_uses_the_actual_continuous_Life_recipient(bool copy, bool fullLife)
    {
        using var f = new Fixture();
        var type = Program.Items.Find("Metadata/Items/Flasks/FlaskMana1")!;
        var flask = f.Items.Items(PlayerInventories.Flask).Single(i => i.X == 4);
        Assert.Equal(type.Hash, flask.Item.TypeHash);
        f.Items.Change(PlayerInventories.Flask, flask.Id, item => item with { Charges = 24 });
        f.Conn.PlayerLife = fullLife ? f.Conn.PlayerMaxLife : f.Conn.PlayerMaxLife - 50;
        f.Conn.PlayerMana = f.Conn.Pools.Mana;
        f.Conn.PlayerStats[Program.Stats.Key("non_instant_mana_recovery_from_flasks_also_recovers_life")!.Value] = copy ? 1 : 0;
        bool accepted = copy && !fullLife;
        await (Task)Call("UseFlask", "whisperer-flask", f.Stream, f.Ciphers, f.Conn, 4u)!;
        byte[] expected = InstanceItems.ItemUsed(type.Hash, PlayerInventories.InventoryId(PlayerInventories.Flask),
            flask.Id, result: accepted ? (ushort)0 : (ushort)0x4e2);
        var wire = new List<byte>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Count(wire.ToArray(), expected) == 0)
        {
            Assert.False(timeout.IsCancellationRequested,
                $"Expected {Convert.ToHexString(expected)}, wire {Convert.ToHexString(wire.ToArray())}");
            await Task.Delay(10);
            wire.AddRange(await f.Drain());
        }
        Assert.Equal(1, Count(wire.ToArray(), expected));
        Assert.Equal(accepted ? 18 : 24, f.Items.Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges);
        Assert.Equal(accepted, f.Conn.FlaskEffects.Any);
        var recovered = f.Conn.FlaskEffects.Advance(0, f.Conn.Clock.Elapsed.TotalSeconds + 4);
        Assert.Equal(accepted ? 50 : 0, recovered.Life);
        Assert.Equal(accepted ? 50 : 0, recovered.Mana);
        Assert.Equal(0, f.Conn.Whisperer.SpentRemainder);
    }
}
