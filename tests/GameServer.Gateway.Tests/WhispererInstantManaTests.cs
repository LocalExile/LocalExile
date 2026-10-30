using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    sealed class WhispererBlockRandom : Random { public override double NextDouble() => 0; }

    [Theory]
    [InlineData("leech", 0)]
    [InlineData("leech", 3)]
    [InlineData("flask", 0)]
    [InlineData("flask", 3)]
    [InlineData("block", 0)]
    [InlineData("block", 3)]
    public async Task Whisperer_instant_full_Mana_discards_earned_Recoup_before_spending_but_retains_future_grants(
        string source, int pendingWhole)
    {
        using var game = WhispererGame([540, 863]);
        var conn = game.State;
        var gem = ItemCatalog.Create(Program.Items.Find("Fireball")!);
        game.Items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Plate Vest")!) with
            { Sockets = [new(SocketColour.White, gem)] });
        conn.PlayerStats[WKey("virtual_mana_leech_%_is_instant")] = 100;
        if (source == "block") conn.IncomingHitRandom = new WhispererBlockRandom();
        conn.PlayerMana = conn.Pools.Mana - 1;
        await using var simulation = new ProjectileRuntimeFixture(conn);
        simulation.Advance(4);
        Program.CharacterPlayerStats(conn);
        uint cap = (uint)ProgramValue<int>("UnreservedMana", conn);
        Assert.Equal(cap - 1, conn.PlayerMana);
        // The first expired hit has earned pendingWhole +0.2. The new grant still owes 0.8 over four seconds.
        conn.Whisperer.Hit(0, 1 + pendingWhole * 5, 20);
        conn.Whisperer.Hit(4, 4, 20);
        conn.Whisperer.Spent(199, 10);
        if (source == "leech")
        {
            var target = new SpawnedMonster("instant leech", new(32, 32, 0), 0, 2, 1000);
            var taken = DamagePacket.Of(DamageType.Physical, 10, 10);
            var sources = LeechSources.From(n => n == "mana_leech_from_any_damage_permyriad" ? 10000 : 0, true);
            lock (conn.Monsters) Program.LeechOnHitLocked(conn, target, taken, 10, sources);
            Assert.True(Program.LeechOf(conn).InstantManaChanged);
        }
        else if (source == "flask")
        {
            var instant = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == "local_flask_recovers_instantly"));
            game.Items.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(Program.Items.Find("Small Mana Flask")!) with
            {
                Charges = 24,
                Mods = [new(ModSection.Explicit, instant.Hash,
                    instant.Stats.Select(s => s.Id == "local_flask_recovers_instantly" ? 1 : 0).ToArray())],
            });
            Assert.Null((await game.UseModifierFlask()).Error);
            Assert.False(conn.FlaskEffects.Any);
        }
        else
        {
            conn.Pools = conn.Pools with { BlockChance = 100, ManaGainedOnBlock = 1 };
            object?[] args = [conn, DamageType.Physical, 1, true, false, false, null, false];
            Assert.True(ProgramValue<bool>("TryDevEnemyHit", args));
            Assert.True((bool)args[6]!.GetType().GetProperty("Blocked")!.GetValue(args[6])!);
        }
        Assert.Equal(cap, conn.PlayerMana); // No earned whole Recoup is paid on top of an already full pool.
        // Inspect immediately after the real commit, before PayMana/stat reads could conceal delayed settlement.
        var carried = conn.Whisperer.Carry(4);
        Assert.Equal(0m, carried.Pending);
        Assert.Equal(199, carried.Spent);
        var grant = Assert.Single(carried.Recoup);
        Assert.Equal(4, grant.Seconds); Assert.Equal(.8m, grant.Remaining);
        conn.Whisperer.Restore(carried, 4);
        object?[] payment = [conn, Program.GemSkillIdPool.Value[0], 0u];
        Assert.True(ProgramValue<bool>("PayMana", payment));
        uint cost = (uint)payment[2]!;
        Assert.True(cost > 0);
        Assert.Equal(cap - cost, conn.PlayerMana);
        Assert.Equal((199 + (int)cost) % 200, conn.Whisperer.SpentRemainder);
        Assert.Equal(0, conn.Whisperer.Take(8)); // The retained 0.8 cannot combine with the discarded old 0.2.
        conn.Whisperer.Hit(8, 1, 20);
        Assert.Equal(1, conn.Whisperer.Take(12)); // Future grants survived; a later earned 0.2 completes their 0.8.
    }

    [Fact]
    public async Task Whisperer_instant_leech_below_full_Mana_retains_earned_Recoup_and_spending_progress()
    {
        using var game = WhispererGame([540, 863]);
        var conn = game.State;
        conn.PlayerStats[WKey("virtual_mana_leech_%_is_instant")] = 100;
        conn.PlayerMana = 1;
        await using var simulation = new ProjectileRuntimeFixture(conn);
        simulation.Advance(4);
        Program.CharacterPlayerStats(conn);
        conn.Whisperer.Hit(0, 1, 20);
        conn.Whisperer.Hit(4, 4, 20);
        conn.Whisperer.Spent(199, 10);
        var taken = DamagePacket.Of(DamageType.Physical, 10, 10);
        var sources = LeechSources.From(n => n == "mana_leech_from_any_damage_permyriad" ? 10000 : 0, true);
        lock (conn.Monsters) Program.LeechOnHitLocked(conn,
            new("non-full leech", new(32, 32, 0), 0, 2, 1000), taken, 10, sources);
        Assert.Equal(11u, conn.PlayerMana);
        Assert.Equal(199, conn.Whisperer.SpentRemainder);
        Assert.Equal(1, conn.Whisperer.Take(8));
    }
}
