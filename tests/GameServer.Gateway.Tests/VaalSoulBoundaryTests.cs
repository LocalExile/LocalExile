using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class VaalSoulGatewayTests
{
    static long Souls(ClientState conn) => conn.VaalSouls.Read().Sum(b => b.Current);
    static Task ActualKill(Fixture f, Wire wire)
    {
        var target = f.Monster(corpse: false);
        return (Task)Call("HitMonster", "soul-r2-outgoing", wire.Stream, wire.Ciphers, f.Conn, f.Id(target), target,
            10, "soul R2 test", false, false, (ushort)0, null, null, null);
    }

    static async Task<Task> HoldActualPlayerDeath(Fixture f, Wire wire, string route)
    {
        if (route != "monster")
        {
            f.Conn.Commands = (ChatCommands)Call("Commands", "soul-r2-dev", wire.Stream, wire.Ciphers, f.Conn);
            await wire.Gate.WaitAsync();
            return f.Conn.Commands.RunAsync(route switch
            {
                "hit" => "/hit chaos 1000000",
                "spellhit" => "/spellhit chaos 1000000",
                _ => "/blockhit attack chaos 1000000"
            });
        }
        var attacker = new SpawnedMonster("soul R2 attacker", new(105, 100, 10, width: 1000, height: 1000), 0, 2, 100)
        {
            Attack = new(SkillCatalog.FirstSkillId, .2, 10, DamagePacket.Of(DamageType.Physical, 1000000, 1000000))
        };
        f.Conn.Monsters[500] = attacker;
        f.Conn.EvasionEntropy = 99; f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
        Task incoming;
        lock (f.Conn.Monsters)
        {
            incoming = (Task)Call("MonsterSwing", "soul-r2-incoming", wire.Stream, wire.Ciphers, f.Conn,
                500u, attacker, (105, 100), (100, 100), f.Conn.IncomingHitEpoch);
            f.Conn.Clock.Stop();
        }
        try
        {
            await wire.Through(InstanceProtocol.ActionStarted(500, 105, 100, 100, 100,
                SkillCatalog.FirstSkillId, 0xffff, 0x0408, targetObjectId: 0x1000));
            await wire.Gate.WaitAsync();
        }
        finally { f.Conn.Clock.Start(); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            lock (f.Conn.Monsters) if (f.Conn.PlayerLife == 0) break;
            await Task.Delay(5, timeout.Token);
        }
        return incoming;
    }

    [Theory]
    [InlineData("monster")]
    [InlineData("hit")]
    [InlineData("spellhit")]
    [InlineData("blockhit")]
    public async Task Actual_player_death_clears_at_zero_and_late_notice_preserves_real_resurrection_kill(string route)
    {
        using var f = new Fixture(); using var wire = new Wire();
        await ActualKill(f, wire); Assert.Equal(1, Souls(f.Conn));
        Task oldNotice = await HoldActualPlayerDeath(f, wire, route);
        long atDeath = Souls(f.Conn);
        Task resurrection, nextKill;
        long afterRevivalKill;
        try
        {
            Assert.Equal(0u, f.Conn.PlayerLife); Assert.False(oldNotice.IsCompleted);
            resurrection = (Task)Call("Resurrect", "soul-r2-revive", wire.Stream, wire.Ciphers, f.Conn, false);
            Assert.True(f.Conn.PlayerLife > 0); Assert.False(resurrection.IsCompleted);
            nextKill = ActualKill(f, wire); afterRevivalKill = Souls(f.Conn);
        }
        finally { wire.Gate.Release(); }
        await Task.WhenAll(oldNotice, resurrection, nextKill).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, atDeath);
        Assert.Equal(1, afterRevivalKill);
        Assert.Equal(1, Souls(f.Conn));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_death_transfer_never_carries_old_souls_or_clears_the_destination_kill(bool reviveBeforeTransfer)
    {
        using var old = new Fixture(); using var sourceWire = new Wire();
        await ActualKill(old, sourceWire);
        Task oldNotice = await HoldActualPlayerDeath(old, sourceWire, "monster");
        long atDeath = Souls(old.Conn);
        Task? resurrection = null;
        uint ticket = (uint)Random.Shared.Next(1, int.MaxValue);
        Fixture? next = null;
        long restored = -1;
        try
        {
            if (reviveBeforeTransfer)
                resurrection = (Task)Call("Resurrect", "soul-r2-transfer-revive", sourceWire.Stream,
                    sourceWire.Ciphers, old.Conn, false);
            Program.CarryVaalSouls(old.Conn, ticket);
            next = new Fixture(ticket: ticket, character: old.Conn.Character);
            Program.RestoreVaalSouls(next.Conn); Program.RefreshVaalSouls(next.Conn); restored = Souls(next.Conn);
            using var destinationWire = new Wire();
            await ActualKill(next, destinationWire);
        }
        finally { sourceWire.Gate.Release(); }
        try
        {
            await oldNotice.WaitAsync(TimeSpan.FromSeconds(10));
            if (resurrection is not null) await resurrection.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, atDeath); Assert.Equal(0, restored);
            Assert.True(old.Conn.VaalSoulsRetired); Assert.Equal(0, Souls(old.Conn));
            Assert.Equal(1, Souls(next!.Conn));
        }
        finally { next?.Dispose(); }
    }

    static uint AttributeAmulet(Fixture f)
    {
        foreach (string attr in new[] { "strength", "dexterity", "intelligence" })
            f.Conn.PlayerStats.Remove(Program.Stats.Key($"virtual_{attr}_for_requirements")!.Value);
        var dexterity = Program.Items.Mods.All.First(m => m.Domain == 1 && m.GenerationType is 1 or 2
            && m.Stats.Count == 1 && m.Stats[0].Id == "additional_dexterity");
        var amulet = ItemCatalog.Create(Program.Items.Find("Jade Amulet")!) with
            { Mods = [new(ModSection.Explicit, dexterity.Hash, [100])] };
        uint id = f.Items.Add(5, 0, 0, amulet).Id;
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        return id;
    }

    static Task ActualPickUpAmulet(Fixture f, Wire wire, uint id) => (Task)Call("PickUpItem", "soul-r2-gear",
        wire.Stream, wire.Ciphers, f.Conn, new InstanceItems.PickUpItem(PlayerInventories.InventoryId(5), id, 0, 0, 0));

    [Theory]
    [InlineData("shared")]
    [InlineData("rf")]
    public async Task Actual_attribute_pickup_with_notifications_held_allocates_to_the_still_eligible_Vaal_row(string path)
    {
        using var f = new Fixture(); using var wire = new Wire();
        f.Gems("SkillGemVaalFireball", "SkillGemVaalGrace"); uint amulet = AttributeAmulet(f);
        Program.RefreshVaalSouls(f.Conn); Assert.Equal(2, f.Conn.VaalSouls.Read().Length);
        await ActualKill(f, wire); Assert.Equal(1u, f.Conn.VaalSouls.Read().Single(b => b.ActiveSkillsRow == 87).Current);
        int cachedDexterity = f.Conn.Pools.Dexterity;
        await wire.Gate.WaitAsync();
        Task pickup = ActualPickUpAmulet(f, wire, amulet);
        Assert.Empty(f.Items.Items(5)); Assert.False(pickup.IsCompleted);
        Task kill;
        uint currentAtKill;
        try
        {
            Assert.Equal(cachedDexterity, f.Conn.Pools.Dexterity); // Notification refresh has not run.
            if (path == "shared") kill = ActualKill(f, wire);
            else
            {
                f.Monster(corpse: false);
                var rf = new RighteousFireState(1, 2, null, SkillCatalog.FirstSkillId, 6000, 0, 100);
                f.Conn.RighteousFire = rf;
                kill = (Task)Call("BurnAround", "soul-r2-gear-rf", wire.Stream, wire.Ciphers, f.Conn, rf, 1.0);
            }
            currentAtKill = f.Conn.VaalSouls.Read().Single(b => b.ActiveSkillsRow == 87).Current;
            Assert.Equal(cachedDexterity, f.Conn.Pools.Dexterity); // Eligibility did not rewrite combat pools.
        }
        finally { wire.Gate.Release(); }
        await Task.WhenAll(pickup, kill).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2u, currentAtKill);
        Assert.Equal(new VaalSoulBudget(87, 30, 2), Assert.Single(f.Conn.VaalSouls.Read()));
    }

    [Fact]
    public async Task Actual_attribute_pickup_invalidates_an_attribute_requiring_accepted_support_before_notifications()
    {
        using var f = new Fixture(); using var wire = new Wire();
        f.Gems("SkillGemVaalFireball", "SupportGemFasterProjectiles"); f.Links(2);
        uint amulet = AttributeAmulet(f);
        Assert.Single(GemRecords(f.Conn).Single(s => s.EffectsPerLevelRow == 532).Supports);
        await wire.Gate.WaitAsync(); Task pickup = ActualPickUpAmulet(f, wire, amulet);
        int supportCount;
        Task kill;
        try
        {
            kill = ActualKill(f, wire);
            supportCount = GemRecords(f.Conn).Single(s => s.EffectsPerLevelRow == 532).Supports.Count;
        }
        finally { wire.Gate.Release(); }
        await Task.WhenAll(pickup, kill).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, supportCount); Assert.Equal(1u, f.Budget.Current);
    }
}
