using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{
    [Fact]
    public async Task Equipped_raw_always_pierce_bow_hits_three_targets_through_the_production_route()
    {
        using var f = new Fixture();
        var modifier = Program.Items.Mods.ById("ArrowPierceUniqueBow7")!;
        Assert.Contains(modifier.Stats, s => s.Id == "base_arrows_always_pierce");
        f.Items.Change(PlayerInventories.Weapon1, f.Host.Id,
            _ => ItemCatalog.Create(Program.Items.Find("Crude Bow")!) with
                { Mods = [new(ModSection.Explicit, modifier.Hash, [1])] });
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100) with { Accuracy = int.MaxValue };
        f.Conn.PlayerStats[Program.Stats.Key("global_cannot_crit")!.Value] = 1;
        var targets = new[] { 50, 70, 90 }.Select(x => new SpawnedMonster("pierce target",
            new(x, 32, 0, width: 128, height: 128), 0, 2, 10000)).ToArray();
        for (int i = 0; i < targets.Length; i++) f.Conn.Monsters.Add((uint)(42 + i), targets[i]);
        await f.Gate.WaitAsync();
        try
        {
            byte[] input = new LoginWriter().U16(0x25).U32(110).U32(32)
                .U16(SkillCatalog.FirstSkillId).U16(0).U16(1).ToArray();
            await Program.OnClientMessages("raw-pierce", f.Stream, f.Ciphers,
                [(InstanceProtocol.UseSkillAtLocationId, input)], [], f.Conn);
            var session = Assert.IsType<Program.ProjectileSession>(f.Conn.ProjectileRuntime);
            session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.Time(3));
            Assert.All(targets, target => Assert.True(target.Life < 10000));
        }
        finally
        {
            f.Conn.Closed = true; f.Conn.ProjectileRuntime?.Invalidate(); f.Gate.Release();
            if (f.Conn.ProjectileRuntime is { } session)
                await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("KineticBlast", true)]
    [InlineData("Fireball", false)]
    public async Task Moving_after_payment_preserves_attack_projectiles_but_cancels_spell_windups(string skill, bool hits)
    {
        using var f = new Fixture(skill);
        var monster = new SpawnedMonster("attack-move target", new(38, 32, 0, width: 128, height: 128), 0, 2, 10000);
        f.Conn.Monsters.Add(42, monster);
        uint mana = f.Conn.PlayerMana;
        Task? move = null;
        await f.Gate.WaitAsync();
        try
        {
            using (Program.ProjectileMutation(f.Conn, advance: false))
            {
                Assert.True(f.Use("ground").IsCompletedSuccessfully);
                Assert.True(f.Conn.PlayerMana < mana, "The original attack/cast must have paid before Move.");
                var session = Assert.IsType<Program.ProjectileSession>(f.Conn.ProjectileRuntime);
                byte[] packet = new LoginWriter().U16(0x25).U32(80).U32(32).U16(0x2909).U16(0).U16(1).ToArray();
                move = Program.OnClientMessages("attack-move", f.Stream, f.Ciphers,
                    [(InstanceProtocol.UseSkillAtLocationId, packet)], [], f.Conn);
                Assert.Equal(19, f.Conn.ActionCount);
                session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.Time(2));
                Assert.Equal(hits, monster.Life < 10000);
            }
        }
        finally
        {
            f.Conn.Closed = true; f.Conn.ProjectileRuntime?.Invalidate(); f.Gate.Release();
            if (move is not null) await move.WaitAsync(TimeSpan.FromSeconds(5));
            if (f.Conn.ProjectileRuntime is { } session)
                await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task A_projectile_without_catalog_metadata_is_refused_before_costs_or_area_damage()
    {
        using var f = new Fixture();
        f.Items.Change(PlayerInventories.Weapon1, f.Host.Id,
            _ => ItemCatalog.Create(Program.Items.Find("Crude Bow")!) with
            {
                Sockets = [new(SocketColour.White,
                    ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemIceShot")!))]
            });
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        int effect = f.Effect("IceShot");
        Assert.True(Program.Skills.IsProjectile(effect));
        Assert.Null(Program.Skills.ProjectileFor(effect));
        Assert.Null(Call("GemSkillInstanceDisabled", f.Conn, effect, f.Skill()));
        var monster = new SpawnedMonster("unavailable projectile target", new(38, 32, 0), 0, 2, 10000);
        f.Conn.Monsters.Add(42, monster);
        uint mana = f.Conn.PlayerMana;

        await f.Use("ground");

        Assert.Equal(mana, f.Conn.PlayerMana);
        Assert.Equal(17, f.Conn.ActionCount);
        Assert.Equal(10000u, monster.Life);
        Assert.Empty(f.Conn.SkillReadyAt);
        Assert.Null(f.Conn.ProjectileRuntime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Framework_ground_projectiles_release_and_hit_before_native_output(bool ordinaryBow)
    {
        using var f = ordinaryBow ? new Fixture() : new Fixture("Fireball");
        if (ordinaryBow) f.Items.Change(PlayerInventories.Weapon1, f.Host.Id,
            _ => ItemCatalog.Create(Program.Items.Find("Crude Bow")!));
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        f.Conn.PlayerStats[Program.Stats.Key("number_of_additional_projectiles")!.Value] = 2;
        var monster = new SpawnedMonster("framework target", new(50, 32, 0, width: 128, height: 128), 0, 2, 10000);
        f.Conn.Monsters.Add(42, monster);
        await f.Gate.WaitAsync();
        try
        {
            if (ordinaryBow)
            {
                byte[] packet = new LoginWriter().U16(0x25).U32(100).U32(32)
                    .U16(SkillCatalog.FirstSkillId).U16(0).U16(1).ToArray();
                await Program.OnClientMessages("framework-route", f.Stream, f.Ciphers,
                    [(InstanceProtocol.UseSkillAtLocationId, packet)], [], f.Conn).WaitAsync(TimeSpan.FromSeconds(2));
            }
            else await f.Use("ground").WaitAsync(TimeSpan.FromSeconds(2));

            var session = Assert.IsType<Program.ProjectileSession>(f.Conn.ProjectileRuntime);
            for (int i = 0; i < 120 && session.Runtime.Projectiles.Count == 0; i++)
                session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.TickQuanta);
            Assert.Equal(3, session.Runtime.Projectiles.Count);
            session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.Time(1));
            Assert.True(monster.Life < 10000, "Damage must commit while native output remains blocked.");
        }
        finally
        {
            f.Conn.Closed = true; f.Conn.ProjectileRuntime?.Invalidate(); f.Gate.Release();
            if (f.Conn.ProjectileRuntime is { } session)
                await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
